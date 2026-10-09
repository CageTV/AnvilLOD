namespace AnvilLOD.Core.Lod;

/// <summary>
/// Finding a 3D tree LOD model for a TREE base record, the way DynDOLOD names them:
/// <c>meshes\dyndolod\lod\trees\&lt;model&gt;_&lt;CRC32 of the full model file&gt;passthru_lod.nif</c>.
/// The checksum ties the LOD model to the exact tree mesh it was made for, so a tree whose mesh was changed
/// afterwards (converted, retextured, PGPatcher output) no longer matches.
/// </summary>
public static class Tree3DModels
{
    public const string Folder = "meshes\\dyndolod\\lod\\trees\\";

    public enum MatchKind { None, Crc32, PlainName }

    public sealed record Match(string Path, MatchKind Kind);

    /// <summary>Standard CRC-32 (the one zlib and DynDOLOD's wbCRC32Data compute).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB8_8320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>The file name stem DynDOLOD uses for a model path: lower-case, no folder, no extension.</summary>
    public static string Stem(string modelPath)
    {
        var name = modelPath.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        int dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).ToLowerInvariant();
    }

    public static string CrcPath(string modelPath, uint crc) => $"{Folder}{Stem(modelPath)}_{crc:x8}passthru_lod.nif";

    public static string PlainPath(string modelPath) => $"{Folder}{Stem(modelPath)}passthru_lod.nif";

    /// <summary>
    /// The CRC32 match when that file exists; otherwise, only when <paramref name="plainNameFallback"/> is on, the model
    /// stored under the plain tree name (the form DynDOLOD Resources ships for most vanilla trees).
    /// </summary>
    public static Match? Resolve(string modelPath, uint crc, bool plainNameFallback, Func<string, bool> exists)
    {
        var withCrc = CrcPath(modelPath, crc);
        if (exists(withCrc)) return new Match(withCrc, MatchKind.Crc32);
        if (plainNameFallback)
        {
            var plain = PlainPath(modelPath);
            if (exists(plain)) return new Match(plain, MatchKind.PlainName);
        }
        return null;
    }
}
