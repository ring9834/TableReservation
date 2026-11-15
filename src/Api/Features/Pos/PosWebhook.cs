using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Common;
using Reservation.Contracts.Pos;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;
using Reservation.Infrastructure.Pos;

namespace Reservation.Api.Features.Pos;

/// <summary>What an inbound POS message did; logged, and returned by the demo's "Bill paid".</summary>
internal enum PosInboundOutcome
{
    /// <summary>The bill completed a seated booking, was attached late, or was already on the booking (a repeated bill).</summary>
    Matched,

    /// <summary>No single booking fits; kept for a manager (§6.5 step 4).</summary>
    Unmatched,

    /// <summary>The same POS message ID arrived before; nothing changed.</summary>
    Duplicate,

    /// <summary>The venue in the message doesn't exist; nothing saved.</summary>
    UnknownVenue,
}

/// <summary>The result of one inbound message.</summary>
internal sealed record PosInboundResult(
    PosInboundOutcome Outcome, Guid? PosMessageId = null, Guid? BookingId = null, string? UnmatchedReason = null);

/// <summary>
/// §6.5 <c>CloseBillCommand</c>: matches an inbound <c>BillClosed</c> to a booking and records the outcome on its
/// <c>pos_messages</c> row. Runs inside <see cref="PosWebhookHandler"/>'s transaction; it saves nothing itself.
/// <list type="number">
/// <item>The <c>bookingId</c> the POS echoed: complete it if seated; attach the bill if a host cleared it without one
/// within the window; otherwise unmatched.</item>
/// <item>A booking seated at the table: <c>Complete(BillClosed, bill)</c>.</item>
/// <item>Exactly one booking at the table cleared by a host, without a bill, within <c>LateBillMatchMinutes</c> of
/// <c>closedAt</c>: <c>AttachLateBill(LateMatch)</c>.</item>
/// <item>None of these, or more than one candidate: <c>Unmatched</c> (<c>no_match</c>, <c>ambiguous</c>; a different
/// bill for a booking that has one: <c>second_bill</c>).</item>
/// </list>
/// A <c>posBillId</c> already recorded at the venue is a repeat: nothing changes, and the row is marked matched to the
/// booking that holds it. The rules themselves are <see cref="LateMatchRule"/>'s; times are business-clock times.
/// </summary>
internal sealed class CloseBillHandler(ReservationsDbContext db, TimeProvider clock)
{
    public async Task HandleAsync(PosMessage row, PosBillClosed message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(message);

        var venueId = row.VenueId;
        var venue = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleAsync(v => v.Id == venueId, cancellationToken);
        var tableId = new TableId(message.TableId);
        if (venue.Tables.All(t => t.Id != tableId))
        {
            row.MarkUnmatched(UnmatchedReasons.NoMatch);
            return;
        }

        var bill = new BookingBill(
            message.PosBillId,
            new Money(message.BillTotalCents, venue.Currency),
            message.TipCents is { } tip ? new Money(tip, venue.Currency) : null,
            message.ClosedAt,
            row.Id);

        // A bill ID already recorded at the venue is a repeat, wherever the booking that holds it is.
        var holder = await Bookings()
            .FirstOrDefaultAsync(b => b.VenueId == venueId && b.Bills.Any(x => x.PosBillId == bill.PosBillId), cancellationToken);
        if (holder is not null)
        {
            row.Match(holder.Id);
            return;
        }

        var window = venue.Settings.LateBillMatchMinutes;
        Booking? byId = null;
        if (message.BookingId is { } echoed)
        {
            var id = new BookingId(echoed);
            byId = await Bookings().SingleOrDefaultAsync(b => b.Id == id && b.VenueId == venueId, cancellationToken);
        }

        var seated = await Bookings()
            .Where(b => b.VenueId == venueId && b.Status == BookingStatus.Seated && b.Tables.Any(t => t.TableId == tableId && t.Active))
            .OrderBy(b => b.SeatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var from = bill.ClosedAt.AddMinutes(-window);
        var to = bill.ClosedAt.AddMinutes(window);
        var completed = await Bookings()
            .Where(b => b.VenueId == venueId && b.Status == BookingStatus.Completed
                && b.CompletedAt >= from && b.CompletedAt <= to
                && b.Tables.Any(t => t.TableId == tableId))
            .ToListAsync(cancellationToken);

        var now = clock.GetUtcNow();
        switch (LateMatchRule.Match(bill, byId, seated, completed, window))
        {
            case BillMatch.CompleteSeated(var booking):
                Check(booking.Complete(CompletionSource.BillClosed, bill, now));
                row.Match(booking.Id);
                break;
            case BillMatch.AttachLate(var booking):
                Check(booking.AttachLateBill(bill, BillMatchedBy.LateMatch, window, now));
                row.Match(booking.Id);
                break;
            case BillMatch.Duplicate(var booking):
                row.Match(booking.Id);
                break;
            case BillMatch.Unmatched(var reason):
                row.MarkUnmatched(reason);
                break;
        }
    }

    private IQueryable<Booking> Bookings() => db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]);

    /// <summary>The rule only picks a transition the booking allows, so a refusal here is a bug, not a business outcome.</summary>
    private static void Check(Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Booking refused the bill the late-match rule chose: {result.Error!.Code}.");
        }
    }
}

/// <summary>
/// Inbound POS messages as commands (Design §5.5, §6.5, §6.7). One transaction: the inbox row
/// <c>('pos-webhook', POS message ID)</c>, the venue's next <c>venue_sequence</c>, the inbound <c>pos_messages</c> row
/// (payload = the body as received), <see cref="CloseBillHandler"/>, commit. A repeat finds the inbox row and changes
/// nothing. An xmin conflict (a host cleared the table meanwhile) runs the message again once on the new state. Reads
/// ignore the venue filter (a webhook has no venue) and match the message's venue. Actor <c>pos</c>.
/// </summary>
internal sealed partial class PosWebhookHandler(
    ReservationsDbContext db,
    RequestContext requestContext,
    PosSequenceAllocator sequences,
    CloseBillHandler closeBill,
    TimeProvider clock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
    ILogger<PosWebhookHandler> logger)
{
    public const string Consumer = "pos-webhook";

    public async Task<PosInboundResult> HandleAsync(PosInbound inbound, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        var message = inbound.Bill;
        requestContext.Set(requestContext.CorrelationId, null, Actor.ForPos());
        var venueId = new VenueId(message.VenueId);
        if (!await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AnyAsync(v => v.Id == venueId, cancellationToken))
        {
            LogOutcome(logger, message.MessageId, PosInboundOutcome.UnknownVenue, null, null);
            return new PosInboundResult(PosInboundOutcome.UnknownVenue);
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var processedAt = infrastructureClock.GetUtcNow().ToUniversalTime();
            var messageId = message.MessageId.ToString();
            var inserted = await db.Database.ExecuteSqlAsync(
                $"INSERT INTO inbox (consumer, message_id, processed_at) VALUES ({Consumer}, {messageId}, {processedAt}) ON CONFLICT DO NOTHING",
                cancellationToken);
            if (inserted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                LogOutcome(logger, message.MessageId, PosInboundOutcome.Duplicate, null, null);
                return new PosInboundResult(PosInboundOutcome.Duplicate);
            }

            // Counter before any booking row: the same lock order as the dispatcher (§6.7).
            var sequence = await sequences.NextAsync(venueId, cancellationToken);
            var row = PosMessage.Inbound(
                PosMessageId.New(infrastructureClock.GetUtcNow()),
                venueId,
                PosMessageTypes.BillClosed,
                $"pos:{message.MessageId}",
                JsonDocument.Parse(inbound.Body),
                sequence,
                clock.GetUtcNow(),
                infrastructureClock.GetUtcNow());
            db.PosMessages.Add(row);
            await closeBill.HandleAsync(row, message, cancellationToken);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException) when (attempt == 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                continue;
            }

            await transaction.CommitAsync(cancellationToken);
            var outcome = row.Status == PosMessageStatus.Matched ? PosInboundOutcome.Matched : PosInboundOutcome.Unmatched;
            LogOutcome(logger, message.MessageId, outcome, row.BookingId?.Value, row.UnmatchedReason);
            return new PosInboundResult(outcome, row.Id.Value, row.BookingId?.Value, row.UnmatchedReason);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "POS message {PosSourceMessageId} (BillClosed): {Outcome} booking {BookingId} {UnmatchedReason}")]
    private static partial void LogOutcome(ILogger logger, Guid posSourceMessageId, PosInboundOutcome outcome, Guid? bookingId, string? unmatchedReason);
}

/// <summary>Why an inbound body was not processed.</summary>
internal enum PosWebhookRejection
{
    /// <summary>Missing, wrong or stale <c>Pos-Signature</c>, or no webhook secret configured.</summary>
    InvalidSignature,

    /// <summary>Signed, but not a message the adapter can read, or for a venue that doesn't exist.</summary>
    InvalidPayload,
}

/// <summary>What happened to one inbound body: either the handler's result or why it was rejected.</summary>
internal sealed record PosWebhookProcessing(PosInboundResult? Result, PosWebhookRejection? Rejection);

/// <summary>
/// The one path an inbound POS message takes (Design §5.5, §6.7): <see cref="IPosAdapter.ParseInbound"/> (signature and
/// format), then <see cref="PosWebhookHandler"/> (inbox, sequence, CloseBill, commit). Used by <c>POST /webhooks/pos</c>
/// and by the demo's "Bill paid", so neither can skip a step. The body is never logged.
/// </summary>
internal sealed partial class PosWebhookProcessor(
    IPosAdapter adapter,
    PosWebhookHandler handler,
    ILogger<PosWebhookProcessor> logger)
{
    public async Task<PosWebhookProcessing> ProcessAsync(string body, string? signature, CancellationToken cancellationToken)
    {
        var parsed = adapter.ParseInbound(body, signature);
        if (parsed.IsFailure)
        {
            var invalidPayload = parsed.Error!.Fields is not null;
            LogRejected(logger, invalidPayload ? MockPosAdapter.InvalidPayload : parsed.Error.Code);
            return new PosWebhookProcessing(null, invalidPayload ? PosWebhookRejection.InvalidPayload : PosWebhookRejection.InvalidSignature);
        }

        var result = await handler.HandleAsync(parsed.Value, cancellationToken);
        return result.Outcome == PosInboundOutcome.UnknownVenue
            ? new PosWebhookProcessing(null, PosWebhookRejection.InvalidPayload)
            : new PosWebhookProcessing(result, null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "POS webhook rejected: {Code}")]
    private static partial void LogRejected(ILogger logger, string code);
}

/// <summary>
/// <c>POST /webhooks/pos</c> (Design §5.5, §6.7, §14): public, not rate limited (the POS sends bursts and retries), not in
/// the OpenAPI document (it is for the POS; docs/api/pos-messages.md is its contract). The body is read up to
/// <see cref="MaxBodyBytes"/> (larger → 413) and checked against the <c>Pos-Signature</c> header (bad → 401, nothing
/// saved; unreadable → 400). 200 for a processed message, a repeat included. The body is never logged.
/// </summary>
internal sealed class PosWebhookEndpoint : IEndpoint
{
    public const int MaxBodyBytes = 64 * 1024;

    public void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/webhooks/pos", HandleAsync)
            .WithName("posWebhook")
            .AllowAnonymous()
            .DisableRateLimiting()
            .ExcludeFromDescription();

    private static async Task<IResult> HandleAsync(HttpRequest request, PosWebhookProcessor processor, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            return ApiProblems.Result(StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge);
        }

        var processed = await processor.ProcessAsync(body, request.Headers[PosWebhookSigner.Header].FirstOrDefault(), cancellationToken);
        return processed.Rejection switch
        {
            PosWebhookRejection.InvalidPayload => ApiProblems.Result(StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed),
            PosWebhookRejection.InvalidSignature => ApiProblems.Result(StatusCodes.Status401Unauthorized, ProblemCodes.InvalidSignature),
            _ => TypedResults.Ok(),
        };
    }

    /// <summary>The body as text, or <c>null</c> when it is larger than <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<string?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await request.Body.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read > MaxBodyBytes ? null : Encoding.UTF8.GetString(buffer, 0, read);
    }
}
