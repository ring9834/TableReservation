using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Pos;

/// <summary>
/// <c>pos_venue_sequences</c>: the last <c>venue_sequence</c> handed out per venue (Design §6.7). Written only by
/// <see cref="PosSequenceAllocator"/> with one <c>INSERT … ON CONFLICT … RETURNING</c>.
/// </summary>
public sealed class PosVenueSequence
{
    public VenueId VenueId { get; private set; }

    public long LastSequence { get; private set; }
}

/// <summary><c>mock_pos_state</c>: the demo mock POS's online switch (one row; no row means online).</summary>
public sealed class MockPosState
{
    public const short SingletonId = 1;

    public short Id { get; private set; }

    public bool Online { get; private set; }

    public DateTimeOffset ChangedAt { get; private set; }
}

/// <summary><c>mock_pos_received</c>: every idempotency key the mock POS accepted, in the order it accepted them.</summary>
public sealed class MockPosReceipt
{
    public string IdempotencyKey { get; private set; } = null!;

    public long ReceivedOrder { get; private set; }

    public VenueId VenueId { get; private set; }

    public string MessageType { get; private set; } = null!;

    public BookingId? BookingId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }
}

/// <summary><c>mock_pos_open_tables</c>: which booking the mock POS has open on which table (from <c>OpenTable</c>).</summary>
public sealed class MockPosOpenTable
{
    public TableId TableId { get; private set; }

    public VenueId VenueId { get; private set; }

    public BookingId BookingId { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }
}

internal sealed class PosVenueSequenceConfiguration : IEntityTypeConfiguration<PosVenueSequence>
{
    public void Configure(EntityTypeBuilder<PosVenueSequence> builder)
    {
        builder.ToTable("pos_venue_sequences");
        builder.HasKey(x => x.VenueId);
        builder.Property(x => x.VenueId).ValueGeneratedNever();
    }
}

internal sealed class MockPosStateConfiguration : IEntityTypeConfiguration<MockPosState>
{
    public void Configure(EntityTypeBuilder<MockPosState> builder)
    {
        builder.ToTable("mock_pos_state", t => t.HasCheckConstraint("ck_mock_pos_state_singleton", "id = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
    }
}

internal sealed class MockPosReceiptConfiguration : IEntityTypeConfiguration<MockPosReceipt>
{
    public void Configure(EntityTypeBuilder<MockPosReceipt> builder)
    {
        builder.ToTable("mock_pos_received");
        builder.HasKey(x => x.IdempotencyKey);
        builder.Property(x => x.ReceivedOrder).UseIdentityByDefaultColumn();
    }
}

internal sealed class MockPosOpenTableConfiguration : IEntityTypeConfiguration<MockPosOpenTable>
{
    public void Configure(EntityTypeBuilder<MockPosOpenTable> builder)
    {
        builder.ToTable("mock_pos_open_tables");
        builder.HasKey(x => x.TableId);
        builder.Property(x => x.TableId).ValueGeneratedNever();
    }
}
