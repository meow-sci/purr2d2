# Validation and native acceptance

This file separates automated checks from native gameplay observation. An unperformed native check remains unperformed even when the DLL builds successfully.

## Automated checks

Completed on 2026-10-22 with .NET SDK 10.0.402 on macOS ARM64, against the supplied KSA 2026.10.19.5638 assemblies. The validation tools exercise production state/geometry code; the Release build compiles the complete runtime mod.

| Check | Result |
| --- | --- |
| `dotnet build Purr2D2.slnx -c Release --no-restore` | Passed: 0 warnings, 0 errors. |
| `python3 tools/validate.py --run-tests` | Passed: CoreChecks, CuttingChecks, PlaneSliceChecks, PoseChecks, SlicingPhysicsChecks and AutopilotChecks, plus source assets and metadata. |
| `glslc Assets/Shaders/SaberGlow.vert` and `.frag`, each with an explicit output path | Both shaders compiled successfully. |
| `tools/inspect_host.cs`, run against KSA.dll and the complete Release Purr2D2.dll | Passed metadata/IL checks for patched method signatures, the ordered simulation handoff, private native integration members, mesh layout and lifecycle hooks, including negative controls. |
| `dotnet msbuild Purr2D2.csproj -t:StageMod -p:Configuration=Release` | Passed; staged the installable mod in `artifacts/stage/purr2d2`. |
| `python3 tools/validate.py --stage artifacts/stage/purr2d2` | Passed source and staged-payload allowlist checks. |
| `python3 tools/package.py` and ZIP inspection | Passed: 22 entries, 1,232,546 bytes, valid CRCs and a single `purr2d2/` root. The only packaged DLL is Purr2D2.dll; game, loader and Harmony assemblies are excluded. |

The geometry checks cover oblique and repeated cuts, concave contours, holes and cavities, overlapping authored solids, material/UV preservation, sealed topology, volume/centroid/full inertia, float export and conservative refusal cases. Physics checks cover principal frames and linear/angular momentum calculations. These are managed numerical checks, not execution of the native game renderer or physics engine.

Additional read-only trials on supplied native meshes are recorded in [research-native-assets.md](research-native-assets.md). Several fairings support the sampled arbitrary planes; open art meshes and some exact grazing configurations are refused. The two original practice targets are included in the mod. [The slicing preview](../art/slicing-preview.png) shows production geometry output on the hollow practice tube, not an in-game capture.

Package: `artifacts/dist/purr2d2-0.2.0.zip`.

SHA-256: `5b0e34e524d52d6886020c77d1757b33cabe4b5713fb97d256d9125bf8bf242c`.

The host inspector reads PE metadata and IL without loading the Windows game assemblies. Harmony installation, Vulkan/FMOD execution, native fragment spawning and save/reload still require the native checks below. No live KSA acceptance result is claimed.

## Native KSA checklist — not yet executed

Use a disposable test save containing an EVA kitten, a simple uncrewed vessel with multiple parts, and a part with several authored subparts. Keep the game log open. Do not use the only copy of a valuable vessel to test fragmentation.

1. **Load:** enable purr2d2 under the matching StarMap installation. Check entry initialization, XML, shaders, textures and audio load without errors. Confirm F7 opens controls and does not activate a stock F-key binding.
2. **Grip:** equip on a standing kitten. Inspect the paw/grip alignment from several angles, walk, jump and use MMU. Confirm feet/root animation continues. Swing repeatedly and unequip; no accumulated rotation or hidden MMU remains. Inspect different kitten rigs.
3. **Deployment:** ignite/retract rapidly including halfway through each transition. The blade must grow from the emitter and reverse without a full-length flash. Change color/length and test an end-on camera angle.
4. **Rendering/light:** put the blade behind a vessel and verify depth occlusion. Check night/day, near metal surfaces, camera zoom and multiple game viewports. Check editor/portrait/thumbnail views for unwanted saber submissions. Change MSAA or resize/rebuild the renderer.
5. **Audio:** verify the supplied activation and idle clips, loop seam and relative gain. Move camera/source near/far and left/right; change SFX/master volume. Toggle while paused, resume, enter/exit warp and hide HUD. Verify swing/contact modulation uses local hand speed, not orbital speed.
6. **Contact safety:** off/retracted/pause/warp must not damage. Teleport or change target while the blade is lit; no long sweep should cut between old and new locations. Test thin triangles, scaled/rotated parts and large meshes. Crew/EVA exclusions and debris budgets must hold.
7. **Connection cuts:** dwell on a nonroot part until it separates. Verify no duplicate geometry, valid free-moving craft/debris, sensible inherited motion, and working remaining systems. Contact a root/brace/multi-connection graph; unsupported separation should report scorch-only rather than corrupt topology.
8. **Subpart fragmentation:** explicitly enable Fragment struck part on an uncrewed test vessel. Confirm native isolation destroys the source, produces at most two authored subpart fragments, adds no explosion and does not leave the original mesh. Check cut marks follow any matching retained fragment. Switch back to default afterward.
9. **Arbitrary slicing:** enable the experimental mode on a closed inert structural part. Try oblique cuts across triangles, then cut a resulting fragment again. Inspect both cap surfaces, surface UVs/materials, cooling emission and absence of the source mesh. Check closed convex and concave/holed test meshes, documenting enclosing-hull collision behavior. Unsupported meshes/modules must show a reason and remain intact. Check mass totals and linear/angular momentum on translating and spinning targets. Exercise isolation of a nonroot part with children and a one-part craft; source removal occurs only after all replacement fragments exist.
10. **Cylinder autopilot:** create a simple uncrewed cylindrical target and use stock targeting. Test X/Y/Z and reverse-axis capture from several axial stations. Align without cutting, inspect all four 90-degree roll choices, then abort and confirm manual MMU/flight-computer/saber state is exactly restored. Run clockwise and counterclockwise laps while the target translates and slowly spins; verify clearance, inward blade aim, one-lap completion and actual cut continuity. Change target, leave MMU, pause, warp, minimize/rebuild rendering and exceed the safety envelope during separate trials; translation must clear, invisible laps must not advance, and no stale thrust may survive. Test an irregular hull only as a documented refusal/limitation, not as evidence of obstacle avoidance.
11. **Resource/failure paths:** reach fragment/geometry/arena limits and make the generated-cache path unwritable. No partially prepared cut should delete its source. Toggle ray tracing after successful slices, resize/rebuild viewports and verify no shared buffer overrun or disappearing native mesh. If a native spawn fails after isolation, the original must remain as a valid isolated part and spawned replacements must be cleaned up.
12. **Save/load:** save ordinary separated craft and generated sliced fragments through KSA. Quit fully, restart with `Generated/Slices/` preserved, and reload; verify template resolution, geometry, collision, mass and inertia. Equip/heat/transient marks reset; preferences remain. Board, switch kittens, reload repeatedly, create a new world and enter/exit editor. No old channel or processor survives.
13. **Shutdown:** exit normally after toggling, cutting and opening secondary views. Check for invalid FMOD channels, Vulkan use-after-dispose, shape-registry errors and late renderer cleanup.

Live visual calibration of the holding pose and audio mix is expected in this first pass. The arbitrary slicer is implemented and its numerical geometry is tested, but native graphics, generated template loading and destructive physics are not certified until this checklist is run in KSA.
