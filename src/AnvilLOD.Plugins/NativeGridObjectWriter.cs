using System.Text;

namespace AnvilLOD.Plugins;

/// <summary>
/// Writes <c>SKSE\Plugins\AnvilLOD\AnvilLOD.native</c>: for every grid object that became a real reference in AnvilLOD.esp, which vanilla
/// reference it stands for and where that reference's enable state comes from. The AnvilLOD SKSE plugin keeps the new reference enabled
/// or disabled to match (the job DynDOLOD's script or DLL does for its references).
/// <code>
/// char[4] "ALNV", u32 version (1)
/// u32 stringCount, then per string: u16 byteLength + UTF-8 bytes   (plugin names)
/// u32 entryCount, then per entry (40 bytes in version 2, the first 28 in version 1):
///   u32 sourcePlugin (string index), u32 sourceLocalId   (the vanilla reference the grid object stands for),
///   u32 nativePlugin (string index), u32 nativeLocalId   (the real reference in AnvilLOD.esp),
///   u32 parentPlugin (string index, 0xFFFFFFFF = none), u32 parentLocalId   (the source's enable parent),
///   u32 flags (1 = enable state opposite of parent, 2 = the source is initially disabled, 4 = near: only enabled within the near grid distance),
///   f32 x, f32 y, f32 z   (version 2: where the reference is, for the near flag)
/// </code>
/// </summary>
public static class NativeGridObjectWriter
{
    public const string RelativePath = "SKSE\\Plugins\\AnvilLOD\\AnvilLOD.native";
    public const uint Version = 2;

    public sealed record Entry(NativeGridObjects.Pick Pick, uint NativeLocalId, string Plugin);

    public static int Write(string outputFolder, IReadOnlyList<Entry> entries)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        uint Str(string s)
        {
            if (!index.TryGetValue(s, out var i)) { i = (uint)strings.Count; strings.Add(s); index[s] = i; }
            return i;
        }
        var rows = new List<(uint SP, uint SId, uint NP, uint NId, uint PP, uint PId, uint Flags, float X, float Y, float Z)>();
        foreach (var e in entries)
        {
            var d = e.Pick.Source;
            rows.Add((Str(d.RefPlugin), d.RefLocalId, Str(e.Plugin), e.NativeLocalId,
                d.ParentPlugin is { } pp ? Str(pp) : 0xFFFFFFFFu, d.ParentLocalId,
                (d.ParentOpposite ? 1u : 0u) | (d.InitiallyDisabled ? 2u : 0u) | (e.Pick.Near ? 4u : 0u),
                d.Ref.Position.X, d.Ref.Position.Y, d.Ref.Position.Z));
        }
        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var fs = File.Create(path + ".tmp"))
        using (var w = new BinaryWriter(fs))
        {
            w.Write("ALNV"u8);
            w.Write(Version);
            w.Write((uint)strings.Count);
            foreach (var s in strings)
            {
                var b = Encoding.UTF8.GetBytes(s);
                w.Write((ushort)b.Length);
                w.Write(b);
            }
            w.Write((uint)rows.Count);
            foreach (var r in rows)
            {
                w.Write(r.SP); w.Write(r.SId); w.Write(r.NP); w.Write(r.NId); w.Write(r.PP); w.Write(r.PId); w.Write(r.Flags);
                w.Write(r.X); w.Write(r.Y); w.Write(r.Z);
            }
        }
        File.Move(path + ".tmp", path, overwrite: true);
        return rows.Count;
    }

    public static void Remove(string outputFolder)
    {
        var path = Path.Combine(outputFolder, RelativePath.Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
    }
}
