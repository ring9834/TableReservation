using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Reservation.Infrastructure.Persistence.Converters;

/// <summary>
/// Value objects stored as <c>jsonb</c> (DM-08): camelCase names, enums as camelCase strings, and only
/// settable properties (computed ones such as <c>BookingTerms.Turn</c> are left out). Matches Design §2.6.
/// </summary>
public static class JsonColumn
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException($"Null {typeof(T).Name} in a jsonb column.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropComputedProperties } },
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.MakeReadOnly();
        return options;
    }

    private static void DropComputedProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].Set is null)
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}

public sealed class JsonValueConverter<T>() : ValueConverter<T, string>(value => JsonColumn.Serialize(value), json => JsonColumn.Deserialize<T>(json))
    where T : class;

/// <summary>Compares by serialised JSON, so records holding lists compare by content.</summary>
public sealed class JsonValueComparer<T>() : ValueComparer<T>(
    (left, right) => JsonColumn.Serialize(left) == JsonColumn.Serialize(right),
    value => JsonColumn.Serialize(value).GetHashCode(StringComparison.Ordinal),
    value => JsonColumn.Deserialize<T>(JsonColumn.Serialize(value)))
    where T : class;
