using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace AnvilLOD.Core.Textures;

/// <summary>A texture Skyrim SE cannot load, found by <see cref="TextureFixer.Scan"/>.</summary>
/// <param name="Root">The folder that was searched (a mod folder); <paramref name="Relative"/> is the path below it.</param>
/// <param name="Fixable">AnvilLOD can convert it to BC7 (BC6H, the format that crashes the game when a shader samples it).</param>
public sealed record BadTexture(string Root, string Relative, string Mod, int Dxgi, string Format, int Width, int Height, int Mips, bool Fixable)
{
    public string FullPath => Path.Combine(Root, Relative);
}

/// <summary>What <see cref="TextureFixer.Fix"/> did with one texture.</summary>
public sealed record FixedTexture(BadTexture Source, string Output, string? Backup, string Status);

/// <summary>
/// Finds DDS textures that Skyrim SE cannot load and converts them to BC7. The classic case is BC6H (DXGI 95/96, an HDR format): a
/// texture saved that way loads as nothing and crashes the game as soon as a shader samples it, often far from where the file is.
/// Scanning only reads the 148-byte headers. Conversion runs texconv (Microsoft's DirectXTex tool, which xEdit, DynDOLOD and many
/// other modding tools already ship in their "Edit Scripts" folder); nothing of it is bundled.
/// </summary>
public static class TextureFixer
{
    /// <summary>DXGI formats the game loads: BC1-BC5, BC7 (all variants), and the usual 8-bit uncompressed ones.</summary>
    private static readonly HashSet<int> Loadable = [2, 10, 28, 29, 54, 56, 61, 71, 72, 74, 75, 77, 78, 80, 81, 83, 84, 87, 88, 91, 98, 99];

    private static readonly string[] LegacyFourCc = ["DXT1", "DXT2", "DXT3", "DXT4", "DXT5", "ATI1", "ATI2", "BC4U", "BC5U"];

    public static string FormatName(int dxgi) => dxgi switch
    {
        95 => "BC6H (unsigned float)",
        96 => "BC6H (signed float)",
        98 => "BC7",
        99 => "BC7 sRGB",
        71 or 72 => "BC1",
        74 or 75 => "BC2",
        77 or 78 => "BC3",
        80 or 81 => "BC4",
        83 or 84 => "BC5",
        28 or 29 => "R8G8B8A8",
        87 or 91 => "B8G8R8A8",
        88 => "B8G8R8X8",
        2 or 10 => "float",
        _ => "DXGI " + dxgi,
    };

    /// <summary>Reads one file's header. Returns null when the texture is fine, otherwise what is wrong with it.</summary>
    public static BadTexture? Inspect(string root, string relative, string mod, ReadOnlySpan<byte> header)
    {
        if (header.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x20534444)
            return new BadTexture(root, relative, mod, -1, "not a DDS file", 0, 0, 0, false);
        int height = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        int mips = Math.Max(1, BinaryPrimitives.ReadInt32LittleEndian(header[28..]));
        if (!header.Slice(84, 4).SequenceEqual("DX10"u8))
        {
            var cc = Encoding.ASCII.GetString(header.Slice(84, 4));
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[80..]);
            if ((flags & 0x4) != 0 && !LegacyFourCc.Contains(cc))
                return new BadTexture(root, relative, mod, -2, "FourCC " + cc, width, height, mips, false);
            return null;
        }
        if (header.Length < 132) return new BadTexture(root, relative, mod, -1, "truncated header", width, height, mips, false);
        int dxgi = BinaryPrimitives.ReadInt32LittleEndian(header[128..]);
        if (Loadable.Contains(dxgi)) return null;
        return new BadTexture(root, relative, mod, dxgi, FormatName(dxgi), width, height, mips, dxgi is 95 or 96);
    }

    /// <summary>Every unloadable DDS under the given folders (each is searched whole; a mod folder's own name is reported as the mod).</summary>
    public static IReadOnlyList<BadTexture> Scan(IEnumerable<string> folders, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = new List<BadTexture>();
        var buffer = new byte[148];
        int files = 0, mods = 0;
        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder)) continue;
            var root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            var mod = Path.GetFileName(root);
            mods++;
            foreach (var path in Directory.EnumerateFiles(root, "*.dds", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                files++;
                int read;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    read = fs.Read(buffer, 0, buffer.Length);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (Inspect(root, Path.GetRelativePath(root, path), mod, buffer.AsSpan(0, read)) is { } bad) found.Add(bad);
            }
            if (mods % 25 == 0) progress?.Report($"Scanned {mods:N0} folders, {files:N0} textures, {found.Count:N0} that the game cannot load so far");
        }
        progress?.Report($"Scanned {mods:N0} folders, {files:N0} textures: {found.Count:N0} that the game cannot load, {found.Count(b => b.Fixable):N0} of them can be converted");
        return found;
    }

    /// <summary>
    /// texconv.exe, looked for in the given folders (and the ones under them that tools use: "Edit Scripts", "texconv"). Null when there is none.
    /// </summary>
    public static string? FindTexconv(IEnumerable<string?> hints)
    {
        string[] names = ["Texconvx64.exe", "texconv.exe", "Texconv.exe"];
        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint)) continue;
            var h = hint.Trim().Trim('"');
            if (File.Exists(h) && names.Contains(Path.GetFileName(h), StringComparer.OrdinalIgnoreCase)) return h;
            if (!Directory.Exists(h)) continue;
            if (Search(h, names, 3) is { } found) return found;
        }
        return null;
    }

    /// <summary>The first texconv under a folder, looking at most <paramref name="depth"/> folders deep (a tools folder holds xLODGen\Edit Scripts\Texconvx64.exe).</summary>
    private static string? Search(string folder, string[] names, int depth)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(folder, n);
            if (File.Exists(p)) return p;
        }
        if (depth == 0) return null;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if (Search(sub, names, depth - 1) is { } hit) return hit;
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return null;
    }

    /// <summary>
    /// Converts the fixable textures to BC7 (keeping size and mip chain). With <paramref name="outputFolder"/> the fixed files go there, below
    /// the same relative paths (a new mod that overrides the broken ones); without it each file is replaced in place and its original is
    /// first copied to <paramref name="backupFolder"/>. Nothing is written when <paramref name="dryRun"/> is set.
    /// </summary>
    public static IReadOnlyList<FixedTexture> Fix(IEnumerable<BadTexture> items, string texconv, string? outputFolder, string? backupFolder,
        bool dryRun, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (outputFolder is null && backupFolder is null && !dryRun)
            throw new ArgumentException("Converting in place needs a backup folder.");
        var list = items.Where(i => i.Fixable).ToList();
        var results = new List<FixedTexture>(list.Count);
        int n = 0;
        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            n++;
            string target = outputFolder is not null ? Path.Combine(outputFolder, item.Relative) : item.FullPath;
            string? backup = outputFolder is null ? Path.Combine(backupFolder!, item.Mod, item.Relative) : null;
            if (dryRun) { results.Add(new FixedTexture(item, target, backup, "Would convert")); continue; }
            try
            {
                progress?.Report($"Converting {n:N0} of {list.Count:N0}: {item.Mod}\\{item.Relative} ({item.Width}x{item.Height})");
                if (backup is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    if (!File.Exists(backup)) File.Copy(item.FullPath, backup);   // never overwrite the first backup
                }
                var temp = Path.Combine(Path.GetTempPath(), "AnvilLOD-texfix-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(temp);
                try
                {
                    var error = RunTexconv(texconv, item.FullPath, temp, ct);
                    var made = Path.Combine(temp, Path.GetFileNameWithoutExtension(item.FullPath) + ".dds");
                    if (error is not null || !File.Exists(made)) { results.Add(new FixedTexture(item, target, backup, "Failed: " + (error ?? "texconv wrote nothing"))); continue; }
                    var check = new byte[148];
                    using (var fs = File.OpenRead(made)) fs.ReadExactly(check, 0, 148);
                    var after = Inspect(item.Root, item.Relative, item.Mod, check);
                    int dxgi = BinaryPrimitives.ReadInt32LittleEndian(check.AsSpan(128));
                    int w = BinaryPrimitives.ReadInt32LittleEndian(check.AsSpan(16)), h = BinaryPrimitives.ReadInt32LittleEndian(check.AsSpan(12)), m = BinaryPrimitives.ReadInt32LittleEndian(check.AsSpan(28));
                    if (after is not null || w != item.Width || h != item.Height || m != item.Mips)
                    { results.Add(new FixedTexture(item, target, backup, $"Failed: result is {w}x{h}, {m} mips, DXGI {dxgi}; the original was left alone")); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(made, target, overwrite: true);
                    results.Add(new FixedTexture(item, target, backup, "Converted to BC7"));
                }
                finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                results.Add(new FixedTexture(item, target, backup, "Failed: " + ex.Message));
            }
        }
        progress?.Report($"{results.Count(r => r.Status.StartsWith("Converted", StringComparison.Ordinal)):N0} of {list.Count:N0} converted");
        return results;
    }

    private static string? RunTexconv(string texconv, string input, string outputDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(texconv)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-nologo", "-f", "BC7_UNORM", "-y", "-o", outputDir, input }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new IOException("texconv could not be started.");
        var output = p.StandardOutput.ReadToEndAsync();
        var err = p.StandardError.ReadToEndAsync();
        while (!p.WaitForExit(250))
            if (ct.IsCancellationRequested) { try { p.Kill(true); } catch (InvalidOperationException) { } ct.ThrowIfCancellationRequested(); }
        var text = output.Result + err.Result;
        return p.ExitCode == 0 && !text.Contains("FAILED", StringComparison.Ordinal) ? null : text.Trim().Split('\n').LastOrDefault(l => l.Length > 0)?.Trim() ?? "texconv failed";
    }
}
