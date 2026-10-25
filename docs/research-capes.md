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

The coffer articles on attaching props to kitten bones and the existing saber pose controller establish the avatar access path. Unlike a static cosmetic attachment, the cape needs its own skinned renderable. A postfix at the end of the kitten's render-data update can read the final chest/head pose without altering any kitten bones.

## Intended behavior and asset contract

Each EVA kitten has its own session-only opt-in, initially false, in the existing per-kitten settings window. Its selected Light/Dark saber type determines brown/black cloth independently of blade color or ignition. The hood accompanies the cape and leaves the helmet visor opening exposed. World reset, boarding, mod disable and disposal release owned resources.

The cape uses an original 9 × 13 grid (117 nodes), with the top row pinned. It has a curved 48 cm shoulder seam, an 86 cm flared hem and a 50 cm authored drop. The renderer maps `Cape_000` through `Cape_116` by name. Mesh coordinates are metres, +Y up and +Z toward the kitten's front, with the origin at the chest. The hood uses a separate `HoodRoot` rig in the native helmet attachment frame. Native two-sided materials provide cloth visibility and lighting from either side.

Artificial gravity and bounded drag deliberately ignore atmospheric density and local gravitational strength. Forward movement makes the sheet trail behind; stopping lets it settle. This is a cosmetic Earth-like simulation and never supplies aerodynamic forces to the kitten or vessel. Hood folds are authored geometry; the hood is attached to the head rather than separately simulated.

## Helmet and shoulder fit correction

The first in-game screenshots exposed a bad authoring reference: the hood crown was 24.5 cm below the real helmet crown. Most of the hood lay inside the helmet, leaving its flared lower section visible around the shoulders. The original cape also extended about 35 cm below the stock kitten's soles.

Fit measurements now use local stock `KSA_Cat`, `KSA_Cat_Helmet` and `KSA_Cat_Visor` assets. Stock geometry is read only and is not redistributed. `GltfLoader.ExtractSkeleton` places `Chest_M` 0.43093 m above the mesh sole and `Head_M` 0.10899 m up / 0.00958 m forward of the chest in the cape's axes. Native helmet coordinates are centimetres; converted to metres, its bounds are X ±0.40318, Y −0.08055–0.63750, Z −0.41173–0.34779. Those dimensions explain why the previous small guide could not establish fit.

`CharacterAssets.xml` assigns the stock helmet to `Head_M` with no extra transform. `KittenRenderable.UpdateRenderData` publishes the final helmet matrix after animation and per-viewport placement. The hood now uses that exact matrix, preceded by a 100× metres-to-centimetres conversion. This preserves the helmet's socket orientation, translation and scale. The existing head-socket frame remains a fallback when no helmet is equipped. No extra skeleton traversal or physics pass is needed.

The revised hood extends to Y 0.765 m, holds its full shell width through the helmet's lower edge, then narrows to a 0.34 m half-width / 0.235 m rear depth at Y −0.18 m. Its open face clears the stock visor, and its lower rear edge overlaps the cape near the neck. The cape's curved top seam moves inward toward the shoulders; its static hem is about 3 cm above the soles. A local lower bound stops simulated nodes at sole height without terrain queries.

The body-clearance profile is cached once with the rest pose. Central nodes retain the −0.22 m rear limit; outboard shoulder nodes retain their authored rear position where the old flat limit would stretch short links away from the fixed pins. The closest dynamic bound is −0.19667 m at X ±0.25583 m, beyond the measured upper-body half-width of about 0.205 m. It is still 4.97 mm behind even the body's globally rearmost vertex. First-row nodes within that body width have at least 18.7 mm clearance from the same plane. There are no additional simulation passes, per-cape arrays or collision queries. Airflow uses half the bounded movement velocity, and lift is capped at 5.5 m/s² so the cloth always retains downward pull.

`tools/check_cape_fit.py` takes local helmet and visor GLTF paths and checks the generated hood against their actual vertices. It is an offline authoring check, never runtime work. The checked-in `art/cape-cloth-preview.png` uses original measurement-based proxies and is a software preview, not an in-game capture. Native acceptance must still cover animation, other character rigs and MMU equipment.

## Performance requirements

- Default-off creates no cape controller, cloth simulation or renderable. Native assets are requested lazily when a cape is used. An explicit registry tracks opted-in kittens and pending cleanup; the empty-registry path returns in constant time without scanning all kittens. The installed render hook takes a cheap gate before any cape lookup or drawing.
- A never-rendered enabled cape defers controller/model allocation until it is within 60 m of a visible main or secondary game camera. Camera positions for setup are captured once per settled pass in bounded stack storage, only when a missing controller needs them. Existing controllers are retained outside range to avoid repeated GPU resource creation; their idle cloth simulation is released separately.
- The persisted global `CapeSimulationLimit` defaults to four and is clamped to 0–16. The nearest eligible capes within 25 m receive cloth updates. Zero releases simulations at the next settled handoff, even while paused, while preserving cape/hood rendering in the authored rest pose. Other capes release simulation after 1.5 seconds idle and use a static drape.
- Updates target 30 Hz, with at most two capes advanced per frame, four 1/120-second native substeps per cape, and no backlog catch-up. A rotating schedule follows stable owner indices within the nearest set, so changes in distance rank cannot starve a cape. Raising the global limit does not raise the per-frame work budget; more capes share those two updates. Candidate storage is fixed at the validated maximum and only the selected span is used.
- Each cloth update enters the native solver once for its batch of substeps, avoiding repeated constraint/environment setup.
- Simulation happens once per settled game update, never once per viewport. Rendering only consumes its result.
- Cloth poses carry a revision. Surface frames and rotations are recomputed only when cloth changes; rendering copies cached transforms into the native animation pose. Rest restoration and idle release are idempotent, avoiding repeated work for static capes.
- Arrays, constraints and native resources are reused. Disable self-collision and world collision queries; use a small local body clearance proxy.
- Distance limits, bounded airflow, pause/warp handling and failure containment keep visual work bounded.
- Do not modify stock parachute tuning, shaders, pools, physics jobs or the global Bepu simulation.

Managed CPU measurements and native visual/frame-time acceptance are recorded separately in `ACCEPTANCE.md`. A bounded work count is not an FPS measurement. Model scale, helmet clearance, lighting and actual game/GPU cost require an in-game check.
