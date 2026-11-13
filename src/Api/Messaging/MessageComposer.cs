using System.Globalization;
using System.Text;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;

namespace Reservation.Api.Messaging;

/// <summary>
/// What a template may use. <see cref="StartsAt"/> is shown in <see cref="TimeZone"/>; fields a purpose doesn't use are
/// ignored.
/// </summary>
internal sealed record TemplateModel(
    string VenueName,
    TimeZoneInfo TimeZone,
    string? Reference = null,
    int? PartySize = null,
    DateTimeOffset? StartsAt = null);

internal sealed record ComposedMessage(string Body, IReadOnlyList<MessageAction> Actions);

/// <summary>
/// Fills a template (Design §7, §7.2). The venue name is used as stored in both languages (venues have no Chinese
/// name). Dates and times are in the venue's zone and formatted by hand per language, so the text never depends on
/// the server's culture data: English <c>Fri 14 Nov</c>, <c>7:30 pm</c>; Chinese <c>11月14日（周五）</c>, <c>19:30</c>.
/// Messages carry no actions yet: the manage-booking link and the reminder's Confirm / Cancel come with M10.
/// </summary>
internal sealed class MessageComposer(ITemplateStore templates)
{
    private static readonly string[] _englishDays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] _englishMonths = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] _chineseDays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    public ComposedMessage Compose(MessagePurpose purpose, Language language, TemplateModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var local = model.StartsAt is { } startsAt ? TimeZoneInfo.ConvertTime(startsAt, model.TimeZone) : (DateTimeOffset?)null;
        var body = new StringBuilder(templates.Get(purpose, language))
            .Replace("{venue}", model.VenueName)
            .Replace("{reference}", model.Reference)
            .Replace("{party}", model.PartySize?.ToString(CultureInfo.InvariantCulture))
            .Replace("{date}", local is { } d ? FormatDate(d, language) : null)
            .Replace("{time}", local is { } t ? FormatTime(t, language) : null)
            .ToString();
        return new ComposedMessage(body, []);
    }

    public static string FormatDate(DateTimeOffset local, Language language) => language == Language.Zh
        ? string.Create(CultureInfo.InvariantCulture, $"{local.Month}月{local.Day}日（{_chineseDays[(int)local.DayOfWeek]}）")
        : string.Create(CultureInfo.InvariantCulture, $"{_englishDays[(int)local.DayOfWeek]} {local.Day} {_englishMonths[local.Month - 1]}");

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
