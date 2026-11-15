using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.GuestBookings;
using Reservation.Api.GuestLinks;
using Reservation.Api.Realtime;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Realtime;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.GuestLinks;

/// <summary>
/// The guest-link endpoints (AC5.3–AC5.6, BR-07, BR-13, Design §3.1, §7.3, §8.3) on the seed at 18:25: opening a link
/// only shows the booking; Confirm and Cancel go through the same domain methods as the host.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GuestBookingTests(PostgresFixture postgres) : GuestLinkTestBase(postgres)
{
    // ---------- Opening a link ----------

    [Fact]
    public async Task Opening_a_confirm_link_shows_the_booking_and_changes_nothing()
    {
        await SendRemindersAsync();
        var token = await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "confirm");
        var before = await ByReferenceAsync(ConfirmsRef);

        var response = await GetGuestAsync(token);
        var view = await ReadGuestAsync(token);

        AssertGuestHeaders(response);
        Assert.Equal(
            (ConfirmsRef, "Golden Dragon", "Pacific/Auckland", 2, BookingStatus.Booked),
            (view.Reference, view.VenueName, view.TimeZone, view.PartySize, view.Status));
        Assert.Equal((DemoTime.Tonight, new TimeOnly(20, 30)), (view.LocalDate, view.LocalTime));
        Assert.Equal(before.Period.Start.UtcDateTime, view.StartsAt);
        Assert.Null(view.Deposit);
        Assert.Equal((2, RefundAmount.Full), (view.Terms.FreeUntilHoursBefore, view.Terms.RefundWhenInTime));
        Assert.Equal(LinkAction.Confirm, view.LinkAction);
        Assert.Equal([LinkAction.Confirm], view.AllowedActions);
        Assert.False(view.CancellingNowKeepsDeposit);
        Assert.Equal(Language.En, view.Language); // the page opens in the guest's language (M10b)

        var after = await ByReferenceAsync(ConfirmsRef);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(BookingStatus.Booked, after.Status);
        Assert.Null(after.Reminder!.Response);
    }

    [Fact]
    public async Task Opening_a_cancel_link_in_time_says_the_deposit_would_be_refunded_in_full()
    {
        await SendRemindersAsync();
        var view = await ReadGuestAsync(await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel"));

        Assert.Equal(new GuestDepositView(12_000, DepositStatus.Captured), new GuestDepositView(view.Deposit!.Amount.AmountCents, view.Deposit.Status));
        Assert.Equal([LinkAction.Cancel], view.AllowedActions);
        Assert.Equal(RefundDecision.Full, view.RefundIfCancelledNow);
        Assert.Equal(Language.Zh, view.Language);
        Assert.False(view.CancellingNowKeepsDeposit);
        Assert.Equal(DemoTime.At(18, 30).UtcDateTime, view.FreeCancellationUntil);
    }

    [Fact]
    public async Task Inside_the_free_cancellation_window_the_page_is_told_the_deposit_would_be_kept_BR13()
    {
        await SendRemindersAsync();
        Factory.Clock.Set(DemoTime.At(19, 0)); // 1 h 30 min before 20:30

        var view = await ReadGuestAsync(await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel"));

        Assert.True(view.CancellingNowKeepsDeposit);
        Assert.Equal(RefundDecision.None, view.RefundIfCancelledNow);
        Assert.Equal([LinkAction.Cancel], view.AllowedActions);
    }

    // ---------- Confirm ----------

    [Fact]
    public async Task Confirm_marks_the_booking_confirmed_on_every_host_screen_and_records_the_response_AC5_3_AC5_6()
    {
        await SendRemindersAsync();
        var token = await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "confirm");
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        await using var host = await HubClient.ConnectAsync(Factory, cookie, Ct);

        var response = await PostGuestAsync("confirm", token);
        AssertGuestHeaders(response);
        var confirmed = await ReadAsync<GuestActionResponse>(response);
        Assert.Equal((BookingStatus.Confirmed, (RefundDecision?)null), (confirmed.Status, confirmed.RefundDecision));

        await DispatchAsync();
        var notice = await host.NextAsync(NoticeMethods.FloorPlanChanged, TimeSpan.FromSeconds(5), Ct);
        var booking = await ByReferenceAsync(ConfirmsRef);
        Assert.True(notice.HasValue);
        Assert.Equal(booking.Id.Value, Assert.Single(notice.Value.GetProperty("bookingIds").EnumerateArray()).GetGuid());

        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal((ReminderResponse.Confirmed, DemoTime.Now), (booking.Reminder!.Response, booking.Reminder.RespondedAt));
        Assert.Contains("BookingConfirmed", await OutboxTypesAsync(booking.Id.Value));
    }

    [Fact]
    public async Task Confirming_twice_is_a_no_op()
    {
        await SendRemindersAsync();
        var token = await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "confirm");
        await ActAsync("confirm", token);
        var version = (await ByReferenceAsync(ConfirmsRef)).Version;

        var again = await ActAsync("confirm", token);

        Assert.Equal(BookingStatus.Confirmed, again.Status);
        Assert.Equal(version, (await ByReferenceAsync(ConfirmsRef)).Version);
        Assert.Single(await OutboxTypesAsync((await ByReferenceAsync(ConfirmsRef)).Id.Value), t => t == "BookingConfirmed");
    }

    // ---------- Cancel ----------

    [Fact]
    public async Task Cancel_in_time_frees_the_table_and_refunds_the_deposit_in_full_AC5_4_AC5_5()
    {
        await SendRemindersAsync();
        var token = await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel");

        var cancelled = await ActAsync("cancel", token);
        Assert.Equal((BookingStatus.Cancelled, (RefundDecision?)RefundDecision.Full), (cancelled.Status, cancelled.RefundDecision));

        var booking = await ByReferenceAsync(CancelsRef);
        Assert.Equal((ReminderResponse.Cancelled, DemoTime.Now), (booking.Reminder!.Response, booking.Reminder.RespondedAt));
        Assert.Equal(CancelReason.GuestRequest, booking.CancellationReason);
        Assert.All(booking.Tables, t => Assert.False(t.Active));

        await DispatchAsync();
        var refund = Assert.Single(Payments.Refunds);
        Assert.Equal((SeedPayments.IntentFor(CancelsRef), 12_000L), (refund.PaymentIntentId, refund.Amount.Cents));

        AssertOk(await PostWebhookAsync(StripeEvents.ChargeRefunded(refund.PaymentIntentId!, 12_000, refundId: "re_test_guest")));
        await DispatchAsync();
        Assert.Equal(DepositStatus.Refunded, (await ByReferenceAsync(CancelsRef)).Deposit!.Status);
        // The infrastructure clock stands still here, so the messages' UUID v7 IDs don't follow creation order.
        Assert.Equal(
            [MessagePurpose.Reminder, MessagePurpose.Cancellation, MessagePurpose.Refund],
            (await MessagesAboutAsync(booking.Id.Value)).Select(m => m.Purpose).Order());
        Assert.All(await MessagesAboutAsync(booking.Id.Value), m => Assert.Equal(Language.Zh, m.Language));

        // A repeat is a no-op: same answer, no second refund.
        var again = await ActAsync("cancel", token);
        await DispatchAsync();
        Assert.Equal((BookingStatus.Cancelled, (RefundDecision?)RefundDecision.Full), (again.Status, again.RefundDecision));
        Assert.Single(Payments.Refunds);
    }

    [Fact]
    public async Task Cancel_inside_the_window_keeps_the_deposit_and_asks_for_no_refund_BR07()
    {
        await SendRemindersAsync();
        var token = await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel");
        Factory.Clock.Set(DemoTime.At(19, 0));

        var cancelled = await ActAsync("cancel", token);
        await DispatchAsync();

        Assert.Equal((BookingStatus.Cancelled, (RefundDecision?)RefundDecision.None), (cancelled.Status, cancelled.RefundDecision));
        Assert.Empty(Payments.Refunds);
        Assert.Equal(DepositStatus.Captured, (await ByReferenceAsync(CancelsRef)).Deposit!.Status);
    }

    [Fact]
    public async Task A_host_cancel_still_refunds_in_full_inside_the_window()
    {
        Factory.Clock.Set(DemoTime.At(19, 0));
        var booking = await ByReferenceAsync(CancelsRef);
        var host = NewClient();
        await SignInAsync(host);

        var cancelled = await ReadAsync<BookingStateResponse>(
            await PostAsync(host, $"{Venue}/bookings/{booking.Id.Value}/cancel", new { version = booking.Version }));
        await DispatchAsync();

        Assert.Equal(RefundDecision.Full, cancelled.RefundDecision);
        Assert.Single(Payments.Refunds);
    }

    // ---------- What a link may do ----------

    [Fact]
    public async Task A_confirm_link_cannot_cancel_and_a_manage_link_cannot_confirm()
    {
        await SendRemindersAsync();
        var confirmToken = await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "confirm");
        var booking = await ByReferenceAsync(ConfirmsRef);
        var manageToken = Factory.Services.GetRequiredService<GuestLinkSigner>().Create(booking.Id, LinkAction.Manage, booking.Period.Start);

        var response = await PostGuestAsync("cancel", confirmToken);
        AssertGuestHeaders(response);
        await ProblemAssert.IsAsync(response, HttpStatusCode.BadRequest, "invalid_link");
        await ProblemAssert.IsAsync(await PostGuestAsync("confirm", manageToken), HttpStatusCode.BadRequest, "invalid_link");
        Assert.Equal(BookingStatus.Booked, (await ByReferenceAsync(ConfirmsRef)).Status);

        Assert.Equal([LinkAction.Cancel], (await ReadGuestAsync(manageToken)).AllowedActions);
        Assert.Equal(BookingStatus.Cancelled, (await ActAsync("cancel", manageToken)).Status);
    }

    [Fact]
    public async Task Confirming_a_cancelled_booking_is_an_invalid_transition()
    {
        await SendRemindersAsync();
        var confirm = await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "confirm");
        await ActAsync("cancel", await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "cancel"));

        Assert.Empty((await ReadGuestAsync(confirm)).AllowedActions);
        await ProblemAssert.IsAsync(await PostGuestAsync("confirm", confirm), HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task A_link_is_expired_from_the_booking_start()
    {
        await SendRemindersAsync();
        var token = await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel");
        Factory.Clock.Set(DemoTime.At(20, 30));

        var get = await GetGuestAsync(token);
        AssertGuestHeaders(get);
        await ProblemAssert.IsAsync(get, Gone, "link_expired");
        await ProblemAssert.IsAsync(await PostGuestAsync("cancel", token), Gone, "link_expired");
        Assert.Equal(BookingStatus.Booked, (await ByReferenceAsync(CancelsRef)).Status);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("AAAA.BBBB")]
    [InlineData("")]
    public async Task A_garbage_token_is_an_invalid_link(string token)
    {
        var get = await GetGuestAsync(token);
        AssertGuestHeaders(get);
        await ProblemAssert.IsAsync(get, HttpStatusCode.BadRequest, "invalid_link");
        await ProblemAssert.IsAsync(await PostGuestAsync("confirm", token), HttpStatusCode.BadRequest, "invalid_link");
        await ProblemAssert.IsAsync(await PostGuestAsync("cancel", null), HttpStatusCode.BadRequest, "invalid_link");
    }

    [Fact]
    public async Task A_link_for_a_booking_that_does_not_exist_is_an_invalid_link()
    {
        var token = Factory.Services.GetRequiredService<GuestLinkSigner>()
            .Create(new BookingId(Guid.CreateVersion7()), LinkAction.Cancel, DemoTime.At(21, 0));

        await ProblemAssert.IsAsync(await GetGuestAsync(token), HttpStatusCode.BadRequest, "invalid_link");
    }

    // ---------- Confirmation's manage link ----------

    [Fact]
    public async Task A_confirmation_carries_a_manage_link_that_opens_the_booking_BR13()
    {
        var created = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 30, 2, mobile: "021 011 0402")), HttpStatusCode.Created);
        await DispatchAsync();

        var confirmation = Assert.Single(await MessagesAboutAsync(created.BookingId));
        var manage = Assert.Single(confirmation.Actions);
        Assert.Equal("manage", manage.Kind);
        Assert.EndsWith($"Manage: {manage.Url}", confirmation.Body, StringComparison.Ordinal);

        var view = await ReadGuestAsync(TokenOf(manage.Url));
        Assert.Equal((created.Reference, LinkAction.Manage), (view.Reference, view.LinkAction));
        Assert.Equal([LinkAction.Cancel], view.AllowedActions);
    }

    [Fact]
    public async Task A_deposit_confirmation_carries_a_manage_link_too()
    {
        var booking = await BookAsync(partySize: 6, mobile: "021 011 0403");
        AssertOk(await PaySucceededAsync(booking));
        await DispatchAsync();

        var confirmation = Assert.Single(await MessagesAboutAsync(booking.BookingId), m => m.Purpose == MessagePurpose.Confirmation);
        var view = await ReadGuestAsync(TokenOf(Assert.Single(confirmation.Actions).Url));
        Assert.Equal(RefundDecision.Full, view.RefundIfCancelledNow);
    }

    private sealed record GuestDepositView(long AmountCents, DepositStatus Status);
}
