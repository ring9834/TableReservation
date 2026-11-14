using System.Net;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Outbox;
using Reservation.Tests.Integration.Api;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Payments;

/// <summary>
/// The payment hold on the infrastructure clock (rule 14, Design §8.4) and refunds on cancellation (BR-07, §8.3):
/// decided once at cancel, requested through the outbox with key <c>{eventId}:refund</c>, recorded on <c>charge.refunded</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class HoldExpiryAndRefundTests(PostgresFixture postgres) : PaymentTestBase(postgres)
{
    // ---------- Hold expiry ----------

    [Fact]
    public async Task The_hold_expires_after_10_minutes_of_real_time_while_the_business_clock_stands_still()
    {
        var businessNow = Factory.Clock.Now;
        var booking = await BookAsync();

        Infra.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await HoldExpiry.RunOnceAsync(Ct));
        Assert.Equal(BookingStatus.PendingPayment, (await LoadAsync(booking.BookingId)).Status);

        Infra.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await HoldExpiry.RunOnceAsync(Ct));

        Assert.Equal(businessNow, Factory.Clock.Now); // only the infrastructure clock moved
        var expired = await LoadAsync(booking.BookingId);
        Assert.Equal(BookingStatus.Cancelled, expired.Status);
        Assert.Equal(CancelReason.HoldExpired, expired.CancellationReason);
        Assert.Equal(RefundDecision.NotApplicable, expired.CancellationRefund);
        Assert.Equal(DepositStatus.Released, expired.Deposit!.Status);
        Assert.All(expired.Tables, t => Assert.False(t.Active)); // the table is free again
        Assert.Equal(["BookingCreated", "BookingCancelled"], await OutboxTypesAsync(booking.BookingId));

        await using (var db = Database.Context())
        {
            var cancelled = await db.Outbox.AsNoTracking().SingleAsync(o => o.AggregateId == booking.BookingId && o.EventType == "BookingCancelled", Ct);
            Assert.Equal(businessNow, cancelled.Envelope.RootElement.GetProperty("occurredAt").GetDateTimeOffset()); // event time: business clock
        }

        Assert.Equal(0, await HoldExpiry.RunOnceAsync(Ct)); // nothing more to do
        await DispatchAsync();
        Assert.Empty(Payments.Refunds);
        Assert.Empty(await MessagesAboutAsync(booking.BookingId)); // no texts for an expired hold
    }

    // ---------- Refund on cancel ----------

    [Fact]
    public async Task A_host_cancel_refunds_in_full_once_and_the_deposit_is_refunded_only_on_charge_refunded_AC5_5()
    {
        var booking = await PaidBookingAsync();
        var intent = await IntentOfAsync(booking.BookingId);

        var cancelled = await CancelAsHostAsync(booking.BookingId);
        Assert.Equal(RefundDecision.Full, cancelled.RefundDecision);

        await DispatchAsync();
        await RedeliverAsync(booking.BookingId, "BookingCancelled");

        var refund = Assert.Single(Payments.Refunds);
        var eventId = await EventIdAsync(booking.BookingId, "BookingCancelled");
        Assert.Equal($"{eventId}:refund", refund.IdempotencyKey);
        Assert.Equal(intent, refund.PaymentIntentId);
        Assert.Equal(12_000, refund.Amount.Cents);
        Assert.Equal(DepositStatus.Captured, (await LoadAsync(booking.BookingId)).Deposit!.Status); // not yet

        AssertOk(await PostWebhookAsync(StripeEvents.ChargeRefunded(intent, 12_000, refundId: "re_test_1")));

        var refunded = await LoadAsync(booking.BookingId);
        Assert.Equal(DepositStatus.Refunded, refunded.Deposit!.Status);
        Assert.Equal("re_test_1", refunded.Deposit.RefundId);
        Assert.Contains("DepositRefunded", await OutboxTypesAsync(booking.BookingId));
    }

    [Fact]
    public async Task A_failed_refund_is_retried_with_the_same_key()
    {
        var booking = await PaidBookingAsync();
        await CancelAsHostAsync(booking.BookingId);

        Payments.Fail = true;
        await DispatchAsync();
        await using (var db = Database.Context())
        {
            var row = await db.Outbox.AsNoTracking().SingleAsync(o => o.AggregateId == booking.BookingId && o.EventType == "BookingCancelled", Ct);
            Assert.Equal(OutboxStatus.Pending, row.Status);
            Assert.Equal(1, row.Attempts);
        }

        Payments.Fail = false;
        Infra.Advance(TimeSpan.FromSeconds(6)); // the first retry is 5 s later (infrastructure clock)
        await DispatchAsync();

        Assert.Equal(2, Payments.Refunds.Count);
        Assert.Single(Payments.Refunds.Select(r => r.IdempotencyKey).Distinct());
    }

    [Fact]
    public async Task A_partial_refund_from_stripe_is_ignored()
    {
        var booking = await PaidBookingAsync();
        await CancelAsHostAsync(booking.BookingId);

        AssertOk(await PostWebhookAsync(StripeEvents.ChargeRefunded(await IntentOfAsync(booking.BookingId), 5_000, refunded: false)));

        Assert.Equal(DepositStatus.Captured, (await LoadAsync(booking.BookingId)).Deposit!.Status);
    }

    // ---------- Texts ----------

    [Fact]
    public async Task A_host_cancel_sends_the_cancellation_and_the_refund_text_in_english()
    {
        var booking = await PaidBookingAsync();
        await CancelAsHostAsync(booking.BookingId);
        await DispatchAsync();
        AssertOk(await PostWebhookAsync(StripeEvents.ChargeRefunded(await IntentOfAsync(booking.BookingId), 12_000)));
        await DispatchAsync();

        // Message IDs follow the infrastructure clock, which stands still here, so compare by purpose.
        var messages = (await MessagesAboutAsync(booking.BookingId)).ToDictionary(m => m.Purpose);
        Assert.Equal([MessagePurpose.Confirmation, MessagePurpose.Cancellation, MessagePurpose.Refund], messages.Keys.Order());
        Assert.All(messages.Values, m => Assert.Equal(Language.En, m.Language));
        Assert.Contains("is cancelled", messages[MessagePurpose.Cancellation].Body, StringComparison.Ordinal);
        Assert.Contains(booking.Reference, messages[MessagePurpose.Cancellation].Body, StringComparison.Ordinal);
        Assert.Contains("NZ$120.00", messages[MessagePurpose.Refund].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chinese_booking_gets_chinese_cancellation_and_refund_texts()
    {
        var booking = await PaidBookingAsync(language: "zh");
        await CancelAsHostAsync(booking.BookingId);
        await DispatchAsync();
        AssertOk(await PostWebhookAsync(StripeEvents.ChargeRefunded(await IntentOfAsync(booking.BookingId), 12_000)));
        await DispatchAsync();

        var messages = await MessagesAboutAsync(booking.BookingId);
        Assert.All(messages, m => Assert.Equal(Language.Zh, m.Language));
        Assert.Contains("已取消", messages.Single(m => m.Purpose == MessagePurpose.Cancellation).Body, StringComparison.Ordinal);
        Assert.Contains("NZ$120.00", messages.Single(m => m.Purpose == MessagePurpose.Refund).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_cancel_of_a_booking_without_a_deposit_sends_a_cancellation_and_requests_no_refund()
    {
        var booking = await BookAsync(partySize: 2);
        var cancelled = await CancelAsHostAsync(booking.BookingId);
        await DispatchAsync();

        Assert.Equal(RefundDecision.NotApplicable, cancelled.RefundDecision);
        Assert.Empty(Payments.Refunds);
        Assert.Equal(
            [MessagePurpose.Confirmation, MessagePurpose.Cancellation],
            (await MessagesAboutAsync(booking.BookingId)).Select(m => m.Purpose).Order());
    }

    private async Task<CreateBookingResponse> PaidBookingAsync(string language = "en")
    {
        var booking = await BookAsync(language: language);
        AssertOk(await PaySucceededAsync(booking));
        await DispatchAsync();
        return booking;
    }

    private async Task<BookingStateResponse> CancelAsHostAsync(Guid bookingId)
    {
        var host = NewClient();
        await SignInAsync(host);
        var version = (await LoadAsync(bookingId)).Version;
        return await ReadAsync<BookingStateResponse>(await PostAsync(host, $"{Venue}/bookings/{bookingId}/cancel", new { version }));
    }

    private async Task<Guid> EventIdAsync(Guid bookingId, string eventType)
    {
        await using var db = Database.Context();
        return await db.Outbox.Where(o => o.AggregateId == bookingId && o.EventType == eventType).Select(o => o.EventId).SingleAsync(Ct);
    }
}
