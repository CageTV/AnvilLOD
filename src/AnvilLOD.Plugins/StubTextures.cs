namespace AnvilLOD.Plugins;

/// <summary>
/// A LOD mesh can name a texture that exists nowhere (a creek LOD from a water mod whose texture was never shipped). The engine then
/// ends up with a null texture and some builds crash in the lighting shader instead of drawing purple. Where nothing exists at the
/// path, a 4x4 stand-in is written there into the output: a plain colour picked from the name for a diffuse, a flat normal map for a
/// normal map. Nothing that exists is ever replaced.
/// </summary>
public static class StubTextures
{
    /// <summary>Writes the stand-in; returns the colour used (RGB), or null for a normal map.</summary>
    public static (byte R, byte G, byte B)? Write(string outputFolder, string texturePath)
    {
        var rel = texturePath.Replace('/', '\\').TrimStart('\\');
        var full = Path.Combine(outputFolder, rel.Replace('\\', Path.DirectorySeparatorChar));
        bool normal = rel.EndsWith("_n.dds", StringComparison.OrdinalIgnoreCase);
        var color = normal ? (R: (byte)128, G: (byte)128, B: (byte)255) : ColorFor(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Build(color.R, color.G, color.B));
        return normal ? null : color;
    }

    public static (byte R, byte G, byte B) ColorFor(string texturePath)
    {
        var n = texturePath.ToLowerInvariant();
        if (n.Contains("snow") || n.Contains("ice")) return (220, 225, 230);
        if (n.Contains("blue") || n.Contains("water") || n.Contains("creek") || n.Contains("river") || n.Contains("stream") || n.Contains("pond") || n.Contains("lake"))
            return (70, 98, 110);
        if (n.Contains("grass") || n.Contains("moss") || n.Contains("leaf")) return (100, 112, 70);
        return (110, 105, 95);
    }

    /// <summary>An uncompressed 32-bit BGRA DDS, 4x4, no mip chain: loads everywhere Skyrim SE does.</summary>
    public static byte[] Build(byte r, byte g, byte b)
    {
        var data = new byte[128 + 4 * 4 * 4];
        "DDS "u8.CopyTo(data);
        void U(int off, uint v) => BitConverter.GetBytes(v).CopyTo(data, off);
        U(4, 124);
        U(8, 0x1007 | 0x8);            // caps, height, width, pixel format, pitch
        U(12, 4); U(16, 4);            // height, width
        U(20, 4 * 4);                  // pitch
        U(28, 1);                      // mip count
        U(76, 32);                     // pixel format size
        U(80, 0x1 | 0x40);             // alpha pixels | rgb
        U(88, 32);                     // bit count
        U(92, 0x00FF0000); U(96, 0x0000FF00); U(100, 0x000000FF); U(104, 0xFF000000);
        U(108, 0x1000);                // caps: texture
        for (int i = 0; i < 16; i++)
        {
            int o = 128 + i * 4;
            data[o] = b; data[o + 1] = g; data[o + 2] = r; data[o + 3] = 255;
        }
        return data;
    }
}
