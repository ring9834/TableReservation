using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Venues;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Messaging;

/// <summary>
/// The "Notifications" consumer (Design §4.3, §7, §7.2): queues texts to guests. It only adds <see cref="Message"/> rows
/// as Queued, in the dispatcher's transaction and its own savepoint (registered wrapped in the inbox decorator, so each
/// event queues at most once); it never calls a sender. <c>MessageChangeInterceptor</c> wakes the delivery worker
/// after the commit.
/// <list type="bullet">
/// <item><see cref="BookingCreated"/> → Confirmation, for online and phone bookings created as Booked. Walk-ins (and
/// parties seated from the waitlist, also walk-ins) and PendingPayment bookings get none.</item>
/// <item><see cref="DepositCaptured"/> → Confirmation (M8a): a deposit booking is confirmed once the payment succeeds.</item>
/// <item><see cref="BookingCancelled"/> by the guest or a host → Cancellation (M8a). None when the hold expired: the
/// guest never had a confirmed booking (and a late payment is refunded with its own text).</item>
/// <item><see cref="DepositRefunded"/> → Refund with the amount (M8a), when Stripe has confirmed it.</item>
/// <item><see cref="WaitlistNotified"/> → TableReady to the entry's guest. Every Notify queues a new one (re-notifying,
/// on the same table or another, is a deliberate tap); an earlier TableReady for the entry that is still Queued is
/// failed as <c>superseded</c>, so the guest never gets two. An entry that has ended gets none.</item>
/// </list>
/// The language is the booking's or the entry's (M7a), not the screen's. Reads ignore the venue filter (the dispatcher
/// has no venue) and match the event's venue instead.
/// </summary>
internal sealed class NotificationHandler(
    ReservationsDbContext db,
    MessageComposer composer,
    TimeProvider clock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock)
    : IEventHandler<BookingCreated>, IEventHandler<DepositCaptured>, IEventHandler<BookingCancelled>, IEventHandler<DepositRefunded>,
        IEventHandler<WaitlistNotified>
{
    public const string Consumer = "notifications";

    public const string Superseded = "superseded";

    public string ConsumerName => Consumer;

    public async Task HandleAsync(BookingCreated domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var createdAsBooked = domainEvent.Channel is BookingChannel.Online or BookingChannel.Phone && domainEvent.HoldExpiresAt is null;
        if (!createdAsBooked || domainEvent.GuestId is not { } guestId)
        {
            return;
        }

        var venue = await LoadVenueAsync(domainEvent.VenueId, cancellationToken);
        var language = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Where(b => b.Id == domainEvent.BookingId && b.VenueId == domainEvent.VenueId)
            .Select(b => (Language?)b.Language)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Booking {domainEvent.BookingId.Value} not found.");

        var composed = composer.Compose(MessagePurpose.Confirmation, language, new TemplateModel(
            venue.Name, venue.FindTimeZone(), domainEvent.Reference, domainEvent.PartySize, domainEvent.StartsAt));
        Queue(domainEvent.VenueId, guestId, MessagePurpose.Confirmation, language, composed, domainEvent.BookingId.Value);
    }

    public Task HandleAsync(DepositCaptured domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return QueueForBookingAsync(domainEvent.VenueId, domainEvent.BookingId, MessagePurpose.Confirmation, null, cancellationToken);
    }

    public Task HandleAsync(BookingCancelled domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return domainEvent.Reason is CancelReason.GuestRequest or CancelReason.HostRequest
            ? QueueForBookingAsync(domainEvent.VenueId, domainEvent.BookingId, MessagePurpose.Cancellation, null, cancellationToken)
            : Task.CompletedTask;
    }

    public Task HandleAsync(DepositRefunded domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return QueueForBookingAsync(
            domainEvent.VenueId, domainEvent.BookingId, MessagePurpose.Refund, domainEvent.AmountCents, cancellationToken);
    }

    public async Task HandleAsync(WaitlistNotified domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var entry = await db.WaitlistEntries.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleOrDefaultAsync(w => w.Id == domainEvent.WaitlistEntryId && w.VenueId == domainEvent.VenueId, cancellationToken)
            ?? throw new InvalidOperationException($"Waitlist entry {domainEvent.WaitlistEntryId.Value} not found.");
        if (!entry.IsActive)
        {
            return;
        }

        var subjectId = entry.Id.Value;
        var earlier = await db.Messages.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
            .Where(m => m.SubjectId == subjectId && m.Purpose == MessagePurpose.TableReady && m.Status == MessageStatus.Queued)
            .ToListAsync(cancellationToken);
        foreach (var message in earlier)
        {
            message.MarkFailed(Superseded);
        }

        var venue = await LoadVenueAsync(domainEvent.VenueId, cancellationToken);
        var composed = composer.Compose(MessagePurpose.TableReady, entry.Language, new TemplateModel(venue.Name, venue.FindTimeZone()));
        Queue(domainEvent.VenueId, entry.GuestId, MessagePurpose.TableReady, entry.Language, composed, subjectId);
    }

    /// <summary>A text about a booking, in the booking's language, to its guest; none for a booking without a guest.</summary>
    private async Task QueueForBookingAsync(
        VenueId venueId, BookingId bookingId, MessagePurpose purpose, long? amountCents, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == bookingId && b.VenueId == venueId, cancellationToken)
            ?? throw new InvalidOperationException($"Booking {bookingId.Value} not found.");
        if (booking.GuestId is not { } guestId)
        {
            return;
        }

        var venue = await LoadVenueAsync(venueId, cancellationToken);
        var amount = amountCents is { } cents ? new Money(cents, booking.Deposit?.Amount.Currency ?? venue.Currency) : (Money?)null;
        var composed = composer.Compose(purpose, booking.Language, new TemplateModel(
            venue.Name, venue.FindTimeZone(), booking.Reference.Value, booking.PartySize.Value, booking.Period.Start, amount));
        Queue(venueId, guestId, purpose, booking.Language, composed, bookingId.Value);
    }

    private async Task<Venue> LoadVenueAsync(VenueId venueId, CancellationToken cancellationToken) =>
        await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == venueId, cancellationToken)
        ?? throw new InvalidOperationException($"Venue {venueId.Value} not found.");

    private void Queue(VenueId venueId, GuestId guestId, MessagePurpose purpose, Language language, ComposedMessage composed, Guid subjectId) =>
        db.Messages.Add(Message.Queue(
            venueId, guestId, purpose, MessageChannel.Sms, language, composed.Body, composed.Actions, subjectId,
            clock.GetUtcNow(), infrastructureClock.GetUtcNow()));
}
