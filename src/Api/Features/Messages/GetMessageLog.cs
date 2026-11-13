using Microsoft.EntityFrameworkCore;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Messages;

/// <summary>What a message is about.</summary>
internal enum MessageSubjectType
{
    Booking,
    WaitlistEntry,
}

/// <summary>A message as the presenter's phone panel shows it (Design §7, §7.2).</summary>
/// <param name="Body">The text as sent (or to be sent).</param>
/// <param name="MobileMasked">Country code and last 3 digits only.</param>
/// <param name="SentAt">When the provider accepted it (business clock).</param>
/// <param name="DeliveredAt">When delivery was reported (business clock).</param>
/// <param name="LastError">Why it failed (e.g. <c>expired</c>, <c>superseded</c>); <c>null</c> unless failed.</param>
/// <param name="SubjectId">The booking or waitlist entry the message is about.</param>
internal sealed record MessageLogItem(
    Guid Id,
    MessagePurpose Purpose,
    Language Language,
    string Body,
    string? MobileMasked,
    MessageStatus Status,
    DateTime? SentAt,
    DateTime? DeliveredAt,
    string? LastError,
    Guid SubjectId,
    MessageSubjectType SubjectType);

/// <param name="At">The business-clock instant the log was read at.</param>
/// <param name="Items">Today's messages (venue-local day, business clock), newest last, at most 100.</param>
internal sealed record MessageLogResponse(DateTime At, string TimeZone, IReadOnlyList<MessageLogItem> Items);

internal sealed record GetMessageLog(VenueId VenueId);

/// <summary>
/// §9.3 <c>GetMessageLog</c> (Design §7.2, §14): today's messages, created in the venue-local day of the business clock,
/// the latest <see cref="MaxItems"/> in the order they were created. Reads only.
/// </summary>
internal sealed class GetMessageLogHandler(ReservationsDbContext db, TimeProvider clock) : IQueryHandler<GetMessageLog, MessageLogResponse>
{
    public const int MaxItems = 100;

    public async Task<Result<MessageLogResponse>> Handle(GetMessageLog query, CancellationToken cancellationToken)
    {
        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var timeZone = venue.FindTimeZone();
        var day = AvailabilityCalculator.LocalDate(now, timeZone);
        var from = AvailabilityCalculator.ToUtc(day, TimeOnly.MinValue, timeZone);
        var to = AvailabilityCalculator.ToUtc(day.AddDays(1), TimeOnly.MinValue, timeZone);

        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.CreatedAt >= from && m.CreatedAt < to)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);
        messages.Reverse();

        var guestIds = messages.Select(m => m.GuestId).Distinct().ToList();
        var mobiles = await db.Guests.AsNoTracking()
            .Where(g => guestIds.Contains(g.Id))
            .Select(g => new { g.Id, g.Mobile })
            .ToDictionaryAsync(g => g.Id, g => g.Mobile, cancellationToken);

        var items = messages.Select(m => new MessageLogItem(
            m.Id.Value,
            m.Purpose,
            m.Language,
            m.Body,
            mobiles.TryGetValue(m.GuestId, out var mobile) ? mobile.Masked() : null,
            m.Status,
            m.SentAt?.UtcDateTime,
            m.DeliveredAt?.UtcDateTime,
            m.Status == MessageStatus.Failed ? m.LastError : null,
            m.SubjectId,
            m.Purpose == MessagePurpose.TableReady ? MessageSubjectType.WaitlistEntry : MessageSubjectType.Booking)).ToList();
        return new MessageLogResponse(now.UtcDateTime, venue.TimeZone, items);
    }
}

/// <summary><c>GET /venues/{venueId}/messages</c> (Presenter): the phone panel's message log.</summary>
internal sealed class GetMessageLogEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/venues/{venueId:guid}/messages", HandleAsync)
            .WithName("getMessages")
            .WithTags("Messages")
            .RequireAuthorization(Policies.Presenter)
            .Produces<MessageLogResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleAsync(Guid venueId, GetMessageLogHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetMessageLog(new VenueId(venueId)), cancellationToken)).ToHttp(TypedResults.Ok);
}
