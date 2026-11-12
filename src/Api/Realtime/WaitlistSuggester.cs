using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Features.Seating;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Realtime;

/// <summary>
/// The "Waitlist suggester" consumer (Design §4.3, §11.4, §11.5; AC4.2, BR-09): when a table frees (<c>TableCleared</c>,
/// or <c>BookingCancelled</c> for the cancelled booking's tables), it finds the first waiting party that fits the table
/// now and pushes <c>waitlistChanged {entryIds: [entry], suggestedEntryId, freedTableId}</c>. Nothing is pushed when no
/// party fits. It reads but never writes, so it is registered unwrapped (<c>idempotent: false</c>); a repeat only costs a
/// repeated highlight. A failed push is logged and swallowed like <see cref="RealtimeNotifier{T}"/>; a failed read is not,
/// so the dispatcher rolls back this handler's savepoint and retries the event.
/// </summary>
internal sealed class WaitlistSuggester(
    ReservationsDbContext db,
    AvailabilityReader reader,
    IVenueBroadcaster broadcaster,
    TimeProvider clock,
    ILogger<WaitlistSuggester> logger) : IEventHandler<TableCleared>, IEventHandler<BookingCancelled>
{
    public const string Consumer = "waitlist-suggester";

    private static readonly WaitlistStatus[] _active = [WaitlistStatus.Waiting, WaitlistStatus.Notified];

    public string ConsumerName => Consumer;

    public Task HandleAsync(TableCleared domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        return SuggestAsync(domainEvent.VenueId, domainEvent.TableIds, context, cancellationToken);
    }

    public async Task HandleAsync(BookingCancelled domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var tableIds = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Where(b => b.Id == domainEvent.BookingId)
            .SelectMany(b => b.Tables.Select(t => t.TableId))
            .ToListAsync(cancellationToken);
        await SuggestAsync(domainEvent.VenueId, tableIds, context, cancellationToken);
    }

    private async Task SuggestAsync(VenueId venueId, IReadOnlyList<TableId> freedTables, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (freedTables.Count == 0)
        {
            return;
        }

        var waiting = await db.WaitlistEntries.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Where(w => w.VenueId == venueId && _active.Contains(w.Status))
            .Select(w => new { w.Id, w.PartySize, w.JoinedAt })
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return;
        }

        var venue = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == venueId, cancellationToken);
        if (venue is null)
        {
            return;
        }

        var floor = await reader.LoadNowAsync(venue, clock.GetUtcNow(), cancellationToken);
        var parties = waiting.Select(w => new WaitingParty(w.Id, w.PartySize.Value, w.JoinedAt)).ToList();
        foreach (var table in freedTables.Select(venue.FindTable).OfType<Domain.Venues.Table>())
        {
            var suggested = SeatingRules.SuggestEntry(floor, new TableInfo(table.Id, table.Name, table.Seats), parties);
            if (suggested is not { } entryId)
            {
                continue;
            }

            var notice = new RealtimeNotice(
                NoticeMethods.WaitlistChanged, new WaitlistChangedNotice([entryId.Value], entryId.Value, table.Id.Value));
            try
            {
                await broadcaster.SendAsync(venueId, notice, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                RealtimeLog.PushFailed(logger, ex.GetType().Name, notice.Method, context.EventType, context.EventId);
            }

            return; // one suggestion per freed booking: the first freed table that a waiting party fits
        }
    }
}
