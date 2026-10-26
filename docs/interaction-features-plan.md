# Saber clashes, capture radius, and per-kitten controls

Requested 2026-10-26. Research: GPT-6.1 Sol / xhigh. Implementation: GPT-6 Luna / max, with orchestrator review and integration checks.

## Shared constraints

- Use the existing audited `GameHooks` insertion in `Program.PrepareFrame`. It runs after prior solver results and input events have been applied, before new worker dispatch. UI callbacks publish intent; only the settled handoff binds avatars, reads native clutter, or changes capture membership.
- Use each kitten's existing `KittenSaber` record. Rendering, lights, audio, clash detection, and cutting must agree on its effective, deployed blade length.
- Reuse fixed or warmed storage in frame loops. No new physics colliders, native particle emitters, extra light sources, per-frame reflection, LINQ, or unbounded event queues for these features.
- Preserve pending transaction cleanup, particularly accepted manual cuts and staged Force breakup. Visual hit-stop must not strand worker results or partially isolated vessels.
- Clear transient owner references and gestures on removal, boarding, world changes, disable, faults, and shutdown. Respect pause and unsupported warp.

The existing Debug build passed with no warnings before changes. Current reference DLLs identify KSA 2026.10.19.5638. Native visual acceptance remains distinct from a managed build or test pass.

## 1. Saber contact and hit-stop

### Detection

1. Gather only equipped sabers with live owners, positive visible lengths, eligible poses, and fresh main-view snapshots. Include a visible blade during retraction and a manually held partial blade.
2. Compute transformed endpoints once per candidate. Reject pairs outside their squared reach bounds before finite-segment closest-point geometry. Skip new-contact work for already held sabers, while continuing separation observations for existing latched pairs. Keep candidate and pair storage bounded; avoid expensive geometry between distant kittens.
3. Compare thin gameplay capsules, independent of halo width. Handle parallel and degenerate geometry and reject non-finite coordinates. Place sparks and sound at the midpoint of the closest blade points.
4. Latch a contacting pair. Trigger once; never renew the hold every overlapping frame. Require confirmed separation beyond a larger threshold and a cooldown before rearming. Missing or stale poses cannot count as separation.
5. Remove latched pairs when an owner disappears or a saber is stowed. Clear all pair storage on world reset.

This is detection between the latest rendered blade segments. It does not claim continuous collision detection for arbitrarily fast crossings between renders.

### Visual hold and angry expression

- Hold both visual kitten poses and saber deployment for 0.4 seconds of supported player time. Continue native locomotion and physical simulation; the held pose is relative to the kitten's moving body.
- `FreezeAnimation` is insufficient: stock code overwrites it, and animation processors still run. Use a persistent tail processor attached to each bound saber, with pose buffers allocated at bind.
- Explicit processor order is stock animation, somersault tuck, hand overlays, somersault root, then clash hold. The tail captures the final main-view pose and emitter, and replays them during the hold. Continue publishing a fresh emitter so lights/audio do not time out.
- Sample an available authored angry expression once at bind. Apply it after replaying the held pose for 0.5 seconds. Missing expression data degrades gracefully.
- Suspend swing, manual swing smoothing, deployment, and assisted visual progression while held. Preserve the captured visible length even if the length setting changes. Retain the latest manual-deployment request and apply it after the hold; an ordinary toggle supersedes that deferred value. Continue consuming release/stow input. A stow ends the hold.
- Block new vessel contact/cutting attempts during the hold; an accepted manual slice still pumps its existing transaction safely.

### Effects

- Play one spatial one-shot per contacting pair using the supplied `saber_clash.ogg` recording. Give it a channel independent of ignition/retraction so clashes cannot cut those sounds short. Retain the contact offset when updating its spatial position.
- Reuse the existing fixed-capacity ballistic spark pass. Clash sparks work for Light and Dark sabers, independently of the Dark ambient-spark settings, and respect the game's particle setting.

### Checks

Crossing/parallel/end contacts; variable and partial length; no overlap spam; separation rearm; stale poses; both background and controlled kittens; 0.4/0.5-second deadlines; stock chest and somersault changes during hold; correct audio point and cleanup; zero warmed detector allocations; accepted slice cleanup.

## 2. Furce part-count tooltip

- Suppress the overlay's setting tooltips while a part-count, capture-radius, or throw-speed drag owns the pointer, including when it remains over the original button.
- Restore ordinary hover tips after release/cancel.
- Avoid unrelated structural removal of the old Distance layout, which the user assigned to another task. Its capture-radius behavior moves to Angry Furce as part of the requested feature.

## 3. Growing Angry Furce radius

The user confirmed that the sphere follows the captured group's selected point.

### Gesture contract

| State and gesture | Result |
| --- | --- |
| Empty hold, normal Angry click | Enter/cancel Angry target selection |
| Held group, normal Angry click | Throw at the existing default speed |
| Normal upward Angry drag | Increase capture radius; release retains the group |
| Shift held when pressing Angry with a held group | Existing adjustable throw-speed drag; release throws |
| Downward motion during radius adjustment | Keep the largest radius reached |
| Escape/right-click/focus loss/modal/invalid layout | Cancel gesture without throwing |

- Start each new capture session at 2 m, bounded by 50 m. Radius is transient session state, not the old saved radius preference.
- Use a small drag threshold to distinguish click and drag. Once crossed, returning to the button must never turn release into a throw.
- Anchor motion to the press and current session radius. Re-grabbing cannot shrink or reset an existing hold's radius. Lock gesture type from press-time Shift.
- Complete the gesture idempotently from either existing native/backend pointer-release path. Retain the final requested value even if release occurs before the next settled handoff.

### Incremental capture

1. Publish only the highest pending radius from pointer movement, tagged with owner and capture generation. Coalesce movement and limit growth queries to at most ten per second. Unchanged, idle, and full-cap holds need no proximity scan.
2. Process release/throw before growth. Defer growth through staged breakup and apply it to the resulting current group, whose replacement pieces count against the capture cap.
3. Query around the maintained selected point, obtained from `ForceController.TryGetPoseTargetBody`. Use that same point for the sphere. It already follows group translation, spread, and breakup replacement.
4. Exclude held vehicle identities before mesh traversal and held clutter identities before nearest-result truncation. Otherwise existing objects can consume all candidate slots and prevent new captures.
5. Keep exact surface/collider eligibility and bounded traversal. Reuse scratch lists and sets. An incomplete or refused query must not append partially validated candidates, and must not repeat until another radius increase.
6. Snapshot all additions before promoting or detaching them. Do not call initial `Capture` again and do not recenter the old group. Preserve every original held record and group transform.
7. Store each new body's offset using the inverse of the displayed group transform:

   ```text
   centered = (ownerRelativeOriginalPosition - groupCenter - groupOffset) / groupSpread
   ```

   This keeps new and old pieces in their current displayed positions when spread or movement is active.
8. Publish updated immutable vehicle ownership only when membership changes. On failure, restore only the additions; retain the original group's capture, position, spread, and ability to move/release.
9. If the configured cap is below the already-held count, add nothing and release nothing. Preserve the existing absolute 512-object limit.

### Sphere visualization

- Precompute unit-sphere dash endpoints once: approximately eight meridians and eight latitude rings. Transform/project with bounded loops; no per-frame trigonometry, geometry allocation, or native capture query.
- Use green for Light and red for Dark, based on the current kitten's type, with approximately 2-pixel lines.
- Draw the same actual sphere used by selection/growth, around the hovered point before capture and maintained selected point while held.
- Clip segments at the camera near plane and viewport, including when the camera is inside the sphere. Reject non-finite projections.
- Draw beneath overlay controls. Hide with Force UI, invalid owner/camera, or modal state. Hiding the UI cancels the gesture but retains a held session's radius.
- Remove the obsolete editable global radius row; preserve old serialized data compatibility without presenting a setting that no longer applies.

### Checks

High-water drag and re-grab; 2 m reset; click/drag/Shift distinction; two release paths; tooltip visibility; coalescing and query throttling; no idle retries; old/new positions under nontrivial offset and spread; held-identity exclusion; cap after breakup; additions-only rollback; stale generation/owner rejection; near-plane and camera-inside projection; warmed geometry allocations.

## 4. Per-kitten length

- Store nullable `BladeLengthOverride` on each saber and save profile. Null means inherit the current global default; an explicit value is independent of later global changes.
- Authoritative helpers are `SaberLengthFor(KittenSaber)` for full length and `VisibleBladeLength(KittenSaber)` for deployment multiplied by full length. Replace all runtime global-only multiplications.
- Add effective length and a Use default action beside per-kitten color controls. Keep the existing .25 m through `SaberLimits.MaximumBladeLength` bounds and finite-value checks.
- Rename the shared control to Default blade length. Selection/reach information should use the controlled kitten's effective length.
- Persist the optional override in the existing version-1 sidecar. Old saves without the field inherit the default. Invalid overrides use the existing preservation path for unavailable/invalid profiles; do not discard the original record.
- Cover normal creation, restoration, unavailable-profile recovery, capture, and rebinding paths.
- Revalidate profile identity for length/reset actions and once per affected kitten per settled batch of deployment/toggle intent writes. A newly occupied seat or conflicting identity must preserve the previous person's saved profile. Deferred deployment retains enabled intent through cleanup even before the visual hold expires.

Checks: independent kittens, global inheritance versus override, reset to default, old-save loading, finite/range validation, round-trip persistence, rendering/contact/reach using visible length.

## 5. Per-kitten deployment bar

### State and input

- Add an explicit manual deployment mode to `BladeState`. A finite clamped fraction becomes the actual deployment, remains unchanged by `Advance`, and counts as on whenever nonzero.
- Ordinary `SetIgnited` always clears manual mode, even if the on/off target is unchanged. Automatic deployment then resumes from the existing fraction. Reset clears manual mode.
- On a direct slider press, begin at 10% if logically off and 90% if logically on. Horizontal movement relative to the press changes that baseline; vertical movement has no effect. Release preserves the final fraction.
- Manual zero may leave the raised hilt equipped. A later ordinary toggle uses the existing ignition/retraction state machine.
- Route updates through the existing settled per-kitten request stream with bounded coalescing. Preserve toggle/manual ordering and the release endpoint. Bind only at the settled handoff.
- Track the dragged kitten by object identity, never row number or controlled-owner alias. Background kittens receive the same behavior.
- Save ordinary enabled intent, length, and appearance; partial deployment itself stays transient. Explicit SLICE resumes full automatic ignition. Stable partial manual blades use their actual short segment for intentional contact.

### Layout and animation

- Split each entry into its existing top row and a full-width deployment track below it. Keep checkbox and color-swatch dimensions unchanged.
- Fill the bar directly from `Blade.Deployment`. This exactly matches live extension/retraction, including equip delay and existing .3/.22-second timing; do not add a competing UI animation timer.
- Use shared draw/hit bounds with scroll clipping. Test the deployment track before the row's equip action. Right-click camera targeting remains in the top/name row.
- Consume pointer and scrolling during drag. Handle both release routes, fast press/release, focus/modal/hide/resize, owner removal/switch, world reset, and faults. Ordinary cancellation keeps the last accepted percentage; lifecycle cleanup removes stale references and commands.

Checks: 10/90% press baseline, relative motion, endpoints/non-finite input, release retention, zero crossing/audio transition, automatic resume, background kittens, queue ordering, row clipping/scrolling, persistence compatibility.

## 6. Automatic swing

### Button and duration gesture

- Add an Auto Swing circle immediately to the right of Ignite, with the same radius. Use the existing translucent black fill when off and blue when on. Keep the row below the large Swing circle so hit targets do not overlap.
- A click toggles the controlled kitten's automatic swing. A drag opens a downward duration pill, with 0.5 seconds at the top and 3 seconds at the bottom. Start each kitten at 0.5 seconds per one-way stroke.
- Preserve the duration at press and adjust relative to vertical pointer travel. A small movement threshold distinguishes click from drag; once crossed, release enables automatic swing even if the pointer returns to its starting point.
- Keep the duration label visible inside the pill, without a covering tooltip. Cache its formatted text until the value changes.
- Bind the gesture to its kitten and layout. Both release routes complete it once; focus loss, modal UI, hidden controls, owner changes, invalid geometry, or removal cancel it without enabling a different kitten.
- Enabling auto swing on a retracted saber queues the ordinary ignition sequence. It preserves an already visible partial deployment. Retraction disables auto swing.

### Motion and interactions

- Store transient automatic-swing state on each existing `KittenSaber`. Background kittens continue their own motion when control switches, allowing two automatic sabers to clash. No new persistent save schema is needed.
- Apply a bounded pose offset after neutral calibration for both controlled and background sabers. Reuse the manual swing's coordinate convention: horizontal paw motion and yaw, with a small upward paw/aim arc.
- Advance a wrapped phase by supported player time divided by duration. A left-to-right stroke takes the selected duration, and the return takes the same duration. The user's follow-up requires easing concentrated near the extents: ramp velocity smoothly over the first and final 20% of each stroke, with a faster constant velocity through the middle 60%. Integrate smoothstep velocity for the end ramps, mirror the curve exactly, and normalize its total displacement. This gives zero endpoint velocity and acceleration with smooth joins to the middle. Keep the low-amplitude squared-sine upward arch.
- Ease in briefly from the neutral pose. Do not accumulate elapsed time during pause, unsupported warp, unavailable pose, or a clash. While `ClashHeld`, neither phase nor entry blend changes; the existing pose hold remains authoritative for exactly the same 0.4-second stop.
- Manual Swing and Slice disable automatic swing for that kitten before taking over. Automatic movement alone does not authorize vessel cutting. Partial deployment remains at the selected percentage.
- Use constant-size state and scalar math, with no per-frame allocations, per-frame scans beyond the existing kitten update, or new native animation processors.

Checks: toggle versus drag; duration limits and re-grab; final release value; duplicate release and cancellation; one-way timing; symmetric end easing, zero endpoint speed, and constant faster middle; shallow arch; phase continuity across a clash and pause; partial deployment; multiple kittens and control switches; warmed allocation count; non-overlapping button hit bounds.

## 7. Mouse swing audio

Requested after the dedicated clash and somersault recordings. Research: GPT-6.1 Sol / xhigh; implementation: GPT-6 Luna / max.

1. Copy the supplied `saber_swoosh.ogg` unchanged and declare a non-looping `Purr2D2SaberSwing` spatial sound. Cache its behavior once, with a channel independent of ignition, somersault, clash and slice cues. Preserve the existing swoosh base gain of 0.525 and the shared volume, pause, follow and cleanup behavior.
2. Route native cursor events and the overlay's polling fallback through `MoveManualSwing`. Record distinct finite raw mouse positions in one fixed-size gate. Four pixels of net movement from the press, scaled with the overlay, authorize the initial cue. Returning pose smoothing and automatic swings cannot authorize audio.
3. Enforce a monotonic 1,250 ms minimum interval across release/repress and ordinary cancellation. Retain only the latest motion marker, not a queue. Consume and clear it once per settled frame after `AdvanceKittenSaber` publishes freshness. Reject samples older than 150 ms, and discard cooldown-denied motion without a delayed replay.
4. Validate owner, pointer hold, controls, visible blade, pose freshness, supported speed and absence of a modal or clash at both sampling and consumption. Rebase blocked movement so motion sampled during the last clash frame cannot play after the hold expires later in the same handoff. Require fresh movement after a pause, clash or stale pose.
5. Release and cancellation clear pending gesture state while preserving cadence; world reset clears the whole gate. Retain no extra kitten references, per-frame collections or delegates. Existing visual motion and contact-slice transactions keep their normal lifecycle.
6. Run focused gate, manual-runtime and audio checks, then build and validate the release payload. Verify exact source/stage/ZIP recording bytes. Native listening still needs KSA; managed checks cannot establish mix balance.

Checks: click/subthreshold silence; first motion; duplicate input paths; stationary hold; 1,249/1,250 ms boundary; no cooldown backlog; release/repress; stale and invalid samples; pause/warp/clash and fresh-motion rearm; owner/visibility/modal cleanup; no automatic, keyboard or return-animation cue; zero warmed gate allocations; independent spatial audio lifetimes.

## Integration and verification

Feature ownership separates Force code, per-kitten controls/persistence, and clash pose/audio/rendering. Shared runtime hooks and isolated test source registration receive a final integration pass.

1. Build against the supplied game DLL snapshot with warnings treated as errors.
2. Run focused managed checks for new geometry/state, capture append, UI scheduling, profile compatibility, and audio lifetimes.
3. Run `python3 tools/validate.py --run-tests` for existing regressions and shader validation, plus `python3 tools/check_force_host.py` for changed Force host contracts.
4. Build the Release distribution and validate its staged payload.
5. Record native checks still requiring KSA: visual hit-stop and expression ordering, contact sparkle appearance/sound, exact UI drag behavior, sphere clipping/color, and mixed vessel/clutter growth during breakup.

## Research anchors

- `src/Runtime/SaberRuntime.cs`, `.Kittens.cs`, `.SaberOverlay.cs`, `.Force.cs`, `.ManualContact.cs`: existing handoff, per-kitten state, UI capture, and transactions.
- `src/Force/ForceController.Breakup.cs`: safe inverse group-transform placement and selected-point preservation.
- `src/Pose/SaberPoseController.cs`, `SomersaultPoseController.cs`: final emitter publication and explicit pose ordering.
- `src/Persistence/KittenProfiles.cs`: optional fields and invalid-profile preservation.
- Current decompilation: `KSA/AnimatedRenderable.cs`, `KittenRenderable.cs`, `CatExpressionAnim.cs`, `CatPostAnim.cs`, `CharacterAvatar.cs`, `Camera.cs`, and `Program.cs`.
- KSA modding skill lifecycle overview: verify the supplied current host rather than relying only on its older pinned build.
