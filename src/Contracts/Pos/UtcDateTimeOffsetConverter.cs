using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reservation.Contracts.Pos;

/// <summary>
/// POS message times as the partner contract states them (docs/api/pos-messages.md, "Envelope"): ISO 8601 in UTC,
/// ending in <c>Z</c>, e.g. <c>2026-11-24T05:25:00Z</c>, with fractional seconds only when there are any. System.Text.Json
/// would write <c>+00:00</c>. Reading accepts <c>Z</c>, <c>+00:00</c> or any other offset and returns the instant in UTC.
/// Used only by <see cref="PosJson.Options"/>, so the rest of the API is unchanged.
/// </summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetDateTimeOffset().ToUniversalTime();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
