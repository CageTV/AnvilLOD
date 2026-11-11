using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Incremental;

/// <summary>
/// A snapshot of what every object-LOD block was built from, taken right after a LOD tool (DynDOLOD, xLODGen, ...)
/// finished. A later "check" re-reads the load order and lists the blocks whose inputs changed since, which are the
/// blocks that are stale. Read-only: nothing here writes LOD, plugins or the load order.
/// <para>
/// The baseline never needs to know what the other tool wrote. It records the inputs (placed references, their LOD
/// meshes and transforms, plus the per-block fingerprint that also covers mesh/texture files and terrain), so it
/// works for any generator's output, including a retroactive "adopt this output as it is now" snapshot.
/// </para>
/// </summary>
public sealed class LodBaseline
{
    public const int FormatVersion = 1;
    public const string DefaultFileName = "AnvilLOD.baseline.json.gz";

    public int Format { get; set; } = FormatVersion;
    public DateTimeOffset TakenAt { get; set; }
    /// <summary>The scan options used. A check with different options compares unlike things, so it warns.</summary>
    public string Options { get; set; } = "";
    public string? Note { get; set; }
    public List<string> Plugins { get; set; } = [];
    /// <summary>"content": loose files were fingerprinted by what is in them; "mtime" (also every older baseline): by size and last-write time.</summary>
    public string Hashing { get; set; } = "mtime";
    /// <summary>The settings part of the block fingerprints.</summary>
    public string? Settings { get; set; }
    /// <summary>Per worldspace: the terrain and the water fingerprints.</summary>
    public Dictionary<string, string> Terrain { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Water { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Block relative path → what it was built from.</summary>
    public Dictionary<string, BaselineBlock> Blocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    // --- capture -----------------------------------------------------------------------------------------------

    public static LodBaseline Capture(
        IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>> quads,
        IReadOnlyDictionary<QuadKey, string> hashes,
        string options, string? note = null, DateTimeOffset? now = null, BaselineComponents? components = null)
    {
        var b = new LodBaseline { TakenAt = now ?? DateTimeOffset.Now, Options = options, Note = note };
        if (components is { ContentHashed: true })
        {
            b.Hashing = "content";
            b.Settings = components.Settings;
            foreach (var (k, v) in components.Terrain ?? new Dictionary<string, string>()) b.Terrain[k] = v;
            foreach (var (k, v) in components.Water ?? new Dictionary<string, string>()) b.Water[k] = v;
        }
        var pluginIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int Plugin(string p)
        {
            if (!pluginIndex.TryGetValue(p, out var i)) { i = b.Plugins.Count; b.Plugins.Add(p); pluginIndex[p] = i; }
            return i;
        }
        foreach (var (q, refs) in quads.OrderBy(kv => kv.Key.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!hashes.TryGetValue(q, out var hash)) continue;
            var block = new BaselineBlock { Hash = hash };
            if (components?.QuadAssets is { } qa && qa.TryGetValue(q, out var assetsHash)) block.Assets = assetsHash;
            foreach (var r in refs)
                block.Refs[r.FormKey] = $"{RefDigest(r, q.Level)}:{Plugin(r.WinningPlugin)}";
            b.Blocks[q.RelativePath] = block;
        }
        return b;
    }

    /// <summary>Short digest of everything about one reference that shapes its LOD at <paramref name="level"/>.</summary>
    public static string RefDigest(LodReference r, LodLevel level)
    {
        var sb = new StringBuilder();
        sb.Append(r.BaseFormKey).Append('|').Append(r.Meshes.For(level)).Append('|').Append(r.TextureVariant).Append('|')
          .Append(r.Shader).Append('|').Append((int)r.Flags);
        foreach (var f in new[] { r.Position.X, r.Position.Y, r.Position.Z, r.RotationRadians.X, r.RotationRadians.Y, r.RotationRadians.Z, r.Scale })
            sb.Append('|').Append(BitConverter.SingleToInt32Bits(f));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 6);
    }

    // --- rebase ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A copy of this baseline that keeps what each block was built from (its references, and the time it was taken) but takes the file state of
    /// the present: blocks whose references are still identical adopt today's fingerprint, so later checks see only what changes from now on in the
    /// files, the terrain and the water. Blocks whose references differ keep their old fingerprint and stay stale. For a baseline made before content
    /// fingerprints existed, taken at the time the LOD tool ran, when the support apps have been rerun since: it cannot know what their files held then.
    /// </summary>
    public LodBaseline WithCurrentFiles(
        IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>> quads, IReadOnlyDictionary<QuadKey, string> hashes,
        BaselineComponents components, out int adopted, out int keptStale)
    {
        var b = new LodBaseline
        {
            TakenAt = TakenAt, Options = Options, Plugins = [.. Plugins], Hashing = "content", Settings = components.Settings,
            Note = ((Note is { Length: > 0 } n ? n + "; " : "") + $"file state rebased onto {DateTimeOffset.Now:yyyy-MM-dd HH:mm}"),
        };
        foreach (var (k, v) in components.Terrain ?? new Dictionary<string, string>()) b.Terrain[k] = v;
        foreach (var (k, v) in components.Water ?? new Dictionary<string, string>()) b.Water[k] = v;

        var byPath = quads.Keys.ToDictionary(q => q.RelativePath, q => q, StringComparer.OrdinalIgnoreCase);
        adopted = 0; keptStale = 0;
        foreach (var (rel, old) in Blocks)
        {
            var nb = new BaselineBlock { Hash = old.Hash, Refs = new Dictionary<string, string>(old.Refs, StringComparer.OrdinalIgnoreCase) };
            if (byPath.TryGetValue(rel, out var q) && hashes.TryGetValue(q, out var hash) && SameRefs(old, quads[q], q.Level))
            {
                nb.Hash = hash;
                if (components.QuadAssets is { } qa && qa.TryGetValue(q, out var assets)) nb.Assets = assets;
                adopted++;
            }
            else keptStale++;
            b.Blocks[rel] = nb;
        }
        return b;
    }

    private static bool SameRefs(BaselineBlock old, IReadOnlyList<LodReference> refs, LodLevel level)
    {
        if (old.Refs.Count != refs.Count) return false;
        foreach (var r in refs)
            if (!old.Refs.TryGetValue(r.FormKey, out var v) || !v.StartsWith(RefDigest(r, level) + ":", StringComparison.Ordinal)) return false;
        return true;
    }

    // --- diff --------------------------------------------------------------------------------------------------

    /// <param name="scopeWorldspaces">Worldspaces this check covers. Blocks of other worldspaces in the baseline are
    /// not reported as removed just because this run did not scan them.</param>
    public StaleReport Diff(
        IReadOnlyDictionary<QuadKey, IReadOnlyList<LodReference>> quads,
        IReadOnlyDictionary<QuadKey, string> hashes,
        IReadOnlyCollection<string> scopeWorldspaces, string options, bool ignoreAssetOnly = false, BaselineComponents? nowParts = null)
    {
        var scope = new HashSet<string>(scopeWorldspaces, StringComparer.OrdinalIgnoreCase);
        var stale = new List<BlockDiff>();
        int unchanged = 0, assetOnlyIgnored = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (q, refs) in quads.OrderBy(kv => kv.Key.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!hashes.TryGetValue(q, out var hash)) continue;
            var rel = q.RelativePath;
            seen.Add(rel);
            if (!Blocks.TryGetValue(rel, out var old))
            {
                stale.Add(new BlockDiff(rel, q.Worldspace, (int)q.Level, BlockChange.New, refs.Count, 0, 0, false,
                    Count(refs.Select(r => r.WinningPlugin))));
                continue;
            }
            if (old.Hash == hash) { unchanged++; continue; }

            int added = 0, moved = 0;
            var plugins = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in refs)
            {
                now.Add(r.FormKey);
                if (!old.Refs.TryGetValue(r.FormKey, out var oldVal)) { added++; Bump(plugins, r.WinningPlugin); }
                else if (!oldVal.StartsWith(RefDigest(r, q.Level) + ":", StringComparison.Ordinal)) { moved++; Bump(plugins, r.WinningPlugin); }
            }
            int removed = 0;
            var removedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (fk, val) in old.Refs)
            {
                if (now.Contains(fk)) continue;
                removed++;
                Bump(removedBy, PluginOf(val));
            }
            // Same references, same placement, but the block's fingerprint moved: a LOD/full mesh or texture file,
            // a texture variant, the terrain under it, or water changed.
            bool assetOnly = added == 0 && removed == 0 && moved == 0;
            // A support app that rewrote a mesh, texture or the terrain/water data gives the block a new fingerprint without any object changing.
            if (ignoreAssetOnly && assetOnly) { unchanged++; assetOnlyIgnored++; continue; }
            stale.Add(new BlockDiff(rel, q.Worldspace, (int)q.Level, BlockChange.Changed, added, removed, moved, assetOnly, plugins, removedBy,
                assetOnly ? Reasons(old, q, nowParts) : AssetChange.None));
        }

        foreach (var (rel, old) in Blocks)
        {
            if (seen.Contains(rel)) continue;
            if (!BuildManifest.TryParseBlockPath(rel, out var ws, out var lv) || !scope.Contains(ws)) continue;
            var removedBy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var val in old.Refs.Values) Bump(removedBy, PluginOf(val));
            stale.Add(new BlockDiff(rel, ws, lv, BlockChange.Removed, 0, old.Refs.Count, 0, false, new Dictionary<string, int>(), removedBy));
        }

        return new StaleReport(TakenAt, unchanged + stale.Count, unchanged, stale,
            string.Equals(options, Options, StringComparison.Ordinal) ? null
                : $"The scan options differ from the snapshot's (snapshot: '{Options}', now: '{options}'). Blocks may show as changed only because of that; take a new snapshot or repeat the same options.",
            assetOnlyIgnored);
    }

    /// <summary>For a block whose objects are unchanged: which parts of its fingerprint moved. Needs a baseline and a check that both hash by content.</summary>
    private AssetChange Reasons(BaselineBlock old, QuadKey q, BaselineComponents? now)
    {
        if (now is not { ContentHashed: true } || Hashing != "content" || old.Assets is null) return AssetChange.Unknown;
        var r = AssetChange.None;
        if (now.QuadAssets is { } qa && qa.TryGetValue(q, out var a) && a != old.Assets) r |= AssetChange.Files;
        if (Terrain.TryGetValue(q.Worldspace, out var ot) && now.Terrain is { } nt && nt.TryGetValue(q.Worldspace, out var tt) && ot != tt) r |= AssetChange.Terrain;
        if (Water.TryGetValue(q.Worldspace, out var ow) && now.Water is { } nw && nw.TryGetValue(q.Worldspace, out var tw) && ow != tw) r |= AssetChange.Water;
        if (Settings is not null && now.Settings is not null && Settings != now.Settings) r |= AssetChange.Settings;
        return r == AssetChange.None ? AssetChange.Unknown : r;
    }

    private string PluginOf(string refValue)
    {
        var i = refValue.LastIndexOf(':');
        return i >= 0 && int.TryParse(refValue.AsSpan(i + 1), out var idx) && idx >= 0 && idx < Plugins.Count ? Plugins[idx] : "?";
    }

    private static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    private static Dictionary<string, int> Count(IEnumerable<string> items)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items) Bump(d, i);
        return d;
    }

    // --- persistence -------------------------------------------------------------------------------------------

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gz, this, Json);
        File.Move(tmp, path, overwrite: true);
    }

    public static LodBaseline Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        var b = JsonSerializer.Deserialize<LodBaseline>(gz, Json) ?? throw new InvalidDataException("Empty baseline file.");
        if (b.Format != FormatVersion) throw new InvalidDataException($"Baseline format {b.Format} is not supported (expected {FormatVersion}); take a new snapshot.");
        return b;
    }
}

public sealed class BaselineBlock
{
    public string Hash { get; set; } = "";
    /// <summary>The file part of the fingerprint (mesh and texture-variant fingerprints), when the baseline hashes by content.</summary>
    public string? Assets { get; set; }
    /// <summary>FormKey → "digest:pluginIndex".</summary>
    public Dictionary<string, string> Refs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public enum BlockChange { New, Changed, Removed }

/// <summary>What moved in the fingerprint of a block whose objects did not change.</summary>
[Flags]
public enum AssetChange { None = 0, Files = 1, Terrain = 2, Water = 4, Settings = 8, Unknown = 16 }

/// <summary>The parts of a scan's block fingerprints that a baseline keeps next to the full hash.</summary>
public sealed record BaselineComponents(
    IReadOnlyDictionary<QuadKey, string>? QuadAssets,
    IReadOnlyDictionary<string, string>? Terrain,
    IReadOnlyDictionary<string, string>? Water,
    string? Settings,
    bool ContentHashed);

public sealed class BlockDiff(string relativePath, string worldspace, int level, BlockChange kind,
    int refsAdded, int refsRemoved, int refsMoved, bool assetOnly, IReadOnlyDictionary<string, int> byPlugin,
    IReadOnlyDictionary<string, int>? removedByPlugin = null, AssetChange assetReasons = AssetChange.None)
{
    public string RelativePath { get; } = relativePath;
    public string Worldspace { get; } = worldspace;
    public int Level { get; } = level;
    public BlockChange Kind { get; } = kind;
    public int RefsAdded { get; } = refsAdded;
    public int RefsRemoved { get; } = refsRemoved;
    /// <summary>References present in both whose mesh, base, transform or flags differ.</summary>
    public int RefsMoved { get; } = refsMoved;
    /// <summary>The references are identical; a mesh/texture file, texture variant, terrain or water fingerprint changed.</summary>
    public bool AssetOnly { get; } = assetOnly;
    /// <summary>For an asset-only block: which parts of the fingerprint changed (files, terrain, water, settings), or Unknown.</summary>
    public AssetChange AssetReasons { get; } = assetReasons;
    /// <summary>Plugins whose references were added or changed (the current winner).</summary>
    public IReadOnlyDictionary<string, int> ByPlugin { get; } = byPlugin;
    /// <summary>Plugins that owned the references that are gone (the old winner in the snapshot), not the plugin that removed them.</summary>
    public IReadOnlyDictionary<string, int> RemovedByPlugin { get; } = removedByPlugin ?? new Dictionary<string, int>();
    /// <summary>Set by the caller: the other tool's block file exists on disk.</summary>
    public bool? OnDisk { get; set; }
}

public sealed record StaleReport(
    DateTimeOffset BaselineTaken,
    int BlocksCompared,
    int Unchanged,
    IReadOnlyList<BlockDiff> Stale,
    string? OptionsWarning,
    int AssetOnlyIgnored = 0)
{
    /// <summary>Asset-only blocks by what changed. One block can count under several parts.</summary>
    public IReadOnlyDictionary<AssetChange, int> AssetBreakdown()
    {
        var d = new Dictionary<AssetChange, int>();
        foreach (var b in Stale.Where(x => x.AssetOnly))
            foreach (var f in new[] { AssetChange.Files, AssetChange.Terrain, AssetChange.Water, AssetChange.Settings, AssetChange.Unknown })
                if (b.AssetReasons.HasFlag(f)) d[f] = d.GetValueOrDefault(f) + 1;
        return d;
    }

    public IEnumerable<IGrouping<string, BlockDiff>> ByWorldspace => Stale.GroupBy(d => d.Worldspace, StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugins ranked by how many stale blocks they touch.</summary>
    public IEnumerable<(string Plugin, int Blocks, int Refs)> ByPlugin() => Rank(d => d.ByPlugin);

    /// <summary>Plugins whose references disappeared from LOD, ranked by blocks.</summary>
    public IEnumerable<(string Plugin, int Blocks, int Refs)> RemovedByPlugin() => Rank(d => d.RemovedByPlugin);

    private IEnumerable<(string Plugin, int Blocks, int Refs)> Rank(Func<BlockDiff, IReadOnlyDictionary<string, int>> pick) =>
        Stale.SelectMany(d => pick(d).Select(kv => (Plugin: kv.Key, d, Refs: kv.Value)))
             .GroupBy(x => x.Plugin, StringComparer.OrdinalIgnoreCase)
             .Select(g => (g.Key, g.Select(x => x.d).Distinct().Count(), g.Sum(x => x.Refs)))
             .OrderByDescending(x => x.Item2);

    private static string Join(IReadOnlyDictionary<string, int> m) =>
        string.Join("; ", m.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} ({kv.Value})"));

    public string ToCsv()
    {
        var sb = new StringBuilder("block,worldspace,level,change,refs_added,refs_removed,refs_changed,asset_only,on_disk,added_or_changed_by,removed_were_from,asset_reasons\n");
        foreach (var d in Stale)
            sb.Append(d.RelativePath).Append(',').Append(d.Worldspace).Append(',').Append(d.Level).Append(',').Append(d.Kind).Append(',')
              .Append(d.RefsAdded).Append(',').Append(d.RefsRemoved).Append(',').Append(d.RefsMoved).Append(',').Append(d.AssetOnly).Append(',')
              .Append(d.OnDisk is null ? "" : d.OnDisk.ToString()).Append(",\"").Append(Join(d.ByPlugin)).Append("\",\"").Append(Join(d.RemovedByPlugin)).Append("\",").Append(d.AssetOnly ? d.AssetReasons.ToString().Replace(", ", "+") : "").Append('\n');
        return sb.ToString();
    }
}
