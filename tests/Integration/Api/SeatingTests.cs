using System.Net;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Seating;
using Reservation.Api.Features.Venues;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// Walk-ins, seating a booked guest and clearing a table (Design §9.2, §11.5, §14.2; Requirements step 3, AC3.1–AC3.3,
/// AC4.1) on the seed at 18:25 (Design §17).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SeatingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string SeatedOnT4 = "GD2A4K"; // #1, party 4, seated 17:30 on T4
    private const string ArrivingOnT5 = "GD7F3R"; // #6, party 4, 18:45, confirmed on T5
    private const string BookedOnT6 = "GDA2J6"; // #9, party 4, 19:15 on T6
    private const string LateOnW1 = "GDB3K7"; // #10, party 2, 20:30 on W1

    private static readonly string Suggestion = $"{Venue}/tables/suggestion";
    private static readonly string WalkIns = $"{Venue}/walk-ins";

    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        ["RateLimits:AnonymousPerMinute"] = "1000",
        ["RateLimits:BookingPerMinute"] = "1000",
    };

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await SignInAsync(Client);
    }

    // ---------- Suggestion (AC3.1, AC3.2) ----------

    [Fact]
    public async Task Step_3_a_walk_in_of_2_is_offered_T1_and_the_other_free_2_seat_tables()
    {
        var suggestion = await ReadAsync<TableSuggestionResponse>(await Client.GetAsync($"{Suggestion}?partySize=2", Ct));

        Assert.Equal(75, suggestion.TurnMinutes);
        Assert.Equal("T1", suggestion.Table!.Name);
        Assert.Equal(["T2", "T3", "W1", "W2"], suggestion.Alternatives.Select(t => t.Name));
    }

    [Fact]
    public async Task Step_3_a_walk_in_of_4_finds_no_table()
    {
        var suggestion = await ReadAsync<TableSuggestionResponse>(await Client.GetAsync($"{Suggestion}?partySize=4", Ct));

        Assert.Null(suggestion.Table);
        Assert.Empty(suggestion.Alternatives);
    }

    [Theory]
    [InlineData("", "party_size_required")]
    [InlineData("?partySize=0", "party_size_out_of_range")]
    [InlineData("?partySize=21", "party_size_out_of_range")]
    public async Task A_suggestion_needs_a_party_size_of_1_to_20(string query, string code)
    {
        await ProblemAssert.IsValidationAsync(await Client.GetAsync(Suggestion + query, Ct), "partySize", code);
    }

    [Fact]
    public async Task Seating_endpoints_need_a_host_session()
    {
        using var guest = NewClient();

        await ProblemAssert.IsAsync(await guest.GetAsync($"{Suggestion}?partySize=2", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await ProblemAssert.IsAsync(await PostAsync(guest, WalkIns, WalkIn(2, "T1")), HttpStatusCode.Unauthorized, "not_signed_in");
        await ProblemAssert.IsAsync(
            await PostAsync(guest, $"{Venue}/tables/{Table("T4")}/clear", new { bookingId = Guid.CreateVersion7(), version = 1 }),
            HttpStatusCode.Unauthorized, "not_signed_in");
    }

    // ---------- Walk-in (AC3.1, AC3.2) ----------

    [Fact]
    public async Task Step_3_a_walk_in_of_2_is_seated_at_T1_for_75_minutes()
    {
        var created = await ReadAsync<SeatWalkInResponse>(
            await PostAsync(Client, WalkIns, WalkIn(2, "T1", name: "Walk-in Wu")), HttpStatusCode.Created);

        Assert.Equal(BookingStatus.Seated, created.Status);
        Assert.Equal([Table("T1")], created.TableIds);
        Assert.Equal(DemoTime.Now.UtcDateTime, created.StartsAt);
        Assert.Equal(DemoTime.At(19, 40).UtcDateTime, created.EndsAt);
        Assert.Equal("Walk-in Wu", created.GuestName);
        Assert.Null(created.WaitlistEntryId);

        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(created.BookingId), Ct);
        Assert.Equal(BookingChannel.WalkIn, booking.Channel);
        Assert.Null(booking.GuestId);
        var events = await db.Outbox.Where(o => o.AggregateId == created.BookingId).OrderBy(o => o.Sequence).Select(o => o.EventType).ToListAsync(Ct);
        Assert.Equal(["BookingCreated", "BookingSeated"], events);

        var item = await ItemAsync(created.Reference);
        Assert.Equal("Walk-in Wu", item.GuestName);
        Assert.Equal(BookingChannel.WalkIn, item.Channel);
    }

    [Fact]
    public async Task A_walk_in_without_a_name_has_no_guest_name()
    {
        var created = await ReadAsync<SeatWalkInResponse>(await PostAsync(Client, WalkIns, WalkIn(2, "T2")), HttpStatusCode.Created);

        Assert.Null(created.GuestName);
        Assert.Null((await ItemAsync(created.Reference)).GuestName);
    }

    [Fact]
    public async Task The_host_may_pick_another_free_table_AC3_2()
    {
        var created = await ReadAsync<SeatWalkInResponse>(await PostAsync(Client, WalkIns, WalkIn(2, "W2")), HttpStatusCode.Created);

        Assert.Equal([Table("W2")], created.TableIds);
    }

    [Fact]
    public async Task A_walk_in_on_a_taken_table_is_slot_taken()
    {
        await ProblemAssert.IsAsync(await PostAsync(Client, WalkIns, WalkIn(4, "T4")), HttpStatusCode.Conflict, "slot_taken"); // seated
        await ProblemAssert.IsAsync(await PostAsync(Client, WalkIns, WalkIn(4, "T5")), HttpStatusCode.Conflict, "slot_taken"); // booked 18:45
    }

    [Fact]
    public async Task A_walk_in_on_a_table_too_small_is_table_too_small()
    {
        await ProblemAssert.IsAsync(await PostAsync(Client, WalkIns, WalkIn(3, "T1")), HttpStatusCode.UnprocessableEntity, "table_too_small");
    }

    [Fact]
    public async Task A_walk_in_on_an_unknown_table_is_not_found()
    {
        await ProblemAssert.IsAsync(
            await PostAsync(Client, WalkIns, new { partySize = 2, tableId = Guid.CreateVersion7() }), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A_walk_in_is_validated()
    {
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, WalkIns, WalkIn(0, "T1")), "partySize", "party_size_out_of_range");
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, WalkIns, new { partySize = 2 }), "tableId", "table_required");
        await ProblemAssert.IsValidationAsync(
            await PostAsync(Client, WalkIns, WalkIn(2, "T1", name: new string('x', 81))), "name", "name_length");
        await ProblemAssert.IsValidationAsync(
            await PostAsync(Client, WalkIns, new { partySize = 2, tableId = Table("T1"), waitlistEntryId = Guid.CreateVersion7() }),
            "waitlistEntryVersion", "waitlist_entry_version_required");
    }

    [Fact]
    public async Task The_same_idempotency_key_seats_one_walk_in()
    {
        var first = await ReadAsync<SeatWalkInResponse>(await PostAsync(Client, WalkIns, WalkIn(2, "T3"), "walk-in-key"), HttpStatusCode.Created);
        var second = await ReadAsync<SeatWalkInResponse>(await PostAsync(Client, WalkIns, WalkIn(2, "T3"), "walk-in-key"), HttpStatusCode.Created);

        Assert.Equal(first.BookingId, second.BookingId);
        await using var db = Database.Context();
        Assert.Equal(1, await db.Bookings.CountAsync(b => b.Channel == BookingChannel.WalkIn, Ct));
        await ProblemAssert.IsAsync(
            await PostAsync(Client, WalkIns, WalkIn(2, "W2"), "walk-in-key"), HttpStatusCode.UnprocessableEntity, "idempotency_key_reused");
    }

    // ---------- Seat a booked guest (AC3.3) ----------

    [Fact]
    public async Task Seating_the_18_45_booking_early_on_its_own_table_moves_its_period_in_place()
    {
        var item = await ItemAsync(ArrivingOnT5);

        var seated = await ReadAsync<BookingStateResponse>(await PostAsync(Client, Seat(item), new { version = item.Version }));

        Assert.Equal(BookingStatus.Seated, seated.Status);
        Assert.NotEqual(item.Version, seated.Version);
        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(item.Id), Ct);
        var assignment = Assert.Single(booking.Tables);
        Assert.Equal(GoldenDragon.TableId("T5"), assignment.TableId);
        Assert.Equal(new TimeRange(DemoTime.Now, DemoTime.At(19, 55)), assignment.Period);
        Assert.Equal(DemoTime.Now, booking.SeatedAt);
        var events = await db.Outbox.Where(o => o.AggregateId == item.Id).Select(o => o.EventType).ToListAsync(Ct);
        Assert.Equal(["BookingSeated"], events);
    }

    [Fact]
    public async Task Seating_a_booking_at_a_different_free_table_moves_it_there()
    {
        var item = await ItemAsync(LateOnW1);

        var seated = await ReadAsync<BookingStateResponse>(
            await PostAsync(Client, Seat(item), new { version = item.Version, tableIds = new[] { Table("T1") } }));

        Assert.Equal(BookingStatus.Seated, seated.Status);
        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(item.Id), Ct);
        var assignment = Assert.Single(booking.Tables);
        Assert.Equal(GoldenDragon.TableId("T1"), assignment.TableId);
        Assert.Equal(new TimeRange(DemoTime.Now, DemoTime.At(19, 40)), assignment.Period);
        Assert.Equal(0, await db.Database.SqlQuery<int>(
            $"SELECT count(*)::int AS \"Value\" FROM table_assignments WHERE booking_id = {item.Id} AND table_id = {GoldenDragon.TableId("W1").Value}")
            .SingleAsync(Ct));
    }

    [Fact]
    public async Task Seating_a_booking_already_seated_returns_its_state()
    {
        var item = await ItemAsync(SeatedOnT4);

        var seated = await ReadAsync<BookingStateResponse>(await PostAsync(Client, Seat(item), new { version = 999 }));

        Assert.Equal(BookingStatus.Seated, seated.Status);
        Assert.Equal(item.Version, seated.Version);
    }

    [Fact]
    public async Task Seating_on_a_table_with_another_booking_within_the_turn_is_slot_taken()
    {
        var item = await ItemAsync(BookedOnT6); // party 4 for 90 minutes; T5 has #6 at 18:45

        var response = await PostAsync(Client, Seat(item), new { version = item.Version, tableIds = new[] { Table("T5") } });

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "slot_taken");
    }

    [Fact]
    public async Task Seating_on_a_table_too_small_unknown_or_empty_is_refused()
    {
        var item = await ItemAsync(BookedOnT6);

        await ProblemAssert.IsAsync(
            await PostAsync(Client, Seat(item), new { version = item.Version, tableIds = new[] { Table("T1") } }),
            HttpStatusCode.UnprocessableEntity, "table_too_small");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, Seat(item), new { version = item.Version, tableIds = new[] { Guid.CreateVersion7() } }),
            HttpStatusCode.NotFound, "not_found");
        await ProblemAssert.IsValidationAsync(
            await PostAsync(Client, Seat(item), new { version = item.Version, tableIds = Array.Empty<Guid>() }), "tableIds", "tables_required");
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, Seat(item), new { }), "version", "version_required");
    }

    [Fact]
    public async Task Seating_with_a_stale_version_is_version_conflict()
    {
        var item = await ItemAsync(ArrivingOnT5);

        await ProblemAssert.IsAsync(
            await PostAsync(Client, Seat(item), new { version = item.Version + 1 }), HttpStatusCode.Conflict, "version_conflict");
    }

    [Fact]
    public async Task Seating_a_cancelled_booking_is_invalid_transition()
    {
        var item = await ItemAsync(BookedOnT6);
        var cancelled = await ReadAsync<BookingStateResponse>(await PostAsync(Client, $"{Venue}/bookings/{item.Id}/cancel", new { version = item.Version }));

        var response = await PostAsync(Client, Seat(item), new { version = cancelled.Version });

        await ProblemAssert.IsAsync(response, HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task Seating_an_unknown_booking_is_not_found()
    {
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Venue}/bookings/{Guid.CreateVersion7()}/seat", new { version = 1 }), HttpStatusCode.NotFound, "not_found");
    }

    // ---------- Clear (AC4.1) ----------

    [Fact]
    public async Task Clearing_T4_completes_its_booking_as_cleared_by_host_and_a_retry_is_safe()
    {
        var current = (await FloorPlanTableAsync("T4")).Current!;

        var cleared = await ReadAsync<BookingStateResponse>(await PostAsync(Client, Clear("T4"), new { current.BookingId, current.Version }));
        var retried = await ReadAsync<BookingStateResponse>(await PostAsync(Client, Clear("T4"), new { current.BookingId, current.Version }));

        Assert.Equal(BookingStatus.Completed, cleared.Status);
        Assert.Equal(cleared, retried);
        await using var db = Database.Context();
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(current.BookingId), Ct);
        Assert.Equal(CompletionSource.ClearedByHost, booking.CompletedBy);
        Assert.All(booking.Tables, t => Assert.False(t.Active));
        var events = await db.Outbox.Where(o => o.AggregateId == current.BookingId).OrderBy(o => o.Sequence).Select(o => o.EventType).ToListAsync(Ct);
        Assert.Equal(["BookingCompleted", "TableCleared"], events);
        Assert.Equal(TableState.Free, (await FloorPlanTableAsync("T4")).State);
    }

    [Fact]
    public async Task Clearing_with_a_stale_version_is_version_conflict()
    {
        var current = (await FloorPlanTableAsync("T4")).Current!;

        await ProblemAssert.IsAsync(
            await PostAsync(Client, Clear("T4"), new { current.BookingId, version = current.Version + 1 }), HttpStatusCode.Conflict, "version_conflict");
    }

    [Fact]
    public async Task Clearing_a_table_the_booking_does_not_hold_is_not_found()
    {
        var current = (await FloorPlanTableAsync("T4")).Current!;

        await ProblemAssert.IsAsync(
            await PostAsync(Client, Clear("T9"), new { current.BookingId, current.Version }), HttpStatusCode.NotFound, "not_found");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, Clear("T4"), new { bookingId = Guid.CreateVersion7(), version = 1 }), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task Clearing_a_booking_that_is_not_seated_is_invalid_transition()
    {
        var item = await ItemAsync(BookedOnT6);

        await ProblemAssert.IsAsync(
            await PostAsync(Client, Clear("T6"), new { bookingId = item.Id, item.Version }), HttpStatusCode.Conflict, "invalid_transition");
    }

    [Fact]
    public async Task Clearing_is_validated()
    {
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, Clear("T4"), new { version = 1 }), "bookingId", "booking_required");
        await ProblemAssert.IsValidationAsync(
            await PostAsync(Client, Clear("T4"), new { bookingId = Guid.CreateVersion7() }), "version", "version_required");
    }

    // ---------- Race (§12) ----------

    [Fact]
    public async Task A_walk_in_and_a_booking_racing_for_one_table_give_one_success_and_one_slot_taken()
    {
        // Leave T1 as the only table a party of 2 can take at 18:30 (Design §17 seed at 18:25).
        foreach (var table in new[] { "T2", "T3", "W1", "W2" })
        {
            Assert.Equal(HttpStatusCode.Created, (await PostAsync(Client, WalkIns, WalkIn(2, table))).StatusCode);
        }

        using var other = NewClient();
        await SignInAsync(other);
        for (var round = 0; round < 20; round++)
        {
            var responses = await Task.WhenAll(
                PostAsync(Client, WalkIns, WalkIn(2, "T1")),
                PostAsync(other, $"{Venue}/phone-bookings", BookingBody(DemoTime.Tonight, 18, 30, 2, mobile: "021 011 0188")));

            var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
            var loser = Assert.Single(responses, r => r.StatusCode != HttpStatusCode.Created);
            await ProblemAssert.IsAsync(loser, HttpStatusCode.Conflict, "slot_taken");
            await FreeT1Async(winner == responses[0]);
        }
    }

    private async Task FreeT1Async(bool walkInWon)
    {
        if (walkInWon)
        {
            var current = (await FloorPlanTableAsync("T1")).Current!;
            Assert.Equal(HttpStatusCode.OK, (await PostAsync(Client, Clear("T1"), new { current.BookingId, current.Version })).StatusCode);
            return;
        }

        var next = (await FloorPlanTableAsync("T1")).Next!;
        Assert.Equal(
            HttpStatusCode.OK, (await PostAsync(Client, $"{Venue}/bookings/{next.BookingId}/cancel", new { next.Version })).StatusCode);
    }

    // ---------- Helpers ----------

    private static Guid Table(string name) => GoldenDragon.TableId(name).Value;

    private static object WalkIn(int partySize, string table, string? name = null) => new { partySize, tableId = Table(table), name };

    private static string Seat(BookingListItem item) => $"{Venue}/bookings/{item.Id}/seat";

    private static string Clear(string table) => $"{Venue}/tables/{Table(table)}/clear";

    private async Task<BookingListItem> ItemAsync(string reference)
    {
        var list = await ReadAsync<BookingListResponse>(await Client.GetAsync(Venue + "/bookings", Ct));
        return list.Items.Single(i => i.Reference == reference);
    }

    private async Task<FloorTableDto> FloorPlanTableAsync(string name) =>
        (await ReadAsync<FloorPlanResponse>(await Client.GetAsync($"{Venue}/floor-plan", Ct))).Tables.Single(t => t.Name == name);
}
