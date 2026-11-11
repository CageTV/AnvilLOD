using System.Text;
using AnvilLOD.Core.World;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

public enum LargeRefIssue
{
    Ok,
    /// <summary>One of DynDOLOD's own per-cell markers (Worshippers): DynDOLOD lists them as large references so the engine keeps them loaded from afar. They stand for no object of the load order, so nothing can make them stale.</summary>
    OwnMarker,
    /// <summary>The listed reference is not in the load order any more.</summary>
    Missing,
    /// <summary>The winning override deletes it.</summary>
    Deleted,
    /// <summary>It starts disabled now: the engine would keep a disabled object in the large reference grid.</summary>
    StartsDisabled,
    /// <summary>It now lies in another cell than the one the list puts it in.</summary>
    MovedCell,
}

public sealed record LargeRefFinding(string Worldspace, int CellX, int CellY, FormKey Reference, LargeRefIssue Issue, string Detail, string? WinningPlugin);

public sealed record LargeRefReport(
    string? Plugin, IReadOnlyList<LargeRefFinding> Findings, IReadOnlyList<string> Warnings)
{
    public IEnumerable<LargeRefFinding> Problems => Findings.Where(f => f.Issue is not (LargeRefIssue.Ok or LargeRefIssue.OwnMarker));

    public string ToCsv(bool all = false)
    {
        var sb = new StringBuilder("worldspace,cell_x,cell_y,reference,issue,winning_plugin,detail\n");
        foreach (var f in all ? Findings : Problems)
            sb.Append(f.Worldspace).Append(',').Append(f.CellX).Append(',').Append(f.CellY).Append(',').Append(f.Reference.ID.ToString("X6")).Append(':')
              .Append(f.Reference.ModKey.FileName).Append(',').Append(f.Issue).Append(',').Append(f.WinningPlugin ?? "").Append(",\"")
              .Append(f.Detail.Replace("\"", "'")).Append("\"\n");
        return sb.ToString();
    }
}

/// <summary>
/// Read-only check of the large reference lists DynDOLOD wrote (the worldspace overrides in DynDOLOD.esm) against the load order as it is now.
/// The engine keeps the listed references loaded from far away; an entry whose reference was deleted, now starts disabled or moved to another
/// cell leaves a ghost, a gap or a flicker. Nothing is written. DynDOLOD.esm is read from the output folder, so it need not be enabled.
/// </summary>
public static class LargeRefCheck
{
    private const int FlagInitiallyDisabled = 0x0000_0800;

    public static LargeRefReport Run(GameContext game, string dyndolodOutput, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var path = Path.Combine(dyndolodOutput, "DynDOLOD.esm");
        if (!File.Exists(path)) return new LargeRefReport(null, [], [$"No DynDOLOD.esm in {dyndolodOutput}: large reference lists not checked."]);

        var cache = game.LinkCache;
        var findings = new List<LargeRefFinding>();
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        // The listed references are DynDOLOD.esm's own copies; DynDOLOD.esm is usually not enabled, so they come from the file itself.
        var own = new Dictionary<FormKey, IPlacedObjectGetter>();
        foreach (var pr in mod.EnumerateMajorRecords<IPlacedObjectGetter>()) own[pr.FormKey] = pr;
        int entries = 0;
        foreach (var ws in mod.Worldspaces)
        {
            if (ws.LargeReferences is not { Count: > 0 } lr) continue;
            var name = ws.EditorID ?? (cache.TryResolve<IWorldspaceGetter>(ws.FormKey, out var w) ? w.EditorID : null) ?? ws.FormKey.ToString();
            foreach (var cell in lr)
            {
                ct.ThrowIfCancellationRequested();
                int cx = cell.GridPosition.X, cy = cell.GridPosition.Y;
                foreach (var entry in cell.References)
                {
                    if (++entries % 5000 == 0) progress?.Report($"Checked {entries:N0} large reference entries");
                    var key = entry.Reference.FormKey;
                    if (!own.TryGetValue(key, out var r) && !cache.TryResolve<IPlacedObjectGetter>(key, out r))
                    { findings.Add(new(name, cx, cy, key, LargeRefIssue.Missing, "reference not in DynDOLOD.esm or the load order", null)); continue; }
                    if (own.ContainsKey(key)) { findings.Add(new(name, cx, cy, key, LargeRefIssue.OwnMarker, r.EditorID ?? "", "DynDOLOD.esm")); continue; }
                    var winner = cache.ResolveContext<IPlacedObject, IPlacedObjectGetter>(key).ModKey.FileName.String;
                    if (r.IsDeleted) { findings.Add(new(name, cx, cy, key, LargeRefIssue.Deleted, $"deleted by {winner}", winner)); continue; }
                    if ((r.MajorRecordFlagsRaw & FlagInitiallyDisabled) != 0)
                    { findings.Add(new(name, cx, cy, key, LargeRefIssue.StartsDisabled, $"starts disabled ({winner})", winner)); continue; }
                    if (r.Placement?.Position is { } p)
                    {
                        var now = CellCoord.FromWorld(p.X, p.Y);
                        int ex = entry.Position.X, ey = entry.Position.Y;     // the reference's own cell as listed
                        if (now.X != ex || now.Y != ey)
                        { findings.Add(new(name, cx, cy, key, LargeRefIssue.MovedCell, $"listed in cell ({ex}, {ey}), now in ({now.X}, {now.Y}) ({winner})", winner)); continue; }
                    }
                    findings.Add(new(name, cx, cy, key, LargeRefIssue.Ok, "", winner));
                }
            }
        }
        if (entries == 0) warnings.Add("DynDOLOD.esm lists no large references.");
        return new LargeRefReport(path, findings, warnings);
    }
}
