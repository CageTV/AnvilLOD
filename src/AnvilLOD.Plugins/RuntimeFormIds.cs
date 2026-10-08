using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

/// <summary>Maps FormKeys to and from the FormIDs the game uses at runtime (full plugins 00–FD, light plugins FE xxx).</summary>
internal sealed class RuntimeFormIds
{
    private readonly Dictionary<ModKey, (bool Light, int Index)> _slots = [];
    private readonly List<ModKey> _full = [];
    private readonly List<ModKey> _light = [];

    public static RuntimeFormIds FromLoadOrder(ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> loadOrder)
    {
        var r = new RuntimeFormIds();
        foreach (var l in loadOrder.ListedOrder)
        {
            if (l.Mod is null || !l.Enabled) continue;
            bool isLight = l.ModKey.Type == ModType.Light || l.Mod.IsSmallMaster;
            if (isLight)
            {
                r._slots[l.ModKey] = (true, r._light.Count);
                r._light.Add(l.ModKey);
            }
            else
            {
                r._slots[l.ModKey] = (false, r._full.Count);
                r._full.Add(l.ModKey);
            }
        }
        return r;
    }

    public uint Resolve(FormKey fk)
    {
        if (!_slots.TryGetValue(fk.ModKey, out var slot)) return fk.ID & 0x00FF_FFFF;
        return slot.Light
            ? 0xFE00_0000u | ((uint)slot.Index << 12) | (fk.ID & 0xFFF)
            : ((uint)slot.Index << 24) | (fk.ID & 0x00FF_FFFF);
    }

    /// <summary>The FormKey a runtime FormID points at in the current load order, if any.</summary>
    public FormKey? ToFormKey(uint runtimeId)
    {
        uint index = runtimeId >> 24;
        if (index == 0xFE)
        {
            int light = (int)((runtimeId >> 12) & 0xFFF);
            return light < _light.Count ? new FormKey(_light[light], runtimeId & 0xFFF) : null;
        }
        return index < _full.Count ? new FormKey(_full[(int)index], runtimeId & 0x00FF_FFFF) : null;
    }
}
