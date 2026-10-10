# AnvilLOD Roadmap

**Goal: a complete DynDOLOD replacement.** Users should never need to run DynDOLOD or TexGen, and never need an old DynDOLOD output. A full run should take minutes, not hours, and re-runs should only redo what changed.

AnvilLOD reads LOD **assets** (the `_lod` meshes and LOD textures that mods and resource packs install) but never DynDOLOD's **output**. xLODGen can still be used for terrain LOD until AnvilLOD has its own terrain step.

| DynDOLOD feature | AnvilLOD replacement | Status |
|---|---|---|
| Object LOD (`.bto`) | Managed NIF reader/writer, parallel per block | ✅ v1 |
| LOD mesh discovery (MNAM + `name_lod_N.nif` by file name) | MNAM first, named files fill gaps, DynDOLOD folder order | ✅ |
| DynDOLOD rules (preset + mod-shipped `DynDOLOD_SSE_*.ini`) | Read as-is from the DynDOLOD install and Data\DynDOLOD; Candles / FXGlow rule sets; rule editor and a custom rules file that wins over them | ✅ (NoGlow flag + Reference column stored, not applied) |
| Hidden-face removal (needs terrain) | LAND heights read from plugins; conservative buried-triangle test | ✅ v1 |
| Texture atlas (TexGen + DynDOLOD) | GPU atlas, BC7, PBR-aware pages | planned |
| Tree LOD: billboards (`.lst`/`.btt` + atlas) | TexGen billboards packed into a BC7 atlas (managed encoder), engine format verified against vanilla and DynDOLOD | ✅ v1 |
| Tree LOD: 3D tree LOD models in object LOD (`passthru_lod.nif`) | Models matched by CRC32 (or, opt-in, plain name), 3D at LOD4 (optionally LOD8), billboard cards beyond, passthru shaders kept, size estimate in the log | ✅ v1 (opt-in) |
| Tree LOD: 3D models for trees that have none | Simplify the full tree model into a hybrid crown + trunk model, cached as a DynDOLOD-compatible file | planned |
| Tree LOD: own billboard / trunk renderer | GPU renderer (TexGen not needed, also for the flat trunk textures 3D models use) | planned |
| Grass LOD (NGIO / FasterNGIO `.cgid`) | Cache read directly, sampled into alpha-tested billboard quads in LOD4, per-cell segments | ✅ v1 |
| Seasons (Seasons of Skyrim) | `Data\Seasons\*_WIN/_SPR/_SUM/_AUT.ini` swaps → `<block>.WIN.bto` etc.; unchanged blocks hard-linked. Tree/terrain LOD not seasonal yet | 🧪 experimental |
| Dynamic LOD for quest-switched references | SKSE plugin (two builds: SE 1.5.97–AE 1.6.1170 and 1.7.x), `AnvilLOD.dyn`, no plugin needed | ✅ v1 |
| In-game settings (DynDOLOD MCM equivalent) | SKSE Menu Framework pages: LOD/fade/grass distances, dynamic LOD, water & animated objects | ✅ |
| Large references (engine large reference grid) | `AnvilLOD.esm` (ESL): lists qualifying references from ESM plugins that no plugin lists, using the rule that reproduces Skyrim.esm's list; report of what was left out | 🧪 experimental |
| Glow windows | SKSE plugin | planned |
| Per-cell LOD4 segments (engine hides LOD for loaded cells, incl. child worlds like Whiterun) | BSSubIndexTriShape 4×4 segments, matched to DynDOLOD output | ✅ |
| Enable parents (quest-built places, e.g. Helgen Reborn) | Initial state resolved through the parent chain; start-disabled refs left out of static LOD | ✅ |
| Water planes, waterfalls, fires, windmills (DynDOLOD Grid objects) | Grid column read from the rules; drawn by the SKSE plugin beyond the loaded cells, `_dyndolod_lod` meshes preferred | ✅ v1 (static; animation 🧪, water look needs tuning) |
| Walled cities in the parent's LOD (Dragonsreach etc.) | Child world references copied into Tamriel's LOD, driven by DynDOLOD's `Configs\DynDOLOD_SSE_childworld_*.ini` + ChildworldMatches | ✅ |
| Child worldspaces with their own LOD | Generate for parent/child links | planned |
| Occlusion.esp | Build from our own terrain data (xLODGen meanwhile) | planned |
| LOD meshes from full models you pick (LOD Mesh Maker) | `name_lod_0/1/2.nif` (simplified, no collision, own textures) into a new mod or folder, plus a DynDOLOD-format rule file | 🧪 experimental |
| PBR full models → matching object LOD textures | TexGen `pbr_lod` twins used as found; other textures with a `textures\pbr` version get a converted, shrunk copy (`textures\anvillod\pbr`). Tree LOD not yet | 🧪 experimental (conversion curve is an approximation) |
| Terrain underside (volumetric lighting) | `<ws>_Underside.nif` from LAND (never above the terrain) + ESL ESM/ESP that place it, as DynDOLOD does | 🧪 experimental (in-game check pending) |
| Terrain LOD | xLODGen for now; own GPU terrain step later | later |

## Milestone 1a — Scan ✅
- [x] Load order through Mutagen (auto-detect, explicit `--data`/`--plugins`, or MO2 instance read directly)
- [x] BSA index built once, with loose-file overrides
- [x] Winning REFR scan for STAT bases with MNAM LOD meshes; missing meshes reported
- [x] `.lod` settings parsing and LOD grid alignment (matches DynDOLOD's block names)
- [x] Quad bucketing, per-quad fingerprints, manifest and incremental plan
- [x] Real load order: 1,532 plugins, 1.88M refs, 61k LOD refs, 2,562 blocks in ~6s

## Mod managers
- [x] MO2: instance read directly (modlist, plugins, loadorder, local INIs). Running *inside* MO2 isn't supported (its VFS breaks .NET 10 startup) and isn't needed.
- [x] AnvilLOD's own output folder is skipped as input
- [ ] Vortex preset: game Data folder + `%LOCALAPPDATA%\Skyrim Special Edition\plugins.txt`, warn if not deployed

## Milestone 1b — Object LOD `.bto` ✅ (v1)
- [x] LODGen-compatible block layout, verified vertex-for-vertex against existing output
- [x] REFR rotation convention R = Rx(-x)·Ry(-y)·Rz(-z)
- [x] Merge per material, split at 65,535 vertices/triangles; parallel writing; stale cleanup
- [x] Full run: 2,562 blocks, 32M triangles, 1,371 meshes in 3.2s
- [x] **In-game check** (Tamriel): objects, child worlds, enable parents

## Milestone 1c — Coverage and size ✅ (v1)
- [x] LOD meshes by file name (`name_lod.nif`, `name_lod_0..3.nif`), folder precedence meshes < dlc01\lod < dlc02\lod < lod < dyndolod, lower levels fall back to higher
- [x] MNAM wins per level; named files fill missing levels (DynDOLOD output was verified to use MNAM)
- [x] DynDOLOD rule files reused (7- and 9-column formats, Low/Medium/High presets, plugin-specific files, Data\DynDOLOD overrides, FormID rules first); "Full model" supported, "Billboard" waits for tree LOD
- [x] Base types: STAT, MSTT, ACTI, FURN, DOOR, CONT (TREE with tree LOD)
- [x] Terrain from LAND/VHGT (winning overrides) → drop triangles whose corners, edge midpoints and centre are all buried (margin 64/128/256/512 by level). Never removes anything DynDOLOD keeps (checked on a real block); DynDOLOD removes more, so there's room to tighten later
- [x] Terrain fingerprint per worldspace in the block hash, so landscape edits rebuild the right blocks
- [x] LOD4 shapes split into per-cell segments (fixes LOD staying visible up close and inside Whiterun)
- [x] Enable-parent chains resolved; refs that start disabled stay out of static LOD
- [ ] Use DynDOLOD's grid/reference/flags columns (Near/Far LOD, VWD) when dynamic LOD arrives
- [ ] Weld duplicate vertices, drop degenerate triangles

## Milestone 2 — Textures (replaces TexGen + DynDOLOD atlas)
- [ ] GPU backend (Vortice D3D12 or Silk.NET Vulkan); CPU fallback
- [ ] Gather LOD textures per worldspace, downscale, pack atlas pages, BC7 on GPU
- [ ] UV remap in the mesh stage (tiled UVs → keep as separate shape or bake)
- [ ] PBR pages (normal + RMAOS) for Community Shaders / True PBR

## Milestone 3 — Trees (replaces TexGen billboards + tree LOD)
- [x] `.lst` + `.btt` output (engine billboard tree LOD) from TexGen/LODGen billboards (`textures\terrain\lodgen\<plugin>\<model>_<formid>.dds/.txt`)
- [x] BC7 atlas: BC7 billboards copied block-for-block; mips with alpha-weighted filtering and alpha-coverage preservation; managed BC7 encoder (modes 5/6) and full BC1–BC7 decoder
- [x] Runtime FormIDs (full and light plugins), rotation 2π − z, Z + ShiftZ × scale; vanilla `.btt` blocks we don't replace are emptied
- [x] Incremental per worldspace (trees, billboards and vanilla blocks fingerprinted)
- [x] In-game check: placement/height, LOD hidden when the tree loads, coverage at distance
- [ ] Hybrid 3D tree LOD in object LOD (trunk meshes + billboard crowns), DynDOLOD tree rules
- [ ] Render tree billboards from full tree models on the GPU (all angles, normals) so TexGen isn't needed

## Milestone 4 — Grass LOD
- [x] Read `.cgid` caches (NGIO / FasterNGIO): format reverse-engineered (halves wrapped every 49152 units, unwrapped with the chunk centre)
- [x] Grass types matched to GRAS through the runtime FormID (falls back to the model if the load order changed), TexGen grass billboards (`_1.dds` + `_n` + `.txt`)
- [x] One reference per cached cell, built on the fly into one quad per kept tuft; density (default 8 %) and size; incremental per cell
- [ ] Seasonal caches (`.WIN/.SPR/.SUM/.AUT.cgid`) with milestone 5
- [ ] Grass LOD in LOD8, brightness/colour matching against terrain LOD
- [ ] Grass billboards from our own GPU renderer

## Milestone 5 — Seasons
- [ ] Read Seasons of Skyrim swap INIs; generate per-season block sets with the seasonal file names (verify against real seasonal output first)
- [ ] Seasonal grass caches and seasonal billboards
- [ ] Test list: small dedicated seasons profile

## Milestone 6 — Dynamic LOD (SKSE)
- [x] Switchable references (initially disabled, parent chain starts disabled, "opposite of parent" switches such as Helgen Reborn's destroyed/rebuilt sets) leave static LOD and go to `SKSE\Plugins\AnvilLOD\AnvilLOD.dyn`
- [x] SKSE plugin v0.1 (`skse/`, CommonLibSSE-NG + Address Library → one DLL for SE/AE/VR): draws their LOD meshes outside the loaded cells while the reference (or its enable parent) is enabled; settings in `AnvilLOD.ini`
- [ ] In-game check (Helgen Reborn before/after the rebuild), then performance (draw calls) with many dynamic objects
- [x] Trees from light (ESL) plugins: the engine never hides their `.btt` LOD, so they go into object LOD as crossed billboard cards (hidden per cell)
- [ ] Hide tree LOD for trees that scripts disable at runtime
- [ ] `AnvilLOD.esm` generator: far-visible references (large refs, glow windows, smoke/flags)
- [ ] Grid objects from DynDOLOD's Grid column (`Near LOD` / `Far LOD` with empty LOD4–16 columns): the full, animated model drawn by the SKSE plugin beyond the loaded cells — waterfalls, rapids, windmill fans and rotors, water wheels, fires, ships, birds (BIRDS rules), Sovngarde statues. ~35 masks in DynDOLOD's own high rules plus mod rules
- [x] Rule files a mod left in the Data root (e.g. BIRDS' `DynDOLOD_SSE_BIRDSesl.ini`) are read too; `Configs\DynDOLOD_SSE_mesh_lookup.txt` aliases models to the LOD names they should have had
- [ ] DynDOLOD `.patch` files (record edits at generation time: Desync Birds of Prey flags, High Hrothgar window glow) — only meaningful once AnvilLOD writes its own plugin
- [ ] Child worldspace LOD and Occlusion.esp

## Mod author tools
- [x] Per-plugin report: objects/trees placed in LOD worldspaces with no LOD, broken MNAM, missing LOD textures, empty models, missing billboards
- [x] LOD mesh generation (QEM simplification, seam-aware), billboard rendering (software rasterizer, BC7, coverage-preserving mips), DynDOLOD-format rule file
- [ ] Billboard normal maps from the model (now flat), multiple views (_2), grass billboards
- [ ] Optional atlas of the generated LOD textures

## Open questions
- Archive priority: confirm a mod BSA that overrides a vanilla mesh wins.
- Persistent refs: confirm they're found through the worldspace persistent cell.
