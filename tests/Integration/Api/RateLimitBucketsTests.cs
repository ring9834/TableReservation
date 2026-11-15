using System.Net;
using Reservation.Api.Pipeline;
using Reservation.Domain.Bookings;
using Reservation.Domain.Messaging;
using Reservation.Tests.Integration.GuestLinks;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// Anonymous traffic in separate buckets (M10b, Design §18.10), with the default limits: on venue Wi-Fi the presenter
/// laptop, a guest phone and the host tablet share one IP, so one guest's payment poll or link must not use up another's
/// allowance. All requests in these tests come from the same (test server) IP.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RateLimitBucketsTests(PostgresFixture postgres) : GuestLinkTestBase(postgres)
{
    private const int StatusPerMinute = 60;
    private const int LinkPerMinute = 30;

    private Task<HttpResponseMessage> StatusAsync(Guid bookingId) =>
        NewClient().GetAsync($"{Venue}/bookings/{bookingId}/status", Ct);

    [Fact]
    public async Task Two_minutes_of_one_bookings_status_poll_leave_other_bookings_and_a_guest_cancel_alone()
    {
        await SendRemindersAsync();
        var polled = await ByReferenceAsync(CancelsRef);
        var other = await ByReferenceAsync(ConfirmsRef);

        // A poll every 2 s for 2 minutes is 60 requests; sent at once they all land in one window, the worst case.
        for (var i = 0; i < StatusPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await StatusAsync(polled.Id.Value)).StatusCode);
        }

        // That booking has reached its own limit…
        await ProblemAssert.IsAsync(await StatusAsync(polled.Id.Value), HttpStatusCode.TooManyRequests, "too_many_requests");

        // …and nobody else has: another booking's poll, the guest's link (open and cancel) and an ordinary read.
        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(other.Id.Value)).StatusCode);
        var cancel = await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel");
        Assert.Equal(HttpStatusCode.OK, (await GetGuestAsync(cancel)).StatusCode);
        Assert.Equal(BookingStatus.Cancelled, (await ActAsync("cancel", cancel)).Status);
        Assert.Equal(
            HttpStatusCode.OK,
            (await NewClient().GetAsync($"{Venue}/availability?date={DemoTime.Tonight:yyyy-MM-dd}&partySize=2", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_single_link_hits_its_own_limit_whether_opened_or_posted_and_other_links_still_work()
    {
        await SendRemindersAsync();
        var confirm = await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "confirm");
        var cancel = await TokenAsync(CancelsRef, MessagePurpose.Reminder, "cancel");

        for (var i = 0; i < LinkPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await GetGuestAsync(confirm)).StatusCode);
        }

        await ProblemAssert.IsAsync(await GetGuestAsync(confirm), HttpStatusCode.TooManyRequests, "too_many_requests");
        // The POST carries the token in its body; it is the same link, so the same bucket.
        await ProblemAssert.IsAsync(await PostGuestAsync("confirm", confirm), HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.Equal(BookingStatus.Booked, (await ByReferenceAsync(ConfirmsRef)).Status);

        // Another link, from the same IP, is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await GetGuestAsync(cancel)).StatusCode);
        Assert.Equal(BookingStatus.Cancelled, (await ActAsync("cancel", cancel)).Status);
    }

    [Fact]
    public async Task Other_anonymous_reads_share_the_per_ip_bucket_of_300_a_minute()
    {
        // Signing in is anonymous too, so get the presenter's links before using up the per-IP bucket.
        await SendRemindersAsync();
        var confirm = await TokenAsync(ConfirmsRef, MessagePurpose.Reminder, "confirm");
        var polled = (await ByReferenceAsync(CancelsRef)).Id.Value;
        var path = $"{Venue}/availability?date={DemoTime.Tonight:yyyy-MM-dd}&partySize=2";
        for (var i = 0; i < 299; i++) // the presenter's sign-in was the 1st of the 300
        {
            Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(path, Ct)).StatusCode);
        }

        var response = await Client.GetAsync(path, Ct);

        await ProblemAssert.IsAsync(response, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.NotNull(response.Headers.RetryAfter);

        // The guest-link and status buckets are separate from it.
        Assert.Equal(HttpStatusCode.OK, (await GetGuestAsync(confirm)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await StatusAsync(polled)).StatusCode);
    }

    [Fact]
    public async Task Online_booking_stays_at_10_a_minute_per_ip()
    {
        // The limit counts attempts, whatever their answer.
        for (var i = 0; i < 10; i++)
        {
            var response = await PostAsync(
                NewClient(), $"{Venue}/bookings", BookingBody(Tomorrow, 18 + (i / 2), i % 2 * 30, 2, mobile: $"021 013 00{i:00}"));
            Assert.NotEqual(HttpStatusCode.TooManyRequests, response.StatusCode);
        }

        await ProblemAssert.IsAsync(
            await PostAsync(NewClient(), $"{Venue}/bookings", BookingBody(Tomorrow, 21, 0, 2, mobile: "021 013 0099")),
            HttpStatusCode.TooManyRequests,
            "too_many_requests");
    }

    [Fact]
    public void The_link_key_is_a_hash_never_the_token()
    {
        const string token = "AQExAZOn0AAAcAAAgAAAAAAAAAoC.Zm9vYmFyYmF6cXV4";

        var key = GuestLinkRateKeyMiddleware.Hash(token);

        Assert.Equal(32, key.Length);
        Assert.Equal(key, GuestLinkRateKeyMiddleware.Hash(token));
        Assert.NotEqual(key, GuestLinkRateKeyMiddleware.Hash(token + "x"));
        Assert.DoesNotContain(token[..8], key, StringComparison.Ordinal);
    }
}

/// <summary>
/// The per-IP ceiling over all anonymous traffic (Design §18.10): made-up tokens each get their own link bucket, but one
/// IP still can't send more than the ceiling. Lowered here so the test stays quick.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RateLimitCeilingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["RateLimits:AnonymousCeilingPerMinute"] = "20",
    };

    [Fact]
    public async Task Spraying_made_up_tokens_from_one_ip_stops_at_the_ceiling()
    {
        for (var i = 0; i < 20; i++)
        {
            await ProblemAssert.IsAsync(
                await Client.GetAsync($"/api/v1/guest/bookings?token=made-up-{i}", Ct), HttpStatusCode.BadRequest, "invalid_link");
        }

        await ProblemAssert.IsAsync(
            await Client.GetAsync("/api/v1/guest/bookings?token=made-up-20", Ct), HttpStatusCode.TooManyRequests, "too_many_requests");
    }
}
