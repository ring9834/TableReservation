using Reservation.Api.Messaging;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;

namespace Reservation.Tests.Unit.Messaging;

/// <summary>
/// <see cref="MessageComposer"/> and the resource templates (Design §7, §7.2): every purpose M7a sends, in English and
/// Simplified Chinese, short, with the venue name and no placeholder left over.
/// </summary>
public class MessageComposerTests
{
    /// <summary>The purposes M7a queues. M8 (deposit, cancellation, refund) and M10 (reminder) add theirs.</summary>
    public static readonly TheoryData<MessagePurpose, Language> UsedNow = Combine([MessagePurpose.Confirmation, MessagePurpose.TableReady]);

    private static readonly TimeZoneInfo _auckland = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");

    // Saturday 14 November 2026, 19:30 in Auckland (NZDT, UTC+13).
    private static readonly DateTimeOffset _startsAt = new(2026, 11, 14, 6, 30, 0, TimeSpan.Zero);

    private readonly MessageComposer _composer = new(new ResxTemplateStore());

    [Theory]
    [MemberData(nameof(UsedNow))]
    public void Every_purpose_in_use_has_a_template_in_every_language(MessagePurpose purpose, Language language)
    {
        var body = _composer.Compose(purpose, language, Model()).Body;

        Assert.StartsWith("Golden Dragon", body, StringComparison.Ordinal);
        Assert.DoesNotContain("{", body, StringComparison.Ordinal);
        Assert.DoesNotContain("}", body, StringComparison.Ordinal);
        Assert.InRange(body.Length, 20, 160);
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
    public void A_purpose_without_a_template_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ResxTemplateStore().Get(MessagePurpose.Reminder, Language.En));
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
    public void Messages_carry_no_actions_yet()
    {
        Assert.Empty(_composer.Compose(MessagePurpose.Confirmation, Language.En, Model()).Actions);
    }

    private static TemplateModel Model() => new("Golden Dragon", _auckland, "GD7K4P", 4, _startsAt);

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
