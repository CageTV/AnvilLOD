using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace AnvilLOD.App.Localization;

/// <summary>
/// {loc:Loc Key} binds the property to the localized string for Key and keeps it updated: when the language changes,
/// LocSource.Refresh() makes every binding re-resolve. Works on element properties and on DataGrid column headers
/// (the binding carries its own source, so it needs no DataContext).
/// </summary>
[MarkupExtensionReturnType(typeof(Binding))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        // Binding is itself a markup extension: hand it the provider so it produces a binding expression
        // for this property (and works on DependencyObjects such as DataGrid columns, not just elements).
        var binding = new Binding
        {
            Source = LocSource.Shared,
            Path = new PropertyPath("Item[" + Key + "]"),
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
