namespace AnvilLOD.Core.World;

/// <summary>
/// Picks a stand-in tree model for a tree record whose model file exists nowhere (a mod that was released without all of its
/// meshes). The choice is by name: the same model family in the same folder first (treemaple04 -> treemaple03), then the longest
/// shared name prefix in the same folder. A hand-written mapping (<c>missing model = replacement model</c>) always wins.
/// </summary>
public static class TreeStandIns
{
    public const string IniName = "AnvilLOD Tree Stand-ins.ini";

    /// <summary>Parses lines of "meshes\missing.nif = meshes\replacement.nif"; ';' and '#' start a comment line.</summary>
    public static Dictionary<string, string> ParseIni(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var from = GamePath.Normalize(line[..eq].Trim());
            var to = GamePath.Normalize(line[(eq + 1)..].Trim());
            if (from.Length > 0 && to.Length > 0) map[from] = to;
        }
        return map;
    }

    /// <param name="missing">The model path that does not exist (normalized, "meshes\..." form).</param>
    /// <param name="available">Model paths of tree records whose file does exist.</param>
    public static string? Choose(string missing, IReadOnlyCollection<string> available, IReadOnlyDictionary<string, string>? overrides = null)
    {
        missing = GamePath.Normalize(missing);
        if (overrides is not null && overrides.TryGetValue(missing, out var forced)) return forced;

        var dir = DirOf(missing);
        var (family, number) = Split(Stem(missing));
        string? best = null;
        int bestScore = int.MinValue;
        foreach (var cand in available)
        {
            if (string.Equals(cand, missing, StringComparison.OrdinalIgnoreCase) || !string.Equals(DirOf(cand), dir, StringComparison.OrdinalIgnoreCase)) continue;
            var (cf, cn) = Split(Stem(cand));
            int score;
            if (string.Equals(cf, family, StringComparison.OrdinalIgnoreCase))
                score = 1000 - Math.Abs(cn - number);                       // same family: the nearest number
            else
            {
                int prefix = CommonPrefix(cf, family);
                if (prefix < Math.Max(6, family.Length * 6 / 10)) continue;  // not alike enough to pass for it
                score = prefix * 10 - Math.Abs(cn - number);
            }
            if (score > bestScore || (score == bestScore && string.Compare(cand, best, StringComparison.OrdinalIgnoreCase) < 0)) { best = cand; bestScore = score; }
        }
        return best;
    }

    private static string DirOf(string p) { int i = p.LastIndexOf('\\'); return i < 0 ? "" : p[..i]; }

    private static string Stem(string p)
    {
        var name = p[(p.LastIndexOf('\\') + 1)..];
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    private static (string Family, int Number) Split(string stem)
    {
        int end = stem.Length;
        while (end > 0 && char.IsDigit(stem[end - 1])) end--;
        return (stem[..end], end < stem.Length && int.TryParse(stem[end..], out var n) ? n : 0);
    }

    private static int CommonPrefix(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length), i = 0;
        while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
        return i;
    }
}
