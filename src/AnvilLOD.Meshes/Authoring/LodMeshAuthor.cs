using System.Numerics;

namespace AnvilLOD.Meshes.Authoring;

/// <summary>Settings for one generated LOD level.</summary>
/// <param name="MaxError">Largest surface deviation allowed by the simplifier, in game units.</param>
/// <param name="MinPartSize">Parts whose bounding box diagonal is smaller than this are dropped (details nobody sees from afar).</param>
/// <param name="MaxTriangles">Optional budget: if the level has more triangles than this, it is simplified further (and small parts dropped) until it fits.</param>
public sealed record AuthorLevel(int Level, float MaxError, float MinPartSize, int? MaxTriangles = null);

/// <summary>
/// Turns a full model into object LOD source meshes (<c>name_lod_0/1/2.nif</c>): small parts are dropped, the rest
/// is simplified with <see cref="MeshSimplifier"/>, textures stay those of the full model.
/// </summary>
public static class LodMeshAuthor
{
    public static readonly AuthorLevel[] DefaultLevels =
    [
        new(0, 6f, 32f),     // LOD4: about 4–8 cells out
        new(1, 20f, 96f),    // LOD8
        new(2, 60f, 256f),   // LOD16
    ];

    /// <param name="Budget">The triangle budget that applied, if any.</param>
    /// <param name="OverBudget">True when the budget could not be met (every part is already at its minimum).</param>
    public sealed record LevelResult(int Level, IReadOnlyList<LodMeshPart> Parts, int Triangles, int DroppedParts, int? Budget = null, bool OverBudget = false);

    public static List<LevelResult> Build(LodMesh full, IReadOnlyList<AuthorLevel>? levels = null)
    {
        levels ??= DefaultLevels;
        var results = new List<LevelResult>();
        var usable = full.Parts.Where(p => p.TriangleCount > 0 && !IsBlended(p.Material)).ToList();
        if (usable.Count == 0) return results;

        int? previous = null;   // triangles of the level before: a farther level never gets more than a nearer one
        foreach (var lv in levels.OrderBy(l => l.Level))
        {
            var kept = usable.Where(p => Diagonal(p) >= lv.MinPartSize).ToList();
            int dropped = usable.Count - kept.Count;
            if (kept.Count == 0)
            {
                kept = [usable.OrderByDescending(Diagonal).First()]; // always keep the main silhouette
                dropped = usable.Count - 1;
            }
            var parts = kept.Select(p => MeshSimplifier.Simplify(p, lv.MaxError)).Where(p => p.TriangleCount > 0).ToList();
            int triangles = parts.Sum(p => p.TriangleCount);

            int? budget = lv.MaxTriangles is > 0 ? lv.MaxTriangles : null;
            if (budget is not null && previous is { } above) budget = Math.Min(budget.Value, above);
            if (budget is { } cap && triangles > cap)
                (parts, triangles, dropped) = FitBudget(kept, parts, triangles, dropped, cap, lv);

            results.Add(new LevelResult(lv.Level, parts, triangles, dropped, budget, budget is { } b && triangles > b));
            if (lv.MaxTriangles is > 0) previous = triangles;
        }
        return results;
    }

    /// <summary>
    /// Fits the level to the budget. First by searching for the error that gives just under the budget, always simplifying
    /// the original parts (re-simplifying simplified parts piles errors up and throws detail away). The triangle count falls
    /// smoothly with the error, so a few tries find it. If even a very large error can't get there (every part is at its
    /// minimum), simplifies further and drops the smallest parts.
    /// </summary>
    private static (List<LodMeshPart> Parts, int Triangles, int Dropped) FitBudget(List<LodMeshPart> original, List<LodMeshPart> parts, int triangles, int dropped, int cap, AuthorLevel lv)
    {
        static int Count(List<LodMeshPart> l) => l.Sum(p => p.TriangleCount);
        static List<LodMeshPart> Run(IEnumerable<LodMeshPart> input, float error) =>
            input.Select(p => MeshSimplifier.Simplify(p, error)).Where(p => p.TriangleCount > 0).ToList();

        // Stage 1: search the error from the original parts. Triangles fall roughly as error^-0.85.
        const float Slope = 0.85f;
        List<LodMeshPart>? best = null;
        int bestTriangles = 0;
        float eLo = lv.MaxError, eHi = 0;     // eLo: too many triangles; eHi: fits
        int tLo = triangles, tHi = 0;
        float e = eLo * MathF.Pow((float)tLo / cap, 1f / Slope);
        List<LodMeshPart> last = parts;
        int lastTriangles = triangles;
        for (int i = 0; i < 7; i++)
        {
            last = Run(original, e);
            lastTriangles = Count(last);
            if (lastTriangles <= cap)
            {
                if (lastTriangles > bestTriangles) { best = last; bestTriangles = lastTriangles; }
                eHi = e; tHi = lastTriangles;
                if (lastTriangles >= cap * 0.9f) break;
            }
            else { eLo = e; tLo = lastTriangles; }

            if (eHi > 0)   // bracketed: interpolate in log space towards 95% of the budget
            {
                double span = Math.Log(tLo) - Math.Log(Math.Max(1, tHi));
                double f = span > 1e-6 ? (Math.Log(tLo) - Math.Log(cap * 0.95)) / span : 0.5;
                f = Math.Clamp(f, 0.1, 0.9);
                e = (float)Math.Exp(Math.Log(eLo) + f * (Math.Log(eHi) - Math.Log(eLo)));
            }
            else e = MathF.Max(eLo * 1.25f, eLo * MathF.Pow((float)tLo / (cap * 0.95f), 1f / Slope));
        }
        if (best is not null) return (best, bestTriangles, dropped);
        if (lastTriangles < triangles) { parts = last; triangles = lastTriangles; }
        float error = MathF.Max(e, lv.MaxError), minPart = lv.MinPartSize;
        for (int pass = 0; pass < 40 && triangles > cap; pass++)
        {
            float step = Math.Clamp((float)triangles / cap, 1.25f, 6f);
            error *= step;
            var basis = parts;
            var next = Run(parts, error);
            int nextTriangles = Count(next);

            if (nextTriangles >= triangles)   // simplifying alone has stalled: let the smallest parts go too
            {
                minPart = MathF.Max(minPart * 1.5f, parts.Min(Diagonal) * 1.01f);
                var survivors = parts.Where(p => Diagonal(p) >= minPart).ToList();
                if (survivors.Count == 0) survivors = [parts.OrderByDescending(Diagonal).First()];
                if (survivors.Count == parts.Count) break;   // nothing left to remove
                dropped += parts.Count - survivors.Count;
                basis = survivors;
                next = Run(survivors, error);
                nextTriangles = Count(next);
            }

            // The step overshot well below the budget: narrow in on it, so detail isn't thrown away for nothing.
            if (nextTriangles <= cap && nextTriangles < cap * 0.85f && Count(basis) > cap)
            {
                float lo = error / step, hi = error;
                for (int k = 0; k < 6; k++)
                {
                    float mid = (lo + hi) * 0.5f;
                    var trial = Run(basis, mid);
                    int trialTriangles = Count(trial);
                    if (trialTriangles <= cap)
                    {
                        if (trialTriangles > nextTriangles) { next = trial; nextTriangles = trialTriangles; }
                        hi = mid;
                    }
                    else lo = mid;
                }
            }
            parts = next;
            triangles = nextTriangles;
        }
        return (parts, triangles, dropped);
    }

    /// <summary>Alpha blending without alpha testing (decals, glass, fog cards): not drawn by object LOD anyway.</summary>
    private static bool IsBlended(LodMaterial m) => m.HasAlpha && (m.AlphaFlags & 0x0001) != 0 && (m.AlphaFlags & 0x0200) == 0;

    public static float Diagonal(LodMeshPart p)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var v in p.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return Vector3.Distance(min, max);
    }

    public static (Vector3 Min, Vector3 Max) Bounds(LodMesh mesh)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in mesh.Parts)
            foreach (var v in p.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return (min, max);
    }
}
