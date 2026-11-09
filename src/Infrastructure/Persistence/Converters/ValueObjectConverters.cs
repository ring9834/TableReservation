using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NpgsqlTypes;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Persistence.Converters;

/// <summary>Npgsql writes <c>timestamptz</c> only with offset 0, so every value is normalised to UTC (DM-04).</summary>
public sealed class UtcDateTimeOffsetConverter()
    : ValueConverter<DateTimeOffset, DateTimeOffset>(value => value.ToUniversalTime(), value => value);

/// <summary>Money columns hold integer cents in the venue currency, NZD (BR-11, DM-03); no currency column.</summary>
public sealed class MoneyCentsConverter() : ValueConverter<Money, long>(money => money.Cents, cents => Money.FromNzd(cents));

public sealed class PartySizeConverter() : ValueConverter<PartySize, int>(size => size.Value, value => PartySize.Of(value));

public sealed class PersonNameConverter() : ValueConverter<PersonName, string>(name => name.Value, value => FromDb(value))
{
    private static PersonName FromDb(string value) => PersonName.Create(value).Value;
}

/// <summary>Stored in E.164 (<c>mobile_e164</c>, DM-05).</summary>
public sealed class PhoneNumberConverter() : ValueConverter<PhoneNumber, string>(phone => phone.E164, value => FromDb(value))
{
    private static PhoneNumber FromDb(string value) => PhoneNumber.Create(value).Value;
}

public sealed class BookingReferenceConverter()
    : ValueConverter<BookingReference, string>(reference => reference.Value, value => FromDb(value))
{
    private static BookingReference FromDb(string value) => BookingReference.Parse(value).Value;
}

/// <summary><see cref="TimeRange"/> ↔ <c>tstzrange</c>, half-open <c>[start, end)</c> in UTC.</summary>
public sealed class TimeRangeConverter() : ValueConverter<TimeRange, NpgsqlRange<DateTime>>(range => ToDb(range), value => FromDb(value))
{
    private static NpgsqlRange<DateTime> ToDb(TimeRange range) =>
        new(range.Start.UtcDateTime, lowerBoundIsInclusive: true, range.End.UtcDateTime, upperBoundIsInclusive: false);

    private static TimeRange FromDb(NpgsqlRange<DateTime> value) =>
        new(
            new DateTimeOffset(DateTime.SpecifyKind(value.LowerBound, DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(value.UpperBound, DateTimeKind.Utc)));
}
