using System.Buffers.Binary;

namespace AnvilLOD.Core.World;

/// <summary>
/// Contents of <c>Data\LODSettings\&lt;Worldspace&gt;.lod</c> (16 bytes, little endian):
/// <code>
/// int16 SWCellX   ("left" on UESP)
/// int16 SWCellY   ("top" on UESP)
/// int32 Stride    (cells covered by the whole grid, power of two)
/// int32 MinLevel  (lowest LOD level, usually 4)
/// int32 MaxLevel  (highest LOD level, usually 32)
/// </code>
/// Without this file the engine (and every LOD generator) has no grid origin for the worldspace.
/// Reference: https://en.uesp.net/wiki/Skyrim_Mod:LOD_Settings_File_Format
/// </summary>
public sealed record LodSettings(short SwCellX, short SwCellY, int Stride, int MinLevel, int MaxLevel)
{
    public const int SizeInBytes = 16;

    public CellCoord SouthWest => new(SwCellX, SwCellY);

    public static LodSettings Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeInBytes)
            throw new InvalidDataException($".lod file is {data.Length} bytes, expected {SizeInBytes}.");

        return new LodSettings(
            BinaryPrimitives.ReadInt16LittleEndian(data[0..2]),
            BinaryPrimitives.ReadInt16LittleEndian(data[2..4]),
            BinaryPrimitives.ReadInt32LittleEndian(data[4..8]),
            BinaryPrimitives.ReadInt32LittleEndian(data[8..12]),
            BinaryPrimitives.ReadInt32LittleEndian(data[12..16]));
    }

    public byte[] ToBytes()
    {
        var b = new byte[SizeInBytes];
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(0, 2), SwCellX);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(2, 2), SwCellY);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4, 4), Stride);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8, 4), MinLevel);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12, 4), MaxLevel);
        return b;
    }

    public bool Supports(LodLevel level) => (int)level >= MinLevel && (int)level <= MaxLevel;

    public static string RelativePathFor(string worldspaceEditorId) =>
        GamePath.Join("LODSettings", worldspaceEditorId + ".lod");
}
