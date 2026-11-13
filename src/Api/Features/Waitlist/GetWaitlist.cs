using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Features.Seating;
using Reservation.Api.Pipeline;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Waitlist;

/// <summary>An active waitlist entry, for the host screens only.</summary>
/// <param name="Name">The name given when the party joined (its contact name).</param>
/// <param name="MobileMasked">Country code and last 3 digits only.</param>
/// <param name="QuotedWaitMinutes">The wait quoted when the party joined (AC3.5); kept as quoted.</param>
/// <param name="WaitedMinutes">Whole minutes since joining, at <c>at</c> (AC3.6).</param>
/// <param name="Overdue">Notified more than the venue's timeout (10 minutes) ago and not seated (AC4.6, BR-10).</param>
/// <param name="Version">Row version; send it back to notify, seat or end the entry.</param>
/// <param name="TableReadyStatus">The status of the latest "table ready" text to this entry; <c>null</c> before any (M7a, AC4.4).</param>
/// <param name="TableReadySentAt">When that text was sent (business clock).</param>
/// <param name="TableReadyFailed">That text failed: the host should call the guest (Design §7.1 Visibility).</param>
internal sealed record WaitlistItem(
    Guid Id,
    string Name,
    string? MobileMasked,
    int PartySize,
    int QuotedWaitMinutes,
    DateTime JoinedAt,
    int WaitedMinutes,
    WaitlistStatus Status,
    DateTime? NotifiedAt,
    Guid? NotifiedTableId,
    bool Overdue,
    uint Version,
    MessageStatus? TableReadyStatus,
    DateTime? TableReadySentAt,
    bool TableReadyFailed);

/// <param name="At">The business-clock instant the list was read at.</param>
/// <param name="Items">Active entries (waiting or notified) in the order they joined (AC3.6).</param>
/// <param name="SuggestedEntryId">With <c>tableId</c>: the first party that fits that table now (BR-09, AC4.2).</param>
internal sealed record WaitlistResponse(DateTime At, string TimeZone, IReadOnlyList<WaitlistItem> Items, Guid? SuggestedEntryId);

internal sealed record WaitlistQuery([property: FromQuery(Name = "tableId")] Guid? TableId);

internal sealed record WaitlistQuoteQuery([property: FromQuery(Name = "partySize")] int? PartySize);

/// <param name="QuotedWaitMinutes">BR-08: minutes until the earliest fitting table is expected to free, rounded up to 5.</param>
internal sealed record WaitlistQuoteResponse(int PartySize, int QuotedWaitMinutes);

internal sealed record GetWaitlist(VenueId VenueId, TableId? TableId);

internal sealed record GetQuotedWait(VenueId VenueId, PartySize PartySize);

internal sealed class WaitlistQuoteQueryValidator : AbstractValidator<WaitlistQuoteQuery>
{
    public WaitlistQuoteQueryValidator()
    {
        RuleFor(x => x.PartySize).NotNull().WithErrorCode("party_size_required")
            .InclusiveBetween(PartySize.Min, PartySize.Max).WithErrorCode("party_size_out_of_range");
    }
}

/// <summary>
/// §9.3 <c>GetWaitlist</c> and <c>GetQuotedWait</c> (Design §11.3, §11.4, §14.2). Reads with <c>AsNoTracking</c>
/// projections at business-clock now; never changes data.
/// </summary>
internal sealed class GetWaitlistHandler(ReservationsDbContext db, AvailabilityReader reader, TimeProvider clock)
    : IQueryHandler<GetWaitlist, WaitlistResponse>, IQueryHandler<GetQuotedWait, WaitlistQuoteResponse>
{
    private static readonly WaitlistStatus[] _active = [WaitlistStatus.Waiting, WaitlistStatus.Notified];

    public async Task<Result<WaitlistResponse>> Handle(GetWaitlist query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var table = query.TableId is { } tableId ? venue.FindTable(tableId) : null;
        if (query.TableId is not null && table is null)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var entries = await db.WaitlistEntries.AsNoTracking()
            .Where(w => _active.Contains(w.Status))
            .OrderBy(w => w.JoinedAt).ThenBy(w => w.Id)
            .ToListAsync(cancellationToken);
        var guestIds = entries.Select(e => e.GuestId).Distinct().ToList();
        var mobiles = await db.Guests.AsNoTracking()
            .Where(g => guestIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Mobile })
            .ToDictionaryAsync(g => g.Id, g => g.Mobile, cancellationToken);

        // The latest "table ready" text per entry (a re-notify queues a new one, Design §7.2).
        var entryIds = entries.Select(e => e.Id.Value).ToList();
        var tableReady = (await db.Messages.AsNoTracking()
                .Where(m => m.Purpose == MessagePurpose.TableReady && entryIds.Contains(m.SubjectId))
                .Select(m => new { m.SubjectId, m.Id, m.CreatedAt, m.Status, m.SentAt })
                .ToListAsync(cancellationToken))
            .GroupBy(m => m.SubjectId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id.Value).First());

        Guid? suggested = null;
        if (table is not null)
        {
            var floor = await reader.LoadNowAsync(venue, now, cancellationToken);
            var waiting = entries.Select(e => new WaitingParty(e.Id, e.PartySize.Value, e.JoinedAt));
            suggested = SeatingRules.SuggestEntry(floor, new TableInfo(table.Id, table.Name, table.Seats), waiting)?.Value;
        }

        var timeout = venue.Settings.NotifiedTimeoutMinutes;
        var items = entries.Select(e =>
        {
            var text = tableReady.GetValueOrDefault(e.Id.Value);
            return new WaitlistItem(
                e.Id.Value,
                e.ContactName.Value,
                mobiles.TryGetValue(e.GuestId, out var mobile) ? mobile.Masked() : null,
                e.PartySize.Value,
                e.QuotedWaitMinutes,
                e.JoinedAt.UtcDateTime,
                Math.Max(0, (int)(now - e.JoinedAt).TotalMinutes),
                e.Status,
                e.NotifiedAt?.UtcDateTime,
                e.NotifiedTableId?.Value,
                e.IsOverdue(now, timeout),
                e.Version,
                text?.Status,
                text?.SentAt?.UtcDateTime,
                text?.Status == MessageStatus.Failed);
        }).ToList();
        return new WaitlistResponse(now.UtcDateTime, venue.TimeZone, items, suggested);
    }

    public async Task<Result<WaitlistQuoteResponse>> Handle(GetQuotedWait query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var floor = await reader.LoadNowAsync(venue, clock.GetUtcNow(), cancellationToken);
        var minutes = SeatingRules.QuotedWait(floor, query.PartySize);
        return minutes is { } quoted
            ? new WaitlistQuoteResponse(query.PartySize.Value, quoted)
            : Error.Rule(ErrorCodes.PartyTooLarge);
    }
}

/// <summary><c>GET /venues/{venueId}/waitlist[?tableId]</c> and <c>GET /venues/{venueId}/waitlist/quote?partySize</c> (Host).</summary>
internal sealed class GetWaitlistEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/venues/{venueId:guid}/waitlist", GetAsync)
            .WithName("getWaitlist")
            .WithTags("Waitlist")
            .RequireAuthorization(Policies.Host)
            .Produces<WaitlistResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);

        api.MapGet("/venues/{venueId:guid}/waitlist/quote", QuoteAsync)
            .WithName("getQuotedWait")
            .WithTags("Waitlist")
            .RequireAuthorization(Policies.Host)
            .WithValidation<WaitlistQuoteQuery>()
            .Produces<WaitlistQuoteResponse>()
            .ProducesApiProblem(
                StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound,
                StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(
        Guid venueId, [AsParameters] WaitlistQuery query, GetWaitlistHandler handler, CancellationToken cancellationToken)
    {
        var tableId = query.TableId is { } id ? new TableId(id) : (TableId?)null;
        return (await handler.Handle(new GetWaitlist(new VenueId(venueId), tableId), cancellationToken)).ToHttp(TypedResults.Ok);
    }

    private static async Task<IResult> QuoteAsync(
        Guid venueId, [AsParameters] WaitlistQuoteQuery query, GetWaitlistHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetQuotedWait(new VenueId(venueId), PartySize.Of(query.PartySize!.Value)), cancellationToken))
            .ToHttp(TypedResults.Ok);
}
