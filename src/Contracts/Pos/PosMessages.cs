using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reservation.Contracts.Pos;

/// <summary>The four POS message types of the demo (Design §6.3, docs/api/pos-messages.md).</summary>
public static class PosMessageTypes
{
    public const string UpsertMember = "UpsertMember";
    public const string OpenTable = "OpenTable";
    public const string ApplyDeposit = "ApplyDeposit";
    public const string BillClosed = "BillClosed";
}

/// <summary>
/// The envelope every POS message carries, in both directions (AC6.3, Design §6.7): flat JSON, camelCase, times in UTC.
/// Version 1. A breaking change raises <see cref="Version"/>; new optional fields don't.
/// </summary>
public abstract record PosEnvelope
{
    /// <summary>The envelope version this record describes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Envelope version; 1.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Unique per message; the receiver ignores a message ID it has already processed.</summary>
    public required Guid MessageId { get; init; }

    /// <summary><c>UpsertMember</c>, <c>OpenTable</c>, <c>ApplyDeposit</c> or <c>BillClosed</c>.</summary>
    public required string MessageType { get; init; }

    /// <summary>The venue.</summary>
    public required Guid VenueId { get; init; }

    /// <summary>The booking the message is about. On <c>BillClosed</c>, the booking ID the POS received in <c>OpenTable</c>, if it echoes it.</summary>
    public Guid? BookingId { get; init; }

    /// <summary>The tables concerned; empty for <c>UpsertMember</c>.</summary>
    public required IReadOnlyList<Guid> TableIds { get; init; }

    /// <summary>When the change happened (UTC, the venue's business time).</summary>
    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>To the POS, on a new booking with a guest: create or update the member, matched by phone.</summary>
public sealed record PosUpsertMember : PosEnvelope
{
    /// <summary>Mobile number in E.164 (<c>+6421…</c>): the member's key.</summary>
    public required string MemberPhone { get; init; }

    /// <summary>The guest's name as stored.</summary>
    public required string Name { get; init; }

    /// <summary>The guest's language: <c>en</c> or <c>zh</c>.</summary>
    public required string Language { get; init; }
}

/// <summary>To the POS, when a party is seated: open the table(s) for this booking.</summary>
public sealed record PosOpenTable : PosEnvelope
{
    /// <summary>Number of guests.</summary>
    public required int PartySize { get; init; }

    /// <summary>The name given with the booking; absent for a walk-in without one.</summary>
    public string? GuestName { get; init; }

    /// <summary>When the party was seated (UTC).</summary>
    public required DateTimeOffset SeatedAt { get; init; }
}

/// <summary>To the POS, after <c>OpenTable</c> when a deposit was paid: credit it to the table's bill.</summary>
public sealed record PosApplyDeposit : PosEnvelope
{
    /// <summary>Deposit in cents (GST inclusive).</summary>
    public required long AmountCents { get; init; }

    /// <summary>ISO 4217, <c>NZD</c>.</summary>
    public required string Currency { get; init; }

    /// <summary>The card payment's reference at the payment provider.</summary>
    public required string PaymentRef { get; init; }
}

/// <summary>From the POS, when a bill is paid and closed.</summary>
public sealed record PosBillClosed : PosEnvelope
{
    /// <summary>The POS's own bill ID; unique per venue.</summary>
    public required string PosBillId { get; init; }

    /// <summary>The table the bill belongs to.</summary>
    public required Guid TableId { get; init; }

    /// <summary>The bill total in cents (GST inclusive), not the sum of payments.</summary>
    public required long BillTotalCents { get; init; }

    /// <summary>Tip in cents, if any.</summary>
    public long? TipCents { get; init; }

    /// <summary>When the bill was closed (UTC).</summary>
    public required DateTimeOffset ClosedAt { get; init; }
}

/// <summary>JSON settings for POS messages: camelCase, nulls left out, nothing else special.</summary>
public static class PosJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
