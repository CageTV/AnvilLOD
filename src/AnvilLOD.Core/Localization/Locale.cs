using System.Globalization;

namespace AnvilLOD.Core.Localization;

/// <summary>
/// The language the UI strings resolve in, plus the languages that ship. "" (the default) follows the system;
/// a chosen culture is applied to every catalog when the app starts or the user switches language.
/// </summary>
public static class Locale
{
    /// <summary>The shipped languages: the code stored in settings ("" = follow the system) and a display name that is always in its own language.</summary>
    public static readonly (string Code, string Name)[] Languages = [("", ""), ("en", "English"), ("zh-CN", "简体中文")];

    private static CultureInfo _culture = Resolve(null);

    /// <summary>The culture string catalogs resolve in right now.</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>Sets the language from a settings code ("" or null = follow the system).</summary>
    public static void Set(string? code) => _culture = Resolve(code);

    /// <summary>
    /// Resolves a language code to a culture. A system language that isn't shipped keeps its own culture, so the
    /// catalogs fall back to the neutral (English) resources. Windows can report Chinese as zh-Hans-CN; it is mapped
    /// to the shipped zh-CN, whose fallback chain a zh-Hans-CN request would otherwise miss.
    /// </summary>
    public static CultureInfo Resolve(string? code)
    {
        var name = string.IsNullOrWhiteSpace(code) ? CultureInfo.CurrentUICulture.Name : code.Trim();
        if (name.Equals("zh", StringComparison.OrdinalIgnoreCase) || name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
            name = "zh-CN";
        try { return CultureInfo.GetCultureInfo(name); }
        catch (CultureNotFoundException) { return CultureInfo.CurrentUICulture; }
    }
}
