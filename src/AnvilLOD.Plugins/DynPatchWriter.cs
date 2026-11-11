using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes a SMALL patch plugin (AnvilLOD_DynFix.esp) that overrides only the DynDOLOD dynamic references whose source moved or was removed since DynDOLOD ran:
/// a moved source gets its new position/rotation/scale, a removed or sunk one is sunk to z -30000. DynDOLOD.esp itself is never copied or rewritten
/// (a full edited copy of it hung the water cache), and the DynDOLOD data files are not touched: the enable-state rows of the NG format are not
/// verified yet, so those findings are only counted, never written. The output folder must be new or empty.
/// </summary>
public static class DynPatchWriter
{
    public const string FileName = "AnvilLOD_DynFix.esp";
    private const float SunkZ = -30000f;

    public sealed record Result(string? Plugin, int Repositioned, int Sunk, int EnableStateSkipped, IReadOnlyList<string> Verification);

    public static Result Apply(GameContext game, DynRefReport report, string outputFolder, IProgress<string>? progress = null)
    {
        var fullOut = Path.GetFullPath(outputFolder);
        if (Directory.Exists(fullOut) && Directory.EnumerateFileSystemEntries(fullOut).Any())
            throw new InvalidOperationException($"{fullOut} already has files. Choose a new folder: nothing is overwritten.");

        var dynKey = ModKey.FromNameAndExtension("DynDOLOD.esp");
        var patch = new SkyrimMod(ModKey.FromNameAndExtension(FileName), SkyrimRelease.SkyrimSE);
        int repositioned = 0, sunk = 0;
        var edited = new List<(FormKey Key, float X, float Y, float Z)>();
        foreach (var f in report.Problems)
        {
            if (f.Issue is not (DynRefIssue.Moved or DynRefIssue.SourceMissing or DynRefIssue.SourceDeleted)) continue;
            var key = new FormKey(dynKey, f.Row.DynId);
            if (!game.LinkCache.TryResolveContext<IPlacedObject, IPlacedObjectGetter>(key, out var ctx))
                throw new InvalidOperationException($"{f.Row.DynId:X6}:DynDOLOD.esp is not in the load order.");
            var rec = ctx.GetOrAddAsOverride(patch);
            rec.Placement ??= new Placement();
            if (f.Issue == DynRefIssue.Moved && f.Place is { } p && Math.Abs(p.SrcPosition.Z - SunkZ) >= 1f)
            {
                rec.Placement.Position = new P3Float(p.SrcPosition.X, p.SrcPosition.Y, p.SrcPosition.Z);
                rec.Placement.Rotation = new P3Float(p.SrcRotation.X, p.SrcRotation.Y, p.SrcRotation.Z);
                rec.Scale = Math.Abs(p.SrcScale - 1f) < 1e-6f ? null : p.SrcScale;
                repositioned++;
            }
            else
            {
                rec.Placement.Position = new P3Float(rec.Placement.Position.X, rec.Placement.Position.Y, SunkZ);
                sunk++;
            }
            edited.Add((key, rec.Placement.Position.X, rec.Placement.Position.Y, rec.Placement.Position.Z));
        }
        int skipped = report.Problems.Count(f => f.Issue == DynRefIssue.EnableStateChanged);
        if (edited.Count == 0) return new Result(null, 0, 0, skipped, new[] { "nothing to patch: no moved or removed sources" });

        Directory.CreateDirectory(fullOut);
        var path = Path.Combine(fullOut, FileName);
        progress?.Report($"Writing {path}...");
        var order = game.LoadOrder.ListedOrder.Select(l => l.ModKey).Where(k => k != patch.ModKey).ToList();
        patch.WriteToBinary(path, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            RecordCount = RecordCountOption.Iterate,
        });

        // Read the written file back: every planned reference must be there with the planned position, and nothing else.
        var msgs = new List<string>();
        using var back = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var got = back.EnumerateMajorRecords<IPlacedObjectGetter>().ToDictionary(r => r.FormKey);
        int bad = edited.Count(e => !got.TryGetValue(e.Key, out var r) || r.Placement is not { } pl
            || Math.Abs(pl.Position.X - e.X) > 0.01f || Math.Abs(pl.Position.Y - e.Y) > 0.01f || Math.Abs(pl.Position.Z - e.Z) > 0.01f);
        msgs.Add(bad == 0 ? $"{edited.Count} planned references read back with the planned positions" : $"{bad} OF {edited.Count} REFERENCES DO NOT MATCH");
        msgs.Add(got.Count == edited.Count ? "no other placed object in the patch" : $"{got.Count - edited.Count} unplanned placed objects in the patch");
        var masters = back.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        msgs.Add($"masters ({masters.Count}): {string.Join(", ", masters.Take(6))}{(masters.Count > 6 ? ", ..." : "")}");
        return new Result(path, repositioned, sunk, skipped, msgs);
    }
}
