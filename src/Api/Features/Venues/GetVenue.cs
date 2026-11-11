using Microsoft.EntityFrameworkCore;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Venues;

/// <summary>What the guest booking page needs to know about the venue before it asks for availability (Design §18.4).</summary>
/// <param name="TimeZone">The venue's IANA time zone; every time on the page is shown in it (BR-11).</param>
/// <param name="Today">Today's date in venue time on the business clock; the first bookable date (AC1.1).</param>
/// <param name="SlotTimes">The booking times of the slot grid in venue time (BR-01), whether free or not.</param>
/// <param name="MaxOnlinePartySize">Larger parties are asked to call (BR-04).</param>
/// <param name="BookingWindowDays">The last bookable date is <c>today</c> plus this many days (AC1.1).</param>
/// <param name="ContactPhone">The number larger parties are asked to call; <c>null</c> when the venue has none set.</param>
internal sealed record VenueResponse(
    Guid VenueId,
    string Name,
    string TimeZone,
    DateOnly Today,
    IReadOnlyList<TimeOnly> SlotTimes,
    int MaxOnlinePartySize,
    int BookingWindowDays,
    string? ContactPhone);

internal sealed record GetVenue(VenueId VenueId);

/// <summary>Public venue details for the guest page. Settings are read, never copied (they are not booking terms).</summary>
internal sealed class GetVenueHandler(ReservationsDbContext db, TimeProvider clock) : IQueryHandler<GetVenue, VenueResponse>
{
    public async Task<Result<VenueResponse>> Handle(GetVenue query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var timeZone = venue.FindTimeZone();
        var today = AvailabilityCalculator.LocalDate(clock.GetUtcNow(), timeZone);
        var settings = venue.Settings;
        return new VenueResponse(
            venue.Id.Value,
            venue.Name,
            venue.TimeZone,
            today,
            AvailabilityCalculator.SlotsOn(settings, timeZone, today).Select(s => s.LocalTime).ToList(),
            settings.MaxOnlinePartySize,
            settings.BookingWindowDays,
            settings.ContactPhone);
    }
}

internal sealed class GetVenueEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/venues/{venueId:guid}", HandleAsync)
            .WithName("getVenue")
            .WithTags("Venues")
            .AllowAnonymous()
            .Produces<VenueResponse>()
            .ProducesApiProblem(StatusCodes.Status404NotFound, StatusCodes.Status429TooManyRequests);

    private static async Task<IResult> HandleAsync(Guid venueId, GetVenueHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(new GetVenue(new VenueId(venueId)), cancellationToken);
        return result.ToHttp(TypedResults.Ok);
    }
}
