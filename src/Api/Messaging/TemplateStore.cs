using System.Globalization;
using System.Resources;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;

namespace Reservation.Api.Messaging;

/// <summary>Message templates per purpose and language (Design §7).</summary>
internal interface ITemplateStore
{
    /// <summary>
    /// The text stored under <paramref name="key"/> (a purpose name, or since M10a a fragment such as
    /// <c>ActionConfirm</c> or <c>CancellationPolicyDeposit</c>) with its <c>{placeholders}</c>; throws when there is
    /// none, which a test catches.
    /// </summary>
    string Get(string key, Language language);
}

internal static class TemplateStoreExtensions
{
    public static string Get(this ITemplateStore store, MessagePurpose purpose, Language language) =>
        store.Get(purpose.ToString(), language);
}

/// <summary>
/// Templates in the API's resource files (Design §7, §7.2): <c>Messaging/Templates/TemplatesEnglish.resx</c> and
/// <c>TemplatesChinese.resx</c>, one neutral resource per language, chosen by <see cref="Language"/>. No satellite
/// assemblies and no culture lookup, so the text never depends on the server's culture.
/// </summary>
internal sealed class ResxTemplateStore : ITemplateStore
{
    private static readonly ResourceManager _english = Load("TemplatesEnglish");
    private static readonly ResourceManager _chinese = Load("TemplatesChinese");

    public string Get(string key, Language language)
    {
        var resources = language switch
        {
            Language.En => _english,
            Language.Zh => _chinese,
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "No templates for this language."),
        };

        var text = resources.GetString(key, CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException($"No {language} template for {key}.")
            : text;
    }

    private static ResourceManager Load(string name) =>
        new($"{typeof(ResxTemplateStore).Namespace}.Templates.{name}", typeof(ResxTemplateStore).Assembly);
}
