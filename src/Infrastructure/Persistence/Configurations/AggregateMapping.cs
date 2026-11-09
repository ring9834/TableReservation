using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence.Converters;

namespace Reservation.Infrastructure.Persistence.Configurations;

internal static class AggregateMapping
{
    /// <summary>Key, <c>xmin</c> row version (concurrency only, DM-07) and the event counter (§2.2).</summary>
    public static void MapAggregate<T, TId>(this EntityTypeBuilder<T> builder)
        where T : AggregateRoot<TId>
        where TId : struct, IEntityId
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Version).IsRowVersion();
        builder.Property(x => x.LastEventSequence).HasDefaultValue(0L).ValueGeneratedNever();
        builder.Ignore(x => x.DomainEvents);
    }

    /// <summary>A value object kept as a <c>jsonb</c> column (DM-08).</summary>
    public static PropertyBuilder<TValue> HasJsonConversion<TValue>(this PropertyBuilder<TValue> property)
        where TValue : class? =>
        property
            .HasConversion(
                typeof(JsonValueConverter<>).MakeGenericType(typeof(TValue)),
                typeof(JsonValueComparer<>).MakeGenericType(typeof(TValue)))
            .HasColumnType("jsonb");
}
