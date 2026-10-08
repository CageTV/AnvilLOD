namespace AnvilLOD.Core.World;

/// <summary>Data-relative game paths always use backslashes, regardless of host OS.</summary>
public static class GamePath
{
    public static string Join(params string[] parts) => string.Join('\\', parts);

    /// <summary>Lower-cased, backslash-separated, no leading "data\" — the form used as a cache/index key.</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        if (p.StartsWith("data\\", StringComparison.Ordinal)) p = p[5..];
        return p;
    }
}
