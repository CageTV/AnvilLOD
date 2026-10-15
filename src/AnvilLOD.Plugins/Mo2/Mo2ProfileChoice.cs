namespace AnvilLOD.Plugins.Mo2;

/// <summary>Which MO2 profile the dropdown starts on.</summary>
public static class Mo2ProfileChoice
{
    /// <summary>
    /// When the app is started from MO2, the profile MO2 has selected is the one the user is looking at and about to play, so it wins over the
    /// remembered one (a remembered profile once made a whole generate run read a different profile than the one MO2 was on). Outside MO2, or when
    /// the choice isn't in the list, the remembered profile, then MO2's, then the first one.
    /// </summary>
    public static string? Pick(IReadOnlyList<string> profiles, string? remembered, string? mo2Selected, bool underMo2)
    {
        string? Find(string? name) => name is null ? null : profiles.FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
        return underMo2
            ? Find(mo2Selected) ?? Find(remembered) ?? profiles.FirstOrDefault()
            : Find(remembered) ?? Find(mo2Selected) ?? profiles.FirstOrDefault();
    }
}
