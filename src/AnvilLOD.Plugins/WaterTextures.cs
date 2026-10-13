using System.Reflection;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Plugins;

/// <summary>The smooth water tile (diffuse and normal) the LOD fake water uses, shipped inside the tool and written to the output.</summary>
public static class WaterTextures
{
    /// <summary>Writes the two textures to <c>textures\anvillod\water</c> in the output folder.</summary>
    public static void WriteTo(string outputFolder)
    {
        foreach (var (resource, rel) in new[] { ("water.watertile_d.dds", NifGeometryReader.FakeWaterDiffuse), ("water.watertile_n.dds", NifGeometryReader.FakeWaterNormal) })
        {
            using var s = typeof(WaterTextures).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Missing embedded water texture " + resource);
            var full = Path.Combine(outputFolder, rel.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using var f = File.Create(full);
            s.CopyTo(f);
        }
    }
}
