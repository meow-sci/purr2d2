# Acceptance record

## Asynchronous slicing and cosmetic sparks (2026-10-22)

The user observed a tank physically split with the previous implementation, but reported failures on service modules/capsules and severe slowdown while touching parts. They requested removal of contact-progress tracking and all autopilot functionality, retaining a first-class slicing swing and visual contact sparks.

The user subsequently confirmed the button slices parts, but reported a severe stall hiding the animation. Preparation now runs once on a worker, source capture/registration are incremental, and GPU uploads use bounded batches with nonblocking fence polls. **Slice nearby part / Alt+F7** plays the entire 0.72-second aimed stroke before one native breakup attempt. Ready results commit on the next settled update after final-pose presentation; otherwise preparation continues without joining the worker. The target dropdown, cancellation and cosmetic 20 Hz contact probe remain.

### Automated evidence

- Release build against KSA 2026.10.19.5638 succeeds without warnings/errors.
- Managed checks exercise blade/settings behavior, incremental target selection and commit guards, worker publication/nonblocking cancellation/failure handoff, final-pose presentation with early/late results, exactly-once completion, geometry clipping and physics math, float32 repair, and GLB serialization.
- Contact checks exercise two-sided intersection, blade-tip bounds, rate limiting, continuation past the triangle budget into later primitives, current-transform validation, disposed targets and visual effects on crewed vessels. A warmed 624-part stand-in scene over 1,800 frames allocates **zero managed bytes** for sustained contact. This is a managed probe measurement, not a native game FPS result.
- Locally installed stock assets pass automatic selection, closed-surface/volume checks, principal-frame float32 conversion and actual GLB export: `CoreFuelTankA_Prefab_LF3W3HA`, `CoreCommandA_Prefab_MediumCapsuleVariantA`, and `CoreServiceModuleA_Prefab_SetAHeightA/B/C/D`. Tank axial/oblique and capsule axial cuts also pass. Proprietary source assets are not committed or packaged.
- Service modules required repairing float-collapsed edge T-junctions, increasing compound/export group limits, and increasing the bounded convex-hull input-point budget. The native GLB writer is exercised by these fixtures so its group limit cannot silently disagree with preflight.
- All six stock fixtures also exercise the actual worker export, prepacked upload sizes, separate full-detail picking/compact collision meshes, and Bepu reconstruction of the saved collider. The local run produced 10–276 collision points per fragment, compared with roughly 7,000–50,000 render/picking points. CPU preparation was about 1.1–1.7 seconds off the game thread; these timings exclude game asset registration, graphics and world mutation. Full worker coverage exposed and fixed the old 100,000-triangle per-piece refusal on taller service modules; the limit now matches the exporter's 200,000-triangle bound.

Local ignored logs: `artifacts/async-slice-build.log`, `artifacts/async-slice-checks.log`, `artifacts/async-stock-all.log`, and `artifacts/async-slice-host-checks.log`. Run the following checks from the repository:

```sh
dotnet build Purr2D2.slnx -c Release
python3 tools/validate.py --run-tests --stage artifacts/stage/purr2d2
python3 tools/check_stock_slices.py /path/to/Content/Core
dotnet run --file tools/inspect_host.cs -- /path/to/KSA.dll bin/Release/net10.0/Purr2D2.dll
```

### Required native retest

These checks do not execute KSA's Vulkan/FMOD pipeline, native fragment spawning or save reconstruction. The previous user-observed tank cut does not establish acceptance of this replacement workflow.

1. Beside an uncrewed Gemini7, press **Slice nearby part** from an unequipped state. Confirm ignition and the entire visible aimed swing before source removal, then two separating textured pieces with sparks and cooling cut caps. Repeat on service-module variants and an empty capsule. Check that preparation keeps rendering responsive and that a ready result has no extra delay after the stroke. Record the preparation/commit timings from Slice diagnostics, especially for a cold first slice. Inspect glass, door details, normals and transformed parts.
2. Select another vessel from the dropdown; confirm only that vessel can be sliced. Cancel during reading, worker calculation, upload and the finished-stroke waiting period; also move out of reach, switch kittens, board and hide the kitten. No cancelled request should mutate a part or block rendering while cleanup finishes. Retry after cleanup. Occupied vessels must refuse structural slicing.
3. Hold and manually sweep a lit blade across a part. Sparks should appear without heat/progress bars, persistent scars or destruction. Move clear; sparks must stop emitting. Verify contact effects on a crewed vessel. Check sustained contact frame time against the prior slowdown.
4. Check pause/resume, 1×/2×/5× and higher warp, HUD visibility, ignition/retraction, walking and MMU movement. Confirm no autopilot panel or flight-control override remains.
5. Inspect the blade and sparks against Earth, atmosphere/clouds, dark space and an intervening vessel; retain correct occlusion. Repeat after resizing and changing MSAA, and in a secondary viewport. Inspect hand alignment and sound.
6. Verify fuel is emptied only from the isolated source, surviving neighboring parts remain usable, and fragment mass/motion are plausible. Test another cut on generated wreckage. Geometry/arena/native isolation refusals must keep the original or report a retained isolated source.
7. Save, quit and reload with `Generated/Slices/` preserved. Check generated template lookup, geometry, collision and inertia; saved cut caps reload cold. Exit normally and inspect logs for graphics/audio/physics cleanup errors.

The enclosing convex hull collider may fill visible recesses or holes. Functional modules and attached IVA furnishings are removed when a part becomes wreckage. Complex geometry can add preparation latency before/after the swing, but CPU calculations and disk I/O no longer run in the frame callback. Native registration/preflight calls have a soft frame budget; the final native topology transaction remains atomic. Actual frame-time acceptance still requires the game.
