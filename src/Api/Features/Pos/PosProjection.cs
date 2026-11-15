using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservation.Contracts.Pos;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;
using Reservation.Infrastructure.Pos;

namespace Reservation.Api.Features.Pos;

/// <summary>
/// Builds outbound POS messages (Design §6.3, §6.7): the v1 envelope (<c>messageId</c> = the row's ID, the venue, the
/// booking, the tables, <c>occurredAt</c> = the event's business time) plus the type's fields, and the idempotency key
/// <c>{eventId}:{MessageType}</c> the POS uses to ignore repeats. The payload is stored unmasked: the POS needs the
/// member's phone. It never reaches a log.
/// </summary>
internal static class PosMessageFactory
{
    public static string IdempotencyKey(Guid eventId, string messageType) => $"{eventId}:{messageType}";

    public static PosMessage UpsertMember(
        BookingCreated domainEvent, Guest guest, PosMessageId id, long sequence, DateTimeOffset createdAt, DateTimeOffset queuedAt)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(guest);
        var message = new PosUpsertMember
        {
            MessageId = id.Value,
            MessageType = PosMessageTypes.UpsertMember,
            VenueId = domainEvent.VenueId.Value,
            BookingId = domainEvent.BookingId.Value,
            TableIds = [],
            OccurredAt = domainEvent.OccurredAt.ToUniversalTime(),
            MemberPhone = guest.Mobile.E164,
            Name = guest.Name.Value,
            Language = LanguageCode(guest.Language),
        };
        return Row(message, domainEvent.EventId, domainEvent.BookingId, sequence, createdAt, queuedAt);
    }

    public static PosMessage OpenTable(
        BookingSeated domainEvent, Booking booking, PosMessageId id, long sequence, DateTimeOffset createdAt, DateTimeOffset queuedAt)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(booking);
        var message = new PosOpenTable
        {
            MessageId = id.Value,
            MessageType = PosMessageTypes.OpenTable,
            VenueId = domainEvent.VenueId.Value,
            BookingId = domainEvent.BookingId.Value,
            TableIds = [.. domainEvent.TableIds.Select(t => t.Value)],
            OccurredAt = domainEvent.OccurredAt.ToUniversalTime(),
            PartySize = domainEvent.PartySize,
            GuestName = booking.ContactName?.Value,
            SeatedAt = domainEvent.SeatedAt.ToUniversalTime(),
        };
        return Row(message, domainEvent.EventId, domainEvent.BookingId, sequence, createdAt, queuedAt);
    }

    public static PosMessage ApplyDeposit(
        BookingSeated domainEvent, Deposit deposit, PosMessageId id, long sequence, DateTimeOffset createdAt, DateTimeOffset queuedAt)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(deposit);
        var message = new PosApplyDeposit
        {
            MessageId = id.Value,
            MessageType = PosMessageTypes.ApplyDeposit,
            VenueId = domainEvent.VenueId.Value,
            BookingId = domainEvent.BookingId.Value,
            TableIds = [.. domainEvent.TableIds.Select(t => t.Value)],
            OccurredAt = domainEvent.OccurredAt.ToUniversalTime(),
            AmountCents = deposit.Amount.Cents,
            Currency = deposit.Amount.Currency,
            PaymentRef = deposit.PaymentIntentId
                ?? throw new InvalidOperationException($"Booking {domainEvent.BookingId.Value} has a captured deposit without a payment intent."),
        };
        return Row(message, domainEvent.EventId, domainEvent.BookingId, sequence, createdAt, queuedAt);
    }

    /// <summary><c>en</c> or <c>zh</c>, as the API's own language codes.</summary>
    public static string LanguageCode(Language language) => language switch
    {
        Language.Zh => "zh",
        _ => "en",
    };

    private static PosMessage Row(
        PosEnvelope message, Guid eventId, BookingId bookingId, long sequence, DateTimeOffset createdAt, DateTimeOffset queuedAt) =>
        PosMessage.Outbound(
            new PosMessageId(message.MessageId),
            new VenueId(message.VenueId),
            message.MessageType,
            IdempotencyKey(eventId, message.MessageType),
            eventId,
            bookingId,
            JsonSerializer.SerializeToDocument(message, message.GetType(), PosJson.Options),
            sequence,
            createdAt,
            queuedAt);
}

/// <summary>
/// The "POS" consumer (Design §4.3, §6.1–§6.3, §6.7): turns events into outbound POS messages. It only adds
/// <c>pos_messages</c> rows as <c>Waiting</c>, in the dispatcher's transaction and its own savepoint (registered wrapped in
/// the inbox decorator, so each event queues its messages once); it never calls the POS. <c>PosDeliveryWorker</c> sends
/// them after the commit, in <c>venue_sequence</c> order.
/// <list type="bullet">
/// <item><see cref="BookingCreated"/> with a guest → <c>UpsertMember</c>. A walk-in without a guest gets none.</item>
/// <item><see cref="BookingSeated"/> → <c>OpenTable</c>, then <c>ApplyDeposit</c> when the deposit is captured (the
/// lower sequence goes to <c>OpenTable</c>).</item>
/// </list>
/// Reads ignore the venue filter (the dispatcher has no venue) and match the event's venue instead.
/// </summary>
internal sealed class PosProjectionHandler(
    ReservationsDbContext db,
    PosSequenceAllocator sequences,
    TimeProvider clock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock)
    : IEventHandler<BookingCreated>, IEventHandler<BookingSeated>
{
    public const string Consumer = "pos";

    public string ConsumerName => Consumer;

    public async Task HandleAsync(BookingCreated domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        if (domainEvent.GuestId is not { } guestId)
        {
            return;
        }

        var guest = await db.Guests.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleOrDefaultAsync(g => g.Id == guestId && g.VenueId == domainEvent.VenueId, cancellationToken)
            ?? throw new InvalidOperationException($"Guest {guestId.Value} not found.");

        var sequence = await sequences.NextAsync(domainEvent.VenueId, cancellationToken);
        db.PosMessages.Add(PosMessageFactory.UpsertMember(domainEvent, guest, NewId(), sequence, clock.GetUtcNow(), infrastructureClock.GetUtcNow()));
    }

    public async Task HandleAsync(BookingSeated domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var booking = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == domainEvent.BookingId && b.VenueId == domainEvent.VenueId, cancellationToken)
            ?? throw new InvalidOperationException($"Booking {domainEvent.BookingId.Value} not found.");

        var openTable = await sequences.NextAsync(domainEvent.VenueId, cancellationToken);
        db.PosMessages.Add(PosMessageFactory.OpenTable(domainEvent, booking, NewId(), openTable, clock.GetUtcNow(), infrastructureClock.GetUtcNow()));

        if (booking.Deposit is { Status: DepositStatus.Captured } deposit)
        {
            var applyDeposit = await sequences.NextAsync(domainEvent.VenueId, cancellationToken);
            db.PosMessages.Add(PosMessageFactory.ApplyDeposit(domainEvent, deposit, NewId(), applyDeposit, clock.GetUtcNow(), infrastructureClock.GetUtcNow()));
        }
    }

    /// <summary>UUID v7 on the infrastructure clock, so IDs follow real time (as message IDs do, §7.2).</summary>
    private PosMessageId NewId() => PosMessageId.New(infrastructureClock.GetUtcNow());
}
