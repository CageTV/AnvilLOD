<p align="center"><img src="assets/anvillod-256.png" width="160" alt="AnvilLOD"></p>

# AnvilLOD

A fast, incremental object-LOD generator for Skyrim SE/AE, built as a modern replacement for DynDOLOD.

Its goals:

- **Fast builds.** Plugins are parsed once, BSAs are indexed once, quads are processed in parallel, and textures go through the GPU.
- **Incremental re-runs.** Every LOD block has a fingerprint, so changing one mod rebuilds only the blocks it touches.
- **Better in-game performance.** Fewer draw calls through merged, atlas-aware blocks.
- **PBR-aware LOD.** Distant terrain and objects should match True PBR / Community Shaders surfaces.

## Downloads

Each release has two downloads:

- **AnvilLOD-&lt;version&gt;.zip**: the tool (desktop app + command line). Unzip anywhere and run `AnvilLOD.App.exe`; it is not a mod and doesn't go in MO2.
- **AnvilLOD SKSE Plugin-&lt;version&gt;.zip**: a FOMOD to install with MO2 or Vortex. It asks for your game version and installs the matching DLL: **SE 1.5.97 up to AE 1.6.1170**, or **newer than 1.6.1170** (1.7.99, 1.7.104 and later).

Maintainers build both with `.\package.ps1` (output in `release\`).

## Status

AnvilLOD scans the load order and writes object LOD blocks (`.bto`), billboard tree LOD (`.lst`, `.btt` and the tree atlas) and grass LOD (from the grass cache) for every worldspace with LOD settings. Walled cities (Whiterun, Solitude, Windhelm, Riften, Markarth) are copied from their child worldspaces into Tamriel's LOD, following DynDOLOD's child world configs. Dynamic LOD for quest-switched references, water planes, waterfalls and other DynDOLOD grid objects come from the SKSE plugin; Seasons of Skyrim LOD is experimental. **Pre-release:** feedback and bug reports are very welcome. Re-runs only rewrite what changed. Object texture atlasing and large references are still to come. See [docs/ROADMAP.md](docs/ROADMAP.md).

## Requirements

- .NET 10 SDK (Visual Studio 2026, or VS 2022 17.14+ with the .NET 10 SDK installed)
- Skyrim SE/AE (Mod Organizer 2 instance or a plain Data folder)
- **Recommended:** DynDOLOD Resources SE and any LOD packs / LOD patches you like (FOLIP, mod-specific LOD). AnvilLOD uses their `_lod` meshes and the `DynDOLOD_SSE_*.ini` rule files they ship.
- **For tree LOD:** tree billboards, e.g. TexGen output installed as a mod (`textures\terrain\LODGen\…`). TexGen is only a billboard renderer; DynDOLOD itself is never run.
- **For grass LOD:** a grass cache (`grass\*.cgid` from NGIO or FasterNGIO) and TexGen grass billboards.
- **Optional:** a DynDOLOD install (set "DynDOLOD folder" in the app or `--dyndolod`). AnvilLOD reuses its Low/Medium/High rule presets. DynDOLOD itself is never run.
- **Recommended in game:** [LOD Refresh Bug Fix](https://www.nexusmods.com/skyrimspecialedition/mods/187070) (`SkyrimLODFixes.dll`). It fixes engine bugs where LOD doesn't reset after fast travel or worldspace changes, and a tree LOD / grass culling lock bug. DynDOLOD DLL NG fixes the first of those too, so keep this one if you remove DynDOLOD DLL NG. AnvilLOD's SKSE plugin deliberately doesn't patch the same engine code, so the two don't conflict.

## Mod author tools

The **Mod Author** tab (or `AnvilLOD author --plugin "<file>" --author-out <folder>`) checks one plugin for objects and trees that are seen from afar but have no LOD: no LOD mesh, an MNAM path to a file that isn't there, LOD textures that are missing, models that are empty, trees with no billboard. It can then generate what's missing into its own folder (never into the LOD output):

- `meshes\lod\<plugin>\<model>_lod_0/1/2.nif`: the full model with small parts dropped and the rest simplified (quadric error, seams and borders kept), using the model's own textures.
- `textures\terrain\lodgen\<plugin>\<model>_<formid>.dds/_1.dds/_1_n.dds/.txt`: TexGen-style billboards rendered from the tree model.
- `DynDOLOD\DynDOLOD_SSE_<plugin>.ini`: rules for the generated meshes, in DynDOLOD's format, so they work with AnvilLOD and DynDOLOD.
- `AnvilLOD Author Report - <plugin>.txt`: every placed object and tree with its status and what was generated.

The tools are behind a confirmation: generated files are a starting point to check in NifSkope and in game, and are only shared with the mod author's permission. Files already in the output folder are kept unless "Overwrite" is ticked, so hand-edited ones survive a rerun.

### LOD Mesh Maker

The **LOD Mesh Maker** tab (or `AnvilLOD lodmaker`) makes LOD meshes from full models you pick, with no plugin involved, for models that have no LOD mesh yet:

- **Models:** add `.nif` files, a mod folder, its `meshes` folder or any folder of models. It only reads the model files, so it needs no load order.
- **Output:** a **new mod in your MO2 mods folder** (give it a name; run it again with the same name to add more models) or any folder you choose. Never your LOD output folder.
- **What it writes:** `meshes\lod\<name>\<model>_lod_0/1/2.nif` (LOD4, LOD8, LOD16; pick the levels you want): small parts dropped, the rest simplified, the model's own textures kept. The files are written from the triangles alone, so collision and other game data from the full model are not copied. A **Detail** setting keeps more (finer) or less (coarser) of the model, and models below a size you set can be skipped.
- **Triangle budget** (on by default, optional): LOD meshes made from dense models can be far heavier than anything in a normal list, so each level is simplified until it fits a budget. **Recommended** scales with the model's largest dimension and comes from measuring the 2,554 LOD meshes in DynDOLOD Resources SE and LOD Model Library (the 90th percentile per size class, kept decreasing with distance): up to 1,000 units 250 / 200 / 150 triangles at LOD 0 / 1 / 2, 1,000-3,000 units 600 / 450 / 300, 3,000-8,000 units 2,500 / 1,500 / 650, over 8,000 units 1,400 / 900 / 600. **Custom** takes your own maximums per level (0 = no limit) and **Off** leaves only the Detail setting. A farther level never gets more triangles than a nearer one. If a budget can't be met (every part is already at its minimum) the file is still written and the report says it is over budget. Command line: `--budget recommended|off|<LOD0,LOD1,LOD2>`. The Mod Author tab and `author` command use the same budget for the LOD meshes they generate (a checkbox, `--budget`).
- **Rule file:** `DynDOLOD\DynDOLOD_SSE_<name>.ini` in DynDOLOD's format, so AnvilLOD and DynDOLOD use the new meshes for the models they were made from. Run it again and the file only gains lines for new models. Existing LOD files are kept unless "Overwrite" is ticked.
- Two models with the same file name can't both get LOD (LOD files are matched by name): the first, in path order, is made and the other is reported. Files that already are LOD meshes are skipped.
- Command line: `AnvilLOD lodmaker --input <file|folder> [--input ...] --output <folder>` or `--mo2 <instance> --new-mod <name>`, with `--levels 0,1,2`, `--detail <0.1-10>`, `--min-size <units>`, `--group`, `--no-rules`, `--overwrite`.

As with the other author tools, the generated files are a starting point to check in NifSkope and in game, and are only shared with the model author's permission.

## Build

```powershell
cd path\to\AnvilLOD
dotnet restore
dotnet build -c Release
dotnet test
```

## Run the scan

**MO2 users (recommended):** point AnvilLOD at your MO2 instance folder. It reads the profile's modlist, plugins and load order and layers the mod folders itself, the way MO2's VFS would. There's no need to launch it through MO2.

**Folders on different drives, or a wrong auto-detect:** AnvilLOD reads the game folder, mods, profiles and overwrite folders from the instance's `ModOrganizer.ini`. Open **Locations** under the instance folder (or use `--mo2-game`, `--mo2-mods`, `--mo2-profiles`, `--mo2-overwrite`) and type any of them to override what the ini says; a box you leave empty keeps the auto-detect. The game folder can be given as the folder with `SkyrimSE.exe` or as its `Data` folder. With the game, mods and profiles folders typed, `ModOrganizer.ini` isn't needed at all (name the profile with the Profile box or `--profile`).

```powershell
AnvilLOD.exe scan --mo2 "D:\Modlists\MyList" --worldspace Tamriel
AnvilLOD.exe scan --mo2 "D:\Modlists\MyList" --profile "Default" --worldspace Tamriel --report tamriel.json
```

Write the LOD files with `generate` (same options plus `--output`):

```powershell
AnvilLOD.exe generate --mo2 "D:\Modlists\MyList" --worldspace Tamriel --output "D:\Modlists\MyList\mods\AnvilLOD Output"
```

Then enable that mod in MO2 and put it below other LOD outputs.

**Plain install:**

```powershell
AnvilLOD.exe scan --data "D:\Skyrim\Data" --plugins "%LOCALAPPDATA%\Skyrim Special Edition\plugins.txt" --worldspace Tamriel
```

The console prints timings for each stage plus block counts per LOD level. The JSON report lists every block, its reference count and hash, and any LOD meshes that MNAM points to but that don't exist.

## SKSE plugin (dynamic LOD)

References that quests switch on and off (Helgen Reborn's destroyed and rebuilt town, for example) can't live in the static LOD blocks. AnvilLOD writes them to `SKSE\Plugins\AnvilLOD\AnvilLOD.dyn` instead, and the AnvilLOD SKSE plugin draws their LOD only while they're enabled.

### Water and animated distant objects

DynDOLOD's rules mark some objects with a Grid ("Near LOD", "Far LOD", "Far Full", "Never Fade LOD") and no LOD meshes: water planes, mineral pools, streams, waterfalls, creeks and rapids, fires, windmills, water wheels, ships. They get no static LOD; AnvilLOD hands them to the SKSE plugin, which draws them beyond the loaded cells ("Near LOD" ones up to `fNearGridDistance`, the others up to `fFarGridDistance`) as static models. Keeping them animated is an **experimental** opt-in (`bAnimateExperimental=1`, or the menu) because it can crash. When a mod ships DynDOLOD dynamic LOD meshes (`meshes\dyndolod\lod\<model path>_dyndolod_lod.nif`, as CS Water Mod and DynDOLOD Resources do) those are used; otherwise the full model. Rule files from mods (BIRDS, animated ships, natural waterfalls…) add their own grid objects the same way. Lake and sea water that's part of the landscape comes from terrain LOD (xLODGen), not from here. Turn it off with "Water and animated distant objects" in the app or `--no-grid-objects`.

Build it with Visual Studio 2026 (bundled vcpkg):

```powershell
cd path\to\AnvilLOD\skse
.\build.ps1 -Dest "D:\Modlists\MyList\mods\AnvilLOD"          # SE 1.5.97 / AE 1.6.x (default)
.\build.ps1 -Line 17                                            # newer than 1.6.1170 -> skse\dist\1.7.x
.\build.ps1 -Line both -Dest "D:\Modlists\MyList\mods\AnvilLOD" # both into skse\dist, line 1 into Dest
```

There are two build lines from the same source: SE 1.5.97 up to AE 1.6.1170, and everything newer. Skyrim 1.7.99/1.7.104 ship Address Library in a new file format that the CommonLibSSE-NG used for SE/AE can't read, so 1.7.x gets its own DLL built on CommonLibSSE-NG 7.2.0 (alandtse, `ng` branch; `cmake\manifest-17`, `cmake\ports-17`). Each DLL refuses to load on the other game version. CommonLibSSE-NG 7.2.0 is GPL-3.0-or-later (with a modding exception), so the 1.7 DLL has to be shared under GPL-compatible terms with its source.

With SKSE Menu Framework (or ApocryphaRealm Menu Framework, which answers to the same name) installed, AnvilLOD adds four pages to its menu: **Distances & Grass** (live sliders for the LOD block and tree distances, the full-model fade multipliers and the real-grass fade distances, saved to `AnvilLOD.ini`; the LOD ones are the same settings DynDOLOD's MCM offers), **Dynamic LOD** (on/off, range, cap, live counts), **Water & Animated Objects** (on/off, water-shader planes, near/far distances, animation rate) and **About** (build line, game version, related DLLs). Without a menu framework the same settings live in `AnvilLOD.ini`.

**Single install:** Generate also puts the matching `AnvilLOD.dll` into the output (`SKSE\Plugins`), so the LOD output mod is all you need. "SKSE plugin" in the app (`--skse-dll` in the CLI) is auto-detect by default (reads `SkyrimSE.exe`'s version next to the game's Data folder), or pick the build yourself, or "Installed separately" if you use the FOMOD instead — never both. The tool carries both builds in its `SKSE\` folder; `build.ps1` bundles whatever `skse\build.ps1` has built.

Settings are in `SKSE\Plugins\AnvilLOD.ini` (written with defaults on the first Generate); the log is `Documents\My Games\Skyrim Special Edition\SKSE\AnvilLOD.log`. Turn the feature off with "Dynamic LOD" in the app or `--no-dynamic`.

The plugin also applies the LOD distance settings (`fBlockLevel0Distance`, `fBlockLevel1Distance`, `fBlockMaximumDistance`, `fSplitDistanceMult`, `fTreeLoadDistance`, `fSkyCellRefFadeDistance`) the way DynDOLOD DLL NG does. It reads DynDOLOD's MCM files (`MCM\Config\DynDOLOD\settings.ini`, `MCM\Settings\DynDOLOD.ini`), so the values you saved there carry over, and the `[LOD]` section of `AnvilLOD.ini` overrides them. Save either file while the game runs and the change is applied within a couple of seconds. While DynDOLOD.dll is still installed it is left in charge (`bOverrideDynDOLOD=1` to take over).

### DynDOLOD rule compatibility

Rule files written for DynDOLOD are used as they are: DynDOLOD's own `Rules` folder, `Data\DynDOLOD\DynDOLOD_*.ini` from mods, and rule files a mod left in the Data root. Both 7- and 9-column lines, `Level0`/`Static LOD4`/`Full model`/`None` choices, FormIDs written with a load order prefix (`Mod.esp;0600187D`), `[… Settings] IgnoreWorlds=`, `Configs\DynDOLOD_SSE_mod_world_ignore.txt`, `mesh_lookup.txt`, child world configs and ChildworldMatches. the Grid column (water and animated objects, above). Not yet: the Reference column (`Replace`/`Enable`), `.patch` files, `IgnoreParentWorlds`.

### Editing the LOD rules

DynDOLOD's Advanced window lets you pick Low, Medium or High, tick **Candles** and **FXGlow**, and edit the Mesh and Reference rules list. AnvilLOD has the same, kept out of the way in the **LOD RULES** section of the settings column:

- **Candles / FXGlow** (`--candles`, `--fxglow`; need the DynDOLOD folder): also load DynDOLOD's `DynDOLOD_SSE_candles_*.ini` and `DynDOLOD_SSE_fxglow_*.ini` for the chosen preset. Candles, lanterns, sconces and the glow cards of fires get Far LOD, so the SKSE plugin draws them beyond the loaded cells. (The `candles_all` file holds the switched-off variants and is read before the lit ones, as DynDOLOD does.) In a Solstheim scan, ticking both added 121 rules and 102 more dynamic LOD references. Whether the lights show up well in game has not been checked yet.
- **Edit rules…** (next to the preset drop-down): opens the rule list for the chosen preset (plus Candles / FXGlow if ticked): mesh mask or reference, LOD 4 / 8 / 16 / 32, flags, grid, reference and which file it comes from, in the order they apply. Double-click a row, or press Edit, for the same editor DynDOLOD has (VWD, NoGlow, NoMATO, Dynamic, TREE; LOD levels with Level0-2, Billboard1-6, Full model, None; Grid; Reference). **Add rule…** makes a new one, **Find** filters the list. Editing one of DynDOLOD's rules makes your own copy (orange) that wins over the original; **Remove my rule** brings the original back. The rules for the DBF-style tree setup, for example, are the `tree` rule (LOD4 Level0, LOD8 Billboard4, LOD16 Billboard4, LOD32 Billboard6) and the `\` rule's LOD32.
- **Custom rules file:** your rules are saved as a DynDOLOD-format rule file (`%APPDATA%\AnvilLOD\Rules` by default) and its **tick box** switches it on. You can also point it at a file you already have, for example a rule file or preset from a modlist. Its rules are read before all of DynDOLOD's own, so they win; its `\` catch-all rule replaces the preset's. With the tick off, only DynDOLOD's own rule files are used. `--rules <file>` does the same on the command line.
- Only the rules of the preset files are listed; the rule files of individual plugins (FOLIP, mod patches and so on) are still read as before, below your own rules.
- The **NoGlow** flag and the **Reference** column are stored and saved but AnvilLOD doesn't act on them yet. Window glow in the LOD still comes from the glow maps of the LOD models themselves.
- Each of the three preset tabs remembers its own Candles / FXGlow / custom rules choices.

## Seasons of Skyrim (experimental)

With "Seasons of Skyrim LOD" ticked (`--seasons`), Generate reads the form-swap INIs in `Data\Seasons` (`*_WIN.ini`, `_SPR`, `_SUM`, `_AUT`; sections Statics, MovableStatics, Activators, Furniture) and writes seasonal object LOD next to the normal blocks: `<World>.<L>.<X>.<Y>.WIN.bto` and so on, the names [Seasons of Skyrim](https://github.com/powerof3/SeasonsOfSkyrim) loads for that season. Blocks containing a swapped object are rebuilt with the swap's LOD; every other block is a hard link to the normal one, so it costs almost no disk space. Seasons without INIs get no files, and Seasons of Skyrim falls back to the normal LOD. Tree and terrain LOD are the same in every season for now.

## Brightness

## 3D tree LOD (opt-in)

Tick **3D tree LOD** (`--tree-3d`) to put real 3D trees into object LOD, the way DynDOLOD's "ultra tree LOD" does. A tree uses a 3D tree LOD model, a `<tree>_<CRC32>passthru_lod.nif` in `meshes\DynDOLOD\lod\trees`, when one matches. The CRC32 is the checksum of the tree's own mesh file, so a model is only used for the exact mesh it was made for. Resource packs such as DynDOLOD Resources and Happy Little Trees' 3D LOD add-on ship them.

- Trees with a model get the 3D model at LOD4 and billboard cards at LOD8, LOD16 and LOD32 (`--tree-3d-lod8` uses the model at LOD8 too, with much bigger files). They are taken out of the billboard tree LOD (`.btt`); every other tree keeps billboard tree LOD.
- Models are read with their shaders untouched ("passthru"). `spherenormals` shapes get normals pointing away from the model's centre, and the **tree LOD brightness** setting applies to the 3D models and the cards alike.
- **Accept models by tree name** (`--tree-3d-by-name`, off by default): when no model matches the CRC32, a model stored under the plain tree name is used. DynDOLOD Resources ships most of its models that way. It may have been made for another version of the mesh (for example before a mod or a patcher changed it), so it's your choice. The log says how many tree types would be picked up by turning it on.
- A model whose textures can't be found isn't used, and the trees keep billboard tree LOD. The usual cause is the flat trunk textures (`textures\DynDOLOD\LOD\Trees\<tree>_<CRC32>_trunk_1.dds`), which TexGen renders from the model's `_trunk.nif` in `DynDOLOD\Render\Billboards`. Run TexGen with that resource installed, or use a pack that ships the textures.
- The log estimates how many triangles and megabytes the models add to the blocks before you generate. 3D trees can make LOD4 files much larger (about 40 KB per tree in a real list).

Tree LOD and object LOD brightness go from 10% to 110% in 10% steps (`--tree-brightness`, `--object-brightness`). Object LOD is darkened through vertex colours, so the LOD textures themselves, which other mods share, are never changed.

## PBR textures in object LOD (opt-in)

LOD is drawn with vanilla shaders, which don't do PBR. A LOD mesh that uses a texture your PBR mods replace (`textures\pbr\<same path>`) shows the vanilla look while the real object shows the PBR one, so mountains and buildings don't match their LOD. Tick **Match PBR textures in object LOD** (`--pbr-lod`) and AnvilLOD handles it the way DynDOLOD does:

- A LOD texture that TexGen has made a PBR twin of (`textures\lod\<name>lod.dds` → `<name>pbr_lod.dds`, with its `_n`) uses the twin. Run TexGen first; AnvilLOD reads its output like any other mod.
- Any other texture that has a PBR version gets a converted copy of the PBR albedo in `textures\anvillod\pbr\...`, and the LOD points at the copy. The vanilla path is never overwritten, so nothing else changes. Copies are shrunk (`--pbr-lod-size`, 512 by default) and written as BC7 with mips.
- The conversion darkens the PBR colours (a gentle power curve and DynDOLOD's default PBR scale of 0.65) because the PBR albedo is meant for linear lighting. It is an approximation fitted to TexGen's own output, so tune **PBR LOD texture brightness** (`--pbr-lod-brightness`, 100 = default) in game: lower it if the LOD looks brighter than the real objects. Changing the option or the brightness rebuilds every block.
- Tree LOD (billboards and 3D models) isn't covered yet.

## Presets

The three tabs at the top of the settings column are presets. Each one remembers every setting in the column: output folder, worldspaces, LOD levels, the DynDOLOD folder and rule preset, and all the options. Click a tab to switch (the column slides over to the new preset), double-click a tab to rename it. The preset you leave is saved first, and a preset that was never used starts as a copy of what is on screen, so switching never loses anything. Use them for, say, a fast test setup and a full release setup.

The game source (MO2 instance, Vortex or Data folder, the profile and the optional **Locations**) lives in the bar at the top of the window and is shared by all presets.

## Large references (opt-in)

Skyrim SE has a **large reference grid**: a list, per cell, in the worldspace record of an ESM-flagged plugin, of references whose full models the game shows beyond the normally loaded cells (`uLargeRefLODGridSize` in SkyrimPrefs.ini; 5 turns it off). Skyrim.esm and the DLCs list theirs. Tick **Large references** (`--large-refs`) and AnvilLOD lists the ones from your other ESM-flagged plugins that nobody lists yet, in `AnvilLOD.esm`, flagged ESL, so it takes no plugin slot.

- **Which references:** the base record is a STAT (or a MSTT with record flag 0x4), half the object bounds diagonal times the reference scale is more than `fLargeRefMinSize` (512 in Skyrim.esm), it is defined in ESM-flagged plugins only, it doesn't start disabled, and no plugin lists it yet. The size rule is not a guess: it reproduces the list Bethesda shipped. In Skyrim.esm's Tamriel every STAT reference whose full bounds diagonal times scale is at least 1,024 is listed, and almost none of the smaller ones.
- **What is left out, and why:** references that a plugin outside the ESMs overrides, and ones that start disabled, because those are the known causes of large references flickering (the full model and the LOD drawn in the same place). The report, `AnvilLOD Large References.txt` in the output folder, counts what was listed and what was left out and why, for every worldspace.
- **The plugin:** `AnvilLOD.esm` only holds one worldspace override per worldspace that got new entries, with the list entries (one per cell, the reference's own cell, as DynDOLOD does). Enable it after your other ESMs, so the worldspace data it is based on is current. Its masters are all ESM-flagged; AnvilLOD checks that every time and warns if one isn't. Turn the option off and the file is removed from the output.
- **ESL or a slot:** by default `AnvilLOD.esm` is flagged ESL, so it takes no plugin slot. If large references flicker at a distance and you want to rule the ESL flag out, untick **Flag AnvilLOD.esm as ESL** (`--large-refs-no-esl`) and generate again: it is then a normal ESM and uses one slot. Nothing in DynDOLOD's documentation says ESL causes flicker (it suggests an ESM+ESL plugin for this), so treat it as a troubleshooting step.
- **Cost:** full models loaded farther out cost performance, so the list is kept to what Bethesda's own rule would pick. The grid size (`uLargeRefLODGridSize`) decides how far they show.
- **Flicker:** AnvilLOD doesn't include the large reference bug workarounds. If you have DynDOLOD DLL NG installed, its Large Reference Bugs Workarounds are what DynDOLOD's documentation recommends for flicker, and it works alongside AnvilLOD's plugin (AnvilLOD leaves the LOD distance settings to it while it is installed). When AnvilLOD finds DynDOLOD DLL NG in your load order it says so in the log and the report; it doesn't recommend installing it.

## Terrain underside (opt-in)

Volumetric lighting mods (DVLaSS, EVLaS, Community Shaders' sky sync) need a low-resolution copy of the terrain facing downward under the world, or sun rays and shadows leak through the landscape. DynDOLOD makes it ("Terrain underside"); tick **Terrain underside** (`--underside`) and AnvilLOD does too, for every worldspace that has LOD32 blocks, including ones added by mods.

- `meshes\Terrain\<worldspace>\<worldspace>_Underside.nif`: one shape per LOD32 block, built from the winning LAND heights. Each vertex is the lowest LAND height within one step around it, so the mesh stays at or below the terrain everywhere and can't poke through. Neighbouring blocks agree along their shared edge. `--underside-detail <4|8|16|32|64|128>` sets the LAND vertices per underside square (default 16: about 90,000 triangles and 1.5 MB for Tamriel; smaller is finer and heavier).
- `AnvilLOD.esp`, flagged ESL, so it takes no plugin slot. It holds one moveable static per worldspace and places it: a persistent reference 500 units below the world, in each worldspace's own persistent cell. Enable it in your mod manager. It needs the plugins that define the worldspaces as masters, and it overrides the worldspace and its persistent cell as they were when you generated, so load it after plugins that edit worldspaces and generate again when they change.
- Don't combine it with DynDOLOD's own underside (they would both place one). Worldspaces DynDOLOD ignores (Blackreach, Soul Cairn, Apocrypha, the Deadlands, Japhet's Folly, the Creation Club Qasmoke world) get none. Hidden quads and the Solstheim and Markarth trims DynDOLOD's INI can set are not applied.
- Experimental: the placement is the same as DynDOLOD's except that the reference is enabled from the start instead of being switched on by a DynDOLOD script. The engine behaviour has not been checked in game yet.

## Vortex

Pick "Vortex" as the game source: Vortex deploys mods into the game's Data folder, so AnvilLOD reads it like a plain install with Vortex's load order. "Output to Vortex staging" sends the output to `%APPDATA%\Vortex\skyrimse\mods\AnvilLOD Output`; refresh Vortex, enable it and Deploy.

## Versions

`VERSION` holds the one version number the tool and the SKSE plugin share. `.\bump.ps1` raises the last number (0.2.0 → 0.2.1) after each change; `.\bump.ps1 -Minor` starts the next minor version. `.\package.ps1` builds everything and writes `release\AnvilLOD-<version>.zip` and `release\AnvilLOD SKSE Plugin-<version>.zip`.

## Desktop app

`src\AnvilLOD.App` is a WPF front end for the same pipeline. Choose **MO2 instance** (instance folder + profile) or **Data folder**, set the output folder, pick worldspaces, LOD levels and options, then press **Scan**. Results appear in four tabs: Overview (stats and timing), Blocks (filterable list of every `.bto` and whether it needs a rebuild), Missing meshes, and Log. Settings are saved to `%AppData%\AnvilLOD\settings.json`. **From MO2:** the app and the command line start from MO2's executables list like any other tool (add `AnvilLOD.App.exe` under Edit executables); the window title then says "running under MO2". It doesn't need MO2 to run, because it reads the instance's mods itself, but starting it from MO2 works too. If it ever fails to start from MO2, `%LocalAppData%\AnvilLOD\logspp.log` says how far it got. **Generate LOD** runs the scan and then writes the changed blocks into the output folder.

## Layout

| Project | Purpose | Depends on |
|---|---|---|
| `AnvilLOD.Core` | Grid math, `.lod` settings, reference model, quad bucketing, hashing, manifest, pipeline interfaces | nothing |
| `AnvilLOD.Plugins` | Mutagen: load order, MO2 instance reader, winning-override scan, fast BSA index, tree LOD, `ScanPipeline` | Core, Meshes, Textures, Mutagen |
| `AnvilLOD.Meshes` | Managed SSE NIF reader/writer, `.bto` builder, parallel generator | Core |
| `AnvilLOD.Textures` | DDS + BC1–BC7 decode, BC7 encode, tree atlas; GPU atlas/billboards later (milestone 2) | Core |
| `AnvilLOD.Cli` | Headless runner (`AnvilLOD.exe`) | Core, Plugins |
| `AnvilLOD.App` | WPF front end | Core, Plugins |
| `tests/AnvilLOD.Core.Tests` | xUnit tests for grid math, hashing, manifest | Core |

Package versions are pinned centrally in `Directory.Packages.props`.

## License

AnvilLOD is released under the **GNU General Public License, version 3** (GPL-3.0-only); see [LICENSE.txt](LICENSE.txt), which also lists the
third-party libraries and why GPL applies (Mutagen, GameFinder and CommonLibSSE-NG by alandtse are GPL-3.0 libraries).

    AnvilLOD
    Copyright (c) 2026 CageTV

    This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
    the Free Software Foundation, version 3 of the License. This program is distributed in the hope that it will be useful, but WITHOUT ANY
    WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License for
    more details. You should have received a copy of the GNU General Public License along with this program. If not, see
    <https://www.gnu.org/licenses/>.

AnvilLOD contains no DynDOLOD code and ships no DynDOLOD assets. Like xLODGen, it reads whatever LOD meshes are installed in the user's own setup
(via MNAM), and the generated output must not be redistributed if it contains other mods' assets without their authors' permission.
