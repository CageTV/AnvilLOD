using System.Text;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes <c>SKSE\Plugins\AnvilLOD\AnvilLOD.copies</c>, the Parent &gt; Child copies the AnvilLOD SKSE plugin draws while the player is
/// in a walled city's child worldspace (no plugin, no records: the model of each parent reference around the city is attached at
/// the reference's position, close to the player).
/// <code>
/// char[4] "ALCP", u32 version (1)
/// u32 stringCount, then per string: u16 byteLength + UTF-8 bytes   (plugin names and model paths)
/// u32 entryCount, then per entry (44 bytes):
///   u32 worldPlugin (string index), u32 worldLocalId   (the child worldspace),
///   u32 model (string index, relative to Data\meshes), f32 pos[3], f32 rot[3] (radians), f32 scale,
///   u32 flags (1 = the child worldspace has a cell at these coordinates, 2 = the base record swaps textures)
/// </code>
/// </summary>
public static class ParentCopyWriter
{
    public const string RelativePath = "SKSE\\Plugins\\AnvilLOD\\AnvilLOD.copies";
    public const uint Version = 1;

    public static int Write(string outputFolder, IReadOnlyList<ParentCopy> copies)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint Str(string s)
        {
            if (!index.TryGetValue(s, out var i))
            {
                i = (uint)strings.Count;
                strings.Add(s);
                index[s] = i;
            }
            return i;
        }

        var rows = new List<(uint WorldPlugin, uint Model, ParentCopy C)>();
        foreach (var c in copies.OrderBy(c => c.ChildWorldspace, StringComparer.Ordinal).ThenBy(c => c.CellX).ThenBy(c => c.CellY)
                     .ThenBy(c => c.SourcePlugin, StringComparer.Ordinal).ThenBy(c => c.SourceLocalId))
        {
            var rel = GamePath.Normalize(c.Model);
            if (rel.StartsWith("meshes\\", StringComparison.Ordinal)) rel = rel["meshes\\".Length..];
            rows.Add((Str(c.WorldPlugin), Str(rel), c));
        }

        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path + ".tmp"))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("ALCP"u8);
            w.Write(Version);
            w.Write((uint)strings.Count);
            foreach (var s in strings)
            {
                var b = Encoding.UTF8.GetBytes(s);
                w.Write((ushort)b.Length);
                w.Write(b);
            }
            w.Write((uint)rows.Count);
            foreach (var (worldPlugin, model, c) in rows)
            {
                w.Write(worldPlugin); w.Write(c.WorldLocalId); w.Write(model);
                w.Write(c.Position.X); w.Write(c.Position.Y); w.Write(c.Position.Z);
                w.Write(c.RotationRadians.X); w.Write(c.RotationRadians.Y); w.Write(c.RotationRadians.Z);
                w.Write(c.Scale);
                w.Write(Flags(c));
            }
        }
        File.Move(path + ".tmp", path, overwrite: true);
        return rows.Count;
    }

    public static uint Flags(ParentCopy c) => (c.ChildCellExists ? 1u : 0u) | (c.HasAlternateTextures ? 2u : 0u);

    /// <summary>Removes the file when the option is off, so an old one never lingers in the output.</summary>
    public static void Remove(string outputFolder)
    {
        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
    }
}
