using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Pipeline;
using Reservation.Api.Realtime;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Demo;

/// <param name="Mode">Fixed (business time stands still) or running (it moves with real time).</param>
/// <param name="At">Business time now (UTC).</param>
/// <param name="LocalTime">Business time now in the venue's zone, to the minute.</param>
/// <param name="TimeZone">The venue's IANA zone.</param>
/// <param name="ClosesAt">The latest time the presenter can move the clock to: the last booking plus the longest turn (UTC).</param>
internal sealed record DemoClockResponse(DemoTimeMode Mode, DateTime At, TimeOnly LocalTime, string TimeZone, DateTime ClosesAt);

/// <param name="Mode">Fixed or running.</param>
internal sealed record SetDemoClockRequest(DemoTimeMode? Mode)
{
    /// <summary>
    /// Optional venue-local time on the current demo date to move to; never earlier than now. Without it the clock stays
    /// where it is. An init property, so it is optional in OpenAPI.
    /// </summary>
    public TimeOnly? At { get; init; }
}

internal sealed class SetDemoClockRequestValidator : AbstractValidator<SetDemoClockRequest>
{
    public SetDemoClockRequestValidator() => RuleFor(x => x.Mode).NotNull().WithErrorCode("demo_clock_mode_required");
}

/// <param name="Minutes">How far to move business time forward: 1 to 360 minutes.</param>
internal sealed record AdvanceDemoClockRequest(int Minutes);

internal sealed class AdvanceDemoClockRequestValidator : AbstractValidator<AdvanceDemoClockRequest>
{
    public AdvanceDemoClockRequestValidator() =>
        RuleFor(x => x.Minutes).InclusiveBetween(1, DemoClockPolicy.MaxAdvanceMinutes).WithErrorCode(DemoClockPolicy.MinutesOutOfRange);
}

/// <summary>
/// The presenter's clock controls (M11a, Design §17, §17.2), mapped only when <c>Demo:Enabled</c> and the demo clock is
/// the business clock (<c>Demo:Clock:Mode = Fixed</c>):
/// <list type="bullet">
/// <item><c>GET /demo/clock</c> (Host or Presenter, <c>getDemoClock</c>): the mode, business time and the closing limit.</item>
/// <item><c>PUT /demo/clock {mode, at?}</c> (Presenter, <c>setDemoClock</c>): Freeze or Run, optionally from a later
/// time today. Earlier than now → 422 <c>demo_clock_backwards</c> (only the reset goes back).</item>
/// <item><c>POST /demo/clock/advance {minutes}</c> (Presenter, <c>advanceDemoClock</c>): 1–360 minutes forward
/// (<c>demo_clock_minutes_out_of_range</c>), keeping the mode.</item>
/// </list>
/// Past the closing limit → 422 <c>demo_clock_past_closing</c>. Only business time moves: the infrastructure clock
/// (holds, retries, message expiry, rate limits, sessions) is untouched (rule 14). After a change the store rings the
/// overdue and arriving-soon jobs, and <c>demoClockChanged</c> goes to every venue's screens, which refetch everything.
/// </summary>
internal sealed partial class DemoClockEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        var services = api.ServiceProvider;
        if (!services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled
            || services.GetRequiredService<IOptions<DemoClockOptions>>().Value.Mode != DemoClockMode.Fixed)
        {
            return;
        }

        api.MapGet("/demo/clock", GetAsync)
            .WithName("getDemoClock")
            .WithTags("Demo")
            .RequireAuthorization(Policies.Host)
            .Produces<DemoClockResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);

        api.MapPut("/demo/clock", SetAsync)
            .WithName("setDemoClock")
            .WithTags("Demo")
            .RequireAuthorization(Policies.Presenter)
            .WithValidation<SetDemoClockRequest>()
            .Produces<DemoClockResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status422UnprocessableEntity);

        api.MapPost("/demo/clock/advance", AdvanceAsync)
            .WithName("advanceDemoClock")
            .WithTags("Demo")
            .RequireAuthorization(Policies.Presenter)
            .WithValidation<AdvanceDemoClockRequest>()
            .Produces<DemoClockResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status422UnprocessableEntity);
    }

    private static async Task<IResult> GetAsync(
        DemoClock clock, ReservationsDbContext db, [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
        CancellationToken cancellationToken)
    {
        var now = clock.State.NowAt(infrastructureClock.GetUtcNow());
        var closesAt = await ClosesAtAsync(db, clock, now, cancellationToken);
        return TypedResults.Ok(Response(clock, clock.State.Mode, now, closesAt));
    }

    private static async Task<IResult> SetAsync(
        SetDemoClockRequest request,
        DemoClock clock,
        DemoClockStore store,
        ReservationsDbContext db,
        IVenueBroadcaster broadcaster,
        ILogger<DemoClockEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var mode = request.Mode!.Value;
        var closesAt = await ClosesAtAsync(db, clock, clock.GetUtcNow(), cancellationToken);
        var result = await store.ChangeAsync(
            (state, infrastructureNow) =>
            {
                DateTimeOffset? at = request.At is { } time
                    ? clock.At(clock.LocalDate(state.NowAt(infrastructureNow)), time)
                    : null;
                return DemoClockPolicy.Set(state, infrastructureNow, mode, at, closesAt);
            },
            cancellationToken);
        return await RespondAsync(result, clock, closesAt, db, broadcaster, logger, cancellationToken);
    }

    private static async Task<IResult> AdvanceAsync(
        AdvanceDemoClockRequest request,
        DemoClock clock,
        DemoClockStore store,
        ReservationsDbContext db,
        IVenueBroadcaster broadcaster,
        ILogger<DemoClockEndpoints> logger,
        CancellationToken cancellationToken)
    {
        var closesAt = await ClosesAtAsync(db, clock, clock.GetUtcNow(), cancellationToken);
        var result = await store.ChangeAsync(
            (state, infrastructureNow) => DemoClockPolicy.Advance(state, infrastructureNow, request.Minutes, closesAt),
            cancellationToken);
        return await RespondAsync(result, clock, closesAt, db, broadcaster, logger, cancellationToken);
    }

    private static async Task<IResult> RespondAsync(
        Result<DemoClockState> result,
        DemoClock clock,
        DateTimeOffset closesAt,
        ReservationsDbContext db,
        IVenueBroadcaster broadcaster,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!result.IsSuccess)
        {
            return ResultToHttp.ToProblem(result.Error!);
        }

        var state = result.Value;
        LogChanged(logger, state.Mode, clock.LocalDate(state.BusinessAt), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(state.BusinessAt, clock.TimeZone).DateTime));
        await PushAsync(db, broadcaster, new DemoClockChangedNotice(state.Mode, state.BusinessAt.UtcDateTime), logger, cancellationToken);
        return TypedResults.Ok(Response(clock, state.Mode, state.BusinessAt, closesAt));
    }

    /// <summary>After the commit, to every venue: the screens refetch everything. A failed push is logged and swallowed.</summary>
    private static async Task PushAsync(
        ReservationsDbContext db, IVenueBroadcaster broadcaster, DemoClockChangedNotice payload, ILogger logger, CancellationToken cancellationToken)
    {
        var venueIds = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);
        var notice = new RealtimeNotice(NoticeMethods.DemoClockChanged, payload);
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
    }

    /// <summary>
    /// The closing limit on the demo date (agreed 16 Nov): the latest end of the service window (last booking plus the
    /// longest turn, <see cref="AvailabilityCalculator.ServiceWindow"/>) across the venues; 22:30 for Golden Dragon.
    /// </summary>
    internal static async Task<DateTimeOffset> ClosesAtAsync(
        ReservationsDbContext db, DemoClock clock, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var venues = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .ToListAsync(cancellationToken);
        var date = clock.LocalDate(now);
        return venues.Count == 0
            ? clock.At(date, TimeOnly.MaxValue)
            : venues.Max(v => AvailabilityCalculator.ServiceWindow(v.Settings, v.FindTimeZone(), date).End);
    }

    private static DemoClockResponse Response(DemoClock clock, DemoTimeMode mode, DateTimeOffset now, DateTimeOffset closesAt)
    {
        var local = TimeZoneInfo.ConvertTime(now, clock.TimeZone);
        return new DemoClockResponse(
            mode,
            now.UtcDateTime,
            new TimeOnly(local.Hour, local.Minute),
            clock.TimeZone.Id,
            closesAt.UtcDateTime);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo clock: {Mode} at {Date} {Time} (venue time)")]
    private static partial void LogChanged(ILogger logger, DemoTimeMode mode, DateOnly date, TimeOnly time);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo clock: {ExceptionType} while pushing demoClockChanged; screens refetch on reconnect")]
    private static partial void LogPushFailed(ILogger logger, string exceptionType);
}
