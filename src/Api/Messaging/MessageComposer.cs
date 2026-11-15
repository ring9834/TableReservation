using System.Globalization;
using System.Text;
using Reservation.Api.GuestLinks;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;
using Reservation.Domain.Venues;

namespace Reservation.Api.Messaging;

/// <summary>
/// What a template may use. <see cref="StartsAt"/> is shown in <see cref="TimeZone"/>; fields a purpose doesn't use are
/// ignored. <see cref="Amount"/> fills <c>{amount}</c> (the refund text, M8a); <see cref="Policy"/> fills <c>{policy}</c>
/// (the reminder's cancellation wording, M10a).
/// </summary>
internal sealed record TemplateModel(
    string VenueName,
    TimeZoneInfo TimeZone,
    string? Reference = null,
    int? PartySize = null,
    DateTimeOffset? StartsAt = null,
    Money? Amount = null,
    string? Policy = null);

/// <summary>A link to put on a message: the action's kind and its signed URL (Design §7.3).</summary>
internal sealed record LinkActionUrl(LinkAction Kind, string Url);

internal sealed record ComposedMessage(string Body, IReadOnlyList<MessageAction> Actions);

/// <summary>
/// Fills a template (Design §7, §7.2). The venue name is used as stored in both languages (venues have no Chinese
/// name). Dates and times are in the venue's zone and formatted by hand per language, so the text never depends on
/// the server's culture data: English <c>Fri 14 Nov</c>, <c>7:30 pm</c>; Chinese <c>11月14日（周五）</c>, <c>19:30</c>.
/// Since M10a a message can carry actions (the reminder's Confirm and Cancel, the confirmation's Manage): each is stored
/// as a <see cref="MessageAction"/> for the phone panel's buttons and also written at the end of the body as
/// "Confirm: {url}", because a real text has no buttons.
/// </summary>
internal sealed class MessageComposer(ITemplateStore templates)
{
    private static readonly string[] _englishDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] _englishMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] _chineseDays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public ComposedMessage Compose(
        MessagePurpose purpose, Language language, TemplateModel model, IReadOnlyList<LinkActionUrl>? actions = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        actions ??= [];
        var local = model.StartsAt is { } startsAt ? TimeZoneInfo.ConvertTime(startsAt, model.TimeZone) : (DateTimeOffset?)null;
        var body = new StringBuilder(templates.Get(purpose, language))
            .Replace("{venue}", model.VenueName)
            .Replace("{reference}", model.Reference)
            .Replace("{party}", model.PartySize?.ToString(CultureInfo.InvariantCulture))
            .Replace("{date}", local is { } d ? FormatDate(d, language) : null)
            .Replace("{time}", local is { } t ? FormatTime(t, language) : null)
            .Replace("{amount}", model.Amount is { } amount ? FormatAmount(amount) : null)
            .Replace("{policy}", model.Policy);
        foreach (var action in actions)
        {
            body.Append(' ').Append(templates.Get(LabelKey(action.Kind), language)).Append(": ").Append(action.Url);
        }

        return new ComposedMessage(body.ToString(), [.. actions.Select(a => new MessageAction(KindOf(a.Kind), a.Url))]);
    }

    /// <summary>The stored kind of a message action: <c>confirm</c>, <c>cancel</c> or <c>manage</c>.</summary>
    public static string KindOf(LinkAction action) => action.ToString().ToLowerInvariant();

    /// <summary>
    /// The cancellation wording from the booking's own terms (Design §2.6, BR-07), in the guest's language. The
    /// reminder shows it and <c>ReminderRecord.PolicyShown</c> stores exactly this text (AC5.6).
    /// </summary>
    public string CancellationPolicy(BookingTerms terms, Money? deposit, Language language)
    {
        ArgumentNullException.ThrowIfNull(terms);
        var key = deposit is null
            ? "CancellationPolicyNoDeposit"
            : terms.Cancellation.RefundWhenInTime == RefundAmount.Full ? "CancellationPolicyDeposit" : "CancellationPolicyDepositKept";
        return new StringBuilder(templates.Get(key, language))
            .Replace("{hours}", terms.Cancellation.FreeUntilHoursBefore.ToString(CultureInfo.InvariantCulture))
            .Replace("{amount}", deposit is { } d ? FormatAmount(d) : null)
            .ToString();
    }

    private static string LabelKey(LinkAction action) => "Action" + action;

    public static string FormatDate(DateTimeOffset local, Language language) => language == Language.Zh
        ? string.Create(CultureInfo.InvariantCulture, $"{local.Month}月{local.Day}日（{_chineseDays[(int)local.DayOfWeek]}）")
        : string.Create(CultureInfo.InvariantCulture, $"{_englishDays[(int)local.DayOfWeek]} {local.Day} {_englishMonths[local.Month - 1]}");

    /// <summary>An amount as <c>NZ$120.00</c> in both languages (M8a); other currencies as <c>120.00 AUD</c>.</summary>
    public static string FormatAmount(Money amount)
    {
        var value = string.Create(CultureInfo.InvariantCulture, $"{amount.Cents / 100}.{Math.Abs(amount.Cents % 100):00}");
        return amount.Currency == Money.Nzd ? "NZ$" + value : value + " " + amount.Currency;
    }

    public static string FormatTime(DateTimeOffset local, Language language)
    {
        if (language == Language.Zh)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{local.Hour}:{local.Minute:00}");
        }

        var hour = local.Hour % 12 == 0 ? 12 : local.Hour % 12;
        return string.Create(CultureInfo.InvariantCulture, $"{hour}:{local.Minute:00} {(local.Hour < 12 ? "am" : "pm")}");
    }
}
