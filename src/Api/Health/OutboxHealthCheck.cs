using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Reservation.Infrastructure;

namespace Reservation.Api.Health;

/// <summary>
/// Outbox lag for <c>/health/ready</c> (ADR-004): Degraded when any row is <c>Failed</c>, or when the oldest due
/// <c>Pending</c> row has waited more than <see cref="MaxLag"/>; Unhealthy when the outbox can't be read.
/// Measured on the infrastructure clock (Design §5.6).
/// </summary>
public sealed class OutboxHealthCheck(NpgsqlDataSource dataSource, [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock) : IHealthCheck
{
    public static readonly TimeSpan MaxLag = TimeSpan.FromSeconds(30);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        try
        {
            await using var command = dataSource.CreateCommand(
                "SELECT (SELECT count(*) FROM outbox WHERE status = 'Failed'), " +
                "(SELECT min(next_attempt_at) FROM outbox WHERE status = 'Pending' AND next_attempt_at <= $1)");
            command.Parameters.Add(new NpgsqlParameter { Value = now });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var failed = reader.GetInt64(0);
            DateTimeOffset? oldestDue = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            return Evaluate(failed, oldestDue, now);
        }
        catch (NpgsqlException ex)
        {
            return HealthCheckResult.Unhealthy("Outbox unreadable.", ex);
        }
    }

    public static HealthCheckResult Evaluate(long failedRows, DateTimeOffset? oldestDue, DateTimeOffset now)
    {
        if (failedRows > 0)
        {
            return HealthCheckResult.Degraded($"{failedRows} outbox rows failed.");
        }

        return oldestDue is { } due && now - due > MaxLag
            ? HealthCheckResult.Degraded($"Outbox lag {(now - due).TotalSeconds:F0} s.")
            : HealthCheckResult.Healthy();
    }
}
