using System.Net;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Seating;
using Reservation.Api.Features.Venues;
using Reservation.Api.Features.Waitlist;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Demo;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// The waitlist (Design §3.2, §9.2, §11.3, §11.4, §14.2; Requirements step 3 and 4: AC3.4–AC3.7, AC4.2, AC4.5, AC4.6)
/// on the seed at 18:25 (Design §17).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class WaitlistTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly string Waitlist = $"{Venue}/waitlist";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await SignInAsync(Client);
    }

    // ---------- Quote (AC3.5, BR-08) ----------

    [Fact]
    public async Task Step_3_a_party_of_4_is_quoted_35_minutes_until_T4_frees_at_19_00()
    {
        var quote = await ReadAsync<WaitlistQuoteResponse>(await Client.GetAsync($"{Waitlist}/quote?partySize=4", Ct));

        Assert.Equal(4, quote.PartySize);
        Assert.Equal(35, quote.QuotedWaitMinutes);
    }

    [Fact]
    public async Task A_party_that_fits_a_free_table_is_quoted_0()
    {
        var quote = await ReadAsync<WaitlistQuoteResponse>(await Client.GetAsync($"{Waitlist}/quote?partySize=2", Ct));

        Assert.Equal(0, quote.QuotedWaitMinutes);
    }

    [Fact]
    public async Task A_party_no_table_seats_gets_party_too_large_and_a_quote_needs_a_party_size()
    {
        await ProblemAssert.IsAsync(await Client.GetAsync($"{Waitlist}/quote?partySize=9", Ct), HttpStatusCode.UnprocessableEntity, "party_too_large");
        await ProblemAssert.IsValidationAsync(await Client.GetAsync($"{Waitlist}/quote", Ct), "partySize", "party_size_required");
    }

    // ---------- Join and list (AC3.4–AC3.6) ----------

    [Fact]
    public async Task Step_3_a_party_of_4_joins_with_the_quote_the_host_adjusted_and_is_listed_in_join_order()
    {
        var lee = await JoinAsync("Lee Family", "021 011 0141", 4, quotedWaitMinutes: 45);
        Factory.Clock.Set(DemoTime.At(18, 30));
        var ngata = await JoinAsync("Ngata Whānau", "021 011 0142", 2, quotedWaitMinutes: 10);
        Factory.Clock.Set(DemoTime.At(18, 37));

        var list = await ReadAsync<WaitlistResponse>(await Client.GetAsync(Waitlist, Ct));

        Assert.Equal("Lee Family", lee.Name);
        Assert.Equal("+64******141", lee.MobileMasked);
        Assert.Equal(WaitlistStatus.Waiting, lee.Status);
        Assert.Equal([lee.Id, ngata.Id], list.Items.Select(i => i.Id));
        var first = list.Items[0];
        Assert.Equal(45, first.QuotedWaitMinutes);
        Assert.Equal(12, first.WaitedMinutes);
        Assert.Equal(DemoTime.Now.UtcDateTime, first.JoinedAt);
        Assert.False(first.Overdue);
        Assert.Null(list.SuggestedEntryId);
    }

    [Fact]
    public async Task A_known_mobile_joins_under_the_name_given_and_the_guest_keeps_its_stored_name()
    {
        var entry = await JoinAsync("Wei's Friend", GoldenDragon.Guests[0].Mobile, 4);

        Assert.Equal("Wei's Friend", entry.Name);
        await using var db = Database.Context();
        var guest = await db.Guests.SingleAsync(g => g.Mobile == PhoneNumber.Create(GoldenDragon.Guests[0].Mobile).Value, Ct);
        Assert.Equal(GoldenDragon.Guests[0].Name, guest.Name.Value);
        Assert.Equal(Language.Zh, guest.Language); // a known guest keeps its stored language
    }

    [Fact]
    public async Task Joining_is_validated()
    {
        await ProblemAssert.IsValidationAsync(
            await PostAsync(Client, Waitlist, Body("Lee", "021 011 0141", 4, quotedWaitMinutes: 241)), "quotedWaitMinutes", "quoted_wait_out_of_range");
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, Waitlist, Body("Lee", "09 300 1234", 4)), "mobile", "mobile_not_mobile");
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, Waitlist, Body(" ", "021 011 0141", 4)), "name", "name_length");
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, Waitlist, Body("Lee", "021 011 0141", 21)), "partySize", "party_size_out_of_range");
    }

    [Fact]
    public async Task The_same_idempotency_key_adds_one_entry()
    {
        var first = await ReadAsync<WaitlistItem>(await PostAsync(Client, Waitlist, Body("Lee", "021 011 0141", 4), "join-key"), HttpStatusCode.Created);
        var second = await ReadAsync<WaitlistItem>(await PostAsync(Client, Waitlist, Body("Lee", "021 011 0141", 4), "join-key"), HttpStatusCode.Created);

        Assert.Equal(first.Id, second.Id);
        await using var db = Database.Context();
        Assert.Equal(1, await db.WaitlistEntries.CountAsync(Ct));
    }

    [Fact]
    public async Task Waitlist_endpoints_need_a_host_session()
    {
        using var guest = NewClient();

        await ProblemAssert.IsAsync(await guest.GetAsync(Waitlist, Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await ProblemAssert.IsAsync(await PostAsync(guest, Waitlist, Body("Lee", "021 011 0141", 4)), HttpStatusCode.Unauthorized, "not_signed_in");
    }

    // ---------- Notify, overdue (AC4.3 status, AC4.6, BR-10) ----------

    [Fact]
    public async Task Notifying_marks_the_entry_notified_and_raises_WaitlistNotified()
    {
        var entry = await JoinAsync("Lee Family", "021 011 0141", 4);

        var notified = await ReadAsync<WaitlistEntryStateResponse>(
            await PostAsync(Client, $"{Waitlist}/{entry.Id}/notify", new { tableId = Table("T4"), entry.Version }));

        Assert.Equal(WaitlistStatus.Notified, notified.Status);
        Assert.Equal(DemoTime.Now.UtcDateTime, notified.NotifiedAt);
        Assert.Equal(Table("T4"), notified.NotifiedTableId);
        Assert.NotEqual(entry.Version, notified.Version);
        await using var db = Database.Context();
        var events = await db.Outbox.Where(o => o.AggregateId == entry.Id).OrderBy(o => o.Sequence).Select(o => o.EventType).ToListAsync(Ct);
        Assert.Equal(["WaitlistJoined", "WaitlistNotified"], events);
    }

    [Fact]
    public async Task Notifying_is_refused_for_a_stale_version_a_small_or_unknown_table_and_an_unknown_entry()
    {
        var entry = await JoinAsync("Lee Family", "021 011 0141", 4);

        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Waitlist}/{entry.Id}/notify", new { tableId = Table("T4"), version = entry.Version + 1 }),
            HttpStatusCode.Conflict, "version_conflict");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Waitlist}/{entry.Id}/notify", new { tableId = Table("T1"), entry.Version }),
            HttpStatusCode.UnprocessableEntity, "table_too_small");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Waitlist}/{entry.Id}/notify", new { tableId = Guid.CreateVersion7(), entry.Version }),
            HttpStatusCode.NotFound, "not_found");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Waitlist}/{Guid.CreateVersion7()}/notify", new { tableId = Table("T4"), version = 1 }),
            HttpStatusCode.NotFound, "not_found");
        await ProblemAssert.IsValidationAsync(await PostAsync(Client, $"{Waitlist}/{entry.Id}/notify", new { entry.Version }), "tableId", "table_required");
    }

    [Fact]
    public async Task A_notified_party_is_overdue_after_10_minutes_on_the_business_clock_BR10()
    {
        var entry = await JoinAsync("Lee Family", "021 011 0141", 4);
        await PostAsync(Client, $"{Waitlist}/{entry.Id}/notify", new { tableId = Table("T4"), entry.Version });

        Factory.Clock.Set(DemoTime.At(18, 35));
        Assert.False(Assert.Single((await ReadAsync<WaitlistResponse>(await Client.GetAsync(Waitlist, Ct))).Items).Overdue);

        Factory.Clock.Set(DemoTime.At(18, 36));
        Assert.True(Assert.Single((await ReadAsync<WaitlistResponse>(await Client.GetAsync(Waitlist, Ct))).Items).Overdue);
    }

    // ---------- Seat from the waitlist (AC4.5) ----------

    [Fact]
    public async Task Step_4_clearing_T4_suggests_the_waiting_party_of_4_who_is_then_seated_from_the_waitlist()
    {
        var two = await JoinAsync("Early Pair", "021 011 0143", 2);
        var lee = await JoinAsync("Lee Family", "021 011 0141", 4);
        await ClearT4Async();

        var forT4 = await ReadAsync<WaitlistResponse>(await Client.GetAsync($"{Waitlist}?tableId={Table("T4")}", Ct));
        Assert.Equal(two.Id, forT4.SuggestedEntryId); // first by join time that T4 seats (BR-09)
        await PostAsync(Client, $"{Waitlist}/{two.Id}/end", new { outcome = "walkedAway", two.Version });
        forT4 = await ReadAsync<WaitlistResponse>(await Client.GetAsync($"{Waitlist}?tableId={Table("T4")}", Ct));
        Assert.Equal(lee.Id, forT4.SuggestedEntryId);

        var seated = await ReadAsync<SeatWalkInResponse>(
            await PostAsync(Client, $"{Venue}/walk-ins", new { partySize = 4, tableId = Table("T4"), waitlistEntryId = lee.Id, waitlistEntryVersion = lee.Version }),
            HttpStatusCode.Created);

        Assert.Equal("Lee Family", seated.GuestName);
        Assert.Equal(lee.Id, seated.WaitlistEntryId);
        Assert.Empty((await ReadAsync<WaitlistResponse>(await Client.GetAsync(Waitlist, Ct))).Items);
        await using var db = Database.Context();
        var entry = await db.WaitlistEntries.SingleAsync(w => w.Id == new WaitlistEntryId(lee.Id), Ct);
        Assert.Equal(WaitlistStatus.Seated, entry.Status);
        Assert.Equal(new BookingId(seated.BookingId), entry.BookingId);
        var booking = await db.Bookings.SingleAsync(b => b.Id == new BookingId(seated.BookingId), Ct);
        Assert.Equal(entry.GuestId, booking.GuestId);
        Assert.Equal("Lee Family", booking.ContactName!.Value);
        var bookingEvents = await db.Outbox.Where(o => o.AggregateId == seated.BookingId).OrderBy(o => o.Sequence).Select(o => o.EventType).ToListAsync(Ct);
        Assert.Equal(["BookingCreated", "BookingSeated"], bookingEvents);
        var seatedEvent = await db.Outbox.SingleAsync(o => o.AggregateId == seated.BookingId && o.EventType == "BookingSeated", Ct);
        var payload = seatedEvent.Envelope.RootElement.GetProperty("payload");
        Assert.Equal("walkIn", payload.GetProperty("source").GetString());
        Assert.Equal(lee.Id, payload.GetProperty("waitlistEntryId").GetGuid());
        Assert.Equal("WaitlistEnded", await db.Outbox.Where(o => o.AggregateId == lee.Id).OrderBy(o => o.Sequence).Select(o => o.EventType).LastAsync(Ct));
    }

    [Fact]
    public async Task Seating_from_the_waitlist_checks_the_entry_version_and_state()
    {
        var lee = await JoinAsync("Lee Family", "021 011 0141", 4);
        await ClearT4Async();
        object Body(uint version) => new { partySize = 4, tableId = Table("T4"), waitlistEntryId = lee.Id, waitlistEntryVersion = version };

        await ProblemAssert.IsAsync(await PostAsync(Client, $"{Venue}/walk-ins", Body(lee.Version + 1)), HttpStatusCode.Conflict, "version_conflict");
        var ended = await ReadAsync<WaitlistEntryStateResponse>(await PostAsync(Client, $"{Waitlist}/{lee.Id}/end", new { outcome = "removed", lee.Version }));
        await ProblemAssert.IsAsync(await PostAsync(Client, $"{Venue}/walk-ins", Body(ended.Version)), HttpStatusCode.Conflict, "invalid_transition");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Venue}/walk-ins", new { partySize = 4, tableId = Table("T4"), waitlistEntryId = Guid.CreateVersion7(), waitlistEntryVersion = 1 }),
            HttpStatusCode.NotFound, "not_found");
    }

    // ---------- End (AC3.7) ----------

    [Theory]
    [InlineData("walkedAway", WaitlistStatus.WalkedAway)]
    [InlineData("removed", WaitlistStatus.Removed)]
    public async Task Ending_an_entry_keeps_its_quote_and_takes_it_off_the_active_list(string outcome, WaitlistStatus status)
    {
        var entry = await JoinAsync("Lee Family", "021 011 0141", 4, quotedWaitMinutes: 40);

        var ended = await ReadAsync<WaitlistEntryStateResponse>(await PostAsync(Client, $"{Waitlist}/{entry.Id}/end", new { outcome, entry.Version }));
        var again = await ReadAsync<WaitlistEntryStateResponse>(await PostAsync(Client, $"{Waitlist}/{entry.Id}/end", new { outcome, entry.Version }));

        Assert.Equal(status, ended.Status);
        Assert.Equal(ended, again); // the same outcome again is a no-op
        Assert.Empty((await ReadAsync<WaitlistResponse>(await Client.GetAsync(Waitlist, Ct))).Items);
        await using var db = Database.Context();
        var stored = await db.WaitlistEntries.SingleAsync(w => w.Id == new WaitlistEntryId(entry.Id), Ct);
        Assert.Equal(40, stored.QuotedWaitMinutes);
        Assert.Equal(DemoTime.Now, stored.EndedAt);
    }

    [Fact]
    public async Task Ending_is_refused_for_another_outcome_after_ending_a_stale_version_and_seated()
    {
        var entry = await JoinAsync("Lee Family", "021 011 0141", 4);

        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Waitlist}/{entry.Id}/end", new { outcome = "removed", version = entry.Version + 1 }), HttpStatusCode.Conflict, "version_conflict");
        await ProblemAssert.IsValidationAsync(
            await PostAsync(Client, $"{Waitlist}/{entry.Id}/end", new { outcome = "seated", entry.Version }), "outcome", "outcome_invalid");
        var walked = await ReadAsync<WaitlistEntryStateResponse>(await PostAsync(Client, $"{Waitlist}/{entry.Id}/end", new { outcome = "walkedAway", entry.Version }));
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Waitlist}/{entry.Id}/end", new { outcome = "removed", walked.Version }), HttpStatusCode.Conflict, "invalid_transition");
        await ProblemAssert.IsAsync(
            await PostAsync(Client, $"{Waitlist}/{Guid.CreateVersion7()}/end", new { outcome = "removed", version = 1 }), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task A_suggestion_for_an_unknown_table_is_not_found()
    {
        await ProblemAssert.IsAsync(await Client.GetAsync($"{Waitlist}?tableId={Guid.CreateVersion7()}", Ct), HttpStatusCode.NotFound, "not_found");
    }

    // ---------- Helpers ----------

    private static Guid Table(string name) => GoldenDragon.TableId(name).Value;

    private static object Body(string name, string mobile, int partySize, int quotedWaitMinutes = 35) =>
        new { name, mobile, partySize, quotedWaitMinutes };

    private async Task<WaitlistItem> JoinAsync(string name, string mobile, int partySize, int quotedWaitMinutes = 35) =>
        await ReadAsync<WaitlistItem>(await PostAsync(Client, Waitlist, Body(name, mobile, partySize, quotedWaitMinutes)), HttpStatusCode.Created);

    private async Task ClearT4Async()
    {
        var plan = await ReadAsync<FloorPlanResponse>(await Client.GetAsync($"{Venue}/floor-plan", Ct));
        var current = plan.Tables.Single(t => t.Name == "T4").Current!;
        var cleared = await ReadAsync<BookingStateResponse>(
            await PostAsync(Client, $"{Venue}/tables/{Table("T4")}/clear", new { current.BookingId, current.Version }));
        Assert.Equal(BookingStatus.Completed, cleared.Status);
    }
}
