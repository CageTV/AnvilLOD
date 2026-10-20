using AnvilLOD.Textures.Bc;
using AnvilLOD.Textures.Dds;

namespace AnvilLOD.Textures;

/// <summary>
/// Turns a PBR albedo (sRGB, made for a shader that works in linear light) into a texture for the vanilla LOD shader, which
/// lights in gamma space. The curve and the base scale are fitted to what TexGen writes for a flat PBR texture: the colours
/// are pulled down along a gentle power curve and scaled by 0.65 (DynDOLOD's default PBR scale), times the user's brightness.
/// It is an approximation: tune the brightness in game. Large textures are shrunk (LOD never shows them at full size).
/// </summary>
public static class PbrLodConverter
{
    public const float Gamma = 1.15f;
    public const float BaseScale = 0.65f;

    /// <summary>Alpha-test reference of the LOD shader (the same 128 the grass and tree patches use).</summary>
    public const byte AlphaThreshold = 128;

    /// <summary>Bump when the mip chain changes, so converted copies written by an older build are made again.</summary>
    public const int MipVersion = 2;

    /// <param name="brightness">Multiplier on top of <see cref="BaseScale"/> (1 = DynDOLOD's default).</param>
    /// <param name="maxSize">Largest side kept; bigger textures are halved until they fit.</param>
    /// <returns>A BC7 DDS with a full mip chain.</returns>
    public static byte[] Convert(byte[] dds, int maxSize, float brightness)
    {
        var (rgba, w, h, _) = DdsFile.DecodeTopMip(dds);
        while (Math.Max(w, h) > maxSize && Math.Min(w, h) > 4)
            (rgba, w, h) = TreeAtlasBuilder.Downsample(rgba, w, h);

        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp(MathF.Round(255f * MathF.Pow(i / 255f, Gamma) * BaseScale * brightness), 0, 255);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = lut[rgba[i]];
            rgba[i + 1] = lut[rgba[i + 1]];
            rgba[i + 2] = lut[rgba[i + 2]];
        }

        var mips = MipChain(rgba, w, h).Select(m => Bc7Encoder.EncodeImage(m.Rgba, m.W, m.H)).ToList();
        using var ms = new MemoryStream();
        DdsFile.WriteBc7(ms, w, h, mips);
        return ms.ToArray();
    }

    /// <summary>
    /// The mip levels down to 4 texels, top level first. A box filter averages the alpha of a sparse leaf card away: the share of texels
    /// that pass the alpha test falls to nothing a few levels down and the crown of a distant tree disappears, leaving the trunk. Each
    /// level's alpha is scaled so the share passing the test matches the top level, as the vanilla tree textures are authored.
    /// </summary>
    public static List<(byte[] Rgba, int W, int H)> MipChain(byte[] rgba, int w, int h)
    {
        var levels = new List<(byte[] Rgba, int W, int H)> { (rgba, w, h) };
        float target = Coverage(rgba, 1f);
        var level = rgba;
        int mw = w, mh = h;
        while (mw > 4 || mh > 4)
        {
            (level, mw, mh) = TreeAtlasBuilder.Downsample(level, mw, mh);   // the next level is filtered from the unadjusted one
            var shown = level;
            if (target is > 0f and < 0.999f)
            {
                shown = (byte[])level.Clone();
                KeepCoverage(shown, target);
            }
            levels.Add((shown, mw, mh));
        }
        return levels;
    }

    /// <summary>Share of texels whose alpha, times <paramref name="scale"/>, passes the alpha test.</summary>
    private static float Coverage(byte[] rgba, float scale)
    {
        int count = 0, total = rgba.Length / 4;
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] * scale >= AlphaThreshold) count++;
        return total == 0 ? 0f : (float)count / total;
    }

    private static void KeepCoverage(byte[] rgba, float target)
    {
        float lo = 0.25f, hi = 8f;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) / 2;
            if (Coverage(rgba, mid) < target) lo = mid; else hi = mid;
        }
        if (MathF.Abs(hi - 1f) < 0.01f) return;
        for (int i = 3; i < rgba.Length; i += 4)
            rgba[i] = (byte)Math.Min(255, MathF.Round(rgba[i] * hi));
    }
}
