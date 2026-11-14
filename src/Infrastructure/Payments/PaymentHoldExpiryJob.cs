using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Domain.Bookings;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure.Payments;

/// <summary>
/// Design §16 <c>PaymentHoldExpiryJob</c> (M8a, §8.4): every 30 seconds on the infrastructure clock, cancels
/// <c>PendingPayment</c> bookings whose hold has run out. The hold is measured on the infrastructure clock (rule 14:
/// real time, so a hold still expires while the demo's business clock stands at 18:25, and the guest's countdown
/// matches); the event time passed to <c>ExpireHold</c> is the business clock. Each booking is its own transaction:
/// <c>Cancelled (HoldExpired)</c>, deposit <c>Released</c>, tables freed, and <c>BookingCancelled</c> through the outbox
/// (so <c>floorPlanChanged</c>). A booking changed meanwhile (a payment webhook won) is skipped. Runs only with
/// <c>Outbox:RunWorkers</c>.
/// </summary>
public sealed partial class PaymentHoldExpiryJob(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> outboxOptions,
    IOptions<PaymentHoldExpiryOptions> options,
    TimeProvider businessClock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
    ILogger<PaymentHoldExpiryJob> logger) : BackgroundService
{
    /// <summary>One check: expires every due hold and returns how many bookings it cancelled.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        var holdNow = infrastructureClock.GetUtcNow();
        var due = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .Where(b => b.Status == BookingStatus.PendingPayment && b.HoldExpiresAt <= holdNow)
            .OrderBy(b => b.HoldExpiresAt)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var id in due)
        {
            db.ChangeTracker.Clear();
            var booking = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
                .SingleOrDefaultAsync(b => b.Id == id, cancellationToken);
            if (booking is not { Status: BookingStatus.PendingPayment })
            {
                continue;
            }

            var result = booking.ExpireHold(businessClock.GetUtcNow(), infrastructureClock.GetUtcNow());
            if (result.IsFailure)
            {
                continue;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                expired++;
                LogExpired(logger, id.Value);
            }
            catch (DbUpdateConcurrencyException)
            {
                LogSkipped(logger, id.Value);
            }
        }

        return expired;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!outboxOptions.Value.RunWorkers)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.Interval, infrastructureClock);
        try
        {
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogCheckFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Payment hold expired for booking {BookingId}")]
    private static partial void LogExpired(ILogger logger, Guid bookingId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Payment hold expiry skipped booking {BookingId}: it changed meanwhile")]
    private static partial void LogSkipped(ILogger logger, Guid bookingId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Payment hold expiry check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception error);
}
