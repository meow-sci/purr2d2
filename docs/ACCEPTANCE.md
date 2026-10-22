# Validation and native acceptance

This file separates automated checks from native gameplay observation. An unperformed native check remains unperformed even when the DLL builds successfully.

## Built-in vessel target limits repair — 2026-10-22

The user reported "The target exceeds the autopilot part budget" on a built-in vessel. The 256-node guard counted full parts plus subparts, including internal/decorative components. The supplied default-vehicle XML lists 1,032 nodes for Rocket (161 full parts) and 664 for Gemini7 (35 full parts). A read-only count using the available older native GLB atlas headers resolves at least 365,224 render triangles for Rocket and 247,688 for Gemini7; these are indicative asset counts, not a live 5638 runtime measurement. Both also exceed the former 150,000-triangle snapshot guard, and Rocket exceeds the former 64-bound ring-search guard.

Shared limits now permit 32,768 nodes/mesh instances, 131,072 primitives and 4 million triangles through capture, picking and ring search. Search ranks nearby/clicked parts first, keeps complete obstacle geometry and retains an independently validated ring if later discovery work reaches its limit. Flight reuses the validated search BVH. The search/route work limits were also increased; parent-cycle protection stays independent of vessel size. See [research-autopilot.md](research-autopilot.md) for exact limits and limitations.

Release build passed with zero warnings/errors. The full managed validation passed, including a 524,288-triangle/2,176-node snapshot, picking/search above 400,000 triangles, 1,701 candidate parts, blocked paths, retained remote obstacles, atomic size/hierarchy refusals and the existing flight-controller checks. Output is in ignored `artifacts/large-target-validation.log`. These fixtures exercise production code with game stand-ins; native built-in-vessel capture and flight must still be retested after replacing the mod and restarting KSA.

## EVA rendezvous and ring targeting update — 2026-10-22

Implemented the vessel-target dropdown, render-geometry ring search, exact clicked-station selection, ring/flight-path/entry overlay, station nudges, local approach and match-velocity controls. Fixed the former capture path's subpart coordinate mistake and dependence on MeshView-only raycasts. Coupled roll now holds the complete blade orientation.

Validation for this update is recorded separately from native gameplay: geometry and pulse-guidance tests exercise production math; snapshot tests exercise production extraction with game stand-ins; controller tests exercise production state transitions and MMU flags with stand-ins. A host build verifies the actual KSA API signatures, and metadata inspection covers the new camera-frame and mouse-input hooks. These checks do not execute KSA's native renderer, thruster solver or input loop.

Final update checks passed: Release build (zero warnings/errors), production geometry and snapshot checks, pulse-guidance checks, production controller checks (10 m/100 m approaches and unequal thrust, abort/reset/failure restoration, station retention, coupled-roll selection and ignition gates), current-host metadata/IL checks, source/stage validation, ZIP CRCs and SHA-256. The initial controller test found premature stopping-corridor rejection after a full native pulse; the speed schedule now reserves more braking margin than the collision stopping envelope, and the affected core/controller suites were rerun successfully. Contact tests passed in the broader validation run and were unchanged by that correction.

Final logs are under `artifacts/eva-core-validation.log`, `artifacts/eva-controller-validation.log` and `artifacts/eva-host-validation.log`. These are ignored local validation artifacts; the native acceptance status below remains pending.

Native checks still required:

- Select and clear vessels from the dropdown; confirm the stock FC target follows. Switch EVA owner or load a save with queued commands and confirm no stale target/selection is applied.
- Find rings from beyond both vessel ends and from 10–100 m away; test native render-only hulls and translated/rotated/scaled subparts. Confirm explicit refusals on non-cylindrical or blocked geometry.
- Click the visible hull at a desired station, nudge both ways, reject an invalid nudge while retaining the preview, and compare overlay/entry alignment at different resolutions, DPI scales and camera angles. Selection clicks must not also change stock targets; Esc must cancel selection.
- Approach the marked entry with initial drift, inspect coupled roll/±90° alignment, hold, then explicitly ignite for a lap. Check physical MMU impulse behavior and conservative arrival clearance in KSA.
- Match velocity without equipping a saber. Abort during approach/hold/cut, pause and resume, enter warp, lose target/MMU/propellant, switch owner, and change target geometry. Confirm no persistent translation or unrequested ignition.
- Test slow target rotation and refuse spin/orbit demands beyond available authority. Approach checks cover the target hull only; nearby unrelated vessels require player clearance.

## Contact detection repair (2026-10-22)

The user reports that the saber, character animation and sound work in KSA, but vessel contact stays at "Blade clear". Source inspection found two definite faults: the contact scan discarded every vessel with assigned crew, and the status reset to clear on each step but only changed to heating when a new scar was emitted (at most once every 40 ms).

The repair allows geometric contact independently of crew, physics-bubble and parent-body restrictions; those restrictions remain on structural changes. It also scans native PartModel surfaces without MeshView modules, retries unavailable CPU mesh data, and reports contact on every touching step. Contact counters and a copyable diagnostics snapshot are available in the saber panel. The production detector is exercised in an isolated managed harness using game stand-ins; this does not execute the native KSA renderer or physics.

Retest a crewed vessel and an uncrewed practice target after updating and restarting KSA. A touch should register a nonzero touching-part count, accumulate heat and produce contact effects. A crewed vessel should report its structural-cut refusal without losing contact. On an uncrewed supported target, verify each cutting mode separately. If a visible touch still misses, hold the blade against it and copy **Contact diagnostics**. Live acceptance of this repair remains pending user feedback.

## Automated checks

Completed on 2026-10-22 with .NET SDK 10.0.402 on macOS ARM64, against the supplied KSA 2026.10.19.5638 assemblies. The validation tools exercise production state/geometry code; the Release build compiles the complete runtime mod.

| Check | Result |
| --- | --- |
| `dotnet build Purr2D2.slnx -c Release` | Passed: 0 warnings, 0 errors; automatically produced the installable folder, ZIP and checksum using MSBuild. |
| `python3 tools/validate.py --run-tests` | Passed: CoreChecks, CuttingChecks, PlaneSliceChecks, PoseChecks, SlicingPhysicsChecks and AutopilotChecks, plus production SaberCutter contact integration checks, source assets and metadata. |
| `glslc Assets/Shaders/SaberGlow.vert` and `.frag`, each with an explicit output path | Both shaders compiled successfully. |
| `tools/inspect_host.cs`, run against KSA.dll and the complete Release Purr2D2.dll | Passed metadata/IL checks for patched method signatures, the ordered simulation handoff, private native integration members, mesh layout and lifecycle hooks, including negative controls. |
| `python3 tools/validate.py --stage artifacts/stage/purr2d2` | Passed source and staged-payload allowlist checks. |
| MSBuild ZIP and checksum inspection | Passed: all 21 files match the staged payload byte for byte, with valid CRCs, a valid SHA-256 sidecar and a single `purr2d2/` root. The only packaged DLL is Purr2D2.dll; game, loader and Harmony assemblies are excluded. |
| Distribution build behavior | Passed: incremental Release removes injected stale files while preserving sibling folders; Debug leaves the Release archive untouched; mismatched manifest/project versions fail; folder-only builds remove an older ZIP; Release clean removes the distribution and a subsequent plain Release build recreates it. |

The geometry checks cover oblique and repeated cuts, concave contours, holes and cavities, overlapping authored solids, material/UV preservation, sealed topology, volume/centroid/full inertia, float export and conservative refusal cases. Physics checks cover principal frames and linear/angular momentum calculations. These are managed numerical checks, not execution of the native game renderer or physics engine.

Additional read-only trials on supplied native meshes are recorded in [research-native-assets.md](research-native-assets.md). Several fairings support the sampled arbitrary planes; open art meshes and some exact grazing configurations are refused. The two original practice targets are included in the mod. [The slicing preview](../art/slicing-preview.png) shows production geometry output on the hollow practice tube, not an in-game capture.

Package: `artifacts/dist/purr2d2-0.2.0.zip`.

The build writes the current SHA-256 to `artifacts/dist/purr2d2-0.2.0.zip.sha256`. ZIP timestamps follow the input files; byte-identical archives across clean builds are not promised.

The host inspector reads PE metadata and IL without loading the Windows game assemblies. Harmony installation, Vulkan/FMOD execution, native fragment spawning and save/reload still require the native checks below. No live KSA acceptance result is claimed.

## Native KSA checklist — not yet executed

Use a disposable test save containing an EVA kitten, a simple uncrewed vessel with multiple parts, and a part with several authored subparts. Keep the game log open. Do not use the only copy of a valuable vessel to test fragmentation.

1. **Load:** enable purr2d2 under the matching StarMap installation. Check entry initialization, XML, shaders, textures and audio load without errors. Confirm F7 opens controls and does not activate a stock F-key binding.
2. **Grip:** equip on a standing kitten. Inspect the paw/grip alignment from several angles, walk, jump and use MMU. Confirm feet/root animation continues. Swing repeatedly and unequip; no accumulated rotation or hidden MMU remains. Inspect different kitten rigs.
3. **Deployment:** ignite/retract rapidly including halfway through each transition. The blade must grow from the emitter and reverse without a full-length flash. Change color/length and test an end-on camera angle.
4. **Rendering/light:** put the blade behind a vessel and verify depth occlusion. Check night/day, near metal surfaces, camera zoom and multiple game viewports. Check editor/portrait/thumbnail views for unwanted saber submissions. Change MSAA or resize/rebuild the renderer.
5. **Audio:** verify the supplied activation and idle clips, loop seam and relative gain. Move camera/source near/far and left/right; change SFX/master volume. Toggle while paused, resume, enter/exit warp and hide HUD. Verify swing/contact modulation uses local hand speed, not orbital speed.
6. **Contact safety:** off/retracted/pause/warp must not damage. Teleport or change target while the blade is lit; no long sweep should cut between old and new locations. Test thin triangles, scaled/rotated parts and large meshes. Crewed vessels must register contact and surface effects while retaining the structural-cut refusal; EVA exclusions and debris budgets must hold.
7. **Connection cuts:** dwell on a nonroot part until it separates. Verify no duplicate geometry, valid free-moving craft/debris, sensible inherited motion, and working remaining systems. Contact a root/brace/multi-connection graph; unsupported separation should report scorch-only rather than corrupt topology.
8. **Subpart fragmentation:** explicitly enable Fragment struck part on an uncrewed test vessel. Confirm native isolation destroys the source, produces at most two authored subpart fragments, adds no explosion and does not leave the original mesh. Check cut marks follow any matching retained fragment. Switch back to default afterward.
9. **Arbitrary slicing:** enable the experimental mode on a closed inert structural part. Try oblique cuts across triangles, then cut a resulting fragment again. Inspect both cap surfaces, surface UVs/materials, cooling emission and absence of the source mesh. Check closed convex and concave/holed test meshes, documenting enclosing-hull collision behavior. Unsupported meshes/modules must show a reason and remain intact. Check mass totals and linear/angular momentum on translating and spinning targets. Exercise isolation of a nonroot part with children and a one-part craft; source removal occurs only after all replacement fragments exist.
10. **Cylinder autopilot:** perform the EVA rendezvous and ring targeting checks above, then clockwise/counterclockwise cutting laps on a simple practice cylinder. Verify the blade reaches the selected station and actual cutting follows the visible ring. Record native results separately from managed tests.
11. **Resource/failure paths:** reach fragment/geometry/arena limits and make the generated-cache path unwritable. No partially prepared cut should delete its source. Toggle ray tracing after successful slices, resize/rebuild viewports and verify no shared buffer overrun or disappearing native mesh. If a native spawn fails after isolation, the original must remain as a valid isolated part and spawned replacements must be cleaned up.
12. **Save/load:** save ordinary separated craft and generated sliced fragments through KSA. Quit fully, restart with `Generated/Slices/` preserved, and reload; verify template resolution, geometry, collision, mass and inertia. Equip/heat/transient marks reset; preferences remain. Board, switch kittens, reload repeatedly, create a new world and enter/exit editor. No old channel or processor survives.
13. **Shutdown:** exit normally after toggling, cutting and opening secondary views. Check for invalid FMOD channels, Vulkan use-after-dispose, shape-registry errors and late renderer cleanup.

Live visual calibration of the holding pose and audio mix is expected in this first pass. The arbitrary slicer is implemented and its numerical geometry is tested, but native graphics, generated template loading and destructive physics are not certified until this checklist is run in KSA.
