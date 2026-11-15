using Reservation.Api.GuestLinks;
using Reservation.Api.Messaging;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Venues;

namespace Reservation.Tests.Unit.Messaging;

/// <summary>
/// <see cref="MessageComposer"/> and the resource templates (Design §7, §7.2, §8.4): every purpose sent so far, in English and
/// Simplified Chinese, short, with the venue name and no placeholder left over; since M10a the reminder, the
/// cancellation wording and the link actions (§7.3).
/// </summary>
public class MessageComposerTests
{
    /// <summary>Every purpose: M7a, M8a's cancellation and refund, and M10a's reminder.</summary>
    public static readonly TheoryData<MessagePurpose, Language> UsedNow = Combine(Enum.GetValues<MessagePurpose>());

    private static readonly BookingTerms _terms = new(
        new DepositRule(6, 2000), new CancellationRule(2, RefundAmount.Full, RefundAmount.Full), 120, DateTimeOffset.UnixEpoch, 1);

    private static readonly TimeZoneInfo _auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

    // Saturday 14 November 2026, 19:30 in Auckland (NZDT, UTC+13).
    private static readonly DateTimeOffset _startsAt = new(2026, 11, 14, 6, 30, 0, TimeSpan.Zero);

    private readonly MessageComposer _composer = new(new ResxTemplateStore());

    [Theory]
    [MemberData(nameof(UsedNow))]
    public void Every_purpose_in_use_has_a_template_in_every_language(MessagePurpose purpose, Language language)
    {
        var body = _composer.Compose(purpose, language, Model() with { Policy = Policy(language) }).Body;

        Assert.StartsWith("Golden Dragon", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{", body, StringComparison.Ordinal);
        Assert.DoesNotContain("}", body, StringComparison.Ordinal);
        // A reminder also carries the cancellation wording (§7.3); its links come on top of this.
        Assert.InRange(body.Length, 20, purpose == MessagePurpose.Reminder ? 240 : 160);
    }

    [Fact]
    public void Every_language_has_a_template_set()
    {
        foreach (var language in Enum.GetValues<Language>())
        {
            Assert.NotEmpty(new ResxTemplateStore().Get(MessagePurpose.TableReady, language));
        }
    }

    [Fact]
    public void A_key_without_a_template_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ResxTemplateStore().Get("NoSuchTemplate", Language.En));
    }

    [Theory]
    [InlineData("ActionConfirm")]
    [InlineData("ActionCancel")]
    [InlineData("ActionManage")]
    [InlineData("CancellationPolicyDeposit")]
    [InlineData("CancellationPolicyDepositKept")]
    [InlineData("CancellationPolicyNoDeposit")]
    public void Every_fragment_has_a_template_in_every_language(string key)
    {
        foreach (var language in Enum.GetValues<Language>())
        {
            Assert.NotEmpty(new ResxTemplateStore().Get(key, language));
        }
    }

    [Fact]
    public void English_confirmation_names_party_day_time_and_reference_in_venue_time()
    {
        var body = _composer.Compose(MessagePurpose.Confirmation, Language.En, Model()).Body;

        Assert.Equal("Golden Dragon: you're booked for 4 on Sat 14 Nov at 7:30 pm. Ref GD7K4P. See you soon!", body);
    }

    [Fact]
    public void Chinese_confirmation_uses_chinese_date_and_24_hour_time()
    {
        var body = _composer.Compose(MessagePurpose.Confirmation, Language.Zh, Model()).Body;

        Assert.Equal("Golden Dragon：已为您预订11月14日（周六） 19:30，4位。预订号 GD7K4P。期待您的光临！", body);
    }

    [Fact]
    public void Table_ready_says_the_table_is_ready_and_to_come_to_the_host_stand()
    {
        Assert.Equal(
            "Golden Dragon: your table is ready! Please come to the host stand.",
            _composer.Compose(MessagePurpose.TableReady, Language.En, new TemplateModel("Golden Dragon", _auckland)).Body);
        Assert.Equal(
            "Golden Dragon：您的餐桌已准备好！请到迎宾台。",
            _composer.Compose(MessagePurpose.TableReady, Language.Zh, new TemplateModel("Golden Dragon", _auckland)).Body);
    }

    [Fact]
    public void Cancellation_names_the_booking_in_both_languages()
    {
        Assert.Equal(
            "Golden Dragon: your booking for 4 on Sat 14 Nov at 7:30 pm (ref GD7K4P) is cancelled.",
            _composer.Compose(MessagePurpose.Cancellation, Language.En, Model()).Body);
        Assert.Equal(
            "Golden Dragon：您11月14日（周六） 19:30的4位预订（预订号 GD7K4P）已取消。",
            _composer.Compose(MessagePurpose.Cancellation, Language.Zh, Model()).Body);
    }

    [Fact]
    public void Refund_names_the_amount_in_both_languages()
    {
        Assert.Equal(
            "Golden Dragon: we've refunded your deposit of NZ$120.00 (ref GD7K4P).",
            _composer.Compose(MessagePurpose.Refund, Language.En, Model()).Body);
        Assert.Equal(
            "Golden Dragon：您的订金 NZ$120.00 已退还（预订号 GD7K4P）。",
            _composer.Compose(MessagePurpose.Refund, Language.Zh, Model()).Body);
    }

    [Theory]
    [InlineData(12_000, "NZ$120.00")]
    [InlineData(2_005, "NZ$20.05")]
    [InlineData(0, "NZ$0.00")]
    public void Amounts_are_formatted_in_dollars_and_cents(long cents, string expected) =>
        Assert.Equal(expected, MessageComposer.FormatAmount(Money.FromNzd(cents)));

    [Theory]
    [InlineData(0, 5, "12:05 am", "0:05")]
    [InlineData(12, 0, "12:00 pm", "12:00")]
    [InlineData(9, 45, "9:45 am", "9:45")]
    [InlineData(21, 15, "9:15 pm", "21:15")]
    public void Times_are_formatted_by_hand(int hour, int minute, string english, string chinese)
    {
        var local = new DateTimeOffset(2026, 11, 14, hour, minute, 0, TimeSpan.FromHours(13));

        Assert.Equal(english, MessageComposer.FormatTime(local, Language.En));
        Assert.Equal(chinese, MessageComposer.FormatTime(local, Language.Zh));
    }

    [Fact]
    public void Texts_without_links_carry_no_actions()
    {
        Assert.Empty(_composer.Compose(MessagePurpose.Confirmation, Language.En, Model()).Actions);
    }

    [Fact]
    public void A_reminder_names_the_booking_shows_the_policy_and_ends_with_its_two_links()
    {
        var composed = _composer.Compose(
            MessagePurpose.Reminder,
            Language.En,
            Model() with { Policy = Policy(Language.En) },
            [new LinkActionUrl(LinkAction.Confirm, "https://g.test/manage/?token=c"), new LinkActionUrl(LinkAction.Cancel, "https://g.test/manage/?token=x")]);

        Assert.Equal(
            "Golden Dragon: reminder of your booking for 4 on Sat 14 Nov at 7:30 pm, ref GD7K4P. Cancel free up to 2 h before and "
            + "your NZ$120.00 deposit is refunded in full; after that it is kept. Confirm: https://g.test/manage/?token=c "
            + "Cancel: https://g.test/manage/?token=x",
            composed.Body);
        Assert.Equal(
            [new MessageAction("confirm", "https://g.test/manage/?token=c"), new MessageAction("cancel", "https://g.test/manage/?token=x")],
            composed.Actions);
    }

    [Fact]
    public void A_chinese_reminder_has_chinese_wording_and_link_labels()
    {
        var composed = _composer.Compose(
            MessagePurpose.Reminder,
            Language.Zh,
            Model() with { Policy = Policy(Language.Zh) },
            [new LinkActionUrl(LinkAction.Confirm, "https://g.test/c"), new LinkActionUrl(LinkAction.Cancel, "https://g.test/x")]);

        Assert.Equal(
            "Golden Dragon：提醒您11月14日（周六） 19:30的4位预订，预订号 GD7K4P。开餐前2小时之前取消，NZ$120.00订金全额退还；"
            + "之后取消，订金不予退还。 确认: https://g.test/c 取消: https://g.test/x",
            composed.Body);
    }

    [Fact]
    public void A_confirmation_ends_with_its_manage_link()
    {
        var composed = _composer.Compose(
            MessagePurpose.Confirmation, Language.En, Model(), [new LinkActionUrl(LinkAction.Manage, "https://g.test/m")]);

        Assert.EndsWith("See you soon! Manage: https://g.test/m", composed.Body, StringComparison.Ordinal);
        Assert.Equal([new MessageAction("manage", "https://g.test/m")], composed.Actions);
    }

    [Fact]
    public void The_policy_follows_the_booking_terms_and_deposit()
    {
        Assert.Equal(
            "Can't make it? Please cancel at least 2 h before.",
            _composer.CancellationPolicy(_terms, null, Language.En));
        Assert.Equal("如不能前来，请至少提前2小时取消。", _composer.CancellationPolicy(_terms, null, Language.Zh));

        var keepsDeposit = _terms with { Cancellation = new CancellationRule(24, RefundAmount.None, RefundAmount.Full) };
        Assert.Equal(
            "Your NZ$120.00 deposit is not refundable if you cancel.",
            _composer.CancellationPolicy(keepsDeposit, Money.FromNzd(12_000), Language.En));

        var fourHours = _terms with { Cancellation = new CancellationRule(4, RefundAmount.Full, RefundAmount.Full) };
        Assert.StartsWith("Cancel free up to 4 h before", _composer.CancellationPolicy(fourHours, Money.FromNzd(12_000), Language.En), StringComparison.Ordinal);
    }

    private string Policy(Language language) => _composer.CancellationPolicy(_terms, Money.FromNzd(12_000), language);

    private static TemplateModel Model() => new("Golden Dragon", _auckland, "GD7K4P", 4, _startsAt, Money.FromNzd(12_000));

    private static TheoryData<MessagePurpose, Language> Combine(MessagePurpose[] purposes)
    {
        var data = new TheoryData<MessagePurpose, Language>();
        foreach (var purpose in purposes)
        {
            foreach (var language in Enum.GetValues<Language>())
            {
                data.Add(purpose, language);
            }
        }

        return data;
    }
}
