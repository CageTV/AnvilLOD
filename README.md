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

## Build

```powershell
cd "H:\LOD Project\AnvilLOD"
dotnet restore
dotnet build -c Release
dotnet test
```

## Run the scan

**MO2 users (recommended):** point AnvilLOD at your MO2 instance folder. It reads the profile's modlist, plugins and load order and layers the mod folders itself, the way MO2's VFS would. There's no need to launch it through MO2.

```powershell
AnvilLOD.exe scan --mo2 "E:\Tabula Rasa" --worldspace Tamriel
AnvilLOD.exe scan --mo2 "E:\Tabula Rasa" --profile "tabula rasa!" --worldspace Tamriel --report tamriel.json
```

Write the LOD files with `generate` (same options plus `--output`):

```powershell
AnvilLOD.exe generate --mo2 "E:\Tabula Rasa" --worldspace Tamriel --output "E:\Tabula Rasa\mods\AnvilLOD Output"
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
cd "H:\LOD Project\AnvilLOD\skse"
.\build.ps1 -Dest "E:\Tabula Rasa\mods\ANvilLOD"              # SE 1.5.97 / AE 1.6.x (default)
.\build.ps1 -Line 17                                            # newer than 1.6.1170 -> skse\dist\1.7.x
.\build.ps1 -Line both -Dest "E:\Tabula Rasa\mods\ANvilLOD"   # both into skse\dist, line 1 into Dest
```

There are two build lines from the same source: SE 1.5.97 up to AE 1.6.1170, and everything newer. Skyrim 1.7.99/1.7.104 ship Address Library in a new file format that the CommonLibSSE-NG used for SE/AE can't read, so 1.7.x gets its own DLL built on CommonLibSSE-NG 7.2.0 (alandtse, `ng` branch; `cmake\manifest-17`, `cmake\ports-17`). Each DLL refuses to load on the other game version. CommonLibSSE-NG 7.2.0 is GPL-3.0-or-later (with a modding exception), so the 1.7 DLL has to be shared under GPL-compatible terms with its source.

With SKSE Menu Framework (or ApocryphaRealm Menu Framework, which answers to the same name) installed, AnvilLOD adds four pages to its menu: **Distances & Grass** (live sliders for the LOD block and tree distances, the full-model fade multipliers and the real-grass fade distances, saved to `AnvilLOD.ini`; the LOD ones are the same settings DynDOLOD's MCM offers), **Dynamic LOD** (on/off, range, cap, live counts), **Water & Animated Objects** (on/off, water-shader planes, near/far distances, animation rate) and **About** (build line, game version, related DLLs). Without a menu framework the same settings live in `AnvilLOD.ini`.

**Single install:** Generate also puts the matching `AnvilLOD.dll` into the output (`SKSE\Plugins`), so the LOD output mod is all you need. "SKSE plugin" in the app (`--skse-dll` in the CLI) is auto-detect by default (reads `SkyrimSE.exe`'s version next to the game's Data folder), or pick the build yourself, or "Installed separately" if you use the FOMOD instead — never both. The tool carries both builds in its `SKSE\` folder; `build.ps1` bundles whatever `skse\build.ps1` has built.

Settings are in `SKSE\Plugins\AnvilLOD.ini` (written with defaults on the first Generate); the log is `Documents\My Games\Skyrim Special Edition\SKSE\AnvilLOD.log`. Turn the feature off with "Dynamic LOD" in the app or `--no-dynamic`.

The plugin also applies the LOD distance settings (`fBlockLevel0Distance`, `fBlockLevel1Distance`, `fBlockMaximumDistance`, `fSplitDistanceMult`, `fTreeLoadDistance`, `fSkyCellRefFadeDistance`) the way DynDOLOD DLL NG does. It reads DynDOLOD's MCM files (`MCM\Config\DynDOLOD\settings.ini`, `MCM\Settings\DynDOLOD.ini`), so the values you saved there carry over, and the `[LOD]` section of `AnvilLOD.ini` overrides them. Save either file while the game runs and the change is applied within a couple of seconds. While DynDOLOD.dll is still installed it is left in charge (`bOverrideDynDOLOD=1` to take over).

### DynDOLOD rule compatibility

Rule files written for DynDOLOD are used as they are: DynDOLOD's own `Rules` folder, `Data\DynDOLOD\DynDOLOD_*.ini` from mods, and rule files a mod left in the Data root. Both 7- and 9-column lines, `Level0`/`Static LOD4`/`Full model`/`None` choices, FormIDs written with a load order prefix (`Mod.esp;0600187D`), `[… Settings] IgnoreWorlds=`, `Configs\DynDOLOD_SSE_mod_world_ignore.txt`, `mesh_lookup.txt`, child world configs and ChildworldMatches. the Grid column (water and animated objects, above). Not yet: the Reference column (`Replace`/`Enable`), `.patch` files, `IgnoreParentWorlds`.

## Seasons of Skyrim (experimental)

With "Seasons of Skyrim LOD" ticked (`--seasons`), Generate reads the form-swap INIs in `Data\Seasons` (`*_WIN.ini`, `_SPR`, `_SUM`, `_AUT`; sections Statics, MovableStatics, Activators, Furniture) and writes seasonal object LOD next to the normal blocks: `<World>.<L>.<X>.<Y>.WIN.bto` and so on, the names [Seasons of Skyrim](https://github.com/powerof3/SeasonsOfSkyrim) loads for that season. Blocks containing a swapped object are rebuilt with the swap's LOD; every other block is a hard link to the normal one, so it costs almost no disk space. Seasons without INIs get no files, and Seasons of Skyrim falls back to the normal LOD. Tree and terrain LOD are the same in every season for now.

## Brightness

Tree LOD and object LOD brightness go from 10% to 110% in 10% steps (`--tree-brightness`, `--object-brightness`). Object LOD is darkened through vertex colours, so the LOD textures themselves, which other mods share, are never changed.

## Vortex

Pick "Vortex" as the game source: Vortex deploys mods into the game's Data folder, so AnvilLOD reads it like a plain install with Vortex's load order. "Output to Vortex staging" sends the output to `%APPDATA%\Vortex\skyrimse\mods\AnvilLOD Output`; refresh Vortex, enable it and Deploy.

## Versions

`VERSION` holds the one version number the tool and the SKSE plugin share. `.\bump.ps1` raises the last number (0.2.0 → 0.2.1) after each change; `.\bump.ps1 -Minor` starts the next minor version. `.\package.ps1` builds everything and writes `release\AnvilLOD-<version>.zip` and `release\AnvilLOD SKSE Plugin-<version>.zip`.

## Desktop app

`src\AnvilLOD.App` is a WPF front end for the same pipeline. Choose **MO2 instance** (instance folder + profile) or **Data folder**, set the output folder, pick worldspaces, LOD levels and options, then press **Scan**. Results appear in four tabs: Overview (stats and timing), Blocks (filterable list of every `.bto` and whether it needs a rebuild), Missing meshes, and Log. Settings are saved to `%AppData%\AnvilLOD\settings.json`. **Generate LOD** runs the scan and then writes the changed blocks into the output folder.

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

## Licensing note

AnvilLOD contains no DynDOLOD code and ships no DynDOLOD assets. Like xLODGen, it reads whatever LOD meshes are installed in the user's own setup (via MNAM), and the generated output must not be redistributed if it contains other mods' assets without their authors' permission.
