using System.Globalization;
using System.Resources;
using Reservation.Domain.Common;
using Reservation.Domain.Messaging;

namespace Reservation.Api.Messaging;

/// <summary>Message templates per purpose and language (Design §7).</summary>
internal interface ITemplateStore
{
    /// <summary>The template text with its <c>{placeholders}</c>; throws when there is none, which a test catches.</summary>
    string Get(MessagePurpose purpose, Language language);
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

    public string Get(MessagePurpose purpose, Language language)
    {
        var resources = language switch
        {
            Language.En => _english,
            Language.Zh => _chinese,
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, "No templates for this language."),
        };

        var text = resources.GetString(purpose.ToString(), CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException($"No {language} template for {purpose}.")
            : text;
    }

    private static ResourceManager Load(string name) =>
        new($"{typeof(ResxTemplateStore).Namespace}.Templates.{name}", typeof(ResxTemplateStore).Assembly);
}
