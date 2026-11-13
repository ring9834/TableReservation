using System.Diagnostics;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Messages;
using Reservation.Api.Features.Seating;
using Reservation.Api.Features.Waitlist;
using Reservation.Api.Messaging;
using Reservation.Api.Realtime;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Messaging;
using Reservation.Infrastructure.Outbox;
using Reservation.Tests.Integration.Messaging;
using Reservation.Tests.Integration.Realtime;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>Shared steps for the messaging tests: dispatch events, run the message worker, read the log.</summary>
public abstract class MessagingTestBase(PostgresFixture postgres) : ApiTestBase(postgres)
{
    protected static Guid Table(string name) => GoldenDragon.TableId(name).Value;

    /// <summary>Runs dispatcher batches until nothing is due.</summary>
    protected async Task DispatchAsync()
    {
        var dispatcher = Factory.Services.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Runs message-worker batches until nothing is due.</summary>
    protected async Task DeliverAsync()
    {
        var worker = Factory.Services.GetRequiredService<MessageDeliveryWorker>();
        while (await worker.DeliverBatchAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Delivers the event again: its outbox row back to Pending, then dispatched.</summary>
    protected async Task RedeliverAsync(Guid aggregateId, string eventType)
    {
        await using (var db = Database.Context())
        {
            Assert.Equal(1, await db.Outbox
                .Where(o => o.AggregateId == aggregateId && o.EventType == eventType)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OutboxStatus.Pending), Ct));
        }

        await DispatchAsync();
    }

    protected async Task<List<Message>> MessagesAboutAsync(Guid subjectId)
    {
        await using var db = Database.Context();
        return await db.Messages.AsNoTracking().Where(m => m.SubjectId == subjectId).OrderBy(m => m.Id).ToListAsync(Ct);
    }

    protected async Task<HttpClient> PresenterAsync()
    {
        var presenter = NewClient();
        await SignInAsync(presenter, ApiFactory.PresenterPasscode);
        return presenter;
    }

    private protected async Task<WaitlistItem> JoinAsync(HttpClient client, string name, string mobile, int partySize = 2, string language = "en") =>
        await ReadAsync<WaitlistItem>(
            await PostAsync(client, $"{Venue}/waitlist", new { name, mobile, partySize, quotedWaitMinutes = 20, language }),
            HttpStatusCode.Created);

    private protected async Task<WaitlistEntryStateResponse> NotifyAsync(HttpClient client, Guid entryId, uint version, string table) =>
        await ReadAsync<WaitlistEntryStateResponse>(
            await PostAsync(client, $"{Venue}/waitlist/{entryId}/notify", new { tableId = Table(table), version }));

    private protected async Task<WaitlistItem> WaitlistItemAsync(HttpClient client, Guid entryId) =>
        (await ReadAsync<WaitlistResponse>(await client.GetAsync($"{Venue}/waitlist", Ct))).Items.Single(i => i.Id == entryId);
}

/// <summary>
/// Design §7, §7.2 through the API: which events queue which texts, in which language, once per event; the message
/// log for the presenter; and the TableReady status on the waitlist (AC1.6, AC4.3, AC4.4). Workers are driven by hand.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagingTests(PostgresFixture postgres) : MessagingTestBase(postgres)
{
    // ---------- Confirmation ----------

    [Fact]
    public async Task An_online_booking_gets_one_english_confirmation_even_when_the_event_is_delivered_twice()
    {
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 30, 2, mobile: "021 011 0201")), HttpStatusCode.Created);

        await DispatchAsync();
        await RedeliverAsync(booking.BookingId, "BookingCreated");

        var message = Assert.Single(await MessagesAboutAsync(booking.BookingId));
        Assert.Equal(MessagePurpose.Confirmation, message.Purpose);
        Assert.Equal(MessageStatus.Queued, message.Status);
        Assert.Equal(Language.En, message.Language);
        Assert.Equal(DemoTime.Now, message.CreatedAt);
        Assert.StartsWith("Golden Dragon: you're booked for 2 on ", message.Body, StringComparison.Ordinal);
        Assert.Contains("7:30 pm", message.Body, StringComparison.Ordinal);
        Assert.Contains(booking.Reference, message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_chinese_online_booking_gets_a_chinese_confirmation()
    {
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 45, 2, mobile: "021 011 0202", language: "zh")),
            HttpStatusCode.Created);

        await DispatchAsync();

        var message = Assert.Single(await MessagesAboutAsync(booking.BookingId));
        Assert.Equal(Language.Zh, message.Language);
        Assert.Contains("19:45", message.Body, StringComparison.Ordinal);
        Assert.Contains("预订号 " + booking.Reference, message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_phone_booking_gets_a_confirmation()
    {
        await SignInAsync(Client);
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/phone-bookings", BookingBody(Tomorrow, 19, 0, 4, mobile: "021 011 0203")), HttpStatusCode.Created);

        await DispatchAsync();

        Assert.Equal(MessagePurpose.Confirmation, Assert.Single(await MessagesAboutAsync(booking.BookingId)).Purpose);
    }

    [Fact]
    public async Task A_booking_waiting_for_its_deposit_gets_no_confirmation_yet()
    {
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 20, 0, 6, mobile: "021 011 0204")), HttpStatusCode.Created);
        Assert.Equal(Reservation.Domain.Bookings.BookingStatus.PendingPayment, booking.Status);

        await DispatchAsync();

        Assert.Empty(await MessagesAboutAsync(booking.BookingId));
    }

    [Fact]
    public async Task Walk_ins_get_no_confirmation_whether_or_not_they_come_from_the_waitlist()
    {
        await SignInAsync(Client);
        var walkIn = await ReadAsync<SeatWalkInResponse>(
            await PostAsync(Client, $"{Venue}/walk-ins", new { partySize = 2, tableId = Table("T1"), name = "Walk In Two" }), HttpStatusCode.Created);
        var entry = await JoinAsync(Client, "Waiting Two", "021 011 0205");
        var fromWaitlist = await ReadAsync<SeatWalkInResponse>(
            await PostAsync(Client, $"{Venue}/walk-ins", new { partySize = 2, tableId = Table("T2"), waitlistEntryId = entry.Id, waitlistEntryVersion = entry.Version }),
            HttpStatusCode.Created);

        await DispatchAsync();

        Assert.Empty(await MessagesAboutAsync(walkIn.BookingId));
        Assert.Empty(await MessagesAboutAsync(fromWaitlist.BookingId));
        Assert.Empty(await MessagesAboutAsync(entry.Id)); // never notified
    }

    // ---------- Table ready ----------

    [Fact]
    public async Task Notifying_a_chinese_entry_queues_one_chinese_table_ready_text_even_when_delivered_twice()
    {
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "王芳 Wang Fang", "021 011 0206", language: "zh");
        await NotifyAsync(Client, entry.Id, entry.Version, "T1");

        await DispatchAsync();
        await RedeliverAsync(entry.Id, "WaitlistNotified");

        var message = Assert.Single(await MessagesAboutAsync(entry.Id));
        Assert.Equal(MessagePurpose.TableReady, message.Purpose);
        Assert.Equal(Language.Zh, message.Language);
        Assert.Equal("Golden Dragon：您的餐桌已准备好！请到迎宾台。", message.Body);
        Assert.Equal(DemoTime.InfrastructureStart.Year, message.ExpiresAt.Year); // sanity: a real date
        Assert.Equal(TimeSpan.FromMinutes(10), message.ExpiresAt - message.NextAttemptAt);
    }

    [Fact]
    public async Task A_known_guest_joining_in_another_language_gets_the_text_in_the_language_chosen_now()
    {
        await SignInAsync(Client);
        await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 30, 2, mobile: "021 011 0207", language: "en")), HttpStatusCode.Created);
        var entry = await JoinAsync(Client, "Known Guest", "021 011 0207", language: "zh");
        await NotifyAsync(Client, entry.Id, entry.Version, "T1");

        await DispatchAsync();

        Assert.Equal(Language.Zh, Assert.Single(await MessagesAboutAsync(entry.Id)).Language);
    }

    [Fact]
    public async Task Re_notifying_queues_a_new_text_and_supersedes_one_still_queued_on_the_same_or_another_table()
    {
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "Renotify Guest", "021 011 0208");
        var first = await NotifyAsync(Client, entry.Id, entry.Version, "T1");
        await DispatchAsync();

        // Same table again before the first text went out: the first is superseded.
        var second = await NotifyAsync(Client, entry.Id, first.Version, "T1");
        await DispatchAsync();
        var afterSecond = await MessagesAboutAsync(entry.Id);
        Assert.Equal(2, afterSecond.Count);
        Assert.Equal((MessageStatus.Failed, NotificationHandler.Superseded), (afterSecond[0].Status, afterSecond[0].LastError));
        Assert.Equal(MessageStatus.Queued, afterSecond[1].Status);

        // The second goes out, then the host notifies for another table: a third text; the sent one stays sent.
        await DeliverAsync();
        await NotifyAsync(Client, entry.Id, second.Version, "T2");
        await DispatchAsync();
        var afterThird = await MessagesAboutAsync(entry.Id);
        Assert.Equal(3, afterThird.Count);
        Assert.Equal(MessageStatus.Sent, afterThird[1].Status);
        Assert.Equal(MessageStatus.Queued, afterThird[2].Status);
        Assert.All(afterThird, m => Assert.Equal(afterThird[0].Body, m.Body));

        var item = await WaitlistItemAsync(Client, entry.Id);
        Assert.Equal(MessageStatus.Queued, item.TableReadyStatus); // the latest text
        Assert.False(item.TableReadyFailed);
    }

    [Fact]
    public async Task An_entry_that_ended_before_the_event_was_handled_gets_no_text()
    {
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "Gone Guest", "021 011 0209");
        var notified = await NotifyAsync(Client, entry.Id, entry.Version, "T1");
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Client, $"{Venue}/waitlist/{entry.Id}/end", new { outcome = "walkedAway", notified.Version })).StatusCode);

        await DispatchAsync();

        Assert.Empty(await MessagesAboutAsync(entry.Id));
    }

    [Fact]
    public async Task The_waitlist_shows_the_table_ready_status_and_when_it_was_sent()
    {
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "Status Guest", "021 011 0210");
        Assert.Null((await WaitlistItemAsync(Client, entry.Id)).TableReadyStatus);
        await NotifyAsync(Client, entry.Id, entry.Version, "T1");
        await DispatchAsync();
        Assert.Equal(MessageStatus.Queued, (await WaitlistItemAsync(Client, entry.Id)).TableReadyStatus);

        await DeliverAsync();

        var item = await WaitlistItemAsync(Client, entry.Id);
        Assert.Equal(MessageStatus.Sent, item.TableReadyStatus);
        Assert.Equal(DemoTime.Now.UtcDateTime, item.TableReadySentAt); // the business clock, fixed at 18:25
        Assert.False(item.TableReadyFailed);
    }

    // ---------- Message log ----------

    [Fact]
    public async Task The_presenter_sees_todays_messages_oldest_first_with_masked_mobiles()
    {
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 30, 2, mobile: "021 011 0211")), HttpStatusCode.Created);
        await DispatchAsync();
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "Log Guest", "021 011 0212", language: "zh");
        await NotifyAsync(Client, entry.Id, entry.Version, "T1");
        await DispatchAsync();
        await DeliverAsync();

        using var presenter = await PresenterAsync();
        var log = await ReadAsync<MessageLogResponse>(await presenter.GetAsync($"{Venue}/messages", Ct));

        Assert.Equal(GoldenDragon.TimeZoneId, log.TimeZone);
        Assert.Equal(DemoTime.Now.UtcDateTime, log.At);
        Assert.Collection(
            log.Items,
            confirmation =>
            {
                Assert.Equal(booking.BookingId, confirmation.SubjectId);
                Assert.Equal(MessageSubjectType.Booking, confirmation.SubjectType);
                Assert.Equal(MessagePurpose.Confirmation, confirmation.Purpose);
                Assert.Equal(Language.En, confirmation.Language);
                Assert.Equal("+64******211", confirmation.MobileMasked);
                Assert.Equal(MessageStatus.Sent, confirmation.Status);
                Assert.Equal(DemoTime.Now.UtcDateTime, confirmation.SentAt);
                Assert.Null(confirmation.DeliveredAt);
                Assert.Null(confirmation.LastError);
            },
            tableReady =>
            {
                Assert.Equal(entry.Id, tableReady.SubjectId);
                Assert.Equal(MessageSubjectType.WaitlistEntry, tableReady.SubjectType);
                Assert.Equal(Language.Zh, tableReady.Language);
                Assert.Contains("餐桌已准备好", tableReady.Body, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task Superseded_texts_show_their_reason_and_others_show_none()
    {
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "Twice Guest", "021 011 0213");
        var first = await NotifyAsync(Client, entry.Id, entry.Version, "T1");
        await DispatchAsync();
        await NotifyAsync(Client, entry.Id, first.Version, "T1");
        await DispatchAsync();

        using var presenter = await PresenterAsync();
        var items = (await ReadAsync<MessageLogResponse>(await presenter.GetAsync($"{Venue}/messages", Ct))).Items;

        Assert.Equal(new string?[] { NotificationHandler.Superseded, null }, items.Select(i => i.LastError));
    }

    [Fact]
    public async Task Only_the_presenter_may_read_the_message_log()
    {
        await ProblemAssert.IsAsync(await Client.GetAsync($"{Venue}/messages", Ct), HttpStatusCode.Unauthorized, "not_signed_in");
        await SignInAsync(Client);
        await ProblemAssert.IsAsync(await Client.GetAsync($"{Venue}/messages", Ct), HttpStatusCode.Forbidden, "forbidden");
        using var presenter = await PresenterAsync();
        await ProblemAssert.IsAsync(await presenter.GetAsync($"{OtherVenue}/messages", Ct), HttpStatusCode.Forbidden, "forbidden");
    }

    [Fact]
    public async Task A_presenter_on_the_hub_hears_messageLogChanged_with_ids_only()
    {
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.PresenterPasscode }, Ct);
        await using var hub = await HubClient.ConnectAsync(Factory, cookie, Ct);
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 30, 2, name: "Hub Listener", mobile: "021 011 0214")),
            HttpStatusCode.Created);

        await DispatchAsync();

        var message = Assert.Single(await MessagesAboutAsync(booking.BookingId));
        var notice = await hub.NextAsync(NoticeMethods.MessageLogChanged, TimeSpan.FromSeconds(2), Ct);
        Assert.True(notice.HasValue, "No messageLogChanged within 2 s.");
        Assert.Equal(["messageIds"], notice.Value.EnumerateObject().Select(p => p.Name));
        Assert.Equal(message.Id.Value, Assert.Single(notice.Value.GetProperty("messageIds").EnumerateArray()).GetGuid());
        Assert.DoesNotContain("Hub Listener", notice.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_table_ready_change_also_tells_the_hosts_waitlist()
    {
        await SignInAsync(Client);
        var cookie = await HubClient.SignInAsync(Factory, "/api/v1/demo-login", new { passcode = ApiFactory.HostPasscode }, Ct);
        await using var hub = await HubClient.ConnectAsync(Factory, cookie, Ct);
        var entry = await JoinAsync(Client, "Hub Waiting", "021 011 0215");
        var notified = await NotifyAsync(Client, entry.Id, entry.Version, "T1");
        await DispatchAsync();
        await DrainAsync(hub, NoticeMethods.WaitlistChanged);

        await DeliverAsync();

        var notice = await hub.NextAsync(NoticeMethods.WaitlistChanged, TimeSpan.FromSeconds(2), Ct);
        Assert.True(notice.HasValue, "No waitlistChanged after the text was sent.");
        Assert.Equal(notified.Id, Assert.Single(notice.Value.GetProperty("entryIds").EnumerateArray()).GetGuid());
    }

    private static async Task DrainAsync(HubClient hub, string method)
    {
        while (await hub.NextAsync(method, TimeSpan.FromMilliseconds(300), Ct) is not null)
        {
        }
    }
}

/// <summary>A provider that refuses the number: the TableReady fails and the waitlist entry is flagged (§7.1 Visibility).</summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagingFailureTests(PostgresFixture postgres) : MessagingTestBase(postgres)
{
    protected override void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IMessageSender>(new FakeSender { Behaviour = (_, _) => Task.FromResult(SendResult.Permanent("invalid_number")) });

    [Fact]
    public async Task A_failed_table_ready_text_is_flagged_on_the_entry_and_shows_its_reason_in_the_log()
    {
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "Unreachable Guest", "021 011 0216");
        await NotifyAsync(Client, entry.Id, entry.Version, "T1");
        await DispatchAsync();

        await DeliverAsync();

        var item = await WaitlistItemAsync(Client, entry.Id);
        Assert.Equal(MessageStatus.Failed, item.TableReadyStatus);
        Assert.True(item.TableReadyFailed);
        Assert.Null(item.TableReadySentAt);
        using var presenter = await PresenterAsync();
        var logged = Assert.Single((await ReadAsync<MessageLogResponse>(await presenter.GetAsync($"{Venue}/messages", Ct))).Items);
        Assert.Equal((MessageStatus.Failed, "invalid_number"), (logged.Status, logged.LastError));
    }
}

/// <summary>
/// End to end with the workers running (outbox dispatcher and message worker on the real clocks; business clock fixed
/// at 18:25): AC1.6 (confirmation in the phone panel within 2 s), AC4.3 and AC4.4 (Chinese "table ready", status and
/// time sent on the waitlist).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagingDeliveryTests(PostgresFixture postgres) : MessagingTestBase(postgres)
{
    protected override IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Outbox:RunWorkers"] = "true" };

    [Fact]
    public async Task An_online_booking_confirmation_reaches_the_phone_panel_within_2_seconds_then_is_delivered()
    {
        using var presenter = await PresenterAsync();
        var booking = await ReadAsync<CreateBookingResponse>(
            await PostAsync(Client, $"{Venue}/bookings", BookingBody(Tomorrow, 19, 30, 2, mobile: "021 011 0221")), HttpStatusCode.Created);
        var answered = Stopwatch.StartNew();

        var shown = await PollAsync(presenter, booking.BookingId, s => s is MessageStatus.Sent or MessageStatus.Delivered, TimeSpan.FromSeconds(2));
        Assert.True(shown is not null, $"The confirmation was not Sent within 2 s of the booking response ({answered.ElapsedMilliseconds} ms).");
        Assert.Equal(MessagePurpose.Confirmation, shown.Purpose);

        var delivered = await PollAsync(presenter, booking.BookingId, s => s == MessageStatus.Delivered, TimeSpan.FromSeconds(3));
        Assert.True(delivered is not null, "The confirmation was not Delivered within a further 3 s.");
        Assert.Equal(DemoTime.Now.UtcDateTime, delivered.DeliveredAt);
    }

    [Fact]
    public async Task Notifying_a_chinese_entry_sends_a_chinese_table_ready_text_and_the_waitlist_shows_it()
    {
        await SignInAsync(Client);
        var entry = await JoinAsync(Client, "李明 Li Ming", "021 011 0222", language: "zh");
        await NotifyAsync(Client, entry.Id, entry.Version, "T1");

        var deadline = Stopwatch.StartNew();
        WaitlistItem item;
        while ((item = await WaitlistItemAsync(Client, entry.Id)).TableReadyStatus != MessageStatus.Delivered)
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(5), $"Status was {item.TableReadyStatus} after 5 s.");
            await Task.Delay(50, Ct);
        }

        Assert.Equal(DemoTime.Now.UtcDateTime, item.TableReadySentAt);
        using var presenter = await PresenterAsync();
        var text = (await ReadAsync<MessageLogResponse>(await presenter.GetAsync($"{Venue}/messages", Ct))).Items.Single(i => i.SubjectId == entry.Id);
        Assert.Equal((MessagePurpose.TableReady, Language.Zh), (text.Purpose, text.Language));
        Assert.Equal("Golden Dragon：您的餐桌已准备好！请到迎宾台。", text.Body);
    }

    private static async Task<MessageLogItem?> PollAsync(HttpClient presenter, Guid subjectId, Func<MessageStatus, bool> reached, TimeSpan within)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < within)
        {
            var log = await ReadAsync<MessageLogResponse>(await presenter.GetAsync($"{Venue}/messages", Ct));
            var item = log.Items.SingleOrDefault(i => i.SubjectId == subjectId);
            if (item is not null && reached(item.Status))
            {
                return item;
            }

            await Task.Delay(25, Ct);
        }

        return null;
    }
}
