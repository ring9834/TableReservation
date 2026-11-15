using System.Net;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Messages;
using Reservation.Api.GuestLinks;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.GuestLinks;

/// <summary>"Send reminders now" (AC5.1, AC5.2, AC5.6, BR-13, Design §7.3) on the seed at 18:25.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RemindersTests(PostgresFixture postgres) : GuestLinkTestBase(postgres)
{
    [Fact]
    public async Task At_18_25_only_bookings_10_and_11_get_a_reminder_in_their_language_with_two_signed_links_AC5_1()
    {
        var response = await SendRemindersAsync();

        Assert.Equal(2, response.Sent);
        // Tonight's other upcoming bookings: #6 confirmed; #7, #8 and #9 start within 2 h.
        Assert.Equal(
            [(ReminderEligibility.AlreadyConfirmed, 1), (ReminderEligibility.StartsWithin2h, 3)],
            response.Skipped.Select(s => (s.Reason, s.Count)));

        var signer = Factory.Services.GetRequiredService<GuestLinkSigner>();
        foreach (var (reference, language, opening, confirmLabel, cancelLabel) in new[]
                 {
                     (ConfirmsRef, Language.En, "Golden Dragon: reminder of your booking for 2 on ", "Confirm", "Cancel"),
                     (CancelsRef, Language.Zh, "Golden Dragon：提醒您", "确认", "取消"),
                 })
        {
            var booking = await ByReferenceAsync(reference);
            var reminder = Assert.Single(await MessagesAboutAsync(booking.Id.Value));
            Assert.Equal(MessagePurpose.Reminder, reminder.Purpose);
            Assert.Equal(language, reminder.Language);
            Assert.StartsWith(opening, reminder.Body, StringComparison.Ordinal);
            Assert.Equal(["confirm", "cancel"], reminder.Actions.Select(a => a.Kind));
            Assert.EndsWith(
                $"{confirmLabel}: {reminder.Actions[0].Url} {cancelLabel}: {reminder.Actions[1].Url}", reminder.Body, StringComparison.Ordinal);

            var confirm = signer.Verify(TokenOf(reminder.Actions[0].Url)).Value;
            var cancel = signer.Verify(TokenOf(reminder.Actions[1].Url)).Value;
            Assert.Equal((booking.Id, LinkAction.Confirm, booking.Period.Start), (confirm.BookingId, confirm.Action, confirm.Expires));
            Assert.Equal((booking.Id, LinkAction.Cancel, booking.Period.Start), (cancel.BookingId, cancel.Action, cancel.Expires));

            // AC5.6: when it was sent and the exact wording shown, from the booking's own terms.
            Assert.Equal(DemoTime.Now, booking.Reminder!.SentAt);
            Assert.Contains(booking.Reminder.PolicyShown, reminder.Body, StringComparison.Ordinal);
            Assert.Null(booking.Reminder.Response);
        }

        var paid = await ByReferenceAsync(CancelsRef);
        Assert.Contains("NZ$120.00", paid.Reminder!.PolicyShown, StringComparison.Ordinal);
        Assert.Equal("Can't make it? Please cancel at least 2 h before.", (await ByReferenceAsync(ConfirmsRef)).Reminder!.PolicyShown);
    }

    [Fact]
    public async Task A_reminder_expires_at_the_booking_start_counted_on_the_infrastructure_clock()
    {
        await SendRemindersAsync();

        var booking = await ByReferenceAsync(CancelsRef);
        var reminder = Assert.Single(await MessagesAboutAsync(booking.Id.Value));
        Assert.Equal(Infra.Now + (booking.Period.Start - DemoTime.Now), reminder.ExpiresAt, TimeSpan.FromMilliseconds(1)); // stored in µs
        Assert.Equal(DemoTime.Now, reminder.CreatedAt);
    }

    [Fact]
    public async Task Sending_again_sends_nothing_new()
    {
        await SendRemindersAsync();

        var again = await SendRemindersAsync();

        Assert.Equal(0, again.Sent);
        Assert.Contains(again.Skipped, s => s is { Reason: ReminderEligibility.AlreadyReminded, Count: 2 });
        await using var db = Database.Context();
        Assert.Equal(2, await db.Messages.CountAsync(m => m.Purpose == MessagePurpose.Reminder, Ct));
    }

    [Fact]
    public async Task A_booking_made_under_24_hours_ahead_gets_no_reminder()
    {
        // Made now (18:25) for 20:30 tonight: starts 2 h 05 min away, but was booked under 24 h ahead.
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(DemoTime.Tonight, 20, 30, 2, mobile: "021 011 0401")),
            HttpStatusCode.Created);

        var response = await SendRemindersAsync();

        Assert.Equal(2, response.Sent);
        Assert.Contains(response.Skipped, s => s is { Reason: ReminderEligibility.BookedLessThan24hAhead, Count: 1 });
    }

    [Fact]
    public async Task The_phone_panel_gets_the_buttons_and_the_host_list_the_reminder()
    {
        await SendRemindersAsync();
        var presenter = await PresenterAsync();

        var log = await ReadAsync<MessageLogResponse>(await presenter.GetAsync($"{Venue}/messages", Ct));
        Assert.Equal(2, log.Items.Count);
        Assert.All(log.Items, i => Assert.Equal([LinkAction.Confirm, LinkAction.Cancel], i.Actions.Select(a => a.Kind)));

        var host = NewClient();
        await SignInAsync(host);
        var list = await ReadAsync<BookingListResponse>(await host.GetAsync($"{Venue}/bookings", Ct));
        var reminded = list.Items.Where(i => i.Reminder is not null).Select(i => i.Reference).Order(StringComparer.Ordinal);
        Assert.Equal([ConfirmsRef, CancelsRef], reminded);
        Assert.All(list.Items.Where(i => i.Reminder is not null), i => Assert.Equal(DemoTime.Now.UtcDateTime, i.Reminder!.SentAt));
    }

    [Fact]
    public async Task Hosts_and_anonymous_callers_cannot_send_reminders()
    {
        var host = NewClient();
        await SignInAsync(host);

        await ProblemAssert.IsAsync(await PostAsync(host, "/api/v1/demo/reminders/send-now", new { }), HttpStatusCode.Forbidden, "forbidden");
        await ProblemAssert.IsAsync(
            await PostAsync(NewClient(), "/api/v1/demo/reminders/send-now", new { }), HttpStatusCode.Unauthorized, "not_signed_in");
    }
}

/// <summary>With the demo switched off, "Send reminders now" isn't mapped; the guest-link routes are.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RemindersDemoDisabledTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Demo:Enabled"] = "false" };

    [Fact]
    public void The_send_now_route_is_not_mapped()
    {
        var routes = Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty).ToList();

        Assert.DoesNotContain(routes, r => r.Contains("demo/reminders", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.Contains("guest/bookings", StringComparison.Ordinal));
    }
}
