# Quiet breakup, repeat slicing, reach settings and manual contact

Research and implementation plan, 2026-10-23. Sections 1, 2 and 3 now have implementation revisions recorded in `docs/ACCEPTANCE.md`; section 4 retains its planned scope.

Baseline: Purr2D2 commit `8919257b3debed0d73092b3b4aadb418a96465ea`; local KSA sources/build `2026.10.19.5638`. Three GPT-6.1-sol / xhigh research agents examined breakup, mesh precision and manual contact. The KSA modding skill supplied lifecycle guidance; member names were checked against the newer local game sources. Existing documentation that says contact cutting is retired describes the old behavior; this request intentionally changes that policy for manual swings.

## Recommended outcome

1. Slicing quietly replaces the selected part with inert pieces. Only the selected source loses its stored contents. Engines stop functioning as engines, landing legs stop functioning as suspension, and neither uses the game's explosion-driven failure path.
2. Valid generated debris remains eligible for further cuts, including after save/load. Repair numerical slivers when topology can still be validated; do not accept holes or arbitrary invalid meshes.
3. Settings exposes the assisted-selection grace allowance. Preserve the existing default: maximum hilt-to-hit distance is **2 × deployed blade length**.
4. Actual contact during an intentional manual SWING can request a slice. Detection has bounded work; expensive geometry preparation stays asynchronous. Assisted-selection grace does not enlarge physical contact.

## 1. Quiet breakup

### Findings

The current mod does not intentionally invoke an explosion when replacing a part. Native isolation already calls `Vehicle.Split(..., 0, ...)` in `src/Slicing/NativeSliceIsolation.cs:87`. The replacement path drains consumables, creates module-free wreckage and deletes the source in `src/Slicing/NativeSlicePhysics.cs:168–184, 211–221, 286–293`. Plain native `Universe.DestroyVehicle` (`Universe.cs:2016`) does not invoke the explosion system; event-driven destruction does (`Universe.cs:1969`).

There are three different risks to address:

- **Frame-dependent spin calculation — confirmed in code.** At `NativeSlicePhysics.cs:246–262`, the solver subtracts `cross(newGroupCOM - oldCOM, absoluteLinearMomentum)` from fragment spin. The geometry uses volume-weighted mass and can have a different center of mass from the native source. Consequently, adding a uniform velocity to an otherwise identical cut can radically change its spin. The existing test at `tests/SlicingPhysicsChecks.cs:38–49` asserts this formula rather than testing invariance under uniform translation.
- **Collision separation — credible mechanism requiring native reproduction.** `Vehicle.AdoptSeparatedVehicle` creates collision-avoidance pairs, but their default is directional filtering, not suppression of every contact. Single convex fragment hulls can overlap around concavities/faceted seams or with neighbors. A collision solver can impart much more velocity than the configured slice separation.
- **One simulation interval before replacement — confirmed sequencing, unconfirmed contribution to the observed explosions.** Isolation and replacement happen on different safe updates. Resource draining currently occurs only during replacement. The isolated source can therefore spend an intervening step with fuel, thrust or suspension behavior still present.

The explicit opening velocity is already small: each piece receives `normal × (sideSign - massWeightedMeanSign) × 0.12 m/s` (`NativeSlicePhysics.cs:253–255`). The relative opening speed between opposite sides is **0.24 m/s**, independent of the unequal mass weighting. Reducing this constant alone will not fix the other mechanisms.

KSA's `PartFailureEvent.Apply` explicitly spawns explosion effects, then sheds/destroys parts. Non-tank failures route to `PopSmallExplosion` even without fuel (`../ksa-game-assemblies/current/decomp/KSA/PartFailureEvent.cs:36–46, 55–80, 101–107`). Emptying a tank does not make that failure pipeline a quiet breakup API.

An independent arithmetic check of the current formula illustrates the spin defect: with mass 10 kg, COM offset `(0.4,-0.2,0.1)` m, diagonal group inertia `(12,14,18)` kg·m² and zero original spin, changing uniform velocity from zero to `(0,7800,0)` m/s changes computed spin from zero to `(650,0,-1733.33)` rad/s. This is a synthetic calculation, not an in-game measurement.

### Implementation

1. Extract fragment motion assignment into a pure helper. Define the desired behavior as inherited bulk motion and rotation plus a tiny, mass-balanced opening velocity. Remove conversion of absolute translational motion into spin. Prefer inherited source angular velocity with a group velocity correction that preserves total linear momentum; explicitly document the approximation caused by replacing native mass distribution with uniform geometric density. If exact angular-momentum preservation is required later, first preserve a physically consistent mass distribution/COM. Do not introduce large rotational energy to compensate for the current approximation.
2. Make opening speed a named internal constant expressed as **relative opening speed**, initially retain 0.24 m/s and tune downward during native acceptance if needed. Keep separation momentum-balanced, without random angular kicks. Add sanity limits on newly introduced motion based on relative speeds and energy, not the current 300,000 rad/s bound.
3. Introduce an owned quiet-breakup transaction covering isolation through replacement. Finish geometry/assets and admission checks before mutating the source. Split its external resource connections with zero impulse; as soon as isolation succeeds, drain only that isolated source and disable its active functional behavior before the next physics dispatch. Preserve the existing off-center mass-change position/velocity correction. Never call `DepleteConsumables` on the unsplit host vessel.
4. Update failure/cancellation semantics explicitly. Before isolation, cancellation leaves the original intact. After isolated draining, a retained source is separated and empty; do not claim its contents were restored. Keep the two-update lifecycle unless host profiling proves same-update replacement safe; the interval exists for native-state reasons.
5. Protect only the vehicles/pairs affected by this transaction during initial separation. Investigate native `CollisionAvoidancePair.SuppressAllContacts` (`CollisionAvoidancePair.cs:13`; `NarrowPhaseCallbacks.cs:86–105, 184–190`) for source/neighbor and replacement/neighbor/sibling pairs. Register and remove protection at the settled update, track ownership, and clean up on cancellation, disposal, world changes and bubble changes. Expiration must consider actual separation: a timer alone can release still-overlapping convex hulls. Keep terrain and unrelated vehicles collidable. Do not globally disable crash damage or explosion effects.
6. Instrument failure scheduling around this transaction. If a narrowly scoped failure guard is still needed, protect only transaction-owned source/fragments for the audited initialization interval, including deferred event application. Do not clear unrelated damage events or merely hide explosion graphics while destructive failure processing continues.
7. Prefer actual mesh cutting. When a part cannot be cut geometrically, prepare a **quiet authored-subpart breakup** as a fallback, using the same inert-asset, mass, admission and transaction rules. Native `Vehicle.SpawnSubPartDebris(..., separationSpeed)` (`Vehicle.cs:1771–1815`) is a useful reference, not an automatic complete solution: validate inherited modules, transforms, mass, durable assets and sibling collision behavior before reusing it. Do not call `PartFailureEvent.Apply`, `ShedDebris` or `IsolateAndDestroy` as the fallback. Stock shedding is destructive, probabilistic and uses its own separation policy.
8. Bound fallback fragment counts; group tiny trim where necessary, conserve source dry mass, preserve materials/pose, and remove the source only after every replacement is ready. If both strategies fail validation, retain the source with an explicit reason. A malformed mesh must never silently become an explosion.

Collision-pair implementation details: native adoption copies relationships without copying `SuppressAllContacts`, so explicitly reapply the policy to replacements. Avoid duplicate active pairs because lookup uses the first matching pair. Native pairs retire after a pass without overlap (`PhysicsBubble.cs:213–223`); monitor long-lived overlap rather than assuming a fixed timeout guarantees clearance. Marking a vehicle as debris alone does not suppress contact-pressure failure.

For a possible temporary failure guard, Unscience's `the-tick.lib/TickProtection.cs:20` and `TheTickPatches.cs:74` demonstrate exact-identity worker-safe protection while preserving already pending destruction. Its structural-failure reflection lookup is stale: this KSA build takes `(VehicleUpdateState, SimStep)`. Use the design as reference, not copied bindings. The existing settled hook and Unscience's `ksa-abstractions.lib/PhysicsFrameHook.cs:106` supply the mutation-order precedent.

### Acceptance

- Full/empty tanks, firing/idle engines, extended/retracted landing legs and generated fragments split without a slice-caused explosion, blast impulse, random spin or secondary failure cascade.
- Test both free flight and ground-supported legs; compare motion immediately before isolation, after isolation, after replacement and after the next solver step.
- Repeat the same pure motion case at zero, surface and orbital bulk speeds: relative fragment motion must be invariant under a uniform velocity boost.
- Test unequal masses, off-center fuel, rotating assemblies, docking boundaries and neighboring fueled parts. Only selected-source consumables are removed.
- Record failure event types, source/new COM, body rates, relative velocities, hull overlaps and collision-protection lifetime. This distinguishes the confirmed motion bug from still-unproven collision/failure mechanisms.
- Verify cancellation and spawn failure at each transaction phase; no orphan protection, lost neighbors, duplicate parts or inaccurate status messages.

## 2. Make generated debris reliably sliceable again

### Findings and reproduced failure

The error comes from the mod's own positional-weld audit (`src/Cutting/PlaneSlicer.cs:626–639`), not an opaque native physics error. Two distinct triangle vertices become the same vertex under the chosen tolerance.

`TrySliceCompound` computes a tolerance from the entire compound bounding diagonal (`PlaneSlicer.cs:66–72`). It then accepts each group with `TryAudit(group)` and immediately re-audits it using the larger compound tolerance (`:80–81`). Small detail that is valid at its own scale can collapse on that second audit. Export similarly audits solids individually in `src/Slicing/SliceMeshPrecision.cs:15–88`; its final compound audit also delegates to individual solid audits. Thus a fragment can be accepted for export and rejected as the next input.

The research agent reran the existing compiled `StockSliceChecks` with `PURR_STOCK_FILTER=HeightB`. Both exported service-module halves failed recutting with this exact unprefixed error, after export/reload validation. `tests/StockSliceChecks.cs:68–85` currently treats that outcome as a known precision limit, allowing the test to pass despite **0/2 successful recuts**. That exemption must become a regression assertion. The run used existing compiled artifacts; implementation validation must rebuild from source before claiming a fix.

### Implementation

1. Separate tolerances for topology identity, plane classification and tiny-area/volume rejection. Share a consistent topology policy between input preparation, compound decomposition, clipping, float32 export and reload. Start by eliminating the conflicting per-group versus whole-compound weld in the duplicate audit. Keep small components independent; do not raise all weld tolerances to the largest object's scale.
2. Reuse component discovery from a single audit instead of accepting a solid and immediately re-auditing it under a different rule. Check remaining uses of the compound epsilon through clipping/capping; fixing line 81 alone is not proof that all later cuts work.
3. Extract the existing conservative repair operations from `SliceMeshPrecision` into a reusable normalization stage: positional welding within a bounded tolerance, collapsed-face removal, controlled duplicate/opposite-face handling and stitching only existing collapsed-edge T-junctions. Apply only when needed. Preserve per-corner UVs/normals/materials while sharing topology positions.
4. Accept a repaired candidate only if it retains closed, consistently oriented manifold shells, valid cavities, positive volume and usable inertia, with tightly bounded volume/position changes. Never drop arbitrary bad triangles and proceed. Do not weld separate overlapping authored solids together or invent caps across arbitrary missing surfaces.
5. Preserve `FragmentTopology` identities through material-merged render meshes. Material batching is not the same as connected-solid grouping. Make the exact exported/reloaded representation satisfy the next input contract before publishing a fragment. Repair old generated assets on copied input; preserve durable originals and report unrepairable assets clearly.
6. Keep fixes on the geometry worker with cancellation and existing face/solid/attempt limits. Cache an accepted normalization result for that immutable source input rather than repeating it for every candidate plane.
7. Improve diagnostics: stage, solid/group, tolerance, shortest edge or collapsed vertices, repair attempt, and post-repair topology failure. Distinguish repairable numerical slivers from genuinely open/nonmanifold geometry.

Additional constraints from the audit:

- Preserve one common cutting plane for the compound. `StablePlane` (`PlaneSlicer.cs:594`) deliberately avoids independently nudging overlapping members onto different planes. Canonicalize source topology before selecting that plane.
- Mark previously generated closed-solid input explicitly. `SliceShellBuilder.cs:24` currently routes any failed audit toward authored-sheet preparation, which can add 4 mm backing. A damaged generated solid should not silently become a new backed panel.
- Prefer a rotation-invariant local scale and deterministic weld representatives. The current bounding-box scale changes with rotation, and first-seen representatives can change when material batching reorders faces.
- If durable output semantics change, review the content-identity version at `FragmentAssetWriter.cs:167`. Old saves still need a compatible read/repair path.
- The current edge-use/winding audit is not a full self-intersection or vertex-link manifold proof. Retain and improve existing validity checks without claiming more than they establish. The exact failing triangle/edge lengths still need diagnostics, and a later clipping/export issue may appear once the first refusal is fixed.

### Acceptance

- Remove the Height B exemption: both halves must export, reload, recut and validate their new outputs.
- Add a minimal fixture with a small valid solid and a much larger companion solid that reproduces the tolerance mismatch without stock assets.
- Repeatedly cut, export and reload tanks, Height B and the other stock service modules through several generations, including rotated/scaled assembly coordinates and thin cap intersections.
- Preserve material/UV/glass grouping and `FragmentTopology`; verify positive volume/inertia and bounded mesh growth.
- Negative fixtures with holes, nonmanifold edges, duplicate shells or unrecoverable float collapse must still refuse without native mutation.
- Infinite recutting is not promised: pieces can eventually reach a genuine precision, mass or resource floor. Such refusals should explain the actual limit rather than disguising a correctable audit inconsistency.

## 3. Configurable slice grace

### Existing behavior

Before this revision, `PartSliceTargeting.SelectionReach(bladeLength)` multiplied the visible length by 2 internally. Native hover, fallback selection and stroke-start checks used the live hilt and selected surface point. The allowance was proportional to deployment rather than a fixed metre offset.

Implemented: `SaberLimits.SliceSelectionReach(visibleLength, multiplier)` now computes the maximum distance once for each runtime check. Targeting accepts that explicit distance in metres for native hover, fallback broadphase and live selected-point validation. Settings persists `SliceReachMultiplier`, defaults to 2× and clamps finite values to 1–4×; nonfinite multipliers recover 2×. The visible Settings row shows current and fully extended reach, applies edits immediately, and uses the existing Save preferences action. An accepted stroke uses explicit ownership/geometry validation without an infinite reach sentinel, and still finishes after movement. Physical contact and Force tolerance are unchanged.

### Implementation

- Add persisted `SliceReachMultiplier` in `src/Core/SaberSettings.cs`, default **2.0**. Proposed UI range: **1.0–4.0×**, with nonfinite values falling back to 2.0. This range is a product choice, not an engine restriction.
- Add a **Slice grace / reach** row in Settings using the existing settings-table pattern in `src/Runtime/SaberRuntime.Ui.cs`. Explain “Maximum selection distance = visible blade length × this value; 1× adds no extra grace.” Show effective maximum distance in meters and, where useful, extra grace `(multiplier - 1) × visibleLength`.
- Use one shared reach calculation across hover feedback, fallback broadphase, selected-point validation and stroke-start revalidation. Replace the hard-coded “twice the visible blade length” status strings. Keep reach policy separate from ownership/freshness validation instead of expanding reliance on the current positive-infinity sentinel.
- Apply edits immediately before a stroke starts; an accepted stroke keeps its current behavior of completing after the player moves. Save through the existing Save preferences action. Old settings files retain exactly the current 2× behavior.
- Do not change visible blade size, manual-contact reach or Force clutter tolerance.

Extend `tests/CoreChecks.cs` settings default/round-trip cases and `tests/contact/TargetSelectionChecks.cs:42–78` for 1×/2×/4× boundaries, partial deployment, invalid settings, moving hilt, camera-independent distances and hover/fallback agreement.

## 4. Cheap manual-swing contact cutting — implemented (2026-10-24)

The implementation uses a dedicated `ManualContactProbe` rather than expanding the cosmetic 20 Hz probe. Real overlay SWING presses own monotonic gesture IDs; a confirmed rendered-triangle sweep accepts at most one shared asynchronous quiet cut without the assisted animation. Discovery, candidate-frame reads, primitive reads, triangle work, candidate storage, subdivisions and query age all have explicit bounds, with a 0.5 ms soft clock and diagnostics for observed work/time, saturation and expiry. Frozen queries survive ordinary solver/render gaps; release/recovery preserves accepted work. Lifecycle/pose/Force invalidation cancels safely in the settled handoff. The current automatic geometry chooser supplies the seam. Final managed validation passes (132 contact/targeting checks and 27 runtime checks), the complete managed/asset/shader suite passes, and Release packaging builds without warnings/errors. Detailed behavior, limitations and pending native measurements are recorded in `research-slicing-physics.md` and `ACCEPTANCE.md`; the findings and design below preserve the original review rationale.

### Findings

There is already a bounded visual-contact probe in `src/Rendering/SaberContactProbe.cs`. It samples at 20 Hz, scans at most 4,096 triangles and 256 meshes per sample, and rechecks the previous hit triangle first. However, candidate refresh enumerates nearby scene geometry, and the scan is round-robin. It tests only the current blade segment (`:138`); fast motion can cross a part between samples. Runtime currently routes its hits only to sparks (`src/Runtime/SaberRuntime.cs:116–122`).

Contact can cheaply identify a target without running the slicer every frame. The heavy operation is still queued once and prepared by the existing worker. Simply wiring every spark hit to slicing would allow idle-touch cuts, duplicate requests and missed fast strokes.

### Recommended first implementation

1. Arm contact cutting only for the controlled kitten during an intentional SWING drag with nontrivial motion, with the blade lit, eligible pose, supported time step and fresh rendered-pose samples. Use `_overlayDrag` and a monotonically increasing gesture ID (`SaberRuntime.SaberOverlay.cs:351,536`), not the unused pose-controller `Swinging` path. End the gesture on release, focus loss, hiding the controls, switching kittens, boarding, disabling or world changes. Returning to the rest pose must not create a new cutting gesture.
2. Capture consecutive visible blade endpoints and the necessary owner/target transforms in a common reference frame. Do not compare old and new body-local endpoints as though the kitten and target were stationary. Reset history across discontinuities, stale poses, teleports and unsupported time steps.
3. Extend the existing mesh probe with a conservative swept-blade bounds query, cached candidate bounds and exact surface confirmation across consecutive samples. Use the actual rendered surfaces so convex physics hulls do not cut empty recesses. Cache acceleration structures per immutable mesh when profiling justifies them; construct them incrementally or on copied worker data, never synchronously for an entire fleet.
4. Bound candidate refresh as well as triangle tests. Keep query and sweep-history budgets, reuse storage, and record saturation. Start with the existing 4,096-triangle envelope, then measure. Under budget exhaustion, preserve the frozen sweep endpoints and scan cursor for a bounded deferred query; replacing its endpoints with the newest pose loses the crossed surface. Expire stale queries explicitly, never authorize a cut from bounds alone, and reset the old 0.5-second candidate cache at gesture/world/fragment changes. Confirm swept ribbons against triangles, with subdivision for appreciable rotation/nonplanarity and tests for coplanar cases. Define the supported approximation error; finite endpoint sweeps cannot guarantee every arbitrarily fast curved motion.
5. Convert a confirmed contact subpart to its structural part and revalidate ownership, physics bubble, parent body, internal-part rules and resource budgets. Use actual contact distance, independent of assisted reach grace. Accept at most one cut per gesture initially, with one transaction in flight, and require a deliberate new gesture to cut again. Cache rejection for that gesture to avoid retry/error floods. Choose the first confirmed contact for the bounded first version; claiming the globally earliest contact requires completing or ordering all remaining candidate queries.
6. Reuse capture, worker preparation, upload, quiet isolation and commit through a shared cut coordinator. Manual contact should supply the trigger and avoid replaying the assisted auto-aim animation. Keep native reads and mutations at the existing settled `BeforePhysics` handoff; the worker continues to receive copied geometry only.
7. The first version may use today's automatic cut selection on the contacted part. This satisfies “contact the part, then slice it.” An exact swing-aligned seam is a separate enhancement: `SaberRuntime.cs:51` currently ignores the supplied plane and `SliceGeometryWork.cs:27–38` chooses faceted/automatic candidates. If added, carry the contact point and blade-motion plane into source coordinates, preserve them across isolation, and try that plane before the quiet subpart fallback. Do not pretend the current plane parameter already does this.

Native Bepu ray/sweep queries are a possible later broadphase optimization. Existing Force clutter code demonstrates safe queries; vessel candidates can be resolved through `ConstraintSim.HandleToState` and `VehicleUpdateState`. Native collider hits still need rendered-surface confirmation, and collider misses may not cover every visible surface. They are not automatically a complete replacement for the mesh probe.

### Performance and gameplay acceptance

- Zero structural queries when manual contact cutting is unarmed; no repeated geometry preparation while the blade remains in one part.
- Profile cold first contact and warm queries separately on dense craft/debris. Initial target: warm contact work below **0.25 ms p95**, with an explicit bounded per-update work limit; this is a proposed target, not a measured result or hard real-time guarantee.
- Sweep across thin legs at 30/60/144 FPS, supported simulation speeds, moving/rotating targets, blade base/middle/tip contacts and maximum blade length. Verify exact visible contact, no self/other-kitten cuts and no false hit in a collider-only recess.
- Confirm no idle, ignition-only, return-to-rest or other-kitten automatic cuts. One deliberate gesture cannot recursively shred its own newly generated debris.
- Exercise pause/resume, stale rendering, cancellation, geometry failure, resource saturation and world changes. An asynchronous completion cannot commit against a moved/replaced/disposed source without the existing freshness checks.

## Delivery order and verification

| Step | Scope | Exit condition |
| --- | --- | --- |
| 1 | Repro fixtures and narrow slice diagnostics | Motion-frame defect and Height B recut failure have source-built regressions; native leg/engine/tank observations are recorded |
| 2 | Quiet motion, isolated draining and collision lifecycle | No slice-caused blasts or high-energy separation in the native matrix |
| 3 | Consistent topology audit and conservative repair | Height B exemption removed; exported/reloaded fragments pass repeated-cut checks |
| 4 | Quiet subpart fallback | Difficult engines/legs break up with materials, bounded motion and durable save/load |
| 5 | Reach setting | Default behavior unchanged; settings and all selection paths agree |
| 6 | Bounded manual contact and shared cut coordinator | Actual manual contacts trigger one quiet asynchronous cut within measured budgets |

The reach setting can be implemented independently. Manual-triggered destruction should follow the quiet-breakup and repeat-slicing fixes so it does not amplify the current failures.

Run the relevant managed checks through `tools/validate.py`, stock geometry through `tools/check_stock_slices.py` against the local game content, and host metadata checks for any newly bound native methods. Use the repository's build validation after implementation. Managed checks do not establish native physics behavior; complete the targeted in-game matrix and save/load recuts before declaring these changes accepted.

Update README, `docs/IMPLEMENTATION_PLAN.md`, `docs/research-slicing-physics.md` and `docs/ACCEPTANCE.md` with the resulting behavior, especially the changed cancellation semantics and manual-contact policy. These are code/geometry/physics changes; new Blender models or textures are not required.
