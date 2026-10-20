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

    /// <param name="brightness">Multiplier on top of <see cref="BaseScale"/> (1 = DynDOLOD's default).</param>
    /// <param name="maxSize">Largest side kept; bigger textures are halved until they fit.</param>
    /// <returns>A BC7 DDS with a full mip chain.</returns>
    public static byte[] Convert(byte[] dds, int maxSize, float brightness)
    {
        var (rgba, w, h, _) = DdsFile.DecodeTopMip(dds);
        float coverage = Coverage(rgba, 1f);
        while (Math.Max(w, h) > maxSize && Math.Min(w, h) > 4)
        {
            (rgba, w, h) = TreeAtlasBuilder.Downsample(rgba, w, h);
            KeepCoverage(rgba, coverage);
        }

        var lut = new byte[256];
        for (int i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp(MathF.Round(255f * MathF.Pow(i / 255f, Gamma) * BaseScale * brightness), 0, 255);
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = lut[rgba[i]];
            rgba[i + 1] = lut[rgba[i + 1]];
            rgba[i + 2] = lut[rgba[i + 2]];
        }

        var mips = new List<byte[]> { Bc7Encoder.EncodeImage(rgba, w, h) };
        int mw = w, mh = h;
        var level = rgba;
        while (mw > 4 || mh > 4)
        {
            (level, mw, mh) = TreeAtlasBuilder.Downsample(level, mw, mh);
            KeepCoverage(level, coverage);
            mips.Add(Bc7Encoder.EncodeImage(level, mw, mh));
        }
        using var ms = new MemoryStream();
        DdsFile.WriteBc7(ms, w, h, mips);
        return ms.ToArray();
    }

    private static float Coverage(byte[] rgba, float scale)
    {
        int n = 0;
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] * scale >= 128f) n++;
        return n / (float)(rgba.Length / 4);
    }

    /// <summary>Scales alpha so the share of texels passing the alpha test matches the top mip (leaves and needles would thin out to nothing).</summary>
    private static void KeepCoverage(byte[] rgba, float target)
    {
        if (target <= 0f || target >= 1f) return;
        float lo = 0.25f, hi = 8f;
        for (int i = 0; i < 12; i++)
        {
            float mid = (lo + hi) / 2;
            if (Coverage(rgba, mid) < target) lo = mid; else hi = mid;
        }
        if (MathF.Abs(hi - 1f) < 0.01f) return;
        for (int i = 3; i < rgba.Length; i += 4) rgba[i] = (byte)Math.Min(255, MathF.Round(rgba[i] * hi));
    }
}
