using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Domain.Messaging;
using Reservation.Domain.Venues;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Audit;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence.Converters;
using Reservation.Infrastructure.Pos;

namespace Reservation.Infrastructure.Persistence;

/// <summary>
/// The EF Core context (Design §5.1, §13). snake_case names, enums as text (DM-06), UTC times (DM-04),
/// xmin row versions (DM-07) and a global venue filter (DM-01).
/// </summary>
public sealed class ReservationsDbContext(DbContextOptions<ReservationsDbContext> options, IVenueContext venueContext)
    : DbContext(options)
{
    /// <summary>Name of the global venue query filter, for <c>IgnoreQueryFilters([VenueFilter])</c>.</summary>
    public const string VenueFilter = "Venue";

    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<Guest> Guests => Set<Guest>();

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<InboxEntry> Inbox => Set<InboxEntry>();

    public DbSet<PosMessage> PosMessages => Set<PosMessage>();

    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    /// <summary>Read per query by the venue filter; <c>null</c> matches no rows.</summary>
    private VenueId? CurrentVenueId => venueContext.VenueId;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampVenueIds();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampVenueIds();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("btree_gist");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReservationsDbContext).Assembly);

        modelBuilder.Entity<Venue>().HasQueryFilter(VenueFilter, x => x.Id == CurrentVenueId);
        modelBuilder.Entity<Area>().HasQueryFilter(VenueFilter, x => EF.Property<VenueId>(x, ShadowKeys.VenueId) == CurrentVenueId);
        modelBuilder.Entity<Table>().HasQueryFilter(VenueFilter, x => EF.Property<VenueId>(x, ShadowKeys.VenueId) == CurrentVenueId);
        modelBuilder.Entity<Guest>().HasQueryFilter(VenueFilter, x => x.VenueId == CurrentVenueId);
        modelBuilder.Entity<Booking>().HasQueryFilter(VenueFilter, x => x.VenueId == CurrentVenueId);
        modelBuilder.Entity<WaitlistEntry>().HasQueryFilter(VenueFilter, x => x.VenueId == CurrentVenueId);
        modelBuilder.Entity<Message>().HasQueryFilter(VenueFilter, x => x.VenueId == CurrentVenueId);
        modelBuilder.Entity<PosMessage>().HasQueryFilter(VenueFilter, x => x.VenueId == CurrentVenueId);
        modelBuilder.Entity<AuditEntry>().HasQueryFilter(VenueFilter, x => x.VenueId == CurrentVenueId);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<VenueId>().HaveConversion<VenueIdConverter>();
        configurationBuilder.Properties<AreaId>().HaveConversion<AreaIdConverter>();
        configurationBuilder.Properties<TableId>().HaveConversion<TableIdConverter>();
        configurationBuilder.Properties<GuestId>().HaveConversion<GuestIdConverter>();
        configurationBuilder.Properties<BookingId>().HaveConversion<BookingIdConverter>();
        configurationBuilder.Properties<WaitlistEntryId>().HaveConversion<WaitlistEntryIdConverter>();
        configurationBuilder.Properties<MessageId>().HaveConversion<MessageIdConverter>();
        configurationBuilder.Properties<PosMessageId>().HaveConversion<PosMessageIdConverter>();

        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
        configurationBuilder.Properties<Money>().HaveConversion<MoneyCentsConverter>();
        configurationBuilder.Properties<PartySize>().HaveConversion<PartySizeConverter>();
        configurationBuilder.Properties<PersonName>().HaveConversion<PersonNameConverter>();
        configurationBuilder.Properties<PhoneNumber>().HaveConversion<PhoneNumberConverter>();
        configurationBuilder.Properties<BookingReference>().HaveConversion<BookingReferenceConverter>();
        configurationBuilder.Properties<TimeRange>().HaveConversion<TimeRangeConverter>();

        // DM-06: every status and kind is stored as its C# name.
        configurationBuilder.Properties<BookingStatus>().HaveConversion<string>();
        configurationBuilder.Properties<BookingChannel>().HaveConversion<string>();
        configurationBuilder.Properties<DepositStatus>().HaveConversion<string>();
        configurationBuilder.Properties<CancelReason>().HaveConversion<string>();
        configurationBuilder.Properties<CompletionSource>().HaveConversion<string>();
        configurationBuilder.Properties<RefundDecision>().HaveConversion<string>();
        configurationBuilder.Properties<WaitlistStatus>().HaveConversion<string>();
        configurationBuilder.Properties<MessagePurpose>().HaveConversion<string>();
        configurationBuilder.Properties<MessageStatus>().HaveConversion<string>();
        configurationBuilder.Properties<MessageChannel>().HaveConversion<string>();
        configurationBuilder.Properties<Language>().HaveConversion<string>();
        configurationBuilder.Properties<OutboxStatus>().HaveConversion<string>();
        configurationBuilder.Properties<PosDirection>().HaveConversion<string>();
        configurationBuilder.Properties<PosMessageStatus>().HaveConversion<string>();
    }

    /// <summary>
    /// Copies the owner's venue ID into rows whose <c>venue_id</c> has no domain property:
    /// tables (from the venue), table assignments and bills (from the booking).
    /// </summary>
    private void StampVenueIds()
    {
        ChangeTracker.DetectChanges();

        foreach (var venue in ChangeTracker.Entries<Venue>().Select(e => e.Entity).ToList())
        {
            foreach (var table in venue.Tables)
            {
                Stamp(Entry(table), venue.Id);
            }
        }

        foreach (var booking in ChangeTracker.Entries<Booking>().Select(e => e.Entity).ToList())
        {
            foreach (var assignment in booking.Tables)
            {
                Stamp(Entry(assignment), booking.VenueId);
            }

            foreach (var bill in booking.Bills)
            {
                Stamp(Entry(bill), booking.VenueId);
            }
        }
    }

    private static void Stamp(EntityEntry entry, VenueId venueId)
    {
        if (entry.State is EntityState.Detached or EntityState.Deleted)
        {
            return;
        }

        var property = entry.Property(ShadowKeys.VenueId);
        if (!Equals(property.CurrentValue, venueId))
        {
            property.CurrentValue = venueId;
        }
    }
}
