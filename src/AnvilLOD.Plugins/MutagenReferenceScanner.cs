using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using AnvilLOD.Core.Lod;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;

namespace AnvilLOD.Plugins;

/// <summary>
/// Finds every exterior REFR (winning override) whose base object gets object LOD: STAT, MSTT, ACTI,
/// FURN, DOOR and CONT bases, with LOD meshes chosen by the DynDOLOD-style rules and found by file name
/// (<c>name_lod_N.nif</c>) or through the base record's MNAM. Trees come with tree LOD.
/// </summary>
public sealed class MutagenReferenceScanner : IReferenceScanner
{
    // REFR record header flags
    private const int FlagPersistent = 0x0000_0400;
    private const int FlagInitiallyDisabled = 0x0000_0800;
    private const int FlagIsFullLod = 0x0001_0000;

    private readonly GameContext _game;
    private readonly IAssetSource _assets;
    private readonly LodMeshResolver? _resolver;
    private readonly ChildWorldCopies _childCopies;

    private sealed record BaseInfo(string ModelPath, string?[]? Mnam, bool DistantFlag, string? EditorId, string FormIdKey);

    /// <param name="resolver">Rules + named-LOD index. Null = MNAM only with the default level mapping.</param>
    /// <param name="childCopies">Child worldspaces (walled cities) whose references also go into the parent's LOD.</param>
    public MutagenReferenceScanner(GameContext game, IAssetSource assets, LodMeshResolver? resolver = null, ChildWorldCopies? childCopies = null)
    {
        _game = game;
        _assets = assets;
        _resolver = resolver;
        _childCopies = childCopies ?? ChildWorldCopies.None;
    }

    /// <summary>References copied from child worldspaces into their parent's LOD by the last scan.</summary>
    public int ChildCopies { get; private set; }

    public ScanResult Scan(ScanOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        ChildCopies = 0;
        var sw = Stopwatch.StartNew();
        var priority = _game.LoadOrder.PriorityOrder;

        // 1) Worldspaces + their LOD grids
        var wanted = options.Worldspaces is { Count: > 0 }
            ? new HashSet<string>(options.Worldspaces, StringComparer.OrdinalIgnoreCase)
            : null;

        var grids = new Dictionary<string, LodGrid>(StringComparer.OrdinalIgnoreCase);
        foreach (var ws in priority.Worldspace().WinningOverrides())
        {
            var edid = ws.EditorID;
            if (string.IsNullOrEmpty(edid)) continue;
            if (wanted is not null && !wanted.Contains(edid)) continue;

            var lodPath = LodSettings.RelativePathFor(edid);
            if (!_assets.TryOpen(lodPath, out var s)) continue; // no .lod file = engine won't load LOD here
            using (s)
            {
                var buf = new byte[LodSettings.SizeInBytes];
                s.ReadExactly(buf);
                grids[edid] = new LodGrid(edid, LodSettings.Parse(buf));
            }
        }
        progress?.Report($"Worldspaces with LOD settings: {grids.Count}");

        // 2) Base objects -> model + MNAM (cached), then rules + named-LOD index -> mesh per level
        var linkCache = _game.LinkCache;
        var baseCache = new ConcurrentDictionary<FormKey, BaseInfo?>();
        var resolved = new ConcurrentDictionary<FormKey, LodMeshResolver.Resolution>();
        var missing = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var meshExists = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool Exists(string path) => meshExists.GetOrAdd(path, p => _assets.Exists(p));
        var resolver = _resolver ?? new LodMeshResolver(LodMeshIndex.Build([]), new LodRules(), Exists);

        string? Mnam(AssetLinkGetter<SkyrimModelAssetType> link)
        {
            if (link.IsNull) return null;
            var path = link.DataRelativePath.Path;
            if (!Exists(path)) missing.AddOrUpdate(path, 1, (_, n) => n + 1);
            return path;
        }

        BaseInfo? ReadBase(FormKey fk)
        {
            if (!linkCache.TryResolve(fk, out var rec)) return null;
            // Object types that can have static object LOD. Trees (TREE) come with tree LOD (milestone 3).
            if (rec is not (IStaticGetter or IMoveableStaticGetter or IActivatorGetter or IFurnitureGetter or IDoorGetter or IContainerGetter))
                return null;
            var model = (rec as IModeledGetter)?.Model?.File;
            if (model is null || model.IsNull) return null;

            string?[]? mnam = null;
            bool distant = false;
            if (rec is IStaticGetter stat)
            {
                distant = stat.MajorFlags.HasFlag(Static.MajorFlag.HasDistantLOD);
                if (stat.Lod is { } lod)
                    mnam = [Mnam(lod.Level0), Mnam(lod.Level1), Mnam(lod.Level2), Mnam(lod.Level3)];
            }
            return new BaseInfo(model.DataRelativePath.Path, mnam, distant, rec.EditorID,
                LodRules.FormIdKey(fk.ModKey.ToString(), fk.ID));
        }

        // 2b) Trees: billboard per TREE base (TexGen/LODGen naming), runtime FormIDs from the load order.
        var treeCache = new ConcurrentDictionary<FormKey, TreeBillboard?>();
        var missingBillboards = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var runtimeIds = RuntimeFormIds.FromLoadOrder(_game.LoadOrder);

        TreeBillboard? ReadTree(FormKey fk)
        {
            if (!linkCache.TryResolve<ITreeGetter>(fk, out var tree)) return null;
            var model = tree.Model?.File;
            if (model is null || model.IsNull) return null;
            var stem = Path.GetFileNameWithoutExtension(model.DataRelativePath.Path.Replace('\\', '/')).ToLowerInvariant();
            var folder = GamePath.Join("textures", "terrain", "lodgen", fk.ModKey.FileName.String.ToLowerInvariant());
            var name = $"{stem}_{fk.ID:x8}";
            foreach (var (dds, firstView) in new[] { (name + ".dds", false), (name + "_1.dds", true) })
            {
                var ddsPath = GamePath.Join(folder, dds);
                if (!_assets.Exists(ddsPath) || !_assets.TryOpen(GamePath.Join(folder, name + ".txt"), out var st)) continue;
                using var reader = new StreamReader(st);
                var bb = TreeBillboard.Parse(ddsPath, reader.ReadToEnd(), firstView);
                if (bb is not null) return bb;
            }
            return null;
        }
        bool IsTree(FormKey fk) => linkCache.TryResolve<ITreeGetter>(fk, out _);
        var isTreeCache = new ConcurrentDictionary<FormKey, bool>();

        // 3) Walk winning REFRs. Enable state is decided after the walk, because an enable parent can be any
        //    reference anywhere in the load order. Only disabled refs and refs with a parent are remembered.
        var refs = new List<LodReference>(capacity: 200_000);
        var parentOf = new Dictionary<LodReference, FormKey>(ReferenceEqualityComparer.Instance);
        var dynamicCandidates = new Dictionary<LodReference, DynamicLodReference>(ReferenceEqualityComparer.Instance);
        var initiallyDisabled = new HashSet<LodReference>(ReferenceEqualityComparer.Instance);
        var trees = new List<TreeReference>(capacity: 200_000);
        var treeParentOf = new Dictionary<TreeReference, FormKey>(ReferenceEqualityComparer.Instance);

        // 3b) Child worldspaces copied into their parent's LOD (only children whose record really has that parent).
        var copyByChild = new Dictionary<FormKey, ChildWorldCopy?>();
        ChildWorldCopy? CopyFor(IWorldspaceGetter ws)
        {
            if (copyByChild.TryGetValue(ws.FormKey, out var c)) return c;
            c = _childCopies.ForChild(ws.EditorID);
            if (c is not null)
            {
                // The winning worldspace record decides the parent (the context's copy may be an older override).
                var winning = linkCache.TryResolve<IWorldspaceGetter>(ws.FormKey, out var w) ? w : ws;
                var parentKey = winning.Parent?.Worldspace.FormKeyNullable;
                string? parentEdid = parentKey is { } pk && linkCache.TryResolve<IWorldspaceGetter>(pk, out var pw) ? pw.EditorID : null;
                if (!grids.ContainsKey(c.Parent))
                {
                    progress?.Report($"Child worldspace {ws.EditorID}: not copied, its parent {c.Parent} has no LOD settings in this run");
                    c = null;
                }
                else if (!string.Equals(parentEdid, c.Parent, StringComparison.OrdinalIgnoreCase))
                {
                    progress?.Report($"Child worldspace {ws.EditorID}: not copied, its parent is {parentEdid ?? "(none)"} but the DynDOLOD config says {c.Parent}");
                    c = null;
                }
            }
            return copyByChild[ws.FormKey] = c;
        }
        var navmeshCells = new HashSet<FormKey>();
        if (_childCopies.Worlds.Any(w => w.NoCellsWithNavmesh && grids.ContainsKey(w.Parent)))
        {
            foreach (var nctx in priority.NavigationMesh().WinningContextOverrides(linkCache))
                if (nctx.TryGetParentSimpleContext<ICellGetter>(out var cellCtx)
                    && cellCtx.TryGetParentSimpleContext<IWorldspaceGetter>(out var nws)
                    && _childCopies.ForChild(nws.Record.EditorID) is { NoCellsWithNavmesh: true })
                    navmeshCells.Add(cellCtx.Record.FormKey);
        }
        var copiedRefs = new HashSet<LodReference>(ReferenceEqualityComparer.Instance);
        var baseEdid = new ConcurrentDictionary<FormKey, string?>();
        var copiedTrees = new List<TreeReference>();
        long childCopyIgnored = 0;
        // ChildworldMatches: a child reference that a parent placeholder stands in for. It's copied only when that
        // placeholder isn't in the parent's LOD (the rules remove Dragonsreach's WRCastleMainBuilding01LOD, for example).
        var presentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copyMatches = new Dictionary<object, IReadOnlyCollection<string>>(ReferenceEqualityComparer.Instance);
        var childSeen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long ignoredByWorldRule = 0;
        var states = new EnableStates(linkCache);
        long visited = 0, skippedDisabled = 0, skippedMissing = 0;

        foreach (var ctx in priority.PlacedObject().WinningContextOverrides(linkCache))
        {
            ct.ThrowIfCancellationRequested();
            visited++;
            if ((visited & 0x3FFFF) == 0) progress?.Report($"Visited {visited:N0} references, {refs.Count:N0} LOD refs so far");

            var r = ctx.Record;
            if (r.IsDeleted) continue; // deleted references never load, so they'd leave LOD that never hides
            states.Record(r.FormKey, r.MajorRecordFlagsRaw, r.EnableParent);

            if (!ctx.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsCtx)) continue; // interior
            var wsId = wsCtx.Record.EditorID;
            if (wsId is null) continue;
            if (resolver.Rules.IgnoresWorld(r.FormKey.ModKey.FileName.String, wsId)) { ignoredByWorldRule++; continue; }
            if (grids.ContainsKey(wsId)) Handle(wsId, null);
            // Walled cities: the child's references also go into the parent's LOD (DynDOLOD child world configs).
            if (CopyFor(wsCtx.Record) is { } copyCfg)
            {
                childSeen[copyCfg.Child] = childSeen.GetValueOrDefault(copyCfg.Child) + 1;
                if (copyCfg.NoCellsWithNavmesh && ctx.TryGetParentSimpleContext<ICellGetter>(out var cctx) && navmeshCells.Contains(cctx.Record.FormKey))
                    childCopyIgnored++;
                else
                    Handle(copyCfg.Parent, copyCfg);
            }

            void Handle(string wsId, ChildWorldCopy? copy)
            {
                var baseKey = r.Base.FormKeyNullable;
                if (baseKey is null || baseKey.Value.IsNull) return;
                if (copy is not null && copy.IgnoresEditorId(baseEdid.GetOrAdd(baseKey.Value, k => linkCache.TryResolve(k, out var b) ? b.EditorID : null)))
                { childCopyIgnored++; return; }

                if (options.TreeLod && isTreeCache.GetOrAdd(baseKey.Value, IsTree))
                {
                    var raw0 = r.MajorRecordFlagsRaw;
                    if ((raw0 & FlagInitiallyDisabled) != 0 && !options.IncludeInitiallyDisabled) { skippedDisabled++; return; }
                    bool parented = r.EnableParent is { } tep && !tep.Reference.IsNull;
                    if (parented && !options.IncludeEnableParented) { skippedDisabled++; return; }
                    if (r.Placement is not { } tp) return;

                    var bb = treeCache.GetOrAdd(baseKey.Value, ReadTree);
                    if (bb is null)
                    {
                        var key = linkCache.TryResolve<ITreeGetter>(baseKey.Value, out var tg) && tg.Model?.File is { IsNull: false } mf
                            ? $"{baseKey.Value.ModKey.FileName}\\{Path.GetFileNameWithoutExtension(mf.DataRelativePath.Path)}_{baseKey.Value.ID:x8}"
                            : baseKey.Value.ToString();
                        missingBillboards.AddOrUpdate(key, 1, (_, n) => n + 1);
                        return;
                    }
                    var tr = new TreeReference(r.FormKey.ToString(), wsId,
                        new Vector3(tp.Position.X, tp.Position.Y, tp.Position.Z), tp.Rotation.Z, r.Scale ?? 1f,
                        runtimeIds.Resolve(r.FormKey), bb, ObjectLod: copy is not null);
                    if (copy is not null)
                    {
                        copiedTrees.Add(tr);
                        if (_childCopies.MatchedParents(LodRules.FormIdKey(r.FormKey.ModKey.ToString(), r.FormKey.ID)) is { Count: > 0 } mp) copyMatches[tr] = mp;
                        if (parented) treeParentOf[tr] = r.FormKey;
                        return;
                    }
                    presentKeys.Add(LodRules.FormIdKey(r.FormKey.ModKey.ToString(), r.FormKey.ID));
                    trees.Add(tr);
                    if (parented) treeParentOf[tr] = r.FormKey;
                    return;
                }

                var info = baseCache.GetOrAdd(baseKey.Value, ReadBase);
                if (info is null) return;

                // Reference-specific rules are rare; everything else is resolved once per base object.
                var refKey = LodRules.FormIdKey(r.FormKey.ModKey.ToString(), r.FormKey.ID);
                var res = resolver.HasFormIdRule(refKey)
                    ? resolver.Resolve(refKey, info.FormIdKey, info.ModelPath, info.Mnam)
                    : resolved.GetOrAdd(baseKey.Value, _ => resolver.Resolve(null, info.FormIdKey, info.ModelPath, info.Mnam));
                if (!res.Meshes.HasAny)
                {
                    if (info.Mnam is not null) skippedMissing++;
                    return;
                }

                var raw = r.MajorRecordFlagsRaw;
                var flags = LodReferenceFlags.None;
                if ((raw & FlagPersistent) != 0) flags |= LodReferenceFlags.Persistent;
                if ((raw & FlagInitiallyDisabled) != 0) flags |= LodReferenceFlags.InitiallyDisabled;
                if ((raw & FlagIsFullLod) != 0) flags |= LodReferenceFlags.IsFullLod;
                if (r.EnableParent is not null) flags |= LodReferenceFlags.HasEnableParent;
                if (info.DistantFlag) flags |= LodReferenceFlags.BaseHasDistantLodFlag;

                var p = r.Placement;
                if (p is null) return;

                // Initially disabled refs may be enabled by a script later; with dynamic LOD they go to the controller.
                // ("Undelete and disable" leftovers sit far below the world and are never enabled, so they're skipped.)
                bool switchable = copy is null && options.DynamicLod && p.Position.Z > -29000f
                    && (flags.HasFlag(LodReferenceFlags.InitiallyDisabled) || flags.HasFlag(LodReferenceFlags.HasEnableParent));
                if (!switchable)
                {
                    if (flags.HasFlag(LodReferenceFlags.InitiallyDisabled) && !options.IncludeInitiallyDisabled) { skippedDisabled++; return; }
                    if (flags.HasFlag(LodReferenceFlags.HasEnableParent) && !options.IncludeEnableParented) { skippedDisabled++; return; }
                }

                var lodRef = new LodReference(
                    FormKey: r.FormKey.ToString(),
                    BaseFormKey: baseKey.Value.ToString(),
                    BaseEditorId: info.EditorId,
                    Worldspace: wsId,
                    WinningPlugin: ctx.ModKey.ToString(),
                    Position: new Vector3(p.Position.X, p.Position.Y, p.Position.Z),
                    RotationRadians: new Vector3(p.Rotation.X, p.Rotation.Y, p.Rotation.Z),
                    Scale: r.Scale ?? 1f,
                    Meshes: res.Meshes,
                    Flags: flags);
                refs.Add(lodRef);
                if (copy is not null)
                {
                    copiedRefs.Add(lodRef);
                    if (_childCopies.MatchedParents(refKey) is { Count: > 0 } mp) copyMatches[lodRef] = mp;
                }
                else presentKeys.Add(refKey);
                if (r.EnableParent is { } ep && !ep.Reference.IsNull) parentOf[lodRef] = r.FormKey;
                if (switchable)
                {
                    var parentKey = r.EnableParent is { } dep && !dep.Reference.IsNull ? dep.Reference.FormKey : (FormKey?)null;
                    dynamicCandidates[lodRef] = new DynamicLodReference(lodRef,
                        r.FormKey.ModKey.FileName.String, r.FormKey.ID,
                        wsCtx.Record.FormKey.ModKey.FileName.String, wsCtx.Record.FormKey.ID,
                        parentKey?.ModKey.FileName.String, parentKey?.ID ?? 0,
                        r.EnableParent?.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent) ?? false,
                        flags.HasFlag(LodReferenceFlags.InitiallyDisabled));
                    if (flags.HasFlag(LodReferenceFlags.InitiallyDisabled)) initiallyDisabled.Add(lodRef);
                }
            }
        }

        // 3c) Child copies: drop the ones the parent already has (same base or same LOD mesh within 32 units), e.g.
        //     city walls that exist in both worldspaces. Placeholders with a different model (Dragonsreach's
        //     WRCastleMainBuilding01LOD) are handled by the rules, which take them out of LOD.
        if (_childCopies.Worlds.Count > 0)
            progress?.Report($"Child worldspaces: {(childSeen.Count == 0 ? "no references seen in any configured child worldspace" : string.Join(", ", childSeen.Select(kv => $"{kv.Key} {kv.Value:N0} refs")))}; "
                + $"{copiedRefs.Count:N0} LOD refs and {copiedTrees.Count:N0} trees kept before de-duplication");
        if (copiedRefs.Count > 0 || copiedTrees.Count > 0)
        {
            const float near = 32f;
            var parentIndex = new Dictionary<(string Ws, int X, int Y), List<LodReference>>();
            foreach (var lr in refs)
            {
                if (copiedRefs.Contains(lr)) continue;
                var k = (lr.Worldspace, (int)MathF.Floor(lr.Position.X / 256f), (int)MathF.Floor(lr.Position.Y / 256f));
                if (!parentIndex.TryGetValue(k, out var list)) parentIndex[k] = list = [];
                list.Add(lr);
            }
            bool Duplicate(LodReference c)
            {
                int bx = (int)MathF.Floor(c.Position.X / 256f), by = (int)MathF.Floor(c.Position.Y / 256f);
                for (int dx = -1; dx <= 1; dx++)
                    for (int dy = -1; dy <= 1; dy++)
                        if (parentIndex.TryGetValue((c.Worldspace, bx + dx, by + dy), out var list))
                            foreach (var o in list)
                                if (Vector3.Distance(o.Position, c.Position) < near
                                    && (o.BaseFormKey == c.BaseFormKey || (o.Meshes.Lod4 ?? o.Meshes.Lod8) == (c.Meshes.Lod4 ?? c.Meshes.Lod8)))
                                    return true;
                return false;
            }
            bool StandIn(object c) => copyMatches.TryGetValue(c, out var parents) && parents.Any(presentKeys.Contains);
            int standIns = copiedRefs.Count(c => StandIn(c)) + copiedTrees.Count(t => StandIn(t));
            var duplicates = copiedRefs.Where(c => StandIn(c) || Duplicate(c)).ToHashSet(ReferenceEqualityComparer.Instance);
            int dup = refs.RemoveAll(duplicates.Contains);
            copiedRefs.ExceptWith(duplicates.Cast<LodReference>());

            var parentTrees = trees.Select(t => (t.Worldspace, t.Billboard.TexturePath, t.Position)).ToList();
            int treeDup = 0;
            foreach (var t in copiedTrees)
            {
                if (StandIn(t) || parentTrees.Any(o => o.Worldspace == t.Worldspace && o.TexturePath == t.Billboard.TexturePath && Vector3.Distance(o.Position, t.Position) < near))
                { treeDup++; continue; }
                trees.Add(t);
            }
            ChildCopies = copiedRefs.Count + copiedTrees.Count - treeDup;
            progress?.Report($"Child worldspaces: {copiedRefs.Count:N0} references and {copiedTrees.Count - treeDup:N0} trees copied into the parent's LOD "
                + $"({string.Join(", ", _childCopies.Worlds.Select(w => w.Child))}); {dup + treeDup:N0} already in the parent ({standIns:N0} of them stand-ins listed in ChildworldMatches), {childCopyIgnored:N0} ignored by the DynDOLOD child world rules");
        }

        if (ignoredByWorldRule > 0)
            progress?.Report($"IgnoreWorlds rules: {ignoredByWorldRule:N0} references left out (plugins whose rule files ignore those worldspaces)");

        // 4a) Dynamic LOD: refs that are switched while playing go to the SKSE controller instead of static LOD:
        //     initially disabled refs, refs whose parent chain starts disabled, and "opposite of parent" pairs
        //     (the usual destroyed/rebuilt switch, e.g. Helgen Reborn). Parented refs that simply start enabled
        //     and follow their parent stay in static LOD.
        var dynamic = new List<DynamicLodReference>();
        if (options.DynamicLod && dynamicCandidates.Count > 0)
        {
            foreach (var (lr, dyn) in dynamicCandidates)
            {
                bool toggles = initiallyDisabled.Contains(lr)
                    || (parentOf.TryGetValue(lr, out var fk) && (!states.StartsEnabled(fk) || states.ChainHasOpposite(fk)));
                if (toggles) dynamic.Add(dyn);
            }
            var moved = dynamic.Select(d => d.Ref).ToHashSet(ReferenceEqualityComparer.Instance);
            refs.RemoveAll(moved.Contains);
            foreach (var lr in moved) parentOf.Remove((LodReference)lr!);
            // Parented refs that weren't switchable enough for dynamic LOD but were only kept for it: apply the old rules.
            refs.RemoveAll(lr => dynamicCandidates.ContainsKey(lr) && !moved.Contains(lr)
                && ((lr.Flags.HasFlag(LodReferenceFlags.InitiallyDisabled) && !options.IncludeInitiallyDisabled)
                    || (lr.Flags.HasFlag(LodReferenceFlags.HasEnableParent) && !options.IncludeEnableParented)));
            progress?.Report($"Dynamic LOD: {dynamic.Count:N0} switchable references go to the SKSE controller");
        }

        // 4) Enable parents: keep a parented ref only if it starts out enabled (following the whole parent
        //    chain, including "opposite of parent"). Refs a quest enables later (Helgen Reborn's rebuilt
        //    town, for example) start disabled and stay out of static LOD; they belong to dynamic LOD.
        if (parentOf.Count > 0)
        {
            int before = refs.Count;
            refs.RemoveAll(lr => parentOf.TryGetValue(lr, out var fk) && !states.StartsEnabled(fk));
            skippedDisabled += before - refs.Count;
            progress?.Report($"Enable parents: {parentOf.Count:N0} parented LOD refs, {before - refs.Count:N0} start disabled and were left out");
        }
        if (treeParentOf.Count > 0)
        {
            int before = trees.Count;
            trees.RemoveAll(t => treeParentOf.TryGetValue(t, out var fk) && !states.StartsEnabled(fk));
            skippedDisabled += before - trees.Count;
        }
        if (options.TreeLod)
            progress?.Report($"Trees: {trees.Count:N0} with billboards, {missingBillboards.Values.Sum():N0} without ({missingBillboards.Count:N0} tree types have no billboard)");

        sw.Stop();
        var stats = new ScanStats(
            PluginsInLoadOrder: _game.PluginsListed,
            PlacedObjectsVisited: visited,
            LodReferencesFound: refs.Count,
            SkippedDisabled: skippedDisabled,
            SkippedMissingMesh: skippedMissing,
            MissingMeshes: missing.ToDictionary(kv => kv.Key, kv => kv.Value),
            Elapsed: sw.Elapsed);

        return new ScanResult(grids, refs, stats, trees, missingBillboards.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase), dynamic);
    }

    /// <summary>Initial enable state of references, following enable-parent chains.</summary>
    private sealed class EnableStates(Mutagen.Bethesda.Plugins.Cache.ILinkCache linkCache)
    {
        private readonly HashSet<FormKey> _disabled = [];
        private readonly Dictionary<FormKey, (FormKey Parent, bool Opposite)> _parents = [];
        private readonly Dictionary<FormKey, bool> _memo = [];

        public void Record(FormKey key, int rawFlags, IEnableParentGetter? parent)
        {
            if ((rawFlags & FlagInitiallyDisabled) != 0) _disabled.Add(key);
            if (parent is not null && !parent.Reference.IsNull)
                _parents[key] = (parent.Reference.FormKey, parent.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent));
        }

        public bool StartsEnabled(FormKey key) => Evaluate(key, 0);

        /// <summary>True if any link in the enable-parent chain uses "set enable state to opposite of parent".</summary>
        public bool ChainHasOpposite(FormKey key)
        {
            for (int depth = 0; depth < 32 && _parents.TryGetValue(key, out var link); depth++)
            {
                if (link.Opposite) return true;
                key = link.Parent;
            }
            return false;
        }

        private bool Evaluate(FormKey key, int depth)
        {
            if (_memo.TryGetValue(key, out var known)) return known;
            if (depth > 32) return true; // a cycle: the engine would ignore it too

            bool enabled;
            if (_parents.TryGetValue(key, out var link))
            {
                bool parent = Evaluate(link.Parent, depth + 1);
                enabled = !_disabled.Contains(key) && (link.Opposite ? !parent : parent);
            }
            else if (_disabled.Contains(key))
                enabled = false;
            else
                enabled = ActorStartsEnabled(key, depth);

            _memo[key] = enabled;
            return enabled;
        }

        // Parents that aren't REFRs we walked (placed actors, mostly). Anything unresolvable counts as enabled.
        private bool ActorStartsEnabled(FormKey key, int depth)
        {
            if (!linkCache.TryResolve<IPlacedNpcGetter>(key, out var npc)) return true;
            if ((npc.MajorRecordFlagsRaw & FlagInitiallyDisabled) != 0) return false;
            if (npc.EnableParent is not { } ep || ep.Reference.IsNull) return true;
            bool parent = Evaluate(ep.Reference.FormKey, depth + 1);
            return ep.Flags.HasFlag(EnableParent.Flag.SetEnableStateToOppositeOfParent) ? !parent : parent;
        }
    }
}
