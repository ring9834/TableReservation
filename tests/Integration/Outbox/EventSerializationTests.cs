using System.Text.Json;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Outbox;

namespace Reservation.Tests.Integration.Outbox;

/// <summary>Envelope, registry and retry policy without a database.</summary>
public sealed class EventSerializationTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 24, 5, 25, 0, TimeSpan.Zero);
    private static readonly VenueId _venue = new(Guid.CreateVersion7(_now));
    private readonly EventEnvelopeSerializer _serializer = new(new EventTypeRegistry());

    [Fact]
    public void Registry_knows_the_14_catalogue_events_by_unique_name()
    {
        var registry = new EventTypeRegistry();

        Assert.Equal(14, registry.All.Count); // LatePaymentReceived since M8a
        Assert.Equal(14, registry.All.Select(e => e.Name).Distinct().Count());
        Assert.Equal(typeof(BookingSeated), registry.Resolve("BookingSeated", 1));
        Assert.Equal(new EventMetadata("MessageSent", 1, "Message", typeof(MessageSent)), registry.MetadataOf(typeof(MessageSent)));
        Assert.Throws<InvalidOperationException>(() => registry.Resolve("BookingSeated", 2));
    }

    public static TheoryData<IDomainEvent> Events => new()
    {
        new BookingCreated(Guid.CreateVersion7(_now), _now, _venue, new BookingId(Guid.CreateVersion7()), null, "GD2A4K",
            BookingChannel.Online, 6, _now.AddHours(2), _now.AddHours(4), [new TableId(Guid.CreateVersion7())], 6000, _now.AddMinutes(15)),
        new BookingConfirmed(Guid.CreateVersion7(_now), _now, _venue, new BookingId(Guid.CreateVersion7()), Actor.ForGuest(new GuestId(Guid.CreateVersion7()))),
        new BookingSeated(Guid.CreateVersion7(_now), _now, _venue, new BookingId(Guid.CreateVersion7()),
            [new TableId(Guid.CreateVersion7()), new TableId(Guid.CreateVersion7())], 4, SeatSource.WalkIn, new WaitlistEntryId(Guid.CreateVersion7()), _now),
        new LatePaymentReceived(Guid.CreateVersion7(_now), _now, _venue, new BookingId(Guid.CreateVersion7()), "pi_test_late", 12_000),
        new MessageSent(Guid.CreateVersion7(_now), _now, _venue, new MessageId(Guid.CreateVersion7()), MessagePurpose.TableReady,
            MessageChannel.Sms, Guid.CreateVersion7(), "•••• 099"),
    };

    [Theory]
    [MemberData(nameof(Events))]
    public void Event_survives_a_round_trip_through_the_envelope(IDomainEvent domainEvent)
    {
        var context = new RequestContext();
        context.Set(Guid.CreateVersion7(), Guid.CreateVersion7(), Actor.ForStaff("demo-host", "Host"));

        var row = _serializer.Serialize(new RaisedEvent(domainEvent, 7), Guid.CreateVersion7(), context, _now);
        var back = _serializer.Deserialize(row);

        Assert.Equal(JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), EventJson.Options), JsonSerializer.Serialize(back, back.GetType(), EventJson.Options));
        var envelope = EventEnvelopeSerializer.ReadEnvelope(row);
        Assert.Equal(7, envelope.Sequence);
        Assert.Equal(context.CausationId, envelope.CausationId);
        Assert.Equal("staff", envelope.Actor.Type);
    }

    [Fact]
    public void Ids_and_enums_are_plain_uuids_and_camelCase_strings()
    {
        var bookingId = new BookingId(Guid.CreateVersion7());
        var seated = new BookingSeated(Guid.CreateVersion7(_now), _now, _venue, bookingId, [], 2, SeatSource.WalkIn, null, _now);

        var payload = EventEnvelopeSerializer.ReadEnvelope(_serializer.Serialize(new RaisedEvent(seated, 1), bookingId.Value, new RequestContext(), _now)).Payload;

        Assert.Equal(bookingId.Value, payload.GetProperty("bookingId").GetGuid());
        Assert.Equal("walkIn", payload.GetProperty("source").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("waitlistEntryId").ValueKind);
    }

    [Fact]
    public void Retry_policy_follows_the_ADR_002_schedule_and_gives_up_after_24_hours()
    {
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)],
            Enumerable.Range(1, 6).Select(OutboxRetryPolicy.DelayAfter));

        var created = _now;
        Assert.Equal(new OutboxRetry(OutboxStatus.Pending, created.AddSeconds(5)), OutboxRetryPolicy.Next(1, created, created));
        Assert.Equal(OutboxStatus.Pending, OutboxRetryPolicy.Next(9, created, created.AddHours(23).AddMinutes(30)).Status);
        Assert.Equal(new OutboxRetry(OutboxStatus.Failed, created.AddHours(23).AddMinutes(31)), OutboxRetryPolicy.Next(9, created, created.AddHours(23).AddMinutes(31)));
    }
}
