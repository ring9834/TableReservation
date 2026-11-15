using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.GuestLinks;
using Reservation.Api.Messaging;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Reminders;

/// <summary>How many of tonight's upcoming bookings were skipped for one reason.</summary>
internal sealed record SkippedReminders(ReminderEligibility Reason, int Count);

/// <param name="Sent">Reminders queued by this press.</param>
/// <param name="Skipped">Tonight's other upcoming bookings (waiting for payment, booked or confirmed), counted by reason.</param>
internal sealed record SendRemindersResponse(int Sent, IReadOnlyList<SkippedReminders> Skipped);

internal sealed record SendRemindersNow(IReadOnlyList<VenueId> VenueIds);

/// <summary>
/// §9.2 <c>SendRemindersNow</c> (presenter, AC5.1, BR-13, Design §7.3): stands in for the pilot's 24-hour scheduler.
/// For each of tonight's upcoming bookings in the caller's venues, <see cref="ReminderPolicy.Check"/> decides; each
/// eligible booking gets <c>RecordReminder</c> with the cancellation wording from its own terms in its language, and a
/// Queued Reminder message with Confirm and Cancel links that expire at the booking start. All in one transaction
/// (Design §7, §10.4): there is no reminder event (ADR-002 folds it into <c>MessageSent</c>), and nothing is sent here;
/// the delivery worker sends after the commit. A second press finds every reminded booking <c>alreadyReminded</c>.
/// </summary>
internal sealed partial class SendRemindersNowHandler(
    ReservationsDbContext db,
    MessageComposer composer,
    GuestLinkUrls links,
    TimeProvider clock,
    [FromKeyedServices(Clocks.Infrastructure)] TimeProvider infrastructureClock,
    ILogger<SendRemindersNowHandler> logger)
    : ICommandHandler<SendRemindersNow, SendRemindersResponse>
{
    private static readonly string[] _upcoming =
        [nameof(BookingStatus.PendingPayment), nameof(BookingStatus.Booked), nameof(BookingStatus.Confirmed)];

    public async Task<Result<SendRemindersResponse>> Handle(SendRemindersNow command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var queuedAt = infrastructureClock.GetUtcNow();
        var sent = 0;
        var skipped = new Dictionary<ReminderEligibility, int>();

        foreach (var venueId in command.VenueIds)
        {
            var venue = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
                .SingleOrDefaultAsync(v => v.Id == venueId, cancellationToken);
            if (venue is null)
            {
                continue;
            }

            var timeZone = venue.FindTimeZone();
            var day = AvailabilityCalculator.LocalDate(now, timeZone);
            var from = AvailabilityCalculator.ToUtc(day, TimeOnly.MinValue, timeZone);
            var to = AvailabilityCalculator.ToUtc(day.AddDays(1), TimeOnly.MinValue, timeZone);
            var ids = (await db.Database.SqlQuery<Guid>($"""
                    SELECT id AS "Value" FROM bookings
                    WHERE venue_id = {venueId.Value} AND lower(period) >= {from} AND lower(period) < {to}
                      AND status = ANY({_upcoming})
                    """).ToListAsync(cancellationToken))
                .Select(id => new BookingId(id)).ToList();
            var bookings = await db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter])
                .Where(b => b.VenueId == venueId && ids.Contains(b.Id))
                .ToListAsync(cancellationToken);

            foreach (var booking in bookings.OrderBy(b => b.Period.Start).ThenBy(b => b.Reference.Value, StringComparer.Ordinal))
            {
                var eligibility = ReminderPolicy.Check(booking, now);
                if (eligibility != ReminderEligibility.Eligible)
                {
                    skipped[eligibility] = skipped.GetValueOrDefault(eligibility) + 1;
                    continue;
                }

                var deposit = booking.Deposit is { Status: DepositStatus.Captured } paid ? paid.Amount : (Money?)null;
                var policy = composer.CancellationPolicy(booking.Terms, deposit, booking.Language);
                var recorded = booking.RecordReminder(now, policy);
                if (recorded.IsFailure)
                {
                    return recorded.Error!;
                }

                var start = booking.Period.Start;
                var composed = composer.Compose(
                    MessagePurpose.Reminder,
                    booking.Language,
                    new TemplateModel(venue.Name, timeZone, booking.Reference.Value, booking.PartySize.Value, start, Policy: policy),
                    [
                        new LinkActionUrl(LinkAction.Confirm, links.For(booking.Id, LinkAction.Confirm, start)),
                        new LinkActionUrl(LinkAction.Cancel, links.For(booking.Id, LinkAction.Cancel, start)),
                    ]);
                db.Messages.Add(Message.Queue(
                    venueId, booking.GuestId!.Value, MessagePurpose.Reminder, MessageChannel.Sms, booking.Language, composed.Body,
                    composed.Actions, booking.Id.Value, now, queuedAt, start));
                sent++;
                LogReminder(logger, booking.Id.Value);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        LogSent(logger, sent, skipped.Values.Sum());
        return new SendRemindersResponse(sent, [.. skipped.OrderBy(s => s.Key).Select(s => new SkippedReminders(s.Key, s.Value))]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reminder queued for booking {BookingId}")]
    private static partial void LogReminder(ILogger logger, Guid bookingId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Send reminders now: {Sent} sent, {Skipped} skipped")]
    private static partial void LogSent(ILogger logger, int sent, int skipped);
}

/// <summary><c>POST /demo/reminders/send-now</c> (Presenter, <c>sendRemindersNow</c>), mapped only when <c>Demo:Enabled</c>.</summary>
internal sealed class SendRemindersNowEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        if (!api.ServiceProvider.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        {
            return;
        }

        api.MapPost("/demo/reminders/send-now", HandleAsync)
            .WithName("sendRemindersNow")
            .WithTags("Demo")
            .RequireAuthorization(Policies.Presenter)
            .Produces<SendRemindersResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> HandleAsync(IUserContext user, SendRemindersNowHandler handler, CancellationToken cancellationToken) =>
        (await handler.Handle(new SendRemindersNow(user.VenueIds), cancellationToken)).ToHttp(TypedResults.Ok);
}
