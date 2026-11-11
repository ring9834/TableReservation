using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Venues;

internal sealed record FloorAreaDto(Guid Id, string Name, int SortOrder);

/// <summary>Where the table is drawn, in floor units (ADR-001); <c>rotation</c> in degrees.</summary>
internal sealed record TablePositionDto(decimal X, decimal Y, decimal Width, decimal Height, decimal Rotation);

/// <summary>A short summary of a booking on a table, for the host screens only.</summary>
/// <param name="GuestName">The guest's full name as stored; <c>null</c> for a walk-in without a name.</param>
/// <param name="HasAllergy">The guest has allergy notes (AC2.6); the notes themselves are in the host list.</param>
internal sealed record TableBookingSummary(
    Guid BookingId,
    string Reference,
    BookingStatus Status,
    int PartySize,
    DateTime StartsAt,
    DateTime EndsAt,
    string? GuestName,
    bool HasAllergy);

/// <param name="State">Derived by <see cref="TableStateRule"/> at <c>at</c> (Design §2.5).</param>
/// <param name="Current">The booking seated at the table, if any.</param>
/// <param name="Next">The earliest booking still to come on the table today, if any.</param>
internal sealed record FloorTableDto(
    Guid Id,
    Guid AreaId,
    string Name,
    int Seats,
    TableShape Shape,
    TablePositionDto Position,
    TableState State,
    TableBookingSummary? Current,
    TableBookingSummary? Next);

/// <param name="At">The business-clock instant the states were derived at.</param>
/// <param name="Areas">In floor order.</param>
/// <param name="Tables">In floor order: area order, then as laid out.</param>
internal sealed record FloorPlanResponse(Guid VenueId, string TimeZone, DateTime At, IReadOnlyList<FloorAreaDto> Areas, IReadOnlyList<FloorTableDto> Tables);

internal sealed record GetFloorPlan(VenueId VenueId);

/// <summary>
/// §9.3 <c>GetFloorPlan</c> (Design §15.1): the layout plus each table's state at business-clock "now". Reads the
/// active bookings that matter now (seated, or still to come before the end of the venue-local day) with an
/// <c>AsNoTracking</c> projection; never loads aggregates.
/// </summary>
internal sealed class GetFloorPlanHandler(ReservationsDbContext db, TimeProvider clock) : IQueryHandler<GetFloorPlan, FloorPlanResponse>
{
    private static readonly BookingStatus[] _upcoming = [BookingStatus.PendingPayment, BookingStatus.Booked, BookingStatus.Confirmed];

    public async Task<Result<FloorPlanResponse>> Handle(GetFloorPlan query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var timeZone = venue.FindTimeZone();
        var endOfDay = AvailabilityCalculator.ToUtc(AvailabilityCalculator.LocalDate(now, timeZone).AddDays(1), TimeOnly.MinValue, timeZone);

        var rows = await db.Bookings
            .FromSql($"""
                SELECT b.*, b.xmin FROM bookings b
                WHERE b.status = 'Seated'
                   OR (upper(b.period) > {now} AND lower(b.period) < {endOfDay})
                """)
            .AsNoTracking()
            .Where(b => b.Status == BookingStatus.Seated || _upcoming.Contains(b.Status))
            .Select(b => new
            {
                b.Id,
                b.Reference,
                b.Status,
                b.PartySize,
                b.Period,
                b.GuestId,
                TableIds = b.Tables.Where(t => t.Active).Select(t => t.TableId).ToList(),
            })
            .ToListAsync(cancellationToken);

        var guestIds = rows.Where(r => r.GuestId is not null).Select(r => r.GuestId!.Value).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking()
            .Where(g => guestIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Name, g.AllergyNotes })
            .ToDictionaryAsync(g => g.Id, cancellationToken);

        var bookings = rows.Select(r =>
        {
            var guest = r.GuestId is { } id && guests.TryGetValue(id, out var g) ? g : null;
            var summary = new TableBookingSummary(
                r.Id.Value,
                r.Reference.Value,
                r.Status,
                r.PartySize.Value,
                r.Period.Start.UtcDateTime,
                r.Period.End.UtcDateTime,
                guest?.Name.Value,
                !string.IsNullOrWhiteSpace(guest?.AllergyNotes));
            return (r.TableIds, View: new TableBookingView(r.Status, r.Period), Summary: summary);
        }).ToList();

        var areas = venue.Areas.OrderBy(a => a.SortOrder).ToList();
        var tables = areas
            .SelectMany(area => area.Tables.Select(table => (area, table)))
            .Select(x =>
            {
                var onTable = bookings.Where(b => b.TableIds.Contains(x.table.Id)).ToList();
                var state = TableStateRule.Derive(onTable.Select(b => b.View), now, endOfDay);
                var current = onTable.Where(b => b.Summary.Status == BookingStatus.Seated)
                    .OrderBy(b => b.Summary.StartsAt).Select(b => b.Summary).FirstOrDefault();
                var next = onTable
                    .Where(b => _upcoming.Contains(b.Summary.Status) && b.View.Period.End > now && b.View.Period.Start < endOfDay)
                    .OrderBy(b => b.Summary.StartsAt).Select(b => b.Summary).FirstOrDefault();
                var p = x.table.Position;
                return new FloorTableDto(
                    x.table.Id.Value,
                    x.area.Id.Value,
                    x.table.Name,
                    x.table.Seats,
                    p.Shape,
                    new TablePositionDto(p.X, p.Y, p.Width, p.Height, p.Rotation),
                    state,
                    current,
                    next);
            })
            .ToList();

        return new FloorPlanResponse(
            venue.Id.Value,
            venue.TimeZone,
            now.UtcDateTime,
            [.. areas.Select(a => new FloorAreaDto(a.Id.Value, a.Name, a.SortOrder))],
            tables);
    }
}

/// <summary><c>GET /venues/{venueId}/floor-plan</c>: the host's floor plan at business-clock "now".</summary>
internal sealed class GetFloorPlanEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/venues/{venueId:guid}/floor-plan", HandleAsync)
            .WithName("getFloorPlan")
            .WithTags("FloorPlan")
            .RequireAuthorization(Policies.Host)
            .Produces<FloorPlanResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(Guid venueId, GetFloorPlanHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetFloorPlan(new VenueId(venueId)), cancellationToken);
        return result.ToHttp(TypedResults.Ok);
    }
}
