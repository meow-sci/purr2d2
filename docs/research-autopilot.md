# EVA cylinder cutting autopilot research

## Goal and operating model

The feature is intentionally a human-assisted close-proximity program rather than a general rendezvous or obstacle-avoidance system. The player uses KSA's stock flight-computer target, parks the EVA kitten beside the desired cut station, selects the target-body cylinder axis, captures the ring, inspects the aligned pose, adjusts roll in 90-degree increments, and explicitly starts one cutting lap.

The captured geometric state is expressed in the target vehicle's body frame: a signed axial station, normalized cylinder axis, initial radial direction, raycast surface radius and configured clearance. Keeping this state target-relative makes it follow both orbital translation and vehicle spin. During each settled simulation handoff, guidance converts the desired ring point and tangent velocity through CCI into the kitten body frame.

## Native control path

KSA's ordinary EVA path suppresses general vessel translation flags unless the kitten is in `LocomotionMode.Mmu`. In MMU mode, `PhysicsBubble.FlightComputerInputsFor` consumes `Vehicle._manualControlInputs.ThrusterCommandFlags`. The controller therefore uses that native input seam rather than applying forces directly. It selects `FlightComputerManualThrustMode.Pulse`, enables RCS, and maps a bounded target-frame PD acceleration to the six discrete translation flags.

Attitude uses the kitten's existing `FlightComputer`: auto attitude, ECL-body reference frame, custom track target, decoupled roll and a conservative rate limit. The desired quaternion points the fresh rendered blade direction, converted into kitten body space, radially inward and uses the selected cylinder axis as the secondary orientation reference. Before the first rendered publication it uses the pose system's explicit model-to-body transform as a fallback. Quarter-turn roll choices rotate the kitten around the blade without changing the blade's inward aim.

All command publication occurs in Purr2D2's checked `Program.PrepareFrame` insertion after stock input application and before new vehicle solver work is dispatched. This is the same safe handoff used by cutting. The host inspector verifies both the ordered IL seam and the exact private `Vehicle._manualControlInputs : ManualControlInputs` field before the build is considered compatible.

## Surface capture and moving-target math

Capture casts from outside the selected radial line toward the axis and calls native `Part.RayCastEgo` across the target's parts. The nearest closed-surface intersection defines the local hull radius. Guidance then computes:

- desired position: captured axial point plus the rotated radial vector at `surface radius + clearance`;
- desired tangent speed: configured orbit speed around the selected axis and direction;
- target-point velocity: target CCI velocity plus `target angular velocity × relative position`;
- translation demand: bounded proportional position plus derivative relative-velocity feedback;
- cut attitude: blade inward, cylinder axis as roll reference.

The geometry unit tests cover all three axes, ring closure, tangent velocity, bounded PD output, monotonic full-turn progress, inward blade alignment and quarter-turn preservation.

## Ownership and fail-safe behavior

Before alignment, the controller snapshots attitude mode/frame/target, custom target, manual-thrust mode, roll mode, RCS mode, rate limit and kitten control mode. Completion or abort first clears translation flags, then restores those values. The runtime separately snapshots whether the saber was equipped and ignited.

Guidance refuses or releases control when the target changes or disappears, the kitten leaves MMU, parent bodies differ, the target leaves the physics bubble, relative distance/velocity exceeds the close-proximity envelope, the step is invalid, the scene changes, or the user aborts. Pause and unsupported warp clear translation commands. A cut lap advances only while the pose system reports fresh rendered blade geometry; otherwise the kitten holds the current point.

## Deliberate limits

This is not collision avoidance. It assumes the chosen station behaves like a closed cylinder and does not account for protruding appendages. The surface radius is sampled only along the initial radial line; irregular hulls can therefore violate the configured clearance elsewhere. Native gameplay must verify MMU flag directions, attitude conventions, achievable hold error, roll presentation, target-spin tracking and safe restoration before using valuable craft.

The implementation was informed by the local KSA coffer references for driving flight-computer attitude modes, solver-phase autopilots and treating EVA kittens as rockets, then checked against the supplied 2026.10.19.5638 decompilation. No direct-force or teleport control path is used.
