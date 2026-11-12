using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

internal sealed record TableRef(Guid Id, string Name);

/// <param name="MobileMasked">Country code and last 3 digits only.</param>
/// <param name="Version">Row version; send it back with confirm or cancel.</param>
internal sealed record BookingListItem(
    Guid Id,
    string Reference,
    BookingStatus Status,
    BookingChannel Channel,
    int PartySize,
    DateTime StartsAt,
    DateTime EndsAt,
    IReadOnlyList<TableRef> Tables,
    string? GuestName,
    string? MobileMasked,
    bool HasAllergy,
    string? AllergyNotes,
    string? Occasion,
    string? Notes,
    MoneyDto? Deposit,
    DepositStatus? DepositStatus,
    uint Version);

/// <param name="Day">The venue-local service day.</param>
/// <param name="Items">Bookings starting that day, in time order, every status. One day is not paged.</param>
internal sealed record BookingListResponse(DateOnly Day, string TimeZone, IReadOnlyList<BookingListItem> Items);

internal sealed record GetTonightBookings(VenueId VenueId, DateOnly? Day);

/// <summary>§9.3 <c>GetTonightBookings</c>: reads with <c>AsNoTracking</c> projections, never loads aggregates.</summary>
internal sealed class GetTonightBookingsHandler(ReservationsDbContext db, TimeProvider clock)
    : IQueryHandler<GetTonightBookings, BookingListResponse>
{
    public async Task<Result<BookingListResponse>> Handle(GetTonightBookings query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var timeZone = venue.FindTimeZone();
        var day = query.Day ?? AvailabilityCalculator.LocalDate(clock.GetUtcNow(), timeZone);
        var from = AvailabilityCalculator.ToUtc(day, TimeOnly.MinValue, timeZone);
        var to = AvailabilityCalculator.ToUtc(day.AddDays(1), TimeOnly.MinValue, timeZone);

        var rows = await db.Bookings
            .FromSql($"SELECT b.*, b.xmin FROM bookings b WHERE lower(b.period) >= {from} AND lower(b.period) < {to}")
            .AsNoTracking()
            .Select(b => new
            {
                b.Id,
                b.Reference,
                b.Status,
                b.Channel,
                b.PartySize,
                b.Period,
                b.GuestId,
                b.ContactName,
                b.Occasion,
                b.Notes,
                DepositAmount = b.Deposit == null ? (Money?)null : b.Deposit.Amount,
                DepositStatus = b.Deposit == null ? (DepositStatus?)null : b.Deposit.Status,
                TableIds = b.Tables.Select(t => t.TableId).ToList(),
                b.Version,
            })
            .ToListAsync(cancellationToken);

        var guestIds = rows.Where(r => r.GuestId is not null).Select(r => r.GuestId!.Value).Distinct().ToList();
        var guests = await db.Guests.AsNoTracking()
            .Where(g => guestIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Mobile, g.AllergyNotes })
            .ToDictionaryAsync(g => g.Id, cancellationToken);
        var tableNames = venue.Tables.ToDictionary(t => t.Id, t => t.Name);

        var items = rows
            .OrderBy(r => r.Period.Start)
            .ThenBy(r => r.Reference.Value, StringComparer.Ordinal)
            .Select(r =>
            {
                var guest = r.GuestId is { } id && guests.TryGetValue(id, out var g) ? g : null;
                return new BookingListItem(
                    r.Id.Value,
                    r.Reference.Value,
                    r.Status,
                    r.Channel,
                    r.PartySize.Value,
                    r.Period.Start.UtcDateTime,
                    r.Period.End.UtcDateTime,
                    r.TableIds.Select(t => new TableRef(t.Value, tableNames.GetValueOrDefault(t, "?"))).ToList(),
                    r.ContactName?.Value,
                    guest?.Mobile.Masked(),
                    !string.IsNullOrWhiteSpace(guest?.AllergyNotes),
                    guest?.AllergyNotes,
                    r.Occasion,
                    r.Notes,
                    r.DepositAmount is { } amount ? MoneyDto.From(amount) : null,
                    r.DepositStatus,
                    r.Version);
            })
            .ToList();
        return new BookingListResponse(day, venue.TimeZone, items);
    }
}

/// <summary><c>GET /venues/{venueId}/bookings?day</c>: the host's list for a service day (today by default).</summary>
internal sealed class GetTonightBookingsEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/venues/{venueId:guid}/bookings", HandleAsync)
            .WithName("getTonightBookings")
            .WithTags("Bookings")
            .RequireAuthorization(Policies.Host)
            .Produces<BookingListResponse>()
            .ProducesApiProblem(StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(Guid venueId, DateOnly? day, GetTonightBookingsHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetTonightBookings(new VenueId(venueId), day), cancellationToken);
        return result.ToHttp(TypedResults.Ok);
    }
}
