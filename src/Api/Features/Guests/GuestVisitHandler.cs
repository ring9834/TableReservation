using Microsoft.EntityFrameworkCore;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Guests;

/// <summary>
/// The "Guest" consumer (Design §4.3, AC6.6, §6.7): keeps the guest's visits and spend.
/// <list type="bullet">
/// <item><see cref="BookingCompleted"/> counts the visit, whether the POS closed the bill or a host cleared the table,
/// with the bill total when there is one (<c>Guest.RecordVisit</c>). A guest whose bill never matched still has the
/// visit.</item>
/// <item><see cref="BookingBillAttached"/> (a late or manual match) records the spend only (<c>Guest.RecordLateBill</c>):
/// the visit was counted when the table was cleared.</item>
/// </list>
/// A booking without a guest (a walk-in who gave no number) is skipped. Wrapped in the inbox decorator, so each event
/// counts once. Reads ignore the venue filter (the dispatcher has no venue) and match the event's venue.
/// </summary>
internal sealed class GuestVisitHandler(ReservationsDbContext db) : IEventHandler<BookingCompleted>, IEventHandler<BookingBillAttached>
{
    public const string Consumer = "guest-visits";

    public string ConsumerName => Consumer;

    public async Task HandleAsync(BookingCompleted domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var guest = await GuestOfAsync(domainEvent.VenueId, domainEvent.BookingId, cancellationToken);
        guest?.RecordVisit(domainEvent.ClosedAt, domainEvent.BillTotalCents is { } cents ? Money.FromNzd(cents) : null);
    }

    public async Task HandleAsync(BookingBillAttached domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var guest = await GuestOfAsync(domainEvent.VenueId, domainEvent.BookingId, cancellationToken);
        guest?.RecordLateBill(Money.FromNzd(domainEvent.BillTotalCents));
    }

    private async Task<Guest?> GuestOfAsync(VenueId venueId, BookingId bookingId, CancellationToken cancellationToken)
    {
        var guestId = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Where(b => b.Id == bookingId && b.VenueId == venueId)
            .Select(b => b.GuestId)
            .SingleOrDefaultAsync(cancellationToken);
        return guestId is { } id
            ? await db.Guests.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
                .SingleOrDefaultAsync(g => g.Id == id && g.VenueId == venueId, cancellationToken)
            : null;
    }
}
