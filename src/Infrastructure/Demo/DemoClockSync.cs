using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Infrastructure.Outbox;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// Keeps this instance's <see cref="DemoClock"/> in step with <c>demo_clock</c> (M11a, Design §17.2): loads the stored
/// state at once and then every <see cref="Interval"/> on the infrastructure clock, so a change made through another
/// API instance reaches this one within seconds (and rings its business-time jobs). The instance that made a change has
/// it at once. Runs with <c>Outbox:RunWorkers</c> when the demo clock is the business clock.
/// </summary>
public sealed partial class DemoClockSync(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> outboxOptions,
    IOptions<DemoClockOptions> clockOptions,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
    ILogger<DemoClockSync> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!outboxOptions.Value.RunWorkers || clockOptions.Value.Mode != DemoClockMode.Fixed)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval, infrastructureClock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<DemoClockStore>().LoadAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogLoadFailed(logger, ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // Exception type only: a database error message could echo SQL.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo clock sync: {ExceptionType} while loading the stored state; trying again shortly")]
    private static partial void LogLoadFailed(ILogger logger, string exceptionType);
}
