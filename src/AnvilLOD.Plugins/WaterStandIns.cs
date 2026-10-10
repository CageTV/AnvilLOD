using System.Security.Cryptography;
using System.Text;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Plugins;

/// <summary>
/// Stream, creek and pond water planes are grid objects the SKSE plugin draws beyond the loaded cells. Their LOD
/// twins (DynDOLOD Resources' <c>tundrastreamstraight01watera_dyndolod_lod.nif</c> and friends) are a single flat plane
/// with a <c>BSWaterShaderProperty</c>; drawn outside the engine's water system that shader comes out black. This writes,
/// for each such mesh, a stand-in into the output (<c>meshes\anvillod\water\...</c>) whose water planes use a lit,
/// alpha-blended "fake water" material (the one CS Water Mod's own stream twins use), and tells the plugin to draw
/// the stand-in. Every other shape of the mesh is carried over unchanged; the original mesh is never touched.
/// </summary>
public static class WaterStandIns
{
    public const string Folder = "anvillod\\water";

    /// <summary>Old mesh path (relative to Data\meshes, normalized) to the stand-in's path.</summary>
    public sealed record Result(IReadOnlyDictionary<string, string> Map, int Converted, int NoWaterShader, int Unreadable, IReadOnlyList<string> Names);

    /// <summary>
    /// Every mesh the SKSE plugin treats as a water plane (same rule as its <c>IsWaterMesh</c>: "water" in the path, but not a
    /// waterfall or water wheel). That is the whole Water1024 / Water2048 family of creek, river and pond planes plus the tundra
    /// stream pieces; the sea and big lakes are not placed objects at all (xLODGen's terrain LOD draws those). A mesh is only
    /// converted if it really has a water-shader shape, so lit meshes with "water" in the name are left alone.
    /// </summary>
    public static bool DefaultFilter(string meshPath)
    {
        var p = GamePath.Normalize(meshPath).ToLowerInvariant();
        return p.Contains("water") && !p.Contains("waterfall") && !p.Contains("waterwheel");
    }

    /// <param name="meshes">Grid object mesh paths, relative to Data\meshes.</param>
    public static Result Build(IEnumerable<string> meshes, IAssetSource assets, string outputFolder, Func<string, bool>? filter = null)
    {
        filter ??= DefaultFilter;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        int converted = 0, noWater = 0, unreadable = 0;

        foreach (var raw in meshes.Select(GamePath.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var rel = raw.StartsWith("meshes\\", StringComparison.Ordinal) ? raw["meshes\\".Length..] : raw;
            if (!filter(rel)) continue;
            if (!assets.TryOpen("meshes\\" + rel, out var st)) { unreadable++; continue; }
            byte[] bytes;
            using (st) { using var ms = new MemoryStream(); st.CopyTo(ms); bytes = ms.ToArray(); }

            LodMesh mesh;
            try { mesh = NifGeometryReader.Read(rel, bytes, passthru: false, waterAsLit: true); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException or NotSupportedException) { unreadable++; continue; }
            if (!mesh.Warnings.Any(w => w.StartsWith(NifGeometryReader.WaterPlaneWarning, StringComparison.Ordinal))) { noWater++; continue; }
            if (mesh.Parts.Count == 0) { unreadable++; continue; }

            var stem = Path.GetFileNameWithoutExtension(rel.Replace('/', '\\'));
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rel.ToLowerInvariant())), 0, 3).ToLowerInvariant();
            var standIn = GamePath.Join(Folder, $"{stem}_{hash}.nif");
            var full = Path.Combine(outputFolder, "meshes", standIn.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, LodNifWriter.Write(stem, mesh.Parts, "AnvilLOD water stand-in"));
            map[rel] = standIn;
            names.Add(rel);
            converted++;
        }
        return new Result(map, converted, noWater, unreadable, names);
    }
}
