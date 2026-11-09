using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Persistence;

/// <summary>
/// The venue the current request or job works for. Drives the global venue query filter (ADR-003 DM-01).
/// <c>null</c> means no venue: filtered queries return nothing.
/// </summary>
public interface IVenueContext
{
    VenueId? VenueId { get; }
}

/// <summary>A fixed venue: the demo session is always Golden Dragon (Design §17); also used by tests.</summary>
public sealed class FixedVenueContext(VenueId? venueId) : IVenueContext
{
    public VenueId? VenueId { get; } = venueId;
}
