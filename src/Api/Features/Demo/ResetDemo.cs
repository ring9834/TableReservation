using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Realtime;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Payments;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Demo;

/// <summary>
/// <c>POST /demo/reset</c> (Presenter, <c>resetDemo</c>; Design §17, §18.10), mapped only when <c>Demo:Enabled</c>:
/// <list type="bullet">
/// <item><see cref="DemoReset"/> empties all venue data and reloads the Golden Dragon seed in one transaction; the mock
/// POS state is emptied with it, so the POS is online again (§6.7).</item>
/// <item>The Fake provider's waiting refunds are dropped: the seeded deposits keep their <c>pi_seed_…</c> intents, so a
/// refund made before the reset must not be reported against the fresh seed.</item>
/// <item>Reliable while the workers run (agreed 16 Nov): the workers are paused first (<see cref="WorkerPause"/>: no new
/// batch starts, the batches in flight commit), then the reset locks every table in one fixed order with a lock timeout;
/// a deadlock, serialization failure or lock timeout rolls that attempt back and it runs again, up to
/// <see cref="Attempts"/> times. If it still can't run, 503 <c>demo_reset_busy</c>: never <c>slot_taken</c>, which the
/// central handler would otherwise make of a deadlock.</item>
/// <item>Since M11a the demo clock goes back to Fixed at 18:25 tonight in the same transaction (Design §17.2), and the
/// overdue and arriving-soon jobs are rung; <c>demoReset</c> already makes every screen refetch, the clock included.</item>
/// <item>After the commit, <c>demoReset {}</c> goes to every venue's screens, which refetch everything (§15). It carries
/// no data; there is no outbox route because the reset empties the outbox. A failed push is logged and swallowed, as in
/// <see cref="RealtimeNotifier{T}"/>: screens also refetch on reconnect.</item>
/// </list>
/// 204, or 503 <c>demo_reset_busy</c>. The presenter's Reset button calls it (M11a).
/// </summary>
internal sealed partial class ResetDemoEndpoints : IEndpoint
{
    /// <summary>How many times the reset runs before giving up.</summary>
    public const int Attempts = 5;

    /// <summary>How long one attempt waits for each table lock.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long the reset waits for the workers' batches in flight to commit.</summary>
    public static readonly TimeSpan PauseTimeout = TimeSpan.FromSeconds(10);

    public void Map(IEndpointRouteBuilder api)
    {
        if (!api.ServiceProvider.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        {
            return;
        }

        api.MapPost("/demo/reset", HandleAsync)
            .WithName("resetDemo")
            .WithTags("Demo")
            .RequireAuthorization(Policies.Presenter)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> HandleAsync(
        IServiceScopeFactory scopeFactory,
        WorkerPause workers,
        FakeRefundQueue fakeRefunds,
        ReservationsDbContext db,
        IVenueBroadcaster broadcaster,
        ILogger<ResetDemoEndpoints> logger,
        CancellationToken cancellationToken)
    {
        using (var pause = await workers.PauseAsync(PauseTimeout, cancellationToken))
        {
            if (pause is null)
            {
                LogBusy(logger, "workers");
                return ApiProblems.Result(StatusCodes.Status503ServiceUnavailable, ProblemCodes.DemoResetBusy);
            }

            if (!await ResetWithRetriesAsync(scopeFactory, logger, cancellationToken))
            {
                return ApiProblems.Result(StatusCodes.Status503ServiceUnavailable, ProblemCodes.DemoResetBusy);
            }

            fakeRefunds.Clear();
        }

        LogReset(logger);

        var venueIds = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
            .AsNoTracking()
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);
        var notice = new RealtimeNotice(NoticeMethods.DemoReset, new DemoResetNotice());
        foreach (var venueId in venueIds)
        {
            try
            {
                await broadcaster.SendAsync(venueId, notice, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogPushFailed(logger, ex.GetType().Name);
            }
        }

        return TypedResults.NoContent();
    }

    /// <summary>Each attempt in its own scope (a fresh context and connection); false when every attempt was rolled back.</summary>
    private static async Task<bool> ResetWithRetriesAsync(
        IServiceScopeFactory scopeFactory, ILogger logger, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DemoReset>().ResetAsync(cancellationToken, LockTimeout);
                return true;
            }
            catch (Exception ex) when (DemoReset.IsRetryable(ex))
            {
                var sqlState = (ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException)?.SqlState ?? "?";
                LogRetry(logger, attempt, sqlState);
                if (attempt < Attempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken);
                }
            }
        }

        LogBusy(logger, "tables");
        return false;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo reset: attempt {Attempt} rolled back ({SqlState}); trying again")]
    private static partial void LogRetry(ILogger logger, int attempt, string sqlState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo reset: gave up, the {What} stayed busy (demo_reset_busy)")]
    private static partial void LogBusy(ILogger logger, string what);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo reset: data reseeded, mock POS online, clock fixed at the reset time")]
    private static partial void LogReset(ILogger logger);

    // The exception itself is not attached: its message could echo a payload.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo reset: {ExceptionType} while pushing demoReset; screens refetch on reconnect")]
    private static partial void LogPushFailed(ILogger logger, string exceptionType);
}
