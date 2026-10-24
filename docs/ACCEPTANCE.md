# Acceptance record

## Dark saber welding sparks (2026-10-24)

Dark sabers emit short colored slag streaks from the deployed blade. Each burst captures its birth position in the celestial body-fixed frame and inherits the kitten's surface-relative velocity, so subsequent paw motion does not drag existing sparks. Simple analytic spray/fall and quadratic cooling use player time. The shader multiplies the saber RGB directly, without a white core. Light sabers do not emit this effect.

Dark's shared effects expose amount, frequency, size, brightness, lifetime and spray speed, with autosave and profile reset. Fixed arrays cap all sabers together at 16 live bursts, eight sparks per burst and 32 tracked sources. Each burst uses one instanced draw of four vertices per spark in the existing glow pipeline. Distant/subpixel sources and offscreen bursts are skipped, with a 12–24 m fade. Excess work is dropped without catch-up. No emitters, GPU buffers, textures, physics queries or lights are added. Native Particles off suppresses the effect.

Focused VFX checks pass for bounded defaults/invalid tuning, zero-effect controls, distance fade, repeated viewport submissions, frequency edits, clock resets, missed windows and zero-allocation scheduling. Core settings checks cover legacy defaults, all six saved values and reset. Both GLSL pairs compile/link with the reflected 128-byte interface intact. The complete managed/shader regression run passes (`artifacts/checks/dark-sparks-validation.log`). Release builds with zero warnings/errors; source/stage validation passes, all 29 packaged files match staging, and the Release DLL and archive checksum match. Other logs: `artifacts/checks/dark-sparks-settings.log`, `dark-sparks-release.log` and `dark-sparks-stage.log`.

Native acceptance remains pending: inspect saturated red/green/purple sabers for matching-color bright births, downward streaks and complete fading; swing and walk to confirm old sparks detach; compare Light/Dark, ignition/retraction, Particles off, multiple kittens/viewports, pause and world reload. Adjust all six controls, disable each emission control with zero, restart and reset Dark to verify persistence/isolation. Compare frame times at defaults and all maxima with many equipped kittens; the hard budgets are verified structurally, not a measured GPU time guarantee.

## Repeated manual cuts and Force group spacing (2026-10-24)

An active manual cut uses a green square made from corner brackets. White “slicing...” sits above the white part name, both centered above the marker. Projection, owner/source validity and viewport clipping still apply. The Slice grace / reach tooltip uses the requested blade-length multiplier description.

A held Swing gesture can accept another rendered-surface contact after the active transaction finishes and a 0.25-second player-time cooldown passes. Detection stays paused during the transaction and cooldown, then restarts from a fresh pose/probe. Refusals and failures also throttle retries. Release and return do not create new cuts; accepted work can finish after release. Lifecycle invalidation retains the existing settled cancellation path.

Move Force reads Shift at wheel-event time. Shift-wheel up compresses captured object positions around the capture-time group centroid; Shift-wheel down expands them. The operation preserves object geometry/orientation and the group's translation, works across vessels and native clutter, and smooths toward a bounded target in the settled update. Plain wheel input retains depth control. Release and Throw use the displayed spacing; capture/lifecycle resets restore the initial spacing.

THROW is a transient upward slider: every press begins at 25 m/s, dragging upward selects up to 200 m/s, and the matching mouse release snapshots and queues one throw. A click-and-release preserves the original 25 m/s behavior. The chosen speed appears beside the nub and never persists between gestures. Hiding/focus loss, modal/control/lifecycle changes and Escape cancel the gesture rather than throwing.

Focused checks pass for manual-contact gesture/runtime and targeting, manual marker layout, Force arithmetic/controller/UI and the transient throw drag. They cover .249/.250 cooldown boundaries and fresh poses, sequential held cuts without overlap, explicit cancel/release paths, initial-idle Shift-wheel, centroid-preserving mixed spread, default/max throw speeds, one-shot release snapshots, cancelled gestures and active-pill rendering.

The final Release build has zero warnings/errors. Source/stage validation passes; all 29 archive files match staging and the archive SHA-256 verifies. Build and package logs: `artifacts/checks/slice-force-release.log` and `artifacts/checks/slice-force-stage.log`. The package is `artifacts/dist/purr2d2-controls-0.2.0.zip`.

Native acceptance remains pending: inspect marker labels near viewport edges and on long part names; keep Swing held across multiple accepted/refused cuts, release while a cut prepares, and confirm the cooldown; compress and expand mixed Force captures while dragging and changing Shift, then release/throw and switch owner; click-release THROW at 25 m/s, drag to 200 m/s, release outside the pill, and cancel through each lifecycle path.


## Mockup overlay refinement (2026-10-24)

Force warning/error text is centered above Move Force with a 25-scaled-pixel gap; ordinary picking, capture, holding and release chatter stays hidden. Saber text is centered 25 scaled pixels below the lowest jump button, in a wrap area 50% wider than before. Both areas center each measured line independently and keep explicit paragraph breaks. Force settings show rounded current values when closed and beside the active nub, with no numeric wrapping. Display rounding preserves the actual distance setting. Count and Distance have the requested short tooltips.

Force controls run from Count and Distance near the bottom toward Force and Angry Force on the right, with Angry Force raised along the circle. The existing edge gaps are preserved. The jump diamond moves upward by three quarters of a button diameter. Blue action rims use the same 25%-opacity white as the black controls. Status boxes, expanded pills and outside value labels participate in viewport bounds and overlay position clamping.

Focused CoreChecks, ForceUiChecks, ForceControllerChecks (169 assertions) and OverlayTextChecks pass. Coverage includes warning severity and clearing, closed/active rounded values without wrapping, exact tooltips and rim colors, per-line centering, explicit newlines, Unicode-safe long-word wrapping, button spacing, menu/viewport bounds and custom offsets. Narrow viewports compact the Force group to prevent overlap and retain space for full-size numeric labels; 1920×1080 and 800×600 retain the shared control scale.

The final Release build has zero warnings/errors. Source/stage validation passes; all 29 ZIP entries match staging, the staged DLL matches Release, and the archive SHA-256 verifies. Build and package logs: `artifacts/checks/mockup-ux-release.log` and `artifacts/checks/mockup-ux-stage.log`.

Native acceptance remains pending: inspect the layout and multiline alignment at different UI scales, show Force errors and successful actions to confirm quiet normal operation, drag fractional distances while watching closed/open displays, and test both control groups at each viewport edge with custom offsets.


## Overlay polish, automatic preferences and movement smoothing (2026-10-24)

Shared settings now save automatically to the mod's `settings.json` after one quiet second, including Force overlay sliders and both overlay position groups. Orderly shutdown flushes pending changes. Reset all settings deletes saved preferences and restores defaults without recreating the file until another change. Initial graphics reservations still require a restart. Settings uses collapsible Global lightsaber settings, Force settings, Overlay UI Positions and Character & grip groups, with Slice resources last and Reset all settings at the bottom. Tooltips use short paragraphs and wrap.

Manual and assisted pending cuts show “slicing...” beside the part marker. Ignite/Retract shares Slice's gap from Swing. Force's four small controls use that same circle-edge gap both from Move Force and from their neighbors. Movement circles show centered arrows; blue actions share their fill, border and text styling. Black drag circles and Force settings controls/pills have a faint white rim at 25% opacity. Force slider nubs span 80% of the pill width and show the current value in regular-size text to the right, aligned with the nub.

Force drag and wheel targets pass through a bounded, time-based critically damped spring in the settled physics handoff. Releasing the pointer settles toward the requested position; re-grabbing starts at the displayed position. Explicit Release/Throw uses the smoothed position, and capture/lifecycle resets clear movement state. The somersault combo interval defaults to 0.75 seconds and is adjustable from 0.1–1.5 seconds under Character & grip, with automatic persistence.

Managed and shader checks pass. The broad run passed every harness except a layout assertion that assumed a point outside Slice could not fall inside Ignite; the corrected CoreChecks rerun passes while retaining all-pairs circle separation checks. The final Force UI rerun includes rims and active-slider draw ordering. Release builds with zero warnings/errors; source/stage validation passes, all 29 archive files match staging, and the Release DLL and SHA-256 checksum match. Logs: `artifacts/checks/ui-refinements-validation.log`, `ui-refinements-core-rerun.log`, `ui-refinements-force-ui.log`, `ui-refinements-release.log` and `ui-refinements-stage.log`.

Native acceptance remains pending: check visual spacing and slider values at multiple UI scales; adjust settings and overlay sliders, close the window, restart and confirm persistence; reset and confirm defaults after restart; swing into a part and observe the pending marker; drag/scroll and re-grab held groups, release and throw mid-settle; exercise double/triple jumps near the configured minimum/default/maximum intervals.


## Force overlay sliders and capped nearby capture (2026-10-24)

Count and Distance join Angry Force and Force in an arc below the left movement
circle. The new circles match the existing small-button radius and Move's black
50% opacity. Holding opens a same-width downward pill; relative vertical dragging
preserves the value on press, with the thumb's top representing the minimum and
bottom representing the maximum. Releasing collapses the pill. The controls write
directly to the existing 1–512 capture limit and 1–50 m radius preferences, shared
with Settings and Save preferences. Expanded bounds and status space move together
with the Force control offsets. Release, Escape, focus/HUD loss, modal dialogs,
owner changes and layout changes end the slider gesture.

Count overflow now succeeds quietly, superseding the refusal behavior recorded
below. The selected target always occupies the first slot; the remaining slots
take the closest eligible objects across vessels and native physical clutter.
Priority uses squared distance from the selected surface hit to each object's
reference origin, avoiding additional surface scans. Existing mesh/collider checks
still determine radius eligibility. Native overlap collection is bounded and sorts
once, with stable-identity deduplication before the cap. A limit of one skips the
proximity search. Independent geometry/query budgets and transactional rollback
remain in effect.

Focused automated checks cover slider rendering, shared values, integer rounding,
range clamps, out-of-bounds motion and cancellation; layout checks include compact,
scaled and offset work areas. The mixed capture matrix covers caps 1, 32, 128 and
512, either primary type, duplicate identities and reversed enumeration. Actual
KSA/Bepu host checks exercise native overlap ordering and existing physics
contracts. Native visual/gameplay acceptance remains pending: hold each slider
at several values, drag past both ends and release outside it, resize or hide the
HUD during a drag, compare Settings values, save/reload, and capture an oversized
mixed group to confirm count, nearest selection, release/throw and frame times.

The full managed/shader suite and source/stage validation pass
(`artifacts/checks/force-controls-validation.log`). Release packaging passes with
zero warnings/errors (`artifacts/checks/force-controls-release.log`); all 29 archive
files match staging and the Release DLL, and the SHA-256 checksum verifies.

## Bounded runtime mesh growth (2026-10-23)

An exhausted runtime mesh arena can now request growth at the settled pre-draw
handoff. Automatic growth defaults to enabled, with 512/256 MiB vertex/index
ceilings above the 128/64 MiB initial reservation. Settings exposes the toggle,
live ceilings, actual shared-buffer sizes, operation count and status. Initial
reservation changes still take effect after saving and restarting; lowering a
ceiling does not shrink live allocations.

Growth preserves existing offsets and appends owned ranges after all existing
native assets. Only exhausted channels grow. Pending private uploads drain before
replacement; the native queue locks and device-idle boundary exclude submissions
during the copy and publication. Old allocation lengths are copied, with explicit
memory barriers. The ray-tracing descriptor set and its cached rebuild inputs are
updated together. Existing registered fragments remain pinned. Startup historical
assets still load into the native buffers outside the runtime slice reservation;
the UI distinguishes those global sizes from runtime ownership.

Host adaptation is limited to the audited KSA/Vulkan assembly identities and
private field contracts. Allocation and pre-publication failures retain the old
buffers. Unknown copy completion or failed descriptor rollback retains both
generations and disables growth instead of freeing potentially referenced memory.
Canceled pending slices do not request a replacement. Published eviction, buffer
shrinking and lazy historical loading are not part of this change.

Targeted settings/UI checks cover defaults, opt-out, persistence, initial/maximum
bounds and separate global-memory snapshots. Production allocator/transaction
checks cover native gaps, independent channels, fragmentation, integer overflow,
published-offset stability, cancellation, old-size copy contracts, allocation
failure and publication rollback. Live Vulkan and native visual acceptance remain
pending: force several growths with small initial arenas, both with and without
ray tracing; inspect stock meshes, cut opaque/glass fragments, shadows, picking,
thumbnails and secondary viewports. Cancel while growth waits, change ceilings,
resize/rebuild the renderer after growth, save/reload generated fragments, and
exercise allocation failures with Vulkan synchronization validation enabled.

The full managed/shader suite passes (`artifacts/bounded-growth-checks.log`),
including the capture-limit regressions. Final Release build passes with zero
warnings/errors (`artifacts/bounded-growth-build.log`). Actual game-assembly and
production-backend IL checks pass through `tools/check_mesh_growth_host.py`,
including the five-module compatibility guard, frame/queue ordering, RT bindings,
old-size copies, memory barriers and dedicated allocation cleanup. Source/stage
validation passes; all 29 archive entries match staging, the release DLL and
README match, and the archive checksum agrees. These checks do not execute Vulkan.

## Configurable Force capture count (2026-10-23)

The combined Force capture limit now defaults to 128 vessels, debris and physical
ground objects, increased from 32. Settings exposes **Force capture limit** from
1–512 and saves it with the other preferences. A clicked capture retains its
chosen limit through the settled handoff. Changing the setting affects subsequent
captures and does not release an existing hold. Count overflow still refuses the
whole group before any vessel detach or native clutter promotion. Count refusals
name the configured limit and point to Settings; native proximity traversal
budget refusals now report geometry separately.

Core preference checks, Force UI checks and the mixed Force controller fixture
pass. The controller fixture has 205 assertions, including exact limits and
overflow at 1, 32, 128 and 512, vessel-only, clutter-only and mixed groups, either
available primary, duplicate native identities, invalid limits and rollback.
Preference checks cover legacy files, bounds and save/reload; UI checks preserve
the clicked limit when preferences change before resolution. Actual KSA/Bepu
host checks pass. The Release build has zero warnings/errors, and source and
staged asset validation pass.

Native acceptance remains pending: test crowded ground scenes with 128 held
objects and increase toward 512 as appropriate for frame times. Confirm the
Settings control and save/reload, exact-count acceptance and over-count refusal,
release and throw with each primary type. The independent native query and
geometry traversal budgets are unchanged; raising the capture count does not
bypass them.

## Stable Force targeting and slice resource controls (2026-10-23)

Force and Angry Force now display CANCEL while selecting; pressing the active
button cancels, and pressing the other switches mode. Deferred picking carries
the aiming ray relative to the owner so the next physics step does not leave it
behind at orbital speed. Vessel markers follow their selected local surface;
grounded clutter markers retain a planet-fixed anchor instead of a frozen
inertial point. This follows the same current-frame/local-anchor principles used
by Unscience decals, while retaining the settled handoff required by clutter.

Clutter aim tolerance defaults to 0.20 m and is adjustable from 0–0.50 m. A bounded
native sphere sweep runs only when exact picking finds no object or kitten
blocker. It uses the actual collider contact, checks terrain along both the
original ray and the assisted contact segment, and respects the owner's reach.
Zero disables assistance. Stock tree trunk/root colliders are eligible; decorative
trees without colliders remain unavailable. The marker does not read live physics
from the render/UI callback.

Slice resources now expose usage, configurable budgets and cleanup actions in
Settings. Defaults are 128/64 MiB vertex/index memory, 2048 MiB saved assets,
8192 fragment entries and 128 local physics vehicles. Graphics reservations take
effect after saving and restarting; storage and physics admission limits apply
immediately. Completed unpublished mesh allocations can be reclaimed, including
automatically under pressure. Published native assets remain pinned. Debris
cleanup removes only uncrewed, untracked, entirely generated slice vehicles,
after confirmation and a settled-state recheck. It retains registered graphics
memory and saved assets, so exhausting published mesh memory still needs a
restart. Existing assets remain reusable after lowering storage limits.

Focused regressions cover orbital translation, rotating frames, current markers,
selection identity, tolerance bounds and capture, exact-hit priority, terrain
occlusion, native sphere/box/capsule/compound queries, resource persistence,
allocation retirement/publication, cleanup eligibility and confirmation UI.
Native host checks are recorded in
`artifacts/checks/ForceHostChecks/latest-forgiving-picking.log`. Exact and assisted
clutter queries also pass a native regression with the camera 1500 m away and a
rock 20 m from the kitten, preserving owner-based reach while extending the ray
search bound (`artifacts/checks/ForceHostChecks/latest-camera-range.log`).
The full managed/shader regression suite passes in
`artifacts/force-resources-checks.log`. The final Release build has zero warnings
or errors (`artifacts/force-resources-build.log`); source/stage validation passes,
and all 29 ZIP files, the release DLL, README and archive checksum agree.

Native acceptance remains pending: point and click at orbital vessels, Luna rocks
and Earth tree trunks while moving, zooming and rotating the camera; verify the
marker stays attached and CANCEL is visible. Compare tolerance zero/default/max,
including rocks behind terrain. Repeatedly slice, cancel during upload, inspect
usage and clear unused caches; then confirm debris removal frees physics capacity
without deleting occupied, controlled, camera-followed or mixed craft. Save/reload
remaining generated fragments and verify restart-only graphics settings.

## Saber halo over water (2026-10-23)

Replaced the host's destination-alpha-dependent additive blend with a dedicated
`One + One` RGB blend that preserves attachment alpha. Open-water alpha can be
zero, which previously erased the background under the halo ribbon. Both saber
styles, arcs and sparks use the corrected pipeline. The late translucency hook,
reverse-Z depth test and disabled depth writes are unchanged.

- Release build passes with zero warnings/errors (`artifacts/water-blend-build.log`).
- `SaberBlendChecks` uses the production Vulkan attachment state to verify faded
  edges, HDR emission and overlapping ribbons against alpha 0, 0.25, 0.5 and 1;
  it also reproduces the former zero-alpha black-border failure. Checks pass in
  `artifacts/water-blend-checks.log` and are included in `tools/validate.py --run-tests`.
- Source and staged Light/Dark GLSL compile/link with the 128-byte ABI
  (`artifacts/water-blend-shaders.log`). Asset/stage validation passes; all 29
  archive entries match the staged payload and the archive checksum matches.

Native visual confirmation remains pending: inspect both saber styles and arcs
over ocean, coastlines, terrain and atmosphere, including overlapping blades,
occluding vessels, MSAA changes and a secondary viewport. The faded halo must
leave the background intact and must not alter sunflare/transmittance behavior.

## Saber and Force refinements (2026-10-23)

Light and Dark effect defaults now match the requested profiles, with up to 20 arcs,
frequency 10, brightness 6 and width 4 supported by settings, runtime tuning and
shaders. Existing saved profiles are preserved; reset a profile to adopt its new
defaults. Shared Settings includes a 1–50 m Angry Force radius (10 m default) and
independent horizontal/vertical offsets for the complete left Force and right
saber/movement control groups. Offsets persist with Save preferences and clamp the
groups inside the viewport. Kitten rows and the Settings button stay fixed.

The per-kitten button toggles a regular movable/resizable ImGui window with a title
bar close button and ordinary scroll behavior. It no longer blocks interaction
outside the window. Existing native camera drags and overlay drags retain their
input ownership when crossing it. Ignite is now below and closer to Slice. The
somersault diamond is shifted down by half a button diameter and its neighboring
buttons are closer by the same amount.

Force depth scrolling works while hovering the Move circle as well as during a
drag; repeated events accumulate before the settled physics update. Angry Force
uses the chosen radius for both vessel mesh surfaces and physical clutter, centered
on the selected hit. The Force paw target is halfway lower toward its sampled
resting height while retaining the measured arm solve.

Focused checks cover profile defaults/reset/persistence, expanded VFX limits and
shader ABI, grouped layout bounds/hit regions, hover/drag wheel movement and
release/throw sequencing, radius bounds and pose composition. Actual KSA/Bepu host
checks cover collider overlap boundaries at 1, 2, 10 and 50 m. Logs are under
`artifacts/checks/ForceHostChecks/latest-refinements.log`,
`artifacts/refinements-build.log` and `artifacts/refinements-checks.log`.
The integrated build has zero warnings/errors. All regression groups pass; the
full run caught one kitten-profile assertion still expecting the old Light arc
count, which was updated to 8 and passed in the targeted rerun recorded at
`artifacts/refinements-kitten-checks.log`. Source/staged assets and the final ZIP
checksum also pass validation.

Native in-game acceptance remains pending: verify the new profile appearance,
toggle/move/resize/titlebar-close the kitten window, drag each position slider and
save/reload preferences, inspect the revised button spacing and lowered paw, and
scroll over the Move circle with captured clutter without holding the mouse.

## Overlay activation and refined layout (2026-10-23)

Purr2D2 starts disabled each session, independently of saved preferences. The native game menu adds **Purr2D2 → Enabled**, which activates the overlay. Disabling hides its UI immediately and releases sabers, Force captures, pending cuts and somersault state at the next settled update. A rapid off/on pair still performs that cleanup before restarting. Persisted fragment registration remains available while disabled.

Settings now opens from a dedicated button below the top-right kitten rows. Its window contains shared blade length, volume, character/grip preferences, saving and diagnostics. Per-kitten controls and appearance stay in the overlay; the F7 action shortcuts and old control-panel actions are removed. Native settings gestures retain their releases and backend input, and foreground controls clip around both Settings and the kitten popup. Ignite/Slice sit below-left of Swing, Force/Angry Force below-right of Move, and the somersault diamond remains below Swing. The white swing indicator and drag normalization share the full circle radius minus the marker radius, replacing the old 62% drawing limit.

Release builds with zero warnings/errors. The full managed regression suite and source/staged asset validation pass, including new production activation checks and circle-edge/layout checks across compact, scaled and offset viewports. Actual KSA frame/worker hook and fragment XML checks pass. All 26 ZIP files match staging and the SHA-256 verifies. Logs: `artifacts/checks/overlay-layout-build.log`, `overlay-layout-checks.log` and `overlay-layout-host-checks.log` in the same directory.

Native acceptance remains pending: launch with existing preferences and confirm no UI or F7 actions until enabling through the menu. Exercise every overlay control, the shared Settings window and per-kitten popups. Move Settings across the circles and test sliders, scrolling, dragging out of the window and native camera gestures. Drag Swing to every edge, release and re-grab. Disable during an active saber, Force capture, picker, cut and somersault; verify resources stop and physics resumes. Test rapid off/on, pause/resume, HUD/focus changes, boarding, loading and viewport resizing.

## Local ground clutter Force support (2026-10-23)

Force can select resident physical clutter with a native collider raycast, including static instances and previously displaced or sleeping rocks. Static capture invokes KSA's own displacement routine and queues its normal rendering exclusion/placement update. The game retains ownership of the collider, shape and displaced object. Held clutter becomes kinematic, with zero solver velocity and scoped contact suppression; release restores its original inertia and inherited motion, and Throw adds the existing outward 25 m/s.

Angry Force starts from either a vessel or a rock and gathers both kinds within 2 m of the primary surface hit. Vessels use mesh-surface distance; clutter uses a native sphere/collider overlap. The shared 32-object cap, deduplication and query budgets are checked before capture. The mixed group preserves relative poses and uses the existing movement control and independent Force hand pose.

GUI code snapshots a fresh camera ray into the parent frame. Both hover and click queries resolve only after physics workers join. Native identities include the original clutter key, bubble generation and simulation/shape/handle checks. Losing the owner's local bubble, changing owner/world, teardown or a partial capture releases reachable bodies and removes contact suppression. This implementation adds no orbital conversion, long-distance streaming or persistence. Terrain and decorative clutter without physical colliders remain ineligible.

The Release build passes with zero warnings/errors, and the full regression suite passes. The production mixed-controller fixture passes 78 assertions covering either primary type, the 2 m boundary, stable identity deduplication, the combined 32-object limit, shared transforms, release/throw, partial failures and local-bubble cleanup. Actual Bepu checks cover sphere/box/convex/compound/rotated collider overlap, stationary kinematic integration and full inertia restoration. Actual KSA host checks invoke the production private-field/displacement bridges, install the contact prefix on the native callback and verify native-clear invalidation. The final staged package passes validation; all 26 ZIP entries match staging and its SHA-256 verifies. Logs: `artifacts/clutter-build.log`, `clutter-checks.log`, `clutter-stage-checks.log`, `clutter-package-checks.log`, and `artifacts/checks/ForceControllerChecks/latest.log` / `ForceHostChecks/latest.log`.

Native acceptance remains pending: capture an untouched rock and confirm its original instanced copy disappears, move/turn/drag/re-grab, release, throw and recapture it after it sleeps. Start Angry Force from both a rock and a vessel with mixed objects just inside and outside 2 m; verify the shared limit refuses an oversized group without partial capture. Check a lit saber, pause/resume, focus/HUD changes, boarding, loading and a local physics-bubble change. Confirm collision/gravity resume, held objects do not push nearby vehicles, no duplicate visuals remain, and frame times stay acceptable with 32 mixed objects. Automated managed checks cannot establish these native visual and gameplay outcomes.

## Force capture, movement and throwing (2026-10-23)

The left overlay adds Force and Angry Force pickers, changing to Release and Throw while objects are held. Angry Force includes distinct vehicles with a mesh surface within 2 m of the selected hit. The group preserves each vehicle's relative position and orientation. A camera-plane drag plus wheel depth changes a persistent kitten-relative offset; releasing and re-grabbing does not spring back. The independent left-paw pose composes with the saber's right paw and the final somersault root rotation.

All scene writes occur in the existing settled handoff. Capture detaches native vehicles from their bubbles and an immutable identity snapshot filters their orphan-worker adoption. Held objects therefore receive no ordinary physics integration. Release writes their current rigid pose and inherited velocity, including angular transport; Throw adds 25 m/s outward. State uses the applied simulation epoch and the parent's inertial frame, with orbit readers joined before replacing flight plans. The native analytic overwrite is used without a full trajectory forecast on every move. Owner/world/parent/topology changes clear the hold, and capture/release failures cannot retain solver exclusion.

Work is bounded: 32 objects per group, 1,000 m target range, 25 m total offset, 8 m per drag, and 20 Hz hover queries. Angry proximity uses a bounded exact surface search; exceeding its budget refuses the capture. This initial implementation covered vessels and native debris; the follow-up above adds local physical ground clutter through its separate native storage.

The Release build and full existing regression suite pass, including the new persistent drag/layout, rigid-body math and independent Force pose checks. Focused production-controller fixtures cover capture rollback, reader-join failures, solver exclusion, inherited spin/velocity, release/throw, topology changes and stale ownership. The production UI partial is checked for deferred clicks, native UI priority, pending movement before throw, stale selections and cleanup. Actual KSA IL checks pass 120 assertions for Force/somersault patch composition in both orders; existing native frame-hook, XML and fragment checks also pass. Logs: `artifacts/force-build.log`, `force-checks.log`, `force-controller-checks.log`, `force-template-checks.log`, `force-host-checks.log`, and `artifacts/checks/force-ui-validation.log` / `force-host.log`.

Native acceptance remains pending. On the ground and in orbit, capture a single vessel, then a group straddling the 2 m threshold; move, turn and translate the kitten, drag/re-grab, change depth, release and throw. Confirm inherited motion without a position jump, collision/gravity resuming, and no change to unrelated vehicles. Repeat with a lit saber, a somersault, pause/resume, rotating parents and multiple viewports. Exercise Escape/right-click, covered ImGui windows, rapid click/release, focus loss, HUD hiding, control changes, boarding, target destruction/slicing and loading. Verify no input reaches camera or covered controls, no object stays frozen after cleanup, and acceptable frame times with 32 held objects. Managed checks do not establish these native outcomes.

## Light/Dark saber profiles and overlay controls (2026-10-23)

The user confirmed the initial shimmer/arc implementation works in game. This follow-up adds per-kitten Light/Dark selection and replaces the overlay's color-only picker with a saber popup. Color and type belong to the selected kitten for the session. Shimmer amount/speed and arc count/frequency/amplitude/brightness/width belong to independent shared Light and Dark profiles; the popup identifies the affected type. Save preferences is available in the popup, and resetting effects changes only that type. The main F7 window retains the remaining global controls.

Light and Dark use separately registered shader entry points and cached pipelines with shared source includes and a 128-byte interface. Light paths/shimmer are smooth; Dark paths/shimmer are jagged and irregular. Shared profile clocks preserve phase when fractional speed/frequency controls change. Count is capped at eight arcs/176 vertices per saber, still in one additional instanced draw; zero count/brightness skips that draw. Extended amplitude/width is included in culling bounds. The two profiles persist in settings JSON, with independent defaults for older or partial files.

Release build passes with zero warnings/errors. The targeted core/settings, per-kitten runtime/pose and VFX harnesses pass, including profile isolation, partial/null/legacy JSON, effects-only reset, per-kitten color/type isolation, fractional phase continuity and allocation bounds. Both source and staged shader pairs compile/link with matching 128-byte layouts; independent shaderc compilation passes. Source/stage validation and all 26 packaged files/checksum pass. Logs: `artifacts/checks/saber-profiles-release.log`, `profile-core.log`, `profile-kittens.log`, `profile-vfx.log`, `profile-shaders.log`, `profile-assets.log`, `saber-profiles-stage.log` and `saber-profiles-package.log` in the same directory.

Native acceptance for the new popup and styles remains pending: open it for an uncontrolled kitten with F7 closed, change color and type without changing another kitten, then edit a shared profile while two same-type and one opposite-type sabers are visible. Verify only the matching type changes. Save/restart and check both profiles; per-kitten color/type choices remain session-only. Exercise small-window scrolling, pointer capture during sliders/color picking, outside-click dismissal, control changes/boarding/HUD hiding, end-on blades and secondary viewports. Compare Light/Dark appearance and frame times at default and maximum settings.

## Blade shimmer and electrical ribbons (2026-10-23)

The existing HDR glow pass now includes subtle travelling blade shimmer and brief, blade-colored electrical ribbons. Arc paths use each kitten's stable seed and local emitter frame; they follow the current deployment and paw pose independently of camera direction. Geometry and draw submissions are bounded per saber, with screen-size and distance reductions. No particle emitter, additional scene light, collision or cutting behavior is introduced. The cosmetic clock continues during pause, matching existing spark cooling.

At most three arcs use six to ten segments each (66 vertices maximum), submitted in one extra instanced draw per eligible saber per viewport. Bursts last 0.24–0.38 seconds within staggered two-second windows; inactive instances skip path generation. The shared push-constant block is 128 bytes. Managed VFX checks cover that production layout, finite detail bounds, short/end-on blades, independent viewport detail, deterministic seeds, long-running/wrapping clocks and allocation-free policy calculations. Actual GLSL compilation/linking, reflected shader layout and SPIR-V validation pass. These checks do not execute the shader on a GPU.

Release build passes with zero warnings/errors. Full source/staged validation and all ten isolated managed harnesses pass, including the new VFX checks. The ZIP's 21 files match staging and its SHA-256 checksum verifies. Logs: `artifacts/checks/saber-vfx-release-build.log`, `artifacts/checks/saber-vfx-validation.log`, `artifacts/checks/saber-vfx-spirv.log` and `artifacts/checks/saber-vfx-package.log`.

Native acceptance remains pending. Inspect short/default/12 m blades through ignition and retraction; look along the blade and orbit around it; swing, drag the paw and somersault. Check multiple differently colored kittens in both main and secondary viewports, changing view order and control without changing the electrical pattern. Inspect against space, atmosphere and clouds and behind an intervening vessel, with MSAA on/off and after resizing. Check distant arcs fade without obvious popping, the core and tip stay readable, contact sparks retain their appearance, and shutdown/loading/boarding leave no effects behind. Compare CPU/GPU frame times at the same resolution and camera with one and many sabers, including close-up overlapping blades; bounded draw counts are not measured FPS acceptance.

## Sequenced saber toggles and ignite control (2026-10-22)

EVA checkboxes now raise the equipped hilt before ignition. Disabling keeps the blade, hand pose and audio alive while retracting, then lowers the arm before removing the hilt and restoring the MMU cosmetics. Switching direction preserves the current blade length and arm blend. Pause holds every stage; control changes preserve each kitten's pending sequence. Boarding/world changes retain immediate resource cleanup.

A blue IGNITE/RETRACT circle above-left of SWING uses the same queued request for the currently controlled EVA kitten. Its radius matches SLICE and its fill alpha is 64/255 (25%). Label and checkbox reflect the latest queued intent. Circular hit testing consumes paired presses/releases without a surrounding square, and the new control stays separate from the list, swing, slice and somersault controls across the tested viewport sizes.

Release build passes with zero warnings/errors against the shared checkout, including the concurrent somersault additions. Production per-kitten runtime/pose checks cover stage order, sound-path retention, pause, interrupted equip, reversed retraction, control switching, button targeting, queued double-clicks and cleanup. Overlay checks cover circle geometry and pointer capture from 240×220 through 3840×2160. Logs: `artifacts/checkbox-build.log`, `artifacts/checkbox-kitten-checks.log`, `artifacts/checkbox-overlay-checks.log`.

Native retest: enable/disable with each checkbox and the new circle; confirm full raise/ignite and retract/lower/stow with spatial sound. Reverse during every stage, pause/resume, switch control between two EVA kittens, board during a transition, and exercise the controls beside the new jump animations. In-game appearance and audio acceptance remain unverified.

## Material grouping and staged native isolation (2026-10-22)

New fragment assets merge render surfaces by durable material ID and glass flag into one visual SubPart, while preserving separate cap surfaces and full picking geometry. Original closed-solid grouping must survive export/reload for repeat cuts: flattening adjacent closed cubes into one authored group was shown to reinterpret volume 2 as 0.048 of backed panels. The merged format therefore preserves topology provenance independently of render-module membership. Existing generated assets remain valid and are not rewritten.

Native isolation snapshots the original external boundaries, splits the parent first, then resolves each remaining boundary's current owner. Bulk-change scopes close on every touched owning tree before the hook returns. Isolation happens only after the full swing is presented and all fragment assets/instances are ready. The final fragment creation/source removal swap occurs on a later safe update, with live motion and revalidated source geometry. Cancelling between those steps retains the intact source and separated neighboring vessels; it does not rejoin native connections.

Release builds with zero warnings/errors. Full managed checks, actual KSA XML decoding, host metadata/IL inspection, staged assets and archive integrity/checksum checks pass. New fixtures cover material/glass aliases, cap identity, exact exported attributes, touching-solid provenance, missing/malformed topology rejection, incremental restoration, parent-first owner changes, docking reroots, bulk-scope failure cleanup, stale/disposed sources, and cancellation between isolation and swap. Logs: `artifacts/slice-performance-build.log`, `artifacts/slice-performance-checks.log`, `artifacts/slice-performance-template-checks.log`, and `artifacts/slice-performance-host-checks.log`.

All six stock fixtures pass the worker/export/collider pipeline. The tank exports 2 render surfaces per fragment instead of 58, and passes persisted XML/GLB cuts through halves, quarters and eighths plus eight fresh cut seeds. The medium capsule drops from 165/145 surfaces to 3/4. Service-module variants A–D drop from 286–532 surfaces to 2 per fragment; height B specifically drops from 437/532 to 2/2. The earlier Height B run refused both repeat cuts because of the mismatched compound weld tolerance; the 2026-10-23 precision revision supersedes that result with two successful exported/reloaded seed-731 recuts. These are asset/module count reductions, not measured FPS gains. Stock logs are under `artifacts/merged-stock-*.log`; the old-exporter comparisons are `artifacts/baseline-stock-service-b-recut.log` and `artifacts/baseline-stock-service-b-audits.log`.

### User-provided baseline

The user supplied these timings from the preceding build, whose commit still includes isolation. No source-part identities or full-frame measurements were supplied. Values are milliseconds; rows retain the supplied order.

| Cut | Background | Largest preparation update | Commit | Validation | Isolation | Drain | Registration | Motion | Source removal |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 4066.4 | 7.34 | 36.25 | 11.23 | 18.44 | 0.13 | 0.36 | 1.69 | 4.13 |
| 2 | 400.2 | 0.60 | 4.50 | 0.10 | 3.78 | 0.37 | 0.13 | 0.06 | 0.07 |
| 3 | 516.4 | 0.37 | 0.54 | 0.11 | 0.00 | 0.00 | 0.31 | 0.03 | 0.06 |
| 4 | 766.2 | 0.40 | 0.38 | 0.06 | 0.01 | 0.00 | 0.20 | 0.03 | 0.06 |
| 5 | 3699.9 | 5.88 | 3.03 | 0.11 | 2.62 | 0.00 | 0.22 | 0.02 | 0.04 |
| 6 | 2048.9 | 0.92 | 0.83 | 0.10 | 0.50 | 0.00 | 0.15 | 0.02 | 0.07 |

Validation plus isolation account for 29.67 ms of the first 36.25 ms commit. The two near-zero-isolation commits take 0.38–0.54 ms. These observations support moving isolation earlier, but do not establish the cause of the first validation spike or measure engine follow-up, GC pauses, next-frame solver waits or FPS. The new diagnostics separate the earlier isolation pass from final swap time; their sum must be considered when comparing total main-thread work.

Native retest: compare cold and repeated cuts on the same leaf, root and mid-stack parts; record both isolation and swap timing plus full-frame timing. Exercise docking-root restoration, occupied neighboring branches, pause/resume, 1×/5×, cancellation between isolation and swap, stowing/switching kittens, source destruction and loading. Confirm source/fragment colliders never coexist across a physics step, separated branches preserve their crew/resources, and fragments use the source's current motion. Inspect glass/cooling caps, cut merged fragments again, and save/reload both new and old generated assets. Native frame-time improvements remain unmeasured for this revision.

## Varied repeat cuts, palette and interaction defaults (2026-10-22)

Targeted strokes run at half speed (1.24 seconds); ordinary Swing remains 0.62 seconds. Reach is twice the current deployed blade length from the live hilt to the hovered ray hit. Crew aboard no longer blocks targeting or replacement. Native destruction handles occupants of the isolated source, while separated branches retain their crew and roster assignments. Generated picking meshes now live on a native visual subpart; a constructor hook repairs old saved fragment instances before tree ownership.

Cuts first try coarse jagged facets with seeded angles and offsets, then validated planar alternatives. Untouched triangles retain their tessellation, avoiding threefold growth per repeat cut. All six stock fixtures pass the worker/export/collider pipeline: Gemini7 tank, medium capsule and four service-module heights. The tank passes exported XML/GLB reload through halves, quarters and eighths, with six faceted cuts and one plane fallback. Eight independent tank seeds pass (seven faceted). Capsule and service fixtures use planar fallbacks. Managed geometry checks cover true noncoplanar caps, shared seams, normals, UVs, holes, volume, inertia, reproducibility, cancellation and malformed-input rejection.

Each EVA row has an independent color swatch/picker without numeric RGB fields. New kittens cycle the exact requested RGB8 red, green, yellow and purple palette. Manual colors survive control changes; world reset restarts palette assignment. Picker clicks cannot toggle equipment or click through into the scene. It closes on hide, focus loss, control change, boarding or reset. Defaults are 1.4 m, grip (-24.5,-14.7,16), aim (.22,.45,.6) and curl .12 rad; saved explicit preferences are retained. Swing/slice circles are larger/smaller and farther left; the slice control toggles to CANCEL while selecting.

Release builds with zero warnings/errors. Full managed checks, actual KSA XML decoding, host metadata/IL compatibility and staged asset validation pass. Logs: `artifacts/slice-v2-build.log`, `artifacts/slice-v2-checks.log`, `artifacts/slice-v2-template-checks.log`, `artifacts/slice-v2-host-checks.log`, and `artifacts/stock-faceted-final.log`. Third-generation stock tank CPU/export/reload checks took 0.54–0.68 seconds locally; native game-thread timings and FPS remain unmeasured.

Native retest: verify the new defaults with fresh preferences; check palette, independent lighting and popup capture on controlled/uncontrolled kittens; hide during a picker drag, release and reopen. Toggle SLICE/CANCEL twice. Target a large part by its near edge at the 2× boundary, cut an occupied source and a neighboring occupied branch, and confirm crew assignment behavior. Slice new and old saved fragments through eighths and save/reload. Confirm the full slower animation precedes breakup and that complex-part fallback preparation remains responsive.

## Saber overlay and per-kitten equipment (2026-10-22)

The settings panel now has compact action rows and aligned preference controls, with contextual status instead of instructional paragraphs. Show/Hide saber overlay toggles the three chromeless, half-transparent surfaces together. EVA checkboxes equip and ignite independent kitten sabers; switching control preserves them. The fixed swing pad drives a temporary bounded arm pose and eases back on release. The red slice circle arms the existing picker, whose enlarged marker stays red for a rejected candidate and green for an eligible point. See [overlay implementation](research-saber-overlay.md).

Native retest: toggle repeatedly and close the settings panel; the overlay should remain usable independently. Enable two EVA kittens, switch control, retract one and board the other. Confirm separate poses, blades, illumination, sound and cleanup. Drag every direction and beyond the circle, release outside, re-grab during return, pause/resume, hide the HUD, resize and change UI scale. No gesture should move the widget, steer the camera, alter saved alignment or slice a part under the overlay. A camera drag that starts outside must still release normally. Click the red circle and inspect the enlarged green/red picker on reachable, distant and occupied targets. Check list scrolling with many kittens and confirm normal Swing still works.

Validation passes: Release build with zero warnings/errors; 44 overlay gesture/layout/input checks; 15 per-kitten lifetime/pose checks; existing geometry, blade, contact and targeting checks, including rejected-hover feedback. Host metadata/IL checks cover both game and ImGui-backend input seams. The staged folder, release DLL, ZIP contents and checksum agree. Logs are under `artifacts/overlay-build.log`, `artifacts/overlay-checks.log`, `artifacts/overlay-host-checks.log` and `artifacts/fragment-ownership-checks.log`. Build and managed checks cannot establish native visual/input acceptance or in-game frame times.

## Reduced breakup-frame work (2026-10-22)

Following a report of a remaining slight stutter, native fragment preparation now retains the real Part and detached PartTree for the final swap. It eliminates the throwaway preflight/descriptor copies, repeated mass/configuration repair and duplicate fragment orbit refresh. Indivisible native preparation units yield between frames; cancellation skips unstarted work. The final isolation, vehicle adoption and source removal remain on the joined game thread. Diagnostics now separate these costs and identify the slowest preparation unit.

The detached-fragment fixture checks single construction, ownership transfer without cloning, cancellation, exactly-once disposal and preparation failures against a small host model. Host metadata checks validate the actual native construction/adoption signatures. In-game retest must compare first and repeated tank/service-module cuts using both the preparation-unit and commit-phase timings; no measured FPS improvement is claimed from these fixtures.

## Cursor-selected slicing, normal swings and cosmetic sparks (2026-10-22)

The user observed a tank physically split with the previous implementation, but reported failures on service modules/capsules and severe slowdown while touching parts. They requested removal of contact-progress tracking and all autopilot functionality, retaining a first-class slicing swing and visual contact sparks.

The first asynchronous revision kept rendering responsive but still delayed the animation until plane preparation and then cut it short on a missing-collider error. The user's 4K/60 FPS video, `media/2026-10-22 20-23-33.mp4` in the surrounding workspace, shows this failure on a service module. Its diagnostics report 1551 ms background preparation, 1.30 ms largest preparation update and no native commit. These are observations of the previous revision, not acceptance of the fix.

**Swing / Alt+F7** is restored. **Select part to slice / Ctrl+Alt+F7** now arms point-and-click part selection. An in-range click starts the original 0.62-second swing without waiting for geometry; even preparation errors let it finish. Ready results commit on the next settled update after final-pose presentation. Late preparation leaves normal flight and ordinary swings available. Reach is checked at selection, and the flight-computer target is independent. Background CPU work, incremental capture/registration, nonblocking uploads and cosmetic 20 Hz contact sparks remain.

The missing-collider failure came from using a plain XML serializer: KSA's unannotated component list requires element overrides. The private worker serializer now retains the exported collider, picking and render modules. Validation rejects missing components before registration, and native preflight failures after successful registration no longer quarantine a reusable template.

Slicing reach now follows the actual visible blade: hilt-to-selected-surface distance must not exceed deployed blade length. Both native hover and fallback ray selection enforce it, with another check at stroke start. The previous 3 m minimum, extra metre and automatic extension are removed. The regular swing audio fix also keeps the cutting sizzle exclusive to slicing strokes.

### Automated evidence

- Release build against KSA 2026.10.19.5638 succeeds without warnings/errors.
- Managed checks exercise blade/settings behavior, 0.62-second swing timing at 30/60/144 FPS, immediate motion with a late worker result, cursor selection/occlusion, empty-space refusal, camera-distance-independent reach, movement after selection, incremental fallback and commit guards, worker publication/nonblocking cancellation/failure handoff, final-pose presentation with early/late results, exactly-once completion, geometry clipping and physics math, float32 repair, and GLB serialization.
- Blade-reach fixtures cover exact tip distance, just-outside refusal with a 0.25 m blade, longer/partially extended/retracted lengths, fallback parity, movement before the stroke, and rotated hilt offsets where the kitten centre is outside blade reach. These and the contact fixtures pass in `artifacts/blade-reach-checks.log`; build results are in `artifacts/blade-reach-build.log`.
- A separate check using actual KSA 2026.10.19.5638 managed types reproduces the plain-serializer component loss and verifies that the corrected loader preserves normal/glass render modules, picking, collision mesh references, subparts and mass. It does not initialize native templates or start a physics/graphics world. On ARM the check uses an ignored copy with only the unsigned IL-only assembly's COFF machine fields adapted; the original DLL and distribution are unchanged.
- Contact checks exercise two-sided intersection, blade-tip bounds, rate limiting, continuation past the triangle budget into later primitives, current-transform validation, disposed targets and visual effects on crewed vessels. A warmed 624-part stand-in scene over 1,800 frames allocates **zero managed bytes** for sustained contact. This is a managed probe measurement, not a native game FPS result.
- Locally installed stock assets pass automatic selection, closed-surface/volume checks, principal-frame float32 conversion and actual GLB export: `CoreFuelTankA_Prefab_LF3W3HA`, `CoreCommandA_Prefab_MediumCapsuleVariantA`, and `CoreServiceModuleA_Prefab_SetAHeightA/B/C/D`. Tank axial/oblique and capsule axial cuts also pass. Proprietary source assets are not committed or packaged.
- Service modules required repairing float-collapsed edge T-junctions, increasing compound/export group limits, and increasing the bounded convex-hull input-point budget. The native GLB writer is exercised by these fixtures so its group limit cannot silently disagree with preflight.
- All six stock fixtures also exercise the actual worker export, prepacked upload sizes, separate full-detail picking/compact collision meshes, and Bepu reconstruction of the saved collider. The local run produced 10–276 collision points per fragment, compared with roughly 7,000–50,000 render/picking points. CPU preparation was about 1.1–1.7 seconds off the game thread; these timings exclude game asset registration, graphics and world mutation. Full worker coverage exposed and fixed the old 100,000-triangle per-piece refusal on taller service modules; the limit now matches the exporter's 200,000-triangle bound.

Local ignored logs: `artifacts/point-slice-build.log`, `artifacts/point-slice-checks.log`, `artifacts/point-template-checks.log`, and `artifacts/point-slice-host-checks.log`. Stock geometry evidence from the unchanged geometry/export pipeline remains in `artifacts/async-stock-all.log`. Run the following checks from the repository:

```sh
dotnet build Purr2D2.slnx -c Release
python3 tools/validate.py --run-tests --stage artifacts/stage/purr2d2
python3 tools/check_fragment_templates.py
python3 tools/check_stock_slices.py /path/to/Content/Core
dotnet run --file tools/inspect_host.cs -- /path/to/KSA.dll bin/Release/net10.0/Purr2D2.dll
```

### Required native retest

These checks do not execute KSA's Vulkan/FMOD pipeline, native fragment spawning or save reconstruction. The previous user-observed tank cut does not establish acceptance of this replacement workflow.

1. Beside an uncrewed Gemini7, press **Select part to slice** from an unequipped state, point at its service module and click. Confirm a marker names the exact part, ignition and the immediate full 0.62-second swing, then separating textured pieces with sparks and cooling cut caps. Repeat on the tank, all service-module variants and an empty capsule. Check that preparation keeps rendering responsive and that a ready result has no extra delay after the stroke. Record the preparation/commit timings, especially for a cold first slice. Inspect glass, door details, normals and transformed parts. A preparation refusal must let the full swing finish and leave the source intact.
2. Set another vessel as the flight-computer target and confirm cursor slicing still selects the clicked part. Click empty space and an out-of-range part; neither may silently pick a nearby substitute. Test 0.25 m, 1 m and 12 m blades: the selected surface must be within that length of the hilt, and slicing must not extend it. Check while igniting, camera zoomed out and the kitten rotated. Move out of reach before the stroke starts; it must refuse. Move away or change the flight-computer target after a valid stroke; the pending cut must continue. Hide the kitten during the stroke (motion waits) and after it (ready breakup can proceed). Cancel during selection, reading, worker calculation, upload and the finished-stroke waiting period; also switch kittens and board. No cancelled request should mutate a part or block rendering while cleanup finishes. Retry after cleanup. Occupied vessels must refuse structural slicing.
3. Check the restored **Swing / Alt+F7** separately, including while a completed slicing stroke waits for its pieces. It should match the original speed and full recovery. For the historical cosmetic-only revision, hold and manually sweep a lit blade across a part. That revision produces sparks without destruction; the current intentional-contact revision below supersedes this behavior. Move clear; sparks must stop emitting. Verify contact effects on a crewed vessel. Check sustained contact frame time against the prior slowdown.
4. Check pause/resume, 1×/2×/5× and higher warp, HUD visibility, ignition/retraction, walking and MMU movement. Confirm no autopilot panel or flight-control override remains.
5. Inspect the blade and sparks against Earth, atmosphere/clouds, dark space and an intervening vessel; retain correct occlusion. Repeat after resizing and changing MSAA, and in a secondary viewport. Inspect hand alignment and sound.
6. Verify fuel is emptied only from the isolated source, surviving neighboring parts remain usable, and fragment mass/motion are plausible. Test another cut on generated wreckage. Geometry/arena/native isolation refusals must keep the original or report a retained isolated source.
7. Save, quit and reload with `Generated/Slices/` preserved. Check generated template lookup, geometry, collision and inertia; saved cut caps reload cold. Exit normally and inspect logs for graphics/audio/physics cleanup errors.

The enclosing convex hull collider may fill visible recesses or holes. Functional modules and attached IVA furnishings are removed when a part becomes wreckage. Complex geometry can add preparation latency after the swing, but CPU calculations and disk I/O no longer run in the frame callback. Native registration/preflight calls have a soft frame budget; the final native topology transaction remains atomic. Actual frame-time acceptance still requires the game.


## Quiet breakup revision (2026-10-23)

Implemented: boost-invariant inherited-spin motion; named 0.24 m/s relative opening with balanced momentum; selected-source draining and engine/actuator quiescence before the intervening solver; persistent firing-nozzle cache clearing; owned source/neighbor and replacement/sibling contact suppression until native overlap clearance; deterministic inert authored-subpart fallback through the ordinary durable asset/preflight transaction. Cancellation after isolation retains a separated empty source with engines off. The two-safe-update lifecycle remains required.

Managed validation covers zero/surface/orbital velocity boosts, unequal masses and shifted geometric COM, opening energy, source-only drain ordering, native collision-pair duplicates/copying/clearance, retained overlap after cancellation/disable, world/bubble/disposal cleanup, unload handoff to native retirement, stale-nozzle clearing, and deterministic bounded module-free fallback including the production geometry-worker path. Actual KSA IL/install/JIT checks verify all three module-update call sites; these checks do not run a physics world.

Release build passes with zero warnings/errors. Source/stage validation, existing managed suites, the new targeted motion/isolation/contact/quiescence/fallback suites, and actual KSA IL/install/JIT/XML checks pass. The initial full validation run exposed a test-stub warning in the new contact harness; its corrected harness was rebuilt and passes separately. Logs are under `artifacts/checks/quiet-breakup-*.log`.

Native acceptance remains pending: slice full/empty tanks, firing/idle engines, extended/retracted legs (free and ground-supported), occupied capsules and generated fragments at surface/orbital speeds and with off-center fuel/rotation. Verify no slice-caused blast, spin spike, contact failure or delayed failure cascade; preserve fueled neighbors. Exercise every cancellation/spawn failure phase, world/bubble changes, long-lived hull overlap, and save/load of both sliced and authored-fallback assets. Record pre/post-isolation/post-swap/next-solver motion, failure events and protection lifetime. Terrain remains collidable; this implementation does not claim blanket native explosion immunity or measured performance improvement.

## Repeat-slice precision revision (2026-10-23)

Rebuilt geometry checks pass local topology tolerance, exact common compound plane, rotated/scaled/translated multiscale solids, deterministic face-order welding, and copied-input T-junction repair at a large assembly offset. Negative generated-input cases reject missing surfaces, nonmanifold duplicate faces, duplicate shells and irrecoverable float collapse; failed generated geometry cannot receive authored panel backing or authored-breakup fallback.

The stock regression uses the locally preserved `artifacts/checks/stock-slices.json` extracted from stock assets. The configured 2026.10.19.5638 reference snapshot retains XML but does not include the atlas GLBs needed to regenerate that fixture. An older local Linux asset checkout identifies build 2026.8.19.5261 and was not substituted for the baseline. No proprietary assets are committed. All six stock fixtures pass. Both Height B exported/reloaded halves successfully recut at seed 731 with mass/volume conservation and no authored fallback; the prior exemption is removed. All seven tank repeat transactions reach halves, quarters and eighths with true facets, and all eight fresh tank seeds use true facets. Full stock results and Release/asset checks are recorded in `artifacts/checks/repeat-slicing-*.log`.

Native acceptance remains pending: perform recuts after actual game save/load, including transformed attached fragments, and inspect material/glass seams, cap appearance, positive mass/inertia and collision behavior. Finite float precision, mass/resource budgets, invalid contours, general mesh self-intersections and unsupported native transforms can still cause explicit refusal.

## Configurable assisted slice reach revision (2026-10-23)

Settings now exposes **Slice grace / reach**, from 1× to 4× visible blade length, defaulting to the previous 2× allowance. The row explains the distance rule and shows effective maximum selection distance in metres at current and full deployment. Changes apply before a stroke starts; **Save preferences** persists `SliceReachMultiplier`. Old files without this property retain exactly the previous 2× behavior. Finite extremes clamp to 1–4× and nonfinite multipliers recover 2×.

One shared helper calculates maximum hilt-to-hit distance for native hover, fallback broadphase/selection and stroke-start validation. Targeting takes that explicit maximum in metres without another multiplier. Accepted strokes use separate ownership/geometry checks and can finish after moving out of reach; range-bound APIs reject infinite distance. Visible blade length, physical contact and Force clutter tolerance retain their existing behavior.

Rebuilt targeted checks pass: **595 CoreChecks PASS checks** and **95 TargetSelectionChecks/contact PASS checks**, with no compiler warnings/errors. Coverage includes defaults and legacy settings, custom save/load, finite clamps and nonfinite recovery, exact/just-outside 1×/2×/4× boundaries at 12.5%/50%/full deployment, hover/fallback/incremental agreement, camera-independent surface distances, rotated/moving live hilts, changed range before acceptance, accepted completion after movement, disposed target refusal and infinite-range refusal. The generated CoreChecks project was regenerated from the current harness after its older source list omitted the previous revision's `ClosedMeshPreparation`; no production fix was needed for that stale project. Release build against KSA 2026.10.19.5638 passes with zero warnings/errors and rebuilds the installable folder/archive. Logs are under `artifacts/checks/slice-grace-core.log`, `artifacts/checks/slice-grace-targeting.log` and `artifacts/checks/slice-grace-build.log`.

Native acceptance remains pending: inspect the Settings row and wrapped metre readout at small window sizes/UI scales; compare red/green hover and clicked fallback selection at 1×/2×/4×; change reach while selection/preparation is pending; verify the live hilt is rechecked before motion and movement after acceptance does not cancel the cut. Save preferences, restart and confirm the chosen multiplier. Verify ordinary blade contact and Force near-miss selection are unchanged. The managed checks do not establish native visual/input acceptance.

## Bounded intentional SWING contact cutting (2026-10-24)

Implemented: a monotonic ID for each real SWING press/regrab; one accepted or refused rendered-surface contact per gesture; lit/full-deployment, changing-drag and physical-motion gates; incremental bounded candidate discovery and target-frame reads; frozen swept-ribbon queries with triangle cursors and explicit age/discontinuity invalidation; two-sided/coplanar/grazing/axial contact confirmation; world-scale motion/tolerance handling; structural/subpart freshness checks; and the shared quiet async cut transaction without the assisted animation. Ordinary release/recovery and intervening solver/render gaps preserve accepted preparation. Explicit hide/focus/pose/owner/world/stow/disable or Force conflicts cancel through the settled handoff. Resource maintenance is blocked while manual work/detection is active. Manual cuts use the automatic geometry chooser; an exactly swing-aligned seam is not implemented.

Managed fixtures compile the production sweep, candidate probe, gesture state, targeting policy and manual runtime partial. They cover thin surfaces missed by endpoint segments, coplanar/grazing/axial cases, rotating/nonuniformly scaled/translating targets, moving visual subparts, budget continuation with frozen endpoints, query expiry, discovery limits, no idle/ignition/return cuts, one transaction per press, no-step/no-fresh handoffs, release/recovery while waiting for the worker, isolated ownership rebasing and commit on a later valid update, cancellation and conflicts. Host boundaries remain stubbed; passing these does not establish gameplay or native performance.

Validation passes: the complete source/stage/shader and managed run reports **1,543 PASS lines** with no compiler warnings/errors or failed checks. After the final edge-case additions, projects regenerated from the current `tools/validate.py` source lists report **132 TargetSelectionChecks/contact PASS checks** and **27 ManualContactRuntimeChecks PASS checks**. The final Release build against KSA 2026.10.19.5638 has zero warnings/errors and rebuilds the installable folder and ZIP. Logs: `artifacts/checks/manual-final-managed.log`, `artifacts/checks/manual-final-TargetSelectionChecks.log`, `artifacts/checks/manual-final-ManualContactRuntimeChecks.log`, and `artifacts/checks/manual-final-build.log`. These are managed/build results; observed contact timings in stub scenes do not establish native frame time.

Native acceptance remains pending: sweep thin legs at 30/60/144 FPS and supported simulation speeds; test moving/rotating/scaled targets, blade base/middle/tip and maximum length, visible recess misses, occupied craft, no self/other-kitten cuts, and no automatic debris recursion. Verify idle/ignition/recovery touches stay cosmetic. Profile cold/warm discovery and query timing separately on dense craft/debris, record saturation/expiry/missed-contact rates, and compare the proposed 0.25 ms p95 warm target with the initial 0.5 ms soft update budget. Test long preparation after release, pause/resume, rendering gaps, SLICE/Force conflicts, every cancellation/failure phase, source movement/replacement/disposal and save/load recuts. Hard unit caps and the soft clock are not measured real-time guarantees.

## Double and triple somersault jumps (2026-10-24)

Implemented: up to three jumps per airborne combo, with each extra deliberate press within an inclusive 0.5 seconds of the previous accepted press. A monotonic unscaled clock captures input before observing newly applied solver results; the timestamp survives the settled queue and worker delay. Follow-ups keep the original direction and add the first launch's upward speed without replacing ascent/descent, horizontal velocity or reference-frame motion. Prepared-worker transactions consume each impulse once, preserve native liftoff/contact history and await applied jump acknowledgement. Extra smooth visual turns accumulate without snapping or discarding prior progress, and the pose adapter renders through three full rotations. Landing closes the combo; pause closes it and cancels an undispatched boost while retaining an already applied boost for acknowledgement after resume. Native collisions and tumbling remain authoritative.

Managed fixtures cover exact/just-outside/decimal-roundoff boundaries, the chained third window, rejected duplicates/fourth presses, additive ascent/descent boosts, distinct coordinate frames, current fractional rotation, complete double/triple turns, rendered second/third poses, short early-contact recovery without rewinding completed turns, paired release/press input, delayed workers, repeated settled ticks, pause before dispatch and before acknowledgement, contact between queue and preparation, failure atomicity and ownership cleanup. The integration fixture links the production input partial and motion controller: first/second native results are applied without a prior controller update, then real input requests refresh acknowledgement before eligibility checks. Low-frame-rate processing retains the original press timestamp. Draw-time availability has no state-changing refresh.

Validation passes on projects regenerated from the current `tools/validate.py` source lists: **628 CoreChecks**, **110 SomersaultWorkerChecks**, **52 SomersaultPoseChecks** and **534 ForcePoseChecks PASS lines**, plus the production somersault runtime arbitration suite. There are no C# compiler warnings/errors in these runs. The Release build against KSA 2026.10.19.5638 passes with zero warnings/errors and rebuilds the installable folder and ZIP. Source/stage validation passes; the ZIP has one `purr2d2/` root, only the mod DLL, byte-identical staged contents and a valid SHA256 checksum. Logs: `artifacts/checks/multi-final-affected.log`, individual `multi-final-<suite>.log` files, `multi-final-build.log` and `multi-final-package.log`. No unrelated full regression suite or native gameplay run was performed for this revision.

Native acceptance remains pending on Earth and Luna: all four initial directions from standing/running; deliberate second/third presses near each half-second boundary; late/fourth presses and held buttons; mixed direction buttons; ascent/descent boosts; low frame rates; early contact, slopes and tumbling landings; pause before/after boost dispatch and resume; manual saber drags, assisted slicing and Force composition; control/owner/world changes, RCS, unsupported warp, disable, boarding and unload. Observe every full turn, momentum addition and input release in the real game. Managed checks do not establish native animation appearance, landing behavior or frame time.
