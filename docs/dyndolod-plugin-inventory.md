# DynDOLOD plugin inventory (2026-10-11)

Measured on the DynDOLOD 3 output of the Tabula Rasa test list (`S DynDOLOD_Output`: `DynDOLOD.esm` 4.1 MB, `DynDOLOD.esp` 1.2 MB), read with Mutagen overlays. Purpose: size the job of putting AnvilLOD's own real references (waterfalls, windmills, wheels, fires, ...) in `AnvilLOD.esm`, so the engine animates them itself.

## Why real references

A model attached to the scene by an SKSE plugin is never updated by the engine: the controllers of a cloned model keep start and last time at -FLT_MAX for the whole session. Windmill blades (transform sequences) and waterfall flow (shader controllers) stay still. In game, DynDOLOD's windmill fan and waterfall pieces are real placed objects, the engine animates them, and they keep moving at distance.

- Windmill fan: ref `skyrimesm&0C8E59_Tamriel_DynDOLOD_REFERENCE` (DynDOLOD.esp, a copy of Skyrim.esm ref 0C8E59), base `FarmhouseWindMillFan_DynDOLOD_BASERECORD`, **MSTT** (Movable Static), model the vanilla `FarmhouseWindMillFan.nif`.
- Far waterfall: ref `skyrimesm&01AC8E_Tamriel_DynDOLOD_REFERENCE`, base `FXWaterfallBodySlope_static_DynDOLOD_BASERECORD`, **STAT**, model `dyndolod\lod\effects\fxwaterfallbodyslope_68cc9984_dyndolod_lod.nif` (a hash-suffixed copy of the `_dyndolod_lod` effect mesh).
- Vanilla rapids rocks `FXrapidsRocks01` (Skyrim.esm, MSTT) are real references the engine loads at distance on its own.
- A STAT base is enough for shader (UV-scroll) animation; MSTT is what makes a model's NiControllerManager sequences play (blades, wheels, fires).

## Numbers

| | count |
|---|---|
| `DynDOLOD.esm` base records | 849 STAT + 92 MSTT (+ 34 ACTI, 28 TREE, 30 TXST, 3 CONT, 3 DOOR, 2 FURN ...) |
| `DynDOLOD.esm` other | 21,285 placed objects, 1,362 cells, 19 worldspaces (the Parent > Child copies, NoLodWater / NoGrass worldspace flags) |
| `DynDOLOD.esp` placed objects | 6,037 (4,798 on DynDOLOD base records, the rest on vanilla / CC / Octos bases) |
| distinct DynDOLOD bases used by those refs | 633 (540 STAT, 92 MSTT) |
| refs in Tamriel | 3,442 on DynDOLOD bases (2,306 STAT, 858 MSTT, 278 other) |
| refs elsewhere | Japhet's Folly 257, Deadlands 264, Soul Cairn 180, Solstheim 288, Blackreach 127, Sovngarde 45, Hunter HQ 42, Falmer Valley 41, Deepwood Redoubt 23, Apocrypha 16 |
| `DynDOLOD.esp` large reference data (WRLD RNAM) | none (0 in every worldspace) |

By kind (base editor id): fires 771, candles/lanterns 583, waterfalls 364, lava 122, other FX 115, streams 91, torches 88, water 52, ships 29, windmills 12 (9 fans + 3 glow refs), water wheels 11 (7 + 1 + 3). `FarmhouseWindMillFan`, `SolitudeRotor`, `FarmLumbermill01WaterWheel01/02`, `SSawWaterWheel`, the fire effects and `MarkarthWaterfallCliff02/03` are MSTT; waterfall bodies and skirts are STAT.

Reference flags on the DynDOLOD refs: **0x40010C00** for 4,775 of 4,798 = Persistent (0x400) + Initially Disabled (0x800) + Is Full LOD (0x10000) + 0x40000000. No "Visible When Distant" (0x8000). They are enabled by script at run time (`DynDOLOD.esp` has two quests, `DynDOLOD_Quest` with script `SHESON_DynDOLOD_Quest` and the MCM quest; the NG DLL does the same job when installed).

Other records in `DynDOLOD.esp`: 17 STAT, 6 ACTI (`DynDOLOD_MasterActivator`, `DynDOLOD_ObjectActivator`, hawk effects, two CS Water Mod water bases), 8 FormLists (`*_UNDERSIDE_DynDOLOD_LIST`).

## What it means for AnvilLOD.esm

- Budget: ~4,800 references and ~630 base records (waterfalls only: ~364 refs and ~20 bases). An ESL holds 2,048 records, so the full set does **not** fit one ESL: a regular ESM (or several ESLs) for everything, one ESL for a waterfalls / windmills / wheels subset.
- Bases: STAT with the `dyndolod\lod\...` mesh; MSTT where the model animates through sequences (fan, rotor, wheels, fire effects).
- References: copies of the vanilla placements (persistent, Initially Disabled, Is Full LOD), enabled by the SKSE plugin by distance (the grid column: Near / Far / Never fade) in place of DynDOLOD's script.
- Meshes: DynDOLOD Resources' `dyndolod\lod\...` until AnvilLOD has its own.
- Open: how the engine loads the 3D of these persistent, initially disabled refs at distance once enabled (Is Full LOD, `uLargeRefLODGridSize`); the plugin must reproduce whatever DynDOLOD does there. The reference list is also what the plugin's own `AnvilLOD.dyn` already carries for the same objects (3,891 grid objects in 14 worldspaces).

The probe that produced this lives in the session scratchpad (Mutagen overlay of both plugins); it can be rebuilt from this description.
