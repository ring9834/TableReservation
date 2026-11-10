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

/// <summary>A fixed venue, for tests and tools that work for one venue.</summary>
public sealed class FixedVenueContext(VenueId? venueId) : IVenueContext
{
    public VenueId? VenueId { get; } = venueId;
}

/// <summary>
/// The scoped venue context of a request (Design §14.1): the API sets it from the route's <c>venueId</c> after the
/// venue check has passed. It starts empty, so a scope that sets nothing sees no venue data; background jobs use
/// <c>IgnoreQueryFilters([ReservationsDbContext.VenueFilter])</c>.
/// </summary>
public sealed class VenueContext : IVenueContext
{
    public VenueId? VenueId { get; private set; }

    public void Set(VenueId? venueId) => VenueId = venueId;
}
