# Runtime selectable slice detection and plane strategies

Implementation specification, 2026-11-02. Status: implemented with native acceptance outstanding. The default-off passive setting, runtime detector and plane choices, queued vessel and clutter processing, shared resource lanes, per-stroke provenance, opt-in extended clutter coverage, bounded diagnostics and copied-mesh replay capture are wired. Cached ribbon, capsule sweep and extended clutter coverage are experimental: host checks do not prove live KSA timing, GPU retirement, physical-cell readiness or the 50 m forest scenario. The configured memory limit combines estimates with tracked retained buffers; it is not a hard total managed/native heap bound. Game sources do not provide a complete contact-time source-wide geometry revision: foreign in-place mesh mutation requires explicit revision invalidation. The work packages below specify intended behavior and acceptance gates; live measurements must be recorded separately before claiming those gates pass.

Baseline inspected: `ffe7d49337bd05e592cafa5a9f6bb53dfdea91d6`, plus the current working tree. Recheck the named symbols before editing because other work can change line numbers. Paths for proposed files below are relative to `/Users/asherwin/wip/purr2d2-initial/repos/purr2d2`. Existing source links are absolute for navigation in Codex.

## 1. Outcome and implementation order

Change the default activation policy: new cuts require the player to click-drag the manual SWING control or explicitly use the Slice button and confirm a target. Walking, jumping or ordinary pose movement must not start cuts by default. Add a persisted **Cut during ordinary movement** boolean, default off, to opt into passive contact cutting. Apply this policy consistently to vessel parts/subparts, trees and rocks.

Preserve existing detection algorithms, plane behavior and processing cost within an authorized interaction, while allowing independent runtime selection of vessel contact detection, clutter contact detection, contact plane selection, and work scheduling. Add a queued mode that records many targets from a swing and prepares several cuts concurrently. Upload and world mutation remain at the existing settled game-thread handoff. References below to legacy parity exclude this intentional activation-policy change.

Deliver in the numbered work packages in section 12. The first release boundary is the activation toggle, extraction and runtime switches with baseline algorithms preserved. The second is alternative detectors and plane strategies. The third is bounded queued processing and long-blade clutter coverage. Do not make experimental algorithms the default as part of this work; the intentional-only activation default is required immediately when the trigger refactor lands.

The 50 m forest acceptance case requires three independent capabilities: physical clutter coverage across the blade, detection of multiple intersections, and a queue that keeps accepted cuts alive while geometry is prepared. Worker parallelism alone supplies only the third capability.

Do not expand this into arbitrary curved incisions, damage accumulation, finite-depth kerfs, GPU mesh cutting, or a rewrite of clipping/capping. All strategies still select a whole-object section for the existing mesh pipeline. A finite blade contact can therefore split the entire target across that section. Authored subparts are hit surfaces, while the replacement unit remains the owning structural part.

## 2. Verified current behavior and extraction sites

This table describes the inspected implementation. Its unrestricted clutter movement trigger is deliberately replaced by section 3.1; do not preserve that trigger as the new default.

| Concern | Current behavior | Source |
| --- | --- | --- |
| Safe execution window | `GameHooks.SettledUpdate` calls `SaberRuntime.BeforePhysics` after the previous workers join and results/input are applied, before new physics dispatch. Ordinary ImGui callbacks run outside this mutation window. | [GameHooks.cs](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Runtime/GameHooks.cs:86), [SaberRuntime.cs](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Runtime/SaberRuntime.cs:151) |
| Manual vessel trigger | Requires intentional held SWING, valid fresh poses and motion. One gesture attempt enters flight; completion starts a 250 ms cooldown. Contact detection stops during slice work. | [manual runtime](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Runtime/SaberRuntime.ManualContact.cs:73), [gesture](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Core/ManualContactGesture.cs) |
| Vessel narrow phase | Tests a ruled ribbon between blade endpoint samples against actual `PositionsCompare` triangles. Samples use each target's own frame. The query returns one confirmed target; an earliest triangle hit is refined within that target, not globally across every part. | [ManualContactProbe](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Rendering/ManualContactProbe.cs:11), [BladeSweep](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Cutting/BladeSweep.cs:3) |
| Vessel budgets | Per advance: 0.5 ms soft budget, 4,096 triangle tests, 64 discovery units, 128 frame reads, 256 primitive reads, 128 retained candidates. Query age 150 ms; confirmed hits may publish before a full scan after bounded refinement. Ribbon subdivision limit 16 and nominal approximation tolerance 2.5 mm. | [ManualContactProbe constants](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Rendering/ManualContactProbe.cs:14) |
| Automatic clutter trigger | Held, lit, deployed saber movement can cut trees/rocks without held SWING. CCF endpoint samples avoid treating planetary rotation as blade motion. Minimum travel is 2 mm; maximum sample interval 200 ms; deployment, scale, teleport and clash guards apply. | [clutter runtime](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Runtime/SaberRuntime.ClutterSlice.cs:21) |
| Clutter narrow phase | Current blade ray plus eight blade-point trajectory rays. Each query retains nearest native clutter collider; first accepted target ends the scan. At 50 m the trajectory sample spacing is 6.25 m. Global attempt spacing 250 ms; accepted target cooldown 10 s. | [sampling](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Runtime/SaberRuntime.ClutterSlice.cs:73), [nearest-only handler](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Force/ClutterForceTargets.Queries.cs:77) |
| Contact planes | Preferred normal is `normalize(blade × motion)` through the contact; collinear motion chooses a deterministic perpendicular. Tries preferred plus 24 tilts (up to 0.6 radians), then up to three part-axis planes. Axis fallbacks are not limited to 0.6 radians. | [ContactSliceGeometry](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Cutting/ContactSliceGeometry.cs:7) |
| Assisted vessel planes | Chooses broad interior sections, with up to three faceted attempts followed by planar candidates. Animation starts independently of completion of geometry preparation. These are not contact-derived blade planes. | [AssistedSliceGeometry](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Cutting/AssistedSliceGeometry.cs:6), [SliceGeometryWork](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Slicing/SliceGeometryWork.cs:51) |
| Assisted clutter | Uses a picked surface and synthetic movement derived from blade direction and radial up. It does not follow the vessel assisted sequence. | [assisted clutter selection](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Runtime/SaberRuntime.ClutterSlice.cs:115) |
| Material-specific fallbacks | Trees prepare structural wood separately from visual foliage. Rocks retry failed contact cutting with `Contact = null`, enabling interior/unanchored sections. Failed clutter slicing can topple the original object. Toppling is not a successful mesh cut. | [ClutterSliceGeometry](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Cutting/ClutterSliceGeometry.cs:10), [ClutterSlicer](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Slicing/ClutterSlicer.cs:106) |
| CPU work | Copied `SliceInput` data goes to one `LongRunning` task per active cut on `TaskScheduler.Default`, with below-normal priority. Geometry, export and expensive convex-hull search already happen off the game thread. | [BackgroundSliceWork](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Core/BackgroundSliceWork.cs:12), [SliceColliderGeometry](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Slicing/SliceColliderGeometry.cs:12) |
| Game-thread preparation | Incremental source copying, GPU upload, asset registration, native shape creation and detached native part preparation. Each current capture/upload has its own soft budget; multiple instances would multiply that cost. | [source capture](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Cutting/ClutterSliceSourceCapture.cs:24), [upload](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Rendering/RuntimeFragmentAssets.Async.cs:79) |
| Commit | Vessel isolation and replacement span separate settled updates. Clutter creates and validates replacements before excluding/removing the source. Cancellation must respect those irreversible boundaries. | [vessel runtime](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Runtime/SaberRuntime.ManualContact.cs:128), [clutter commit](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/src/Slicing/NativeSlicePhysics.Clutter.cs:11) |

Current source preparation, precision repair, cap construction, mass/inertia calculation, foliage handling and native breakup rules are shared services, not detection strategies. Keep their behavior unchanged during extraction.

## 3. Independent choices and persisted settings

Use stable string IDs for strategy choices in JSON; do not persist ImGui indices or enum ordinals. Booleans and numeric limits retain their native JSON types. Add these properties to `SaberSettings`; validate strategy IDs with explicit allowlists and default missing/null/unknown IDs to the values below. An unknown stored ID produces one diagnostic on load, not a message every frame. Keep a requested ID when its native capability is temporarily unavailable and display the effective fallback separately.

| Property | Default | Choices to implement |
| --- | --- | --- |
| `PassiveContactCutsEnabled` | `false` | Boolean; allows contact cuts during ordinary movement without manual SWING; applies to vessel parts/subparts, trees and rocks |
| `VesselContactDetector` | `legacy-ribbon` | `legacy-ribbon`, `cached-ribbon` |
| `ClutterContactDetector` | `legacy-rays` | `legacy-rays`, `all-hit-rays`, `capsule-sweep` |
| `ContactPlaneStrategy` | `legacy-contact` | `legacy-contact`, `blade-strict`, `blade-limited`, `sweep-fit` |
| `SliceExecutionMode` | `legacy-serial` | `legacy-serial`, `queued` |
| `SliceWorkerCount` | `2` | Integer 1–4; effective count cannot exceed `max(1, ProcessorCount - 2)` |
| `SlicePendingTargetLimit` | `128` | Integer 16–512 |
| `SliceWorkingMemoryMiB` | `256` | Integer 64–1024; admission estimate limit, not a promise about total managed heap size |
| `SliceClutterReachExtension` | `false` | Enables scoped native clutter residency extension, section 9 |

Add one ImGui section named **Slicing methods**, adjacent to the existing slicing sections. Use the existing table helpers and autosaver. Put **Cut during ordinary movement** first as a checkbox, visible in both execution modes. Its tooltip is: "Off: cuts start only while click-dragging SWING or after selecting a target with Slice. On: movement of your held, lit blade can also cut vessels, trees and rocks while walking or moving normally. Turning this off stops new passive cuts; accepted cuts can still finish." Other labels: **Vessel contact**, **Tree and rock contact**, **Contact cut plane**, **Processing**, **Geometry workers**, **Pending targets**, **Working memory**, **Extend clutter coverage to blade**. Show worker/queue controls only for queued processing. Display requested and active modes while a change is draining. Tooltips explain accuracy, fallback behavior, and when changes apply. The host-checked alternatives are exposed as explicitly experimental for comparison; their native acceptance remains open until the game tests below pass.

These contact plane settings apply to manual and optional passive contacts for both target domains. Assisted selection and its planes retain existing behavior in this iteration. Record `TriggerKind` explicitly: `ManualContact`, `PassiveContact`, `AssistedVessel`, `AssistedClutter`; record target kind separately and never infer either from `Contact == null`. The strategy registry must support future assisted strategies, but do not invent blade-motion evidence for a click.

All combinations are defined. A multi-hit detector with `legacy-serial` feeds the existing admission policy and records how many hits were suppressed; it does not secretly enable a queue. A legacy detector with `queued` still offers only its original hit per query; the UI explains that this combination cannot collect all intersections. `capsule-sweep` applies only to clutter; a vessel compound sweep lacks the subpart information and render coverage needed for equivalence.

### 3.1 Activation policy and toggle semantics

The activation policy is independent of detector, plane and scheduler selection. Implement one shared settled-thread policy (`SliceActivationPolicy`) upstream of both vessel and clutter detectors. Do not hide the toggle inside a clutter strategy or leave legacy methods able to bypass it.

| Player interaction | Toggle off (default) | Toggle on |
| --- | --- | --- |
| Walking, jumping, idle pose motion, or a target moving into a held lit blade; no SWING drag | No new contact query, cut intent or destructive slicing fallback | Passive detection allowed subject to existing blade, motion, locality and safety guards |
| Valid click-drag of the manual SWING control | Manual detection for vessel parts/subparts, trees and rocks | Same manual detection; it takes precedence over passive detection |
| Slice button followed by an accepted target selection | Existing assisted target selection/cut, for vessels or clutter | Same assisted behavior |
| Another UI drag, ordinary mouse press, or merely opening Slice selection | No authorization for ambient contact cuts | Passive detection only if otherwise eligible and not blocked by assisted selection/UI arbitration |
| Dropped/stowed/unlit blade, invalid owner/scene, Force interaction or clash | No new contact detection | Same guards; the toggle does not override them |

A manual contact interval requires the actual SWING control's owned drag (`_overlayDrag.Held` with a valid gesture identity), fresh published poses, and the existing manual drag-motion threshold (`Drag` delta squared >= `0.000001f`) plus physical blade-motion guards. A stationary mouse held over the control must not authorize a cut merely because the player walks. Reuse existing pointer capture/release/cancel semantics; a generic left-mouse-down flag is insufficient. Both target domains consume the same manual intent without consuming it twice. In queued mode, use stroke authorization separately from `ManualContactGesture.InFlight` so the first accepted target cannot disable later targets from the same drag.

Passive mode uses fresh pose samples even without held SWING. Add an explicit authorization kind to the probe context: the vessel detector bypasses only its manual gesture/drag-delta requirement for `PassiveContact`, retaining physical-motion thresholds, sample-age, ownership, scaling, teleport and per-target rules. Do not fake drag deltas or synthesize mouse clicks. `PublishManualPose` currently suppresses samples outside held SWING; factor a shared pose publication path for passive detection while retaining the manual adapter. `legacy-ribbon` still uses its existing intersection algorithm. Passive detection remains scoped to the player's controlled, held saber; this toggle does not enable cutting by dropped sabers or every NPC.

Manual input takes precedence when passive cutting is enabled: run each selected domain detector once per pose interval, label the interval `ManualContact`, and dedupe through the same target reservations. With no manual intent, use `PassiveContact` only if the toggle is on. Assisted selection remains exclusive under section 8.7; choosing Slice never enables ambient contact detection of unrelated targets.

Apply toggle changes at the next settled handoff, without waiting for an execution-mode drain. Turning it off clears only incomplete passive queries, passive provisional confirmations and their endpoint history; no new passive evidence may be admitted afterward. Turning it on primes a new passive sample pair, and never sweeps across movement performed while it was off. Switching between manual and passive authorization also re-primes history instead of bridging two trigger epochs. Ordinary SWING release can finish already-confirmed/accepted work as specified below; with the toggle off it cannot create new contact intervals.

Already accepted jobs retain their authorization and may finish when this toggle changes, in either execution mode. Do not fold the toggle or current drag state into source-validity/commit permission checks or automatically call `CancelClutterSlice` because input authorization ended. Use **Cancel pending cuts** for explicit cancellation. Unaccepted manual query/provisional work may finish only against its frozen, originally authorized interval and existing age limits; it cannot sample post-release blade motion. The queue's ordinary release behavior remains intact.

Declare `public bool PassiveContactCutsEnabled { get; set; } = false;`. Old preference files without the property load as false, and Reset settings restores false; do not infer true from the old always-active clutter behavior. Persist through the existing settings autosaver. When off and no authorized manual interval, assisted operation or pending authorized work exists, do not run cut-contact discovery/narrow-phase queries. Separate visual sparks and optional clutter-residency prewarming may continue, but neither can enqueue a cut or invoke a destructive fallback.

## 4. Architecture and ownership contracts

```mermaid
flowchart LR
    Pose[Published blade pose] --> T[Manual or enabled passive authorization]
    T --> D[Selected detector at settled handoff]
    D --> E[Contact evidence and target lease]
    E --> A[Admission and deduplication]
    A --> C[Budgeted source capture]
    C --> P[Selected plane strategy]
    P --> G[Existing geometry and export]
    G --> L[Detached asset loading]
    L --> U[One upload and native preparation lane]
    U --> W[Validated world commit]
```

Introduce these files; names are prescriptive so later agents have stable integration points:

| New file | Responsibility |
| --- | --- |
| `src/Slicing/Strategies/SliceStrategyIds.cs` | IDs, descriptors and capability checks; no reflection-based plugin discovery |
| `src/Slicing/Strategies/SliceStrategyOptions.cs` | Validated immutable options snapshot and monotonically increasing options generation |
| `src/Slicing/Detection/SliceActivationPolicy.cs` | Shared manual/passive authorization, trigger epochs and default-off passive toggle; separate from job validity |
| `src/Slicing/Detection/SliceContactEvidence.cs` | Pure evidence and sweep value types |
| `src/Slicing/Detection/ISliceContactDetector.cs` | Settled-thread detector boundary, caller-owned output buffer and explicit counters |
| `src/Slicing/Detection/LegacyVesselContactDetector.cs` | Thin adapter over `ManualContactProbe` |
| `src/Slicing/Detection/LegacyClutterContactDetector.cs` | Existing nine-path algorithm without algorithmic changes |
| `src/Slicing/Detection/AllHitClutterDetector.cs` | Multi-hit ray algorithm, section 5 |
| `src/Slicing/Detection/CapsuleClutterDetector.cs` | Native capsule algorithm, section 5 |
| `src/Slicing/Detection/CachedRibbonDetector.cs` | Copied triangle BVH traversal with existing ribbon intersection |
| `src/Slicing/Detection/SliceTargetLease.cs` | Game-thread-only live identity, ownership and geometry revision checks |
| `src/Slicing/Planes/ISlicePlaneStrategy.cs` | Pure bounded candidate selection with explicit failure/fallback flags |
| `src/Slicing/Planes/LegacyContactPlaneStrategy.cs` | Delegates to existing candidate order |
| `src/Slicing/Planes/BladePlaneStrategies.cs` | Strict, limited and fitted alternatives |
| `src/Slicing/Scheduling/SliceCoordinator.cs` | Admission, deduplication, lifecycle and main-thread lanes |
| `src/Slicing/Scheduling/SliceJob.cs` | One job's state/ownership, cancellation and terminal outcome |
| `src/Slicing/Scheduling/SliceWorkerPool.cs` | Fixed reusable geometry threads, bounded channels and result handoff |
| `src/Slicing/Scheduling/SliceFrameBudget.cs` | Shared budgets across active and retiring jobs |
| `src/Runtime/SaberRuntime.SliceMethodsUi.cs` | ImGui method selection and pending/active status |
| `src/Runtime/SliceClutterCoverageHooks.cs` | Optional scoped reach integration, section 9 |

Keep the initial legacy adapters thin. Do not copy entire implementations into adapters or replace the existing nine rays with a generalized allocation-heavy enumeration. Do not move `BladeSweep` into a new assembly. Existing isolated test harnesses explicitly list source files; add each dependency to the appropriate harness list.

### 4.1 Detector boundary

Use an internal interface with these semantic operations (exact C# parameter representation can follow project conventions):

- `Advance(in DetectionFrame frame, ref DetectionBudget budget, ContactBuffer output) -> DetectionProgress`.
- `Reset(DetectorResetReason reason)` and `Dispose()`.
- `Descriptor` exposes stable ID, supported target domain and capabilities.

`DetectionFrame` contains owner/scene epoch, authorization kind and trigger epoch, stroke ID, current settled frame ID, pose sequence ID, previous/current sample timestamps, blade endpoints, nominal deployment length and visual scale/axes. The live context and target registry are game-thread-only. A monotonically increasing sequence ID prevents querying the same render pose twice. Reset on scene/owner, deployment or scale discontinuity, teleport guard, invalid time, trigger epoch or method generation change. Do not bridge invalidated samples into a later sweep.

`ContactBuffer` is fixed-capacity, reusable, and cleared by its caller. A hit contains a target lease ID and pure evidence; it never starts a slice, sparks, changes cooldowns, chooses a plane, or mutates physics. `DetectionProgress` distinguishes `Complete`, `Pending`, `BudgetLimited`, `CoverageUnavailable`, and `Invalidated`. An exhausted budget is not proof of no hit.

Keep trigger eligibility outside the detector and apply section 3.1 to both domains. Physical blade-motion and locality checks still belong to their existing detection paths; clutter movement alone is no longer authorization with the toggle off. The current render-only contact/spark probe remains separate; a visual spark must not authorize a mesh replacement.

### 4.2 Evidence and coordinate contract

Every accepted contact must carry these values:

1. Scene epoch, actor ID, stroke ID, sample sequence, detection generation, detector ID, trigger and target kind.
2. `HitTime01`, `AlongBlade01`, absolute interval timestamps, confirmed point and optional surface normal, plus evidence quality (`ExactMeshScan`, `PartialMeshScan`, `ColliderRay`, `ColliderSweep`, `SyntheticAssisted`).
3. Previous/current base and tip, interpolation kind (`EndpointRibbon`, `RigidCapsule` or `SyntheticAssisted`), blade direction at hit and blade-point displacement/normalized-time derivative at hit. For a ruled ribbon, compute `blade(t) = lerp(tip0-base0, tip1-base1, t)` and `motion(s) = lerp(base1-base0, tip1-tip0, s)`. For the rigid capsule, `blade(t)=L*d(t)` and `motion(s,t)=centerDelta + cross(omega,(s-0.5)*L*d(t))`, where `d(t)` is the rotated blade axis and `omega` is radians per normalized interval. Provide a pure `Evaluate(t,s)` on each copied motion model; fitting must sample the same interpolant that detected the contact.
4. Source-frame definition, rigid source pose at observation, geometry/template revision and captured structural/subpart transforms. All geometry coordinates use double precision and metres.

For vessels, convert hit/subpart data into the structural part's own local frame when creating the intent. At capture, transform into the exact assembly coordinates used by `SliceSourceCapture`. This lets a queued intent follow rigid movement without treating its old world-space point as current. Preserve each endpoint's target-relative sampling history; never transform both historical samples with the current moving target pose. If the detector lacks previous target pose, prime history and report `Pending` rather than fabricate it.

For clutter, use scaled object metres as `ClutterSliceSourceCapture` does, and the collider-to-object correction in `ClutterForceNative.TrySliceFrame`. The collider center is not the model origin. Raw clutter motion is measured in CCF. For moving promoted clutter, label native sweeps as querying a frozen collider pose; do not claim relative continuous collision accuracy. If historical object poses exist, use them to compute plane evidence in object coordinates.

Transform directions with the linear matrix; transform an already constructed plane normal with the inverse transpose. Prefer transforming the blade and motion first and taking their cross product in the destination frame. Include reflection/nonuniform-scale fixtures. Plane offsets are recomputed from the transformed anchor.

### 4.3 Live leases and immutable work

The coordinator retains live `Part`, `Vehicle`, `KittenEva`, native clutter and mesh references in leases only. Geometry workers receive no live lease or resolver. `SliceInput` owns its arrays/material value records exclusively after capture; no writer may modify them until the job ends. Read-only interfaces alone do not establish immutability.

Vessel dedupe identity is the structural part object plus scene epoch, not display name, mesh name, body handle or subpart object. Clutter dedupe identity is `ClutterForceIdentity` plus scene epoch and validated object/template/scale identity, not recyclable Bepu handles. Preserve existing locality and ownership checks. Revalidate before capture, before isolation and immediately before source removal. A stale target ends with an explicit outcome and cannot remove a replacement that happens to reuse its handle.

## 5. Viable detector implementations

| Method | Accuracy and cost | Decision |
| --- | --- | --- |
| Existing mesh ribbon | Actual render triangles, bounded incremental traversal; one result and expensive repeated scanning | Preserve as vessel baseline |
| Cached mesh ribbon | Same narrow phase over a BVH of copied immutable triangles; traversal skips distant triangles and can emit one hit per structural source | Implement vessel alternative |
| All-hit sampled rays | Cheap native queries collect trees behind nearer trees; gaps between point paths still exist | Implement clutter comparison mode |
| Convex capsule sweep | Covers blade length and nonzero thickness with angular/linear motion; uses physics proxies and frozen target poses | Implement clutter coverage mode |
| Native overlap/broadphase alone | Bounds are conservative and may overlap empty space | Candidate discovery only, never a confirmed cut |
| Clutter render-mesh ribbon | Could improve visual precision beyond physical proxies; requires placement/template cache and foliage policy | Follow-up after this plan, not a hidden capsule fallback |
| Physics contact callback / attached blade collider | Native contacts available during simulation but requires solver integration, response suppression and cross-thread events | Defer; higher integration risk and still proxy geometry |
| GPU/depth picking | View dependent, readback latency, misses offscreen/occluded geometry | Reject for authoritative contact |

### 5.1 All-hit clutter rays

Add a new query handler beside `PickQuery`; keep `TryPick` and its nearest-surface semantics unchanged for Force and assisted selection. In the new handler, keep `maximumT` equal to the finite segment length; do not shorten it to each hit. Filter through the existing native clutter mapping and ignore already-pending target identities. Deduplicate compound children and repeated rays.

Use current and previous blade segments plus point trajectories at `s=i/N`, including the hilt. Choose `N = clamp(ceil(max(length0,length1)/0.25 m), 8, 256)`. This is a comparison algorithm, not a coverage guarantee: a thin trunk between paths can still be missed. Start each trajectory with its finite normalized ray and maximum segment distance; no infinite casts followed by distance filtering. Reject a trajectory shorter than 1 mm. Record trajectory time from hit distance divided by path length, and use the known sample `s`; endpoint blade rays have time 0 or 1 and along-distance-derived `s`.

Budget the entire detector to 0.5 ms soft time and at most 256 ray invocations per settled update. A Bepu call is indivisible. If a frame cannot finish all rays, discard unprocessed rays for that live scene interval and increment `UnprocessedRays`; do not replay them against future target poses and label them historical detection. Process a rotating deterministic ray order to avoid permanent tip starvation. Emit confirmed hits even from an incomplete query with `BudgetLimited`; telemetry must expose incomplete coverage. Do not present this mode as the final 50 m solution.

Use a fixed collector of 512 unique clutter candidates per query. On overflow stop admitting additional candidates and count overflow; never allocate proportionally to forest density. Sort collected hits by interval sequence, `HitTime01`, `AlongBlade01`, then stable per-scene target ordinal before admission. Native callback traversal order is not deterministic time order.

In alternative modes, a ray callback at `t<=1e-6 m` is an initial-overlap candidate, not a proven boundary point: Bepu convex rays can return zero for an origin inside a solid. Try the reversed finite segment, accepting only a positive-distance surface hit; if both ends are inside, report unresolved overlap. Preserve legacy's current behavior unchanged.

### 5.2 Capsule clutter sweep

Use the verified native `Simulation.Sweep<TShape, TSweepHitHandler>` overload, existing clutter mapping, and a detector-owned temporary `BufferPool` used only at the settled handoff. A `Capsule(radius, length)` is aligned with local Y; its supplied length is the centerline length. Place it at `(base+tip)/2`, orient Y to `normalize(tip-base)`, and use blade endpoint separation as capsule length. Initial contact radius is 0.01 m, explicitly independent of assisted selection reach and Force pick tolerance. This fixed world-space radius is an experimental physical contact policy, not the rendered glow radius. Convert CCF positions to bubble-local floats only after subtracting the native bubble origin, following `TryPick`; never cast planet-sized coordinates to float first.

The motion model is linear center translation plus shortest-arc blade-axis rotation at constant angular speed over a normalized interval of duration 1. Derive orientation change from blade directions, with roll held to the shortest rotation; do not derive arbitrary roll from an unstable up vector. The verified helpers are `BepuUtilities.QuaternionEx.GetQuaternionBetweenNormalizedVectors` and `GetAxisAngleFromQuaternion`. Pass center delta as linear velocity and axis times rotation angle as angular velocity, with `maximumT=1`. Use the explicit tolerance overload: `speedBound=|centerDelta|+|omega|*(L/2+radius)`, `minimumProgression=min(1e-4,0.00025/max(speedBound,1e-6))`, `convergenceThreshold=min(1e-5,0.0001/max(speedBound,1e-6))`, and `maximumIterationCount=64`. These proposed tolerances scale with spatial motion instead of the long capsule's bounding radius; validate them on thin-target fixtures. If deployment length changes beyond the existing guard, re-prime instead of sweeping a changing-length capsule.

This rigid rotation model differs from the existing linearly interpolated endpoint ribbon. Record that difference in the descriptor and comparison results. Rotations approaching 180 degrees in one interval or invalid time/teleport discontinuities are rejected by motion guards, not silently assigned an arbitrary arc. The measured pose cadence bounds knowledge of the actual path; interpolation creates an estimate, not additional observations.

The sweep handler must retain hits for every eligible collidable without shortening the maximum time after the first hit. Its hit callback has no child index, which is why this implementation is clutter-only. The callback location is an approximate capsule contact computed at the lower sweep bracket, whereas its reported time is the upper bracket. Do not treat it as an exact target-surface anchor. For each nonzero-time candidate, cast two opposing finite rays through that location along the normalized reported normal, with half-length `2*radius+0.005 m`, filtering to that exact current collidable/target only. Choose the confirmed surface point nearest the reported location; reject it unless distance to the blade centerline at reported time is <= `radius+0.0025 m`. Then project onto that centerline for `AlongBlade01` and record `ColliderSweep` evidence with `AnchorRefined=true`. Invalid normal, failed rays or excessive residual produces `UnconfirmedSweepContact`, not a guessed plane. This is a bounded conservative confirmation and can reject legitimate coarse-bracket hits; record the rate. It does not establish render-mesh accuracy.

Handle `OnHitAtZeroT` separately: it gives neither point nor normal. Never invent an anchor at the collider center. Mark an overlap candidate and confirm with a finite centerline ray against that candidate at the start pose, then the reversed start segment, then end pose and reversed end segment (stop at first success). A start confirmation records time 0; an end confirmation records time 1. Recompute along coordinate, blade and motion at that same time. Only a confirmed surface point can produce a cut. If all rays fail, record `UnresolvedInitialOverlap` and omit the cut. A later render-mesh refinement may cover this case; the current algorithm makes no full initial-overlap guarantee.

Budget at most four native sweep invocations, 256 confirmation rays and 0.5 ms soft total time per update, with a 512-candidate collector. Usually one query covers one interval. Apply confirmation in sorted hit order after the query; never mutate collector state by recursively starting another scene query inside the callback. Confirmation consumes the same time allowance. Require positive ray distance > `1e-6 m` for a boundary anchor; zero-distance inside-origin callbacks are not surface confirmation.

To retain a burst of sweep hits, keep a separate detector-owned queue of at most 512 unconfirmed candidates for up to one second. These are provisional evidence, not accepted cut jobs or successes. Freeze each candidate's target identity/shape revision, native shape pose, motion model and proposed confirmation rays in that target's local shape coordinates at detection. On subsequent settled updates, validate the same shape/revision, transform only those stored rays through its current rigid pose, and query only that target. Transform the confirmed point back to the saved local frame before comparing with the saved blade motion. This confirms immutable target geometry after rigid movement without replaying the old broadphase against a different scene. If shape/ownership changes or the candidate expires, discard with a reason. Pending confirmation consumes the same detector budget, dedupes by target and survives ordinary swing release; scene/method invalidation clears it. Do not start a job until confirmation succeeds. Count provisional overflow/expiry separately from job queue overflow.

In each native query, stop allowing expensive tests after 4,096 collidable/child tests; mark the query incomplete. Broadphase traversal itself is indivisible. Verify supported sweep tasks for observed shape types; an unregistered task is `UnsupportedShape`, not a proven miss. Do not repeatedly sweep an arbitrarily huge angular interval to evade motion guards. Native sweep targets are stationary for the query; moved clutter can require future relative-motion refinement. Do not query the live simulation from geometry workers.

### 5.3 Cached vessel ribbon

Reuse `BladeSweep.TryHit`, including coplanar/edge cases, time ordering and degenerate axial sweeps. Change discovery/traversal and multi-target output only. Cache copied mesh-local triangles keyed by mesh object identity, `PositionsCompare` array identity and a mod-owned revision. There is no verified native mesh content-generation property: add `SliceGeometryRevisionRegistry` with explicit invalidation on scene changes, mod-generated asset replacement and observed array/template replacement. Cache only immutable loaded assets under this contract; foreign in-place array edits are unsupported unless their producer calls the invalidation hook. Unknown mutable sources use uncached scanning. Do not hash every mesh each frame. Keep per-instance transforms outside the shared cache. A geometry revision invalidates both cache entries and associated pending queries.

Build an immutable binary AABB tree with stable median splits along the longest centroid axis, original triangle index as tie-breaker, and at most eight triangles per leaf. Use deterministic pre-order node indexing and a reusable traversal stack. Query bounds are the target-local ribbon's endpoint AABB, expanded by the existing intersection tolerance. Bounds reject work; only `TryHit` confirms hits. Build from copied triangles on a worker after budgeted game-thread copying. Never retain mutable native spans in a worker job.

Cap cache storage at 64 MiB, 256 mesh entries, and two pending builds. Count triangle and node array bytes, evict only unleased entries using least-recent use. Native mesh identity can be held by the settled-thread cache index but must not cross into build work. On cache miss use an extracted candidate-level triangle scanner sharing the detector's total budgets while preparing the cache; do not instantiate/run a full `ManualContactProbe` per candidate. Finish an already-started scanner cursor for its frozen interval even if the BVH becomes ready; subsequent intervals use the cache. Keep fallback counters and do not synchronously build a BVH on the first swing. Before queued workers exist, use one bounded background cache builder; after package 8 it shares worker capacity at lower priority than accepted cuts.

Retain original vessel discovery/transform budgets and 0.5 ms soft time. Add a 4,096-node visit cap as well as the existing triangle cap. Maintain independent cursors per candidate, bounded by the existing candidate limit. Allow each completed or bounded-refinement query to emit one confirmed hit per structural source; keep scanning other candidates while the common budget permits. Frame snapshots must be frozen per query. Initial cache/discovery can miss early intersections; record cold/discovery/incomplete counts instead of claiming exhaustive detection.

If `BladeSweep.Subdivisions` exceeds 16 in the cached detector, bisect the frozen interval in normalized time using its declared endpoint interpolation, up to depth four (16 subintervals). Each subinterval still obeys the 16-strip cap. Convert local hit time back to the original interval before ordering. Spend the same shared node/triangle budget across all subintervals. If subdivision still fails or a query ages out, emit `UnsupportedMotion`/`BudgetLimited` with counters; never translate the existing kernel's refusal into a completed no-hit result. Legacy keeps its current guard.

Earliest hit means earliest among confirmed samples scanned for that source; distinguish full scan from bounded-refinement output. The mesh narrow phase estimates a zero-thickness ruled surface; the capsule uses a physical radius. Compare both coverage and false positives rather than interpreting raw hit-count differences as algorithm quality.

## 6. Plane strategies and exact fallback rules

Refactor `SliceGeometryWork.Calculate` so source preparation occurs once, then the selected pure strategy supplies an ordered, bounded candidate list. Represent each candidate with `Plane`, `ShapeKind` (`Planar` or legacy `Faceted`), index and fallback reason. The geometry loop retains all current topology, export, mass and budget checks. Keep geometry failure separate from plane-generation failure.

Add an immutable `SlicePlaneRequest` to `SliceInput`: strategy ID, trigger, target kind, evidence and fallback policy. Existing `Contact` remains during migration; the legacy adapter creates the same candidates and compatibility retries before removal of the implicit `Contact == null` policy. Do not switch mutable global delegates while workers are running.

All manual alternatives are anchored at the confirmed contact `p` and produce unit `n` with offset `d=dot(n,p)`. A surface normal is not a cutting-plane normal. Candidate count and attempt order must be deterministic. Return `DegenerateMotion` rather than guessing a plane when the alternative lacks enough evidence.

### 6.1 `legacy-contact`

Delegate exactly to `ContactSliceGeometry.TryCreate`, `TryCandidates` and `AxisCandidates`, including the current collinear perpendicular and all ordering. Preserve rock unanchored retry, tree preparation, authored-subpart fallback eligibility and clutter toppling. Explicitly label output `TiltFallback`, `AxisFallback`, `UnanchoredRockFallback`, `AuthoredSubpartBreakup` or `ToppledOriginal`. Preserve GLB/XML identity when geometry is unchanged; strategy metadata belongs in diagnostics, not asset content hashes.

### 6.2 `blade-strict`

Use blade direction `b` at contact and relative blade-point displacement `v` at the recorded along coordinate. Reject nonfinite/near-zero vectors and `|cross(normalize(b), normalize(v))| <= 1e-4`. Otherwise use that cross product as the preferred normal. Attempt exactly one planar cut. No axis, tilt, interior, faceted or authored-subpart retry. For clutter, no whole-object toppling after a strict cut failure. This option measures fidelity and exposes unsupported geometry rather than concealing failure behind a different result.

### 6.3 `blade-limited`

Start with the strict plane. If valid but geometry fails, try eight planes tilted 5 degrees from its normal at azimuths `k*pi/4`, `k=0..7`, using `AssistedSliceGeometry.Basis`. Every plane passes through the same contact. Maximum nine candidates, no larger rotations, no part-axis/unanchored/faceted/toppling fallback. Degenerate strict input fails without retries. This provides an explicit small fidelity-versus-success tradeoff.

### 6.4 `sweep-fit`

Fit one anchored plane to the observed interval while constraining it to contain the blade direction at contact. No future-pose wait and no unbounded history. In source metres, let `b` be the normalized blade direction at hit. Construct deterministic perpendicular basis `u,v` using the existing basis helper. Sample the evidence's declared motion model with `Evaluate(t,s)` at `t={0,HitTime01,1}` and `s={0,AlongBlade01,1}`; retain repeated points as repeated equal weights. Subtract the contact anchor from each point, then project to `(dot(q,u),dot(q,v))`. Form the symmetric 2×2 sum-of-squares matrix and choose its normalized eigenvector with the smaller eigenvalue; the plane normal is that vector expressed in `u,v`.

Use a direct stable symmetric 2×2 eigensolve, no external numerical package. If trace is nonfinite or <= `1e-12 m²`, or the eigenvalue gap is <= `1e-8 * trace`, the fit is ambiguous: return the strict plane if it is defined, otherwise `DegenerateMotion`. Orient the normal to have nonnegative dot with the strict normal when available; otherwise use lexicographic sign with the first component above `1e-12` positive. Try one planar cut only, with the same no-fallback rules as strict. Record RMS residual `sqrt(lambdaMin/9)` in metres. No three-dimensional PCA unconstrained by the blade: that can choose a visually unrelated plane.

This fit is a deterministic approximation for a twisted/nonplanar sweep, not a curved cutting surface. A future history-window fit is possible behind the same interface, but is outside this implementation.

### 6.5 Effective plane and numerical shifts

`PlaneSlicer.StablePlane` currently normalizes and nudges plane offset by up to 64 times the effective topology epsilon to avoid coincident vertices. Keep this kernel behavior. Record both requested and effective plane, angular deviation and `abs(dot(effectiveNormal,anchor)-effectiveOffset)`. Strict/limited/fitted alternatives reject an otherwise successful result if anchor residual exceeds 1 mm. This check belongs before export, not after publishing assets. Legacy output retains its existing tolerances. Thus “strict” describes geometric direction and a defined numerical anchor bound, not mathematically zero displacement.

Trees still clip foliage with the accepted effective plane and exclude foliage from structural mass/collision. Generated wood debris follows provenance, not name heuristics. Rocks in nonlegacy modes must never reach `Contact = null` through an outer catch block. Move that retry behind explicit legacy policy and test it.

## 7. Safe runtime switching

ImGui changes validated desired settings only. `BeforePhysics` compares desired and active snapshots and performs switches once at the settled handoff.

1. Detector changes reset that detector's incomplete queries and sample history. Start with a priming sample; never connect endpoints across method generations. Retain already accepted jobs with their original evidence.
2. Plane changes affect newly admitted jobs only. Freeze strategy, geometry limits and all plane parameters at admission. Queued jobs waiting for capture use those frozen options too.
3. Execution-mode changes enter a drain barrier: stop new admissions, continue capture/work/upload/commit/retirement under the old mode, and install the new mode only after all old work and native transactions finish. Display `Waiting for N cuts` with desired and active modes. Changing back while draining updates the desired mode; it does not create a second pool.
4. Worker-count changes apply once running work has drained; capacity increases/decreases cannot strand completion records. Lower queue/memory settings never discard already accepted jobs; stop new admissions until usage is below the new cap.
5. Unsupported native capability: show one explicit reason and use the effective legacy detector for that domain. Do not silently claim that the requested detector is running. Tests force capability failure and recovery.
6. `PassiveContactCutsEnabled` takes effect at the next settled handoff, even during queued work or an execution-mode drain. Clear unaccepted passive evidence on disable, prime on enable, and preserve accepted jobs. Manual and assisted authorization are independent of the toggle.
7. No UI callback joins threads, cancels an upload directly, runs a native query or mutates a source. No background thread reads `SaberSettings`.

Preserve `legacy-serial` gesture cooldown and existing scene/source cancellation behavior. The explicit exceptions are the shared activation gate and preserving already accepted work when the passive toggle changes or a manual drag ends. Do not confuse ending input authorization with invalidating the target. Queued mode has additional lifecycle rules below.

## 8. Queued processing and bounded parallelism

### 8.1 Admission and job states

The queue stores small intents before mesh copying. Reserve a pending-target slot immediately upon acceptance, preventing repeated rays/frames from enqueueing the same source. Allow at most one pending/running job per target revision across all actors. Keep stroke identity separate from individual job completion; a tree cut finishing must not consume or reset a vessel gesture.

States are `Queued -> Capturing -> GeometryQueued -> GeometryRunning -> LoadQueued -> Loading -> UploadQueued -> Uploading -> Ready -> Isolated (vessels only) -> Committed`. Before commit, failure may transition to `Rejected`, `Stale` or `Cancelled`. Jobs with active workers, submitted GPU work, partial registration or isolated native sources enter `Retiring` until ownership obligations finish. Release target/memory reservations exactly once after the relevant owner relinquishes them. Each transition is coordinator-owned except worker-local computation and publication of immutable completion messages.

Admission ordering: sample sequence, contact time, along-blade coordinate, then stable target ordinal. Deduplicate first, then apply limits. At queue capacity reject new arrivals with `QueueFull`; do not evict older accepted cuts. Count distinct overflow targets with bounded bookkeeping. Do not mark them accepted or play the successful-cut sound. Once a job is accepted it has no arbitrary short expiry; the source/scene validation and explicit user cancellation bound its lifetime. Incomplete detector queries retain their short age limits.

Queued mode removes the global 250 ms clutter attempt gate and the manual one-job-in-flight gate from detection. Apply section 3.1 authorization and preserve physical-motion/locality guards. Use a per-source 250 ms rejection cooldown to avoid repeated failure floods, and one successful attempt per target revision per stroke; pending identities remain blocked until retirement. Tag generated fragments with their originating stroke in the coordinator's bounded transient identity table and exclude them from that same stroke, avoiding immediate recursive cuts. A new deliberate SWING gets one new stroke ID shared by vessel and clutter detection. Only while the passive toggle is enabled, ordinary movement starts a passive stroke after 250 ms without qualifying motion or after eligibility is lost and restored; continuous passive movement retains one stroke across both domains. Authorization transitions prime new samples and never reuse the previous stroke. Generated fragments may be recut by a later stroke. Clear provenance entries when the fragment retires or scene epoch changes.

### 8.2 Workers and loading

Create a fixed pool of background threads owned by the mod. Start lazily when queued work first needs them; reuse them, set `IsBackground=true`, name them and set below-normal priority where supported. Use blocking bounded channels/queues on workers only. The main thread uses nonblocking enqueue/dequeue. Do not create a `LongRunning` task for each target, alter global ThreadPool limits, borrow KSA solver workers, or add nested `Parallel.For` calls inside a cut.

Worker input is copied geometry, immutable plane options, seed and cancellation token. The existing expensive hull search uses a private `BufferPool`; retain that isolation. Every exception/cancellation creates a terminal completion message and is observed. Keep per-job plan publication keyed by job ID; a single global `Plan` field would mix simultaneous cuts.

Initially serialize `RuntimeFragmentAssets.LoadDetached` in one separate background loader lane. Its `KSA.Mod` pointer is not pure geometry input; it may be used only by the existing audited detached-loading path. Keep `StorageGate` for disk accounting and immutable writes. Do not access the mod/asset registry from geometry threads. Parallel detached loading is a separate optimization requiring a loader/static-state audit; it is not necessary for concurrent geometry work.

### 8.3 Main-thread budgets and upload serialization

Use one common budget object per settled update. Initial limits in queued mode:

| Stage | Soft elapsed target | Hard scheduling limit |
| --- | --- | --- |
| Contact detection | 1.0 ms combined | Each domain retains its 0.5 ms target and documented operation caps |
| Source capture | 1.5 ms total | Eight existing capture units total, round-robin; at most one new capture begins per update |
| Upload and native preparation | 1.5 ms total | 32 existing cheap units total; at most one GPU submission/poll or native preparation unit |
| World commit | Separate measured indivisible work | At most one source transaction step per update |

Expose `Step`/`Advance` methods accepting the remaining shared budget; do not call an unchanged 1.5 ms `Advance()` on every queued job. Retirement is charged to the same upload budget and has priority over starting new registration. The limits are scheduling targets, not hard frame-time guarantees: a single native step, a hull allocation or a driver call can exceed them. Measure those overruns.

Keep exactly one active `UploadJob` across all queued sources, including draining cancelled registration. This avoids a concrete race: two identical content IDs can both observe a missing cached template, yield, then both attempt registration. The next job may start only once the previous registration is complete/retired. Reuse already prepared assets without duplicating their native ownership. Do not parallelize GPU allocation, renderer dictionaries, native shape registration or source mutation.

Ready jobs may commit in completion order, using admission order as tie-breaker; do not block every small tree behind one expensive early tree. Commit only on simulation advancement with valid current scene/native state. Pause can drain CPU/upload work but cannot change native bodies. Queued commits do not require the original blade pose to remain fresh; their evidence was fixed at detection.

### 8.4 Memory and resource reservations

Queue length alone is insufficient. Before capture reserve an estimated working allowance from the copied triangle count (include foliage and materials), expected output limits and export buffers. Use checked 64-bit arithmetic. Initial estimate is `max(4 MiB, 16 * sourceTriangleCount * Unsafe.SizeOf<SliceFace>())` plus source material/metadata bytes; this is a conservative admission heuristic, not an enforceable heap bound. Account actual retained arrays and GLB/XML byte arrays at stage boundaries, and stop starting work if observed retention exceeds the limit. Report estimate/actual separately. A single source whose estimate exceeds the configured limit is explicitly refused with the setting name.

Active geometry plus completed results awaiting loading/upload remain charged. Limit concurrent loaded-but-not-uploaded results to two. Completion capacity must hold every running worker's result even when new admissions are blocked. Never let the main thread block waiting for memory or a result consumer. When admission rejects large sources, later smaller intents can proceed; preserve original admission ordinal for fairness.

Reserve expected fragment/native-vehicle capacity when geometry reports an accepted result; check existing global resource limits again at commit. Reservations prevent several ready jobs from all assuming the same remaining capacity. On failure release reservations. Existing generated assets referenced by saves remain durable; cancellation is not permission to delete them. Bound diagnostics, history, BVH caches and temporary collectors independently.

### 8.5 Vessel transactions

First enable queued processing for clutter. Then admit vessel intents using the same contract, while retaining a serialized vessel transaction per owning vehicle group. Multiple subpart hits on one structural source dedupe to one cut. Multiple structural parts on the same vessel remain queued; do not capture the next one while another source from that vessel is being captured/prepared/isolated/committed. Different vessels and clutter can prepare concurrently.

Keep queued vessel contact evidence in source-local coordinates. After a coordinator-owned cut changes vessel ownership, refresh remaining intent leases only when the exact structural part and its mesh/local geometry revision still exist and the ownership transition is recorded by that coordinator transaction. Re-resolve the current assembly transform before capture. External docking, breakup, mesh edits or uncertain ownership cause `Stale`, not a guessed transform. Do not apply an old assembly-space plane to a new vessel frame. Once captured, retain the existing `SliceSourceFrame` checks and explicit successful-isolation rebase mechanism.

Hold the vessel transaction lease across the separate isolation and replacement updates. Cancellation after isolation preserves the current warning that the source can remain detached/empty with engines disabled; do not claim rollback. Fallback is strategy policy and cannot bypass transaction checks.

### 8.6 Cancellation and lifecycle

- Releasing SWING ends manual contact intervals. With passive detection off, no new contact intervals start; with it on and other guards satisfied, re-prime passive sampling before detecting ordinary movement. Turning passive detection off stops new passive intervals and clears unaccepted passive work, while manual/assisted actions remain available. Accepted jobs continue in either execution mode if sources remain valid.
- Ordinary blade stow, entering a clash or beginning a Force action stops new queued detections; accepted queued jobs continue if sources remain valid. A Force mutation of the source can invalidate its lease. Legacy mode retains its existing lifecycle cancellation behavior for these events, subject to the explicit drag-release/toggle exceptions in section 3.1.
- Add **Cancel pending cuts** to the slicing settings/status. It requests cancellation at the next settled handoff. Already committed cuts remain. Already isolated/partially registered work follows the established retirement/transaction rules.
- Owner switch, scene/system load/unload, disposed actor, incompatible parent/bubble transition, mod shutdown or invalid source cancels affected jobs. Increment scene epoch; reject all old completion messages before native access.
- Cancellation signals do not synchronously join on the render/game thread. Keep a collection of retired jobs, replacing current single `_retiredWork` slots for queued mode. Dispose cancellation sources and copied buffers only after the worker has stopped using them.
- Shutdown stops admission, requests cancellation, and arranges completion cleanup before releasing loader/native resources. If workers cannot yet stop inside an indivisible native CPU hull call, retain those resources until their completion; do not unload them underneath workers.
- Queues are transient, not save data. Loading a save invalidates pending work. Existing committed fragment assets retain the current persistence behavior.

### 8.7 Existing singleton integration and assisted arbitration

Replace singleton job state only as queued mode is introduced. `PartSliceTargeting` currently retains `_wielder` and a slice handler tied to one slicer: extract explicit-owner validation functions and route commit by job ID. Never swap a shared handler to whichever asynchronous job last completed. Preserve the existing isolation rebase entry point as a per-job operation. `_sliceTarget`, `_sliceError`, `_sliceStarted`, `_manualIsolated` and `_manualSliceActive` cannot describe a batch; move queued equivalents into `SliceJob`, leaving legacy/assisted facade fields until their callers migrate.

Add `HasOutstandingJobs` that includes queued, active, isolated and retiring jobs. Use it to block destructive slice-resource maintenance, arena reset/clear, shutdown resource disposal and execution-mode transitions. Do not use it to block queued-mode contact detection. Keep `CanAdmitContacts`, `CanStartAssisted`, and `HasOutstandingJobs` distinct so old `SliceWorkActive` guards cannot silently reintroduce single-cut behavior. Mesh growth retains its existing global upload-drain barrier, which must account for the coordinator's sole active/retiring upload.

`SliceFrameDiagnostics` has singleton armed/operation fields; the coordinator owns frame sampling for the whole batch and jobs carry their own stage timestamps. Completing/cancelling one job cannot disarm diagnostics for another.

Retain assisted exclusive admission in this version: an assisted selection can start only when the coordinator and legacy slicers, including retirement, are idle; while selecting/preparing/animating/committing an assisted cut, automatic/manual contact admission pauses. Show why selection is unavailable while pending cuts drain. Do not preempt or cancel existing jobs to make room for a click. Assisted foreground clutter semantics remain `Absent`, `Blocked`, `Started`; incomplete query, refusal or unavailable foreground coverage is `Blocked` and never authorizes a vessel behind it. Keep terrain occlusion and configured selection reach independent of contact detectors.

## 9. Native clutter coverage for a 50 m blade

KSA's physical clutter cells are generated and retained based on vehicle bounds. A long visual blade does not enlarge those bounds automatically. Both the renderer's physical-data generation and `BubbleClutterStatics.Update` must request the extra area. Querying Bepu more often cannot hit nonexistent colliders.

Verified source: [BubbleClutterStatics.Update](/Users/asherwin/wip/purr2d2-initial/repos/ksa-game-assemblies/current/decomp/KSA/BubbleClutterStatics.cs:762), [physical-data generation loop](/Users/asherwin/wip/purr2d2-initial/repos/ksa-game-assemblies/current/decomp/KSA/GroundClutterRenderer.cs:1309), [ClutterEcotypePhysicalData](/Users/asherwin/wip/purr2d2-initial/repos/ksa-game-assemblies/current/decomp/KSA/ClutterEcotypePhysicalData.cs:241).

Implement a narrowly scoped call-site replacement for the `Vehicle.BoundingSphereRadiusBody` read in those two clutter-interest loops. The exact methods are `GroundClutterRenderer.OnFrame(ReadOnlySpan<IViewport>, Brutal.VulkanApi.CommandBuffer, int)` and `BubbleClutterStatics.Update(ConstraintSim, Celestial, ClutterEcotypePhysicalData[], List<VehicleUpdateState>, ref readonly BubbleOrigin)`. Replace the getter call with `static float ClutterInterestBound(Vehicle vehicle)`, preserving its evaluation-stack signature. The helper returns `max(originalRadius, requestedSliceRadius)` only for the active saber owner's exact vehicle and current scene epoch; other vehicles get their original value. Never assign to the actual vehicle bounds and never patch the getter globally. Verify exactly one intended getter use at each call site before enabling; signature/IL mismatch disables the feature with a diagnostic. Install/uninstall both as one capability: a partially patched pair cannot advertise extended coverage.

Publish immutable interest using `Volatile.Write` from settled state, so renderer and native consumers only read a snapshot using `Volatile.Read`. Set requested radius to the maximum owner-origin-to-blade-endpoint distance plus 2 m; include recent previous endpoints and still-pending clutter target bounds. Cap the slice-requested radius at 64 m for this implementation; never shrink larger genuine vehicle bounds. If required reach exceeds the cap, show `Clutter coverage limited` and do not claim complete long-blade detection. Global ground-clutter collision settings remain authoritative; if disabled, show coverage unavailable rather than silently enabling them.

Before enlarging interest, enumerate expected cells with the same pure calculation as `MultiCubeCellGrid.Update`: `GetKernelSize`, `DirectionToQscUv`, `GetCellOnFace`, `ResolveAdjacentCell` and `CellInRange`. Do not call native `Update` merely to preview because it mutates grid state. Bound preview enumeration to 4,096 visited cell candidates and at most 32 additional unique cells across ecotypes beyond the owner's original-interest set (worst case 8,192 placements at 256 per cell). If any preview limit is exceeded, retain the last admitted interest radius, mark coverage limited and stop; do not allocate an unbounded expected-cell set or partially claim the new radius. These initial conservative caps may limit some 50 m environments; report them and require native acceptance before increasing them. Existing native interest remains unaffected.

Warm the region while the blade is equipped/extended, before intentional swinging. Cell generation/readback is asynchronous. Keep existing sample guards, and never invent contacts from a swing that happened before coverage existed. Diagnostics distinguish **requested reach**, **loaded physical cells**, **resident targets**, and **coverage pending/limited**. `VesselGrid.loadedCells` and a zero count from `TryGetCellData` are insufficient proof: a loaded cell may still be awaiting GPU readback.

Add a checked observational postfix to `ClutterEcotypePhysicalData.ReadbackGroundClutter(int frameIndex)`: only on successful completion, inspect the checked `_idsGeneratedThisFrame[frameIndex]` layout and record `(ecotype identity, cell, buffer ID, generation)` for entries whose current buffer ID still matches. Count empty completed cells as ready. Invalidate these stamps before `UnloadGroundClutter(ReadOnlySpan<Cell>)`, `InvalidateGeneratedClutter`, buffer resize/reset and ecotype disposal. A later successful `BubbleClutterStatics.Update` must have processed the same expected cells before coverage is marked ready. Nonempty cells need verified resident installation; empty cells need no `_instanceData` entry. Any failed/unknown layout or incomplete expected-cell enumeration reports coverage unknown. These hooks observe state; they never force a GPU wait or install colliders themselves. The checked native readback loop is at [ClutterEcotypePhysicalData.cs](/Users/asherwin/wip/purr2d2-initial/repos/ksa-game-assemblies/current/decomp/KSA/ClutterEcotypePhysicalData.cs:1123).

Retain interest for accepted queued clutter until capture/commit validation no longer needs those native targets, or until scene invalidation. Release progressively when work completes, with a 0.5 s quiet hysteresis to avoid cell churn. Merely releasing SWING must not evict queued targets. Native clutter cell generation and installation have their own costs and may be indivisible; profile them separately from slice budgets. If 64 m residency violates frame acceptance, keep reach extension experimental and report that the 50 m acceptance gate has not passed. Do not hide this failure by passing only tests with preexisting nearby colliders.

## 10. Diagnostics and comparison method

Add job IDs and strategy IDs to existing slicing diagnostics. Keep counters enabled with constant bounded cost; collect detailed timestamps/traces only when debug timings are enabled. Use a 256-event ring, not unbounded per-frame logging. Report:

- Unique candidates, confirmed contacts, queued/deduplicated/queue-full/stale targets, query budget stops, cold-cache fallbacks, unprocessed rays and coverage failures.
- Detection time, capture time, queue delay, geometry duration, loader duration, upload/native preparation duration and commit duration separately; active workers, queue depth, estimated/retained memory and longest indivisible step.
- Requested/effective plane, candidate attempts, tilt/axis/unanchored/faceted/topple outcomes, angular deviation, anchor residual and fit residual.
- End-to-end detection-to-visible-commit latency; success means a mesh cut, not a fallback topple. Display median/p95/p99 over a bounded window, with sample count.

Do not run every strategy every frame by default. For deterministic comparisons, add an opt-in bounded capture/replay harness using copied mesh/pose fixtures. Replay all methods against the same samples and target geometry; do not compare them against worlds already altered by different previous cuts. Native proxy queries require a dedicated native fixture/world reset; pure mesh replay cannot substantiate Bepu accuracy or performance. Hold plane strategy, scheduler settings, target assets, seed, blade path and sample cadence fixed when comparing detectors; vary one dimension at a time. Test one versus two versus four workers separately.

All detailed instrumentation must identify partial scans and incomplete native coverage. Measure both uninstrumented frame times and stage timings so instrumentation cost is not confused with a detector regression.

## 11. Validation and acceptance criteria

Do not claim runtime performance from source inspection. Capture a baseline on the same machine/build before refactoring. Warm both versions equally; use identical fixture data, release builds and at least five runs. Keep cold-start measurements separate. Compare legacy algorithms under matching authorized interactions; the new default's lack of passive queries is an intentional behavior change, not a parity failure. Default mode passes when the revised activation fixtures and unchanged algorithm fixtures match and detector/coordinator p95 adds no more than `max(0.05 ms, 10% of baseline)` with no increase in per-frame steady-state allocations attributable to inactive strategies. Idle/default walking must invoke zero cut-contact queries. Report p99 and indivisible native spikes rather than suppressing them with averages.

Required behavioral fixtures:

1. Legacy algorithm parity within authorized interactions: existing contact/plane order, manual 250 ms rearm, clutter cooldowns, assisted reach/occlusion, scene/source cancellation, wood/foliage and rock fallback; same effective plane/output geometry for fixed inputs and seeds. Replace tests that require passive clutter cuts by default with the explicit toggle cases below.
2. Geometry contacts: stationary hilt with moving tip, translation, thrust/collinearity, coplanar touch, grazing edges, moving/rotating target, nonuniform scale/reflection, planetary rotation, pause/time reversal, teleport, scale/deployment change and stale mesh.
3. Multi-hit: 32 distinct trees in one observed sweep; two targets on the same ray; one target hit by many rays/children; thin trunk between fixed samples; target behind another eligible target; 50 m angular sweep; small-radius capsule false positives and initial overlap without a point.
4. Plane strategies: strict cross product at hit; small-motion degeneracy; exact anchored offsets; deterministic tilt ordering; symmetric fit ambiguity; curved/twisted fit residual; stable-plane offset <=1 mm for alternatives; no unanchored rock/topple fallback leaking into strict/limited/fit.
5. Switching: change each detector during incomplete discovery, each plane mode with queued/running jobs, scheduler mode during upload/isolation, workers during execution, invalid JSON IDs, unsupported hooks, reset settings and autosave/reload. Accepted jobs keep their original strategy snapshot.
6. Queue lifetime: release swing while 32 cuts are queued; pause; owner change; save load; recycled native handle; source moved/promoted/destroyed; duplicate target from two actors; overflow; cancelled worker finishing late; loader exception; duplicate content IDs; cancellation during GPU registration; isolation then cancellation; resource cap exhausted by competing jobs.
7. Vessel scope: multiple subparts of one structural part dedupe; queued different parts on one vessel serialize; different vessels prepare concurrently; coordinator-owned ownership change revalidates remaining intents; external ownership change is stale.
8. Memory/backpressure: active workers never exceed limit, completion messages always have capacity, retained results stay charged, at most one active upload job, no per-job budget multiplication, no main-thread joins, exactly-once reservation release.
9. Activation defaults and migration: new settings, old JSON without `PassiveContactCutsEnabled`, and Reset all leave it false. Save/reload preserves an explicit true. With false, walking/jumping/idle motion into vessel parts, trees and rocks invokes no cut-contact queries, enqueues nothing, and cannot trigger toppling/breakup fallbacks. A generic mouse press, unrelated UI drag, or stationary press on SWING also cannot authorize walking cuts. A valid manual click-drag and an explicitly confirmed Slice target can cut each target family with the toggle off.
10. Passive activation and transitions: true allows movement-driven detection for both vessel and clutter domains, while dropped/stowed/unlit blades and other existing guards remain blocked. Entering manual SWING while true produces one detector pass per interval and no duplicate jobs. Turning false during a passive query/provisional confirmation clears that unaccepted work immediately at the settled handoff; accepted jobs finish. Turning true or releasing manual SWING into passive mode requires a fresh sample pair and never cuts along old inactive motion. Test these rules with every detector and both execution modes. Repeat with pending assisted selection to verify exclusive admission.

Native forest acceptance: with passive detection off, ground collisions enabled and extended coverage warmed, a deterministic 50 m blade sweep made by click-dragging the manual SWING control intersects at least 32 known resident tree colliders using a supported trajectory and excluding deliberately unresolved initial-overlap cases. The fixture must independently establish the 32 expected contacts. Every one must be detected, surface-confirmed and admitted exactly once under default queue limits; a missed expected target fails this acceptance gate. All jobs must eventually produce a mesh cut or a visible specific geometry/resource rejection. Confirm at least two geometry jobs overlap in time at worker count two, that detections continue while earlier jobs run, and that releasing the swing does not cancel accepted jobs or admit new walking contacts. Do not require all meshes to appear in the same frame. Report missed targets, false hits, cut failures, queue delay, frame p95/p99 and physics-body growth. Repeat ordinary movement through targets without SWING: expect no new cuts with the toggle off and eligible passive contacts with it on. Repeat with cold physical coverage and explicitly show the incomplete-coverage outcome. Separate overlap fixtures include hilt inside/trailing tip outside a trunk and both endpoints inside, ensuring zero-distance callbacks never become fabricated boundary points.

Use existing harnesses as the base: [contact checks](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/tests/contact/ManualContactChecks.cs), [manual runtime checks](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/tests/contact/ManualRuntimeChecks.cs), [clutter coordinator checks](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/tests/trees/ClutterSlicerChecks.cs), [background checks](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/tests/BackgroundSliceChecks.cs), [stock clutter checks](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/tests/ClutterStockSliceChecks.cs). Add focused strategy/scheduler fixtures to the source lists in [tools/validate.py](/Users/asherwin/wip/purr2d2-initial/repos/purr2d2/tools/validate.py:569). Full managed validation command is `python3 tools/validate.py --run-tests`; native checks use the existing host scripts with their documented environment. Build the mod against the checked local game assemblies before native acceptance. A stub test passing is not proof of native API/thread safety.

## 12. Ordered work packages for implementing agents

Each package is a reviewable change. Its agent must read this entire specification, inspect the current worktree, preserve unrelated edits, run the relevant focused checks, and report changed files, acceptance evidence and unresolved limitations. Do not advance through a failed prerequisite by weakening a test or silently removing a strategy.

### Package 1 — Characterize and define contracts

Read the source map in section 2. Add parity fixtures covering exact candidate order, rock retry/topple semantics, manual/clutter admission and cancellation, plus timing/allocation baseline capture. Add IDs, options snapshots, evidence, target leases and diagnostic outcome types. Add the default-false `PassiveContactCutsEnabled` setting, shared activation-policy contract, explicit trigger kinds and settings migration tests. No active code path changes yet. Exit: baseline recorded and contract tests pass; defaults/unknown settings resolve deterministically, and old JSON defaults to intentional-only cutting.

### Package 2 — Extract detector adapters

Extract clutter contact gathering from `SaberRuntime.ClutterSlice.cs`; preserve its physical-motion guards, timing, first-hit behavior and state storage under the legacy path. Replace its unrestricted activation with the shared section 3.1 gate. Wrap `ManualContactProbe` and expose its existing evidence including hit time/along coordinates without changing traversal; accept explicit passive authorization without fake drag input. Factor pose publication so enabled passive detection can receive fresh samples. Move sparks/audio/cooldown/admission outside detector results and separate accepted-job processing from current input authorization. Preserve render spark probing. Exit: revised activation fixtures and existing algorithm/runtime tests pass, walking cannot cut with default settings, manual dragging reaches both domains, and default performance meets section 11.

### Package 3 — Extract plane policy and implement alternatives

Add `SlicePlaneRequest` to copied inputs and explicit trigger/fallback metadata. Refactor only candidate production and policy dispatch in `SliceGeometryWork`/`ClutterSliceGeometry`; retain kernel and preparation. Implement the four contact plane strategies and effective-plane acceptance checks. Keep assisted behavior behind its explicit legacy policy. Exit: parity for legacy and positive/negative geometry fixtures for every alternative, including rock no-fallback checks.

### Package 4 — Add safe ImGui switching

Wire settings validation, descriptors, ImGui controls, autosave/reset and settled activation, including the default-off **Cut during ordinary movement** checkbox and its exact tooltip from section 3. Initially expose implemented legacy and plane options only. Exercise query resets, pinned worker options, failed capabilities, passive toggle during pending/accepted work, and false-by-default migration. Exit: changing settings needs no restart, cannot mutate native state from ImGui, and does not reinterpret accepted work; the activation toggle works independently of method/execution selections.

### Package 5 — Add all-hit rays and cached vessel ribbon

Implement independent all-hit clutter query without changing `TryPick`; implement BVH copy/build/cache/traversal with the common detector contract. Add multi-result buffers and comparison fixtures. Keep serial admission selectable and explain suppression. Exit: two targets behind each other are both detected, cache misses remain budgeted, output is deterministic for fixed fixtures, allocations/budgets pass.

### Package 6 — Add capsule clutter detector

Verify native signatures/capsule orientation against local assemblies, add strict capability validation, implement finite sweep/collector/initial-overlap confirmation and proxy-quality metadata. Test outside start overlap, zero-time overlap, direction changes and promoted clutter. Exit: supported collider fixture tests pass; no vessel detector substitution or fabricated initial contact points.

### Package 7 — Build queued coordinator with one worker

Introduce per-target jobs and dedupe, shared source-capture budget, one loader lane, one upload lane, explicit results/retirement and resource reservations. Start with one geometry worker and clutter only. Split admission eligibility from processing eligibility. Add cancel-pending and switching drain barrier. Wire resource maintenance, diagnostics and assisted-exclusive arbitration from section 8.7. Exit: 32 queued fake/native-fixture targets drain safely, repeated detector hits do not duplicate jobs or recut their own newly generated fragments, releasing swing preserves work, stale handles and duplicate content IDs are safe.

### Package 8 — Enable bounded parallel geometry and vessel intents

Raise selectable worker count to 1–4 after shared-state checks. Ensure `SliceInput.Geometry` and material/arrays are frozen; audit all code reachable from geometry/export for mutable statics. Keep detached loading serialized. Add vessel transaction leases, local evidence rebasing and queued same-vessel processing from section 8.5. Exit: cancellation/exception/memory tests pass with concurrent completions; geometry overlaps on multiple independent sources, native preparation/commit stays serialized.

### Package 9 — Extend native clutter interest

Implement the two exact clutter-bound call-site replacements and immutable interest lifetime. Verify actual renderer/native phase behavior, no global bounds mutation, disabled collision setting behavior, native cell readiness and teardown. Exit: native physical coverage includes the 50 m blade region after warmup; invalid hooks safely disable the feature; cell generation/installation costs are measured. Do not enable reach extension by default.

### Package 10 — Compare and document

Run deterministic detector/plane fixtures and native forest runs, including all defaults, suggested experimental profile and worker counts 1/2/4. Suggested forest profile: `cached-ribbon`, `capsule-sweep`, `blade-limited`, `queued`, two workers, extended clutter coverage enabled, passive cuts off and manual SWING click-drag as the trigger. Report actual throughput, accuracy, fallback rate and frame cost; retain legacy algorithm defaults and the new intentional-only activation default. Update `docs/ACCEPTANCE.md` only with checks actually performed and explicit remaining native gaps. Exit: each advertised option has a validated implementation, switching semantics are documented, and the forest acceptance result is reported without conflating queued/detected/toppled targets with completed mesh cuts.

## 13. Research basis and limitations

The local KSA/decompiled Bepu sources take precedence over online API versions. The KSA modding skill's lifecycle overview was used to locate the execution model and checked against this repository's hooks. Online primary documentation confirms the general query capabilities: [Bepu getting started, Queries](https://github.com/bepu/bepuphysics2/blob/master/Documentation/GettingStarted.md), [Simulation query API](https://docs.bepuphysics.com/api/BepuPhysics.Simulation.html). Native sweeps support linear/angular convex motion and treat queried objects as stationary. These facts support the capsule option, not a claim of exact render-mesh collision or observed performance.

`LongRunning` requests scheduler treatment appropriate to coarse long-running work and possible additional threads; it is not a concurrency limit. See [Microsoft TaskCreationOptions](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskcreationoptions). The proposed fixed pool is a design choice to control simultaneous CPU work and priority, not a requirement to use KSA's own schedulers.

The algorithms, limits, fallback policies, worker defaults and acceptance thresholds proposed here are implementation decisions. Their performance must be measured. Some visible forest objects may remain unavailable while native physical cells are loading; no method in this plan can reconstruct an unobserved historical collision exactly. Preserve explicit coverage/partial-result diagnostics so this limitation is observable.
