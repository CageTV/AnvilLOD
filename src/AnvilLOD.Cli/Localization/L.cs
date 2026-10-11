using AnvilLOD.Core.Localization;

namespace AnvilLOD.Cli;

/// <summary>Access to the CLI's strings: L.T("Key") for plain strings, L.F("Key", args) for formatted ones.</summary>
public static class L
{
    public static readonly Catalog Strings = new("AnvilLOD.Cli.Localization.Strings", typeof(L).Assembly);

    public static string T(string key) => Strings.Get(key);

    public static string F(string key, params object?[] args) => Strings.Format(key, args);
}
