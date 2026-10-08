using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace AnvilLOD.Core.World;

/// <summary>One grass blade/tuft from a grass cache.</summary>
public readonly record struct GrassInstance(Vector3 Position, float Scale, float Yaw);

/// <summary>All instances of one grass type (GRAS) in a cell.</summary>
public sealed record GrassCacheType(string Model, uint RuntimeFormId, IReadOnlyList<GrassInstance> Instances);

/// <summary>
/// Reader for NGIO / FasterNGIO grass caches (<c>grass\&lt;ws&gt;x0005y-005.cgid</c>).
/// <para>Layout (little-endian), reverse-engineered and checked against real caches:</para>
/// <code>
/// u32 typeCount
/// per type:  u32 len, char[len] model (NUL-terminated), f32 (unknown), u32 runtime FormID, u8[3] flags,
///            u32 chunkCount
///   per chunk: f32 center[3], f32 extent[3], f32 (0), u32 count, u32 halvesPerInstance (16),
///              count × half[16]: x, y, z, scale, then the rotation matrix
/// </code>
/// Positions are halves wrapped into ±49152 (12 cells); the chunk centre (a full float) tells which
/// 49152-unit period they belong to.
/// </summary>
public static partial class GrassCache
{
    private const float Period = 49152f;

    [GeneratedRegex(@"^(?<ws>.+)x(?<x>-?\d+)y(?<y>-?\d+)(\.(?<season>win|spr|sum|aut))?\.cgid$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNameRegex();

    /// <summary>Parses "Tamrielx0005y-005.cgid" (or a seasonal "….WIN.cgid").</summary>
    public static bool TryParseFileName(string fileName, out string worldspace, out int x, out int y, out string? season)
    {
        worldspace = ""; x = y = 0; season = null;
        var m = FileNameRegex().Match(fileName);
        if (!m.Success) return false;
        worldspace = m.Groups["ws"].Value;
        x = int.Parse(m.Groups["x"].Value);
        y = int.Parse(m.Groups["y"].Value);
        season = m.Groups["season"].Success ? m.Groups["season"].Value.ToUpperInvariant() : null;
        return true;
    }

    public static IReadOnlyList<GrassCacheType> Read(ReadOnlySpan<byte> d)
    {
        var types = new List<GrassCacheType>();
        int p = 0;
        uint typeCount = U32(d, ref p);
        if (typeCount > 4096) throw new InvalidDataException("Not a grass cache (type count).");
        for (int t = 0; t < typeCount; t++)
        {
            int len = (int)U32(d, ref p);
            if (len <= 0 || len > 1024 || p + len > d.Length) throw new InvalidDataException("Bad model string.");
            var model = Encoding.ASCII.GetString(d.Slice(p, len)).TrimEnd('\0');
            p += len;
            p += 4; // float, meaning unknown
            uint formId = U32(d, ref p);
            p += 3; // flags
            uint chunks = U32(d, ref p);

            var instances = new List<GrassInstance>();
            for (int c = 0; c < chunks; c++)
            {
                float cx = F32(d, ref p), cy = F32(d, ref p), cz = F32(d, ref p);
                p += 16; // extent xyz + unknown
                int count = (int)U32(d, ref p);
                int halves = (int)U32(d, ref p);
                if (halves < 6 || (long)p + (long)count * halves * 2 > d.Length) throw new InvalidDataException("Bad instance block.");
                instances.EnsureCapacity(instances.Count + count);
                for (int i = 0; i < count; i++)
                {
                    var rec = d.Slice(p, halves * 2);
                    float x = Unwrap(H(rec, 0), cx), y = Unwrap(H(rec, 1), cy), z = Unwrap(H(rec, 2), cz);
                    float scale = H(rec, 3);
                    float yaw = MathF.Atan2(H(rec, 5), H(rec, 4));
                    instances.Add(new GrassInstance(new Vector3(x, y, z), scale, yaw));
                    p += halves * 2;
                }
            }
            types.Add(new GrassCacheType(model, formId, instances));
        }
        return types;
    }

    private static float Unwrap(float v, float center) => v + MathF.Round((center - v) / Period) * Period;

    private static float H(ReadOnlySpan<byte> rec, int i) => (float)BinaryPrimitives.ReadHalfLittleEndian(rec[(i * 2)..]);

    private static uint U32(ReadOnlySpan<byte> d, ref int p)
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
        p += 4;
        return v;
    }

    private static float F32(ReadOnlySpan<byte> d, ref int p)
    {
        var v = BinaryPrimitives.ReadSingleLittleEndian(d[p..]);
        p += 4;
        return v;
    }
}
