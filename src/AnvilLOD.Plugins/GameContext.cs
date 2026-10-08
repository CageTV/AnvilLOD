using AnvilLOD.Plugins.Mo2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace AnvilLOD.Plugins;

public enum GameSourceMode
{
    /// <summary>A Data folder (auto-detected, explicit, or MO2's VFS when launched from MO2).</summary>
    DataFolder,
    /// <summary>An MO2 instance read directly from disk — no need to launch through MO2.</summary>
    Mo2Instance,
    /// <summary>Vortex: read like a Data folder install (Vortex deploys into the real Data folder).</summary>
    Vortex,
}

/// <summary>Where the game data and load order come from.</summary>
public sealed record GameContextOptions(
    string? DataFolder = null,      // DataFolder mode: null = auto-detect
    string? PluginsTxt = null,      // DataFolder mode: null = the game's default plugins.txt
    GameRelease Release = GameRelease.SkyrimSE,
    GameSourceMode Mode = GameSourceMode.DataFolder,
    string? Mo2InstanceFolder = null,
    string? Mo2Profile = null,      // null = the instance's selected profile
    string? ExcludeFolder = null,   // MO2 mode: never read this folder (AnvilLOD's own output) as a mod
    IReadOnlyList<string>? ExtraLooseRoots = null); // MO2 mode: more loose folders to index (e.g. "textures" for the author tools)

/// <summary>
/// Load order + link cache + asset access, regardless of where they come from.
/// Create one per run and dispose it at the end (plugins are memory-mapped while open).
/// </summary>
public sealed class GameContext : IDisposable
{
    public GameRelease Release { get; }
    public ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> LoadOrder { get; }
    public ILinkCache<ISkyrimMod, ISkyrimModGetter> LinkCache { get; }

    /// <summary>Human-readable description of the source ("Data folder …" / "MO2 instance … profile …").</summary>
    public string SourceDescription { get; }

    /// <summary>Base game Data folder.</summary>
    public string DataFolder { get; }

    public int PluginsListed { get; }
    public IReadOnlyList<string> MissingPlugins { get; }
    public IReadOnlyList<string> Warnings { get; }
    public int PluginsLoaded => PluginsListed - MissingPlugins.Count;

    private readonly Func<Func<string, bool>?, AssetIndex> _assetFactory;
    private readonly IDisposable[] _disposables;
    private Func<IEnumerable<(string Name, string FullPath)>> _rootFiles = () => [];

    /// <summary>Files directly in the (virtual) Data folder: name (as the game sees it) and full path on disk.</summary>
    public IEnumerable<(string Name, string FullPath)> RootFiles() => _rootFiles();

    private GameContext(
        GameRelease release,
        ILoadOrderGetter<IModListingGetter<ISkyrimModGetter>> loadOrder,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        string dataFolder,
        string source,
        Func<Func<string, bool>?, AssetIndex> assetFactory,
        List<string> warnings,
        params IDisposable[] disposables)
    {
        Release = release;
        LoadOrder = loadOrder;
        LinkCache = linkCache;
        DataFolder = dataFolder;
        SourceDescription = source;
        _assetFactory = assetFactory;
        _disposables = disposables;

        PluginsListed = loadOrder.Count;
        MissingPlugins = loadOrder.ListedOrder.Where(l => l.Mod is null).Select(l => l.ModKey.ToString()).ToList();
        if (MissingPlugins.Count > 0)
        {
            warnings.Add(
                $"{MissingPlugins.Count:N0} of {PluginsListed:N0} plugins in the load order could not be found or loaded " +
                $"(first: {string.Join(", ", MissingPlugins.Take(5))}).");
        }
        Warnings = warnings;
    }

    /// <summary>Indexes BSAs (+ loose files) as the game would see them for this source.</summary>
    public AssetIndex BuildAssets(Func<string, bool>? pathFilter = null) => _assetFactory(pathFilter);

    public static GameContext Open(GameContextOptions o, IProgress<string>? progress = null) =>
        o.Mode == GameSourceMode.Mo2Instance ? OpenMo2(o, progress) : OpenDataFolder(o);

    // ---------- Data folder mode ----------

    private static GameContext OpenDataFolder(GameContextOptions o)
    {
        var warnings = new List<string>();
        var builder = GameEnvironment.Typical.Builder<ISkyrimMod, ISkyrimModGetter>(o.Release);

        string? dataFolder = null;
        if (!string.IsNullOrWhiteSpace(o.DataFolder))
        {
            dataFolder = ResolveDataFolder(o.DataFolder.Trim(), warnings);
            builder = builder.WithTargetDataFolder(dataFolder);
        }

        if (!string.IsNullOrWhiteSpace(o.PluginsTxt))
        {
            if (dataFolder is null)
                throw new ArgumentException("A plugins.txt also needs the Data folder, so plugins can be found.");
            if (!File.Exists(o.PluginsTxt))
                throw new FileNotFoundException($"plugins.txt not found: {o.PluginsTxt}");

            // plugins.txt never lists the implicit plugins (base masters, Creation Club), so add them first.
            var listings = new List<ILoadOrderListingGetter>();
            var seen = new HashSet<ModKey>();
            void Add(ILoadOrderListingGetter l)
            {
                if (seen.Add(l.ModKey)) listings.Add(l);
            }

            foreach (var key in Implicits.Get(o.Release).Listings)
                if (File.Exists(Path.Combine(dataFolder, key.ToString())))
                    Add(new LoadOrderListing(key, enabled: true));
            try
            {
                foreach (var cc in CreationClubListings.GetLoadOrderListings(GameCategory.Skyrim, dataFolder))
                    if (File.Exists(Path.Combine(dataFolder, cc.ModKey.ToString()))) Add(cc);
            }
            catch (Exception ex)
            {
                warnings.Add("Could not read Skyrim.ccc: " + ex.Message);
            }
            foreach (var l in PluginListings.LoadOrderListingsFromPath(o.PluginsTxt, o.Release, dataFolder, throwOnMissingMods: false))
                Add(l);

            builder = builder.WithLoadOrder(listings.ToArray());
        }

        var env = builder.Build();
        var data = env.DataFolderPath.Path;
        var ctx = new GameContext(
            o.Release, env.LoadOrder, env.LinkCache, data, $"Data folder {data}",
            filter => AssetIndex.Build(o.Release, data, filter),
            warnings, env);
        ctx._rootFiles = () => Directory.EnumerateFiles(data).Select(f => (Path.GetFileName(f), f));

        if (ctx.MissingPlugins.Count > 0)
            warnings.Add("Outside MO2, mods installed through MO2 aren't in the Data folder. Use MO2 instance mode instead.");
        return ctx;
    }

    /// <summary>Accepts the Data folder or the game folder above it; fails clearly if Skyrim.esm isn't there.</summary>
    internal static string ResolveDataFolder(string path, List<string> warnings)
    {
        static bool HasMaster(string dir) => File.Exists(Path.Combine(dir, "Skyrim.esm"));

        if (HasMaster(path)) return path;
        var sub = Path.Combine(path, "Data");
        if (HasMaster(sub))
        {
            warnings.Add($"Using {sub} (the folder you picked is the game folder; LOD needs its Data subfolder).");
            return sub;
        }
        throw new DirectoryNotFoundException(
            $"Skyrim.esm was not found in \"{path}\" or its Data subfolder. Point the Data folder setting at your Skyrim Data folder.");
    }

    // ---------- MO2 instance mode ----------

    /// <summary>Top-level folders indexed from mods. Grows as later milestones need textures etc.</summary>
    public static readonly string[] Mo2LooseRoots = ["meshes", "lodsettings", "dyndolod", "textures\\terrain\\lodgen", "grass", "seasons"];

    private static GameContext OpenMo2(GameContextOptions o, IProgress<string>? progress)
    {
        if (string.IsNullOrWhiteSpace(o.Mo2InstanceFolder))
            throw new ArgumentException("Choose the MO2 instance folder (the one with ModOrganizer.ini).");

        var warnings = new List<string>();
        var instance = Mo2Instance.Open(o.Mo2InstanceFolder.Trim());
        var profile = instance.OpenProfile(string.IsNullOrWhiteSpace(o.Mo2Profile) ? null : o.Mo2Profile.Trim());
        progress?.Report($"MO2 instance {instance.InstanceFolder}, profile \"{profile.Name}\": " +
                         $"{profile.EnabledModsLowToHigh.Count:N0} enabled mods, {profile.EnabledPlugins.Count:N0} enabled plugins");

        if (!File.Exists(Path.Combine(instance.GameDataFolder, "Skyrim.esm")))
            throw new DirectoryNotFoundException($"Skyrim.esm not found in the instance's game Data folder: {instance.GameDataFolder}");

        progress?.Report("Building virtual Data folder from mods (plugins, BSAs, meshes, LOD settings)...");
        var roots = Mo2LooseRoots.Concat(o.ExtraLooseRoots ?? []).Select(r => r.Trim('\\').ToLowerInvariant()).Distinct().ToList();
        roots = roots.Where(r => !roots.Any(other => other != r && r.StartsWith(other + "\\", StringComparison.Ordinal))).ToList();
        var vdi = VirtualDataIndex.Build(profile, roots, o.ExcludeFolder);
        progress?.Report($"Virtual Data: {vdi.Layers.Count:N0} layers, {vdi.LooseFileCount:N0} loose files indexed in {vdi.BuildTime.TotalSeconds:F1}s");
        if (vdi.ExcludedLayer is not null)
            progress?.Report($"Skipping AnvilLOD's own output folder as input: {vdi.ExcludedLayer}");
        if (vdi.MissingModFolders > 0)
            warnings.Add($"{vdi.MissingModFolders} enabled mods in modlist.txt have no folder in {instance.ModsFolder}.");

        // Load order with plugin files resolved through the virtual Data folder.
        var release = o.Release;
        var resolved = profile.EnabledPlugins
            .Select(name => (Name: name, Key: ModKey.FromFileName(name), Path: vdi.ResolveRootFile(name)))
            // Implicit entries (base masters / Skyrim.ccc) that aren't installed are simply not loaded by the game.
            .Where(r => r.Path is not null || profile.ExplicitlyEnabled.Contains(r.Name))
            .Select(r => (r.Key, r.Path))
            .ToList();

        var masterStyles = new LoadOrder<IModMasterStyledGetter>(resolved
            .Where(r => r.Path is not null)
            .Select(r => (IModMasterStyledGetter)KeyedMasterStyle.FromPath(new ModPath(r.Key, r.Path!), release)));
        var param = BinaryReadParameters.Default with { MasterFlagsLookup = masterStyles };
        var skyrimRelease = release.ToSkyrimRelease();

        progress?.Report($"Opening {resolved.Count(r => r.Path is not null):N0} plugins...");
        var listings = new IModListing<ISkyrimModGetter>[resolved.Count];
        Parallel.For(0, resolved.Count, i =>
        {
            var (key, path) = resolved[i];
            listings[i] = path is null
                ? ModListing<ISkyrimModGetter>.CreateUnloaded(key, enabled: true)
                : new ModListing<ISkyrimModGetter>(SkyrimMod.CreateFromBinaryOverlay(new ModPath(key, path), skyrimRelease, param), enabled: true);
        });

        var loadOrder = new LoadOrder<IModListing<ISkyrimModGetter>>(listings);
        var linkCache = loadOrder.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();

        // Archives the game would load: INI list first, then each plugin's own BSAs in load order.
        AssetIndex BuildAssets(Func<string, bool>? filter)
        {
            var archives = new List<string>();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? fullPath)
            {
                if (fullPath is not null && added.Add(fullPath)) archives.Add(fullPath);
            }

            foreach (var a in profile.IniArchives()) Add(vdi.ResolveRootFile(a));

            var bsaNames = vdi.RootFileNames.Where(n => n.EndsWith(".bsa", StringComparison.Ordinal)).ToList();
            foreach (var (key, path) in resolved)
            {
                if (path is null) continue;
                foreach (var bsa in bsaNames.Where(b => Archive.IsApplicable(release, key, b)).OrderBy(b => b, StringComparer.Ordinal))
                    Add(vdi.ResolveRootFile(bsa));
            }

            return AssetIndex.Create(release, archives, prefix => vdi.EnumerateLoose(prefix), key =>
            {
                var p = vdi.FindLoose(key);
                return p is null ? null : new FileInfo(p);
            }, filter);
        }

        return new GameContext(
            release, loadOrder, linkCache, instance.GameDataFolder,
            $"MO2 instance {instance.InstanceFolder} (profile \"{profile.Name}\")",
            BuildAssets, warnings, loadOrder)
        {
            _rootFiles = () => vdi.RootFileNames.Select(n => (n, vdi.ResolveRootFile(n)!)),
        };
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
    }
}
