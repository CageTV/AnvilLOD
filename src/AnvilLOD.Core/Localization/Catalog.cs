using System.Reflection;
using System.Resources;

namespace AnvilLOD.Core.Localization;

/// <summary>
/// One project's resx string catalog. Strings resolve in <see cref="Locale.Culture"/> and fall back to the neutral
/// (English) resources; a missing key returns the key itself, so a forgotten translation shows instead of throwing.
/// </summary>
public sealed class Catalog(string baseName, Assembly assembly)
{
    private readonly ResourceManager _resources = new(baseName, assembly);

    public string Get(string key) => _resources.GetString(key, Locale.Culture) ?? key;

    public string Format(string key, params object?[] args) => string.Format(Locale.Culture, Get(key), args);
}
