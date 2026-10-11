using System.Globalization;
using AnvilLOD.Core.Lod;
using AnvilLOD.Meshes;

namespace AnvilLOD.Plugins;

/// <summary>
/// What the last generation was made of, so the next one can be estimated for the user's own list: size per LOD level
/// and layer of the base files, and the seasonal copies on disk. Saved with the settings (per preset).
/// </summary>
public sealed record SizeCalibration(
    int GrassPercent,
    bool GrassOn,
    bool Tree3D,
    bool Tree3DLod8,
    Dictionary<string, long> BaseBytes,   // "<LOD level>|<layer>" -> bytes of the base files
    long BaseTotal,                        // bytes of all base object LOD on disk
    long SeasonTotal);                     // bytes of all seasonal object LOD on disk (0 = no seasons in that run)

public sealed record SizeEstimate(long Base, long? Seasons, string Note);

public static class SizeEstimator
{
    // Measured on a real list (Tamriel, objects + grass, no seasons): total object LOD megabytes at each grass density.
    // About 270 MB of that is not grass, so the grass part is what the density changes.
    private static readonly (int Percent, double Mb)[] GrassMb = [(4, 703), (8, 1060), (15, 1699), (25, 2672), (40, 3863), (60, 6121), (100, 10999)];
    private const double NonGrassMb = 270;

    private static double GrassCost(int percent)
    {
        var best = GrassMb[0];
        foreach (var g in GrassMb) if (Math.Abs(g.Percent - percent) < Math.Abs(best.Percent - percent)) best = g;
        return Math.Max(1, best.Mb - NonGrassMb);
    }

    /// <summary>Called after a generation: what it was made of. Null when the run didn't record sizes.</summary>
    public static SizeCalibration? Calibrate(GenerateStats gen, string outputFolder, int grassPercent, bool grassOn, bool tree3d, bool tree3dLod8)
    {
        if (gen.BytesByLevel is not { Count: > 0 } byLevel || gen.SizeBreakdown is not { } breakdown) return null;
        var bytes = new Dictionary<string, long>();
        foreach (var (level, levelBytes) in byLevel)
        {
            var parts = breakdown.Where(kv => kv.Key.Level == level).ToList();
            double total = parts.Sum(kv => SizeBreakdown.EstimatedBytes(kv.Value.Triangles, kv.Value.Vertices));
            if (total <= 0) continue;
            foreach (var kv in parts)
                bytes[$"{level}|{kv.Key.Category}"] = (long)(levelBytes * (SizeBreakdown.EstimatedBytes(kv.Value.Triangles, kv.Value.Vertices) / total));
        }
        var disk = SizeBreakdown.DiskBySeason(outputFolder);
        long baseTotal = disk.TryGetValue("base", out var b) ? b.Values.Sum() : 0;
        long seasons = disk.Where(kv => kv.Key != "base").Sum(kv => kv.Value.Values.Sum());
        return new SizeCalibration(grassPercent, grassOn, tree3d, tree3dLod8, bytes, baseTotal, seasons);
    }

    /// <summary>The size of a generation with the given settings, scaled from the last one. Null if there's nothing to scale from.</summary>
    public static SizeEstimate? Estimate(SizeCalibration c, int grassPercent, bool grassOn, bool tree3d, bool tree3dLod8, bool seasons)
    {
        var unknown = new List<string>();
        if (grassOn && !c.GrassOn) unknown.Add("grass");
        if (tree3d && !c.Tree3D) unknown.Add("3D trees");
        if (tree3d && tree3dLod8 && c.Tree3D && !c.Tree3DLod8) unknown.Add("3D trees at LOD8");

        double newBase = 0;
        foreach (var (key, value) in c.BaseBytes)
        {
            var bar = key.IndexOf('|');
            int level = int.Parse(key[..bar], CultureInfo.InvariantCulture);
            string layer = key[(bar + 1)..];
            double factor = layer switch
            {
                LodLayers.Grass => !grassOn ? 0 : c.GrassOn ? GrassCost(grassPercent) / GrassCost(c.GrassPercent) : 0,
                LodLayers.Tree3D => !tree3d ? 0 : level == 8 && !tree3dLod8 ? 0 : (level == 8 && !c.Tree3DLod8) || !c.Tree3D ? 0 : 1,
                _ => 1,
            };
            newBase += value * factor;
        }
        double scale = c.BaseTotal > 0 ? newBase / c.BaseTotal : 1;
        long? seasonBytes = !seasons ? 0 : c.SeasonTotal > 0 ? (long)(c.SeasonTotal * scale) : null;
        var note = unknown.Count > 0 ? $"Left out of the estimate because your last run had none to measure: {string.Join(", ", unknown)}." : "";
        return new SizeEstimate((long)newBase, seasonBytes, note);
    }

    public static string Format(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:F1} GB" : $"{bytes / 1048576.0:F0} MB";
}
