using System.Text.Json;
using Reservation.Api.Features.Pos;
using Reservation.Contracts.Pos;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Infrastructure.Pos;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Pos;

/// <summary>Design §6.3, §6.7: the v1 envelope, idempotency keys, the signature scheme and the log's masking.</summary>
public class PosMessageTests
{
    private static readonly DateTimeOffset _queuedAt = new(2026, 11, 24, 2, 0, 0, TimeSpan.Zero);

    private static readonly VenueId _venueId = new(Guid.CreateVersion7(TestData.Created));

    [Fact]
    public void UpsertMember_carries_the_envelope_with_no_tables_and_the_key_of_its_event()
    {
        var guest = Guest.Create(
            _venueId, PersonName.Create("Mei Lin").Value, PhoneNumber.Create("021 123 4567").Value, null, Language.Zh, null, TestData.Created);
        var bookingId = new BookingId(Guid.CreateVersion7());
        var created = new BookingCreated(
            Guid.CreateVersion7(), TestData.Created, _venueId, bookingId, guest.Id, "GD7K4P", BookingChannel.Online, 2,
            TestData.Start, TestData.Start.AddMinutes(75), [TestData.Table4], null, null);
        var id = PosMessageId.New(_queuedAt);

        var row = PosMessageFactory.UpsertMember(created, guest, id, 7, TestData.Created, _queuedAt);

        Assert.Equal((PosDirection.Outbound, PosMessageStatus.Waiting, 7L, $"{created.EventId}:UpsertMember"),
            (row.Direction, row.Status, row.VenueSequence, row.IdempotencyKey));
        Assert.Equal((_queuedAt, (DateTimeOffset?)_queuedAt, TestData.Created), (row.QueuedAt, row.NextAttemptAt, row.CreatedAt));
        var json = row.Payload.RootElement;
        string[] fields = ["bookingId", "language", "memberPhone", "messageId", "messageType", "name", "occurredAt", "tableIds", "venueId", "version"];
        Assert.Equal(fields, json.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, json.GetProperty("version").GetInt32());
        Assert.Equal(id.Value, json.GetProperty("messageId").GetGuid());
        Assert.Equal(0, json.GetProperty("tableIds").GetArrayLength());
        Assert.Equal("+64211234567", json.GetProperty("memberPhone").GetString());
        Assert.Equal("zh", json.GetProperty("language").GetString());

        var parsed = JsonSerializer.Deserialize<PosUpsertMember>(json.GetRawText(), PosJson.Options)!;
        Assert.Equal((_venueId.Value, bookingId.Value, TestData.Created), (parsed.VenueId, parsed.BookingId, parsed.OccurredAt));
    }

    [Fact]
    public void OpenTable_and_ApplyDeposit_from_one_seat_share_the_event_but_not_the_key()
    {
        var booking = BookingFactory.BookedWithDeposit();
        var seated = new BookingSeated(
            Guid.CreateVersion7(), TestData.Start, _venueId, booking.Id, [TestData.Table8], 6, SeatSource.Booking, null, TestData.Start);

        var open = PosMessageFactory.OpenTable(seated, booking, PosMessageId.New(_queuedAt), 1, TestData.Start, _queuedAt);
        var deposit = PosMessageFactory.ApplyDeposit(seated, booking.Deposit!, PosMessageId.New(_queuedAt), 2, TestData.Start, _queuedAt);

        Assert.Equal($"{seated.EventId}:OpenTable", open.IdempotencyKey);
        Assert.Equal($"{seated.EventId}:ApplyDeposit", deposit.IdempotencyKey);
        Assert.Equal(seated.EventId, open.SourceEventId);
        Assert.Equal(6, open.Payload.RootElement.GetProperty("partySize").GetInt32());
        Assert.Equal(TestData.ContactName.Value, open.Payload.RootElement.GetProperty("guestName").GetString());
        Assert.Equal([TestData.Table8.Value], deposit.Payload.RootElement.GetProperty("tableIds").EnumerateArray().Select(t => t.GetGuid()));
        Assert.Equal(12_000, deposit.Payload.RootElement.GetProperty("amountCents").GetInt64());
        Assert.Equal(BookingFactory.PaymentIntent, deposit.Payload.RootElement.GetProperty("paymentRef").GetString());
    }

    [Fact]
    public void A_signature_verifies_only_with_the_same_secret_body_and_time_window()
    {
        const string body = """{"messageType":"BillClosed"}""";
        var now = _queuedAt;
        var header = PosWebhookSigner.Sign(body, "secret", now);
        var tolerance = TimeSpan.FromMinutes(5);

        Assert.Matches("^t=[0-9]+,v1=[0-9a-f]{64}$", header);
        Assert.True(PosWebhookSigner.Verify(body, header, "secret", now.AddMinutes(4), tolerance));
        Assert.False(PosWebhookSigner.Verify(body, header, "other", now, tolerance));
        Assert.False(PosWebhookSigner.Verify(body + " ", header, "secret", now, tolerance));
        Assert.False(PosWebhookSigner.Verify(body, header, "secret", now.AddMinutes(6), tolerance));
        Assert.False(PosWebhookSigner.Verify(body, null, "secret", now, tolerance));
        Assert.False(PosWebhookSigner.Verify(body, header, null, now, tolerance));
        Assert.False(PosWebhookSigner.Verify(body, "t=abc,v1=zz", "secret", now, tolerance));
    }

    [Fact]
    public void The_log_masks_phone_numbers_in_the_payload_like_the_phone_panel()
    {
        using var payload = JsonDocument.Parse("""{"memberPhone":"+64211234567","name":"Mei Lin","tableIds":[]}""");

        var masked = GetPosLogHandler.MaskedPayload(payload);

        Assert.Equal(PhoneNumber.Create("021 123 4567").Value.Masked(), masked.GetProperty("memberPhone").GetString());
        Assert.Equal("+64******567", masked.GetProperty("memberPhone").GetString());
        Assert.Equal("Mei Lin", masked.GetProperty("name").GetString());
        Assert.Equal("+64211234567", payload.RootElement.GetProperty("memberPhone").GetString()); // the stored payload is untouched
    }

    [Fact]
    public void Unavailable_retries_count_no_attempt_until_the_window_has_passed()
    {
        var row = Outbound();

        row.DeferUnavailable(_queuedAt.AddHours(1), _queuedAt.AddHours(1).AddSeconds(5), TimeSpan.FromHours(24));
        Assert.Equal((PosMessageStatus.Waiting, 0, PosMessage.Unavailable), (row.Status, row.Attempts, row.LastError));

        row.DeferUnavailable(_queuedAt.AddHours(24).AddSeconds(1), _queuedAt.AddHours(25), TimeSpan.FromHours(24));
        Assert.Equal((PosMessageStatus.Failed, 0, PosMessage.UnavailableTooLong), (row.Status, row.Attempts, row.LastError));
        Assert.Throws<InvalidOperationException>(() => row.MarkSent(TestData.Start));
    }

    private static PosMessage Outbound() => PosMessage.Outbound(
        PosMessageId.New(_queuedAt), _venueId, PosMessageTypes.OpenTable, "k", Guid.CreateVersion7(),
        new BookingId(Guid.CreateVersion7()), JsonDocument.Parse("{}"), 1, TestData.Start, _queuedAt);
}
