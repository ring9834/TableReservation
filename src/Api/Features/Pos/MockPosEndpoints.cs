using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Pipeline;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Persistence;
using Reservation.Infrastructure.Pos;

namespace Reservation.Api.Features.Pos;

/// <param name="Online">True to accept messages again, false to answer "unavailable" to every send.</param>
internal sealed record SetMockPosOnlineRequest(bool? Online);

internal sealed class SetMockPosOnlineRequestValidator : AbstractValidator<SetMockPosOnlineRequest>
{
    public SetMockPosOnlineRequestValidator() => RuleFor(x => x.Online).NotNull().WithErrorCode("online_required");
}

/// <param name="TableId">The table whose bill was paid.</param>
/// <param name="TotalCents">The bill total in cents (GST inclusive).</param>
internal sealed record SimulateBillPaidRequest(Guid TableId, long TotalCents);

internal sealed class SimulateBillPaidRequestValidator : AbstractValidator<SimulateBillPaidRequest>
{
    /// <summary>NZ$100,000.00: far above any table's bill.</summary>
    public const long MaxTotalCents = 10_000_000;

    public SimulateBillPaidRequestValidator()
    {
        RuleFor(x => x.TableId).NotEmpty().WithErrorCode("table_required");
        RuleFor(x => x.TotalCents).InclusiveBetween(1, MaxTotalCents).WithErrorCode("total_cents_out_of_range");
    }
}

/// <param name="PosMessageId">The inbound <c>pos_messages</c> row the bill was stored as.</param>
/// <param name="Status"><c>matched</c> or <c>unmatched</c>.</param>
/// <param name="BookingId">The booking the bill was matched to.</param>
/// <param name="UnmatchedReason"><c>no_match</c>, <c>ambiguous</c> or <c>second_bill</c> when unmatched.</param>
internal sealed record SimulateBillPaidResponse(Guid PosMessageId, PosMessageStatus Status, Guid? BookingId, string? UnmatchedReason);

/// <summary>
/// The demo's mock POS controls (Design §6.7, §14, §17), mapped only when <c>Demo:Enabled</c>:
/// <list type="bullet">
/// <item><c>PUT /demo/pos/online {online}</c> (Presenter, <c>setMockPosOnline</c>): stores the switch; back online, the
/// waiting messages are due at once and the delivery worker wakes. 204.</item>
/// <item><c>POST /demo/pos/bill-paid {tableId, totalCents}</c> (Host or Presenter, <c>simulateBillPaid</c>): the host's
/// "Bill paid" (AC6.6). The mock builds the signed <c>BillClosed</c> its POS would send, echoing the booking it opened on
/// the table if any, and hands it to <see cref="PosWebhookProcessor"/>: the same signature check, inbox and CloseBill as
/// <c>POST /webhooks/pos</c>, never a shortcut. The route has no venue, so the table must belong to a venue in the
/// caller's session (else 404). 200 with what the bill matched.</item>
/// </list>
/// </summary>
internal sealed class MockPosEndpoints : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        if (!api.ServiceProvider.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        {
            return;
        }

        api.MapPut("/demo/pos/online", SetOnlineAsync)
            .WithName("setMockPosOnline")
            .WithTags("Demo")
            .RequireAuthorization(Policies.Presenter)
            .WithValidation<SetMockPosOnlineRequest>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesApiProblem(StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden);

        api.MapPost("/demo/pos/bill-paid", BillPaidAsync)
            .WithName("simulateBillPaid")
            .WithTags("Demo")
            .RequireAuthorization(Policies.Host)
            .WithValidation<SimulateBillPaidRequest>()
            .Produces<SimulateBillPaidResponse>()
            .ProducesApiProblem(
                StatusCodes.Status400BadRequest, StatusCodes.Status401Unauthorized, StatusCodes.Status403Forbidden, StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> SetOnlineAsync(
        SetMockPosOnlineRequest request, MockPosAdapter mock, PosDeliveryWorker worker, CancellationToken cancellationToken)
    {
        var online = request.Online!.Value;
        await mock.SetOnlineAsync(online, cancellationToken);
        if (online)
        {
            await worker.RetryNowAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private static async Task<IResult> BillPaidAsync(
        SimulateBillPaidRequest request,
        IUserContext user,
        ReservationsDbContext db,
        MockPosAdapter mock,
        PosWebhookProcessor processor,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var tableId = new TableId(request.TableId);
        var tableVenue = (await db.Database
            .SqlQuery<Guid>($"""SELECT venue_id AS "Value" FROM tables WHERE id = {request.TableId}""")
            .ToListAsync(cancellationToken)).Select(id => (VenueId?)new VenueId(id)).SingleOrDefault();
        if (tableVenue is not { } venue || !user.VenueIds.Contains(venue))
        {
            return ApiProblems.Result(StatusCodes.Status404NotFound, ErrorCodes.NotFound);
        }

        var simulated = await mock.SimulateBillClosedAsync(venue, tableId, request.TotalCents, clock.GetUtcNow(), cancellationToken);
        var processed = await processor.ProcessAsync(simulated.Body, simulated.Signature, cancellationToken);
        if (processed.Result is not { PosMessageId: { } posMessageId } result)
        {
            throw new InvalidOperationException($"The simulated bill was rejected: {processed.Rejection}.");
        }

        await mock.CloseTableAsync(tableId, cancellationToken);
        var status = result.Outcome == PosInboundOutcome.Matched ? PosMessageStatus.Matched : PosMessageStatus.Unmatched;
        return TypedResults.Ok(new SimulateBillPaidResponse(posMessageId, status, result.BookingId, result.UnmatchedReason));
    }
}
