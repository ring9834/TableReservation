using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Domain.Venues;

namespace Reservation.Infrastructure.Persistence.Configurations;

internal sealed class BookingConfiguration : IEntityTypeConfiguration<Booking>
{
    public void Configure(EntityTypeBuilder<Booking> builder)
    {
        builder.ToTable("bookings", t => t.HasCheckConstraint("ck_bookings_party_size", "party_size BETWEEN 1 AND 20"));
        builder.MapAggregate<Booking, BookingId>();

        builder.Property(x => x.VenueId);
        builder.Property(x => x.GuestId);
        builder.Property(x => x.ContactName).HasColumnName("contact_name");
        builder.Property(x => x.Reference);
        builder.Property(x => x.Channel);
        builder.Property(x => x.PartySize);
        builder.Property(x => x.Period).HasColumnType("tstzrange");
        builder.Property(x => x.Status);
        builder.Property(x => x.Notes);
        builder.Property(x => x.Occasion);
        builder.Property(x => x.Language);
        builder.Property(x => x.CreatedAt);
        builder.Property(x => x.HoldExpiresAt);
        builder.Property(x => x.SeatedAt);
        builder.Property(x => x.CompletedAt);
        builder.Property(x => x.CompletedBy).HasColumnName("completion_source");
        builder.Property(x => x.CancellationReason).HasColumnName("cancel_reason");
        builder.Property(x => x.CancellationRefund).HasColumnName("refund_decision");
        builder.Property(x => x.Terms).HasJsonConversion();
        builder.Property(x => x.Reminder).HasJsonConversion();
        builder.Ignore(x => x.BillTotal);
        builder.Ignore(x => x.IsActive);

        builder.HasIndex(x => new { x.VenueId, x.Reference }).IsUnique();
        builder.HasOne<Venue>().WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Guest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Restrict);

        // The deposit lives in the bookings row (§2.2): deposit_cents, deposit_status, payment_intent_id, refund_id.
        builder.OwnsOne(x => x.Deposit, deposit =>
        {
            deposit.Property(d => d.Amount).HasColumnName("deposit_cents");
            deposit.Property(d => d.Status).HasColumnName("deposit_status");
            deposit.Property(d => d.PaymentIntentId).HasColumnName("payment_intent_id");
            deposit.Property(d => d.RefundId).HasColumnName("refund_id");
        });

        builder.OwnsMany(x => x.Tables, assignment =>
        {
            assignment.ToTable("table_assignments");
            assignment.WithOwner().HasForeignKey(ShadowKeys.BookingId);
            assignment.Property(a => a.TableId);
            assignment.HasKey(ShadowKeys.BookingId, nameof(TableAssignment.TableId));
            assignment.Property(a => a.Period).HasColumnType("tstzrange");
            assignment.Property(a => a.Active);
            assignment.Property<VenueId>(ShadowKeys.VenueId);
            assignment.HasOne<Table>().WithMany().HasForeignKey(a => a.TableId).OnDelete(DeleteBehavior.Restrict);

            // The exclusion constraint ex_table_assignments_no_overlap is added in the migration (§12).
        });
        builder.Navigation(x => x.Tables).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One row per POS bill (§6.6). Bill IDs are unique per venue; one bill per booking for now.
        builder.OwnsMany(x => x.Bills, bill =>
        {
            bill.ToTable("booking_bills");
            bill.WithOwner().HasForeignKey(ShadowKeys.BookingId);
            bill.Property(b => b.PosBillId);
            bill.HasKey(ShadowKeys.BookingId, nameof(BookingBill.PosBillId));
            bill.Property(b => b.Total).HasColumnName("total_cents");
            bill.Property(b => b.Tip).HasColumnName("tip_cents");
            bill.Property(b => b.ClosedAt);
            bill.Property(b => b.SourceMessage).HasColumnName("pos_message_id");
            bill.Property<VenueId>(ShadowKeys.VenueId);
            bill.HasIndex(ShadowKeys.VenueId, nameof(BookingBill.PosBillId)).IsUnique()
                .HasDatabaseName("ux_booking_bills_venue_pos_bill");
            bill.HasIndex(ShadowKeys.BookingId).IsUnique()
                .HasDatabaseName("ux_booking_bills_one_per_booking"); // drop to allow split bills
        });
        builder.Navigation(x => x.Bills).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
