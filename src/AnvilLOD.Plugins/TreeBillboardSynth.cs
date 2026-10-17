using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Authoring;
using AnvilLOD.Meshes.Nif;
using AnvilLOD.Plugins.Authoring;
using AnvilLOD.Textures;
using AnvilLOD.Textures.Dds;

namespace AnvilLOD.Plugins;

/// <summary>
/// Tree billboards without TexGen: renders the tree's own model and textures into TexGen-style files
/// (<c>textures\terrain\lodgen\&lt;plugin&gt;\&lt;model&gt;_&lt;formid&gt;.dds/_1.dds/_1_n.dds/.txt</c>) in a cache folder that the asset index
/// reads as its lowest-priority layer, so a real TexGen billboard always wins and everything downstream (tree atlas, cards, 3D trees)
/// sees an ordinary billboard. A billboard is re-rendered only when the model or one of its textures changed.
/// </summary>
public sealed class TreeBillboardSynth(IAssetSource assets, string? cacheRoot = null)
{
    private readonly string _root = cacheRoot ?? CacheRoot;

    /// <summary>Trees lower than this get no billboard (TexGen and DynDOLOD skip them too).</summary>
    public const float MinHeight = 256f;
    public const string TooSmall = "too small for a billboard";

    public static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnvilLOD", "TreeBillboards");

    private readonly ConcurrentDictionary<string, (byte[], int, int)?> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _write = new();

    /// <summary>Renders (or finds up to date) the billboard for <paramref name="model"/>. False with a reason when there is nothing to draw.</summary>
    /// <param name="pluginFolder">Lower-case plugin file name: the lodgen sub folder.</param>
    /// <param name="name">"model_formid" (formid as 8 hex digits, lower case).</param>
    public bool TryRender(string model, string pluginFolder, string name, out string reason)
    {
        reason = "";
        model = GamePath.Normalize(model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : "meshes\\" + model);
        if (!assets.TryOpen(model, out var st)) { reason = "model file missing"; return false; }
        byte[] bytes;
        using (st) { using var ms = new MemoryStream(); st.CopyTo(ms); bytes = ms.ToArray(); }

        LodMesh mesh;
        try { mesh = NifGeometryReader.Read(model, bytes); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException or ArgumentException or EndOfStreamException)
        { reason = "model unreadable: " + ex.Message; return false; }
        if (mesh.Parts.Count == 0) { reason = "model has no drawable shapes"; return false; }

        var (min, max) = LodMeshAuthor.Bounds(mesh);
        if (max.Z - min.Z < MinHeight) { reason = TooSmall; return false; }

        // What the picture depends on: the model and its textures.
        var sb = new StringBuilder().Append(Convert.ToHexString(SHA256.HashData(bytes)));
        foreach (var t in mesh.Parts.SelectMany(p => p.Material.Textures.Take(1)).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            sb.Append('|').Append(assets.Fingerprint(TexturePath(t)));
        var source = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v2|" + sb))).ToLowerInvariant()[..24];

        var dir = Path.Combine(_root, "textures", "terrain", "lodgen", pluginFolder);
        var main = Path.Combine(dir, name + ".dds");
        var txt = Path.Combine(dir, name + ".txt");
        if (File.Exists(main) && File.Exists(txt) && File.ReadAllText(txt).Contains("Source=" + source, StringComparison.Ordinal)) return true;

        var img = BillboardRenderer.Render(mesh, LoadTexture);
        if (img is null) { reason = "no visible pixels"; return false; }
        lock (_write)
        {
            Directory.CreateDirectory(dir);
            foreach (var file in new[] { main, Path.Combine(dir, name + "_1.dds") })
                using (var fs = File.Create(file)) BillboardRenderer.WriteDds(fs, img.Rgba, img.Width, img.Height);
            using (var fs = File.Create(Path.Combine(dir, name + "_1_n.dds"))) { if (img.NormalRgba is { } nrm) BillboardRenderer.WriteNormalDds(fs, nrm, img.Width, img.Height); else BillboardRenderer.WriteFlatNormalDds(fs, img.Width, img.Height); }
            var inv = CultureInfo.InvariantCulture;
            File.WriteAllText(txt, string.Join("\r\n",
                "[LOD]",
                string.Create(inv, $"Width={img.WorldWidth:F6}"),
                string.Create(inv, $"Height={img.WorldHeight:F6}"),
                "ShiftX=0.000000", "ShiftY=0.000000",
                string.Create(inv, $"ShiftZ={img.ShiftZ:F6}"),
                "Scale=1.000000",
                string.Create(inv, $"Width_1={img.WorldWidth:F6}"),
                string.Create(inv, $"Height_1={img.WorldHeight:F6}"),
                $"Model={model}",
                "Source=" + source,
                "Generator=AnvilLOD tree billboards (no TexGen)", ""));
        }
        return true;
    }

    private (byte[], int, int)? LoadTexture(string texture) => _textures.GetOrAdd(TexturePath(texture), p =>
    {
        if (!assets.TryOpen(p, out var st)) return null;
        byte[] bytes;
        using (st) { using var ms = new MemoryStream(); st.CopyTo(ms); bytes = ms.ToArray(); }
        try
        {
            var (rgba, w, h, _) = DdsFile.DecodeTopMip(bytes);
            while (w > 1024 && h > 4) (rgba, w, h) = TreeAtlasBuilder.Downsample(rgba, w, h);
            return (rgba, w, h);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException) { return null; }
    });

    private static string TexturePath(string t)
    {
        var p = GamePath.Normalize(t);
        return p.StartsWith("textures\\", StringComparison.Ordinal) ? p : "textures\\" + p;
    }
}
