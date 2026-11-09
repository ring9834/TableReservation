using System.Reflection;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Outbox;

/// <summary>What <c>[EventMetadata]</c> says about one event type: its catalogue name, current version and aggregate.</summary>
public sealed record EventMetadata(string Name, int Version, string AggregateType, Type Type);

/// <summary>
/// Lookup between catalogue names and C# event types (Design §5.1). The outbox stores the name, not the
/// C# type name, so renaming a class doesn't break stored events.
/// </summary>
public sealed class EventTypeRegistry
{
    private readonly Dictionary<Type, EventMetadata> _byType = [];
    private readonly Dictionary<string, EventMetadata> _byName = new(StringComparer.Ordinal);

    /// <summary>Every event in the Domain assembly.</summary>
    public EventTypeRegistry()
        : this(typeof(DomainEvent).Assembly.GetTypes())
    {
    }

    public EventTypeRegistry(IEnumerable<Type> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (var type in candidates.Where(t => t is { IsAbstract: false, IsClass: true } && typeof(IDomainEvent).IsAssignableFrom(t)))
        {
            var attribute = type.GetCustomAttribute<EventMetadataAttribute>()
                ?? throw new InvalidOperationException($"Event {type.Name} has no [EventMetadata].");
            if (string.IsNullOrWhiteSpace(attribute.AggregateType) || attribute.Version < 1)
            {
                throw new InvalidOperationException($"Event {type.Name} needs an AggregateType and a version of 1 or more.");
            }

            var metadata = new EventMetadata(attribute.Name, attribute.Version, attribute.AggregateType, type);
            if (!_byName.TryAdd(metadata.Name, metadata))
            {
                throw new InvalidOperationException($"Two events are named {metadata.Name}.");
            }

            _byType.Add(type, metadata);
        }
    }

    public IReadOnlyCollection<EventMetadata> All => _byType.Values;

    /// <summary>The C# type for a stored name and version; older versions are upcast first (see the serializer).</summary>
    public Type Resolve(string name, int version)
    {
        var current = Current(name);
        return version == current.Version
            ? current.Type
            : throw new InvalidOperationException($"{name} v{version} is not the current version (v{current.Version}).");
    }

    public EventMetadata Current(string name) =>
        _byName.TryGetValue(name, out var metadata) ? metadata : throw new InvalidOperationException($"Unknown event type {name}.");

    public EventMetadata MetadataOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _byType.TryGetValue(type, out var metadata) ? metadata : throw new InvalidOperationException($"{type.Name} is not a registered event.");
    }
}
