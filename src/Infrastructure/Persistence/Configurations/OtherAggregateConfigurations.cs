using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Domain.Messaging;
using Reservation.Domain.Venues;
using Reservation.Domain.Waitlist;

namespace Reservation.Infrastructure.Persistence.Configurations;

internal sealed class GuestConfiguration : IEntityTypeConfiguration<Guest>
{
    public void Configure(EntityTypeBuilder<Guest> builder)
    {
        builder.ToTable("guests");
        builder.MapAggregate<Guest, GuestId>();
        builder.Property(x => x.VenueId);
        builder.Property(x => x.Name);
        builder.Property(x => x.Mobile).HasColumnName("mobile_e164");
        builder.Property(x => x.Email);
        builder.Property(x => x.Language);
        builder.Property(x => x.AllergyNotes);
        builder.Property(x => x.VisitCount).HasDefaultValue(0).ValueGeneratedNever();
        builder.Property(x => x.LastBillTotal).HasColumnName("last_bill_cents");
        builder.Property(x => x.LastVisitAt);

        builder.HasIndex(x => new { x.VenueId, x.Mobile }).IsUnique();
        builder.HasOne<Venue>().WithMany().HasForeignKey(x => x.VenueId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries");
        builder.MapAggregate<WaitlistEntry, WaitlistEntryId>();
        builder.Property(x => x.VenueId);
        builder.Property(x => x.GuestId);
        builder.Property(x => x.ContactName).HasColumnName("contact_name").IsRequired();
        builder.Property(x => x.PartySize);
        builder.Property(x => x.QuotedWaitMinutes);
        builder.Property(x => x.Status);
        builder.Property(x => x.JoinedAt);
        builder.Property(x => x.NotifiedAt);
        builder.Property(x => x.EndedAt);
        builder.Property(x => x.NotifiedTableId);
        builder.Property(x => x.BookingId);
        builder.Ignore(x => x.IsActive);

        builder.HasOne<Guest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("messages");
        builder.MapAggregate<Message, MessageId>();
        builder.Property(x => x.VenueId);
        builder.Property(x => x.GuestId);
        builder.Property(x => x.Purpose);
        builder.Property(x => x.Channel);
        builder.Property(x => x.Language);
        builder.Property(x => x.Body);
        builder.Property(x => x.SubjectId);
        builder.Property(x => x.Status);
        builder.Property(x => x.CreatedAt);
        builder.Property(x => x.SentAt);
        builder.Property(x => x.DeliveredAt);
        builder.Property(x => x.Attempts).HasDefaultValue(0).ValueGeneratedNever();
        builder.Property(x => x.NextAttemptAt);
        builder.Property(x => x.ExpiresAt);
        builder.Property(x => x.ProviderRef);
        builder.Property(x => x.LastError);

        builder.Ignore(x => x.Actions);
        builder.Property<List<MessageAction>>("_actions").HasColumnName("actions").HasJsonConversion();

        builder.HasIndex(x => x.NextAttemptAt).HasFilter("status = 'Queued'").HasDatabaseName("ix_messages_queued");
    }
}
