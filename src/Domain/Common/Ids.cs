namespace Reservation.Domain.Common;

/// <summary>A strongly typed ID wrapping a UUID v7 (Design §1, ADR-003 DM-02).</summary>
public interface IEntityId
{
    Guid Value { get; }
}

public readonly record struct VenueId(Guid Value) : IEntityId
{
    public static VenueId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct AreaId(Guid Value) : IEntityId
{
    public static AreaId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct TableId(Guid Value) : IEntityId
{
    public static TableId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct GuestId(Guid Value) : IEntityId
{
    public static GuestId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct BookingId(Guid Value) : IEntityId
{
    public static BookingId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct WaitlistEntryId(Guid Value) : IEntityId
{
    public static WaitlistEntryId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct MessageId(Guid Value) : IEntityId
{
    public static MessageId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}

public readonly record struct PosMessageId(Guid Value) : IEntityId
{
    public static PosMessageId New(DateTimeOffset now) => new(Guid.CreateVersion7(now));
}
