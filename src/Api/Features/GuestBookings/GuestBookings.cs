using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Common;
using Reservation.Api.GuestLinks;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.GuestBookings;

/// <param name="Token">The signed guest link token, from the link's query string.</param>
internal sealed record GuestLinkRequest(string? Token);

/// <param name="Amount">The deposit due or paid.</param>
/// <param name="Status">Pending, captured (paid), refunded or released.</param>
internal sealed record GuestDeposit(MoneyDto Amount, DepositStatus Status);

/// <summary>The booking's own cancellation terms (Design §2.6), never the venue's current settings.</summary>
/// <param name="FreeUntilHoursBefore">Cancelling at least this many hours before the start is in time.</param>
/// <param name="RefundWhenInTime">The refund for a guest who cancels in time.</param>
internal sealed record GuestCancellationTerms(int FreeUntilHoursBefore, RefundAmount RefundWhenInTime);

/// <summary>What the guest's manage page shows (Design §7.3). Nothing personal beyond the booking itself.</summary>
/// <param name="StartsAt">UTC.</param>
/// <param name="LocalDate">The start's date in the venue's zone.</param>
/// <param name="LocalTime">The start's time in the venue's zone.</param>
/// <param name="FreeCancellationUntil">UTC: cancelling before this is in time (start minus the terms' hours).</param>
/// <param name="CancellingNowKeepsDeposit">BR-13 warning: a paid deposit would be kept if the guest cancelled now.</param>
/// <param name="RefundIfCancelledNow">What cancelling now would decide (<c>RefundPolicy</c> at this moment, nothing
/// changed); <c>null</c> when the booking can't be cancelled.</param>
/// <param name="CancellationRefund">The decision made when the booking was cancelled; <c>null</c> unless cancelled.</param>
/// <param name="LinkAction">What the link was made for: <c>manage</c>, <c>confirm</c> or <c>cancel</c>.</param>
/// <param name="AllowedActions">What the guest can do now with this link: <c>confirm</c> and/or <c>cancel</c>.</param>
internal sealed record GuestBookingResponse(
    string Reference,
    string VenueName,
    string TimeZone,
    DateTime StartsAt,
    DateOnly LocalDate,
    TimeOnly LocalTime,
    int PartySize,
    BookingStatus Status,
    GuestDeposit? Deposit,
    GuestCancellationTerms Terms,
    DateTime FreeCancellationUntil,
    bool CancellingNowKeepsDeposit,
    RefundDecision? RefundIfCancelledNow,
    RefundDecision? CancellationRefund,
    LinkAction LinkAction,
    IReadOnlyList<LinkAction> AllowedActions);

/// <param name="Status">The booking's status after the action.</param>
/// <param name="RefundDecision">Set once the booking is cancelled (§8.3): <c>full</c>, <c>none</c> (deposit kept) or
/// <c>notApplicable</c> (nothing paid).</param>
internal sealed record GuestActionResponse(BookingStatus Status, RefundDecision? RefundDecision);

/// <summary>
/// The guest-link endpoints (Design §7, §7.3, §9.2, §14). Opening a link only shows the booking, so link scanners and
/// previews change nothing; confirm and cancel are POSTs that verify the token again. A Confirm link can only
/// confirm; Cancel and Manage links can only cancel. The change goes through the same <see cref="Booking.Confirm"/> and
/// <see cref="Booking.Cancel"/> the host uses, with the guest as the actor, so the reminder's response is recorded on
/// its <c>ReminderRecord</c> (AC5.6) and the refund follows BR-07 (§8.3). Repeats are no-ops returning success (§3.1).
/// The token is never logged: log lines carry the booking ID, the action and the outcome only.
/// </summary>
internal sealed partial class GuestBookingHandler(
    ReservationsDbContext db, GuestLinkSigner signer, TimeProvider clock, ILogger<GuestBookingHandler> logger)
{
    public async Task<Result<GuestBookingResponse>> GetAsync(string? token, CancellationToken cancellationToken)
    {
        var link = signer.Verify(token);
        if (link.IsFailure)
        {
            return link.Error!;
        }

        var booking = await Bookings().AsNoTracking().SingleOrDefaultAsync(b => b.Id == link.Value.BookingId, cancellationToken);
        if (booking is null)
        {
            return GuestLinkSigner.InvalidLink;
        }

        var venue = await db.Venues.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]).AsNoTracking()
            .SingleAsync(v => v.Id == booking.VenueId, cancellationToken);
        var now = clock.GetUtcNow();
        var timeZone = venue.FindTimeZone();
        var local = TimeZoneInfo.ConvertTime(booking.Period.Start, timeZone);
        var terms = booking.Terms.Cancellation;
        var cancellable = CanCancel(booking);

        List<LinkAction> allowed = [];
        if (link.Value.Action == LinkAction.Confirm && booking.Status == BookingStatus.Booked && now < booking.Period.Start)
        {
            allowed.Add(LinkAction.Confirm);
        }

        if (link.Value.Action is LinkAction.Cancel or LinkAction.Manage && cancellable)
        {
            allowed.Add(LinkAction.Cancel);
        }

        return new GuestBookingResponse(
            booking.Reference.Value,
            venue.Name,
            venue.TimeZone,
            booking.Period.Start.UtcDateTime,
            DateOnly.FromDateTime(local.DateTime),
            TimeOnly.FromDateTime(local.DateTime),
            booking.PartySize.Value,
            booking.Status,
            booking.Deposit is { } deposit ? new GuestDeposit(MoneyDto.From(deposit.Amount), deposit.Status) : null,
            new GuestCancellationTerms(terms.FreeUntilHoursBefore, terms.RefundWhenInTime),
            booking.Period.Start.AddHours(-terms.FreeUntilHoursBefore).UtcDateTime,
            booking.CancellingNowKeepsDeposit(now),
            cancellable ? RefundPolicy.Decide(booking, CancelReason.GuestRequest, Actor.ForGuest(booking.GuestId), now) : null,
            booking.Status == BookingStatus.Cancelled ? booking.CancellationRefund : null,
            link.Value.Action,
            allowed);
    }

    public Task<Result<GuestActionResponse>> ConfirmAsync(string? token, CancellationToken cancellationToken) =>
        ChangeAsync(token, LinkAction.Confirm, BookingStatus.Confirmed, (b, now) => b.Confirm(Actor.ForGuest(b.GuestId), now), cancellationToken);

    public Task<Result<GuestActionResponse>> CancelAsync(string? token, CancellationToken cancellationToken) =>
        ChangeAsync(
            token, LinkAction.Cancel, BookingStatus.Cancelled,
            (b, now) => b.Cancel(Actor.ForGuest(b.GuestId), CancelReason.GuestRequest, now), cancellationToken);

    private static bool CanCancel(Booking booking) =>
        booking.Status is BookingStatus.PendingPayment or BookingStatus.Booked or BookingStatus.Confirmed;

    private static bool Allows(LinkAction linkAction, LinkAction wanted) => wanted switch
    {
        LinkAction.Confirm => linkAction == LinkAction.Confirm,
        LinkAction.Cancel => linkAction is LinkAction.Cancel or LinkAction.Manage,
        _ => false,
    };

    private IQueryable<Booking> Bookings() => db.Bookings.IgnoreQueryFilters([ReservationsDbContext.VenueFilter]);

    private async Task<Result<GuestActionResponse>> ChangeAsync(
        string? token, LinkAction wanted, BookingStatus target, Func<Booking, DateTimeOffset, Result> change, CancellationToken cancellationToken)
    {
        var link = signer.Verify(token);
        if (link.IsFailure)
        {
            return link.Error!;
        }

        if (!Allows(link.Value.Action, wanted))
        {
            LogRefused(logger, link.Value.BookingId.Value, wanted, ProblemCodes.InvalidLink);
            return GuestLinkSigner.InvalidLink;
        }

        // One retry when the booking changed between the read and the save (xmin), e.g. a host tap at the same moment.
        for (var attempt = 1; ; attempt++)
        {
            var booking = await Bookings().SingleOrDefaultAsync(b => b.Id == link.Value.BookingId, cancellationToken);
            if (booking is null)
            {
                return GuestLinkSigner.InvalidLink;
            }

            if (booking.Status != target)
            {
                var result = change(booking, clock.GetUtcNow());
                if (result.IsFailure)
                {
                    LogRefused(logger, booking.Id.Value, wanted, result.Error!.Code);
                    return result.Error!;
                }

                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateConcurrencyException) when (attempt == 1)
                {
                    db.ChangeTracker.Clear();
                    continue;
                }

                LogChanged(logger, booking.Id.Value, wanted, booking.Status);
            }

            return new GuestActionResponse(booking.Status, booking.CancellationRefund);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Guest link {Action} on booking {BookingId}: {Status}")]
    private static partial void LogChanged(ILogger logger, Guid bookingId, LinkAction action, BookingStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Guest link {Action} on booking {BookingId} refused: {Code}")]
    private static partial void LogRefused(ILogger logger, Guid bookingId, LinkAction action, string code);
}

/// <summary>
/// <c>GET /guest/bookings?token</c>, <c>POST /guest/bookings/confirm {token}</c> and <c>POST /guest/bookings/cancel
/// {token}</c> (anonymous, under the anonymous rate limit). Responses carry <c>Referrer-Policy: no-referrer</c> and
/// <c>Cache-Control: no-store</c> (<see cref="GuestLinkHeadersMiddleware"/>).
/// </summary>
internal sealed class GuestBookingEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/guest/bookings", GetAsync)
            .WithName("getGuestBooking")
            .WithTags("GuestBookings")
            .AllowAnonymous()
            .Produces<GuestBookingResponse>()
            .ProducesApiProblem(StatusCodes.Status400BadRequest, StatusCodes.Status410Gone, StatusCodes.Status429TooManyRequests);

        api.MapPost("/guest/bookings/confirm", ConfirmAsync)
            .WithName("confirmGuestBooking")
            .WithTags("GuestBookings")
            .AllowAnonymous()
            .Produces<GuestActionResponse>()
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status410Gone, StatusCodes.Status429TooManyRequests);

        api.MapPost("/guest/bookings/cancel", CancelAsync)
            .WithName("cancelGuestBooking")
            .WithTags("GuestBookings")
            .AllowAnonymous()
            .Produces<GuestActionResponse>()
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, StatusCodes.Status410Gone, StatusCodes.Status429TooManyRequests);
    }

    // The token is bound as a plain string, so binding never fails with its value in an exception message.
    private static async Task<IResult> GetAsync([FromQuery] string? token, GuestBookingHandler handler, CancellationToken cancellationToken) =>
        (await handler.GetAsync(token, cancellationToken)).ToHttp(TypedResults.Ok);

    private static async Task<IResult> ConfirmAsync(GuestLinkRequest request, GuestBookingHandler handler, CancellationToken cancellationToken) =>
        (await handler.ConfirmAsync(request.Token, cancellationToken)).ToHttp(TypedResults.Ok);

    private static async Task<IResult> CancelAsync(GuestLinkRequest request, GuestBookingHandler handler, CancellationToken cancellationToken) =>
        (await handler.CancelAsync(request.Token, cancellationToken)).ToHttp(TypedResults.Ok);
}
