using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reservation.Infrastructure.Audit;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Pos;

namespace Reservation.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox");
        builder.HasKey(x => x.EventId);
        builder.Property(x => x.EventId).ValueGeneratedNever();
        builder.Property(x => x.Envelope).HasColumnType("jsonb");
        builder.Property(x => x.Attempts).HasDefaultValue(0).ValueGeneratedNever();
        builder.HasIndex(x => new { x.AggregateId, x.Sequence }).IsUnique();
        builder.HasIndex(x => x.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_outbox_pending");
    }
}

internal sealed class InboxEntryConfiguration : IEntityTypeConfiguration<InboxEntry>
{
    public void Configure(EntityTypeBuilder<InboxEntry> builder)
    {
        builder.ToTable("inbox");
        builder.HasKey(x => new { x.Consumer, x.MessageId });
    }
}

internal sealed class PosMessageConfiguration : IEntityTypeConfiguration<PosMessage>
{
    public void Configure(EntityTypeBuilder<PosMessage> builder)
    {
        builder.ToTable("pos_messages");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Payload).HasColumnType("jsonb");
        builder.Property(x => x.Attempts).HasDefaultValue(0).ValueGeneratedNever();
        builder.HasIndex(x => x.IdempotencyKey).IsUnique();
        builder.HasIndex(x => new { x.VenueId, x.VenueSequence }).IsUnique();
        builder.HasIndex(x => new { x.VenueId, x.CreatedAt })
            .HasFilter("status = 'Unmatched'")
            .HasDatabaseName("ix_pos_unmatched");
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_log");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseSerialColumn();
        builder.Property(x => x.Actor).HasColumnType("jsonb");
        builder.Property(x => x.Summary).HasColumnType("jsonb");
        builder.HasIndex(x => x.EventId).IsUnique();
    }
}
