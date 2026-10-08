using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AnvilLOD.Core.World;

namespace AnvilLOD.Core.Incremental;

/// <summary>
/// Fingerprints everything that affects one LOD block's output. If the fingerprint matches the
/// last build's manifest, the block is skipped — this is what makes re-runs take seconds.
/// <para>
/// Callers pass an <paramref name="assetFingerprint"/> lookup so that swapping a LOD mesh
/// (same path, new file) also invalidates the block. The settings string covers user options.
/// </para>
/// </summary>
public static class QuadHasher
{
    /// <summary>Bump when the output format/algorithm changes so every block rebuilds once.</summary>
    public const int AlgorithmVersion = 3; // 3: per-cell LOD4 segments, enable-parent state

    public static string Hash(
        QuadKey quad,
        IReadOnlyList<LodReference> refsSortedByFormKey,
        Func<string, string?> assetFingerprint,
        string settingsFingerprint)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[4];

        void Str(string? s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "\0");
            BinaryPrimitives.WriteInt32LittleEndian(buf, bytes.Length);
            h.AppendData(buf);
            h.AppendData(bytes);
        }
        void F(float f)
        {
            BinaryPrimitives.WriteSingleLittleEndian(buf, f);
            h.AppendData(buf);
        }

        Str($"v{AlgorithmVersion}");
        Str(settingsFingerprint);
        Str(quad.FileName);

        foreach (var r in refsSortedByFormKey)
        {
            var mesh = r.Meshes.For(quad.Level);
            Str(r.FormKey);
            Str(mesh);
            Str(mesh is null ? null : assetFingerprint(mesh));
            F(r.Position.X); F(r.Position.Y); F(r.Position.Z);
            F(r.RotationRadians.X); F(r.RotationRadians.Y); F(r.RotationRadians.Z);
            F(r.Scale);
            BinaryPrimitives.WriteInt32LittleEndian(buf, (int)r.Flags);
            h.AppendData(buf);
        }

        return Convert.ToHexString(h.GetHashAndReset(), 0, 16);
    }
}
