# EVA holding pose and emitter frame

The implementation in `src/Pose/SaberPoseController.cs` targets the supplied KSA **2026.10.19.5638** snapshot. The coffer's related articles target 2026.10.10.5554; the members below were checked in the current decompile before use.

## Verified seams

All source paths in this table are relative to `../ksa-game-assemblies/current/decomp/`.

| Source | Verified behavior |
|---|---|
| `KSA/KittenEva.cs`, `Renderable`, `LocomotionState` | Public access to the EVA renderable and current locomotion mode. Seated crew are separate renderables and are not bound. |
| `KSA/KittenRenderable.cs`, `_characterAvatar` | One private field is the only reflection dependency. Failure produces a status message and disables the pose. |
| `KSA/CharacterAvatar.cs`, `CharacterCore` | Public `Core.CharacterModel`, `Core.Scale` (stock 0.01), and attachment state. |
| `KSA/AnimatedRenderable.cs`, `UpdateAnimation` | Samples the stock clip, calls processors' `UpdateLocalPose` once per frame, rebuilds skeleton world matrices per viewport, calls `UpdateSkeleton`, then skins. The processor list is insertion ordered. |
| `KSA/IAnimProcessor.cs` | `Priority`, `ShouldUpdate`, `CanCacheAnimation`, local-pose and skeleton callbacks match the implementation signatures. |
| `KSA/KittenRenderable.cs`, `UpdateAnimationLod` | A processor's `Priority` controls its distance limit: 45 m multiplied by priority. Priority 5 matches the stock character model's 225 m limit. It does not control list ordering. |
| `RenderCore.Animation/Skeleton.cs` and `RenderCore/PackedTransformExtensions.cs` | Named bones, parent indices, local/world matrices; hierarchy composition uses `local * parentWorld`. |
| `KSA/KittenRenderable.cs`, `ModelToBodyMatrix` | Scale by avatar scale, rotate −90° about X, then −90° about Z. |
| `KSA/KittenEva.cs`, `UpdateRenderData` | Subtracts assembly centre of mass from the draw anchor before drawing the character. |
| `KSA/Program.cs`, `RenderGame` and `RenderViewport` | `ClearBuckets` occurs before EVA kittens evaluate their skeletons. The renderer must submit the hilt through the late pose callback to avoid a frame of hand lag. |

The coffer reference path is `.agents/skills/ksa-coffer-skill/references/`. Useful topics are `howto/kittens/animation/pose-kitten-bones-with-an-anim-processor/` (overview, local overlay, shoulder aim, two-bone IK and coordinate conversion), `howto/kittens/appearance/attach-a-prop-to-a-kitten-bone/` (overview and same-view attachment), and `mods/ksagolf/features/putting-address-pose.md`. These established the approach; the current source above is the compatibility authority. No community implementation code was copied into this component.

## Implemented behavior

`Bind` resolves the avatar and `Shoulder_R`, `Elbow_R`, and `Wrist_R` once and registers one scoped processor. It optionally resolves `Chest_M`/`Spine1_M` and discovers index/middle/ring/pinky finger roots by name under the right wrist. Missing required arm bones fail closed. Optional fingers use conservative wrist attachment when their names do not match.

`UpdateLocalPose` builds model-space matrices from the live clip, solves a two-bone right arm with measured segment lengths and a clamped reach, and blends only shoulder/elbow/wrist transforms. The root, spine, left arm, legs and stock animation selection remain live. A small optional +Y curl is applied only to discovered finger roots. A 0.62 s swing changes the hand target and blade direction without teleporting the kitten or changing any physics state.

`UpdateSkeleton` reads the current view's final paw position, builds the emitter frame, and invokes `Draw` only for game Main/Secondary viewports while the bound kitten is controlled. Only the reference-equal `Program.MainViewport` may refresh the body-local snapshot. Portraits, part thumbnails, and editor rendering cannot refresh contact/audio state or submit a held saber. The actual hilt renderer subscribes to that event. This avoids the stock cosmetic-attachment axis correction and allows the blade and hilt to share a metre-based asset contract.

Saber use is enabled for Grounded, Airborne and Mmu modes. Ladder, Swimming, Tumbling and Rightening suppress the pose. Climbing requires both hands and adds a separate visual ladder offset in `KittenRenderable`; excluding it also keeps the body-frame conversion exact. Hiding the stock MMU cosmetic exposes its hand controllers; the prior `Enabled` state is captured, changed only when needed, and restored on stow/unbind. Helmet state is untouched.

Bind/update/unbind run on the main thread outside rendering. The callbacks contain exceptions and report them through `Status`. No render callback edits vehicle state, part topology or simulation data. Unbinding removes exactly the owned processor, clears the pose snapshot, and restores the changed cosmetic state; disposing also removes event subscriptions.

Thread affinity was checked through the current call chain: `App.Run` calls `Program.OnFrame` synchronously; `OnFrame` calls `Render`; `Render` calls `RenderGame`; `RenderGame` calls secondary `RenderViewport` methods and then updates main-view EVA kittens directly. Each goes through `KittenEva.UpdateRenderData` to `KittenRenderable.UpdateRenderData` and `AnimatedRenderable.UpdateAnimation`. No task or render-worker dispatch occurs along this chain. The game's render worker pool exists but does not evaluate this kitten path. Thus pose callbacks, native mesh submission, and the runtime's main-view audio update remain on the frame thread in this snapshot.

The sampled source clip is copied anew into the processed pose each frame. Each blend starts from that fresh clip, so shoulder/wrist edits and finger curl never accumulate. Each viewport rebuilds its skeleton from the same processed local pose; secondary views can run first, but their camera-relative matrices never overwrite the gameplay body-frame snapshot. `Program.OnFrame` increments `FrameNumber` after `PostRender`, so a main-view snapshot is age one at the next settled simulation handoff. Age two, unavailable snapshots, backwards frame counters, editor mode and a changed controlled kitten are rejected.

`FrameNumber` alone is insufficient when swapchain acquisition skips rendering: the game can continue applying physics without incrementing that counter. `MainPoseGeneration` therefore increments on every successful main-view publication and is retained across unbind. The runtime consumes each generation once at its safe simulation seam; another seam with no new generation suspends contact, light and frame audio. The same-view Draw callback may still update audio immediately after a new main pose. This prevents a minimized/unrendered window from keeping a historical pose fresh indefinitely.

## Public contract

| Member | Meaning |
|---|---|
| `Bind(KittenEva)`, `Unbind()`, `Dispose()` | Scoped ownership of one EVA avatar. Bind returns compatibility success. |
| `Update(double dtPlayer, bool equipped)` | Advances the equip blend and swing clock; delta is bounded to 0.1 s. |
| `Swing()` | Starts a short arm/blade sweep, with a short retrigger gate. |
| `Active`, `Eligible`, `Status`, `Blend`, `Kitten` | Main-thread status for UI and runtime gates. |
| `Draw` event | `(kitten, viewport, emitterToEgo)`; matrix maps emitter-local metres to that viewport's camera-relative frame in the same skeleton evaluation. |
| `TryGetBodyFrame(out double4x4)` | Emitter-local metres to kitten **body-COM-relative metres**. Rejects snapshots older than one frame. It is not an assembly-origin matrix. |
| `FrameNumber`, `FreshnessFrames`, `HasFreshBodyFrame` | Snapshot freshness, including the initial/unavailable state. |
| `MainPoseGeneration` | Monotonic successful-main-publication counter; runtime consumes a generation once, even if the game's frame number stops during skipped renders. |
| `TryGetBladeSegment(length, out baseBody, out tipBody)` | Emitter origin and normalized +Z times the physical metre length, in body-COM coordinates; custom avatar scale cannot change contact length. |
| `TryGetEmitterToEgo(viewport, out matrix)` | Reprojects the most recent fresh body-local snapshot using current vehicle position/orientation, for early light submission. |
| `GripOffsetFromChestCm`, `BladeDirectionModel`, `FingerCurlRadians`, `HideMmuWhenEquipped` | Explicit calibration controls for the live rig. |

The asset contract is emitter at zero, blade along +Z, grip at −0.12 m and hilt ending near −0.22 m. The skeleton is in centimetres with +Y up, +Z forward and +X kitten left. The emitter model frame is multiplied by scale 100 before the game's model matrix, so metre-authored hilt vertices remain correctly sized.

The body snapshot is computed independently of viewport state:

```text
emitter_metres_to_model_cm
    * Scale(avatar.Core.Scale)
    * RotX(-90 degrees)
    * RotZ(-90 degrees)
    * Translation(-kitten.CenterOfMassAsmbF)
```

Absolute ecliptic points follow by rotation with `Body2Cce` and addition of `GetPositionEcl()`. Subtract the camera's ecliptic position in double precision before narrowing for rendering. Reading `CharacterModel.Transform` after a render is unsuitable for physics/audio: it belongs to whichever viewport drew the kitten last.

## Verification and remaining native acceptance

`tests/PoseChecks.cs` exercises the same game-free `SaberPoseMath` used by production: row-vector quaternion convention, antiparallel/zero directions, segment-length preservation under ordinary, unreachable, folded and collinear IK targets, degenerate segment rejection, model-centimetre to body-metre axis mapping, centre-of-mass subtraction, and snapshot freshness across the frame-end increment. These checks need only `Brutal.Numerics`, not an initialized game, native skeleton or Vulkan device. The root validation record tracks the final complete build and check results.

The supplied reference repository contains character XML but not the proprietary kitten glTF mesh/rig payload. Exact finger names, paw dimensions and the preferred grip offset therefore require an in-game calibration pass. The IK measures arm lengths rather than assuming them, and the authored hilt is placed on discovered knuckles where possible, but a successful managed build cannot certify a good-looking grip.

Required native checks: stand/walk/run/jump/EVA-flight with the held hilt, swing near a vessel, second game viewport with no hand lag, pause/equip transitions, boarding, controlled-kitten switch, save reload, unsupported locomotion, and unload. Confirm the optional finger curl does not overclose the stock paw and tune exposed pose values if needed. Verify MMU cosmetics return to their original state on every exit path. Another mod that changes the same arm later in the processor list can override the pose; no global processor ordering is imposed.
