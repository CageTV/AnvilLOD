using System.ComponentModel;
using AnvilLOD.Core.Localization;

namespace AnvilLOD.App.Localization;

/// <summary>Access to the app's strings from code: L.T("Key") for plain strings, L.F("Key", args) for formatted ones.</summary>
public static class L
{
    public static readonly Catalog Strings = new("AnvilLOD.App.Localization.Strings", typeof(L).Assembly);

    public static string T(string key) => Strings.Get(key);

    public static string F(string key, params object?[] args) => Strings.Format(key, args);
}

/// <summary>
/// The string source XAML binds to. Item[key] re-reads the catalog on every lookup, so raising a change for
/// "Item[]" makes every {loc:Loc} binding re-resolve — that is how a language switch repaints the UI live.
/// </summary>
public sealed class LocSource : INotifyPropertyChanged
{
    public static LocSource Shared { get; } = new();

    public string this[string key] => L.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Makes every {loc:Loc} binding re-read its string (after a language change).</summary>
    public static void Refresh() => Shared.PropertyChanged?.Invoke(Shared, new PropertyChangedEventArgs("Item[]"));
}
