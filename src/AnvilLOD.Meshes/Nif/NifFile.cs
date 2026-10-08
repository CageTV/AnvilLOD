using System.Text;

namespace AnvilLOD.Meshes.Nif;

/// <summary>
/// Minimal reader for Skyrim SE NIFs (20.2.0.7, user version 12, BS version 100).
/// Parses the header and block table only; blocks are decoded on demand by
/// <see cref="NifGeometryReader"/>. Unknown blocks are skipped using the size table,
/// so any valid SSE NIF can be opened even if it contains block types we don't use.
/// </summary>
public sealed class NifFile
{
    public const uint Version = 0x14020007;
    public const uint UserVersion = 12;
    public const uint BsVersionSse = 100;

    public byte[] Data { get; }
    public uint BsVersion { get; }
    public IReadOnlyList<string> Strings { get; }
    public IReadOnlyList<NifBlock> Blocks { get; }
    public IReadOnlyList<int> Roots { get; }

    private NifFile(byte[] data, uint bsVersion, List<string> strings, List<NifBlock> blocks, List<int> roots)
    {
        Data = data;
        BsVersion = bsVersion;
        Strings = strings;
        Blocks = blocks;
        Roots = roots;
    }

    public static NifFile Read(byte[] data)
    {
        var r = new NifSpanReader(data, 0);

        int nl = Array.IndexOf(data, (byte)'\n');
        if (nl < 0 || nl > 64) throw new InvalidDataException("Not a NIF file (no header line).");
        var headerLine = Encoding.ASCII.GetString(data, 0, nl);
        if (!headerLine.StartsWith("Gamebryo File Format", StringComparison.Ordinal))
            throw new InvalidDataException($"Not a Gamebryo NIF: \"{headerLine}\".");
        r.Position = nl + 1;

        uint version = r.U32();
        if (version != Version) throw new NotSupportedException($"NIF version 0x{version:X8} is not supported (need 20.2.0.7).");
        byte endian = r.U8();
        if (endian != 1) throw new NotSupportedException("Big-endian NIFs are not supported.");
        uint userVersion = r.U32();
        uint numBlocks = r.U32();
        if (userVersion != UserVersion) throw new NotSupportedException($"NIF user version {userVersion} is not supported (need 12).");
        uint bsVersion = r.U32();
        if (bsVersion != BsVersionSse)
            throw new NotSupportedException(bsVersion == 83
                ? "This is a Skyrim LE (Oldrim) mesh. Convert it to SSE format (e.g. with Cathedral Assets Optimizer)."
                : $"NIF BS version {bsVersion} is not supported (need 100 / Skyrim SE).");

        r.ExportString(); // author
        r.ExportString(); // process script
        r.ExportString(); // export script

        int numTypes = r.U16();
        var types = new string[numTypes];
        for (int i = 0; i < numTypes; i++) types[i] = r.SizedString();

        var typeIndex = new ushort[numBlocks];
        for (int i = 0; i < numBlocks; i++) typeIndex[i] = r.U16();
        var sizes = new uint[numBlocks];
        for (int i = 0; i < numBlocks; i++) sizes[i] = r.U32();

        uint numStrings = r.U32();
        r.U32(); // max string length
        var strings = new List<string>((int)numStrings);
        for (int i = 0; i < numStrings; i++) strings.Add(r.SizedString());

        uint numGroups = r.U32();
        r.Position += (int)numGroups * 4;

        var blocks = new List<NifBlock>((int)numBlocks);
        int pos = r.Position;
        for (int i = 0; i < numBlocks; i++)
        {
            blocks.Add(new NifBlock(i, types[typeIndex[i] & 0x7FFF], pos, (int)sizes[i]));
            pos += (int)sizes[i];
        }
        if (pos > data.Length) throw new InvalidDataException("NIF is truncated.");

        r.Position = pos;
        var roots = new List<int>();
        if (r.Remaining >= 4)
        {
            uint numRoots = r.U32();
            for (int i = 0; i < numRoots && r.Remaining >= 4; i++) roots.Add(r.I32());
        }
        if (roots.Count == 0 && blocks.Count > 0) roots.Add(0);

        return new NifFile(data, bsVersion, strings, blocks, roots);
    }

    public string? String(int index) => index >= 0 && index < Strings.Count ? Strings[index] : null;

    public NifSpanReader ReaderFor(NifBlock b) => new(Data, b.Offset);
}

public readonly record struct NifBlock(int Index, string Type, int Offset, int Size);

/// <summary>Little-endian cursor over a byte array.</summary>
public struct NifSpanReader
{
    private readonly byte[] _d;
    public int Position;

    public NifSpanReader(byte[] data, int position)
    {
        _d = data;
        Position = position;
    }

    public readonly int Remaining => _d.Length - Position;

    public byte U8() => _d[Position++];
    public ushort U16() { var v = BitConverter.ToUInt16(_d, Position); Position += 2; return v; }
    public uint U32() { var v = BitConverter.ToUInt32(_d, Position); Position += 4; return v; }
    public int I32() { var v = BitConverter.ToInt32(_d, Position); Position += 4; return v; }
    public ulong U64() { var v = BitConverter.ToUInt64(_d, Position); Position += 8; return v; }
    public float F32() { var v = BitConverter.ToSingle(_d, Position); Position += 4; return v; }
    public Half F16() { var v = BitConverter.ToHalf(_d, Position); Position += 2; return v; }

    public string SizedString()
    {
        int n = (int)U32();
        var s = Encoding.Latin1.GetString(_d, Position, n);
        Position += n;
        return s;
    }

    public string ExportString()
    {
        int n = U8();
        var s = Encoding.Latin1.GetString(_d, Position, n).TrimEnd('\0');
        Position += n;
        return s;
    }

    public void Skip(int n) => Position += n;
}
