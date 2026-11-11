# Arbitrary slice physics and persistence

## Stock strut and panel mass investigation, 5801

The reported "The part has no finite positive inert mass." originated in
`NativeSlicePhysics.ValidateSource`. `SliceSourceCapture` runs this check before
reading render geometry. The previous `SourceMass` summed `Part.ComputeSubtreeInertMass()` over
the selected structural shell and internally attached parts. KSA's method sums
`InertMass` modules built from authored mass templates and `IInertMass`
components. Visible geometry and colliders alone supply no part-level mass.

All 19 strut and 32 panel templates in CoreStructuralA have no authored inert mass in their
definitions, parent definitions, referenced subparts or Core GameData patches.
This accounts for the reported refusal on both bare trusses and their foil variants.

KSA handles a vehicle whose total authored inert mass is zero in
`Vehicle.UpdateAfterPartTreeModification`. Its
`ComputeFallbackVehicleMassPropsAsmb` computes solid-cuboid mass and inertia from
the assembly-space MeshView bounding box, using a density of 100 kg/m³. Invalid
or zero box dimensions use a one-metre cube. The result belongs to the vehicle's
physics properties and is exposed through `Vehicle.InertMass`. The individual
parts still report zero from `ComputeSubtreeInertMass()`. A massless strut attached
to a positive-dry-mass vessel starts using fallback vehicle mass when native
isolation makes it its own massless vessel.

Purr2D2 needs finite positive mass to construct fragment inertia, split source
mass between fragments and assign balanced separating motion. Removing the
preflight check alone would reach the worker's invalid-fragment-mass refusal.
`ValidatePreparedFragments` also checks source dry-mass conservation, and the
final swap compares the created fragments' native total mass with the emptied
isolated owner's mass.

`SourceMass` now delegates to `SliceSourceMass.Resolve` throughout capture,
preparation, isolation and replacement. Authored nonzero mass keeps its original
value, including invalid values that preflight must reject. An already isolated
source uses `Vehicle.InertMass`, which includes native fallback mass and excludes
stores.

For an attached massless structural source, the resolver predicts the native
detached frame. A root retained while its children separate keeps its existing
assembly-space bounds. A detached child keeps its position and scale, receives
identity root rotation, and carries internally retained full parts through the
same native pose transformation. Bounds are recomputed from the same direct
visual subparts, MeshView primitive and float32-transformed picking vertices as
KSA. This preserves the native rounding behavior for scaled parts far from the
assembly origin. Neighboring parts and consumables contribute no source mass.

After isolation and draining, `TryIsolate` rechecks the prepared masses against
the actual source owner before authorizing replacement. Unexpected native frame
restoration or mass changes retain the separated empty source. The final swap
also checks the actual created vehicle masses before removing the source.

Current source references are `KSA/InertMass.cs` (`ComputeTotalAsmb` and
`CreateComponents`), `KSA/Part.cs` (`ComputeSubtreeInertMass` and
`ComputeSubPartBoundingBox`), `KSA/PartTree.cs` (`ComputeBoundingBoxAsmb`), and
`KSA/Vehicle.cs` (`UpdateAfterPartTreeModification` and
`ComputeFallbackVehicleMassPropsAsmb`) under
`../ksa-game-assemblies/current/decomp/`.

### Reproducible structural tests

Run from the repository with the full stock Content/Core directory, including
mesh atlases and GameData XML.

```sh
python3 tools/check_stock_slices.py --structural /path/to/Content/Core
python3 tools/check_stock_slices.py --fixture artifacts/checks/structural-slices.json
```

The new mode audits all 51 strut/panel templates' missing mass declarations, copies
rendered meshes with their authored subpart transforms and exercises the
production worker. Each target gets an assisted cut, a blade-limited manual
contact cut at a real rendered triangle near 57% of its X extent, and a
zero-mass worker-input refusal case.

| Fixture suffix after CoreStructuralA_Prefab_ | Coverage |
|---|---|
| StrutCubeHalfW1HalfHA | Transformed square truss and mirrored endcaps |
| StrutHex1HA | Hexagonal truss and diagonal braces |
| StrutOcto2HA | Large octagonal truss with 36 authored subparts |
| StrutOcto2HB | Foil variant with replacement subparts and a second mesh atlas |
| PanelAA | Thin triangular panel |
| PanelAC | Frame and inset panel from different mesh atlases |

Positive geometry cases use an explicit 100 kg test input to exercise geometry
beyond the current native preflight refusal. They require two true cut fragments,
fresh cap meshes, conserved fixture mass, a manual anchor residual of at most
1 mm, XML/GLB reload with closed topology, and Bepu convex-hull cooking. The
zero-mass worker cases require rejection without publishing a slice. Cached
fixtures retain the geometry test mode, and the XML mass audit runs during fresh
fixture generation. Local fixtures remain in ignored `artifacts/checks/`.

The run passed all 12 positive geometry cases and six zero-mass refusal cases using
`/Users/asherwin/repos/meow-sci/ksa-linux/Content/Core`. Its sibling KSA.dll
byte-matches the 5801 reference, and its structural/surfaces XML matches the
reference XML. CoreStructuralA_MeshAtlas.glb has SHA-256
`84a2fb0308c67a77c666cd6ecf9887fd6f921e4783052b772e70d727b0119220`.
Native source capture, isolation, replacement and live physics still need the
in-game cases recorded in `ACCEPTANCE.md`.

`python3 tools/check_fragment_templates.py` also runs
`SliceSourceMassHostChecks` against real KSA meshes, Part/PartTree instances,
`SetRootPose` and the engine's own fallback mass routine. It covers retained
roots, detached children at 45-degree rotation and 10 km translation, scales
0.5×/1×/2× and nonuniform scale, retained internal parts, stored-content exclusion,
authored/invalid mass and native empty-bounds behavior. Isolation regressions
verify that a post-separation mass mismatch retains the empty source.

## Existing slice transaction

The slice geometry is authored in the original vehicle's assembly frame in physical metres. Every source subpart transform, including scale, is baked before clipping. `SlicePrincipalFrame` recentres each closed component on its exact volume centroid, diagonalizes its complete unit-density inertia tensor, and rotates the geometry into a proper right-handed principal frame. Its diagonal specific moments are persisted in native `CustomMass` XML. This preserves offdiagonal inertia through ordinary native save/load without a runtime-only mass override.

`NativeSlicePhysics.ValidateSource` accepts finite positive inert mass on a native-physics vessel, including occupied vessels. Tanks, service modules and capsules can become inert wreckage; their functional modules are discarded. Existing dents and deployed parachutes are refused. External connections must join actual full parts on unique parent-child boundaries; reinforced links, subpart endpoints and non-tree graphs are refused. Work and native fragment counts are bounded.

`TryReplace` is synchronous and must run at the joined simulation handoff after input events, before new workers dispatch. It requires two to eight registered, GPU-ready, durably written fragment templates and their already constructed native Part/PartTree instances. Lazy convex-hull cooking, module construction and detached tree preparation run on separate earlier preparation frames while the source remains intact. Commit checks total mass against the source inert mass, then transfers each single-use instance to its native vehicle.

After the assisted swing (or confirmed manual contact) and fragment preparation, the source is isolated with stock `Vehicle.Split` calls, preserving attached neighboring branches as their own native vessels. The original external boundaries are captured before mutation: the parent is split first, then children are split from the smaller current owner. Touched owning trees use nested-safe `BeginBulkChange` / `EndBulkChange` scopes that all close before the hook returns. The final replacement occurs on a later settled update, after native derived-data flushing and worker preparation. The source remains a live native shell in that interval, but its contents are already released, its engines/thrusters switched off, and its functional module updates held. Gravity, terrain contact and normal simulation can still move separated vessels. Cancelling retains the separated, empty source with its engines off rather than reconstructing native connections or restoring released stores. The old target transform and its new isolated transform give the original-assembly to isolated-assembly mapping; no source scale is applied twice. Source capture and hit coordinates are rebased for that known isolation, then revalidated before replacement. Final pose and velocity are read from the live isolated owner at the swap, so the intervening physics step is not overwritten with stale motion. `Vehicle.CreateVehicle` adopts each prepared root/tree, and a narrow delegate to native `AdoptSeparatedVehicle` establishes bubble ownership and collision-avoidance links. This follows the native debris path without its repeated Part construction or temporary box-inertia override. Exact durable template inertia is present from construction, so a second full configuration pass is unnecessary. Immediately after isolation, native depletion affects only the selected shell and its internal parts. Native mass/collision refresh preserves its assembly pose and rigid velocity field across an off-center COM shift. Nozzle performance caches are cleared too, so a firing source cannot retain stale thrust. An immutable exact-worker identity hold gates all three module-update call sites in `PhysicsBubble.UpdateModulesJob.ExecuteOnBubble` and `PhysicsBubble.ApplySingleVehicleMotion`; caller transpilers avoid bypass through `AggressiveInlining`. The hold also freezes deployed leg/engine animation for the required intervening solver interval. Cancelling releases the module hold; released stores and disabled engines are not restored.

After creation, motion is assigned in parent CCI coordinates rather than rotating CCF physics coordinates. `QuietSliceMotion` preserves inherited source angular velocity and balances total linear momentum around the generated mass centroid. With source bulk velocity `v0`, angular velocity `omega0`, fragment centres `r_i`, masses `m_i`, and side signs `s_i`:

```text
c1 = sum(m_i * r_i) / sum(m_i)
meanSign = sum(m_i * s_i) / sum(m_i)
opening_i = normal * (s_i - meanSign) * (0.24 m/s / 2)
v_i = v0 + omega0 cross (r_i - c1) + opening_i
omega_i = omega0
```

The named 0.24 m/s constant is the relative opening speed between opposite sides, not a per-piece impulse. Added opening energy is bounded by `M * (0.12 m/s)^2 / 2`, and no angular kick is introduced. A uniform surface/orbital velocity boost produces the same relative motion. Final CCI velocities are converted through native CCI-to-CCF helpers as needed, preserving the bubble origin. The fragment's cached `BodyRates` is synchronized through its exact private setter so a same-frame native save records inherited rotation. Flight-plan and navigation refresh happens once after this assignment.

`QuietSliceContacts` upgrades only native avoidance relationships among the source and branches separated by this cut, then reapplies `SuppressAllContacts` to replacement/neighbor and sibling relationships because native adoption copies links without their suppression flag. Existing active matches are reused, including duplicates that could otherwise precede the protected match in native lookup. The manager never suppresses terrain or unrelated craft. Native narrowphase still records actual penetration and retires pairs after a clear pass; a timer cannot release a trapped overlap. Settled updates log long-lived overlap and remove managed ownership on native retirement, disposal, world changes or bubble changes. Cancellation and disabling keep pumping clearance. Unload releases managed references while leaving live same-bubble pairs to native retirement, preventing a last-frame collision kick.

`PartFailure.Detect` and the current two-argument `PhysicsBubble.DetectStructuralFailure` report newly scheduled failures for the held source and replacements during their first solver interval. `PartFailureEvent.Apply` is also observed for those exact participants; no event is swallowed. Existing damage, terrain collisions and unrelated failure events remain native behavior. This revision does not add a global failure/explosion guard; native acceptance must identify any remaining source of destructive initialization failures.

Only after every fragment exists with valid mass and kinematics is the isolated source deleted with `Universe.DestroyVehicle` and `CrewDisposition.Kill`, matching native destructive events. Crew in separated neighboring branches is not removed. Before that deletion, a failure destroys newly created fragment vehicles and retains the source. Native isolation itself is not rolled back: a later failure can leave the original part intact in a separated vessel with an explicit diagnostic. Once source disposal has begun, replacements are retained even if native teardown reports an error; deleting them would lose the already removed geometry.

Uniform-density slicing conserves total source dry mass but approximates an authored primitive mass distribution. Visible geometry is held fixed, so its new group COM can differ from the original physical COM. We preserve bulk linear momentum and angular velocity rather than forcing exact angular momentum about a displaced, fixed old COM: the latter previously converted absolute orbital translation into enormous spin. Exact angular-momentum preservation across arbitrary source mass distributions is not claimed.

`QuietSubpartBreakup` is the final worker-side fallback after all mesh cuts fail. It prepares and validates each authored subpart with the existing closed-shell rules, retains all surface attributes, groups small trim with the nearest of at most eight substantial subparts, and builds the same immutable GLB/XML assets, mass/inertia, collider and detached native preflight used for sliced meshes. Mass is volume-weighted and sums to source dry mass. Both side signs are assigned deterministically so even co-centred subparts receive balanced opening motion. Generated closed debris is explicitly ineligible for this authored-sheet fallback. Invalid geometry or too few valid pieces refuses before isolation. No `SpawnSubPartDebris`, `PartFailureEvent.Apply`, `ShedDebris` or `IsolateAndDestroy` is used for fallback creation.

Native collision uses a convex hull of all fragment positions. The visible mesh remains the exact closed clipped surface, including holes and concavities; its exact geometric inertia is retained. The collision proxy fills holes and concavities. This is a stated experimental limitation, not an assertion of exact collision geometry. A hollow cut tube can look hollow while its collision proxy remains solid.

Durability is owned by `RuntimeFragmentAssets`: immutable content-addressed GLB/XML files are written before commit, and native `PartInstance.InstanceOf` records their stable generated template IDs. Startup loads generated XML at `StarMapImmediateLoad`, before ordinary global file loading and binding and before native missing-part validation. Old generated assets must not be removed merely because the current save does not use them; another saved vessel or save can still reference those IDs. Moving a save to another installation requires the corresponding generated assets.

Verified current-source paths under `../ksa-game-assemblies/current/decomp/`:

| Source | Integration |
|---|---|
| `KSA/Part.cs`, `GetReferenceWithChildren` | Native saves reference `Template.Id`; generated geometry must have a durable registered template. |
| `KSA/PartInstance.cs`, `CollectMissingParts` | Generated IDs must exist before native saved-vessel validation. |
| `KSA/PartTemplate.cs`, `OnDataLoad` | Registers native generated templates and initializes their component references. |
| `KSA/CustomMassTemplate.cs`, `InertiaTemplate.cs` | Native durable principal inertia and mass. |
| `KSA/ConvexHullColliderTemplate.cs`, `MeshColliderTemplate.cs` | Lazy hull cooking; the first host primitive supplies hull positions. The exporter therefore provides a separate combined collision mesh. |
| `KSA/Vehicle.cs`, `Split`, `CreateVehicle`, `AdoptSeparatedVehicle`, `UpdateAfterPartTreeModification` | Native separation, adoption, collision links and mass refresh; checked against the stock debris route. |
| `KSA/PhysicsStates.cs`, `ReadOnlyPhysicsStates.cs` | CCI state extraction and rotating-frame conversion. |
| `KSA/VehicleReferenceFrameEx.cs` | Native CCI-to-CCF position/velocity conversion. |
| `KSA/FxDeformation.cs` | Live dent-state guard. |
| `KSA/Universe.cs`, `DestroyVehicle` | Final source removal without native failure/debris/explosion behavior. |
| `KSA/Mod.cs`, `ModLibrary.cs`, `FileReference.cs`, `XmlLoader.cs` | Early generated asset registration and later native loader/binder lifecycle. |

`tests/SlicingPhysicsChecks.cs` verifies principal-axis reconstruction, centroid transforms, positive-inertia rejection, complete parallel-axis products, boost-invariant relative motion, inherited spin, balanced linear momentum with shifted geometric COM, and bounded opening energy. New managed fixtures cover collision ownership/clearance, isolation drain ordering, stale-nozzle quiescence, deterministic bounded inert fallback and the production worker fallback path. `tools/check_fragment_templates.py` also audits, installs and JITs the exact current KSA module caller hooks without invoking physics. Native GPU, collision, structural mutation and save restoration require the documented in-game acceptance pass; they cannot be executed on the current macOS ARM host.

## Intentional manual-contact trigger (2026-10-24)

A controlled kitten's actual overlay SWING press receives a monotonically increasing gesture ID. Main-view rendering only publishes blade endpoints, displayed drag offset, gesture identity and player time. The settled handoff runs discovery and surface confirmation only with a fully deployed lit blade, intentional changing drag offset, nontrivial physical movement, supported simulation speed and fresh main-view pose. Fresh rendered motion is sampled even when that joined handoff applied no physics step, so a fast out-and-back stroke cannot disappear between solver steps. Confirmed contacts wait in a bounded buffer for an advancing physics handoff, with their original authorization and source evidence revalidated before cut admission. Other kittens never enter this structural path.

Each candidate stores previous blade endpoints already transformed into that target's own local coordinates using the corresponding owner/target transforms. The next sample uses the new transforms; this handles common translation, target rotation and nonuniform scale without comparing unrelated body frames. A ribbon joins those samples. Triangle/triangle confirmation includes two-sided, coplanar, edge/vertex and degenerate axial-thrust cases. Nonplanar ribbons subdivide up to 16 times with a conservative 2.5 mm world-space planarization tolerance after scale conversion. This bounds the sampled ribbon approximation, not the unknown curved physical trajectory between rendered samples; highly curved/fast motion can still be missed. Relative hilt displacement above 1 m, endpoint displacement above the greater of 1 m or 1.5× physical blade length, blade-length changes, stale samples over 0.15 s and lifecycle changes discard history.

Discovery yields after individual scene/part/module operations and is capped at 64 units per update. Target-frame reads and retained candidates are capped at 128. Admission prefers meshes potentially near the blade over separated meshes; unknown or multi-primitive bounds remain eligible, and ties rotate. Native single-primitive bounds can reject a sweep, but bounds never accept hits. Primitive reads are capped at 256. Triangle work is capped at 4,096 with a 0.5 ms soft clock checked between units/blocks. A saturated query keeps its exact endpoint ribbon, mesh references and triangle cursor, expiring after 0.35 s; the maximum gap between endpoint samples stays 0.15 s. This lets a cold 40,000-triangle mesh finish ten bounded scan handoffs at 30 Hz. Source/module ownership, source and visual-subpart transforms, geometry revisions and vertex-array identity are revalidated before resuming or accepting. Bounded discovery/cache/history can still miss very short contacts before a candidate is sampled or while saturated; no global earliest-contact ordering or universal frame-time guarantee is claimed. The existing cosmetic probe is skipped during held manual gestures, so its older whole-scene refresh is not an extra manual query.

The probe compares sweep-time fractions, then along-blade position, within each frozen mesh query. It retains the best confirmed triangle across budget yields; a complete scan finds the earliest contact on the approximated ribbon. Dense queries publish the best confirmed point after two further updates, 50 ms of refinement, or once the query is 100 ms old. The longer scan expiry does not delay an already-confirmed hit. An expired or invalidated query cannot publish. This keeps saturated meshes usable without increasing the per-update work caps; unread triangles and other candidate meshes can still contain an earlier contact.

The accepted contact consumes the gesture before policy validation, preventing retry floods and recursive cutting of its newly spawned fragments. Targeting validates the actual ribbon coordinate against physical blade length without assisted reach grace. Completion or refusal applies the existing 0.25-second rearm cooldown. The manual trigger reuses the ordinary capture, copied-data worker, upload and quiet native transaction. Its hit is transformed into the original vehicle assembly frame. Blade direction and contact motion are transformed as vectors before their cross product determines the cut-plane normal, preserving scaled and reflected subpart frames. A stationary pivot uses the moving endpoint to define the sweep plane; a collinear thrust uses a deterministic perpendicular to the blade. Capture carries only the immutable contact point/normal into the worker. The preferred plane, four cardinal tilts of 0.06 radians and four diagonal tilts of 0.18 radians all pass through the contact point, subject to the clipper's existing microscopic seam adjustment. Manual cuts use planar clipping and refuse if these anchored candidates fail; assisted cuts retain their faceted/random-section and authored-subpart fallbacks. Shared isolation rebases the accepted target only after successful native separation; the swap requires another settled update and the existing `SliceSourceCapture.IsCurrent` checks. No assisted auto-aim animation is set.

Ordinary release freezes the last unconsumed held pose and lets already-authorized intervals finish within their existing expiry, including in serial mode. Recovery poses cannot create new contacts, and serial mode accepts at most one released attempt. A new gesture or explicit hide/focus loss, stow, disable, owner/world change, unavailable pose, unsupported speed or Force conflict discards pending authorization. Gesture resets discard all endpoint history, frozen queries and held contacts while retaining discovered sources and immutable cache-copy progress. Owner/world changes fully reset discovery. Ordinary no-render handoffs retain bounded evidence and advance accepted preparation without native isolation/swap until a valid step/fresh pose arrives. Resource maintenance sees both manual and assisted work as busy. Failure/cancellation after isolation retains the already separated empty source under the existing quiet-cut contract. Native performance, visual contact coverage and physical outcomes remain acceptance work.
