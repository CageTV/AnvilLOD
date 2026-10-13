using System.Numerics;

namespace AnvilLOD.Meshes.Authoring;

/// <summary>
/// Simplifies a whole-ship model to a triangle budget. The generic <see cref="LodMeshAuthor"/> fit raises one error for every part
/// and, when that stalls, throws parts away by size; on a ship that keeps a single hull and loses the deck, masts and rigging,
/// because the shield and rope parts can't be reduced much (their UV seams stop the simplifier) while the hull can.
/// Here the parts compete instead: every part starts at a small error, and while the ship is over budget the part with the most
/// triangles per unit of its own size (decoration and rigging, never the smooth hull) is simplified one step further, or dropped
/// when it is already at the coarsest step. The smoothest of the large parts (the hull) is always kept. Decoration (the shield
/// rows) is what goes first; rigging, sails and masts only go when the ship is still far over budget with everything at its
/// coarsest, so a big ship keeps its sails. Parts that sit exactly on top of another (the paint over a hull, a floor and its
/// alpha-tested copy) are pushed out a little after simplifying: simplified separately they stop matching, and the two surfaces
/// would otherwise fight over the same pixels.
/// </summary>
public sealed class ShipLodBuilder
{
    /// <summary>Errors (game units) a part is simplified with, finest first.</summary>
    public static readonly float[] Ladder = [2f, 4f, 8f, 16f, 32f, 64f, 128f, 256f];

    private sealed class Entry(LodMeshPart original)
    {
        public LodMeshPart Original { get; } = original;
        public float Diagonal { get; } = LodMeshAuthor.Diagonal(original);
        public LodMeshPart?[] Rungs { get; } = new LodMeshPart?[Ladder.Length];
        public int Rung;
        public bool Dropped;
        /// <summary>The part lies exactly on another, opaque one and is alpha-tested: paint, or a copy of a floor.</summary>
        public bool Overlay;
        public bool Decoration { get; } = IsDecoration(original);
        /// <summary>Coarsest step this part may be simplified to. An overlay stays fine, or its lift would have to grow with the error.</summary>
        public int MaxRung => Overlay || Hull ? OverlayMaxRung : Ladder.Length - 1;
        /// <summary>The hull keeps its shape too: it must not take the whole cut just because the rigging can't be reduced.</summary>
        public bool Hull;

        public LodMeshPart Current()
            => Rungs[Rung] ??= Place(MeshSimplifier.Simplify(Original, Ladder[Rung]), Overlay ? Ladder[Rung] + OverlayLift : 0f);

        private static LodMeshPart Place(LodMeshPart part, float lift)
        {
            if (lift <= 0f) return part;
            var moved = new Vector3[part.VertexCount];
            for (int i = 0; i < moved.Length; i++) moved[i] = part.Positions[i] + part.Normals[i] * lift;
            return new LodMeshPart
            {
                Name = part.Name, Material = part.Material, Positions = moved, UVs = part.UVs, Normals = part.Normals,
                Tangents = part.Tangents, Bitangents = part.Bitangents, Colors = part.Colors, Triangles = part.Triangles,
            };
        }
    }

    /// <summary>Units an overlay is lifted off the surface under it, on top of the simplification error.</summary>
    private const float OverlayLift = 2f;

    /// <summary>Index in <see cref="Ladder"/> of the coarsest error an overlay is simplified with (32 units).</summary>
    private const int OverlayMaxRung = 4;

    /// <summary>With everything at its coarsest, rigging and structure are only dropped when the ship is still this many times over budget.</summary>
    private const double StructureSlack = 1.8;

    private static bool IsDecoration(LodMeshPart p)
        => p.Material.Textures.Count > 0 && p.Material.Textures[0].Contains("shield", StringComparison.OrdinalIgnoreCase);

    private static (Vector3 Center, Vector3 Size) Box(LodMeshPart p)
    {
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        foreach (var v in p.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return ((min + max) * 0.5f, max - min);
    }

    private readonly List<Entry> _entries;

    /// <param name="full">The ship's full model.</param>
    public ShipLodBuilder(LodMesh full)
    {
        _entries = full.Parts
            .Where(p => p.TriangleCount > 0 && !(p.Material.HasAlpha && (p.Material.AlphaFlags & 0x0001) != 0 && (p.Material.AlphaFlags & 0x0200) == 0))
            .Select(p => new Entry(p))
            .ToList();
        MarkOverlays();
    }

    private void MarkOverlays()
    {
        foreach (var o in _entries)
        {
            var m = o.Original.Material;
            if (!m.HasAlpha) continue;
            var (oc, os) = Box(o.Original);
            foreach (var b in _entries)
            {
                if (ReferenceEquals(b, o) || b.Original.Material.HasAlpha) continue;
                if (!string.Equals(b.Original.Material.Textures[0], m.Textures[0], StringComparison.OrdinalIgnoreCase)) continue;
                var (bc, bs) = Box(b.Original);
                if (Vector3.Distance(oc, bc) <= 4f && Vector3.Distance(os, bs) <= 4f) { o.Overlay = true; break; }
            }
        }
    }

    public sealed record Result(IReadOnlyList<LodMeshPart> Parts, int Triangles, int DroppedParts, bool OverBudget);

    /// <summary>The ship at a budget. Levels asked for in order of falling budget reuse the simplifications already made.</summary>
    /// <param name="minPartSize">Parts whose bounding box diagonal is smaller than this are not drawn at all.</param>
    /// <param name="dropOverlays">Leave out the parts that lie on top of others (paint over a hull): from far away they add nothing.</param>
    public Result Fit(int budget, float minPartSize, bool dropOverlays = false)
    {
        foreach (var e in _entries) { e.Rung = 0; e.Dropped = e.Diagonal < minPartSize || (dropOverlays && e.Overlay); }
        // The hull: the planking-side texture's part if there is one (ShipWoodSide01 in the game's own ships), else, of the parts at least
        // half as big as the biggest, the one with the fewest triangles per unit of size once simplified.
        float biggest = _entries.Count == 0 ? 0 : _entries.Max(e => e.Diagonal);
        var keep = _entries.Where(e => e.Diagonal >= biggest * 0.5f && !e.Overlay && e.Original.Material.Textures[0].Contains("side", StringComparison.OrdinalIgnoreCase)).Take(1).ToList();
        if (keep.Count == 0)
            keep = _entries.Where(e => e.Diagonal >= biggest * 0.5f)
                .OrderBy(e => MeshSimplifier.Simplify(e.Original, Ladder[^1]).TriangleCount / Math.Max(1.0, e.Diagonal)).Take(1).ToList();
        foreach (var e in _entries) e.Hull = false;
        foreach (var e in keep) { e.Dropped = false; e.Hull = true; }

        int Total() => _entries.Where(e => !e.Dropped).Sum(e => e.Current().TriangleCount);
        int total = Total();
        double Weight(Entry e) => e.Current().TriangleCount / Math.Max(1.0, e.Diagonal) * (e.Decoration ? 3.0 : 1.0);
        void Coarsen(Entry e)
        {
            int before = e.Current().TriangleCount;
            e.Rung++;
            // A step that changes nothing is skipped through, so the loop always makes progress.
            while (e.Rung < e.MaxRung && e.Current().TriangleCount >= before) e.Rung++;
        }

        while (total > budget)
        {
            // First everything gets simplified further, densest part first (decoration counts triple and is dropped at its coarsest).
            var movable = _entries.Where(e => !e.Dropped && (e.Rung < e.MaxRung || e.Decoration)).ToList();
            if (movable.Count > 0)
            {
                var worst = movable.OrderByDescending(Weight).First();
                if (worst.Rung < worst.MaxRung) Coarsen(worst); else worst.Dropped = true;
            }
            else if (total > budget * StructureSlack)
            {
                // Everything is at its coarsest and the ship is still far over: rigging and structure start to go, densest first.
                // A paint overlay goes before anything else: the hull under it is still there.
                var worst = _entries.Where(e => !e.Dropped && !keep.Contains(e)).OrderByDescending(e => Weight(e) * (e.Overlay ? 4.0 : 1.0)).FirstOrDefault();
                if (worst is null) break;
                worst.Dropped = true;
            }
            else break;   // slightly over budget is better than a ship without its masts and sails
            total = Total();
        }

        var parts = _entries.Where(e => !e.Dropped).Select(e => e.Current()).ToList();
        return new Result(parts, total, _entries.Count(e => e.Dropped), total > budget);
    }
}
