using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>
/// Grass LOD from the grass cache (NGIO / FasterNGIO <c>grass\*.cgid</c>): every cached cell becomes one
/// reference in the LOD4 block, whose "mesh" is built on the fly from the cache: a sample of the cell's grass
/// as alpha-tested billboard quads, textured with the TexGen grass billboards
/// (<c>textures\terrain\lodgen\&lt;plugin&gt;\&lt;model&gt;_&lt;formid&gt;_1.dds</c>).
/// Because each cell is its own reference, the per-cell LOD4 segments hide the grass LOD where the real grass is loaded.
/// </summary>
public sealed class GrassLodSource : ISyntheticMeshSource
{
    public const string Prefix = "~anvillod-grass|";
    private const int Version = 3; // 2: crossed quads, coverage-compensated size, vertex colours; 3: rendered billboards

    private readonly AssetIndex _assets;
    private readonly float _density;
    private readonly float _sizeScale;
    private readonly RuntimeFormIds _runtime;
    private readonly Dictionary<FormKey, string> _grassModel = [];               // winning GRAS -> normalized model
    private readonly Dictionary<string, FormKey> _firstByModel = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Model, uint Id), GrassBillboard?> _billboards = new();
    private readonly ConcurrentDictionary<string, long> _missing = new(StringComparer.OrdinalIgnoreCase);
    private string? _billboardFingerprint;

    private readonly string? _outputFolder;
    private readonly object _renderLock = new();

    /// <param name="outputFolder">Where rendered grass billboards go (grass types TexGen made no billboard for). Null: use the model's own texture.</param>
    public GrassLodSource(GameContext game, AssetIndex assets, float density, float sizeScale, string? outputFolder = null)
    {
        _assets = assets;
        _outputFolder = outputFolder;
        _density = Math.Clamp(density, 0.001f, 1f);
        _sizeScale = sizeScale;
        _runtime = RuntimeFormIds.FromLoadOrder(game.LoadOrder);
        foreach (var g in game.LoadOrder.PriorityOrder.Grass().WinningOverrides())
        {
            var file = g.Model?.File;
            if (file is null || file.IsNull) continue;
            var model = NormalizeModel(file.DataRelativePath.Path);
            _grassModel[g.FormKey] = model;
            _firstByModel.TryAdd(model, g.FormKey);
        }
    }

    /// <summary>Grass types (plugin\model_formid) placed in rebuilt blocks that have no billboard, with instance counts.</summary>
    public IReadOnlyDictionary<string, long> MissingBillboards => _missing;

    public int CellsFound { get; private set; }

    /// <summary>One LOD reference per cached cell inside the worldspaces' LOD grids (non-seasonal caches only).</summary>
    public List<LodReference> Discover(IReadOnlyDictionary<string, LodGrid> grids)
    {
        var refs = new List<LodReference>();
        foreach (var path in _assets.EnumeratePaths("grass\\"))
        {
            var name = path["grass\\".Length..];
            if (name.Contains('\\') || !GrassCache.TryParseFileName(name, out var ws, out var x, out var y, out var season) || season is not null)
                continue;
            if (!grids.TryGetValue(ws, out var grid)) continue;
            var cell = new CellCoord(x, y);
            if (!grid.IsInsideGrid(cell) || !grid.Settings.Supports(LodLevel.Lod4)) continue;

            // Real grass from the cache is drawn out to the grass cell radius (often 10+ cells with NGIO), well into the
            // LOD8/LOD16 range, so grass LOD goes into all three near levels, sparser and bigger as the level grows.
            string Mesh(int level) => $"{Prefix}{level}|{path}";
            refs.Add(new LodReference(
                FormKey: $"~grass:{grid.Worldspace}:{x}:{y}",
                BaseFormKey: "grass",
                BaseEditorId: "Grass LOD",
                Worldspace: grid.Worldspace,
                WinningPlugin: "grass cache",
                Position: new Vector3(x * CellCoord.CellSize, y * CellCoord.CellSize, 0),
                RotationRadians: Vector3.Zero,
                Scale: 1f,
                Meshes: new LodMeshSet(Mesh(4), Mesh(8), Mesh(16), null),
                Flags: LodReferenceFlags.None));
        }
        CellsFound = refs.Count;
        return refs;
    }

    public bool Owns(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Input fingerprint of a grass patch: its cache file, every billboard, and the settings.</summary>
    public string? Fingerprint(string path)
    {
        if (!Owns(path)) return null;
        var (level, cache) = Split(path);
        return $"g{Version}|L{level}|{_assets.Fingerprint(cache)}|{BillboardFingerprint()}|{_density:R}|{_sizeScale:R}";
    }

    /// <summary>Fewer tufts further out; the builder widens them to keep the covered area.</summary>
    private static (float Density, float Size) LevelFactors(int level) => level switch
    {
        8 => (0.6f, 1f),
        16 => (0.35f, 1f),
        _ => (1f, 1f),
    };

    private static (int Level, string Cache) Split(string path)
    {
        var rest = path[Prefix.Length..];
        int bar = rest.IndexOf('|');
        return bar > 0 && int.TryParse(rest[..bar], out var level) ? (level, rest[(bar + 1)..]) : (4, rest);
    }

    public LodMesh? Build(string path)
    {
        var (level, cache) = Split(path);
        var (densityMul, sizeMul) = LevelFactors(level);
        if (!_assets.TryOpen(cache, out var st)) return null;
        byte[] bytes;
        using (st)
        {
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            bytes = ms.ToArray();
        }

        var types = GrassCache.Read(bytes);
        GrassCache.TryParseFileName(Path.GetFileName(cache), out _, out int x, out int y, out _);
        var origin = new Vector3(x * CellCoord.CellSize, y * CellCoord.CellSize, 0);

        var withBillboards = new List<(GrassBillboard, IReadOnlyList<GrassInstance>)>();
        foreach (var t in types)
        {
            if (t.Instances.Count == 0) continue;
            var bb = _billboards.GetOrAdd((NormalizeModel(t.Model), t.RuntimeFormId), k => FindBillboard(k.Model, k.Id));
            if (bb is null) continue;
            withBillboards.Add((bb, t.Instances));
        }
        return GrassPatchBuilder.Build(path, origin, withBillboards, Math.Min(1f, _density * densityMul), _sizeScale * sizeMul,
            seed: unchecked(x * 73856093 ^ y * 19349663 ^ level * 83492791));
    }

    private GrassBillboard? FindBillboard(string model, uint runtimeId)
    {
        // The cache stores the runtime FormID from when it was made; prefer the GRAS it points at today if the model
        // matches, otherwise any GRAS using the same model.
        FormKey? fk = _runtime.ToFormKey(runtimeId);
        if (fk is null || !_grassModel.TryGetValue(fk.Value, out var m) || m != model)
            fk = _firstByModel.TryGetValue(model, out var byModel) ? byModel : null;

        var stem = Path.GetFileNameWithoutExtension(model.Replace('\\', '/'));
        if (fk is { } key)
        {
            var folder = GamePath.Join("textures", "terrain", "lodgen", key.ModKey.FileName.String.ToLowerInvariant());
            var name = $"{stem}_{key.ID:x8}";
            foreach (var (dds, firstView) in new[] { (name + "_1.dds", true), (name + ".dds", false) })
            {
                var ddsPath = GamePath.Join(folder, dds);
                if (!_assets.Exists(ddsPath) || !_assets.TryOpen(GamePath.Join(folder, name + ".txt"), out var st)) continue;
                string text;
                using (var reader = new StreamReader(st)) text = reader.ReadToEnd();
                var tb = TreeBillboard.Parse(ddsPath, text, firstView);
                if (tb is null) continue;
                var normal = ddsPath[..^4] + "_n.dds";
                return new GrassBillboard(ddsPath, _assets.Exists(normal) ? normal : null, tb.Width, tb.Height, tb.ShiftZ);
            }
        }

        // No TexGen billboard (TexGen skips grass whose record has no bounds, e.g. Realistic Grass Field's main types):
        // use the grass model's own texture on the card, sized from the model.
        if (FromModel(model) is { } fallback)
        {
            Interlocked.Increment(ref _fromModel);
            return fallback;
        }
        _missing.AddOrUpdate(fk is { } k2 ? $"{k2.ModKey.FileName}\\{stem}_{k2.ID:x8}" : model, 1, (_, n) => n + 1);
        return null;
    }

    private int _fromModel;

    /// <summary>Grass types drawn with their own model texture because no billboard exists.</summary>
    public int TypesFromModelTexture => _fromModel;

    private GrassBillboard? FromModel(string model)
    {
        var path = GamePath.Join("meshes", model);
        if (!_assets.TryOpen(path, out var st)) return null;
        LodMesh mesh;
        using (st)
        {
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            try { mesh = AnvilLOD.Meshes.Nif.NifGeometryReader.Read(path, ms.ToArray()); }
            catch (Exception) { return null; }
        }
        if (mesh.Parts.Count == 0) return null;
        if (_outputFolder is not null && Rendered(model, mesh) is { } rendered) return rendered;
        var part = mesh.Parts.OrderByDescending(p => p.TriangleCount).FirstOrDefault();
        if (part is null) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in mesh.Parts)
            foreach (var v in p.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        float width = MathF.Max(max.X - min.X, max.Y - min.Y), height = max.Z - min.Z;
        if (height < 16f || width < 8f) return null; // tiny ground cover: nothing to see from afar
        var tex = part.Material.Textures;
        var diffuse = tex.Count > 0 ? tex[0] : "";
        if (string.IsNullOrWhiteSpace(diffuse)) return null;
        var normal = tex.Count > 1 && !string.IsNullOrWhiteSpace(tex[1]) ? tex[1] : null;
        return new GrassBillboard(diffuse, normal, width, height, min.Z);
    }

    /// <summary>
    /// Renders a billboard from the grass model (like TexGen would) into the output folder. The model's own texture
    /// is a strip of blades that thins out to nothing in the distant mipmaps; a rendered tuft with coverage-preserving
    /// mips stays as full from afar as it is up close.
    /// </summary>
    private GrassBillboard? Rendered(string model, LodMesh mesh)
    {
        lock (_renderLock)
        {
            var stem = Path.GetFileNameWithoutExtension(model.Replace('\\', '/')).ToLowerInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.ToLowerInvariant())), 0, 4).ToLowerInvariant();
            var rel = GamePath.Join("textures", "anvillod", "grass", $"{stem}_{hash}.dds");
            var img = Authoring.BillboardRenderer.Render(mesh, LoadTexture, maxPixels: 256, vertexColors: false, minCoverage: 0.3f);
            if (img is null) return null;
            var full = Path.Combine(_outputFolder!, rel.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            using (var fs = File.Create(full)) Authoring.BillboardRenderer.WriteDds(fs, img.Rgba, img.Width, img.Height);
            Interlocked.Increment(ref _rendered);
            return new GrassBillboard(rel, null, img.WorldWidth, img.WorldHeight, img.ShiftZ);
        }
    }

    private int _rendered;

    /// <summary>Grass types that got a billboard rendered from their model.</summary>
    public int BillboardsRendered => _rendered;

    private (byte[], int, int)? LoadTexture(string texture)
    {
        var p = GamePath.Normalize(texture);
        if (!p.StartsWith("textures\\", StringComparison.Ordinal)) p = "textures\\" + p;
        if (!_assets.TryOpen(p, out var st)) return null;
        byte[] bytes;
        using (st) { using var ms = new MemoryStream(); st.CopyTo(ms); bytes = ms.ToArray(); }
        try
        {
            var (rgba, w, h, _) = AnvilLOD.Textures.Dds.DdsFile.DecodeTopMip(bytes);
            while (w > 1024 && h > 4) (rgba, w, h) = AnvilLOD.Textures.TreeAtlasBuilder.Downsample(rgba, w, h);
            return (rgba, w, h);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>One fingerprint over every billboard and .txt in textures\terrain\lodgen (re-run TexGen = rebuild grass).</summary>
    private string BillboardFingerprint()
    {
        if (_billboardFingerprint is not null) return _billboardFingerprint;
        lock (_billboards)
        {
            if (_billboardFingerprint is not null) return _billboardFingerprint;
            var sb = new StringBuilder();
            foreach (var p in _assets.EnumeratePaths("textures\\terrain\\lodgen\\").Order(StringComparer.Ordinal))
                sb.Append(p).Append('=').Append(_assets.Fingerprint(p)).Append('\n');
            return _billboardFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())), 0, 8);
        }
    }

    private static string NormalizeModel(string path)
    {
        var p = GamePath.Normalize(path);
        return p.StartsWith("meshes\\", StringComparison.Ordinal) ? p["meshes\\".Length..] : p;
    }
}
