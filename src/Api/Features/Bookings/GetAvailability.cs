using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Pipeline;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings;

/// <param name="Date">Venue-local date, today to the booking window (AC1.1).</param>
/// <param name="PartySize">1–20; online bookings are limited further by the venue (BR-04).</param>
/// <param name="Time">Optional venue-local time; when it is full, the 3 nearest times are returned (AC1.3).</param>
internal sealed record AvailabilityQuery(
    [property: FromQuery(Name = "date")] DateOnly? Date,
    [property: FromQuery(Name = "partySize")] int? PartySize,
    [property: FromQuery(Name = "time")] TimeOnly? Time);

/// <param name="StartsAt">UTC.</param>
/// <param name="LocalTime">Venue-local time, for display.</param>
internal sealed record AvailableSlot(DateTime StartsAt, TimeOnly LocalTime);

/// <param name="RequestedTimeAvailable"><c>false</c> when the requested time is full and <c>slots</c> holds the nearest times instead.</param>
/// <param name="TimeZone">The venue's IANA time zone, for display.</param>
internal sealed record AvailabilityResponse(
    DateOnly Date,
    int PartySize,
    TimeOnly? RequestedTime,
    bool? RequestedTimeAvailable,
    string TimeZone,
    IReadOnlyList<AvailableSlot> Slots);

internal sealed record GetAvailability(VenueId VenueId, DateOnly Date, PartySize PartySize, TimeOnly? Time);

internal sealed class AvailabilityQueryValidator : AbstractValidator<AvailabilityQuery>
{
    public AvailabilityQueryValidator()
    {
        RuleFor(x => x.Date).NotNull().WithErrorCode("date_required");
        RuleFor(x => x.PartySize).NotNull().WithErrorCode("party_size_required")
            .InclusiveBetween(PartySize.Min, PartySize.Max).WithErrorCode("party_size_out_of_range");
    }
}

/// <summary>§9.3 <c>GetAvailability</c>, §11. Rules that need the venue (window, online party limit) are checked here.</summary>
internal sealed class GetAvailabilityHandler(ReservationsDbContext db, AvailabilityReader reader, TimeProvider clock)
    : IQueryHandler<GetAvailability, AvailabilityResponse>
{
    public async Task<Result<AvailabilityResponse>> Handle(GetAvailability query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow();
        var timeZone = venue.FindTimeZone();
        if (!venue.Settings.IsWithinBookingWindow(query.Date, AvailabilityCalculator.LocalDate(now, timeZone)))
        {
            return Error.Rule(ErrorCodes.OutsideBookingWindow);
        }

        if (query.PartySize.Value > venue.Settings.MaxOnlinePartySize)
        {
            return Error.Rule(ErrorCodes.PartyTooLarge);
        }

        var input = await reader.LoadAsync(venue, query.Date, now, cancellationToken);
        var search = AvailabilityCalculator.Search(input, query.Date, query.PartySize, query.Time);
        return new AvailabilityResponse(
            query.Date,
            query.PartySize.Value,
            query.Time,
            search.RequestedTimeAvailable,
            venue.TimeZone,
            search.Slots.Select(s => new AvailableSlot(s.StartsAt.UtcDateTime, s.LocalTime)).ToList());
    }
}

internal sealed class GetAvailabilityEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/venues/{venueId:guid}/availability", HandleAsync)
            .WithName("getAvailability")
            .WithTags("Bookings")
            .AllowAnonymous()
            .WithValidation<AvailabilityQuery>()
            .Produces<AvailabilityResponse>()
            .ProducesApiProblem(StatusCodes.Status404NotFound, StatusCodes.Status422UnprocessableEntity, StatusCodes.Status429TooManyRequests);

    private static async Task<IResult> HandleAsync(
        Guid venueId, [AsParameters] AvailabilityQuery query, GetAvailabilityHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.Handle(
            new GetAvailability(new VenueId(venueId), query.Date!.Value, Domain.Common.PartySize.Of(query.PartySize!.Value), query.Time),
            cancellationToken);
        return result.ToHttp(TypedResults.Ok);
    }
}
