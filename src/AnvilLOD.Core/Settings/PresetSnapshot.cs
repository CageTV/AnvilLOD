using System.Reflection;
using System.Text.Json;

namespace AnvilLOD.Core.Settings;

/// <summary>
/// Copies the public settings of an object into a name-to-value map and back, so a settings class can hold several named
/// presets without a second list of fields to keep in step: whatever property the class gets later is included automatically.
/// Properties named in <c>exclude</c> (the ones that are not part of a preset) are skipped.
/// </summary>
public static class PresetSnapshot
{
    private static IEnumerable<PropertyInfo> Properties(Type type, IReadOnlySet<string> exclude) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !exclude.Contains(p.Name));

    public static Dictionary<string, JsonElement> Capture(object source, IReadOnlySet<string> exclude)
    {
        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in Properties(source.GetType(), exclude))
            map[p.Name] = JsonSerializer.SerializeToElement(p.GetValue(source), p.PropertyType);
        return map;
    }

    /// <summary>Sets every property that has a value in the map. Properties missing from it (added after the preset was saved) keep their value; ones that can't be read are skipped.</summary>
    public static void Apply(object target, IReadOnlyDictionary<string, JsonElement> values, IReadOnlySet<string> exclude)
    {
        foreach (var p in Properties(target.GetType(), exclude))
        {
            if (!values.TryGetValue(p.Name, out var element)) continue;
            try
            {
                p.SetValue(target, element.Deserialize(p.PropertyType));
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // a value of an older or different shape: leave the current one
            }
        }
    }
}
