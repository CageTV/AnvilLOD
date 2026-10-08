namespace AnvilLOD.Textures.Bc;

/// <summary>
/// Managed block-compression decoders (BC1, BC2, BC3, BC4, BC5, BC7) to RGBA8.
/// Each Decode* call writes one 4×4 block into <paramref name="rgba"/> (64 bytes, row-major RGBA).
/// </summary>
public static class BcDecoder
{
    /// <summary>Decodes a whole compressed surface into an RGBA8 image (width × height × 4 bytes).</summary>
    public static byte[] DecodeImage(ReadOnlySpan<byte> data, int width, int height, BcFormat format)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        int blockBytes = format.BlockBytes();
        var image = new byte[width * height * 4];
        Span<byte> block = stackalloc byte[64];

        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            var src = data.Slice((by * bw + bx) * blockBytes, blockBytes);
            DecodeBlock(src, block, format);
            for (int y = 0; y < 4; y++)
            {
                int py = by * 4 + y;
                if (py >= height) break;
                for (int x = 0; x < 4; x++)
                {
                    int px = bx * 4 + x;
                    if (px >= width) break;
                    block.Slice((y * 4 + x) * 4, 4).CopyTo(image.AsSpan((py * width + px) * 4, 4));
                }
            }
        }
        return image;
    }

    public static void DecodeBlock(ReadOnlySpan<byte> src, Span<byte> rgba, BcFormat format)
    {
        switch (format)
        {
            case BcFormat.Bc1: DecodeBc1(src, rgba, allowTransparent: true); break;
            case BcFormat.Bc2:
                DecodeBc1(src[8..], rgba, allowTransparent: false);
                for (int i = 0; i < 16; i++)
                {
                    int nibble = (src[i / 2] >> ((i & 1) * 4)) & 0xF;
                    rgba[i * 4 + 3] = (byte)(nibble * 17);
                }
                break;
            case BcFormat.Bc3:
                DecodeBc1(src[8..], rgba, allowTransparent: false);
                DecodeAlphaChannel(src, rgba, 3);
                break;
            case BcFormat.Bc4:
                DecodeAlphaChannel(src, rgba, 0);
                for (int i = 0; i < 16; i++) { rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4]; rgba[i * 4 + 3] = 255; }
                break;
            case BcFormat.Bc5:
                DecodeAlphaChannel(src, rgba, 0);
                DecodeAlphaChannel(src[8..], rgba, 1);
                for (int i = 0; i < 16; i++)
                {
                    // Reconstruct Z for normal maps.
                    float nx = rgba[i * 4] / 127.5f - 1f, ny = rgba[i * 4 + 1] / 127.5f - 1f;
                    float nz = MathF.Sqrt(MathF.Max(0, 1 - nx * nx - ny * ny));
                    rgba[i * 4 + 2] = (byte)Math.Clamp((int)MathF.Round((nz + 1f) * 127.5f), 0, 255);
                    rgba[i * 4 + 3] = 255;
                }
                break;
            case BcFormat.Bc7: Bc7Decoder.DecodeBlock(src, rgba); break;
            default: throw new NotSupportedException($"Cannot decode {format}.");
        }
    }

    private static void DecodeBc1(ReadOnlySpan<byte> src, Span<byte> rgba, bool allowTransparent)
    {
        int c0 = src[0] | (src[1] << 8);
        int c1 = src[2] | (src[3] << 8);
        Span<byte> pal = stackalloc byte[16];
        Expand565(c0, pal[..4]);
        Expand565(c1, pal.Slice(4, 4));
        if (c0 > c1 || !allowTransparent)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                pal[8 + ch] = (byte)((2 * pal[ch] + pal[4 + ch] + 1) / 3);
                pal[12 + ch] = (byte)((pal[ch] + 2 * pal[4 + ch] + 1) / 3);
            }
            pal[11] = pal[15] = 255;
        }
        else
        {
            for (int ch = 0; ch < 3; ch++)
            {
                pal[8 + ch] = (byte)((pal[ch] + pal[4 + ch]) / 2);
                pal[12 + ch] = 0;
            }
            pal[11] = 255;
            pal[15] = 0;
        }

        uint bits = (uint)(src[4] | (src[5] << 8) | (src[6] << 16) | (src[7] << 24));
        for (int i = 0; i < 16; i++)
        {
            int idx = (int)((bits >> (2 * i)) & 3);
            pal.Slice(idx * 4, 4).CopyTo(rgba.Slice(i * 4, 4));
        }
    }

    private static void Expand565(int c, Span<byte> dst)
    {
        int r = (c >> 11) & 31, g = (c >> 5) & 63, b = c & 31;
        dst[0] = (byte)((r << 3) | (r >> 2));
        dst[1] = (byte)((g << 2) | (g >> 4));
        dst[2] = (byte)((b << 3) | (b >> 2));
        dst[3] = 255;
    }

    /// <summary>BC3/BC4-style 8-byte single channel block into channel <paramref name="channel"/>.</summary>
    private static void DecodeAlphaChannel(ReadOnlySpan<byte> src, Span<byte> rgba, int channel)
    {
        int a0 = src[0], a1 = src[1];
        Span<int> pal = stackalloc int[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1 + 3) / 7;
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1 + 2) / 5;
            pal[6] = 0; pal[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)src[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            rgba[i * 4 + channel] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
    }
}

/// <summary>Block-compressed formats AnvilLOD reads and writes.</summary>
public enum BcFormat
{
    None,
    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc5,
    Bc7,
}

public static class BcFormatExtensions
{
    public static int BlockBytes(this BcFormat f) => f is BcFormat.Bc1 or BcFormat.Bc4 ? 8 : 16;
}
