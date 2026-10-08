namespace AnvilLOD.Textures.Bc;

/// <summary>Full BC7 decoder (all eight modes), following the D3D11 specification.</summary>
public static class Bc7Decoder
{
    private readonly record struct ModeInfo(int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits,
        int ColorBits, int AlphaBits, int EndpointPBits, int SharedPBits, int IndexBits, int IndexBits2);

    private static readonly ModeInfo[] Modes =
    [
        new(3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
        new(2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
        new(3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
        new(2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
        new(1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
        new(1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
        new(1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
        new(2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
    ];

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public int Read(int count)
        {
            int v = 0;
            for (int i = 0; i < count; i++, _pos++)
                v |= ((_data[_pos >> 3] >> (_pos & 7)) & 1) << i;
            return v;
        }
    }

    public static void DecodeBlock(ReadOnlySpan<byte> src, Span<byte> rgba)
    {
        int mode = 0;
        while (mode < 8 && (src[0] & (1 << mode)) == 0) mode++;
        if (mode == 8)
        {
            rgba[..64].Clear(); // reserved mode: transparent black
            return;
        }

        var m = Modes[mode];
        var br = new BitReader(src);
        br.Read(mode + 1);
        int partition = br.Read(m.PartitionBits);
        int rotation = br.Read(m.RotationBits);
        int indexSelection = br.Read(m.IndexSelectionBits);

        int ns = m.Subsets;
        Span<int> ep = stackalloc int[3 * 2 * 4]; // [subset][endpoint][channel]
        for (int ch = 0; ch < 3; ch++)
            for (int s = 0; s < ns; s++)
                for (int e = 0; e < 2; e++)
                    ep[(s * 2 + e) * 4 + ch] = br.Read(m.ColorBits);
        if (m.AlphaBits > 0)
            for (int s = 0; s < ns; s++)
                for (int e = 0; e < 2; e++)
                    ep[(s * 2 + e) * 4 + 3] = br.Read(m.AlphaBits);

        Span<int> pbits = stackalloc int[6];
        if (m.EndpointPBits > 0)
            for (int i = 0; i < ns * 2; i++) pbits[i] = br.Read(1);
        else if (m.SharedPBits > 0)
            for (int s = 0; s < ns; s++) pbits[s * 2] = pbits[s * 2 + 1] = br.Read(1);

        bool hasP = m.EndpointPBits > 0 || m.SharedPBits > 0;
        for (int i = 0; i < ns * 2; i++)
        {
            for (int ch = 0; ch < 4; ch++)
            {
                int bits = ch < 3 ? m.ColorBits : m.AlphaBits;
                if (bits == 0) { ep[i * 4 + ch] = 255; continue; }
                int v = ep[i * 4 + ch];
                if (hasP) { v = (v << 1) | pbits[i]; bits++; }
                v <<= 8 - bits;
                v |= v >> bits;
                ep[i * 4 + ch] = v;
            }
        }

        // Which subset each pixel belongs to, and the anchor pixels (whose index has one bit less).
        Span<int> subsetOf = stackalloc int[16];
        int anchor1 = 0, anchor2 = 0;
        if (ns == 2)
        {
            for (int i = 0; i < 16; i++) subsetOf[i] = Bc7Tables.Partition2[partition * 16 + i];
            anchor1 = Bc7Tables.Anchor2[partition];
        }
        else if (ns == 3)
        {
            for (int i = 0; i < 16; i++) subsetOf[i] = Bc7Tables.Partition3[partition * 16 + i];
            anchor1 = Bc7Tables.Anchor3A[partition];
            anchor2 = Bc7Tables.Anchor3B[partition];
        }

        bool IsAnchor(int i) => i == 0 || (ns >= 2 && i == anchor1) || (ns == 3 && i == anchor2);

        Span<int> idx1 = stackalloc int[16];
        Span<int> idx2 = stackalloc int[16];
        for (int i = 0; i < 16; i++) idx1[i] = br.Read(IsAnchor(i) ? m.IndexBits - 1 : m.IndexBits);
        if (m.IndexBits2 > 0)
            for (int i = 0; i < 16; i++) idx2[i] = br.Read(i == 0 ? m.IndexBits2 - 1 : m.IndexBits2);

        for (int i = 0; i < 16; i++)
        {
            int s = subsetOf[i];
            int colorIndex, alphaIndex, colorBits, alphaBits;
            if (m.IndexBits2 == 0)
            {
                colorIndex = alphaIndex = idx1[i];
                colorBits = alphaBits = m.IndexBits;
            }
            else if (indexSelection == 0)
            {
                colorIndex = idx1[i]; colorBits = m.IndexBits;
                alphaIndex = idx2[i]; alphaBits = m.IndexBits2;
            }
            else
            {
                colorIndex = idx2[i]; colorBits = m.IndexBits2;
                alphaIndex = idx1[i]; alphaBits = m.IndexBits;
            }

            int wc = Weight(colorBits, colorIndex), wa = Weight(alphaBits, alphaIndex);
            int e0 = s * 2 * 4, e1 = (s * 2 + 1) * 4;
            byte r = Lerp(ep[e0], ep[e1], wc);
            byte g = Lerp(ep[e0 + 1], ep[e1 + 1], wc);
            byte b = Lerp(ep[e0 + 2], ep[e1 + 2], wc);
            byte a = Lerp(ep[e0 + 3], ep[e1 + 3], wa);
            switch (rotation)
            {
                case 1: (r, a) = (a, r); break;
                case 2: (g, a) = (a, g); break;
                case 3: (b, a) = (a, b); break;
            }
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
        }
    }

    private static int Weight(int bits, int index) => bits switch
    {
        2 => Bc7Tables.Weights2[index],
        3 => Bc7Tables.Weights3[index],
        _ => Bc7Tables.Weights4[index],
    };

    private static byte Lerp(int a, int b, int w) => (byte)(((64 - w) * a + w * b + 32) >> 6);
}
