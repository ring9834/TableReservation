using System.Text.Json;
using System.Text.Json.Serialization;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Outbox;

/// <summary>
/// JSON for envelopes and payloads (ADR-002): camelCase names, enums as camelCase strings, strongly typed IDs as
/// plain UUIDs, times as ISO 8601.
/// </summary>
public static class EventJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new EntityIdJsonConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class EntityIdJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsValueType && typeof(IEntityId).IsAssignableFrom(typeToConvert);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(EntityIdJsonConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class EntityIdJsonConverter<TId> : JsonConverter<TId>
        where TId : struct, IEntityId
    {
        public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            (TId)Activator.CreateInstance(typeof(TId), reader.GetGuid())!;

        public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
