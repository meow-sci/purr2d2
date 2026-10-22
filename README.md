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

Hotkeys are ignored while ImGui captures keyboard input. The simulation and effects continue when the game HUD is hidden; the panel follows the HUD's visibility. Pause suspends blade motion, cutting and sound. The saber and autopilot support up to **5× timewarp** with applied steps up to 0.25 seconds. The autopilot panel includes **1× / 2× / 5×** controls and returns to 1× on completion or abort. Ladder climbing, swimming, tumbling and righting require the kitten's stock pose and stow the saber.

The panel controls color, blade length, volume, contact time and cutting mode. **Save preferences** writes `settings.json` beside the installed mod; updates should preserve that file. Equipping is intentionally scoped to the currently controlled EVA kitten and is cleared on switching, boarding, loading a save or entering the editor.

## EVA rendezvous and ring targeting

Open **Cylinder cutting autopilot** in the F7 panel:

1. Enable MMU and choose a vessel from **Vessel target**. This sets the kitten's actual flight-computer target.
2. Press **Approach ring**. It finds a valid ring if none is selected, equips and aligns the saber, flies to the marked entry and holds. The scene shows a **cyan cut ring**, **amber flight circle**, and **MMU entry** marker.
3. Once holding, **Cut ring** becomes available. Inspect the position, then press it to ignite and cut a lap with a small overlap at the seam. Speed automatically reduces to suit the available MMU thrust. **Flight around ring** and **Cut contact coverage** report separate progress. **Abort / release** or **Esc** returns manual control and restores the previous flight-computer and saber settings.

In experimental slicing mode, the first contact validates the part and selected plane before continuing the lap. Actual blade contact accumulates around that fixed plane, including across cooling or a pause. A complete contacted ring triggers the structural slice; an incomplete or refused cut reports its reason instead of treating a flown lap as success. Rings crossing multiple full parts are refused. Fuel tanks and empty capsules can become cut wreckage. The first-contact check also verifies float32 export and fragment geometry limits; occupied vessels and geometry that cannot form valid fragments report a reason.

To choose the station yourself, use **Select hull point** before approaching. **Ring setup (optional)** contains axis/clearance controls, preview-only search, **Ring −/+0.25 m**, the maximum cut-speed setting and standalone **Match target velocity**. **Saber orientation (optional)** offers ±90° roll adjustments. The far side of the preview ring is dashed; the overlay is not a depth-tested cut. **Copy autopilot diagnostics** reports thrust, target spin and planned cut speed if guidance refuses or stalls.

Finding a ring no longer requires the kitten to be beside the correct cross-section. The search reads rendered hull geometry, including transformed subparts, samples the circumference, and checks the direct approach and whole flight circle for clearance. A clicked point fixes the slice station; an invalid selection explains why it failed. A failed nudge retains the previous preview. The preview does not guarantee that the selected cutting mode can structurally cut that part.

This is local EVA guidance: the target must share the kitten's parent body, be loaded in the physics bubble, and be within 500 m of its bounds with less than 20 m/s relative speed. Approach speed is limited and reduced using available MMU pulse thrust. Fast target spin, depleted thrusters, changed geometry, blocked paths and unsupported simulation speed stop or suspend guidance. It does not plan orbital transfers or routes around obstacles. Sampled rings assume a locally cylindrical hull and a 0.25 m kitten clearance volume; other vessels are not included in path checks. Native in-game calibration remains required. See [autopilot research and limits](docs/research-autopilot.md).

## Cutting behavior

The blade tests a bounded sweep against the actual CPU mesh triangles. Sustained contact heats a surface and produces a glowing track that cools into a retained dark scar. Adjacent short marks are combined so a slow ring cut keeps its beginning visible. Marks remain until world/reset cleanup, source removal or the bounded oldest-mark eviction; they are not saved. Geometry and exposure budgets keep large scenes bounded. Off, paused, stale, distant and discontinuous sweeps cannot accumulate damage.

Contact includes crewed vessels and their parts/subparts, including native part-model surfaces without a separate picking mesh. Crew and physics restrictions apply when attempting a structural cut; they do not hide contact, scorch marks or contact audio. The panel shows touching-part and heat counters independently of the cut result. If a visible touch still misses, expand **Contact diagnostics** and use **Copy contact diagnostics** while holding the blade against the surface; this copies the geometry counters, blade endpoints and nearest-vessel information, and writes the same snapshot to the game log.

- **Default mode:** separates an eligible existing full-part connection using native `Vehicle.Split`. The severed subtree remains a valid native craft/debris object. Reinforced or unsupported connection graphs receive surface marks without being forcibly corrupted.
- **Fragment struck part:** an optional native failure path isolates and destroys the contacted full part without adding an explosion, then sheds up to two of its authored subparts as debris. Crewed vessels and unsupported custom subpart connection graphs are excluded. This is destructive to the entire struck part, including portions not retained as native debris.
- **Arbitrary plane slicing (experimental):** actually clips the source triangles, interpolates their normals/UVs, retains material groups, and seals the new surfaces. The blade and relative swing define the cut plane. Closed solids and backed open hull panels become independently rendered native wreckage with generated colliders, volume-weighted mass, principal-axis inertia and conserved momentum. Sparks and hot cut rims cool into persistent dark caps; a gentle separating impulse opens the seam. A rejected slice reports its reason and does not fall back to destroying the part.

Arbitrary slicing accepts closed native meshes and repairs supported open render sheets with a 4 mm backing. Exterior UVs/materials and glass render layers are retained. Blade length can be set from **0.25 to 12 metres**. An explicit **Swing** slices after brief moving contact; a stationary blade uses the configured contact delay. Overlapping closed authored solids are clipped independently and retained as two rigid halves; their volumes are additive, not a Boolean union. Tank contents are emptied only after the cut part is isolated; neighboring tanks keep their contents. Parts lose their functional modules when replaced by wreckage. Attached capsule IVA furnishings are discarded with the original part, with their dry mass included in the fragments. Occupied vessels, unbakeable dents, deployed parachutes, self-intersecting cut contours and unsupported connection graphs are still refused. It cuts a whole supported part along a plane; it does not simulate a progressively carved curved kerf. Caps support concave profiles and holes. **Collision uses an enclosing convex hull per fragment**, so a visible concavity or hole can still block another object. The mode is opt-in while native acceptance is pending.

The Structural editor category includes **Purr2D2 Practice Billet** and **Purr2D2 Practice Hollow Tube**, original positive-mass targets for repeatable testing. Native Aeroshroud BA/BB geometry also passes the offline plane-cut checks; see [native asset evidence and exact template IDs](docs/research-native-assets.md). Passing offline geometry checks does not establish native gameplay or save/load correctness.

Generated fragment GLBs/XML live under **`Generated/Slices/` in the installed mod directory**. Keep that folder when updating the mod and copy it alongside any save containing sliced parts: native saves store generated template IDs, not their geometry. These immutable assets load before native save reconstruction. Existing entries are retained for other saves; budgets can refuse further slices. Saved cut faces reload cold. Ordinary surface scorch ribbons remain transient. See [cutting research](docs/research-cutting.md) and [arbitrary slicing research](docs/research-slicing.md).

## Build

Requires the .NET 10 SDK and an authorized matching set of KSA DLLs. Game and loader DLLs are compile inputs and are never included in the mod package.

The default discovers `../ksa-game-assemblies/current/dll/`. Alternatively set `KSA_DLL_DIR`, pass `-p:KSAFolder=/absolute/path/to/dll`, or copy `Local.Build.props.example` to `Local.Build.props` and fill in your path. StarMap.API and Harmony are pinned compile-only NuGet dependencies.

```sh
dotnet build Purr2D2.slnx -c Release
```

The Release build produces the installable folder `artifacts/stage/purr2d2/`, `artifacts/dist/purr2d2-0.2.0.zip`, and its `.zip.sha256` checksum using built-in MSBuild tasks. No packaging scripts or Python installation are required. The archive name follows the project version; `mod.toml` must match that version or the build fails. Add `-p:CreateModZip=false` for just the folder. Debug builds compile without changing the Release distribution; `dotnet clean Purr2D2.slnx -c Release` removes its generated folder, ZIP and checksum.

Every Release build recreates the folder from the project's explicit file set, so stale files and host DLLs cannot survive a rebuild. The ZIP contains one `purr2d2/` folder. Extract that folder into your configured KSA/StarMap mod directory and enable the `purr2d2` mod ID. A game restart is needed. Preserve existing `settings.json` and `Generated/Slices/` when updating.

Optional development checks are separate from building the distribution:

```sh
python3 tools/validate.py --run-tests --stage artifacts/stage/purr2d2
```

This repository contains exactly one runtime `.csproj`; validation generates temporary game-free harness projects under ignored `artifacts/`. The solution and all mod code target `net10.0`.

## Assets and implementation

- `Assets/Models/` and `Assets/Textures/`: original hilt and PBR maps, generated by `tools/create_hilt_portable.py`; inspected preview and metadata under `art/`. The optional Blender importer/studio script is `tools/create_hilt.py`; no `.blend` is included because Blender could not start in this environment.
- `Assets/Shaders/`: depth-tested HDR core/halo and cooling scar rendering. Native point lights illuminate nearby geometry. Flexo's native emissive **PartModel** approach is also documented in [rendering research](docs/research-rendering.md).
- `Assets/Audio/`: user-supplied ignition/idle recordings prepared for mono spatial playback, plus generated supplementary retraction/contact audio. See [audio provenance and preparation](docs/research-audio.md).
- `src/Pose/`: scoped right-arm IK overlay; preserves stock root/leg animation and derives the emitter from the live paw skeleton.
- `src/Runtime/`: one StarMap entry/controller; a checked `PrepareFrame` insertion after applied input and before new workers ensures structural mutations happen safely. It leaves `GetJobSimStep` available to other mods' wrappers.
- `src/Autopilot/`: target-relative cylinder guidance, native EVA MMU pulse commands, custom flight-computer attitude control and full state restoration.

Start with the [implementation plan](docs/IMPLEMENTATION_PLAN.md), then [compatibility map](docs/COMPATIBILITY.md) and [acceptance record](docs/ACCEPTANCE.md). Source code is MIT; supplied audio provenance is recorded separately.
