using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Bookings;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Payments;

/// <summary>
/// <c>POST /api/v1/webhooks/stripe</c> (Design §5.5, §8.1, §12, §8.4): signature, inbox, capture, late payments,
/// refusals and the API version. Bodies are signed with the test secret, as Stripe signs them.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StripeWebhookTests(PostgresFixture postgres) : PaymentTestBase(postgres)
{
    [Fact]
    public async Task A_bad_signature_is_401_and_saves_nothing_CC2()
    {
        var booking = await BookAsync();
        var body = StripeEvents.PaymentSucceeded(await IntentOfAsync(booking.BookingId), booking.BookingId, GoldenDragon.VenueId.Value, 12_000);
        using var json = JsonDocument.Parse(body);
        var eventId = json.RootElement.GetProperty("id").GetString()!;

        var forged = await PostWebhookAsync(body, StripeEvents.Sign(body, "whsec_someone_else", Infra.Now));
        var missing = await PostWebhookAsync(body, signature: "");
        var stale = await PostWebhookAsync(body, StripeEvents.Sign(body, ApiFactory.WebhookSecret, Infra.Now.AddMinutes(-10)));

        await ProblemAssert.IsAsync(forged, HttpStatusCode.Unauthorized, "invalid_signature");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(0, await InboxRowsAsync(eventId));
        Assert.Equal(BookingStatus.PendingPayment, (await LoadAsync(booking.BookingId)).Status);
    }

    [Fact]
    public async Task Payment_succeeded_books_the_party_and_queues_the_confirmation_AC1_5()
    {
        var booking = await BookAsync();
        await DispatchAsync();
        Assert.Empty(await MessagesAboutAsync(booking.BookingId)); // no confirmation while pending payment

        AssertOk(await PaySucceededAsync(booking));

        var paid = await LoadAsync(booking.BookingId);
        Assert.Equal(BookingStatus.Booked, paid.Status);
        Assert.Equal(DepositStatus.Captured, paid.Deposit!.Status);
        Assert.Null(paid.HoldExpiresAt);
        Assert.Equal(["BookingCreated", "DepositCaptured"], await OutboxTypesAsync(booking.BookingId));

        await DispatchAsync();
        var message = Assert.Single(await MessagesAboutAsync(booking.BookingId));
        Assert.Equal(MessagePurpose.Confirmation, message.Purpose);
        Assert.Contains(booking.Reference, message.Body, StringComparison.Ordinal);

        var status = await ReadAsync<BookingStatusResponse>(await NewClient().GetAsync($"{Venue}/bookings/{booking.BookingId}/status", Ct));
        Assert.Equal(BookingStatus.Booked, status.Status);
        Assert.Equal(DepositStatus.Captured, status.DepositStatus);
    }

    [Fact]
    public async Task A_repeated_event_is_processed_once()
    {
        var booking = await BookAsync();
        var eventId = StripeEvents.NewEventId();

        AssertOk(await PaySucceededAsync(booking, eventId: eventId));
        AssertOk(await PaySucceededAsync(booking, eventId: eventId));

        Assert.Equal(1, await InboxRowsAsync(eventId));
        Assert.Equal(["BookingCreated", "DepositCaptured"], await OutboxTypesAsync(booking.BookingId));
    }

    [Fact]
    public async Task A_late_payment_with_the_table_still_free_is_captured_as_normal()
    {
        var booking = await BookAsync();
        Infra.Advance(TimeSpan.FromMinutes(11)); // the hold has expired; the expiry job has not run yet

        AssertOk(await PaySucceededAsync(booking));

        Assert.Equal(BookingStatus.Booked, (await LoadAsync(booking.BookingId)).Status);
    }

    [Fact]
    public async Task A_payment_after_the_hold_expiry_ran_and_the_table_was_taken_is_refunded_in_full()
    {
        var booking = await BookAsync(partySize: 8); // T10, the only table for 8
        var intent = await IntentOfAsync(booking.BookingId);
        Infra.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, await HoldExpiry.RunOnceAsync(Ct));
        var taker = await BookAsync(partySize: 8, mobile: "021 011 0302");
        Assert.Equal(booking.TableIds, taker.TableIds);

        AssertOk(await PaySucceededAsync(booking));

        var late = await LoadAsync(booking.BookingId);
        Assert.Equal(BookingStatus.Cancelled, late.Status);
        Assert.Equal(CancelReason.HoldExpired, late.CancellationReason);
        Assert.Equal(DepositStatus.Captured, late.Deposit!.Status); // the money Stripe now holds, until charge.refunded
        Assert.Equal(["BookingCreated", "BookingCancelled", "LatePaymentReceived"], await OutboxTypesAsync(booking.BookingId));

        await DispatchAsync();
        var refund = Assert.Single(Payments.Refunds);
        Assert.Equal(intent, refund.PaymentIntentId);
        Assert.Equal(intent, refund.IdempotencyKey);
        Assert.Equal(booking.Deposit!.AmountCents, refund.Amount.Cents);
        Assert.Empty(await MessagesAboutAsync(booking.BookingId)); // no cancellation text for an expired hold

        AssertOk(await PostWebhookAsync(StripeEvents.ChargeRefunded(intent, booking.Deposit.AmountCents)));
        Assert.Equal(DepositStatus.Refunded, (await LoadAsync(booking.BookingId)).Deposit!.Status);
    }

    [Fact]
    public async Task An_amount_that_differs_from_the_deposit_is_recorded_and_not_captured()
    {
        var booking = await BookAsync();

        AssertOk(await PaySucceededAsync(booking, amountCents: 5_000));

        Assert.Equal(BookingStatus.PendingPayment, (await LoadAsync(booking.BookingId)).Status);
        await using var db = Database.Context();
        var audit = await db.Audit.AsNoTracking().SingleAsync(a => a.EventType == "PaymentWebhookRefused", Ct);
        Assert.Equal(booking.BookingId, audit.AggregateId);
        Assert.Equal("deposit_amount_mismatch", audit.Summary.RootElement.GetProperty("code").GetString());
        Assert.Equal(5_000, audit.Summary.RootElement.GetProperty("amountCents").GetInt64());
        Assert.Equal(12_000, audit.Summary.RootElement.GetProperty("expectedCents").GetInt64());
    }

    [Fact]
    public async Task A_payment_for_another_intent_is_refused()
    {
        var booking = await BookAsync();

        AssertOk(await PostWebhookAsync(StripeEvents.PaymentSucceeded("pi_someone_else", booking.BookingId, GoldenDragon.VenueId.Value, 12_000)));

        Assert.Equal(BookingStatus.PendingPayment, (await LoadAsync(booking.BookingId)).Status);
        await using var db = Database.Context();
        var audit = await db.Audit.AsNoTracking().SingleAsync(a => a.EventType == "PaymentWebhookRefused", Ct);
        Assert.Equal("payment_intent_mismatch", audit.Summary.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_unknown_event_type_is_acknowledged_and_its_inbox_row_kept()
    {
        var eventId = StripeEvents.NewEventId();

        AssertOk(await PostWebhookAsync(StripeEvents.Other("customer.created", eventId)));

        Assert.Equal(1, await InboxRowsAsync(eventId));
    }

    [Fact]
    public async Task An_event_in_another_API_version_is_still_read()
    {
        var booking = await BookAsync();
        var body = StripeEvents.PaymentSucceeded(
            await IntentOfAsync(booking.BookingId), booking.BookingId, GoldenDragon.VenueId.Value, 12_000, apiVersion: StripeEvents.OldApiVersion);

        AssertOk(await PostWebhookAsync(body));

        Assert.Equal(BookingStatus.Booked, (await LoadAsync(booking.BookingId)).Status);
    }

    [Fact]
    public async Task A_body_over_64_KB_is_413()
    {
        var body = StripeEvents.Other("customer.created") + new string(' ', 70 * 1024);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostWebhookAsync(body)).StatusCode);
    }

    [Fact]
    public async Task The_webhook_is_not_limited_by_the_anonymous_rate_limit()
    {
        for (var i = 0; i < 70; i++)
        {
            AssertOk(await PostWebhookAsync(StripeEvents.Other("customer.updated")));
        }
    }
}
