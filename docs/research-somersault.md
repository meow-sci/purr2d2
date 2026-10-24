# Grounded jumping somersaults: research and recommended plan

Implementation addendum (2026-10-24): [the current controls](somersault.md) extend the implemented first jump with two optional airborne boosts. Each deliberate press must be within 0.5 unscaled player seconds of the prior accepted press. `KSA/Program.cs:2299–2315` applies solver results before polling input; the mod's settled update follows `InputEvents.ApplyInputEvents()`. Input callbacks therefore capture a monotonic `Stopwatch` time and refresh the controller's applied snapshot before checking the combo. `Program.GetPlayerTime()` still contains the previous frame's time during those callbacks because `_lastPlayerTime` is published later (`KSA/Program.cs:2336`), making it unsuitable for a precise low-frame-rate input window. The captured clock is unscaled and survives queue/worker delay; draw-time availability remains free of state changes. `VehicleUpdateState.Kinematic` remains the prepared `NewKinematicStates` reference (`KSA/VehicleUpdateState.cs:161`), so each boost adds only a radial-up delta there once. It must never reuse the initial desired-velocity correction's subtraction of current velocity. Preserve `LiftoffTime` and `ContactClearedSinceLiftoff` on boosts because native liftoff grace depends on them (`KSA/KittenLocomotion.cs:150`). Additional acknowledged turns sum independent smooth angle contributions, preserve the first direction, and render through 6π. Native trajectory/collision handling remains authoritative and no physical landing stabilization is added. This addendum is verified by managed fixtures and source/build checks; native double/triple gameplay acceptance remains pending. The original planning report below is retained as historical design context.

Research date: 2026-10-22. **Planning only; no feature implementation or in-game validation.**

Three GPT-6.1-sol subagents at xhigh reasoning investigated physics, pose/animation, and overlay integration. Findings are checked against the local KSA **2026.10.19.5638** decompile, not assumed from the older coffer examples. KSA source commit: `edda84495fcdc5592c80e1e6442573cd3419354a`. Purr2D2 base commit: `173b7d4771305f4aa565573d7598ec9ed57225cb`, including the existing working-tree changes present during research. Those changes are not part of this report's implementation.

## Recommendation

Use **a one-time launch through native physics, plus a procedural visual flip**. Let KSA simulate gravity, flight, collision and landing. Rotate the rendered skeleton around a pelvis/body-centre pivot without deliberately spinning the physical vehicle. Add a procedural tuck where the runtime rig supports it; fall back to rotating the existing pose if the necessary bones cannot be resolved.

This supports forward/backward somersaults and left/right sideways flips. Left/right mean airborne side flips, not cartwheels with hands planted on the ground. Directions are relative to the kitten's heading at launch, projected onto the local ground tangent plane. Camera movement during the action must not redirect it. Preserve heading after a successful full turn.

Use a chosen leap height and approximate travel distance to derive launch velocity from local gravity. This gives a compact move on Earth and a naturally slower move on Luna or Mars without a table of body names. Freeze pose progress with simulation pause. Landing is contact-driven; an estimated flight duration must never teleport the kitten onto the ground.

## What the source establishes

Game paths beginning `KSA/`, `RenderCore.Animation/` or `RenderCore.Gltf/` below are relative to the sibling repository's `../ksa-game-assemblies/current/decomp/`, from the Purr2D2 repository root. Purr2D2 source paths are relative to its repository root; explicitly relative reference links are relative to this document.

| Evidence | Finding and consequence |
|---|---|
| `KSA/KittenLocomotion.cs:382`, `:403` | Stock grounded jump emits `LocomotionCommand.JumpImpulse`, sets `JumpConsumed`, `JumpFired`, and `JumpFiredTime`. Reuse these semantics instead of inventing a disconnected animation-only jump. |
| `KSA/KittenServoPrecomp.cs:83`; `KSA/PoseIntegratorCallbacks.cs:122` | Despite its name, `JumpImpulse` is a delta-velocity, distributed across solver substeps before being added to velocity. Do not multiply it by mass as though it were a momentum impulse. |
| `KSA/KittenLocomotion.cs:427` | The native airborne command is inactive and retains facing. Airborne motion need not be driven by repeated teleports or walking traction. |
| `KSA/KittenLocomotion.cs:30` | Mode transitions handle terrain contact, upright alignment, grace time, tumbling, swimming and RCS/MMU. An active RCS mode can switch a jumping kitten to MMU. Physical flips would interact with the tumble/rightening thresholds. |
| `KSA/PhysicsBubble.cs:1922`, `:2051` | Locomotion facts include local up, gravity, fresh terrain contact, velocity and orientation. Worker-side locomotion produces state copied back to the main thread. |
| `KSA/KittenLocomotionTuning.cs:168` | The stock jump delta-velocity is 3 m/s. This is evidence of the native path, not necessarily an adequate acrobatic preset. |
| `KSA/AnimatedRenderable.cs:147` | Fresh sampled local poses run through `AnimProcessors` once per frame; world skeleton construction and `UpdateSkeleton` run per viewport. This supports non-accumulating procedural pose composition. |
| `src/Pose/SaberPoseController.cs:277`, `:317` | Purr2D2 already edits local bones and publishes the saber from the resulting world skeleton. Whole-body animation must compose before that final skeleton is sampled. |
| `KSA/KittenRenderable.cs:528`, `:607` | Stock jump animation is a jump-intro / airborne-flail / landing chain. It is not an existing four-direction somersault action. |
| `src/Runtime/GameHooks.cs:54` | Existing safe main-thread seam runs after solver results and input events are applied, before the next workers start. UI requests should enter through this seam. |
| `src/Runtime/SaberRuntime.Kittens.cs:83`; `src/Runtime/SaberRuntime.SaberOverlay.cs:237` | Checkbox rows toggle saber equipment/ignition. They do not select a group of movement targets. |

The engine API makes the proposed approach plausible and provides concrete integration points. These sources cannot prove that a tuck looks good, that the authored pivot clears the terrain, or that a Harmony hook is reached in the optimized game. Those are explicit prototype gates below.

## Physics and timing design

### Why not take over the complete trajectory?

| Approach | Advantages | Costs | Recommendation |
|---|---|---|---|
| Native launch + rendered flip | Native gravity, collisions and landing; stable physical orientation; bounded patch scope | Requires careful launch/state synchronization and collision-aware animation recovery | Use this |
| Physically rotate the kitten body | Collider follows the turn | Fights upright/tumble/rightening behavior; changes contact and possibly camera behavior | Do not use for the initial canned move |
| Repeatedly set position/orientation along an authored arc | Exact cinematic path | Must recreate collision sweeps, slopes, terrain heights, reference frames, interrupted flight and landing | Reserve for a future explicitly cinematic mode |

### Launch profile

For initial tuning, choose apex height `h` above takeoff and approximate level-ground travel `d`. Read actual local gravity magnitude `g` from the native physics environment. Use native local up, and project the kitten's forward/right directions onto its tangent plane.

```text
v_up = sqrt(2 * g * h)
T_estimate = 2 * v_up / g
v_tangent = d / T_estimate
```

These are constant-gravity, equal-elevation ballistic estimates. Drag, slope, obstacles, uneven landing elevations and native launch distribution change the actual outcome. Use them to initialize the motion and pose schedule, not as a promise of an exact landing position.

For illustration only, `h = 1 m` gives approximately 0.90 s at `g = 9.81`, 1.47 s at `g = 3.71`, and 2.22 s at `g = 1.62 m/s²`. These are calculated examples, not measured KSA acceptance results or final animation tuning. A fixed launch delta-velocity instead makes both height and airtime proportional to `1/g`, which is substantially more dramatic in low gravity. That could be an optional later “constant effort” preset.

For a predictable canned action while walking, compute a bounded *change* from current surface-relative velocity toward the selected launch velocity. Do not stack the entire launch speed on top of an existing run, and never replace a global/inertial velocity with a small local walking vector. Preserve the planet/frame velocity. If required correction is too large, decline the launch rather than abruptly cancelling high-speed motion.

Initial eligibility: a live controlled EVA kitten, genuinely grounded on terrain, upright enough for native grounded locomotion, valid gravity, supported simulation step, no active acrobatic action and no active slice stroke. Walking is allowed. Swimming, ladders, seated crew, free flight, near-zero gravity and high time warp are outside the first implementation. Grounded support on vessel decks needs separate investigation because the current facts distinguish terrain contacts; do not promise it from these findings.

Start with normal 1× simulation speed. Pause freezes the current action and does not queue a launch for later. A later increment can support additional physics rates after testing. If warp changes during a move, release custom motion/pose ownership safely; never teleport to the estimated landing point.

### Concrete launch integration to prototype

The preferred implementation is a single velocity change using KSA's mutable physics state and normal worker snapshot, with a scoped locomotion-state handoff. This avoids needing a new Bepu integration callback or rewriting the entire trajectory. It is a researched proposal, not a tested patch:

1. Queue a direction/owner token through `BeforePhysics`. A prefix on `KittenEva.PrepareWorker(SimStep)` accepts it only for a finite, positive upcoming step and a still-eligible owner. This provides the actual upcoming step rather than inferring it from player/render time (`KSA/KittenEva.cs:283`).
2. Read `GetPhysicsStates().Environment.GravitationBub.Length()` for gravity (`KSA/PhysicsStates.cs:950`). Obtain current surface-relative position, velocity and orientation through `GetStatesCcf` (`KSA/ReadOnlyPhysicsStates.cs:68`). Physical body forward is +X and up is -Z (`KSA/PoseIntegratorCallbacks.cs:15`); these differ from model-space axes. Project forward into the tangent plane and derive right with `Cross(up, forward)` as the native fallback does (`KSA/KittenLocomotion.cs:363`).
3. Compute `deltaV_Ccf = desiredLaunchVelocity_Ccf - currentVelocity_Ccf`. Transform the delta through inverse `body2Ccf` and then `Body2Phys`. Add it once to `GetPhysicsStatesMutable().Kinematic.VelocityPhys`, and call `TakeOffRails()` (`KSA/Vehicle.cs:999`). Do not modify position, rebuild an orbit, or add a second native `JumpImpulse`.
4. At the corresponding `VehicleUpdateState.PrepareFromVehicle` handoff, after native kinematics have been copied, initialize the armed kitten's worker locomotion to Airborne with the correct start/liftoff time, contact-clear latch and captured facing. Replace stale grounded traction/jump commands with an inactive airborne command and zero jump impulse; publish `UpdateData.NewLocomotionState`. Coordinate jump event timestamps with the native animation chain if that chain is retained. The copied state is at `KSA/VehicleUpdateState.cs:300` and `KSA/VehicleUpdateData.cs:84`.
5. Consume the token once and acknowledge launch from applied state. Native `ConstraintSim.UpdateSimFromVehicle` pushes kinematics into the solver and wakes off-rails bodies (`KSA/ConstraintSim.cs:283`, `:333`); native task results update the kitten's public locomotion state (`KSA/KittenEva.cs:1027`). Do not reapply a launch on later worker preparation calls.

Require RCS disabled for the first prototype and show that reason in availability. If the player enables RCS during flight, release the action to native control. Supporting an automatically temporary RCS override later requires explicit ownership/restoration testing; no global locomotion tuning edits are needed.

If movement/grab input is temporarily suppressed, apply those scoped overrides after the final character/ladder snapshot. `KittenEva.PrepareWorker` calls `UpdateLadderDetection` and `PrepareCharacterWorkerState` after its base preparation; overrides written only in `PrepareFromVehicle` can be overwritten later. Preserve unrelated inputs and never mutate another kitten's worker.

Validate both hooks before allowing a launch, including optimized/inlined execution. If a snapshot postfix is bypassed, use a uniquely matched, guarded helper insertion at the concrete call in `Vehicle.PrepareWorker`, following the existing `GameHooks` approach. Failure before dispatch must not leave a half-applied velocity/state transaction. The prototype must explicitly test stationary/asleep kittens, native grounded traction, held stock jump, worker substeps and wake-up behavior before this is accepted as the final implementation path.

### Action lifecycle

1. **Ready / queued:** a click captures kitten identity and direction. Revalidate after the current solver results are applied. Accept one request; discard repeated clicks while busy.
2. **Anticipation:** short, optional crouch while still grounded. Revalidate before launch; stepping off an edge cancels an unlaunched request.
3. **Launch pending:** publish a one-shot physics request. Do not start the full visual rotation merely because the UI accepted a click.
4. **Airborne:** advance from confirmed applied simulation time after the native launch acknowledgement. Blend into a tuck and execute one turn.
5. **Extend:** untuck and finish upright before the expected contact, adjusting to live descent/contact evidence. Use a short gravity-scaled phase margin rather than completing the rotation exactly at the mathematical touchdown instant.
6. **Land / recover:** actual native contact/state transition ends flight. Blend back to native standing/walking and release input ownership. A wall, early landing or tumble interrupts the flourish; recovery is more important than forcing a full turn.

If terrain falls away, finish the turn, hold an appropriate extended falling pose and let native physics continue. Do not repeat flips to fill the extra airtime. An action timeout releases mod ownership without altering velocity. Track “has actually become airborne” and native liftoff grace so the takeoff frame's remaining contact is not misread as a landing.

Own only the inputs needed to make the canned move deterministic. Held jump must not cause a duplicate launch, and enabled RCS must not silently turn the move into powered MMU flight. Prefer scoped worker-input overrides with ownership tokens to changing global bindings or repeatedly overwriting player settings. When the player intentionally requests an incompatible mode, hand back to native control and blend out; do not keep fighting the player. Hiding the overlay or losing focus clears pointer capture, but does not stop an already launched ballistic flight.

## Visual flip and tuck

Use the current avatar's skeleton, not a separately rendered replacement kitten. This preserves the native skin, fur and attachment system. Resolve the skeleton hierarchy and an anatomical pivot at bind time. A whole-body transform should reach every relevant skeleton root; blindly rotating a node named “root” around its existing origin may make the kitten orbit around its feet.

Recommended composition:

1. Native sampled animation provides the fresh baseline.
2. Procedural tuck bends available spine/hip/knee/ankle and arm joints, preserving bone lengths.
3. Saber grip/guard solves the equipped hand without fighting the tuck; the saber hand should keep a usable grip and blade direction rather than being forced fully into the torso.
4. Apply the full-body rotation about the chosen model-space pivot.
5. Let the engine rebuild the final world skeleton. Publish hilt/blade, lights and spatial-audio positions from that same result.

Treat this as one explicit ordered pose composition, or coordinated processors with verified list order. `IAnimProcessor.Priority` is not a general ordering mechanism. A late skeleton edit after the existing saber publisher would leave the blade/contact frame stale; avoid it. Preserve existing per-viewport rebuild and main-view snapshot freshness rules.

Keep root rotation procedural even if a tuck clip is authored later. Compute a continuous scalar angle through `2π`; interpolating between “identity” and “360°” quaternions alone produces no turn because those orientations are equivalent. Verify axis/sign conventions using all four directions in the actual rig.

For row-vector transforms, the pivot operation is `Translate(-pivot) * Rotation(angle) * Translate(pivot)`, composed into every root with `ParentIndex < 0`. Derive a stable model-space pivot from the physical COM using the inverse model-to-body conversion, or calibrate a fixed pelvis-centred pivot after rig inspection. `src/Pose/SaberPoseMath.cs:14` and `KSA/KittenEva.cs:1044` establish the existing model/body/COM conversion. Do not accumulate a second visual trajectory on top of the native jump.

The tuck is a visual pose, not a physical resize. The collider remains its native size. Do not shrink the whole avatar, change its mass, or promise it can pass through smaller gaps. Validate helmet, backpack, hilt and blade clearance, particularly on Earth where airtime is shortest. Native attachment transforms use the final head/chest/socket bones (`KSA/KittenRenderable.cs:362`), so they should follow the root flip. Ordinary follow-camera position uses the physical target (`KSA/Camera.cs:116`); portraits can read animated head/neck positions (`KSA/CrewPortraitPanel.cs:391`). Head-attached camera mods need separate validation.

Missing leg/spine bones should disable only the tuck where possible. The fallback is a rigid visual full turn using the supported whole-body transform. Failure to establish a safe visual root/pivot should disable the action with a clear reason rather than launch an invisible or broken flip. Binding must work with the saber unequipped; the current saber processor's equipment-dependent lifetime is not sufficient as the action owner.

### Blender path

Blender MCP can be available during implementation for posing and authoring a polished tuck. It is not necessary for the first feasibility prototype and no new textures are required.

The provided reference snapshot does not include the complete proprietary kitten rig/animation GLBs. Exact lower-body bone names, joint axes and pleasing joint limits therefore need runtime inspection or access to the user's installed assets. Do not assume a generic humanoid rig can be dropped into KSA.

The current importer loads the first glTF animation and drops animation channels mapped to bone index zero (`RenderCore.Gltf/GltfUtils.cs:192`). `AnimationClipSystem.cs:42` does not provide arbitrary name-based retargeting. If authored clips are used, export one action per file with verified matching hierarchy/index ordering, or explicitly implement and validate a remapping layer. Procedural root rotation avoids relying on an exported root-animation channel.

Unscience's `../../unscience/kitten-animations.lib/KittenAnimationDriver.cs:162` demonstrates overriding clip selection immediately before `AnimatedRenderable.UpdateAnimation`, after native selection and before sampling. Use that pattern only if an authored clip becomes necessary. Native `BoneAnimRuntime.cs:110` wraps time, so a forced one-shot clip must be phase-sampled/clamped or released before it loops. Procedural tuck offsets avoid this extra clip-selection ownership for the first version.

## Overlay behavior matching the supplied sketch

Keep the kitten checkbox list at the top right, swing and slice controls above, and add four circular controls in a diamond below them:

```text
                FORWARD
        LEFT               RIGHT
                BACKWARD
```

All four operate on the currently controlled kitten. They work without a saber equipped. Keep the existing show/hide-overlay switch and appearance settings. Use the existing circle drawing and geometric hit-testing path; separate square ImGui button windows would give incorrect click regions and could leak clicks into camera controls or the slice picker.

Extend the same press/release ownership rules: one launch per click, no hold-repeat, no launching by dragging across a button, captured release remains consumed outside the circle, and hover alone never becomes a launch. Disabled controls still own clicks within their visible circles, with a concise reason such as “Land first”, “Finishing slice”, or “Paused”. Empty corners remain click-through.

Layout must budget the entire group, not append fixed coordinates to the current panel. Current circles sit at half viewport height, and the existing slice status starts immediately below the swing circle—the requested diamond occupies that space. Move/wrap the status, reserve room for the full group, and adapt circle sizes/gaps to the viewport and UI scale. At extremely small viewports use a compact layout or hide the action group with an explanation rather than create overlapping/invisible click targets.

Arbitration: beginning a move can exit uncommitted slice selection. Refuse it during an actual slice stroke. Once the stroke is fully presented, existing background cutting work may continue while the kitten moves, consistent with current behavior; do not undo committed isolation or cancel a pending cut merely because a somersault starts. During the flip, reject new swing/drag/slice commands that would compete for the pose. Keep cosmetic sparks separate from structural damage as the current mod does.

## Implementation sequence and validation gates

1. **Compatibility and launch prototype.** Validate exact method signatures/IL against the game DLLs, instrument a per-kitten command ID, prove one launch, native airborne state, gravity-driven motion and native landing. Test Earth and Luna before expanding the action. Confirm no extra launch under held jump, multiple substeps or duplicate render/UI calls.
2. **Plain visual rotation.** Implement a standalone action state and ordered pose layer, with no tuck initially. Prove four directions, correct pivot, a full turn, stable physical body and clean landing. Confirm the followed camera and multiple viewports; do not assume head-attached cameras behave like ordinary chase cameras.
3. **Tuck and saber composition.** Discover runtime bone names/axes; add leg/spine tuck, then safe arm/guard behavior. Validate fur, helmet, MMU and hilt attachments, lighting/audio/contact positions and optional authored Blender polish.
4. **Overlay integration.** Add the four circles, command routing, availability reasons, responsive sizing and existing pointer capture integration. Keep checked saber rows independent from the motion target.
5. **Hardening.** Exercise pause/resume, step/warp changes, collisions and slopes, camera/control switch, boarding, destruction, save/load, overlay hide, focus loss and unload. Release only mod-owned state and processors.

Suggested implementation units are a pure `SomersaultSequence`/motion-profile model, an engine-facing physics bridge, an equipment-independent acrobatics pose layer, and an overlay/runtime partial. These are proposed responsibilities, not files implemented by this research.

Meaningful automated checks should cover gravity timing, frame transforms and bounded velocity changes; state acknowledgement and once-only launches; no pose accumulation and correct 360° rotation; per-owner isolation; and circle hit testing/capture/layout. Extend host inspection to every new private/worker seam. Build checks alone are insufficient for the native solver and rendered rig.

Native acceptance must cover forward/back/left/right from standing and walking on Earth and Luna, with Mars as another gravity sample; saber on/off; slopes and early impacts; held movement/jump/RCS; standing on an edge; repeated clicks; varied frame rates; paused frames; camera switching and secondary viewports; and interruption cleanup. A successful default move launches visibly, completes one turn, slows naturally at lower gravity, reaches a contact-driven landing and returns to walking without another jump, stuck input, pose drift or camera spin.

No runtime tests were performed for this report. Launch-hook behavior in the optimized game, exact rig geometry and in-game presentation remain to be demonstrated before claiming the feature works.

## Research references

- KSA coffer skill: `../../../.agents/skills/ksa-coffer-skill/SKILL.md`, particularly lifecycle safe mutation, kitten animation processors, and UI overlay/input references. The corpus targets build 2026.10.10.5554; current source checks above take precedence.
- Existing project research: `docs/research-pose.md`, `docs/research-saber-overlay.md`, and `docs/COMPATIBILITY.md`.
- Unscience animation example: commit `1829b3b298dd0fa5a67342cafff29074c8c29286`, `kitten-animations.lib/KittenAnimationDriver.cs`.
- Source line anchors describe the inspected working tree and may move with future changes.
