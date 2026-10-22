# Purr2D2 implementation map

The current gameplay is a held EVA lightsaber with cosmetic contact sparks and an explicit **Slice nearby part** action. Contact-triggered cutting, exposure tracking, ring flight and autopilot have been retired at the user's request. Current behavior and evidence are in [README](../README.md) and [acceptance](ACCEPTANCE.md).

## Component ownership

| Area | Files | Responsibility |
|---|---|---|
| Controller | `src/Mod.cs`, `src/Runtime/`, `src/Core/` | Lifecycle, queued intentions, blade deployment, one-click slice sequence, UI and settled mutation seam |
| Pose | `src/Pose/` | Scoped right-arm overlay, directed swing and final-paw emitter; retains stock locomotion |
| Rendering | `src/Rendering/` | Native hilt, HDR blade after atmosphere, light spill, bounded contact sparks and durable fragment assets |
| Audio | `src/Audio/` | Supplied activation/idle sounds, owned spatial channels and motion cues |
| Targeting/slicing | `src/Cutting/` | On-demand nearest render-surface selection, automatic plane candidates, clipping and panel backing |
| Native replacement | `src/Slicing/` | Float32 closure checks, principal mass/inertia, isolation and native wreckage transaction |
| Validation | `tests/`, `tools/` | Managed geometry/control/contact checks, stock-asset fixtures, host metadata and package validation |

## Slice transaction

1. Capture the selected vessel, equip and ignite. Select a visible part within 3 metres or the configured blade length.
2. Bake its native rendered surfaces in assembly metres, preserving normals, UVs and materials. Back supported open panels and try up to nine part-axis sections. Require closed capped output, valid inertia and export budgets.
3. Aim and temporarily extend the saber, animate a 0.72-second stroke, then consume exactly one commit cue. Cancel on stale pose, changed target, lost reach or explicit cancellation.
4. Revalidate the source, prepare all immutable GLB/XML templates and native colliders, then isolate the part at the settled simulation seam. Empty its resources, spawn replacements and conserve dry mass and momentum. Delete the source only after successful replacement.
5. Emit sparks and warm the new cut caps. Preserve generated asset files for native saves; refuse new work when budgets are exhausted rather than evicting another save's geometry.

Visual contact uses a separate read-only, throttled, bounded probe. It keeps no progress/heat/sweep history and cannot call the structural transaction. The native collider is an enclosing convex hull, so visible holes/recesses are not collision-exact. Functional modules and attached IVA furnishings do not survive as functional components in wreckage.

## Platform boundary

One .NET 10 runtime project targets KSA 2026.10.19.5638, StarMap.API 0.3.6 and Harmony 2.4.2. Game and loader assemblies remain compile-only and are never packaged. Build, geometry and metadata checks do not certify native graphics, physics spawning or save/load; use the native acceptance checklist before claiming those results.
