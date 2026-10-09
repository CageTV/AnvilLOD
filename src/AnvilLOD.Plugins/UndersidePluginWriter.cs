using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes the plugins that place the terrain underside, the way DynDOLOD does, both flagged ESL so they take no plugin slot:
/// <list type="bullet">
/// <item><c>AnvilLOD Underside.esm</c>: one moveable static per worldspace pointing at
/// <c>Terrain\&lt;ws&gt;\&lt;ws&gt;_Underside.nif</c>. Its FormIDs start at 0x800 and it has no masters.</item>
/// <item><c>AnvilLOD Underside.esp</c>: one persistent reference to that static, 500 units below the worldspace origin, in the
/// worldspace's own persistent cell. It needs the worldspaces' plugins as masters, which is why it is not part of the ESM: an
/// ESM-flagged file must not depend on plugins that aren't master-flagged.</item>
/// </list>
/// The worldspace and its persistent cell are written as overrides of the winners at generation time; load the ESP after the
/// plugins that change those worldspaces and run Generate again whenever they change.
/// </summary>
public static class UndersidePluginWriter
{
    public const string FileName = "AnvilLOD Underside.esm";
    public const string PlacementFileName = "AnvilLOD Underside.esp";

    /// <summary>How far below the world the underside sits. DynDOLOD uses the same value.</summary>
    public const float Depth = -500f;

    private static string UndersideModelPath(string worldspace) => AnvilLOD.Meshes.UndersideBuilder.ModelPath(worldspace);

    private const int FlagNotPlayable = 0x4, FlagPersistent = 0x400, FlagIsFullLod = 0x10000; // the flags DynDOLOD sets

    public sealed record Result(string Path, string PlacementPath, IReadOnlyList<string> Worldspaces, IReadOnlyList<string> Skipped, IReadOnlyList<string> Masters);

    /// <param name="worldspaces">EditorIDs of the worldspaces whose underside NIF was generated.</param>
    public static Result Write(GameContext game, IReadOnlyCollection<string> worldspaces, string outputFolder)
    {
        var wanted = new HashSet<string>(worldspaces, StringComparer.OrdinalIgnoreCase);
        var esm = new SkyrimMod(ModKey.FromNameAndExtension(FileName), SkyrimRelease.SkyrimSE) { IsMaster = true, IsSmallMaster = true };
        esm.ModHeader.Stats.NextFormID = 0x800;
        var esp = new SkyrimMod(ModKey.FromNameAndExtension(PlacementFileName), SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        esp.ModHeader.Stats.NextFormID = 0x800;

        var done = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ctx in game.LoadOrder.PriorityOrder.Worldspace().WinningContextOverrides())
        {
            var id = ctx.Record.EditorID;
            if (id is null || !wanted.Contains(id) || !seen.Add(id)) continue;

            var stat = new MoveableStatic(esm) { EditorID = $"AnvilLOD_{id}_UndersideBase", MajorRecordFlagsRaw = FlagNotPlayable };
            esm.MoveableStatics.Add(stat);
            stat.Model = new Model { File = UndersideModelPath(id) };
            stat.ObjectBounds.First = new P3Int16(-4096, -4096, -4096);
            stat.ObjectBounds.Second = new P3Int16(4096, 4096, 4096);

            // The persistent cell has to be the worldspace's own, not a new one: override it, then drop the copied refs
            // (they stay with their plugins; only ours is written).
            // The winning worldspace record can be an override that carries no cells (a CC plugin changing one field),
            // so ask every version for the persistent cell.
            ICell top;
            var topKey = ctx.Record.TopCell?.FormKey
                ?? game.LinkCache.ResolveAllContexts<IWorldspace, IWorldspaceGetter>(ctx.Record.FormKey)
                    .Select(c => c.Record.TopCell?.FormKey).FirstOrDefault(k => k is not null);
            if (topKey is { } key && game.LinkCache.TryResolveContext<ICell, ICellGetter>(key, out var cellCtx))
            {
                top = cellCtx.GetOrAddAsOverride(esp);
            }
            else
            {
                var ws = ctx.GetOrAddAsOverride(esp);
                top = ws.TopCell ??= new Cell(esp);
            }
            top.Persistent.Clear();
            top.Temporary.Clear();
            top.Persistent.Add(new PlacedObject(esp)
            {
                EditorID = $"AnvilLOD_{id}_Underside",
                Base = new FormLinkNullable<IPlaceableObjectGetter>(stat.FormKey),
                MajorRecordFlagsRaw = FlagPersistent | FlagIsFullLod,
                Placement = new Placement { Position = new Noggog.P3Float(0, 0, Depth), Rotation = new Noggog.P3Float(0, 0, 0) },
            });
            done.Add(id);
        }

        var skipped = wanted.Where(w => !seen.Contains(w)).OrderBy(w => w, StringComparer.OrdinalIgnoreCase).ToList();
        Directory.CreateDirectory(outputFolder);
        var esmPath = Path.Combine(outputFolder, FileName);
        var espPath = Path.Combine(outputFolder, PlacementFileName);
        var order = game.LoadOrder.ListedOrder.Select(l => l.ModKey).Where(k => k != esm.ModKey && k != esp.ModKey).Append(esm.ModKey).ToList();
        esm.WriteToBinary(esmPath, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            RecordCount = RecordCountOption.Iterate,
        });
        esp.WriteToBinary(espPath, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.CorrectToPath,
            MastersListOrdering = new MastersListOrderingByLoadOrder(order),
            RecordCount = RecordCountOption.Iterate,
        });

        using var written = SkyrimMod.CreateFromBinaryOverlay(espPath, SkyrimRelease.SkyrimSE);
        var masters = written.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        return new Result(esmPath, espPath, done, skipped, masters);
    }
}
