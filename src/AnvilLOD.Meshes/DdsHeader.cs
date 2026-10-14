namespace AnvilLOD.Meshes;

/// <summary>
/// Reads just enough of a DDS header to tell whether Skyrim SE can load the texture. A texture the engine cannot create (a format it
/// has no support for, a zero size, a truncated file) leaves a null texture behind and the game crashes in the shader that uses it
/// instead of drawing purple.
/// </summary>
public static class DdsHeader
{
    /// <summary>Null when the header looks loadable, otherwise what is wrong with it.</summary>
    public static string? Problem(ReadOnlySpan<byte> head, long fileLength)
    {
        if (head.Length < 128 || fileLength < 128) return "file is shorter than a DDS header";
        if (head[0] != 'D' || head[1] != 'D' || head[2] != 'S' || head[3] != ' ') return "not a DDS file (bad magic)";
        uint size = BitConverter.ToUInt32(head[4..]);
        if (size != 124) return $"bad DDS header size {size}";
        uint height = BitConverter.ToUInt32(head[12..]), width = BitConverter.ToUInt32(head[16..]);
        if (width == 0 || height == 0) return $"zero size ({width}x{height})";
        if (width > 16384 || height > 16384) return $"size {width}x{height} is too large";
        uint pfFlags = BitConverter.ToUInt32(head[80..]);
        var fourCc = System.Text.Encoding.ASCII.GetString(head.Slice(84, 4));
        if ((pfFlags & 0x4) != 0)   // DDPF_FOURCC
        {
            switch (fourCc)
            {
                case "DXT1": case "DXT2": case "DXT3": case "DXT4": case "DXT5": case "ATI1": case "ATI2": case "BC4U": case "BC5U": return null;
                case "DX10":
                    if (head.Length < 148 || fileLength < 148) return "DX10 header is truncated";
                    uint dxgi = BitConverter.ToUInt32(head[128..]);
                    uint arraySize = BitConverter.ToUInt32(head[140..]);
                    if (!SupportedDxgi(dxgi)) return $"DXGI format {dxgi} is not one Skyrim SE loads";
                    if (arraySize > 1) return $"texture array of {arraySize}";
                    return null;
                default: return $"unsupported FourCC '{fourCc}'";
            }
        }
        // Uncompressed: needs a bit count and masks.
        uint bits = BitConverter.ToUInt32(head[88..]);
        if (bits is not (8 or 16 or 24 or 32)) return $"unsupported uncompressed format ({bits} bits)";
        return null;
    }

    private static bool SupportedDxgi(uint f) => f is 28 or 29 or 61 or 71 or 72 or 74 or 75 or 77 or 78 or 80 or 81 or 83 or 84 or 87 or 88 or 91 or 93 or 98 or 99 or 56 or 85 or 86;
}
