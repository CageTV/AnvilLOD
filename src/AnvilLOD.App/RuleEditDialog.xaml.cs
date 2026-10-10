using System.Windows;
using AnvilLOD.Core.Lod;

namespace AnvilLOD.App;

/// <summary>Edits one LOD rule (the same fields as DynDOLOD's "Edit Mesh Mask / Reference Rule" window).</summary>
public partial class RuleEditDialog : Window
{
    private readonly RuleEntry _rule;

    /// <param name="rule">A copy of the rule to edit; its fields are updated when the user presses OK.</param>
    public RuleEditDialog(RuleEntry rule)
    {
        InitializeComponent();
        _rule = rule;
        MaskBox.Text = rule.Mask;
        Fill(Lod4Box, RuleEntry.LodChoices, rule.Lod4);
        Fill(Lod8Box, RuleEntry.LodChoices, rule.Lod8);
        Fill(Lod16Box, RuleEntry.LodChoices, rule.Lod16);
        Fill(Lod32Box, RuleEntry.LodChoices, rule.Lod32);
        Fill(GridBox, RuleEntry.GridChoices, rule.Grid);
        Fill(ReferenceBox, RuleEntry.ReferenceChoices, rule.Reference.Length == 0 ? "Unchanged" : rule.Reference);
        DescriptionBox.Text = rule.Description;
        VwdCheck.IsChecked = rule.HasFlag(RuleEntry.FlagVwd);
        NoGlowCheck.IsChecked = rule.HasFlag(RuleEntry.FlagNoGlow);
        NoMatoCheck.IsChecked = rule.HasFlag(RuleEntry.FlagNoMato);
        DynamicCheck.IsChecked = rule.HasFlag(RuleEntry.FlagDynamic);
        TreeCheck.IsChecked = rule.HasFlag(RuleEntry.FlagTree);
    }

    /// <summary>
    /// Fills a drop-down and selects the rule's value. A value that isn't one of the standard choices ("Static LOD4", as older
    /// DynDOLOD files write it) is added to the list, so the rule keeps it unless the user picks something else.
    /// </summary>
    private static void Fill(System.Windows.Controls.ComboBox box, string[] choices, string current)
    {
        var items = choices.ToList();
        var value = string.IsNullOrWhiteSpace(current) ? "None" : current.Trim();
        var match = items.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (match is null) { items.Insert(0, value); match = value; }
        box.ItemsSource = items;
        box.SelectedItem = match;
    }

    private static string Picked(System.Windows.Controls.ComboBox box) => box.SelectedItem as string ?? "";

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var mask = MaskBox.Text.Trim().Replace('/', '\\');
        if (mask.Length == 0)
        {
            MessageBox.Show(this, "The rule needs a mesh mask or a reference FormID.", "AnvilLOD", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _rule.Mask = mask;
        _rule.Lod4 = Picked(Lod4Box);
        _rule.Lod8 = Picked(Lod8Box);
        _rule.Lod16 = Picked(Lod16Box);
        _rule.Lod32 = Picked(Lod32Box);
        _rule.Grid = Picked(GridBox);
        _rule.Reference = Picked(ReferenceBox);
        _rule.Description = DescriptionBox.Text.Trim();
        _rule.SetFlag(RuleEntry.FlagVwd, VwdCheck.IsChecked == true);
        _rule.SetFlag(RuleEntry.FlagNoGlow, NoGlowCheck.IsChecked == true);
        _rule.SetFlag(RuleEntry.FlagNoMato, NoMatoCheck.IsChecked == true);
        _rule.SetFlag(RuleEntry.FlagDynamic, DynamicCheck.IsChecked == true);
        _rule.SetFlag(RuleEntry.FlagTree, TreeCheck.IsChecked == true);
        DialogResult = true;
    }
}
