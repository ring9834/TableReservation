using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// <c>demo_clock</c>: the demo clock's one row (M11a, Design §17.2). No row = a reset state (Fixed, tonight at the
/// reset time). Written only by <see cref="DemoClockStore"/> and <see cref="DemoReset"/>; read by every instance.
/// </summary>
public sealed class DemoClockRow
{
    public const short SingletonId = 1;

    public short Id { get; private set; }

    public DemoTimeMode Mode { get; private set; }

    /// <summary>Business time at <see cref="AnchoredAt"/>.</summary>
    public DateTimeOffset BusinessAt { get; private set; }

    /// <summary>Infrastructure-clock time the state was set.</summary>
    public DateTimeOffset AnchoredAt { get; private set; }

    public DemoClockState ToState() => new(Mode, BusinessAt, AnchoredAt);
}

internal sealed class DemoClockRowConfiguration : IEntityTypeConfiguration<DemoClockRow>
{
    public void Configure(EntityTypeBuilder<DemoClockRow> builder)
    {
        builder.ToTable("demo_clock", t => t.HasCheckConstraint("ck_demo_clock_singleton", "id = 1"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Mode).HasConversion<string>().HasMaxLength(16);
    }
}
