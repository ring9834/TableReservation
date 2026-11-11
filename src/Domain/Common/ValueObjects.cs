using PhoneNumbers;

namespace Reservation.Domain.Common;

public enum ActorType
{
    Guest,
    Staff,
    System,
    Pos,
}

/// <summary>Who did something (ADR-002 envelope).</summary>
public sealed record Actor(ActorType Type, string? Id = null, string? Role = null)
{
    public static Actor ForGuest(GuestId? guestId) => new(ActorType.Guest, guestId?.Value.ToString());

    public static Actor ForStaff(string id, string role) => new(ActorType.Staff, id, role);

    public static Actor ForSystem() => new(ActorType.System, "system");

    public static Actor ForPos() => new(ActorType.Pos, "pos");
}

public enum Language
{
    En,
    Zh,
}

/// <summary>Money in integer cents (BR-11). No floating point.</summary>
public readonly record struct Money(long Cents, string Currency)
{
    public const string Nzd = "NZD";

    public static Money FromNzd(long cents) => new(cents, Nzd);

    public static Result<Money> Create(long cents, string currency)
    {
        if (cents < 0)
        {
            return Error.Validation("amountCents", "amount_negative");
        }

        if (currency is not { Length: 3 } || !currency.All(char.IsAsciiLetterUpper))
        {
            return Error.Validation("currency", "currency_invalid");
        }

        return new Money(cents, currency);
    }

    public static Money operator +(Money left, Money right)
    {
        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException("Cannot add amounts in different currencies.");
        }

        return new Money(left.Cents + right.Cents, left.Currency);
    }

    public Money Times(int factor) => new(Cents * factor, Currency);
}

/// <summary>A half-open period <c>[Start, End)</c> in UTC; maps to <c>tstzrange</c>.</summary>
public readonly record struct TimeRange
{
    public TimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new ArgumentException("End must be after start.", nameof(end));
        }

        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();
    }

    public DateTimeOffset Start { get; }

    public DateTimeOffset End { get; }

    public TimeSpan Duration => End - Start;

    public static TimeRange FromStart(DateTimeOffset start, TimeSpan length) => new(start, start + length);

    public static Result<TimeRange> Create(DateTimeOffset start, DateTimeOffset end) =>
        end <= start ? Error.Validation("period", "period_invalid") : new TimeRange(start, end);

    public bool Contains(DateTimeOffset instant) => instant >= Start && instant < End;

    public bool Overlaps(TimeRange other) => Start < other.End && other.Start < End;
}

/// <summary>1–20 guests. The online limit (BR-04) is a venue setting checked by <c>Booking.Create</c>.</summary>
public readonly record struct PartySize
{
    public const int Min = 1;
    public const int Max = 20;

    private PartySize(int value) => Value = value;

    public int Value { get; }

    public static Result<PartySize> Create(int value) =>
        value is < Min or > Max ? Error.Validation("partySize", "party_size_out_of_range") : new PartySize(value);

    /// <summary>For values already known to be valid (seed data, tests); throws otherwise.</summary>
    public static PartySize Of(int value) =>
        value is < Min or > Max ? throw new ArgumentOutOfRangeException(nameof(value)) : new PartySize(value);

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A guest's name, trimmed, 1–80 characters.</summary>
public sealed record PersonName
{
    public const int MaxLength = 80;

    private PersonName(string value) => Value = value;

    public string Value { get; }

    public static Result<PersonName> Create(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length is 0 or > MaxLength
            ? Error.Validation("name", "name_length")
            : new PersonName(trimmed);
    }

    public override string ToString() => Value;
}

/// <summary>An NZ mobile number in E.164 form (<c>+64…</c>), BR-12.</summary>
public sealed record PhoneNumber
{
    private static readonly PhoneNumberUtil _util = PhoneNumberUtil.GetInstance();

    private PhoneNumber(string e164) => E164 = e164;

    public string E164 { get; }

    public static Result<PhoneNumber> Create(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Error.Validation("mobile", "mobile_invalid");
        }

        PhoneNumbers.PhoneNumber parsed;
        try
        {
            parsed = _util.Parse(input, "NZ");
        }
        catch (NumberParseException)
        {
            return Error.Validation("mobile", "mobile_invalid");
        }

        if (!_util.IsValidNumberForRegion(parsed, "NZ"))
        {
            return Error.Validation("mobile", "mobile_invalid");
        }

        if (_util.GetNumberType(parsed) is not (PhoneNumberType.MOBILE or PhoneNumberType.FIXED_LINE_OR_MOBILE))
        {
            return Error.Validation("mobile", "mobile_not_mobile");
        }

        return new PhoneNumber(_util.Format(parsed, PhoneNumberFormat.E164));
    }

    /// <summary>
    /// Whether <paramref name="input"/> could be an NZ phone number of any kind (landline, mobile, freephone), e.g. a
    /// venue's contact number. Possible, not valid: fictional demo numbers such as <c>+64 9 000 0000</c> pass.
    /// </summary>
    public static bool IsPossibleNz(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            var parsed = _util.Parse(input, "NZ");
            return parsed.CountryCode == 64 && _util.IsPossibleNumber(parsed);
        }
        catch (NumberParseException)
        {
            return false;
        }
    }

    /// <summary>Safe to log or show in the message log: country code and last 3 digits only.</summary>
    public string Masked() => E164.Length <= 6
        ? new string('*', E164.Length)
        : string.Concat(E164.AsSpan(0, 3), new string('*', E164.Length - 6), E164.AsSpan(E164.Length - 3));

    public override string ToString() => Masked();
}
