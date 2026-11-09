using Reservation.Domain.Common;

namespace Reservation.Tests.Unit.Support;

/// <summary>Checks that events are raised once each, with <c>LastEventSequence</c> going up by exactly 1 per event.</summary>
public static class EventAssert
{
    /// <summary>Exactly one new event, of type <typeparamref name="T"/>, stamped <paramref name="sequenceBefore"/> + 1.</summary>
    public static T RaisedOnce<T>(IAggregateRoot aggregate, long sequenceBefore)
        where T : IDomainEvent
    {
        var raised = Raised(aggregate, sequenceBefore, typeof(T));
        return (T)raised[0];
    }

    /// <summary>Exactly these new events, in this order, with consecutive sequence numbers.</summary>
    public static IReadOnlyList<IDomainEvent> Raised(IAggregateRoot aggregate, long sequenceBefore, params Type[] eventTypes)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var newEvents = aggregate.DomainEvents.Where(e => e.Sequence > sequenceBefore).ToList();

        Assert.Equal(sequenceBefore + eventTypes.Length, aggregate.LastEventSequence);
        Assert.Equal(eventTypes.Length, newEvents.Count);
        for (var i = 0; i < eventTypes.Length; i++)
        {
            Assert.Equal(sequenceBefore + i + 1, newEvents[i].Sequence);
            Assert.IsType(eventTypes[i], newEvents[i].Event);
            Assert.Single(newEvents, e => e.Event.EventId == newEvents[i].Event.EventId);
        }

        return newEvents.Select(e => e.Event).ToList();
    }

    /// <summary>No new event and no change to <c>LastEventSequence</c>.</summary>
    public static void NoneRaised(IAggregateRoot aggregate, long sequenceBefore)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        Assert.Equal(sequenceBefore, aggregate.LastEventSequence);
        Assert.DoesNotContain(aggregate.DomainEvents, e => e.Sequence > sequenceBefore);
    }

    public static void Failed(Result result, string code, ErrorKind kind)
    {
        ArgumentNullException.ThrowIfNull(result);
        Assert.True(result.IsFailure, "Expected a failure.");
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(kind, result.Error.Kind);
    }

    public static void InvalidTransition(Result result) =>
        Failed(result, ErrorCodes.InvalidTransition, ErrorKind.Conflict);
}
