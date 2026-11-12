using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Pipeline;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Seating;

internal sealed record TableSuggestionQuery([property: FromQuery(Name = "partySize")] int? PartySize);

/// <summary>A table offered for a walk-in.</summary>
internal sealed record SuggestedTableDto(Guid Id, string Name, int Seats);

/// <param name="TurnMinutes">How long the walk-in would hold the table (BR-02).</param>
/// <param name="Table">The smallest free table that fits now (AC3.1, BR-05); <c>null</c> when none fits, so offer the waitlist (AC3.4).</param>
/// <param name="Alternatives">The other free tables that fit, smallest first (AC3.2).</param>
internal sealed record TableSuggestionResponse(int PartySize, int TurnMinutes, SuggestedTableDto? Table, IReadOnlyList<SuggestedTableDto> Alternatives);

internal sealed record GetTableSuggestion(VenueId VenueId, PartySize PartySize);

internal sealed class TableSuggestionQueryValidator : AbstractValidator<TableSuggestionQuery>
{
    public TableSuggestionQueryValidator()
    {
        RuleFor(x => x.PartySize).NotNull().WithErrorCode("party_size_required")
            .InclusiveBetween(PartySize.Min, PartySize.Max).WithErrorCode("party_size_out_of_range");
    }
}

/// <summary>§9.3 <c>GetSuggestedTable</c>: free tables for a walk-in at business-clock now (Design §11.5).</summary>
internal sealed class GetTableSuggestionHandler(ReservationsDbContext db, AvailabilityReader reader, TimeProvider clock)
    : IQueryHandler<GetTableSuggestion, TableSuggestionResponse>
{
    public async Task<Result<TableSuggestionResponse>> Handle(GetTableSuggestion query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var floor = await reader.LoadNowAsync(venue, clock.GetUtcNow(), cancellationToken);
        var free = SeatingRules.FreeTablesNow(floor, query.PartySize)
            .Select(t => new SuggestedTableDto(t.Id.Value, t.Name, t.Seats))
            .ToList();
        return new TableSuggestionResponse(
            query.PartySize.Value,
            venue.Settings.TurnMinutesFor(query.PartySize),
            free.FirstOrDefault(),
            [.. free.Skip(1)]);
    }
}

/// <summary><c>GET /venues/{venueId}/tables/suggestion?partySize</c> (Host).</summary>
internal sealed class GetTableSuggestionEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/venues/{venueId:guid}/tables/suggestion", HandleAsync)
            .WithName("getTableSuggestion")
            .WithTags("Seating")
            .RequireAuthorization(Policies.Host)
            .WithValidation<TableSuggestionQuery>()
            .Produces<TableSuggestionResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(
        Guid venueId, [AsParameters] TableSuggestionQuery query, GetTableSuggestionHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetTableSuggestion(new VenueId(venueId), PartySize.Of(query.PartySize!.Value)), cancellationToken))
            .ToHttp(TypedResults.Ok);
}
