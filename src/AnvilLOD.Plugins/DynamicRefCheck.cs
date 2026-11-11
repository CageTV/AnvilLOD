using System.Globalization;
using System.Numerics;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>What is wrong (or worth knowing) about one DynDOLOD dynamic LOD reference.</summary>
public enum DynRefIssue
{
    Ok,
    /// <summary>DynDOLOD's own override sinks the original reference to z = -30000 and keeps the real placement in the dynamic copy. Expected, not stale.</summary>
    HiddenByDynDOLOD,
    /// <summary>Differs like <see cref="Moved"/>, but no plugin overrides the source now (only the plugin that defines it touches it): a DynDOLOD
    /// adjustment, or an override from a plugin that is no longer loaded. Not a change made by the current load order.</summary>
    DiffersNoOverride,
    /// <summary>The source reference is no longer in the load order (plugin removed, or the record is gone).</summary>
    SourceMissing,
    /// <summary>The winning override deletes the source reference.</summary>
    SourceDeleted,
    /// <summary>The placement still matches, but whether the source starts enabled (following its enable-parent chain) or whether that chain
    /// uses "opposite of parent" no longer matches what DynDOLOD recorded: the dynamic reference switches on and off at the wrong times.</summary>
    EnableStateChanged,
    /// <summary>Like <see cref="EnableStateChanged"/>, but no plugin overrides any record of the enable-parent chain: DynDOLOD and the engine rules
    /// evaluate the chain differently (for example script-enabled markers), the load order did not change it.</summary>
    EnableDiffersNoOverride,
    /// <summary>The source exists but its position, rotation or scale no longer match what DynDOLOD copied, and a plugin overrides it.</summary>
    Moved,
    /// <summary>The dynamic reference named in DynDOLOD's data file is not in DynDOLOD.esp/.esm (the data files and the plugin disagree).</summary>
    DynRefMissing,
    /// <summary>The row could not be read (wrong column count, bad number).</summary>
    Unparsed,
}

/// <summary>One line of DynDOLOD_&lt;World&gt;_Objects.txt (or, with the DynDOLOD DLL NG package, DynDOLOD_NG_&lt;World&gt;.txt): the dynamic reference DynDOLOD placed
/// and the real reference it stands for. <see cref="Raw"/> always holds the columns in the old Objects.txt order (columns 0..14), so the Col(n) numbers below
/// mean the same for both formats; <paramref name="Ng"/> says the row came from a DynDOLOD_NG_&lt;World&gt;.txt file.</summary>
public sealed record DynRefRow(string World, int Line, uint DynId, uint SourceId, string SourcePlugin, string Raw, bool Ng = false)
{
    /// <summary>The data file this row was read from.</summary>
    public string DataFile => Ng ? $"DynDOLOD_NG_{World}.txt" : $"DynDOLOD_{World}_Objects.txt";
    /// <summary>Column 2: DynDOLOD's evaluation of "the source starts enabled" (enable-parent chain resolved).</summary>
    public bool DataStartsEnabled => Col(2) == "1";
    /// <summary>Column 4: some link of the source's enable-parent chain is "opposite of parent".</summary>
    public bool DataChainOpposite => Col(4) == "1";
    private string Col(int i) { var c = Raw.Split(','); return i < c.Length ? c[i] : ""; }
}

/// <summary>How a reference switches on and off: starts disabled, is enable-parented, and whether it follows the parent or the opposite.</summary>
public readonly record struct EnableState(bool InitiallyDisabled, bool HasParent, bool Opposite)
{
    /// <summary>Three digits: initially disabled, has enable parent, opposite of parent.</summary>
    public override string ToString() => $"{(InitiallyDisabled ? 1 : 0)}{(HasParent ? 1 : 0)}{(Opposite ? 1 : 0)}";
}

/// <summary>The dynamic reference's placement (copied by DynDOLOD) next to its source's placement now.</summary>
public sealed record PlacementPair(Vector3 DynPosition, Vector3 DynRotation, float DynScale, Vector3 SrcPosition, Vector3 SrcRotation, float SrcScale);

public sealed record DynRefFinding(DynRefRow Row, DynRefIssue Issue, string Detail, string? WinningPlugin, bool Toggled,
    EnableState Source = default, EnableState Dynamic = default, bool StartsEnabledNow = true, bool ChainOppositeNow = false, PlacementPair? Place = null);

public enum DynPlanAction
{
    /// <summary>Set the dynamic reference's position, rotation and scale in the copied DynDOLOD.esp to its source's current placement.</summary>
    Reposition,
    /// <summary>The source is gone, deleted or sunk out of the world: move the dynamic reference to z = -30000 in the copied DynDOLOD.esp (DynDOLOD's own way of hiding a reference; the DLL can still resolve and enable it, but nothing shows).</summary>
    Sink,
    /// <summary>Change column 2 / column 4 of the row in the copied DynDOLOD_&lt;World&gt;_Objects.txt so the recorded enable state matches the load order.</summary>
    EditDataColumns,
    /// <summary>Not fixable from the data files: the dynamic reference named in the data file is not in DynDOLOD.esp. Look at the DynDOLOD output itself.</summary>
    Investigate,
}

/// <summary>One edit a write pass would make. Nothing here has been written.</summary>
public sealed record DynPlanItem(DynPlanAction Action, string World, string File, int Line, string DynamicRef, string SourceRef, string Plugin, string Current, string Proposed, string Reason);

public sealed record DynRefReport(
    int Files, int Rows, int Unparsed, IReadOnlyList<DynRefFinding> Findings, IReadOnlyList<string> Warnings)
{
    /// <summary>Findings that point at a change in the load order since DynDOLOD ran.</summary>
    public IEnumerable<DynRefFinding> Problems =>
        Findings.Where(f => f.Issue is not (DynRefIssue.Ok or DynRefIssue.HiddenByDynDOLOD or DynRefIssue.DiffersNoOverride or DynRefIssue.EnableDiffersNoOverride));

    /// <summary>The edits a write pass would make to a COPY of DynDOLOD.esp and the data files. Rows that differ only without any plugin overriding the source are left out.</summary>
    public IReadOnlyList<DynPlanItem> Plan()
    {
        var items = new List<DynPlanItem>();
        foreach (var f in Problems)
        {
            var r = f.Row;
            var src = $"{r.SourceId:X6}:{r.SourcePlugin}";
            var dyn = $"{r.DynId:X6}:DynDOLOD.esp";
            var file = r.DataFile;
            int colEnabled = r.Ng ? 4 : 2, colOpposite = r.Ng ? 6 : 4;   // the NG row has the dynamic id and plugin in front
            switch (f.Issue)
            {
                case DynRefIssue.Moved when f.Place is { } p:
                    if (Math.Abs(p.SrcPosition.Z + 30000f) < 1f)
                        items.Add(new(DynPlanAction.Sink, r.World, file, r.Line, dyn, src, f.WinningPlugin ?? "", $"z {p.DynPosition.Z:0.##}", "z -30000",
                            $"{f.WinningPlugin} sinks the source to -30000 (disables it in place)"));
                    else
                        items.Add(new(DynPlanAction.Reposition, r.World, file, r.Line, dyn, src, f.WinningPlugin ?? "", Place(p.DynPosition, p.DynRotation, p.DynScale),
                            Place(p.SrcPosition, p.SrcRotation, p.SrcScale), f.Detail));
                    break;
                case DynRefIssue.SourceMissing:
                case DynRefIssue.SourceDeleted:
                    items.Add(new(DynPlanAction.Sink, r.World, file, r.Line, dyn, src, f.WinningPlugin ?? "", "placed", "z -30000", f.Detail));
                    break;
                case DynRefIssue.EnableStateChanged:
                    items.Add(new(DynPlanAction.EditDataColumns, r.World, file, r.Line, dyn, src, f.WinningPlugin ?? "",
                        $"col{colEnabled}={(r.DataStartsEnabled ? "1" : "")} col{colOpposite}={(r.DataChainOpposite ? "1" : "")}",
                        $"col{colEnabled}={(f.StartsEnabledNow ? "1" : "")} col{colOpposite}={(f.ChainOppositeNow ? "1" : "")}", f.Detail));
                    break;
                case DynRefIssue.DynRefMissing:
                    items.Add(new(DynPlanAction.Investigate, r.World, file, r.Line, dyn, src, "", "", "", f.Detail));
                    break;
            }
        }
        return items;
    }

    private static string Place(Vector3 pos, Vector3 rot, float scale) =>
        string.Create(CultureInfo.InvariantCulture, $"pos({pos.X:0.##}, {pos.Y:0.##}, {pos.Z:0.##}) rot({rot.X:0.###}, {rot.Y:0.###}, {rot.Z:0.###}) scale {scale:0.###}");

    public string ToPlanCsv()
    {
        var sb = new StringBuilder("action,world,data_file,line,dynamic_ref,source_ref,plugin,current,proposed,reason\n");
        foreach (var i in Plan())
            sb.Append(i.Action).Append(',').Append(i.World).Append(',').Append(i.File).Append(',').Append(i.Line).Append(',').Append(i.DynamicRef).Append(',')
              .Append(i.SourceRef).Append(',').Append(i.Plugin).Append(",\"").Append(i.Current).Append("\",\"").Append(i.Proposed).Append("\",\"")
              .Append(i.Reason.Replace("\"", "'")).Append("\"\n");
        return sb.ToString();
    }

    /// <summary>One line per finding. <paramref name="all"/> false = the problems and the no-override differences; true = every row, with the
    /// data columns of the DynDOLOD file as well (for studying what those columns mean).</summary>
    public string ToCsv(bool all = false)
    {
        var sb = new StringBuilder("world,line,dynamic_ref,source_ref,issue,toggled_source,winning_plugin,source_enable,dynamic_enable,starts_enabled_now,data_starts_enabled,chain_opposite_now,data_chain_opposite,detail"
            + (all ? ",data_columns" : "") + "\n");
        foreach (var f in all ? Findings : Findings.Where(x => x.Issue is not (DynRefIssue.Ok or DynRefIssue.HiddenByDynDOLOD)))
        {
            sb.Append(f.Row.World).Append(',').Append(f.Row.Line).Append(',').Append(f.Row.DynId.ToString("X6")).Append(',')
              .Append(f.Row.SourceId.ToString("X6")).Append(':').Append(f.Row.SourcePlugin).Append(',').Append(f.Issue).Append(',')
              .Append(f.Toggled).Append(',').Append(f.WinningPlugin ?? "").Append(',').Append(f.Source).Append(',').Append(f.Dynamic)
              .Append(',').Append(f.StartsEnabledNow ? 1 : 0).Append(',').Append(f.Row.DataStartsEnabled ? 1 : 0)
              .Append(',').Append(f.ChainOppositeNow ? 1 : 0).Append(',').Append(f.Row.DataChainOpposite ? 1 : 0)
              .Append(",\"").Append(f.Detail.Replace("\"", "'")).Append('"');
            if (all) sb.Append(",\"").Append(f.Row.Raw.Replace("\"", "'")).Append('"');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Read-only check of DynDOLOD's dynamic LOD references against the load order as it is now.
///
/// DynDOLOD writes, per worldspace, <c>skse/plugins/DynDOLOD_Data/DynDOLOD_&lt;World&gt;_Objects.txt</c>. Each line names a dynamic
/// reference in DynDOLOD.esp (the key, a decimal local FormID) and the real reference it stands for (columns 12 and 13: decimal local
/// FormID and plugin; columns 5 and 6 are shared by many rows). The dynamic reference is a copy of the source taken when DynDOLOD ran,
/// with the same placement, so a source that has since moved, been deleted or vanished leaves a dynamic LOD object in the wrong place
/// or standing for nothing. Nothing is written.
/// </summary>
public static class DynamicRefCheck
{
    private const int Columns = 19;
    private const int FlagInitiallyDisabled = 0x0000_0800;
    private const float PositionTolerance = 0.05f;
    private const float AngleTolerance = 0.001f;
    private const float ScaleTolerance = 0.001f;
    private const float HiddenZ = -30000f;

    public static string DataFolder(string dyndolodOutput) => Path.Combine(dyndolodOutput, "skse", "plugins", "DynDOLOD_Data");

    public static IReadOnlyList<DynRefRow> ReadRows(string dataFolder, out int unparsed, out int files)
    {
        var rows = new List<DynRefRow>();
        unparsed = 0; files = 0;
        if (!Directory.Exists(dataFolder)) return rows;
        foreach (var path in Directory.EnumerateFiles(dataFolder, "DynDOLOD_*_Objects.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            files++;
            var name = Path.GetFileNameWithoutExtension(path);                 // DynDOLOD_Tamriel_Objects
            var world = name["DynDOLOD_".Length..^"_Objects".Length];
            int line = 0;
            foreach (var raw in File.ReadLines(path))
            {
                line++;
                var eq = raw.IndexOf('=');
                if (eq <= 0 || !char.IsDigit(raw[0])) continue;                  // [Config], [Objects], bunchofnumbers=
                var data = raw[(eq + 1)..].TrimEnd('\r');
                var cols = data.Split(',');
                if (cols.Length != Columns) { unparsed++; continue; }
                // Columns 5/6 are shared by many rows (hundreds of lanterns name the same record), columns 12/13 are unique per row:
                // those name the placed reference this dynamic reference stands for. Where 12 is empty, fall back to 5/6.
                var idCol = cols[12].Length > 0 ? 12 : 5;
                var plugCol = cols[12].Length > 0 ? 13 : 6;
                if (!uint.TryParse(raw.AsSpan(0, eq), NumberStyles.None, CultureInfo.InvariantCulture, out var dyn)
                    || !uint.TryParse(cols[idCol], NumberStyles.None, CultureInfo.InvariantCulture, out var src) || string.IsNullOrWhiteSpace(cols[plugCol]))
                { unparsed++; continue; }
                rows.Add(new DynRefRow(world, line, dyn, src, cols[plugCol].Trim(), data));
            }
        }
        ReadNgRows(dataFolder, rows, ref unparsed, ref files);
        return rows;
    }

    /// <summary>
    /// DynDOLOD_NG_&lt;World&gt;.txt, written by a DynDOLOD run with the DLL NG package. A row is <c>dynId,DynDOLOD.esp,</c> followed by the old Objects.txt
    /// columns 0..14 (checked against the same source references in both formats): so "starts enabled" is column 4, "opposite of parent" column 6, the shared
    /// base record columns 7/8 and the unique source reference columns 14/15. The old trailing columns 15..18 are not written any more.
    /// DynDOLOD_NG_Worlds.txt (the world list) is not a row file. Only the [Objects] section holds these rows; [Resets] (id,plugin), [Markers] and
    /// [Disabled] (id,plugin) carry other data and are skipped without counting as unreadable.
    /// </summary>
    private static void ReadNgRows(string dataFolder, List<DynRefRow> rows, ref int unparsed, ref int files)
    {
        const string prefix = "DynDOLOD_NG_";
        foreach (var path in Directory.EnumerateFiles(dataFolder, prefix + "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var world = Path.GetFileNameWithoutExtension(path)[prefix.Length..];
            if (world.Equals("Worlds", StringComparison.OrdinalIgnoreCase)) continue;
            files++;
            int line = 0;
            var section = "";
            foreach (var raw in File.ReadLines(path))
            {
                line++;
                if (raw.StartsWith('[')) { section = raw.Trim(); continue; }
                if (!section.Equals("[Objects]", StringComparison.OrdinalIgnoreCase) || raw.Length == 0 || !char.IsDigit(raw[0])) continue;
                var cols = raw.TrimEnd('\r').Split(',');
                if (cols.Length < 16 || !cols[1].Equals("DynDOLOD.esp", StringComparison.OrdinalIgnoreCase)) { unparsed++; continue; }
                var idCol = cols[14].Length > 0 ? 14 : 7;                         // like the old 12 / 5
                var plugCol = idCol + 1;
                if (!uint.TryParse(cols[0], NumberStyles.None, CultureInfo.InvariantCulture, out var dyn)
                    || !uint.TryParse(cols[idCol], NumberStyles.None, CultureInfo.InvariantCulture, out var src) || string.IsNullOrWhiteSpace(cols[plugCol]))
                { unparsed++; continue; }
                rows.Add(new DynRefRow(world, line, dyn, src, cols[plugCol].Trim(), string.Join(",", cols.Skip(2)), Ng: true));
            }
        }
    }

    public static DynRefReport Run(GameContext game, string dyndolodOutput, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var rows = ReadRows(DataFolder(dyndolodOutput), out var unparsed, out var files);
        var warnings = new List<string>();
        if (files == 0) warnings.Add($"No DynDOLOD_*_Objects.txt or DynDOLOD_NG_*.txt in {DataFolder(dyndolodOutput)}.");
        var cache = game.LinkCache;
        var enable = new EnableEvaluator(cache);
        var findings = new List<DynRefFinding>(rows.Count);
        int done = 0;

        // DynDOLOD.esp is read from the output folder itself: it need not be enabled in the load order (it is usually switched off to
        // regenerate it), and this is the file the data files were written for.
        var espPath = Path.Combine(dyndolodOutput, "DynDOLOD.esp");
        ISkyrimModGetter? dynMod = File.Exists(espPath) ? SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE) : null;
        if (dynMod is null) warnings.Add($"No DynDOLOD.esp in {dyndolodOutput}; looking for it in the load order instead.");
        var dynRefs = new Dictionary<FormKey, IPlacedObjectGetter>();
        if (dynMod is not null)
            foreach (var pr in dynMod.EnumerateMajorRecords<IPlacedObjectGetter>()) dynRefs[pr.FormKey] = pr;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 2000 == 0) progress?.Report($"Checked {done:N0} of {rows.Count:N0} dynamic references");

            // The dynamic reference lives in DynDOLOD.esp (its bases are in DynDOLOD.esm).
            IPlacedObjectGetter? dyn = null;
            if (FormKey.TryFactory($"{row.DynId:X6}:DynDOLOD.esp", out var dk) && !dynRefs.TryGetValue(dk, out dyn)) cache.TryResolve<IPlacedObjectGetter>(dk, out dyn);
            if (dyn is null)
            {
                findings.Add(new(row, DynRefIssue.DynRefMissing, $"{row.DynId:X6}:DynDOLOD.esp is not in the DynDOLOD output folder or the load order", null, false));
                continue;
            }

            // Some rows name a reference of DynDOLOD's own plugins as the "source" (objects DynDOLOD made, with no real reference behind them): nothing to go stale.
            if (IsDynDolod(ModKey.FromNameAndExtension(row.SourcePlugin)))
            {
                findings.Add(new(row, DynRefIssue.Ok, "source is a DynDOLOD reference", null, false, default, StateOf(dyn)));
                continue;
            }

            // The source as DynDOLOD saw it: its own overrides (which sink or disable the original) are left out, so the answer does not depend on
            // whether DynDOLOD's plugins happen to be enabled in the profile.
            if (!FormKey.TryFactory($"{row.SourceId:X6}:{row.SourcePlugin}", out var sk) || Effective(cache, sk) is not { } sourceCtx)
            {
                findings.Add(new(row, DynRefIssue.SourceMissing, "source reference not in the load order", null, false, default, StateOf(dyn)));
                continue;
            }
            var src = sourceCtx.Record;
            var winner = sourceCtx.ModKey.FileName.String;
            var sState = StateOf(src);
            var dState = StateOf(dyn);
            bool toggled = sState.InitiallyDisabled || sState.HasParent;
            bool startsEnabled = enable.StartsEnabled(sk), chainOpposite = enable.ChainHasOpposite(sk);

            if (src.IsDeleted)
            {
                findings.Add(new(row, DynRefIssue.SourceDeleted, $"deleted by {winner}", winner, toggled, sState, dState, startsEnabled, chainOpposite));
                continue;
            }

            if (dyn.Placement is { } dp && src.Placement is { } sp)
            {
                if (winner.StartsWith("DynDOLOD.", StringComparison.OrdinalIgnoreCase) && Math.Abs(sp.Position.Z - HiddenZ) < 1f)
                {
                    findings.Add(new(row, DynRefIssue.HiddenByDynDOLOD, "sunk by DynDOLOD", winner, toggled, sState, dState, startsEnabled, chainOpposite));
                    continue;
                }

                var d = new List<string>();
                if (Math.Abs(dp.Position.X - sp.Position.X) > PositionTolerance || Math.Abs(dp.Position.Y - sp.Position.Y) > PositionTolerance
                    || Math.Abs(dp.Position.Z - sp.Position.Z) > PositionTolerance)
                    d.Add($"position {Fmt(dp.Position.X, dp.Position.Y, dp.Position.Z)} -> {Fmt(sp.Position.X, sp.Position.Y, sp.Position.Z)}");
                if (Math.Abs(dp.Rotation.X - sp.Rotation.X) > AngleTolerance || Math.Abs(dp.Rotation.Y - sp.Rotation.Y) > AngleTolerance
                    || Math.Abs(dp.Rotation.Z - sp.Rotation.Z) > AngleTolerance)
                    d.Add($"rotation {Fmt(dp.Rotation.X, dp.Rotation.Y, dp.Rotation.Z)} -> {Fmt(sp.Rotation.X, sp.Rotation.Y, sp.Rotation.Z)}");
                if (Math.Abs((dyn.Scale ?? 1f) - (src.Scale ?? 1f)) > ScaleTolerance)
                    d.Add($"scale {(dyn.Scale ?? 1f):0.###} -> {(src.Scale ?? 1f):0.###}");
                if (d.Count > 0)
                {
                    int touching = cache.ResolveAllContexts<IPlacedObject, IPlacedObjectGetter>(sk).Count(c => !IsDynDolod(c.ModKey));
                    findings.Add(new(row, touching <= 1 ? DynRefIssue.DiffersNoOverride : DynRefIssue.Moved, string.Join("; ", d), winner, toggled, sState, dState, startsEnabled, chainOpposite,
                        new PlacementPair(new(dp.Position.X, dp.Position.Y, dp.Position.Z), new(dp.Rotation.X, dp.Rotation.Y, dp.Rotation.Z), dyn.Scale ?? 1f,
                                          new(sp.Position.X, sp.Position.Y, sp.Position.Z), new(sp.Rotation.X, sp.Rotation.Y, sp.Rotation.Z), src.Scale ?? 1f)));
                    continue;
                }
            }
            if (startsEnabled != row.DataStartsEnabled || chainOpposite != row.DataChainOpposite)
                findings.Add(new(row, enable.ChainOverridden(sk) ? DynRefIssue.EnableStateChanged : DynRefIssue.EnableDiffersNoOverride,
                    $"starts enabled: DynDOLOD {(row.DataStartsEnabled ? "yes" : "no")}, now {(startsEnabled ? "yes" : "no")}; opposite-of-parent in chain: DynDOLOD {(row.DataChainOpposite ? "yes" : "no")}, now {(chainOpposite ? "yes" : "no")}",
                    winner, toggled, sState, dState, startsEnabled, chainOpposite));
            else
                findings.Add(new(row, DynRefIssue.Ok, "", winner, toggled, sState, dState, startsEnabled, chainOpposite));
        }

        (dynMod as IDisposable)?.Dispose();
        return new DynRefReport(files, rows.Count, unparsed, findings, warnings);
    }

    /// <summary>Initial enable state of a reference following its enable-parent chain; mirrors the scanner's evaluation.</summary>
    private sealed class EnableEvaluator(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache)
    {
        private readonly Dictionary<FormKey, bool> _memo = [];

        public bool StartsEnabled(FormKey key) => Evaluate(key, 0);

        /// <summary>True if another plugin overrides the reference or any reference up its enable-parent chain.</summary>
        public bool ChainOverridden(FormKey key)
        {
            for (int depth = 0; depth < 32; depth++)
            {
                if (cache.ResolveAllContexts<IPlacedObject, IPlacedObjectGetter>(key).Count(c => !IsDynDolod(c.ModKey)) > 1) return true;
                if (!TryGet(key, out _, out var parent, out _, out var has) || !has) return false;
                key = parent;
            }
            return false;
        }

        public bool ChainHasOpposite(FormKey key)
        {
            for (int depth = 0; depth < 32 && TryGet(key, out _, out var parent, out var opposite, out var has) && has; depth++)
            {
                if (opposite) return true;
                key = parent;
            }
            return false;
        }

        // flags and enable parent of a placed object or a placed actor; false when the record is not found
        private bool TryGet(FormKey key, out bool disabled, out FormKey parent, out bool opposite, out bool hasParent)
        {
            disabled = false; parent = default; opposite = false; hasParent = false;
            if (Effective(cache, key) is { } oc && oc.Record is { } o) { Fill(o.MajorRecordFlagsRaw, o.EnableParent, out disabled, out parent, out opposite, out hasParent); return true; }
            if (cache.TryResolve<IPlacedNpcGetter>(key, out var n)) { Fill(n.MajorRecordFlagsRaw, n.EnableParent, out disabled, out parent, out opposite, out hasParent); return true; }
            return false;
        }

        private static void Fill(int flags, IEnableParentGetter? ep, out bool disabled, out FormKey parent, out bool opposite, out bool hasParent)
        {
            disabled = (flags & FlagInitiallyDisabled) != 0;
            hasParent = ep is not null && !ep.Reference.IsNull;
            parent = hasParent ? ep!.Reference.FormKey : default;
            opposite = hasParent && ep!.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent);
        }

        private bool Evaluate(FormKey key, int depth)
        {
            if (_memo.TryGetValue(key, out var known)) return known;
            if (depth > 32) return true;                       // a cycle: the engine ignores it too
            bool enabled = true;                               // anything unresolvable counts as enabled
            if (TryGet(key, out var disabled, out var parent, out var opposite, out var has))
            {
                if (has) { bool p = Evaluate(parent, depth + 1); enabled = !disabled && (opposite ? !p : p); }
                else enabled = !disabled;
            }
            _memo[key] = enabled;
            return enabled;
        }
    }

    private static bool IsDynDolod(ModKey m) =>
        m.FileName.String.Equals("DynDOLOD.esm", StringComparison.OrdinalIgnoreCase) || m.FileName.String.Equals("DynDOLOD.esp", StringComparison.OrdinalIgnoreCase);

    /// <summary>The winning override of a placed reference, not counting DynDOLOD's own plugins; null when nothing else defines it.</summary>
    private static IModContext<ISkyrimMod, ISkyrimModGetter, IPlacedObject, IPlacedObjectGetter>? Effective(ILinkCache<ISkyrimMod, ISkyrimModGetter> cache, FormKey key)
    {
        foreach (var c in cache.ResolveAllContexts<IPlacedObject, IPlacedObjectGetter>(key))
            if (!IsDynDolod(c.ModKey)) return c;
        return null;
    }

    private static EnableState StateOf(IPlacedObjectGetter r) => new(
        (r.MajorRecordFlagsRaw & FlagInitiallyDisabled) != 0,
        r.EnableParent is { } ep && !ep.Reference.IsNull,
        r.EnableParent is { } eo && !eo.Reference.IsNull && eo.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent));

    private static string Fmt(float x, float y, float z) =>
        string.Create(CultureInfo.InvariantCulture, $"({x:0.##}, {y:0.##}, {z:0.##})");
}
