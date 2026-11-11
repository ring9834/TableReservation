using Reservation.Domain.Common;
using Reservation.Infrastructure.Outbox;

namespace Reservation.Api.Realtime;

/// <summary>
/// The "Realtime" consumer (Design §4.3, §15.1): turns each domain event into a small notice for the venue's screens.
/// Registered unwrapped (<c>idempotent: false</c>): it reads and writes nothing, and a repeated notice only costs a
/// refetch. A failed push is logged and swallowed, so it never fails or delays the event; screens refetch on
/// reconnect anyway (ADR-002).
/// </summary>
internal sealed class RealtimeNotifier<T>(IVenueBroadcaster broadcaster, ILogger<RealtimeNotifier<T>> logger) : IEventHandler<T>
    where T : IDomainEvent
{
    public const string Consumer = "realtime";

    public string ConsumerName => Consumer;

    public async Task HandleAsync(T domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(context);
        var notice = NoticeMap.For(domainEvent, context.Sequence);
        if (notice is null)
        {
            return;
        }

        try
        {
            await broadcaster.SendAsync(domainEvent.VenueId, notice, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RealtimeLog.PushFailed(logger, ex.GetType().Name, notice.Method, context.EventType, context.EventId);
        }
    }
}

internal static partial class RealtimeLog
{
    // The exception itself is not attached: its message could echo a payload. Type, notice and event ID are enough.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Realtime: {ExceptionType} while pushing {Notice} for {EventType} {EventId}; screens refetch on reconnect")]
    public static partial void PushFailed(ILogger logger, string exceptionType, string notice, string eventType, Guid eventId);
}
