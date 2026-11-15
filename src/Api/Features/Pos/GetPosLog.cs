using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;
using Reservation.Infrastructure.Pos;

namespace Reservation.Api.Features.Pos;

/// <summary>A POS message as the presenter's POS log shows it (AC6.1, AC6.2, Design §6.7).</summary>
/// <param name="VenueSequence">Order within the venue, both directions; outbound messages are sent in this order.</param>
/// <param name="Attempts">Sends the POS answered (accepted or a temporary error); waiting for an unavailable POS doesn't count.</param>
/// <param name="CreatedAt">When the message was created (business clock).</param>
/// <param name="DeliveredAt">When the POS accepted an outbound message (business clock).</param>
/// <param name="BookingId">The booking the message is about; for an inbound bill, the booking it was matched to.</param>
/// <param name="TableIds">The tables in the message's envelope.</param>
/// <param name="TableNames">
/// The names of <c>tableIds</c> in the same order (e.g. <c>T4</c>), so the POS log needs no floor plan (M9b); <c>?</c> for a
/// table the venue doesn't have.
/// </param>
/// <param name="UnmatchedReason">Inbound only: <c>no_match</c>, <c>ambiguous</c> or <c>second_bill</c>.</param>
/// <param name="Payload">The full message JSON (AC6.2), with phone numbers masked; stored unmasked for the POS.</param>
internal sealed record PosLogItem(
    Guid Id,
    PosDirection Direction,
    string MessageType,
    PosMessageStatus Status,
    long VenueSequence,
    int Attempts,
    DateTime CreatedAt,
    DateTime? DeliveredAt,
    Guid? BookingId,
    IReadOnlyList<Guid> TableIds,
    IReadOnlyList<string> TableNames,
    string? UnmatchedReason,
    JsonElement Payload);

/// <param name="Items">Newest first (highest <c>venueSequence</c>), at most 200.</param>
internal sealed record PosLogResponse(string TimeZone, IReadOnlyList<PosLogItem> Items);

internal sealed record GetPosLog(VenueId VenueId, string? Status);

/// <summary>
/// §9.3 <c>GetPosLog</c> (AC6.1, AC6.2, Design §6.7): the venue's POS messages in both directions, newest first by
/// <c>venue_sequence</c> (the fixed demo clock gives many the same <c>created_at</c>), at most <see cref="MaxItems"/>,
/// optionally one status. Phone numbers in the payload are masked as on the phone panel. Reads only.
/// </summary>
internal sealed class GetPosLogHandler(ReservationsDbContext db) : IQueryHandler<GetPosLog, PosLogResponse>
{
    public const int MaxItems = 200;

    /// <summary>Payload fields that hold a phone number.</summary>
    private static readonly string[] _phoneFields = ["memberPhone"];

    public async Task<Result<PosLogResponse>> Handle(GetPosLog query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        PosMessageStatus? status = null;
        if (query.Status is { } text)
        {
            if (!Enum.TryParse<PosMessageStatus>(text, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed) || int.TryParse(text, out _))
            {
                return Error.Validation("status", "status_invalid");
            }

            status = parsed;
        }

        var venue = await db.Venues.AsNoTracking().SingleOrDefaultAsync(v => v.Id == query.VenueId, cancellationToken);
        if (venue is null)
        {
            return Error.NotFound();
        }

        var rows = db.PosMessages.AsNoTracking();
        if (status is { } wanted)
        {
            rows = rows.Where(m => m.Status == wanted);
        }

        var messages = await rows.OrderByDescending(m => m.VenueSequence).Take(MaxItems).ToListAsync(cancellationToken);
        var tableNames = venue.Tables.ToDictionary(t => t.Id.Value, t => t.Name);
        var items = messages.Select(m =>
        {
            var tableIds = TableIds(m.Payload);
            return new PosLogItem(
                m.Id.Value,
                m.Direction,
                m.MessageType,
                m.Status,
                m.VenueSequence,
                m.Attempts,
                m.CreatedAt.UtcDateTime,
                m.DeliveredAt?.UtcDateTime,
                m.BookingId?.Value,
                tableIds,
                [.. tableIds.Select(id => tableNames.GetValueOrDefault(id, "?"))],
                m.UnmatchedReason,
                MaskedPayload(m.Payload));
        }).ToList();
        return new PosLogResponse(venue.TimeZone, items);
    }

    /// <summary>The payload with every phone field masked like <c>PhoneNumber.Masked()</c>.</summary>
    public static JsonElement MaskedPayload(JsonDocument payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var node = JsonNode.Parse(payload.RootElement.GetRawText());
        if (node is JsonObject root)
        {
            foreach (var field in _phoneFields)
            {
                if (root[field] is JsonValue value && value.TryGetValue<string>(out var phone))
                {
                    root[field] = PhoneNumber.Mask(phone);
                }
            }
        }

        return JsonSerializer.SerializeToElement(node);
    }

    private static List<Guid> TableIds(JsonDocument payload) =>
        payload.RootElement.ValueKind == JsonValueKind.Object
        && payload.RootElement.TryGetProperty("tableIds", out var tables) && tables.ValueKind == JsonValueKind.Array
            ? [.. tables.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String && t.TryGetGuid(out _)).Select(t => t.GetGuid())]
            : [];
}

/// <param name="Online">Whether the demo's mock POS accepts messages.</param>
internal sealed record PosStatusResponse(bool Online);

/// <summary>
/// <c>GET /venues/{venueId}/pos-messages?status</c> (Presenter): the POS log. <c>GET /venues/{venueId}/pos/status</c>
/// (Presenter): the mock POS's online switch, mapped only when <c>Demo:Enabled</c> (there is one mock POS for the
/// installation, Design §6.7).
/// </summary>
internal sealed class GetPosLogEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/venues/{venueId:guid}/pos-messages", HandleAsync)
            .WithName("getPosMessages")
            .WithTags("Pos")
            .RequireAuthorization(Policies.Presenter)
            .Produces<PosLogResponse>()
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);

        if (!api.ServiceProvider.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        {
            return;
        }

        api.MapGet("/venues/{venueId:guid}/pos/status", StatusAsync)
            .WithName("getPosStatus")
            .WithTags("Pos")
            .RequireAuthorization(Policies.Presenter)
            .Produces<PosStatusResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);
    }

    /// <param name="status">One status only (<c>waiting</c>, <c>sent</c>, <c>failed</c>, <c>matched</c>, <c>unmatched</c>, …).</param>
    private static async Task<IResult> HandleAsync(Guid venueId, string? status, GetPosLogHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new GetPosLog(new VenueId(venueId), status), cancellationToken)).ToHttp(TypedResults.Ok);

    private static async Task<IResult> StatusAsync(Guid venueId, MockPosAdapter mock, CancellationToken cancellationToken) =>
        TypedResults.Ok(new PosStatusResponse(await mock.IsOnlineAsync(cancellationToken)));
}
