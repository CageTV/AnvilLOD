using System.Diagnostics;
using System.Numerics;
using System.Text;
using AnvilLOD.Core.World;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Reason = AnvilLOD.Core.World.LargeReferenceRules.Reason;

namespace AnvilLOD.Plugins;

/// <summary>
/// The large reference step of a Generate run: finds the references that qualify for the engine's large reference grid but that no
/// plugin lists yet, and writes them to <c>AnvilLOD.esm</c>, flagged ESL, as an override of each worldspace record with the list
/// entries (one per cell, the reference's own cell, like DynDOLOD does). Lists from several ESMs add up, so only the missing
/// references go in; a cell's existing entry is repeated for the cells that get a new one, which changes nothing if the lists add up and
/// keeps the old references if the engine replaces a cell's entry.
/// </summary>
public static class LargeReferenceStage
{
    public const string FileName = "AnvilLOD.esm";

    public sealed record WorldspaceResult(string Worldspace, int Listed, int Cells, IReadOnlyDictionary<Reason, int> Skipped);

    public sealed record Result(IReadOnlyList<WorldspaceResult> Worldspaces, int Listed, string? Plugin, float MinSize, string ReportFile, TimeSpan Elapsed);

    /// <param name="flagEsl">Flag AnvilLOD.esm as ESL (takes no plugin slot). Off: a normal ESM that uses a slot.</param>
    /// <param name="dyndolodDllInstalled">DynDOLOD DLL NG is installed: the report and log then point at its large reference workarounds.</param>
    public static Result Run(GameContext game, IReadOnlyCollection<string> worldspaces, string output, IProgress<string>? progress, Action<string> warn, CancellationToken ct,
        bool flagEsl = true, bool dyndolodDllInstalled = false)
    {
        var sw = Stopwatch.StartNew();
        var linkCache = game.LinkCache;
        var wanted = new HashSet<string>(worldspaces, StringComparer.OrdinalIgnoreCase);

        // ESM-flagged plugins (an ESL-flagged ESM counts). Only they can define or override a large reference.
        var master = game.LoadOrder.ListedOrder
            .Where(l => l.Mod is { } m && m.ModHeader.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master))
            .Select(l => l.ModKey).ToHashSet();

        float minSize = LargeReferenceRules.DefaultMinSize;
        foreach (var g in game.LoadOrder.PriorityOrder.GameSetting().WinningOverrides())
            if (g.EditorID == "fLargeRefMinSize" && g is IGameSettingFloatGetter f) { minSize = f.Data ?? minSize; break; }

        // The worldspaces we may list in, and everything already listed in them (by any plugin).
        var wsName = new Dictionary<FormKey, string>();
        foreach (var ws in game.LoadOrder.PriorityOrder.Worldspace().WinningOverrides())
            if (ws.EditorID is { } id && wanted.Contains(id)) wsName[ws.FormKey] = id;

        var listed = new HashSet<(FormKey Ws, FormKey Ref)>();
        var existing = new Dictionary<(FormKey Ws, P2Int16 Cell), Dictionary<FormKey, P2Int16>>();
        foreach (var listing in game.LoadOrder.ListedOrder)
        {
            if (listing.Mod is not { } mod) continue;
            foreach (var ws in mod.Worldspaces)
            {
                if (!wsName.ContainsKey(ws.FormKey) || ws.LargeReferences is not { Count: > 0 } lr) continue;
                foreach (var cell in lr)
                {
                    var key = (ws.FormKey, cell.GridPosition);
                    if (!existing.TryGetValue(key, out var refs)) existing[key] = refs = [];
                    foreach (var r in cell.References)
                    {
                        listed.Add((ws.FormKey, r.Reference.FormKey));
                        refs[r.Reference.FormKey] = r.Position;
                    }
                }
            }
        }
        progress?.Report($"Large references: {listed.Count:N0} already listed by your plugins (fLargeRefMinSize {minSize:0})");

        // References that a plugin outside the ESMs overrides: listing those is a known source of flicker (DynDOLOD documents it).
        var overriddenOutside = new HashSet<FormKey>();
        foreach (var listing in game.LoadOrder.ListedOrder)
        {
            ct.ThrowIfCancellationRequested();
            if (listing.Mod is not { } mod || master.Contains(listing.ModKey)) continue;
            foreach (var r in mod.EnumerateMajorRecords<IPlacedObjectGetter>())
                if (r.FormKey.ModKey != listing.ModKey) overriddenOutside.Add(r.FormKey);
        }

        // The winning references: which qualify and aren't listed.
        var baseInfo = new Dictionary<FormKey, (bool Ok, Vector3 Min, Vector3 Max)>();
        var candidates = new Dictionary<FormKey, Dictionary<P2Int16, List<FormKey>>>();
        var skipped = wsName.Keys.ToDictionary(k => k, _ => new Dictionary<Reason, int>());
        long visited = 0;
        var bySource = new Dictionary<ModKey, int>();

        foreach (var ctx in game.LoadOrder.PriorityOrder.PlacedObject().WinningContextOverrides(linkCache))
        {
            ct.ThrowIfCancellationRequested();
            if ((++visited & 0x3FFFF) == 0) progress?.Report($"Large references: {visited:N0} references checked...");
            var r = ctx.Record;
            if (r.IsDeleted || !ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsCtx) || !wsName.ContainsKey(wsCtx.Record.FormKey)) continue;
            var wsKey = wsCtx.Record.FormKey;
            void Skip(Reason why) => skipped[wsKey][why] = skipped[wsKey].GetValueOrDefault(why) + 1;

            if (r.Base.FormKeyNullable is not { } baseKey) continue;
            if (LargeReferenceRules.StartsDisabled(r.MajorRecordFlagsRaw)) { Skip(Reason.StartsDisabled); continue; }

            if (!baseInfo.TryGetValue(baseKey, out var info))
            {
                info = (false, default, default);
                if (linkCache.TryResolve<IStaticGetter>(baseKey, out var st))
                    info = (true, ToVector(st.ObjectBounds.First), ToVector(st.ObjectBounds.Second));
                else if (linkCache.TryResolve<IMoveableStaticGetter>(baseKey, out var ms) && LargeReferenceRules.MoveableStaticQualifies(ms.MajorRecordFlagsRaw))
                    info = (true, ToVector(ms.ObjectBounds.First), ToVector(ms.ObjectBounds.Second));
                baseInfo[baseKey] = info;
            }
            if (!info.Ok) { Skip(Reason.WrongBase); continue; }
            if (!LargeReferenceRules.IsLargeEnough(info.Min, info.Max, r.Scale ?? 1f, minSize)) { Skip(Reason.TooSmall); continue; }
            if (listed.Contains((wsKey, r.FormKey))) { Skip(Reason.AlreadyListed); continue; }
            if (!master.Contains(r.FormKey.ModKey)) { Skip(Reason.DefinedOutsideEsm); continue; }
            if (!master.Contains(ctx.ModKey) || overriddenOutside.Contains(r.FormKey)) { Skip(Reason.OverriddenOutsideEsm); continue; }
            if (r.Placement?.Position is not { } pos) continue;

            var cell = CellCoord.FromWorld(pos.X, pos.Y);
            var cellKey = new P2Int16((short)cell.X, (short)cell.Y);
            if (!candidates.TryGetValue(wsKey, out var cells)) candidates[wsKey] = cells = [];
            if (!cells.TryGetValue(cellKey, out var list)) cells[cellKey] = list = [];
            list.Add(r.FormKey);
            bySource[r.FormKey.ModKey] = bySource.GetValueOrDefault(r.FormKey.ModKey) + 1;
        }
        progress?.Report("Large references: new references by defining plugin: "
                         + string.Join(", ", bySource.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key.FileName}={k.Value:N0}")));

        var perWorld = new List<WorldspaceResult>();
        foreach (var (wsKey, name) in wsName.OrderBy(k => k.Value, StringComparer.OrdinalIgnoreCase))
        {
            candidates.TryGetValue(wsKey, out var cells);
            perWorld.Add(new WorldspaceResult(name, cells?.Sum(c => c.Value.Count) ?? 0, cells?.Count ?? 0, skipped[wsKey]));
        }
        int total = perWorld.Sum(w => w.Listed);
        foreach (var w in perWorld.Where(w => w.Listed > 0))
            progress?.Report($"Large references: {w.Worldspace}: {w.Listed:N0} new in {w.Cells:N0} cells");

        string? plugin = null;
        if (total > 0)
        {
            plugin = Write(game, output, candidates, existing, master, flagEsl);

            // An ESM loads before every plugin that isn't an ESM, so none of its masters may be one that isn't flagged ESM.
            using (var written = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE))
            {
                var bad = written.ModHeader.MasterReferences.Select(m => m.Master).Where(m => !master.Contains(m)).Select(m => m.FileName.String).ToList();
                if (bad.Count > 0)
                    warn($"Large references: {FileName} needs {string.Join(", ", bad)}, which {(bad.Count == 1 ? "isn't" : "aren't")} flagged ESM. "
                         + "The load order may refuse it or sort it after them; flag them ESM or leave this option off.");
                else
                    progress?.Report($"Large references: all {written.ModHeader.MasterReferences.Count} masters of {FileName} are ESM-flagged.");
            }
            progress?.Report($"Large references: {total:N0} references listed in {FileName} ("
                             + (flagEsl ? "flagged ESM and ESL: it takes no plugin slot" : "a normal ESM: it takes a plugin slot") + "). Enable it after your other ESMs.");
            if (dyndolodDllInstalled)
                progress?.Report("Large references: DynDOLOD DLL NG is installed. If large references flicker, turn on its Large Reference Bugs Workarounds; it works alongside AnvilLOD's plugin.");
        }
        else
        {
            Remove(output);
            progress?.Report("Large references: nothing new to list.");
        }

        var reportFile = Path.Combine(output, "AnvilLOD Large References.txt");
        Directory.CreateDirectory(output);
        File.WriteAllText(reportFile, Report(perWorld, minSize, total, plugin is not null, flagEsl, dyndolodDllInstalled));
        sw.Stop();
        return new Result(perWorld, total, plugin, minSize, reportFile, sw.Elapsed);
    }

    private static string Write(GameContext game, string output,
        Dictionary<FormKey, Dictionary<P2Int16, List<FormKey>>> candidates,
        Dictionary<(FormKey Ws, P2Int16 Cell), Dictionary<FormKey, P2Int16>> existing,
        HashSet<ModKey> master, bool flagEsl)
    {
        var esm = new SkyrimMod(ModKey.FromNameAndExtension(FileName), SkyrimRelease.SkyrimSE) { IsMaster = true, IsSmallMaster = flagEsl };
        esm.ModHeader.Stats.NextFormID = 0x800;

        foreach (var (wsKey, cells) in candidates.OrderBy(c => c.Key.ToString(), StringComparer.Ordinal))
        {
            // Base the override on the last ESM version of the worldspace: an ESM loads before every other plugin, so
            // plugins outside the ESMs still win over it.
            var source = game.LinkCache.ResolveAllContexts<IWorldspace, IWorldspaceGetter>(wsKey).FirstOrDefault(c => master.Contains(c.ModKey));
            if (source is null) continue;
            var ws = source.GetOrAddAsOverride(esm);
            ws.LargeReferences.Clear();
            foreach (var (cell, refs) in cells.OrderBy(c => c.Key.Y).ThenBy(c => c.Key.X))
            {
                var entry = new WorldspaceGridReference { GridPosition = cell };
                var done = new HashSet<FormKey>();
                if (existing.TryGetValue((wsKey, cell), out var old))
                    foreach (var (fk, pos) in old)
                        if (done.Add(fk)) entry.References.Add(new WorldspaceReference { Reference = new FormLink<IPlacedObjectGetter>(fk), Position = pos });
                foreach (var fk in refs)
                    if (done.Add(fk)) entry.References.Add(new WorldspaceReference { Reference = new FormLink<IPlacedObjectGetter>(fk), Position = cell });
                ws.LargeReferences.Add(entry);
            }
        }

        Directory.CreateDirectory(output);
        var path = Path.Combine(output, FileName);
        var order = game.LoadOrder.ListedOrder.Select(l => l.ModKey).Where(k => k != esm.ModKey).ToList();
        esm.WriteToBinary(path, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            RecordCount = RecordCountOption.Iterate,
        });
        return path;
    }

    private static Vector3 ToVector(P3Int16 p) => new(p.X, p.Y, p.Z);

    /// <summary>Deletes <c>AnvilLOD.esm</c> from the output (used when the option is off).</summary>
    public static void Remove(string output)
    {
        try
        {
            var file = Path.Combine(output, FileName);
            if (File.Exists(file)) File.Delete(file);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Report(IReadOnlyList<WorldspaceResult> worlds, float minSize, int total, bool written, bool flagEsl, bool dyndolodDllInstalled)
    {
        var sb = new StringBuilder();
        sb.AppendLine("AnvilLOD large references");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        sb.AppendLine("The engine's large reference grid shows the full models of listed references beyond the loaded cells (uLargeRefLODGridSize in SkyrimPrefs.ini).");
        sb.AppendLine($"A reference qualifies when its base is a STAT (or a MSTT with record flag 0x4), half the object bounds diagonal times its scale is more than {minSize:0} (fLargeRefMinSize; this reproduces the list in Skyrim.esm),");
        sb.AppendLine("it is defined in ESM-flagged plugins only (no plugin outside the ESMs overrides it), it doesn't start disabled, and no plugin lists it yet.");
        sb.AppendLine();
        sb.AppendLine(written ? $"Listed in AnvilLOD.esm: {total:N0} references." : "Nothing new to list; no AnvilLOD.esm was written.");
        if (written)
        {
            sb.AppendLine(flagEsl
                ? "AnvilLOD.esm is flagged ESL, so it takes no plugin slot. If large references flicker at a distance and you want to rule the ESL flag out, generate again with \"Flag AnvilLOD.esm as ESL\" off: it is then a normal ESM and uses a slot."
                : "AnvilLOD.esm is a normal ESM (not flagged ESL), so it uses a plugin slot.");
            if (dyndolodDllInstalled)
                sb.AppendLine("DynDOLOD DLL NG is installed. Flicker from large references (the full model and the LOD drawn in the same place) is what its Large Reference Bugs Workarounds are for; it works alongside AnvilLOD's plugin, which leaves the LOD distance settings to it while it is installed.");
        }
        sb.AppendLine();
        string Why(Reason r) => r switch
        {
            Reason.AlreadyListed => "already listed by a plugin",
            Reason.TooSmall => "too small",
            Reason.WrongBase => "base is not a STAT or flagged MSTT",
            Reason.StartsDisabled => "starts disabled",
            Reason.DefinedOutsideEsm => "defined in a plugin that isn't an ESM",
            Reason.OverriddenOutsideEsm => "overridden by a plugin that isn't an ESM",
            _ => r.ToString(),
        };
        foreach (var w in worlds.Where(w => w.Listed > 0 || w.Skipped.Count > 0).OrderByDescending(w => w.Listed))
        {
            sb.AppendLine($"{w.Worldspace}: {w.Listed:N0} new references in {w.Cells:N0} cells");
            foreach (var (reason, n) in w.Skipped.OrderByDescending(k => k.Value)) sb.AppendLine($"    not listed, {Why(reason)}: {n:N0}");
        }
        return sb.ToString();
    }
}
