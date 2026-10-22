# Purr2D2 lightsaber implementation plan

## Goal and baseline

Deliver one .NET 10 StarMap mod with a held, animated lightsaber for the controlled EVA kitten; a blade that grows from its emitter; supplied activation/idle sounds, supplementary retraction/contact sounds, and a spatial looping hum; and contact-driven vessel separation with cooling molten cut marks. The source remains one `Purr2D2.csproj`, organized into feature folders rather than separate runtime libraries.

Target the supplied KSA **2026.10.19.5638** assemblies, StarMap.API **0.3.6**, and host Harmony **2.4.2**. The coffer implementation corpus targets 2026.10.10.5554, so every integration point must be checked against `../ksa-game-assemblies/current/decomp/`. All other repositories and knowledge/feature symlinks are read-only reference material. No proprietary assembly is distributed.

## Implementation sequence

1. **Establish a buildable mod and compatibility boundary.** Add XML `.slnx`, one class library, explicit compile-only game references, compile-only loader/Harmony packages, asset manifest, local overrides, and clean allowlisted staging. Fail clearly when host references are absent. Record the sources and exact integration points.
2. **Implement the controller and lifecycle.** Model unequipped, retracted, extending, lit, and retracting states with reversible continuous blade length. Queue UI intentions; consume them at the settled simulation boundary. Bind only the currently controlled EVA kitten. Clear bindings, channels, pending damage, and stale references on scene/kitten changes, boarding, disposal, and unload. Keep rendering and input functional with the HUD hidden. Prevent saber hotkeys while typing in ImGui. Pause and timewarp must not accumulate surprise cuts.
3. **Author an editable original hilt.** Create a kitten-scale hilt with machined metal collars, dark grip, emitter, accent details, and UV texture maps. Prefer Blender while preserving existing work in a separate scene. If Blender is unavailable, use a reproducible procedural GLB/texture generator and supply a Blender import/studio script; do not fabricate a `.blend` deliverable. Contract: meters, blade along local +Z, emitter at the origin, body to approximately Z=-0.22 m, grip center Z=-0.12 m; baked transforms, matching object/mesh names, normals and UV0, no export axis conversion.
4. **Attach to the actual animated paw.** Use a scoped `IAnimProcessor` on the EVA avatar. Blend only the right arm/wrist/finger pose, preserving stock locomotion. Support a short swing motion for deliberate cuts. Resolve bones and the private avatar field once, with clear compatibility failure. Derive the hilt transform after skeleton evaluation; use the same pose for hilt, blade, audio, and contact detection. Retain double precision until converting to camera-relative rendering coordinates.
5. **Render the saber and illuminate the world.** Load the hilt through native GLTF/PBR asset machinery. Render the blade through a small depth-tested HDR shader with a bright core and colored halo. This is necessary because the current generic static-mesh shader does not add its sampled emissive term. Submit colored native dynamic lights after the engine clears its light list. Support viewport changes, renderer rebuilds, bounded resources, and complete owned-resource cleanup.
6. **Integrate supplied sound assets and 3D playback.** Use the user's `feature/purr2d2-light-sabers/sounds/lightsaber-ignite.wav` and `lightsaber-idle.wav` for ignition and hum; inspect their format and loop boundary and copy them into the owned runtime assets. Generate only supplementary retraction/contact/swing sounds if needed. Use the game's FMOD wrapper and SFX bus with explicit channel ownership, looping sample bounds, spatial position and velocity. Avoid per-frame restart and global mixer changes. Stop channels on ownership changes and suspend appropriately during pause. The fantasy saber remains audible in vacuum; attenuation still follows camera distance.
7. **Implement cutting at the simulation handoff.** Inspect nearby non-EVA vessel geometry in the same parent frame, broad-phase by bounds, then test a bounded sweep of the blade against current part/subpart mesh geometry. Accumulate contact exposure using simulation time, cap frame advances, and reject stale/teleported sweeps. Exclude the wielder and crew. Drain structural changes after previous workers apply and before the next worker snapshots. Join the nearest-orbit reader before editing vessel topology.
8. **Implement arbitrary slicing as a primary feature.** The user's follow-up elevates real geometry slicing above authored-boundary separation. Build and validate triangle/plane clipping with interpolated normals and UVs, preserved material groups, closed caps with holes, connected-component extraction, signed volume, center of mass and full inertia. Generate native fragment GLBs and immutable part templates, bind them within a pre-reserved native GPU arena, and construct independent colliders. Prepare every asset before isolating the source; remove the source only after all replacement fragments exist. Preserve total mass and linear/angular momentum. Reload generated template references before native saves. Use glowing molten caps that cool to durable dark faces. Keep ordinary boundary separation and native subpart failure available as separate modes; rejected arbitrary slices must not silently destroy the source through a fallback.
9. **Add controls and diagnostics.** Provide an ImGui panel for equip, ignition, swing, blade color/length, sound volume, cutting toggle, and cut exposure. Show current target/support status and failures in usable terms. Persist user preferences separately from transient channels, GPU handles, and contact heat. Native KSA saves own resulting vessel topology; short-lived heat/glow effects expire rather than serialize pointers.
10. **Validate and package.** Build the complete mod against the real snapshot; compile shaders and validate GLB/texture/WAV/XML inputs; exercise state reversal, pause/warp gates, sweep/heat logic, lifecycle signature discovery, and the actual Harmony seam where feasible without the game. Stage and inspect the final ZIP for correct identity/version and absence of host DLLs. Document the exact native in-game acceptance pass, keeping build success distinct from a successful game session.
11. **Add human-assisted cylinder cutting guidance.** Reuse the stock vessel target, capture a local target-body cylinder axis and cut station, raycast the outer hull radius, then drive the EVA MMU with native pulse translation and a custom flight-computer attitude. Split the workflow into capture, align/inspect, roll adjustment and one-lap cut. Track target translation and spin, require fresh saber geometry before advancing the lap, enforce close-proximity limits, clear translation during pause/warp, and restore every changed flight/saber state on completion, abort, target change, scene change or failure.

## Ownership and component boundaries

| Area | Files | Responsibility |
|---|---|---|
| Host/controller | `src/Mod.cs`, `src/Runtime/`, `src/Core/` | Lifecycle, safe mutation seam, commands, preferences and player controls |
| Pose | `src/Pose/` | Scoped skeletal overlay, hand/emitter transform, swing pose |
| Rendering | `src/Rendering/`, `Assets/Shaders/`, asset XML | Native hilt, blade/glow, world light, cooling cut ribbons |
| Cutting | `src/Cutting/` | Swept contact, exposure, native separation, fragment-local marks |
| EVA guidance | `src/Autopilot/` | Target-relative ring capture, close-proximity MMU pulse guidance and attitude/state ownership |
| Audio | `src/Audio/`, `Assets/Audio/`, audio generator | Original sounds, FMOD spatial channels and disposal |
| Art | `Assets/Models/`, `Assets/Textures/`, `art/`, Blender script | Editable source and runtime model/texture assets |
| Delivery | root project/manifest, `tools/`, `docs/` | Build, test, reproducible packaging, compatibility and acceptance records |

Research and engine-facing implementation use gpt-6.1-sol / xhigh agents. Simpler scripted validation may use gpt-6-luna / max. Agents own disjoint paths; integration and commits remain centralized.

## Verified integration decisions

- `Program.PrepareFrame(double,double)` waits/applies old solvers, applies input, queues the nearest-orbit reader, calls `Universe.GetJobSimStep`, then dispatches cloth/vehicle/orbit work. The implementation inserts a checked callback immediately after `InputEvents.ApplyInputEvents`, before the reader is queued. This avoids mutating physics from ImGui/render callbacks and leaves Unscience's `GetJobSimStep` wrapper untouched. Verify unique ordered call sites and fail closed on incompatible game updates.
- `KittenEva.Renderable` exposes the renderable; the avatar is a private field in `KittenRenderable`. `CharacterModel.AnimProcessors` provides local-pose and per-view skeleton hooks. `UpdateSkeleton` precedes cosmetic attachments and provides current bone transforms.
- Character model coordinates are centimeters, +Y up/+Z forward; the game applies scale and axis turns through `ModelToBodyMatrix`. Physics/audio transforms must not borrow an already camera-relative matrix.
- Native `GltfFile`, `PbrMaterial`, and `StaticMeshRenderable` can render the authored hilt. Static-mesh emissive sampling is insufficient for the blade in this snapshot, so blade HDR emission needs its own render pass integration.
- Native `Vehicle.Split` handles full parts. `SpawnSubPartDebris` constructs a new fragment but does not itself delete the old mesh or subtract mass. This distinction drives the destructive-contact implementation and tests.
- `SpatialAudio`'s astronomical offset is an ego/world displacement, not a vehicle-local coordinate. The FMOD listener is at the origin; use engine view-position and velocity conversion. Explicit channels avoid the stock compound sound's overlapping start/loop behavior.

## Acceptance criteria

| Requirement | Acceptance |
|---|---|
| Held hilt | Paw aligns with grip during standing, walking, EVA flight, and swing; no accumulating pose offsets; unequip restores animation |
| Ignition | Continuous extension from emitter, reversible while moving; no full-length flash at activation |
| Sound | One activation per transition; seamless spatial hum; pause, switch, load and unload leave no orphan channels |
| Light | White core/colored halo, world depth occlusion, nearby vessel illumination; no editor/portrait ghosts |
| Contact | No damage while off, paused, in unsupported warp, during teleport discontinuity, or to the wielder/crew |
| Separation | Contact can produce independent native fragments without duplicate geometry, mass bookkeeping corruption or worker races |
| Arbitrary slicing | A blade-defined plane cuts supported closed geometry across existing triangles; output has sealed manifold caps, conserved solid volume, native fragment ownership and durable template IDs |
| Cylinder autopilot | Stock-target capture and staged human inspection produce one bounded MMU lap; abort, target loss and unsafe proximity restore prior controls without stale thrust |
| Cut appearance | Hot local marks cool and follow the affected fragments; effects remain bounded |
| Packaging | One installable `purr2d2/` directory, entry DLL + required owned assets, no game/loader/Harmony/test binaries |
| Evidence | Record managed tests and package checks separately from unperformed native KSA acceptance |

## Scope honesty

This plan commits to implementing the requested feature paths, including a real arbitrary-plane clipping path. Initial eligibility is deliberately explicit: closed, consistently wound inert native part meshes; bounded complexity; no crew or live functional modules. A generated convex collider encloses each fragment, so concave holes and recesses are visually exact but not collision-exact. Arbitrary slicing means a free cutting plane through supported geometry, not unrestricted support for every game asset or curved cut paths. Native graphics, skeleton tuning, generated GPU uploads, save/load and destructive physics still require an actual KSA acceptance session; a managed build cannot certify them.

## Phase 8 implementation detail

1. Gather all supported model surfaces in physical vehicle-assembly meters. Bake transforms, correct reflected winding and transform normals with the inverse transpose.
2. Derive a plane from the blade axis and relative sweep. Retain the deliberate swing plane during contact heating; stationary contact chooses a plane containing the blade and surface normal.
3. Weld position seams only for topology, retaining each corner's normals and UVs. Reject open, collapsed, inconsistently wound or excessive source geometry.
4. Clip both sides with shared edge intersections. Build noncrossing contour loops and hole-aware trapezoidal caps; subdivide side seams to avoid T-junctions. Audit closed output and volume conservation before publication.
5. Calculate principal axes and specific inertia from tetrahedral integrals. Write recentered GLB geometry and native XML mass/collider/material templates under immutable IDs.
6. Reserve bounded native vertex/index space during immediate mod loading. Runtime uploads must not advance native shared-buffer counters: the host's later ray-tracing rebuild assumes equal copy sizes.
7. At the settled solver seam, validate the connection tree, isolate the full part, spawn replacement native vehicles, restore exact mass/inertia, and solve conserved momentum. Destroy the original only after success; retain it on replacement failure.
8. Reload immutable generated assets before native save reconstruction, keep old cache entries needed by other saves, and refuse additional work when bounded memory/disk/geometry limits are reached. Show actionable reasons in the saber panel.
