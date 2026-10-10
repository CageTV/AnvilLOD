using AnvilLOD.Core.Pipeline;
using AnvilLOD.Textures.Bc;
using AnvilLOD.Textures.Dds;

namespace AnvilLOD.Textures;

/// <summary>
/// Packs tree billboards (TexGen / LODGen <c>.dds</c> files) into one BC7 atlas with a full mip chain,
/// the texture the engine uses for <c>.btt</c> tree LOD (<c>textures\terrain\&lt;ws&gt;\trees\&lt;ws&gt;treelod.dds</c>).
/// <list type="bullet">
/// <item>Billboards that already are BC7 are copied block-for-block into the top mip (no quality loss).</item>
/// <item>Lower mips are filtered with alpha-weighted averaging and keep each billboard's alpha-test coverage,
/// so distant trees don't thin out.</item>
/// <item>If everything doesn't fit at <see cref="MaxSize"/>, only the largest billboards are halved (a size cap that
/// drops until everything fits), so small trees keep their full resolution.</item>
/// </list>
/// </summary>
public sealed class TreeAtlasBuilder
{
    public int MaxSize { get; init; } = 4096;

    /// <summary>Largest side any billboard keeps, whatever the atlas size (grass LOD: its billboards are huge, 2048x1024, and are seen small).</summary>
    public int MaxTile { get; init; } = int.MaxValue;
    public byte AlphaThreshold { get; init; } = 128;

    /// <summary>Colour multiplier for every billboard (1 = unchanged), like DynDOLOD's tree LOD brightness.</summary>
    public float Brightness { get; init; } = 1f;
    private const int Gutter = 4;

    public sealed record Input(string Key, byte[] DdsFile);

    /// <param name="SizeCap">Largest billboard side kept (int.MaxValue = nothing reduced).</param>
    /// <param name="Reduced">How many billboards were scaled down to fit.</param>
    public sealed record Output(byte[] Dds, IReadOnlyDictionary<string, AtlasRect> Rects, int Size, int SizeCap, int Reduced,
        IReadOnlyDictionary<string, string> Errors);

    private sealed class Item
    {
        public required string Key;
        public required byte[] File;
        public required DdsFile.Info Info;
        public int W, H, X, Y, Factor = 1;
        public byte[]? Rgba;
    }

    public Output Build(IReadOnlyList<Input> inputs, CancellationToken ct = default)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var items = new List<Item>();
        foreach (var inp in inputs)
        {
            try
            {
                var info = DdsFile.ReadInfo(inp.DdsFile);
                items.Add(new Item { Key = inp.Key, File = inp.DdsFile, Info = info, W = info.Width, H = info.Height });
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            {
                errors[inp.Key] = ex.Message;
            }
        }

        // Smallest square atlas that fits with every billboard at full size; otherwise lower a cap on the
        // largest side until it fits, so only the biggest billboards lose resolution.
        int size = 0, cap = int.MaxValue;
        foreach (int c in new[] { MaxTile, 2048, 1024, 768, 512, 384, 256, 192, 128, 96, 64 }.Distinct())
        {
            if (c > MaxTile) continue;
            for (int s = 256; s <= MaxSize && size == 0; s *= 2)
                if (Pack(items, s, c, apply: false)) { size = s; cap = c; }
            if (size != 0) break;
        }
        if (size == 0)
            throw new InvalidOperationException($"{items.Count} tree billboards don't fit into a {MaxSize}×{MaxSize} atlas even at 64 px.");
        Pack(items, size, cap, apply: true);
        int reduced = items.Count(i => i.Factor > 1);

        // Decode (and scale) every billboard to RGBA.
        Parallel.ForEach(items, new ParallelOptions { CancellationToken = ct }, it =>
        {
            var (rgba, w, h, _) = DdsFile.DecodeTopMip(it.File);
            for (int f = it.Factor; f > 1; f /= 2) (rgba, w, h) = Downsample(rgba, w, h);
            if (MathF.Abs(Brightness - 1f) > 0.001f)
                for (int i = 0; i < rgba.Length; i += 4)
                    for (int c = 0; c < 3; c++)
                        rgba[i + c] = (byte)Math.Clamp((int)MathF.Round(rgba[i + c] * Brightness), 0, 255);
            it.Rgba = rgba;
            it.W = w; it.H = h;
        });

        // Compose the top level.
        var atlas = new byte[size * size * 4];
        foreach (var it in items)
            for (int y = 0; y < it.H; y++)
                it.Rgba.AsSpan(y * it.W * 4, it.W * 4).CopyTo(atlas.AsSpan(((it.Y + y) * size + it.X) * 4));

        // Top mip: BC7 sources at full size are copied block-for-block; everything else is encoded.
        int blocksPerRow = size / 4;
        var top = new byte[blocksPerRow * blocksPerRow * 16];
        var empty = new byte[16];
        Bc7Encoder.EncodeBlock(new byte[64], empty);
        for (int i = 0; i < top.Length; i += 16) empty.CopyTo(top, i);
        Parallel.ForEach(items, new ParallelOptions { CancellationToken = ct }, it =>
        {
            int bw = (it.W + 3) / 4, bh = (it.H + 3) / 4;
            byte[] blocks;
            if (it.Factor == 1 && it.Info.Format == BcFormat.Bc7 && MathF.Abs(Brightness - 1f) <= 0.001f)
                blocks = DdsFile.TopMipBlocks(it.File, it.Info).ToArray();
            else
                blocks = Bc7Encoder.EncodeImage(it.Rgba!, it.W, it.H);
            for (int by = 0; by < bh; by++)
                Buffer.BlockCopy(blocks, by * bw * 16, top, ((it.Y / 4 + by) * blocksPerRow + it.X / 4) * 16, bw * 16);
        });

        var mips = new List<byte[]> { top };
        var coverage = items.ToDictionary(it => it, it => Coverage(atlas, size, it.X, it.Y, it.W, it.H, 1f));
        var level = atlas;
        int levelSize = size;
        while (levelSize > 4)
        {
            ct.ThrowIfCancellationRequested();
            (level, levelSize, _) = Downsample(level, levelSize, levelSize);
            int shift = mips.Count;
            var output = (byte[])level.Clone();
            foreach (var it in items)
            {
                int x0 = it.X >> shift, y0 = it.Y >> shift;
                int x1 = Math.Max(x0 + 1, (it.X + it.W + (1 << shift) - 1) >> shift);
                int y1 = Math.Max(y0 + 1, (it.Y + it.H + (1 << shift) - 1) >> shift);
                KeepCoverage(output, levelSize, x0, y0, x1 - x0, y1 - y0, coverage[it]);
            }
            mips.Add(Bc7Encoder.EncodeImage(output, levelSize, levelSize));
        }

        using var ms = new MemoryStream();
        DdsFile.WriteBc7(ms, size, size, mips);

        var rects = items.ToDictionary(
            it => it.Key,
            it => new AtlasRect((float)it.X / size, (float)it.Y / size, (float)(it.X + it.W) / size, (float)(it.Y + it.H) / size),
            StringComparer.Ordinal);
        return new Output(ms.ToArray(), rects, size, cap, reduced, errors);
    }

    /// <summary>Shelf packing, tallest first, positions on 4-pixel (block) boundaries with a gutter.</summary>
    private static bool Pack(List<Item> items, int size, int cap, bool apply)
    {
        static int FactorFor(Item i, int cap)
        {
            int f = 1;
            while (Math.Max(i.Info.Width, i.Info.Height) / f > cap) f *= 2;
            return f;
        }
        var order = items.Select(i => (Item: i, F: FactorFor(i, cap)))
            .OrderByDescending(t => Scaled(t.Item.Info.Height, t.F)).ThenByDescending(t => Scaled(t.Item.Info.Width, t.F)).ToList();
        int x = 0, y = 0, rowH = 0;
        foreach (var (it, f) in order)
        {
            int w = Align4(Scaled(it.Info.Width, f)), h = Align4(Scaled(it.Info.Height, f));
            if (w > size || h > size) return false;
            if (x + w > size)
            {
                x = 0;
                y += rowH + Gutter;
                rowH = 0;
            }
            if (y + h > size) return false;
            if (apply) { it.X = x; it.Y = y; it.Factor = f; }
            x += w + Gutter;
            rowH = Math.Max(rowH, h);
        }
        return true;

        static int Scaled(int v, int f) => Math.Max(4, v / f);
        static int Align4(int v) => (v + 3) & ~3;
    }

    /// <summary>2×2 box filter; colours are alpha-weighted so transparent texels don't darken edges.</summary>
    public static (byte[] Rgba, int W, int H) Downsample(byte[] src, int w, int h)
    {
        int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
        var dst = new byte[nw * nh * 4];
        Parallel.For(0, nh, y =>
        {
            for (int x = 0; x < nw; x++)
            {
                float r = 0, g = 0, b = 0, a = 0, pr = 0, pg = 0, pb = 0;
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                {
                    int sx = Math.Min(x * 2 + dx, w - 1), sy = Math.Min(y * 2 + dy, h - 1);
                    int i = (sy * w + sx) * 4;
                    float al = src[i + 3];
                    r += src[i] * al; g += src[i + 1] * al; b += src[i + 2] * al; a += al;
                    pr += src[i]; pg += src[i + 1]; pb += src[i + 2];
                }
                int o = (y * nw + x) * 4;
                if (a > 0)
                {
                    dst[o] = (byte)MathF.Round(r / a);
                    dst[o + 1] = (byte)MathF.Round(g / a);
                    dst[o + 2] = (byte)MathF.Round(b / a);
                }
                else
                {
                    dst[o] = (byte)MathF.Round(pr / 4);
                    dst[o + 1] = (byte)MathF.Round(pg / 4);
                    dst[o + 2] = (byte)MathF.Round(pb / 4);
                }
                dst[o + 3] = (byte)MathF.Round(a / 4);
            }
        });
        return (dst, nw, nh);
    }

    private float Coverage(byte[] img, int stride, int x0, int y0, int w, int h, float scale)
    {
        int count = 0, total = 0;
        for (int y = y0; y < y0 + h && y < stride; y++)
            for (int x = x0; x < x0 + w && x < stride; x++)
            {
                total++;
                if (img[(y * stride + x) * 4 + 3] * scale >= AlphaThreshold) count++;
            }
        return total == 0 ? 0 : (float)count / total;
    }

    /// <summary>Scales alpha inside a rectangle so the share of texels passing the alpha test matches the top mip.</summary>
    private void KeepCoverage(byte[] img, int stride, int x0, int y0, int w, int h, float target)
    {
        if (target <= 0) return;
        float lo = 0.25f, hi = 8f;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) / 2;
            if (Coverage(img, stride, x0, y0, w, h, mid) < target) lo = mid; else hi = mid;
        }
        float s = hi;
        if (MathF.Abs(s - 1f) < 0.01f) return;
        for (int y = y0; y < y0 + h && y < stride; y++)
            for (int x = x0; x < x0 + w && x < stride; x++)
            {
                int i = (y * stride + x) * 4 + 3;
                img[i] = (byte)Math.Min(255, MathF.Round(img[i] * s));
            }
    }
}
