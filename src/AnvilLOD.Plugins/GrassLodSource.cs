using System.Collections.Concurrent;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AnvilLOD.Core.Pipeline;
using AnvilLOD.Core.World;
using AnvilLOD.Meshes;
using AnvilLOD.Textures;
using AnvilLOD.Textures.Bc;
using AnvilLOD.Textures.Dds;
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
    private const int Version = 5; // 2: crossed quads, coverage-compensated size, vertex colours; 3: rendered billboards; 4: binned sampling (one tuft per bin); 5: atlas, brightness top/bottom

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
    private readonly float _top, _bottom;

    /// <param name="outputFolder">Where rendered grass billboards go (grass types TexGen made no billboard for). Null: use the model's own texture.</param>
    /// <param name="top">Vertex-colour brightness at the top of a tuft (share of white), like DynDOLOD's GrassBrightnessTop.</param>
    /// <param name="bottom">Vertex-colour brightness at the roots, like DynDOLOD's GrassBrightnessBottom.</param>
    public GrassLodSource(GameContext game, AssetIndex assets, float density, float sizeScale, string? outputFolder = null,
        float top = GrassPatchBuilder.DefaultTop, float bottom = GrassPatchBuilder.DefaultBottom)
    {
        _assets = assets;
        _outputFolder = outputFolder;
        _top = top;
        _bottom = bottom;
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

    private readonly Dictionary<(string Ws, int X, int Y, string Season), string> _seasonal = [];

    /// <summary>True if the grass cache has files for this season ("WIN", "SPR", "SUM", "AUT").</summary>
    public bool HasSeason(string suffix) => _seasonal.Keys.Any(k => k.Season == suffix);

    /// <summary>
    /// The same grass reference, but built from the season's cache file (Seasons of Skyrim's seasonal LOD). Null if the
    /// reference isn't a grass cell or that season has no cache file for the cell (the normal grass stays).
    /// </summary>
    public LodReference? SeasonalVariant(LodReference r, string suffix)
    {
        if (!r.FormKey.StartsWith("~grass:", StringComparison.Ordinal)) return null;
        var parts = r.FormKey.Split(':');
        if (parts.Length != 4 || !int.TryParse(parts[2], out var x) || !int.TryParse(parts[3], out var y)) return null;
        if (!_seasonal.TryGetValue((parts[1].ToLowerInvariant(), x, y, suffix), out var cache)) return null;
        string Mesh(int level) => $"{Prefix}{level}|{cache}";
        return r with { Meshes = new LodMeshSet(Mesh(4), Mesh(8), Mesh(16), null) };
    }

    /// <summary>One LOD reference per cached cell inside the worldspaces' LOD grids (non-seasonal caches only).</summary>
    public List<LodReference> Discover(IReadOnlyDictionary<string, LodGrid> grids)
    {
        var refs = new List<LodReference>();
        foreach (var path in _assets.EnumeratePaths("grass\\"))
        {
            var name = path["grass\\".Length..];
            if (name.Contains('\\') || !GrassCache.TryParseFileName(name, out var ws, out var x, out var y, out var season))
                continue;
            if (season is not null)
            {
                _seasonal[(ws.ToLowerInvariant(), x, y, season)] = path; // ...x0005y-005.WIN.cgid: the grass of that season
                continue;
            }
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
        return $"g{Version}|L{level}|{_assets.Fingerprint(cache)}|{BillboardFingerprint()}|{_density:R}|{_sizeScale:R}|{_top:R}|{_bottom:R}|{_atlasHash}";
    }

    /// <summary>Fewer tufts further out; the builder picks larger bins (and so bigger tufts) to keep the covered area.</summary>
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
            var bb = _billboards.GetOrAdd((NormalizeModel(t.Model), t.RuntimeFormId), k => Atlased(FindBillboard(k.Model, k.Id)));
            if (bb is null) continue;
            withBillboards.Add((bb, t.Instances));
        }
        return GrassPatchBuilder.Build(path, origin, withBillboards, Math.Min(1f, _density * densityMul), _sizeScale * sizeMul, _top, _bottom);
    }

    private GrassBillboard? FindBillboard(string model, uint runtimeId)
    {
        // The cache stores the runtime FormID from when it was made; prefer the GRAS it points at today if the model
        // matches, otherwise any GRAS using the same model.
        FormKey? fk = _runtime.ToFormKey(runtimeId);
        if (fk is null || !_grassModel.TryGetValue(fk.Value, out var m) || m != model)
            fk = _firstByModel.TryGetValue(model, out var byModel) ? byModel : null;
        return Resolve(model, fk, record: true);
    }

    /// <param name="record">Count a type with no billboard as missing (false while the atlas looks at every GRAS, placed or not).</param>
    private GrassBillboard? Resolve(string model, FormKey? fk, bool record)
    {
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
        if (_fromModelCache.GetOrAdd(model, m => FromModel(m)) is { } fallback) return fallback;
        if (record) _missing.AddOrUpdate(fk is { } k2 ? $"{k2.ModKey.FileName}\\{stem}_{k2.ID:x8}" : model, 1, (_, n) => n + 1);
        return null;
    }

    private int _fromModel;
    private readonly ConcurrentDictionary<string, GrassBillboard?> _fromModelCache = new(StringComparer.Ordinal);

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
        Interlocked.Increment(ref _fromModel);
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

    // ----- grass atlas: every grass billboard in one texture pair, so grass needs one material (draw call) per block -----

    /// <summary>The atlas textures, as the LOD meshes name them (written into the output folder).</summary>
    public const string AtlasDiffuse = "textures\\anvillod\\grass\\atlas.dds", AtlasNormal = "textures\\anvillod\\grass\\atlas_n.dds";

    private Dictionary<string, AtlasRect>? _atlasRects;
    private string _atlasHash = "-";

    /// <summary>What the atlas holds, for the log (null until <see cref="PrepareAtlas"/> has built one).</summary>
    public GrassAtlasSummary? Atlas { get; private set; }

    /// <summary>
    /// Packs the billboards of every grass type (TexGen's, or rendered from the model) into <see cref="AtlasDiffuse"/> and a
    /// normal map atlas with the same layout (<see cref="AtlasNormal"/>), tiles capped at 256 px. Must run before blocks are
    /// built: the grass meshes take their UVs from it. Without an output folder (a scan) nothing is built.
    /// </summary>
    public void PrepareAtlas()
    {
        if (_outputFolder is null || _atlasRects is not null) return;
        var raw = new SortedDictionary<string, GrassBillboard>(StringComparer.Ordinal);
        foreach (var (fk, model) in _grassModel)
            if (Resolve(model, fk, record: false) is { } bb) raw.TryAdd(GamePath.Normalize(bb.Diffuse), bb);
        var diffuse = new List<TreeAtlasBuilder.Input>();
        var normal = new List<TreeAtlasBuilder.Input>();
        var flat = new Dictionary<(int, int), byte[]>();
        foreach (var (key, bb) in raw)
        {
            var d = ReadTexture(bb.Diffuse);
            if (d is null) continue;
            DdsFile.Info info;
            try { info = DdsFile.ReadInfo(d); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { continue; }
            // The normal tile must have the diffuse tile's size (the two atlases share one layout); otherwise it is flat.
            var n = bb.Normal is null ? null : ReadTexture(bb.Normal);
            if (n is not null)
            {
                try { var ni = DdsFile.ReadInfo(n); if (ni.Width != info.Width || ni.Height != info.Height) n = null; }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException) { n = null; }
            }
            if (n is null)
            {
                if (!flat.TryGetValue((info.Width, info.Height), out n)) flat[(info.Width, info.Height)] = n = FlatNormalDds(info.Width, info.Height);
            }
            diffuse.Add(new TreeAtlasBuilder.Input(key, d));
            normal.Add(new TreeAtlasBuilder.Input(key, n));
        }
        if (diffuse.Count == 0) return;

        var builder = new TreeAtlasBuilder { MaxSize = 4096, MaxTile = 256 };
        var a = builder.Build(diffuse);
        var b = builder.Build(normal);
        if (a.Size != b.Size || b.Errors.Count > 0 || a.Rects.Count != b.Rects.Count
            || a.Rects.Any(kv => !b.Rects.TryGetValue(kv.Key, out var r) || r != kv.Value))
            return; // the pair doesn't line up (shouldn't happen): leave every grass type on its own textures

        foreach (var (rel, bytes) in new[] { (AtlasDiffuse, a.Dds), (AtlasNormal, b.Dds) })
        {
            var full = Path.Combine(_outputFolder, rel.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        _atlasRects = new Dictionary<string, AtlasRect>(a.Rects, StringComparer.OrdinalIgnoreCase);
        _atlasHash = Convert.ToHexString(SHA256.HashData(a.Dds), 0, 6).ToLowerInvariant();
        Atlas = new GrassAtlasSummary(a.Rects.Count, a.Size, a.SizeCap, a.Reduced, a.Errors.Count);
    }

    /// <summary>The billboard moved into the atlas (same textures for every type, UVs from its tile), if it has a tile.</summary>
    private GrassBillboard? Atlased(GrassBillboard? bb)
        => bb is not null && _atlasRects is not null && _atlasRects.TryGetValue(GamePath.Normalize(bb.Diffuse), out var rect)
            ? new GrassBillboard(AtlasDiffuse, AtlasNormal, bb.Width, bb.Height, bb.ShiftZ, rect)
            : bb;

    /// <summary>A texture from the output folder (billboards rendered this run) or the load order.</summary>
    private byte[]? ReadTexture(string texture)
    {
        var p = GamePath.Normalize(texture);
        if (!p.StartsWith("textures\\", StringComparison.Ordinal)) p = "textures\\" + p;
        if (_outputFolder is not null)
        {
            var local = Path.Combine(_outputFolder, p.Replace('\\', Path.DirectorySeparatorChar));
            if (File.Exists(local)) return File.ReadAllBytes(local);
        }
        if (!_assets.TryOpen(p, out var st)) return null;
        using (st)
        {
            using var ms = new MemoryStream();
            st.CopyTo(ms);
            return ms.ToArray();
        }
    }

    /// <summary>A BC7 DDS of the given size filled with the flat tangent-space normal (128,128,255), one mip.</summary>
    private static byte[] FlatNormalDds(int width, int height)
    {
        var px = new byte[64];
        for (int i = 0; i < 64; i += 4) { px[i] = 128; px[i + 1] = 128; px[i + 2] = 255; px[i + 3] = 255; }
        var block = new byte[16];
        Bc7Encoder.EncodeBlock(px, block);
        int blocks = ((width + 3) / 4) * ((height + 3) / 4);
        var top = new byte[blocks * 16];
        for (int i = 0; i < top.Length; i += 16) block.CopyTo(top, i);
        using var ms = new MemoryStream();
        DdsFile.WriteBc7(ms, width, height, [top]);
        return ms.ToArray();
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

/// <summary>The grass atlas for the log: tiles, side length in pixels, the tile size cap that was needed, tiles scaled down, billboards that couldn't be read.</summary>
public sealed record GrassAtlasSummary(int Tiles, int Size, int SizeCap, int Reduced, int Skipped);
