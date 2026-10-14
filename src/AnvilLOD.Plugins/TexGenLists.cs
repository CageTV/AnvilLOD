using AnvilLOD.Core.World;
using AnvilLOD.Meshes;

namespace AnvilLOD.Plugins;

/// <summary>TexGen's own lists of the trees and grass it considered (<c>TexGen_SSE_*_Billboards.txt</c>).</summary>
public static class TexGenLists
{
    /// <summary>"plugin;FORMID" for every tree/grass TexGen listed. One listed without a billboard was skipped on purpose (too small).</summary>
    public static HashSet<string> Read(AssetIndex assets)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in assets.EnumeratePaths("textures\\terrain\\lodgen\\"))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("texgen_", StringComparison.Ordinal) || !name.EndsWith("_billboards.txt", StringComparison.Ordinal)) continue;
            if (!assets.TryOpen(path, out var st)) continue;
            using var reader = new StreamReader(st);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split(';');
                if (parts.Length >= 2) set.Add($"{parts[0].Trim()};{parts[1].Trim()}");
            }
        }
        return set;
    }

    /// <summary>"plugin.esm\model_000a7329" -> "plugin.esm;000a7329"</summary>
    public static string Key(string missingKey)
    {
        int slash = missingKey.IndexOf('\\');
        int us = missingKey.LastIndexOf('_');
        return slash < 0 || us < slash ? missingKey : $"{missingKey[..slash]};{missingKey[(us + 1)..]}";
    }
}
