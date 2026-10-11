using System.Text;

namespace AnvilLOD.Core.Lod;

/// <summary>
/// One rule line as DynDOLOD's Mesh and Reference rules list shows it, kept as text so a rule that isn't touched is
/// written back exactly as it was read. <see cref="LodRules"/> turns the same lines into the rules the generator uses.
/// </summary>
public sealed class RuleEntry
{
    /// <summary>Mesh mask ("tree", "\road") or reference/base FormID ("Skyrim.esm;000FCC65").</summary>
    public string Mask { get; set; } = "";
    public string Lod4 { get; set; } = "";
    public string Lod8 { get; set; } = "";
    public string Lod16 { get; set; } = "";
    public string Lod32 { get; set; } = "";
    public string Grid { get; set; } = "";
    public string Reference { get; set; } = "Unchanged";
    public int Flags { get; set; }
    public string Description { get; set; } = "";
    /// <summary>The file the rule came from, for display.</summary>
    public string Source { get; set; } = "";
    /// <summary>A rule of the user's own custom file (written back to it) rather than one of the defaults.</summary>
    public bool IsCustom { get; set; }

    // DynDOLOD's Flags column is a bit mask.
    public const int FlagVwd = 1, FlagNoGlow = 2, FlagNoMato = 4, FlagDynamic = 8, FlagTree = 16;

    /// <summary>The catch-all rule, which DynDOLOD keeps last.</summary>
    public bool IsCatchAll => Mask == "\\";

    public bool HasFlag(int flag) => (Flags & flag) != 0;
    public void SetFlag(int flag, bool on) => Flags = on ? Flags | flag : Flags & ~flag;

    public static readonly string[] LodChoices =
        ["None", "Level0", "Level1", "Level2", "Level3", "Full model", "Billboard1", "Billboard2", "Billboard3", "Billboard4", "Billboard5", "Billboard6"];
    public static readonly string[] GridChoices = ["None", "Near LOD", "Far LOD", "Far Full", "Never Fade LOD"];
    public static readonly string[] ReferenceChoices =
        ["Unchanged", "Replace", "Disable", "KeepParent", "KeepChild", "KeepBoth", "Original", "Copy", "Delete", "Enable", "Ignore"];

    /// <summary>Reads the value of a <c>LODGen1=…</c> line (7-, 8- or 9-column, like <see cref="LodRules.ParseRule"/>).</summary>
    public static RuleEntry? Parse(string value, string source, bool isCustom = false)
    {
        var c = value.Split(',');
        if (c.Length < 4) return null;
        var mask = c[0].Trim().Replace('/', '\\');
        if (mask.Length == 0) return null;

        bool hasLod32 = c.Length >= 8;
        int gridCol = hasLod32 ? 5 : 4;
        string At(int i) => i < c.Length ? c[i].Trim() : "";
        int.TryParse(At(gridCol + 2), out var flags);
        return new RuleEntry
        {
            Mask = mask,
            Lod4 = At(1),
            Lod8 = At(2),
            Lod16 = At(3),
            Lod32 = hasLod32 ? At(4) : "",    // as the generator reads it (LodRules.ParseRule): a 7-column line has no LOD32, so none (DynDOLOD gives it none either)
            Grid = At(gridCol),
            Reference = At(gridCol + 1) is { Length: > 0 } r ? r : "Unchanged",
            Flags = flags,
            Description = hasLod32 ? At(8) : "",
            Source = source,
            IsCustom = isCustom,
        };
    }

    /// <summary>The 9-column line (DynDOLOD 3), without the "LODGenN=" part.</summary>
    public string Format()
    {
        static string Clean(string s) => s.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ').Trim();
        static string Choice(string s) => string.Equals(s.Trim(), "None", StringComparison.OrdinalIgnoreCase) ? "" : Clean(s);
        return string.Join(',', Clean(Mask), Choice(Lod4), Choice(Lod8), Choice(Lod16), Choice(Lod32),
            string.Equals(Grid.Trim(), "None", StringComparison.OrdinalIgnoreCase) ? "" : Clean(Grid),
            Clean(Reference.Length == 0 ? "Unchanged" : Reference), Flags, Clean(Description));
    }

    public RuleEntry Clone() => (RuleEntry)MemberwiseClone();

    /// <summary>The rules of a file, in order (lines sorted by their LODGen number, like the generator reads them).</summary>
    public static List<RuleEntry> ParseFile(string content, string source, bool isCustom = false)
    {
        var lines = new List<(int N, string Value)>();
        bool settings = false;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '/') continue;
            if (line[0] == '[') { settings = line.TrimEnd(']').EndsWith("Settings", StringComparison.OrdinalIgnoreCase); continue; }
            int eq = line.IndexOf('=');
            if (eq < 0 || settings || !line.StartsWith("LODGen", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line.AsSpan(6, eq - 6), out var n)) n = int.MaxValue;
            lines.Add((n, line[(eq + 1)..]));
        }
        var result = new List<RuleEntry>();
        foreach (var (_, value) in lines.OrderBy(l => l.N))
            if (Parse(value, source, isCustom) is { } e) result.Add(e);
        return result;
    }

    /// <summary>
    /// The text of a rule file for these rules, in DynDOLOD's format. The catch-all rule (mask <c>\</c>) is written last, because the
    /// first matching rule wins and it matches everything.
    /// </summary>
    public static string FormatFile(IEnumerable<RuleEntry> rules, string header = "AnvilLOD custom LOD rules (DynDOLOD mesh mask / reference rule format)")
    {
        var list = rules.ToList();
        var ordered = list.Where(r => !r.IsCatchAll).Concat(list.Where(r => r.IsCatchAll)).ToList();
        var sb = new StringBuilder();
        sb.Append("; ").AppendLine(header);
        sb.AppendLine("; Mask,LOD4,LOD8,LOD16,LOD32,Grid,Reference,Flags,Description. Flags: 1 VWD, 2 NoGlow, 4 NoMATO, 8 Dynamic, 16 TREE.");
        sb.AppendLine("[Skyrim LODGen]");
        for (int i = 0; i < ordered.Count; i++)
            sb.Append("LODGen").Append(i + 1).Append('=').AppendLine(ordered[i].Format());
        return sb.ToString();
    }
}
