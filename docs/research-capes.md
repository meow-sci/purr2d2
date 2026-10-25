# Optional kitten capes

Research target: the local KSA **2026.10.19.5638** decompiled sources and DLLs. The coffer skill's older source map was used for navigation; the implementation contracts were checked against this snapshot.

## Feasibility

The native parachute cloth solver is reusable for a cape. The parachute deployment system and its authored canopy are not suitable replacements for clothing: they assume a round canopy, suspension lines, inflation and a single riser. A cape instead needs a shoulder seam, an open sheet and no pressure inflation.

The useful lower layer accepts custom topology and state. `ChuteBepuClothState.Create` creates a kinematic body for **every** node with zero inverse mass; it does not limit pins to `PinIndex`. It creates structural, shear and bend constraints from the supplied arrays. `ChuteBepuClothSolver.StepDynamic` advances that isolated Bepu simulation. A mod can supply a shoulder-pinned sheet without creating a parachute component, changing a vessel's mass, or applying forces to its wearer.

| Local source under `repos/ksa-game-assemblies/current/decomp/KSA/` | Relevant contract |
| --- | --- |
| `ChuteClothTopology.cs` | Public topology arrays; stock `Build` and inflated-pose methods are canopy-specific and must not initialize a cape. |
| `ChuteClothState.cs` | Public positions, velocities, inverse masses and environment inputs. Stock `Reset` also assumes canopy topology. |
| `ChuteBepuClothState.cs` | Per-node pins, independent simulation ownership, distance constraints and explicit disposal. Skip constraints joining two pinned nodes. |
| `ChuteBepuClothSolver.cs` | Native integration, finite-state recovery and bounded substeps. |
| `ChuteClothForcePass.cs`, `ChuteClothForces.cs` | Gravity, damping and relative-air drag are caller-supplied; pressure and radial canopy aerodynamics can be disabled. |
| `ChuteRenderable.cs`, `ChuteCanopyPose.cs` | Two-sided skinned PBR rendering, shadows, per-instance materials and simulation-driven bones. The stock canopy's bone mapping is not reusable. |
| `AnimatedRenderable.cs` | Custom material handles are captured on construction; local skinning can be shared across viewports within a frame. Every new frame restores the animation pose before calling processors, so cached cloth transforms must still be copied into that pose. Skinning allocation indices cannot be reused across frames. |
| `KittenRenderable.cs` | Final skeleton is available after `UpdateRenderData`; sockets can follow all native and mod animation, including full-body flips. |
| `Vehicle.cs`, `GetSurfaceVelocityCci` | Subtracts the body's rotational surface velocity. This avoids treating a stationary landed kitten's orbital velocity as airflow. |

The coffer articles on attaching props to kitten bones and the existing saber pose controller establish the avatar access path. Unlike a static cosmetic attachment, the cape needs its own skinned renderable. A postfix at the end of the kitten's render-data update reads the final chest pose without altering any kitten bones.

## Intended behavior and asset contract

Each EVA kitten has its own session-only opt-in, initially false, in the existing per-kitten settings window. Its selected Light/Dark saber type determines brown/black cloth independently of blade color or ignition. World reset, boarding, mod disable and disposal release owned resources.

The cape uses an original 9 × 13 grid (117 nodes), with the top row pinned. It has a curved 48 cm shoulder seam, an 86 cm flared hem and a 50 cm authored drop. The renderer maps `Cape_000` through `Cape_116` by name. Mesh coordinates are metres, +Y up and +Z toward the kitten's front, with the origin at the chest. Native two-sided materials provide cloth visibility and lighting from either side.

Artificial gravity and bounded drag deliberately ignore atmospheric density and local gravitational strength. Forward movement makes the sheet trail behind; stopping lets it settle. This is a cosmetic Earth-like simulation and never supplies aerodynamic forces to the kitten or vessel.

## Cape body and shoulder clearance

The cape has a curved shoulder seam and its static hem sits about 3 cm above the stock kitten's soles. A local lower bound stops simulated nodes at sole height without terrain queries. Only the cape is loaded and drawn; all hood assets, rendering and authoring checks have been removed.

The body-clearance profile is cached once with the rest pose. Central nodes retain the −0.22 m rear limit; outboard shoulder nodes retain their authored rear position where the old flat limit would stretch short links away from the fixed pins. The closest dynamic bound is −0.19667 m at X ±0.25583 m, beyond the measured upper-body half-width of about 0.205 m. It is still 4.97 mm behind even the body's globally rearmost vertex. First-row nodes within that body width have at least 18.7 mm clearance from the same plane. There are no additional simulation passes, per-cape arrays or collision queries. Airflow uses half the bounded movement velocity, and lift is capped at 5.5 m/s² so the cloth always retains downward pull.

## Worn Light-side cloth

The second visual review replaced the Light cloth's regular grid, embossed weave and large stitched seam with a desaturated dusty brown, broad fading, gentle hem grime, worn edges and irregular fine fibers. Its diffuse base is RGB (92, 72, 55). Light has separate near-flat normal and matte AO/roughness/metalness maps; all three original Dark-side maps remain byte-identical. The six shared maps stay at 256 × 256 pixels. This adds no material instances, draw calls, shader samples or simulation work.

The deterministic generator recreates the textures and `art/brown-cloth-preview.png`. Texture checks enforce non-metallic matte cloth and a maximum 2.5° tangent-normal tilt; measured Light tilt is about 2.03°. Final game lighting still needs native acceptance.

## Overlay-wide backpack visibility

The next in-game review confirmed that the resting cape overlaps the EVA backpack. Backpack visibility now belongs to the mod's enabled session, independently of cape and saber equipment. `CharacterAvatar.Attachments.Mmu.Enabled` gates the native MMU attachment's animation and drawing in `KittenRenderable`; it does not control flight physics or RCS input.

Each existing per-kitten runtime record caches its avatar reference once, captures the attachment's original enabled state and keeps it hidden during the existing settled fleet refresh. Disabling the mod, boarding/removal, world reset, faults and disposal restore that state. The record releases its visibility ownership after disposing the saber pose, so stowing or changing hand poses cannot reveal the backpack. Originally hidden attachments stay hidden. Temporary HUD, focus and viewport visibility changes do not control the lease. The old `HideMmu` preference remains readable for compatibility but no longer changes this behavior; its equipment-only checkbox has been removed.

This adds no fleet scan, render hook or physics work. The warmed 128-refresh check retains one existing fleet scan per refresh and allocates zero managed bytes. Cape simulation still requires a per-kitten opt-in.

## Performance requirements

- Default-off creates no cape controller, cloth simulation or renderable. Native assets are requested lazily when a cape is used. An explicit registry tracks opted-in kittens and pending cleanup; the empty-registry path returns in constant time without scanning all kittens. The installed render hook takes a cheap gate before any cape lookup or drawing.
- A never-rendered enabled cape defers controller/model allocation until it is within 60 m of a visible main or secondary game camera. Camera positions for setup are captured once per settled pass in bounded stack storage, only when a missing controller needs them. Existing controllers are retained outside range to avoid repeated GPU resource creation; their idle cloth simulation is released separately.
- The persisted global `CapeSimulationLimit` defaults to four and is clamped to 0–16. The nearest eligible capes within 25 m receive cloth updates. Zero releases simulations at the next settled handoff, even while paused, while preserving cape rendering in the authored rest pose. Other capes release simulation after 1.5 seconds idle and use a static drape.
- Updates target 30 Hz, with at most two capes advanced per frame, four 1/120-second native substeps per cape, and no backlog catch-up. A rotating schedule follows stable owner indices within the nearest set, so changes in distance rank cannot starve a cape. Raising the global limit does not raise the per-frame work budget; more capes share those two updates. Candidate storage is fixed at the validated maximum and only the selected span is used.
- Each cloth update enters the native solver once for its batch of substeps, avoiding repeated constraint/environment setup.
- Simulation happens once per settled game update, never once per viewport. Rendering only consumes its result.
- Cloth poses carry a revision. Surface frames and rotations are recomputed only when cloth changes; rendering copies cached transforms into the native animation pose. Rest restoration and idle release are idempotent, avoiding repeated work for static capes.
- Arrays, constraints and native resources are reused. Disable self-collision and world collision queries; use a small local body clearance proxy.
- Distance limits, bounded airflow, pause/warp handling and failure containment keep visual work bounded.
- Do not modify stock parachute tuning, shaders, pools, physics jobs or the global Bepu simulation.

Managed CPU measurements and native visual/frame-time acceptance are recorded separately in `ACCEPTANCE.md`. A bounded work count is not an FPS measurement. Model scale, lighting and actual game/GPU cost require an in-game check.
