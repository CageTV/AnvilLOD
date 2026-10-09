using System.Collections.Concurrent;
using System.Numerics;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Meshes.Nif;

namespace AnvilLOD.Plugins;

/// <summary>What 3D tree LOD did in a run (reported in the log and the summary).</summary>
public sealed record Tree3DStats(
    int TreesIn3D,
    int ModelTypes,
    int TypesByCrc,
    int TypesByName,
    int TypesSkipped,
    long TreesSkipped,
    int NameOnlyAvailable,          // types whose only model is under the plain name, with the option off
    IReadOnlyList<string> Warnings,
    long Triangles = 0,             // triangles the 3D models add per LOD level that uses them (summed over every placed tree)
    double ApproxMegabytes = 0);    // rough size of that geometry in the blocks, per level

/// <summary>
/// 3D tree LOD models (DynDOLOD's <c>…passthru_lod.nif</c>) placed in object LOD: real crown geometry plus trunk
/// billboards in one mesh, drawn at LOD4 (and LOD8 when asked), with billboard cards from there on.
/// <para>
/// A model is read with its shaders untouched (that is what "passthru" means), brightened with the tree LOD brightness,
/// and, for shapes named <c>spherenormals</c>, given normals that point away from the centre of the model. A model whose
/// textures can't be found (TexGen hasn't rendered its trunk billboards yet) is not used: the trees keep billboard LOD.
/// </para>
/// </summary>
public sealed class Tree3DSource : ISyntheticMeshSource
{
    public const string Prefix = "~anvillod-tree3d|";
    private const int Version = 1;

    private readonly AssetIndex _assets;
    private readonly float _brightness;
    private readonly ConcurrentDictionary<string, Lazy<LodMesh?>> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LodMesh?> _built = new(StringComparer.OrdinalIgnoreCase);

    public Tree3DSource(AssetIndex assets, float brightness = 1f)
    {
        _assets = assets;
        _brightness = brightness;
    }

    public static string MeshPath(string modelPath) => Prefix + modelPath;

    public bool Owns(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    public string? Fingerprint(string path)
        => Owns(path) ? $"t{Version}|b{_brightness:F3}|{_assets.Fingerprint(path[Prefix.Length..])}" : null;

    public LodMesh? Build(string path) => _built.GetOrAdd(path, p => Process(p, Raw(p[Prefix.Length..])));

    private LodMesh? Raw(string modelPath) => _raw.GetOrAdd(modelPath, p => new Lazy<LodMesh?>(() => Load(p))).Value;

    private LodMesh? Load(string modelPath)
    {
        if (!_assets.TryOpen(modelPath, out var s)) return null;
        byte[] bytes;
        using (s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            bytes = ms.ToArray();
        }
        return NifGeometryReader.Read(modelPath, bytes, passthru: true);
    }

    /// <summary>
    /// Null when the model is fine to use, otherwise why not: unreadable, no shapes, or a shape whose diffuse texture
    /// doesn't exist (usually a trunk billboard TexGen hasn't made).
    /// </summary>
    public string? Validate(string modelPath)
    {
        LodMesh? mesh;
        try { mesh = Raw(modelPath); }
        catch (Exception ex) { return "could not be read: " + ex.Message; }
        if (mesh is null) return "could not be found";
        if (mesh.Parts.Count == 0)
            return mesh.Warnings.Count > 0 ? "no usable shapes (" + string.Join("; ", mesh.Warnings.Distinct().Take(2)) + ")" : "no usable shapes";
        foreach (var part in mesh.Parts)
        {
            var tex = part.Material.Textures.Count > 0 ? part.Material.Textures[0] : "";
            if (string.IsNullOrWhiteSpace(tex)) return "a shape has no diffuse texture";
            var path = tex.Replace('/', '\\').TrimStart('\\');
            if (!path.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase)) path = "textures\\" + path;
            if (!_assets.Exists(path)) return "missing texture " + path;
        }
        return null;
    }

    /// <summary>Vertex and triangle counts of a usable model (0, 0 when it can't be read).</summary>
    public (int Vertices, int Triangles) Size(string modelPath)
    {
        LodMesh? mesh;
        try { mesh = Raw(modelPath); } catch { return (0, 0); }
        return mesh is null ? (0, 0) : (mesh.Parts.Sum(p => p.VertexCount), mesh.Parts.Sum(p => p.TriangleCount));
    }

    /// <summary>Object LOD references for the trees: 3D model at LOD4 (and LOD8 if asked), billboard cards further out.</summary>
    public static IEnumerable<LodReference> ToReferences(IEnumerable<TreeReference> trees, Tree3DSettings settings)
    {
        foreach (var t in trees)
        {
            var model = MeshPath(t.Model3D!);
            var card = TreeCardSource.Prefix + t.Billboard.TexturePath;
            var near8 = settings.Lod8 ? model : card;
            // Trees copied in from a child worldspace (walled cities) only matter close up, as before.
            var meshes = t.ObjectLod ? new LodMeshSet(model, near8, null, null) : new LodMeshSet(model, near8, card, card);
            yield return new LodReference(
                FormKey: t.FormKey,
                BaseFormKey: "tree",
                BaseEditorId: "3D tree LOD",
                Worldspace: t.Worldspace,
                WinningPlugin: "tree LOD",
                Position: t.Position,
                RotationRadians: new Vector3(0, 0, t.RotationZ),
                Scale: t.Scale,
                Meshes: meshes,
                Flags: LodReferenceFlags.None);
        }
    }

    private LodMesh? Process(string path, LodMesh? raw)
    {
        if (raw is null || raw.Parts.Count == 0) return null;

        // The model's own centre, for spherenormals shapes.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var part in raw.Parts)
            foreach (var p in part.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var center = (min + max) * 0.5f;

        bool dim = MathF.Abs(_brightness - 1f) > 0.001f;
        var parts = new List<LodMeshPart>(raw.Parts.Count);
        foreach (var part in raw.Parts)
        {
            var normals = part.Normals;
            if (part.Name?.Contains("spherenormals", StringComparison.OrdinalIgnoreCase) == true)
            {
                normals = new Vector3[part.VertexCount];
                for (int i = 0; i < normals.Length; i++)
                    normals[i] = Transforms.SafeNormalize(part.Positions[i] - center, part.Normals[i]);
            }

            var colors = part.Colors;
            if (dim)
            {
                colors = new uint[part.VertexCount];
                for (int i = 0; i < colors.Length; i++) colors[i] = BtoBuilder.Scale(part.Colors?[i] ?? 0xFFFF_FFFFu, _brightness);
            }

            parts.Add(new LodMeshPart
            {
                Name = part.Name,
                Material = part.Material,
                Positions = part.Positions, UVs = part.UVs, Normals = normals,
                Tangents = part.Tangents, Bitangents = part.Bitangents,
                Colors = colors, Triangles = part.Triangles,
            });
        }
        return new LodMesh { Path = path, Parts = parts, Warnings = raw.Warnings };
    }
}
