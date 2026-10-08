using System.Buffers.Binary;
using AnvilLOD.Textures.Bc;

namespace AnvilLOD.Textures.Dds;

/// <summary>
/// Minimal DDS reader/writer: 2D textures, BC1/2/3/4/5/7 and 32-bit uncompressed, legacy or DX10 headers.
/// Reading decodes the top mip to RGBA8; writing produces BC7 (DX10 header) with a full mip chain.
/// </summary>
public static class DdsFile
{
    private const uint Magic = 0x20534444; // "DDS "
    private const int HeaderSize = 124;

    public sealed record Info(int Width, int Height, int MipCount, BcFormat Format, bool Uncompressed, bool Bgra, int DataOffset, int DxgiFormat);

    public static Info ReadInfo(ReadOnlySpan<byte> file)
    {
        if (file.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(file) != Magic)
            throw new InvalidDataException("Not a DDS file.");
        int height = BinaryPrimitives.ReadInt32LittleEndian(file[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(file[16..]);
        int mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(file[28..]));

        // DDS_PIXELFORMAT at offset 76
        uint pfFlags = BinaryPrimitives.ReadUInt32LittleEndian(file[80..]);
        uint fourCC = BinaryPrimitives.ReadUInt32LittleEndian(file[84..]);
        int bitCount = BinaryPrimitives.ReadInt32LittleEndian(file[88..]);
        uint rMask = BinaryPrimitives.ReadUInt32LittleEndian(file[92..]);

        int offset = 4 + HeaderSize;
        string cc = System.Text.Encoding.ASCII.GetString(file.Slice(84, 4));
        if ((pfFlags & 0x4) != 0) // DDPF_FOURCC
        {
            switch (cc)
            {
                case "DXT1": return new(width, height, mips, BcFormat.Bc1, false, false, offset, 71);
                case "DXT2" or "DXT3": return new(width, height, mips, BcFormat.Bc2, false, false, offset, 74);
                case "DXT4" or "DXT5": return new(width, height, mips, BcFormat.Bc3, false, false, offset, 77);
                case "ATI1" or "BC4U": return new(width, height, mips, BcFormat.Bc4, false, false, offset, 80);
                case "ATI2" or "BC5U": return new(width, height, mips, BcFormat.Bc5, false, false, offset, 83);
                case "DX10":
                    int dxgi = BinaryPrimitives.ReadInt32LittleEndian(file[128..]);
                    offset += 20;
                    return dxgi switch
                    {
                        70 or 71 or 72 => new(width, height, mips, BcFormat.Bc1, false, false, offset, dxgi),
                        73 or 74 or 75 => new(width, height, mips, BcFormat.Bc2, false, false, offset, dxgi),
                        76 or 77 or 78 => new(width, height, mips, BcFormat.Bc3, false, false, offset, dxgi),
                        79 or 80 or 81 => new(width, height, mips, BcFormat.Bc4, false, false, offset, dxgi),
                        82 or 83 or 84 => new(width, height, mips, BcFormat.Bc5, false, false, offset, dxgi),
                        97 or 98 or 99 => new(width, height, mips, BcFormat.Bc7, false, false, offset, dxgi),
                        27 or 28 or 29 => new(width, height, mips, BcFormat.None, true, false, offset, dxgi),
                        87 or 88 or 90 or 91 => new(width, height, mips, BcFormat.None, true, true, offset, dxgi),
                        _ => throw new NotSupportedException($"DXGI format {dxgi} is not supported."),
                    };
                default: throw new NotSupportedException($"DDS FourCC '{cc}' is not supported.");
            }
        }
        if (bitCount == 32)
            return new(width, height, mips, BcFormat.None, true, rMask == 0x00FF0000, offset, rMask == 0x00FF0000 ? 87 : 28);
        throw new NotSupportedException($"DDS pixel format ({bitCount}-bit, flags 0x{pfFlags:X}) is not supported.");
    }

    /// <summary>Decodes the top mip level to RGBA8.</summary>
    public static (byte[] Rgba, int Width, int Height, Info Info) DecodeTopMip(byte[] file)
    {
        var info = ReadInfo(file);
        var data = file.AsSpan(info.DataOffset);
        if (info.Uncompressed)
        {
            var rgba = data[..(info.Width * info.Height * 4)].ToArray();
            if (info.Bgra)
                for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            if (info.DxgiFormat == 88) // BGRX: no alpha
                for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            return (rgba, info.Width, info.Height, info);
        }
        return (BcDecoder.DecodeImage(data, info.Width, info.Height, info.Format), info.Width, info.Height, info);
    }

    /// <summary>The raw blocks of the top mip of a block-compressed file.</summary>
    public static ReadOnlySpan<byte> TopMipBlocks(byte[] file, Info info)
    {
        int bytes = ((info.Width + 3) / 4) * ((info.Height + 3) / 4) * info.Format.BlockBytes();
        return file.AsSpan(info.DataOffset, bytes);
    }

    /// <summary>Writes a BC7 DDS (DX10 header). <paramref name="mips"/> are the compressed levels, largest first.</summary>
    public static void WriteBc7(Stream output, int width, int height, IReadOnlyList<byte[]> mips, bool srgb = false)
    {
        Span<byte> h = stackalloc byte[4 + HeaderSize + 20];
        h.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(h, Magic);
        var hdr = h[4..];
        BinaryPrimitives.WriteInt32LittleEndian(hdr, HeaderSize);
        // DDSD_CAPS | HEIGHT | WIDTH | PIXELFORMAT | MIPMAPCOUNT | LINEARSIZE
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[4..], 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[8..], height);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[12..], width);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[16..], mips[0].Length);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[20..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[24..], mips.Count);
        // pixel format at 72
        BinaryPrimitives.WriteInt32LittleEndian(hdr[72..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[76..], 0x4); // DDPF_FOURCC
        "DX10"u8.CopyTo(hdr[80..]);
        // caps: COMPLEX | TEXTURE | MIPMAP
        BinaryPrimitives.WriteUInt32LittleEndian(hdr[104..], 0x8 | 0x1000 | 0x400000);
        var dx10 = h[(4 + HeaderSize)..];
        BinaryPrimitives.WriteInt32LittleEndian(dx10, srgb ? 99 : 98);
        BinaryPrimitives.WriteInt32LittleEndian(dx10[4..], 3); // TEXTURE2D
        BinaryPrimitives.WriteInt32LittleEndian(dx10[8..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(dx10[12..], 1); // array size
        BinaryPrimitives.WriteInt32LittleEndian(dx10[16..], 0);
        output.Write(h);
        foreach (var m in mips) output.Write(m);
    }
}
