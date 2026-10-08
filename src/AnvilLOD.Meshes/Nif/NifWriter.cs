using System.Numerics;
using System.Text;

namespace AnvilLOD.Meshes.Nif;

/// <summary>
/// Builds an SSE NIF file. Reserve block indices first (so blocks can reference each
/// other), fill each block's payload, then call <see cref="ToBytes"/>.
/// </summary>
public sealed class NifWriter
{
    private readonly List<string> _types = [];
    private readonly List<int> _blockTypes = [];
    private readonly List<byte[]?> _payloads = [];
    private readonly List<string> _strings = [];
    private readonly Dictionary<string, int> _stringIndex = new(StringComparer.Ordinal);

    public string Author { get; init; } = "AnvilLOD";

    public int Reserve(string type)
    {
        int t = _types.IndexOf(type);
        if (t < 0) { t = _types.Count; _types.Add(type); }
        _blockTypes.Add(t);
        _payloads.Add(null);
        return _payloads.Count - 1;
    }

    public void Set(int block, Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.Latin1, leaveOpen: true)) write(w);
        _payloads[block] = ms.ToArray();
    }

    public int String(string? s)
    {
        if (s is null) return -1;
        if (_stringIndex.TryGetValue(s, out var i)) return i;
        i = _strings.Count;
        _strings.Add(s);
        _stringIndex[s] = i;
        return i;
    }

    public byte[] ToBytes(params int[] roots)
    {
        for (int i = 0; i < _payloads.Count; i++)
            if (_payloads[i] is null) throw new InvalidOperationException($"NIF block {i} was reserved but never written.");

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Latin1);

        w.Write(Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n"));
        w.Write(NifFile.Version);
        w.Write((byte)1);
        w.Write(NifFile.UserVersion);
        w.Write((uint)_payloads.Count);
        w.Write(NifFile.BsVersionSse);
        WriteExportString(w, Author);
        WriteExportString(w, "");
        WriteExportString(w, "");

        w.Write((ushort)_types.Count);
        foreach (var t in _types) WriteSizedString(w, t);
        foreach (var bt in _blockTypes) w.Write((ushort)bt);
        foreach (var p in _payloads) w.Write((uint)p!.Length);

        w.Write((uint)_strings.Count);
        w.Write((uint)(_strings.Count == 0 ? 0 : _strings.Max(s => Encoding.Latin1.GetByteCount(s))));
        foreach (var s in _strings) WriteSizedString(w, s);
        w.Write(0u); // groups

        foreach (var p in _payloads) w.Write(p!);

        w.Write((uint)roots.Length);
        foreach (var r in roots) w.Write(r);
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteExportString(BinaryWriter w, string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s + "\0");
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }

    public static void WriteSizedString(BinaryWriter w, string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        w.Write((uint)bytes.Length);
        w.Write(bytes);
    }

    // ---------- common field groups ----------

    /// <summary>NiObjectNET (name, no extra data except the given list, no controller).</summary>
    public static void WriteObjectNet(BinaryWriter w, int nameIndex, params int[] extraData)
    {
        w.Write(nameIndex);
        w.Write((uint)extraData.Length);
        foreach (var e in extraData) w.Write(e);
        w.Write(-1); // controller
    }

    /// <summary>NiAVObject fields after NiObjectNET (SSE layout).</summary>
    public static void WriteAvObject(BinaryWriter w, uint flags, Vector3 translation, float scale)
    {
        w.Write(flags);
        w.Write(translation.X); w.Write(translation.Y); w.Write(translation.Z);
        w.Write(1f); w.Write(0f); w.Write(0f);
        w.Write(0f); w.Write(1f); w.Write(0f);
        w.Write(0f); w.Write(0f); w.Write(1f);
        w.Write(scale);
        w.Write(-1); // collision object
    }
}
