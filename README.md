# purr2d2

Lightsabers for Kitten Space Agency: an authored hilt held in an EVA kitten's paw, extending/retracting blade, colored illumination, spatial sound, and contact-driven vessel cutting.

Targets **KSA 2026.10.19.5638**, **StarMap.API 0.3.6**, and the loader's **Harmony 2.4.2**. This is an initial implementation with managed build/asset checks; native in-game acceptance is tracked in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).

## Controls

Take control of an EVA kitten, equip the saber, then ignite it. Use walking/EVA flight or **Swing** to bring the blade into contact with a vessel.

| Control | Action |
|---|---|
| F7 | Open/close the saber panel |
| Ctrl+F7 | Equip/unequip |
| Shift+F7 | Ignite/retract |
| Alt+F7 | Swing |
| Panel buttons | The same actions without hotkeys |

Hotkeys are ignored while ImGui captures keyboard input. The simulation and effects continue when the game HUD is hidden; the panel follows the HUD's visibility. Pause suspends blade motion, cutting and sound. Return to 1× speed to use the saber. Ladder climbing, swimming, tumbling and righting require the kitten's stock pose and stow the saber.

The panel controls color, blade length, volume, contact time and cutting mode. **Save preferences** writes `settings.json` beside the installed mod; updates should preserve that file. Equipping is intentionally scoped to the currently controlled EVA kitten and is cleared on switching, boarding, loading a save or entering the editor.

## Cutting behavior

The blade tests a bounded sweep against the actual CPU mesh triangles. Sustained contact heats a surface and produces a glowing track that cools into a temporary dark scar. Geometry and exposure budgets keep large scenes bounded. Off, paused, stale, distant and discontinuous sweeps cannot accumulate damage.

- **Default mode:** separates an eligible existing full-part connection using native `Vehicle.Split`. The severed subtree remains a valid native craft/debris object. Reinforced or unsupported connection graphs receive surface marks without being forcibly corrupted.
- **Fragment struck part:** an optional native failure path isolates and destroys the contacted full part without adding an explosion, then sheds up to two of its authored subparts as debris. Crewed vessels and unsupported custom subpart connection graphs are excluded. This is destructive to the entire struck part, including portions not retained as native debris.
- **Arbitrary plane slicing (experimental):** actually clips the source triangles, interpolates their normals/UVs, retains material groups, and seals the new surfaces. The blade and relative swing define the cut plane. Closed inert parts become independently rendered native debris with generated colliders, volume-weighted mass, principal-axis inertia and conserved momentum. Hot cut faces cool into persistent dark caps. A rejected slice reports its reason and does not fall back to destroying the part.

Arbitrary slicing accepts consistently wound closed native meshes within the geometry budget. Overlapping closed authored solids are clipped independently and retained as two rigid halves; their volumes are additive, not a Boolean union. Open render skins, unsupported functional modules, crew, deformed meshes and reinforced connection graphs are rejected. It cuts a whole supported part along a plane; it does not simulate a progressively carved curved kerf. Caps support concave profiles and holes. **Collision uses an enclosing convex hull per fragment**, so a visible concavity or hole can still block another object. The mode is opt-in while native acceptance is pending.

The Structural editor category includes **Purr2D2 Practice Billet** and **Purr2D2 Practice Hollow Tube**, original positive-mass targets for repeatable testing. Native Aeroshroud BA/BB geometry also passes the offline plane-cut checks; see [native asset evidence and exact template IDs](docs/research-native-assets.md). Passing offline geometry checks does not establish native gameplay or save/load correctness.

Generated fragment GLBs/XML live under **`Generated/Slices/` in the installed mod directory**. Keep that folder when updating the mod and copy it alongside any save containing sliced parts: native saves store generated template IDs, not their geometry. These immutable assets load before native save reconstruction. Existing entries are retained for other saves; budgets can refuse further slices. Saved cut faces reload cold. Ordinary surface scorch ribbons remain transient. See [cutting research](docs/research-cutting.md) and [arbitrary slicing research](docs/research-slicing.md).

## Build

Requires the .NET 10 SDK and an authorized matching set of KSA DLLs. Game and loader DLLs are compile inputs and are never included in the mod package.

The default discovers `../ksa-game-assemblies/current/dll/`. Alternatively set `KSA_DLL_DIR`, pass `-p:KSAFolder=/absolute/path/to/dll`, or copy `Local.Build.props.example` to `Local.Build.props` and fill in your path. StarMap.API and Harmony are pinned compile-only NuGet dependencies.

```sh
dotnet build Purr2D2.slnx -c Release
python3 tools/validate.py --run-tests
dotnet msbuild Purr2D2.csproj -t:StageMod -p:Configuration=Release
python3 tools/validate.py --stage artifacts/stage/purr2d2
python3 tools/package.py
```

`StageMod` rebuilds one allowlisted directory, `artifacts/stage/purr2d2/`. It never installs the mod or changes game saves. The ZIP under `artifacts/dist/` contains one `purr2d2/` folder. Extract that folder into your configured KSA/StarMap mod directory and enable the `purr2d2` mod ID. A game restart is needed. Preserve existing `settings.json` and `Generated/Slices/` when updating.

This repository contains exactly one runtime `.csproj`; validation generates temporary game-free harness projects under ignored `artifacts/`. The solution and all mod code target `net10.0`.

## Assets and implementation

- `Assets/Models/` and `Assets/Textures/`: original hilt and PBR maps, generated by `tools/create_hilt_portable.py`; inspected preview and metadata under `art/`. The optional Blender importer/studio script is `tools/create_hilt.py`; no `.blend` is included because Blender could not start in this environment.
- `Assets/Shaders/`: depth-tested HDR core/halo and cooling scar rendering. Native point lights illuminate nearby geometry. Flexo's native emissive **PartModel** approach is also documented in [rendering research](docs/research-rendering.md).
- `Assets/Audio/`: user-supplied ignition/idle recordings prepared for mono spatial playback, plus generated supplementary retraction/contact audio. See [audio provenance and preparation](docs/research-audio.md).
- `src/Pose/`: scoped right-arm IK overlay; preserves stock root/leg animation and derives the emitter from the live paw skeleton.
- `src/Runtime/`: one StarMap entry/controller; a checked `PrepareFrame` insertion after applied input and before new workers ensures structural mutations happen safely. It leaves `GetJobSimStep` available to other mods' wrappers.

Start with the [implementation plan](docs/IMPLEMENTATION_PLAN.md), then [compatibility map](docs/COMPATIBILITY.md) and [acceptance record](docs/ACCEPTANCE.md). Source code is MIT; supplied audio provenance is recorded separately.
