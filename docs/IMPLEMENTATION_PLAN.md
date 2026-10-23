# Purr2D2 implementation map

The current gameplay is a held EVA lightsaber with cosmetic contact sparks, an ordinary **Swing**, and explicit **Select part to slice**. Contact-triggered cutting, exposure tracking, ring flight and autopilot have been retired at the user's request. Current behavior and evidence are in [README](../README.md) and [acceptance](ACCEPTANCE.md).

## Component ownership

| Area | Files | Responsibility |
|---|---|---|
| Controller | `src/Mod.cs`, `src/Runtime/`, `src/Core/` | Lifecycle, queued intentions, blade deployment, one-click slice sequence, UI and settled mutation seam |
| Pose | `src/Pose/` | Scoped right-arm overlay, directed swing and final-paw emitter; retains stock locomotion |
| Rendering | `src/Rendering/` | Native hilt, HDR blade after atmosphere, light spill, bounded contact sparks and durable fragment assets |
| Audio | `src/Audio/` | Supplied activation/idle sounds, owned spatial channels and motion cues |
| Targeting/slicing | `src/Cutting/` | Exact cursor hit selection, varied faceted/plane candidates, clipping and panel backing |
| Native replacement | `src/Slicing/` | Float32 closure checks, principal mass/inertia, isolation and native wreckage transaction |
| Validation | `tests/`, `tools/` | Managed geometry/control/contact checks, stock-asset fixtures, host metadata and package validation |

## Slice transaction

1. Equip and ignite; select the cursor's surface hit within twice the deployed blade length of the hilt. The flight-computer target is independent. A second press cancels selection.
2. Immediately animate a 1.24-second stroke while capturing native rendered surfaces in bounded batches. Bake assembly metres, normals, UVs and materials. On the worker, back open panels and try bounded coarse faceted sections, then varied oblique/axis planes. Require closed output, valid inertia and export budgets.
3. Export durable GLB/XML and upload/register incrementally, preparing native colliders, Parts and detached trees before commit. The full stroke must be presented before consuming exactly one ready commit cue. Ordinary Swing stays at 0.62 seconds.
4. Revalidate source ownership/geometry and isolate the part at the settled simulation seam. Empty its resources, adopt prepared replacements and conserve dry mass and momentum. Delete the source only after successful replacement. Crew aboard does not block slicing; surviving neighboring branches retain their crew assignments.
5. Emit sparks and warm the new cut caps. Preserve generated asset files for native saves; refuse new work when budgets are exhausted rather than evicting another save's geometry.

Visual contact uses a separate read-only, throttled, bounded probe. It keeps no progress/heat/sweep history and cannot call the structural transaction. The native collider is an enclosing convex hull, so visible holes/recesses are not collision-exact. Functional modules and attached IVA furnishings do not survive as functional components in wreckage.

## Platform boundary

One .NET 10 runtime project targets KSA 2026.10.19.5638, StarMap.API 0.3.6 and Harmony 2.4.2. Game and loader assemblies remain compile-only and are never packaged. Build, geometry and metadata checks do not certify native graphics, physics spawning or save/load; use the native acceptance checklist before claiming those results.
