namespace AnvilLOD.Cli;

public sealed class CliArgException(string message) : Exception(message);

/// <summary>Minimal "--key value" / "--flag" parser. Keys may repeat.</summary>
public sealed class CliArgs
{
    private static readonly HashSet<string> Flags = ["include-disabled", "no-enable-parented", "keep-buried", "no-trees", "no-grass", "water-standins", "no-dynamic", "no-child-worlds", "no-grid-objects", "seasons", "tree-3d", "tree-3d-lod8", "tree-3d-by-name", "tree-3d-crc-only", "underside", "pbr-lod", "no-meshes", "no-billboards", "no-rules", "overwrite", "large-refs", "large-refs-no-esl", "candles", "fxglow", "keep-output"];
    private static readonly HashSet<string> Valued = ["data", "plugins", "mo2", "profile", "worldspace", "output", "report", "dyndolod", "preset", "grass-density", "grass-top", "grass-bottom", "tree-brightness", "object-brightness", "skse-dll", "underside-detail", "pbr-lod-brightness", "pbr-lod-size", "mo2-game", "mo2-mods", "mo2-profiles", "mo2-overwrite", "plugin", "author-out", "min-size", "min-tree-height", "input", "group", "levels", "detail", "new-mod", "budget", "rules", "lang"];

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    public static CliArgs Parse(string[] args)
    {
        var r = new CliArgs();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--")) throw new CliArgException(L.F("Err_UnexpectedArgFmt", a));
            var key = a[2..].ToLowerInvariant();

            if (Flags.Contains(key)) { r.Add(key, "true"); continue; }
            if (!Valued.Contains(key)) throw new CliArgException(L.F("Err_UnknownOptionFmt", a));
            if (i + 1 >= args.Length) throw new CliArgException(L.F("Err_OptionNeedsValueFmt", a));
            r.Add(key, args[++i]);
        }
        return r;
    }

    private void Add(string k, string v)
    {
        if (!_values.TryGetValue(k, out var list)) _values[k] = list = [];
        list.Add(v);
    }

    public bool Has(string k) => _values.ContainsKey(k);
    public string? Get(string k) => _values.TryGetValue(k, out var l) ? l[^1] : null;
    public IReadOnlyCollection<string>? GetAll(string k) => _values.TryGetValue(k, out var l) ? l : null;
}
