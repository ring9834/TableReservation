using System.Text.Json;
using Reservation.Contracts.Pos;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Pos;

/// <summary>
/// The single seam to the POS (Design §6.1): everything POS-specific lives behind it. The demo uses
/// <see cref="MockPosAdapter"/>; the pilot adds the real adapter, and only that class changes.
/// </summary>
public interface IPosAdapter
{
    /// <summary>
    /// Sends one outbound message with its idempotency key. The POS must ignore a key it has already accepted, so a
    /// resend after a crash is delivered once.
    /// </summary>
    Task<PosSendResult> SendAsync(PosOutbound message, CancellationToken cancellationToken);

    /// <summary>
    /// Checks an inbound webhook's signature and reads it. Takes the raw body and signature header rather than the
    /// HTTP request, so Infrastructure doesn't depend on ASP.NET Core (Design §6.7).
    /// </summary>
    Result<PosInbound> ParseInbound(string body, string? signatureHeader);
}

/// <summary>What the worker sends: the stored payload and the idempotency key that goes with it.</summary>
public sealed record PosOutbound(PosMessageId Id, VenueId VenueId, string MessageType, string IdempotencyKey, BookingId? BookingId, JsonDocument Payload);

public enum PosSendOutcome
{
    /// <summary>Accepted (or a repeat the POS ignored).</summary>
    Accepted,

    /// <summary>The POS can't be reached; nothing was accepted. Retried without counting an attempt.</summary>
    Unavailable,

    /// <summary>A temporary error; retried with backoff.</summary>
    Temporary,

    /// <summary>The POS refused the message for good; the venue's queue stops behind it.</summary>
    Rejected,
}

/// <summary>The POS's answer to one send. <see cref="Error"/> is a code, never a payload.</summary>
public sealed record PosSendResult(PosSendOutcome Outcome, string? Error = null, bool Duplicate = false)
{
    public static PosSendResult Accepted(bool duplicate = false) => new(PosSendOutcome.Accepted, null, duplicate);

    public static PosSendResult Unavailable() => new(PosSendOutcome.Unavailable, PosMessage.Unavailable);

    public static PosSendResult Temporary(string error) => new(PosSendOutcome.Temporary, error);

    public static PosSendResult Rejected(string error) => new(PosSendOutcome.Rejected, error);
}

/// <summary>An inbound message whose signature checked out: the bill and the body as received (stored as the payload).</summary>
public sealed record PosInbound(PosBillClosed Bill, string Body);
