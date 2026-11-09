using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Infrastructure.Persistence.Configurations;

internal sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("venues");
        builder.MapAggregate<Venue, VenueId>();
        builder.Property(x => x.Name);
        builder.Property(x => x.TimeZone);
        builder.Property(x => x.Currency).HasColumnType("char(3)").HasDefaultValue(Money.Nzd).ValueGeneratedNever();
        builder.Property(x => x.Settings).HasJsonConversion();
        builder.Property(x => x.SettingsVersion).HasDefaultValue(1).ValueGeneratedNever();
        builder.Ignore(x => x.Tables);

        builder.HasMany(x => x.Areas).WithOne().HasForeignKey(ShadowKeys.VenueId).IsRequired();
        builder.Navigation(x => x.Areas).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class AreaConfiguration : IEntityTypeConfiguration<Area>
{
    public void Configure(EntityTypeBuilder<Area> builder)
    {
        builder.ToTable("areas");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name);
        builder.Property(x => x.SortOrder);

        builder.HasMany(x => x.Tables).WithOne().HasForeignKey(ShadowKeys.AreaId).IsRequired();
        builder.Navigation(x => x.Tables).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class TableConfiguration : IEntityTypeConfiguration<Table>
{
    public void Configure(EntityTypeBuilder<Table> builder)
    {
        builder.ToTable("tables", t => t.HasCheckConstraint("ck_tables_seats", "seats > 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name);
        builder.Property(x => x.Seats);
        builder.Property(x => x.Position).HasJsonConversion();

        // venue_id is copied from the venue on save (ReservationsDbContext.StampVenueIds).
        builder.Property<VenueId>(ShadowKeys.VenueId);
        builder.HasOne<Venue>().WithMany().HasForeignKey(ShadowKeys.VenueId).OnDelete(DeleteBehavior.Restrict);
    }
}
