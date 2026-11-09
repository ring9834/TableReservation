using Reservation.Domain.Common;

namespace Reservation.Domain.Bookings;

/// <summary>6 characters from an unambiguous alphabet (no 0/O/1/I), e.g. <c>GD7K4P</c>. Unique per venue (DB).</summary>
public sealed record BookingReference
{
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 6;

    private BookingReference(string value) => Value = value;

    public string Value { get; }

    public static Result<BookingReference> Parse(string? value)
    {
        var normalised = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalised.Length == Length && normalised.All(c => Alphabet.Contains(c, StringComparison.Ordinal))
            ? new BookingReference(normalised)
            : Error.Validation("reference", "reference_invalid");
    }

    public static BookingReference Generate(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        Span<char> chars = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[random.Next(Alphabet.Length)];
        }

        return new BookingReference(new string(chars));
    }

    public override string ToString() => Value;
}
