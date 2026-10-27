# Saber clashes, capture radius, and per-kitten controls

Requested 2026-10-26. Research: GPT-6.1 Sol / xhigh. Implementation: GPT-6 Luna / max, with orchestrator review and integration checks.

## Shared constraints

- Use the existing audited `GameHooks` insertion in `Program.PrepareFrame`. It runs after prior solver results and input events have been applied, before new worker dispatch. UI callbacks publish intent; only the settled handoff binds avatars, reads native clutter, or changes capture membership.
- Use each kitten's existing `KittenSaber` record. Rendering, lights, audio, clash detection, and cutting must agree on its effective, deployed blade length.
- Reuse fixed or warmed storage in frame loops. No new physics colliders, native particle emitters, extra light sources, per-frame reflection, LINQ, or unbounded event queues for these features.
- Preserve pending transaction cleanup, particularly accepted manual cuts and staged Furce breakup. Visual hit-stop must not strand worker results or partially isolated vessels.
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

## 3. Angry Furce session radius and throw pills

The corrected workflow chooses the radius before any object is captured. The former after-capture growth feature is removed.

### Gesture contract

| State and gesture | Result |
| --- | --- |
| Empty hold, Angry press/hold | Open an upward radius pill, initially 2 m at the bottom and 50 m at the top |
| Radius pill drag up/down | Choose any radius in the 2–50 m range; preview sphere remains centered on the kitten |
| Radius pill release | Enter Angry target selection with that radius; the sphere follows the hovered target |
| Separate target click | Capture the initial nearby group, then hide the sphere |
| Angry target mode, CANCEL press | Cancel target selection and hide the preview |
| Held group, THROW press/hold | Open a separate upward speed pill, initially 10 m/s and bounded by 250 m/s |
| THROW release | Throw at the chosen speed; a plain click/release uses 10 m/s |
| Escape/right-click/focus loss/modal/invalid layout | Cancel the gesture without entering a picker or throwing |

- Start every radius selection at 2 m and every throw-speed selection at 10 m/s. Neither requires Shift. Radius and speed are transient session values.
- The radius can increase or decrease before capture. Once captured, no proximity growth or additional-object capture runs for that hold.
- Do no targeting, capture or proximity queries while the radius pill is held. Only pointer movement, cached label changes and preview drawing occur.
- Snapshot the selected radius before resetting the pill. Complete idempotently through either native/backend release path; the release cannot leak into a target click.
- Validate press-time owner, world, parent and capture generation. Cancellation clears gesture references and prevents a later release from starting target mode.
- Keep the initial capture's existing exact surface/collider eligibility, nearest mixed ordering, primary-target inclusion, capture cap and rollback. Retain breakup replacement, spread and movement behavior.
- Display radius and throw speed to the right of their respective pills in regular-size text, centered on the thumb, with a dark shadow and no wrapping. Include the value labels in viewport bounds, matching Count.

### Sphere visualization

- Show a sparse sphere while selecting radius, centered on the kitten. During target picking, center it on the current hovered surface point. Hide it after capture or cancellation.
- Retain eight distinct meridians and eight latitude rings, using precomputed directions and fixed work bounds. Render filled 2-pixel dots instead of connecting dashes, green for Light kittens and red for Dark kittens.
- Reject points behind the near plane, outside the viewport or with invalid projection. Reject the entire sphere outside the frustum. Camera-inside and partially visible spheres must remain finite and bounded.
- Reuse stored geometry and draw through the existing overlay without per-frame collections, physics queries or native particle emitters.

Checks: initial 2 m thumb/bottom and upward geometry; reversible selection; kitten-centered preview; selected-radius retention and target preview; separate click capture and immediate sphere hide; duplicate release/cancellation; throw reset 10 m/s and 250 m/s maximum; shared pointer ownership; no after-capture growth; initial mixed capture and rollback; finite 2-pixel dots and zero warmed projection allocations.

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

- Manual deployment uses a finite clamped target fraction in `BladeState`. `SetManualDeploymentTarget` retains the displayed fraction and lets `Advance` approach the target at one full range per 0.15 seconds, clamping exactly without overshoot. `TargetOn` follows the requested target immediately; `HoldDeployment` remains an explicit immediate setter. Repeating the same target never restarts motion.
- Ordinary `SetIgnited` always clears manual mode, even if the on/off target is unchanged. Automatic deployment then resumes from the existing fraction. Reset clears manual mode.
- Press, drag and release map the absolute horizontal pointer position to `(pointerX - trackMinX) / trackWidth`, clamped to 0–1. Vertical movement has no effect. Release retains the final target, and the blade finishes its quick adjustment. This supersedes the original 10%/90% press baseline at the user's request.
- Manual zero retains the raised hilt while the visible blade retracts quickly. A later ordinary toggle uses the existing ignition/retraction state machine and starts from the current displayed fraction.
- Route updates through the existing settled per-kitten request stream with bounded coalescing. Preserve toggle/manual ordering and the release endpoint. Bind only at the settled handoff.
- Track the dragged kitten by object identity, never row number or controlled-owner alias. Background kittens receive the same behavior.
- Save ordinary enabled intent, length, and appearance; partial deployment itself stays transient. Explicit SLICE resumes full automatic ignition. Stable partial manual blades use their actual short segment for intentional contact.

### Layout and animation

- Split each entry into its existing top row and a full-width deployment track below it. Keep checkbox and color-swatch dimensions unchanged.
- Fill the bar directly from `Blade.Deployment`. This matches the quick manual adjustment as well as ordinary extension/retraction with the existing .3/.22-second timing; do not add a competing UI animation timer. Supported player time advances manual motion; pause and clash holds freeze it.
- Use shared draw/hit bounds with scroll clipping. Test the deployment track before the row's equip action. Right-click camera targeting remains in the top/name row.
- Consume pointer and scrolling during drag. Handle both release routes, fast press/release, focus/modal/hide/resize, owner removal/switch, world reset, and faults. Ordinary cancellation keeps the last accepted percentage; lifecycle cleanup removes stale references and commands.

Checks: absolute click/drag/release mapping, endpoints/non-finite input, no press jump on re-grab, fast exact settling and reversal, zero crossing/audio transition, pause/clash freeze, automatic resume, independent background kittens, target-based intent coalescing, queue ordering, row clipping/scrolling and persistence compatibility.

## 6. Automatic swing

### Button and duration gesture

- Add an Auto Swing circle immediately to the right of Ignite, with the same radius. Use the existing translucent black fill when off and blue when on. Keep the row below the large Swing circle so hit targets do not overlap.
- A click toggles the controlled kitten's automatic swing. A drag opens a downward duration pill, with 0.5 seconds at the top and 3 seconds at the bottom. Start each kitten at 0.5 seconds per one-way stroke.
- Preserve the duration at press and adjust relative to vertical pointer travel. A small movement threshold distinguishes click from drag; once crossed, release enables automatic swing even if the pointer returns to its starting point.
- Draw the duration in regular-size text to the right of the pill, vertically centered on the thumb, with a dark shadow and no wrapping. Include the value label in viewport clamping and draw it above overlapping controls. Cache its formatted text until the value changes.
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

## 8. Responsive Furce hover presentation

- Refresh vessel preview hits every overlay frame from KSA's current native part picker. Store the result as a value type and perform no scene scans, mesh raycasts or native physics queries in the drawing path.
- Keep capture authority in the existing settled query. Its bounded 20 Hz hover work also remains the fallback for physical ground clutter; visual previews never enqueue a capture.
- Use the current native surface point for both marker and radius sphere. A newly encountered vessel is shown as pending until the settled query confirms its identity; the sphere still previews the chosen radius there.
- Hide stale vessel feedback immediately when the native hit disappears. Retain clutter's existing world-anchored render metadata, without calling native body validity checks from drawing. Full clutter validation remains at the settled handoff.
- Cache label text and measurement by target, mode, radius and font/layout state. Stable native and clutter presentation must not allocate per frame. UI blocking, cancellation, owner/world changes and successful capture clear preview references.

Checks: current-frame hit changes without increasing full-query count; pending/confirmed identity transitions; marker/sphere agreement; click snapshot remains independent; no native clutter validation on draw; lifecycle clearing; warmed native and clutter cache allocations.

## Integration and verification

Feature ownership separates Furce code, per-kitten controls/persistence, and clash pose/audio/rendering. Shared runtime hooks and isolated test source registration receive a final integration pass.

1. Build against the supplied game DLL snapshot with warnings treated as errors.
2. Run focused managed checks for new geometry/state, initial capture, UI scheduling, profile compatibility, and audio lifetimes.
3. Run `python3 tools/validate.py --run-tests` for existing regressions and shader validation, plus `python3 tools/check_force_host.py` for changed Furce host contracts.
4. Build the Release distribution and validate its staged payload.
5. Record native checks still requiring KSA: visual hit-stop and expression ordering, contact sparkle appearance/sound, exact UI drag behavior, sphere clipping/color, and mixed vessel/clutter growth during breakup.

## Research anchors

- `src/Runtime/SaberRuntime.cs`, `.Kittens.cs`, `.SaberOverlay.cs`, `.Force.cs`, `.ManualContact.cs`: existing handoff, per-kitten state, UI capture, and transactions.
- `src/Force/ForceController.Breakup.cs`: safe inverse group-transform placement and selected-point preservation.
- `src/Pose/SaberPoseController.cs`, `SomersaultPoseController.cs`: final emitter publication and explicit pose ordering.
- `src/Persistence/KittenProfiles.cs`: optional fields and invalid-profile preservation.
- Current decompilation: `KSA/AnimatedRenderable.cs`, `KittenRenderable.cs`, `CatExpressionAnim.cs`, `CatPostAnim.cs`, `CharacterAvatar.cs`, `Camera.cs`, and `Program.cs`.
- KSA modding skill lifecycle overview: verify the supplied current host rather than relying only on its older pinned build.
