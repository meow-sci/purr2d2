# purr2d2

Lightsabers for Kitten Space Agency: an authored hilt held in an EVA kitten's paw, extending/retracting blade, colored illumination, spatial sound, one-click part slicing and cosmetic contact sparks.

Targets **KSA 2026.10.19.5638**, **StarMap.API 0.3.6**, and the loader's **Harmony 2.4.2**. This is an initial implementation with managed build/asset checks; native in-game acceptance is tracked in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).

## Controls

Take control of an EVA kitten beside a vessel. **Swing / Alt+F7** performs the original ordinary saber swing. To break up a part, press **Select part to slice** (or **Ctrl+Alt+F7**), point at that part, and click. A large marker names the part under the pointer: green for an eligible point in reach, red with a reason otherwise. The action equips and ignites the saber, swings across the selected part, and replaces it with textured pieces, sparks and cooling cut edges. No contact history or ring flight is required. Touching a part produces **visual sparks only**; destruction happens only through an explicit slice.

**Show saber overlay** opens three frameless, half-transparent controls. The top-right list equips and ignites each checked EVA kitten independently, including kittens you are not controlling. Hold and drag inside the black **SWING** circle to move the controlled kitten's paw and saber; the circle stays fixed and releasing smoothly restores the starting pose. The smaller red **SLICE** circle opens the part picker. The overlay remains visible when the settings panel closes; **Hide saber overlay** hides it without stowing anyone's saber.

The **0.62-second** swing starts as soon as the selected part is locked and the blade is ignited, without waiting for geometry preparation. Cut geometry is calculated once on a background worker. The full swing plays before breakup; ready pieces separate on the next safe update after its final frame. If preparation takes longer, normal flying and ordinary swings remain available while the pieces finish. Even a preparation failure lets the full animation finish before reporting the error. Target reading and GPU uploads are spread across frames instead of blocking on the entire cut. Slice diagnostics include preparation and final breakup timings.

Selection uses the part under the pointer only when the **selected surface point is within the visible blade's length of its hilt**. There is no minimum reach, extra distance allowance or automatic blade extension. A partially extended blade has only its current visible reach. Move closer or increase the blade-length setting to reach farther. Empty space never selects a nearby substitute. Native hover picking is reused; geometry without a native hover hit uses a bounded ray search after clicking. **Vessel target** sets the actual flight-computer target independently of slicing.

| Control | Action |
|---|---|
| F7 | Open/close the saber panel |
| Ctrl+F7 | Equip/unequip |
| Shift+F7 | Ignite/retract |
| Alt+F7 / Swing | Ordinary saber swing; cosmetic contact sparks |
| Ctrl+Alt+F7 / Select part to slice | Point and click a part to swing and slice it |
| Cancel slice / Esc | Cancel before breakup |
| Right-click while selecting | Cancel part selection |

Reach is checked when selecting the part and again before the stroke starts, using the live hilt position. Moving away or changing the flight-computer target after a valid stroke does not undo it. Switching kittens, boarding, loading, stowing or cancelling clears the pending action. The animation waits while its main-view pose is hidden; once the complete stroke has been shown, looking away does not delay a ready breakup. The action does not steer the kitten or modify the flight computer's attitude controls. Autopilot, ring tracking, contact heat and automatic contact cutting have been removed.

Hotkeys are ignored while ImGui captures keyboard input. The effects continue with the HUD hidden. Pause suspends blade motion, contact emission and sound. Supported simulation speeds extend to **5×**; ignition and swings run at normal player-time speed, independently of physics-step timing. Contact probing skips applied steps above 0.25 seconds. Ladder climbing, swimming, tumbling and righting stow the saber.

The panel groups color, blade length (**0.25–12 m**), volume and paw alignment. These preferences apply to all equipped sabers; overlay drags never change saved alignment. **Save preferences** writes `settings.json` beside the installed mod. Old contact/autopilot preferences are ignored. Equipped sabers remain with their kittens when control switches; boarding, loading or entering the editor releases their resources. Overlay visibility and per-kitten equip choices last for the current session.

## Slicing and effects

The slice clips the actual native mesh triangles, preserves UVs/materials and glass layers, and seals new surfaces. Supported open hull panels receive a thin backing. The resulting native wreckage has generated colliders, volume-weighted mass, principal-axis inertia and conserved momentum. Hot cut rims cool into persistent dark caps; a gentle separating impulse opens the seam. Geometry failures report a reason and leave the original intact.

Fuel tanks, empty capsules and service bays/modules are supported by this path. Fuel is emptied only from the isolated source part. Functional modules and attached capsule IVA furnishings are discarded; their dry mass contributes to the wreckage. Occupied vessels, unbakeable dents, deployed parachutes, invalid cut contours and unsupported connection graphs are refused. This is a whole-part plane cut. Collision uses an enclosing convex hull per fragment, so visible recesses or holes can still block objects.

Contact sparks use a separate read-only probe: at most 20 samples per second, a bounded mesh/triangle search, reused storage and one triangle recheck for a sustained touch. There is no heat accumulation, coverage record, scar history or repeated slicing attempt in the contact loop. The Structural editor category includes **Purr2D2 Practice Billet** and **Purr2D2 Practice Hollow Tube** for repeatable testing.

Generated fragment GLBs/XML live under **`Generated/Slices/` in the installed mod directory**. Keep that folder when updating and copy it alongside saves containing sliced parts: native saves store template IDs, not geometry. Existing entries are retained for other saves; budgets can refuse further slices. Saved cut faces reload cold. See [cutting architecture](docs/research-cutting.md), [slicing details](docs/research-slicing.md) and [acceptance status](docs/ACCEPTANCE.md).

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
python3 tools/check_fragment_templates.py
```

This repository contains exactly one runtime `.csproj`; validation generates temporary harness projects under ignored `artifacts/`. The fragment-template check uses the actual managed KSA XML types without starting graphics or physics. On ARM hosts it adapts only the PE machine fields in an ignored copy of the unsigned IL-only game assembly; the original game files and mod distribution are unchanged. The solution and all mod code target `net10.0`.

## Assets and implementation

- `Assets/Models/` and `Assets/Textures/`: original hilt and PBR maps, generated by `tools/create_hilt_portable.py`; inspected preview and metadata under `art/`. The optional Blender importer/studio script is `tools/create_hilt.py`; no `.blend` is included because Blender could not start in this environment.
- `Assets/Shaders/`: depth-tested HDR core/halo and spark rendering. Native point lights illuminate nearby geometry. Flexo's native emissive **PartModel** approach is also documented in [rendering research](docs/research-rendering.md).
- `Assets/Audio/`: user-supplied ignition/idle recordings prepared for mono spatial playback, plus generated supplementary retraction/contact audio. See [audio provenance and preparation](docs/research-audio.md).
- `src/Pose/`: scoped right-arm IK overlay; preserves stock root/leg animation and derives the emitter from the live paw skeleton.
- `src/Runtime/`: one StarMap entry/controller; a checked `PrepareFrame` insertion after applied input and before new workers ensures structural mutations happen safely. It leaves `GetJobSimStep` available to other mods' wrappers.
- `src/Cutting/`: on-demand target selection, automatic slice-plane planning and validated native mesh replacement.

Start with the [implementation plan](docs/IMPLEMENTATION_PLAN.md), then [compatibility map](docs/COMPATIBILITY.md) and [acceptance record](docs/ACCEPTANCE.md). Source code is MIT; supplied audio provenance is recorded separately.
