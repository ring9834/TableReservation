using System.Net;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Common;
using Reservation.Infrastructure.Demo;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// Design §12 through the API: two requests for the last table (the pacing lock or the exclusion constraint decides)
/// and Idempotency-Key on create (ADR-004).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConcurrencyAndIdempotencyTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly string Path = Venue + "/bookings";

    [Theory]
    [InlineData(19, 0)] // same slot: the second waits at the advisory lock, then finds no room
    [InlineData(19, 15)] // overlapping slot, different lock: the exclusion constraint refuses the second (23P01)
    public async Task Two_simultaneous_requests_for_the_last_table_one_wins_one_is_slot_taken(int secondHour, int secondMinute)
    {
        // T10 is the only table for 8.
        using var other = NewClient();
        var responses = await Task.WhenAll(
            PostAsync(Client, Path, BookingBody(Tomorrow, 19, 0, 8, mobile: "021 011 0181")),
            PostAsync(other, Path, BookingBody(Tomorrow, secondHour, secondMinute, 8, mobile: "021 011 0182")));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        await ProblemAssert.IsAsync(responses.Single(r => r.StatusCode != HttpStatusCode.Created), HttpStatusCode.Conflict, "slot_taken");
        Assert.Equal(1, await CountTomorrowBookingsAsync());
    }

    [Fact]
    public async Task The_same_key_twice_returns_the_first_result_with_the_same_version_and_one_booking()
    {
        var body = BookingBody(Tomorrow, 19, 0, 2);

        var first = await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Path, body, "key-1"), HttpStatusCode.Created);
        var second = await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Path, body, "key-1"), HttpStatusCode.Created);

        Assert.Equal(first.BookingId, second.BookingId);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(first.TableIds, second.TableIds);
        Assert.Equal(1, await CountTomorrowBookingsAsync());
    }

    [Fact]
    public async Task The_same_key_at_the_same_time_creates_one_booking()
    {
        var body = BookingBody(Tomorrow, 19, 0, 2);
        using var other = NewClient();

        var responses = await Task.WhenAll(PostAsync(Client, Path, body, "key-2"), PostAsync(other, Path, body, "key-2"));

        var results = await Task.WhenAll(responses.Select(r => ReadAsync<CreateBookingResponse>(r, HttpStatusCode.Created)));
        Assert.Equal(results[0].BookingId, results[1].BookingId);
        Assert.Equal(results[0].Version, results[1].Version);
        Assert.Equal(1, await CountTomorrowBookingsAsync());
    }

    [Fact]
    public async Task The_same_key_with_another_body_is_idempotency_key_reused()
    {
        await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Path, BookingBody(Tomorrow, 19, 0, 2), "key-3"), HttpStatusCode.Created);

        var response = await PostAsync(Client, Path, BookingBody(Tomorrow, 19, 0, 4), "key-3");

        await ProblemAssert.IsAsync(response, HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
        Assert.Equal(1, await CountTomorrowBookingsAsync());
    }

    [Fact]
    public async Task A_failed_request_keeps_no_key_so_a_corrected_retry_runs()
    {
        var full = await PostAsync(Client, Path, BookingBody(Tomorrow, 19, 0, 9), "key-4"); // party_too_large
        await ProblemAssert.IsAsync(full, HttpStatusCode.UnprocessableEntity, "party_too_large");

        await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Path, BookingBody(Tomorrow, 19, 0, 2), "key-4"), HttpStatusCode.Created);

        await using var db = Database.Context();
        var key = Assert.Single(await db.IdempotencyKeys.ToListAsync(Ct));
        Assert.Equal(201, key.StatusCode);
        Assert.Equal(GoldenDragon.VenueId, key.VenueId);
        Assert.Equal("create-online-booking", key.Operation);
    }

    [Fact]
    public async Task An_invalid_key_is_validation_failed()
    {
        var response = await PostAsync(Client, Path, BookingBody(Tomorrow, 19, 0, 2), new string('k', 101));

        await ProblemAssert.IsValidationAsync(response, "idempotencyKey", "idempotency_key_invalid");
    }

    private async Task<int> CountTomorrowBookingsAsync()
    {
        await using var db = Database.Context();
        var from = GoldenDragon.At(Tomorrow, TimeOnly.MinValue);
        var to = GoldenDragon.At(Tomorrow.AddDays(1), TimeOnly.MinValue);
        return await db.Bookings
            .FromSql($"SELECT * FROM bookings WHERE lower(period) >= {from} AND lower(period) < {to}")
            .CountAsync(Ct);
    }
}
