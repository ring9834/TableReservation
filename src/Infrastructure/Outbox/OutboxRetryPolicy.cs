namespace Reservation.Infrastructure.Outbox;

/// <summary>What happens to an outbox row after a failed attempt.</summary>
public readonly record struct OutboxRetry(OutboxStatus Status, DateTimeOffset NextAttemptAt);

/// <summary>
/// ADR-002 backoff: 5 s, 30 s, 2 min, 10 min, then every 30 min until 24 h after the row was created; a retry
/// that would fall later marks the row <see cref="OutboxStatus.Failed"/>. All times are infrastructure time.
/// </summary>
public static class OutboxRetryPolicy
{
    public static readonly IReadOnlyList<TimeSpan> Backoff =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    public static readonly TimeSpan Repeat = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromHours(24);

    /// <summary>The delay after <paramref name="attempts"/> failed attempts (1-based).</summary>
    public static TimeSpan DelayAfter(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        return attempts <= Backoff.Count ? Backoff[attempts - 1] : Repeat;
    }

    public static OutboxRetry Next(int attempts, DateTimeOffset createdAt, DateTimeOffset now)
    {
        var next = now + DelayAfter(attempts);
        return next > createdAt + GiveUpAfter
            ? new OutboxRetry(OutboxStatus.Failed, now)
            : new OutboxRetry(OutboxStatus.Pending, next);
    }
}
