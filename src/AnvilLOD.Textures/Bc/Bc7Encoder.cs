namespace AnvilLOD.Textures.Bc;

/// <summary>
/// Small, fast BC7 encoder using mode 6 only (one subset, RGBA 7.7.7.7 + p-bit endpoints, 4-bit indices).
/// Endpoints come from the principal axis, then one least-squares refinement pass.
/// Good enough for LOD atlases and billboards; a GPU encoder replaces it in milestone 2.
/// </summary>
public static class Bc7Encoder
{
    /// <summary>Encodes an RGBA8 image (dimensions padded up to multiples of 4 by edge clamping).</summary>
    public static byte[] EncodeImage(ReadOnlySpan<byte> rgba, int width, int height) =>
        EncodeImage(rgba.ToArray(), width, height);

    public static byte[] EncodeImage(byte[] rgba, int width, int height)
    {
        int bw = (width + 3) / 4, bh = (height + 3) / 4;
        var output = new byte[bw * bh * 16];
        Parallel.For(0, bh, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < bw; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int py = Math.Min(by * 4 + y, height - 1);
                    for (int x = 0; x < 4; x++)
                    {
                        int px = Math.Min(bx * 4 + x, width - 1);
                        rgba.AsSpan((py * width + px) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4, 4));
                    }
                }
                EncodeBlock(block, output.AsSpan((by * bw + bx) * 16, 16));
            }
        });
        return output;
    }

    public static void EncodeBlock(ReadOnlySpan<byte> rgba, Span<byte> dst)
    {
        Span<float> px = stackalloc float[64];
        for (int i = 0; i < 64; i++) px[i] = rgba[i];

        // Fully transparent texels: their colour is invisible, so give them the block's visible average.
        // That keeps the block on one colour line (mode 6 has a single subset) and pads edges for filtering.
        int visible = 0;
        float vr = 0, vg = 0, vb = 0;
        for (int i = 0; i < 16; i++)
            if (rgba[i * 4 + 3] > 0) { visible++; vr += px[i * 4]; vg += px[i * 4 + 1]; vb += px[i * 4 + 2]; }
        if (visible is > 0 and < 16)
            for (int i = 0; i < 16; i++)
                if (rgba[i * 4 + 3] == 0)
                {
                    px[i * 4] = MathF.Round(vr / visible); px[i * 4 + 1] = MathF.Round(vg / visible); px[i * 4 + 2] = MathF.Round(vb / visible);
                }

        EncodeMode6(px, dst);

        // Mode 6 puts colour and alpha on one line. Blocks at alpha-tested edges (opaque leaves next to
        // transparent gaps) usually do better with mode 5, which has separate colour and alpha indices.
        bool alphaVaries = false;
        for (int i = 1; i < 16; i++) if (rgba[i * 4 + 3] != rgba[3]) { alphaVaries = true; break; }
        if (!alphaVaries) return;
        Span<byte> alt = stackalloc byte[16];
        EncodeMode5(px, alt);
        if (BlockError(rgba, alt) < BlockError(rgba, dst)) alt.CopyTo(dst);
    }

    /// <summary>Error that ignores the colour of fully transparent texels and weights colour by alpha.</summary>
    private static float BlockError(ReadOnlySpan<byte> original, ReadOnlySpan<byte> block)
    {
        Span<byte> dec = stackalloc byte[64];
        Bc7Decoder.DecodeBlock(block, dec);
        float err = 0;
        for (int i = 0; i < 16; i++)
        {
            float da = original[i * 4 + 3] - dec[i * 4 + 3];
            err += da * da;
            float w = original[i * 4 + 3] / 255f;
            for (int c = 0; c < 3; c++)
            {
                float d = original[i * 4 + c] - dec[i * 4 + c];
                err += w * d * d;
            }
        }
        return err;
    }

    private static void EncodeMode6(ReadOnlySpan<float> px, Span<byte> dst)
    {
        // Solid block: exact endpoints, all indices 0.
        bool solid = true;
        for (int i = 1; i < 16 && solid; i++)
            for (int c = 0; c < 4; c++)
                if (px[i * 4 + c] != px[c]) { solid = false; break; }

        Span<float> e0 = stackalloc float[4];
        Span<float> e1 = stackalloc float[4];
        if (solid)
        {
            for (int c = 0; c < 4; c++) e0[c] = e1[c] = px[c];
        }
        else
        {
            PrincipalEndpoints(px, e0, e1);
        }

        Span<int> q0 = stackalloc int[4];
        Span<int> q1 = stackalloc int[4];
        Span<int> idx = stackalloc int[16];
        int p0, p1;

        Quantize(e0, q0, out p0);
        Quantize(e1, q1, out p1);
        float err = AssignIndices(px, q0, p0, q1, p1, idx);

        if (!solid)
        {
            // Least-squares refit of the endpoints for the chosen indices, keep it if it helps.
            Span<float> r0 = stackalloc float[4];
            Span<float> r1 = stackalloc float[4];
            if (Refit(px, idx, r0, r1))
            {
                Span<int> rq0 = stackalloc int[4];
                Span<int> rq1 = stackalloc int[4];
                Span<int> ridx = stackalloc int[16];
                Quantize(r0, rq0, out int rp0);
                Quantize(r1, rq1, out int rp1);
                float rerr = AssignIndices(px, rq0, rp0, rq1, rp1, ridx);
                if (rerr < err)
                {
                    rq0.CopyTo(q0); rq1.CopyTo(q1); ridx.CopyTo(idx);
                    p0 = rp0; p1 = rp1;
                }
            }
        }

        // The anchor (pixel 0) index is stored with 3 bits, so its top bit must be 0.
        if (idx[0] >= 8)
        {
            for (int c = 0; c < 4; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
            (p0, p1) = (p1, p0);
            for (int i = 0; i < 16; i++) idx[i] = 15 - idx[i];
        }

        var w = new BitWriter(dst);
        w.Write(1 << 6, 7);                      // mode 6
        for (int c = 0; c < 4; c++)
        {
            w.Write(q0[c], 7);
            w.Write(q1[c], 7);
        }
        w.Write(p0, 1);
        w.Write(p1, 1);
        w.Write(idx[0], 3);
        for (int i = 1; i < 16; i++) w.Write(idx[i], 4);
    }

    /// <summary>Mode 5: one subset, RGB 7-bit + alpha 8-bit endpoints, separate 2-bit colour and alpha indices.</summary>
    private static void EncodeMode5(ReadOnlySpan<float> px, Span<byte> dst)
    {
        // Colour line from the principal axis of RGB only.
        Span<float> rgb = stackalloc float[64];
        for (int i = 0; i < 16; i++) { rgb[i * 4] = px[i * 4]; rgb[i * 4 + 1] = px[i * 4 + 1]; rgb[i * 4 + 2] = px[i * 4 + 2]; rgb[i * 4 + 3] = 0; }
        Span<float> e0 = stackalloc float[4];
        Span<float> e1 = stackalloc float[4];
        PrincipalEndpoints(rgb, e0, e1);

        Span<int> q0 = stackalloc int[3];
        Span<int> q1 = stackalloc int[3];
        Span<int> ci = stackalloc int[16];
        for (int c = 0; c < 3; c++) { q0[c] = Q7(e0[c]); q1[c] = Q7(e1[c]); }
        float err = ColourIndices2(rgb, q0, q1, ci);

        // Least-squares refit for the chosen indices.
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[3];
        Span<float> bx = stackalloc float[3];
        for (int i = 0; i < 16; i++)
        {
            float wt = Bc7Tables.Weights2[ci[i]] / 64f, a = 1 - wt;
            aa += a * a; ab += a * wt; bb += wt * wt;
            for (int c = 0; c < 3; c++) { ax[c] += a * rgb[i * 4 + c]; bx[c] += wt * rgb[i * 4 + c]; }
        }
        float det = aa * bb - ab * ab;
        if (MathF.Abs(det) > 1e-6f)
        {
            Span<int> r0 = stackalloc int[3];
            Span<int> r1 = stackalloc int[3];
            Span<int> rci = stackalloc int[16];
            for (int c = 0; c < 3; c++)
            {
                r0[c] = Q7((bb * ax[c] - ab * bx[c]) / det);
                r1[c] = Q7((aa * bx[c] - ab * ax[c]) / det);
            }
            float rerr = ColourIndices2(rgb, r0, r1, rci);
            if (rerr < err) { r0.CopyTo(q0); r1.CopyTo(q1); rci.CopyTo(ci); }
        }

        // Alpha: exact 8-bit endpoints at min/max, nearest of four levels.
        int amin = 255, amax = 0;
        for (int i = 0; i < 16; i++) { int a = (int)px[i * 4 + 3]; amin = Math.Min(amin, a); amax = Math.Max(amax, a); }
        int a0 = amin, a1 = amax;
        Span<int> ai = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int best = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int wt = Bc7Tables.Weights2[k];
                int v = ((64 - wt) * a0 + wt * a1 + 32) >> 6;
                int d = Math.Abs(v - (int)px[i * 4 + 3]);
                if (d < best) { best = d; ai[i] = k; }
            }
        }

        // Anchor (pixel 0) indices are stored with one bit less.
        if (ci[0] >= 2)
        {
            for (int c = 0; c < 3; c++) (q0[c], q1[c]) = (q1[c], q0[c]);
            for (int i = 0; i < 16; i++) ci[i] = 3 - ci[i];
        }
        if (ai[0] >= 2)
        {
            (a0, a1) = (a1, a0);
            for (int i = 0; i < 16; i++) ai[i] = 3 - ai[i];
        }

        var w = new BitWriter(dst);
        w.Write(1 << 5, 6);   // mode 5
        w.Write(0, 2);        // no rotation
        for (int c = 0; c < 3; c++) { w.Write(q0[c], 7); w.Write(q1[c], 7); }
        w.Write(a0, 8);
        w.Write(a1, 8);
        w.Write(ci[0], 1);
        for (int i = 1; i < 16; i++) w.Write(ci[i], 2);
        w.Write(ai[0], 1);
        for (int i = 1; i < 16; i++) w.Write(ai[i], 2);
    }

    private static int Q7(float v) => Math.Clamp((int)MathF.Round(Math.Clamp(v, 0, 255) * 127f / 255f), 0, 127);
    private static int E7(int q) => (q << 1) | (q >> 6);

    private static float ColourIndices2(ReadOnlySpan<float> rgb, ReadOnlySpan<int> q0, ReadOnlySpan<int> q1, Span<int> idx)
    {
        Span<int> pal = stackalloc int[12];
        for (int k = 0; k < 4; k++)
        {
            int wt = Bc7Tables.Weights2[k];
            for (int c = 0; c < 3; c++) pal[k * 3 + c] = ((64 - wt) * E7(q0[c]) + wt * E7(q1[c]) + 32) >> 6;
        }
        float total = 0;
        for (int i = 0; i < 16; i++)
        {
            float best = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float d = 0;
                for (int c = 0; c < 3; c++) { float t = rgb[i * 4 + c] - pal[k * 3 + c]; d += t * t; }
                if (d < best) { best = d; idx[i] = k; }
            }
            total += best;
        }
        return total;
    }

    private static void PrincipalEndpoints(ReadOnlySpan<float> px, Span<float> e0, Span<float> e1)
    {
        Span<float> mean = stackalloc float[4];
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < 4; c++) mean[c] += px[i * 4 + c] / 16f;

        Span<float> cov = stackalloc float[16];
        for (int i = 0; i < 16; i++)
            for (int a = 0; a < 4; a++)
            {
                float da = px[i * 4 + a] - mean[a];
                for (int b = 0; b < 4; b++) cov[a * 4 + b] += da * (px[i * 4 + b] - mean[b]);
            }

        // Power iteration for the dominant axis.
        Span<float> axis = stackalloc float[4] { 1, 1, 1, 1 };
        Span<float> tmp = stackalloc float[4];
        for (int it = 0; it < 8; it++)
        {
            float len = 0;
            for (int a = 0; a < 4; a++)
            {
                float s = 0;
                for (int b = 0; b < 4; b++) s += cov[a * 4 + b] * axis[b];
                tmp[a] = s;
                len += s * s;
            }
            if (len < 1e-12f) break;
            len = MathF.Sqrt(len);
            for (int a = 0; a < 4; a++) axis[a] = tmp[a] / len;
        }

        float tmin = float.MaxValue, tmax = float.MinValue;
        for (int i = 0; i < 16; i++)
        {
            float t = 0;
            for (int c = 0; c < 4; c++) t += (px[i * 4 + c] - mean[c]) * axis[c];
            tmin = MathF.Min(tmin, t);
            tmax = MathF.Max(tmax, t);
        }
        for (int c = 0; c < 4; c++)
        {
            e0[c] = Math.Clamp(mean[c] + tmin * axis[c], 0, 255);
            e1[c] = Math.Clamp(mean[c] + tmax * axis[c], 0, 255);
        }
    }

    /// <summary>7-bit endpoint + shared p-bit: picks the p-bit that best reproduces the endpoint.</summary>
    private static void Quantize(ReadOnlySpan<float> e, Span<int> q, out int pbit)
    {
        float bestErr = float.MaxValue;
        pbit = 0;
        Span<int> cand = stackalloc int[4];
        for (int p = 0; p < 2; p++)
        {
            float err = 0;
            for (int c = 0; c < 4; c++)
            {
                int v = Math.Clamp((int)MathF.Round((e[c] - p) / 2f), 0, 127);
                cand[c] = v;
                float d = ((v << 1) | p) - e[c];
                err += d * d;
            }
            if (err < bestErr)
            {
                bestErr = err;
                pbit = p;
                cand.CopyTo(q);
            }
        }
    }

    private static float AssignIndices(ReadOnlySpan<float> px, ReadOnlySpan<int> q0, int p0, ReadOnlySpan<int> q1, int p1, Span<int> idx)
    {
        Span<int> pal = stackalloc int[64];
        for (int k = 0; k < 16; k++)
        {
            int w = Bc7Tables.Weights4[k];
            for (int c = 0; c < 4; c++)
            {
                int a = (q0[c] << 1) | p0, b = (q1[c] << 1) | p1;
                pal[k * 4 + c] = ((64 - w) * a + w * b + 32) >> 6;
            }
        }

        float total = 0;
        for (int i = 0; i < 16; i++)
        {
            float best = float.MaxValue;
            int bi = 0;
            for (int k = 0; k < 16; k++)
            {
                float d = 0;
                for (int c = 0; c < 4; c++)
                {
                    float t = px[i * 4 + c] - pal[k * 4 + c];
                    d += t * t;
                }
                if (d < best) { best = d; bi = k; }
            }
            idx[i] = bi;
            total += best;
        }
        return total;
    }

    private static bool Refit(ReadOnlySpan<float> px, ReadOnlySpan<int> idx, Span<float> e0, Span<float> e1)
    {
        float aa = 0, ab = 0, bb = 0;
        Span<float> ax = stackalloc float[4];
        Span<float> bx = stackalloc float[4];
        for (int i = 0; i < 16; i++)
        {
            float w = Bc7Tables.Weights4[idx[i]] / 64f;
            float a = 1 - w, b = w;
            aa += a * a; ab += a * b; bb += b * b;
            for (int c = 0; c < 4; c++)
            {
                ax[c] += a * px[i * 4 + c];
                bx[c] += b * px[i * 4 + c];
            }
        }
        float det = aa * bb - ab * ab;
        if (MathF.Abs(det) < 1e-6f) return false;
        float inv = 1f / det;
        for (int c = 0; c < 4; c++)
        {
            e0[c] = Math.Clamp((bb * ax[c] - ab * bx[c]) * inv, 0, 255);
            e1[c] = Math.Clamp((aa * bx[c] - ab * ax[c]) * inv, 0, 255);
        }
        return true;
    }

    private ref struct BitWriter(Span<byte> dst)
    {
        private readonly Span<byte> _dst = dst;
        private int _pos;

        public void Write(int value, int bits)
        {
            for (int i = 0; i < bits; i++, _pos++)
            {
                if ((_pos & 7) == 0) _dst[_pos >> 3] = 0;
                _dst[_pos >> 3] |= (byte)(((value >> i) & 1) << (_pos & 7));
            }
        }
    }
}
