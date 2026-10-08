using System.Numerics;
using AnvilLOD.Meshes;
using AnvilLOD.Textures;
using AnvilLOD.Textures.Bc;
using AnvilLOD.Textures.Dds;

namespace AnvilLOD.Plugins.Authoring;

/// <summary>A rendered tree billboard: RGBA image plus the world size it covers.</summary>
public sealed record BillboardImage(byte[] Rgba, int Width, int Height, float WorldWidth, float WorldHeight, float ShiftZ, float Coverage);

/// <summary>
/// Small software renderer for tree/plant billboards, the way TexGen makes them: the model seen from the front
/// (looking along +Y, Z up), orthographic, alpha-tested with its own textures, 2× supersampled.
/// </summary>
public static class BillboardRenderer
{
    public delegate (byte[] Rgba, int Width, int Height)? TextureLoader(string texturePath);

    /// <param name="vertexColors">Multiply by the model's vertex colours (trees: baked shading). Grass uses them for wind, so off there.</param>
    /// <param name="minCoverage">Thicken the cut-out until at least this share of the image is opaque (sparse grass blades
    /// otherwise vanish at a distance; 0 = leave as rendered).</param>
    public static BillboardImage? Render(LodMesh model, TextureLoader loadTexture, int maxPixels = 512, bool vertexColors = true, float minCoverage = 0f)
    {
        var (min, max) = Meshes.Authoring.LodMeshAuthor.Bounds(model);
        float worldW = max.X - min.X, worldH = max.Z - min.Z;
        if (!(worldW > 1f) || !(worldH > 1f)) return null;

        float scale = maxPixels / MathF.Max(worldW, worldH);
        int outW = Math.Max(16, Align4((int)MathF.Ceiling(worldW * scale)));
        int outH = Math.Max(16, Align4((int)MathF.Ceiling(worldH * scale)));
        int ss = 2, w = outW * ss, h = outH * ss;
        float sx = w / worldW, sz = h / worldH;

        var color = new byte[w * h * 4];
        var depth = new float[w * h];
        Array.Fill(depth, float.MaxValue);
        var light = Vector3.Normalize(new Vector3(0.3f, -1f, 0.8f));

        foreach (var part in model.Parts)
        {
            var tex = loadTexture(part.Material.Textures[0]);
            byte threshold = part.Material.HasAlpha ? (part.Material.AlphaThreshold == 0 ? (byte)128 : part.Material.AlphaThreshold) : (byte)0;
            var p = part.Positions;
            for (int t = 0; t < part.TriangleCount; t++)
            {
                int i0 = part.Triangles[t * 3], i1 = part.Triangles[t * 3 + 1], i2 = part.Triangles[t * 3 + 2];
                var a = Screen(p[i0]); var b = Screen(p[i1]); var c = Screen(p[i2]);
                float area = Edge(a, b, c.X, c.Y);
                if (MathF.Abs(area) < 1e-6f) continue; // both sides are drawn
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
                int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
                int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = Edge(b, c, px, py) / area, w1 = Edge(c, a, px, py) / area, w2 = 1f - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                    int idx = y * w + x;
                    if (z >= depth[idx]) continue;

                    var uv = part.UVs[i0] * w0 + part.UVs[i1] * w1 + part.UVs[i2] * w2;
                    var (r, g, bl, al) = Sample(tex, uv);
                    if (al < threshold) continue;
                    if (vertexColors && part.Colors is { } col)
                    {
                        var vc = Unpack(col[i0]) * w0 + Unpack(col[i1]) * w1 + Unpack(col[i2]) * w2;
                        r *= vc.X; g *= vc.Y; bl *= vc.Z;
                    }
                    var n = Vector3.Normalize(part.Normals[i0] * w0 + part.Normals[i1] * w1 + part.Normals[i2] * w2);
                    float shade = 0.7f + 0.3f * MathF.Abs(Vector3.Dot(float.IsFinite(n.X) ? n : -Vector3.UnitY, light));
                    depth[idx] = z;
                    int o = idx * 4;
                    color[o] = (byte)Math.Clamp(r * shade, 0, 255);
                    color[o + 1] = (byte)Math.Clamp(g * shade, 0, 255);
                    color[o + 2] = (byte)Math.Clamp(bl * shade, 0, 255);
                    color[o + 3] = 255;
                }
            }
        }

        // 2x2 down to the output size (alpha-weighted), then bleed colour into transparent texels for clean mips.
        var (img, iw, ih) = TreeAtlasBuilder.Downsample(color, w, h);
        for (int i = 3; i < img.Length; i += 4) img[i] = img[i] >= 96 ? (byte)255 : (byte)0; // crisp cut-out
        for (int pass = 0; pass < 6 && minCoverage > 0 && Coverage(img, 1f) < minCoverage; pass++) Thicken(img, iw, ih);
        Bleed(img, iw, ih, 6);
        int opaque = 0;
        for (int i = 3; i < img.Length; i += 4) if (img[i] >= 128) opaque++;
        if (opaque == 0) return null;
        return new BillboardImage(img, iw, ih, worldW, worldH, min.Z, opaque / (float)(iw * ih));

        Vector3 Screen(Vector3 v) => new((v.X - min.X) * sx, (max.Z - v.Z) * sz, v.Y);
    }

    /// <summary>Writes the image as a BC7 DDS with coverage-preserving mips (alpha-tested cutouts stay as thick as on top).</summary>
    public static void WriteDds(Stream output, byte[] rgba, int width, int height)
    {
        float coverage = Coverage(rgba, 1f);
        var mips = new List<byte[]> { Bc7Encoder.EncodeImage(rgba, width, height) };
        var (cur, w, h) = (rgba, width, height);
        while (w > 4 && h > 4)
        {
            (cur, w, h) = TreeAtlasBuilder.Downsample(cur, w, h);
            float lo = 0.5f, hi = 4f;
            for (int i = 0; i < 12; i++)
            {
                float mid = (lo + hi) / 2;
                if (Coverage(cur, mid) < coverage) lo = mid; else hi = mid;
            }
            float s = (lo + hi) / 2;
            for (int i = 3; i < cur.Length; i += 4) cur[i] = (byte)Math.Min(255f, cur[i] * s);
            mips.Add(Bc7Encoder.EncodeImage(cur, w, h));
        }
        DdsFile.WriteBc7(output, width, height, mips);
    }

    /// <summary>A flat normal map (facing the camera) of the given size, as BC7.</summary>
    public static void WriteFlatNormalDds(Stream output, int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 128; px[i + 1] = 128; px[i + 2] = 255; px[i + 3] = 255; }
        var mips = new List<byte[]>();
        var (cur, w, h) = (px, width, height);
        mips.Add(Bc7Encoder.EncodeImage(cur, w, h));
        while (w > 4 && h > 4)
        {
            (cur, w, h) = TreeAtlasBuilder.Downsample(cur, w, h);
            mips.Add(Bc7Encoder.EncodeImage(cur, w, h));
        }
        DdsFile.WriteBc7(output, width, height, mips);
    }

    private static float Coverage(byte[] rgba, float scale)
    {
        int n = 0, total = rgba.Length / 4;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] * scale >= 128f) n++;
        return n / (float)total;
    }

    /// <summary>Grows the opaque area by one pixel (colour from the opaque neighbours).</summary>
    private static void Thicken(byte[] img, int w, int h)
    {
        var src = (byte[])img.Clone();
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            if (src[i + 3] >= 128) continue;
            int r = 0, g = 0, b = 0, n = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                int j = (ny * w + nx) * 4;
                if (src[j + 3] < 128) continue;
                r += src[j]; g += src[j + 1]; b += src[j + 2]; n++;
            }
            if (n == 0) continue;
            img[i] = (byte)(r / n); img[i + 1] = (byte)(g / n); img[i + 2] = (byte)(b / n); img[i + 3] = 255;
        }
    }

    private static void Bleed(byte[] img, int w, int h, int passes)
    {
        var filled = new bool[w * h];
        for (int i = 0; i < filled.Length; i++) filled[i] = img[i * 4 + 3] > 0;
        for (int pass = 0; pass < passes; pass++)
        {
            var next = (bool[])filled.Clone();
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (filled[i]) continue;
                int r = 0, g = 0, b = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || !filled[ny * w + nx]) continue;
                    int j = (ny * w + nx) * 4;
                    r += img[j]; g += img[j + 1]; b += img[j + 2]; n++;
                }
                if (n == 0) continue;
                img[i * 4] = (byte)(r / n); img[i * 4 + 1] = (byte)(g / n); img[i * 4 + 2] = (byte)(b / n);
                next[i] = true;
            }
            filled = next;
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float px, float py) => (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X);

    private static Vector3 Unpack(uint c) => new((c & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f);

    private static (float R, float G, float B, float A) Sample((byte[] Rgba, int Width, int Height)? tex, Vector2 uv)
    {
        if (tex is not { } t) return (128, 128, 128, 255);
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        float fx = u * t.Width - 0.5f, fy = v * t.Height - 0.5f;
        int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
        float ax = fx - x0, ay = fy - y0;
        float r = 0, g = 0, b = 0, a = 0;
        for (int k = 0; k < 4; k++)
        {
            int dx = k & 1, dy = k >> 1;
            float wgt = (dx == 0 ? 1 - ax : ax) * (dy == 0 ? 1 - ay : ay);
            int px = ((x0 + dx) % t.Width + t.Width) % t.Width, py = ((y0 + dy) % t.Height + t.Height) % t.Height;
            int i = (py * t.Width + px) * 4;
            r += t.Rgba[i] * wgt; g += t.Rgba[i + 1] * wgt; b += t.Rgba[i + 2] * wgt; a += t.Rgba[i + 3] * wgt;
        }
        return (r, g, b, a);
    }

    private static int Align4(int v) => (v + 3) & ~3;
}
