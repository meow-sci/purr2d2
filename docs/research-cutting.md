# Lightsaber contact, separation and molten edges

Verified against the local `ksa-game-assemblies` snapshot at commit
`edda84495fcdc5592c80e1e6442573cd3419354a`, current build `2026.10.19.5638`.
The coffer skill is pinned to `2026.10.10.5554`; names below were checked in current
decompiled sources rather than assumed from the older articles. This is source and
compile verification. Live KSA behavior still needs the acceptance checks below.

The subsequent arbitrary-plane implementation is documented in
[research-slicing.md](research-slicing.md). The native boundary modes below remain
available independently; their topology limits do not describe the new clipper.

## What the native boundary modes do

`SaberCutter` checks the actual finite blade and its relative movement against retained
CPU view-mesh triangles on nearby **other** uncrewed vehicles under the same celestial
parent, including existing debris. Surface
contact produces local-space hot scar ribbons and accumulates heat once per full part
per applied simulation step. Sustained contact severs a native full-part connection
when that connection is the sole crossing of the chosen part subtree. KSA creates a
real separated vehicle with its own native colliders, mass, resources and motion.

The optional **Fragment struck part** mode goes further: it invokes the game's native
blast-free `PartFailure.IsolateAndDestroy` primitive. That isolates and destroys the
struck full part, preserving surrounding full-part fragments and shedding up to two
eligible modeled subparts as physical debris. It is off by default, rejects any
crew-assigned target vessel, rejects external subpart/brace/parallel connection graphs
and runtime-scaled eligible debris subparts, and reserves
enough slots before proceeding. It does not inject `PartFailureEvent`, whose outer
`Apply` method explicitly spawns explosions and can invoke the whole-vehicle failure
guard. The exact internal method signature is bound once; if unavailable, the mode
reports that it is unavailable.

Both modes have limits. Full-part separation follows existing connections, which can
be some distance from the contact. Native fragmentation destroys the struck full part
and selects at most two subparts using native deterministic seeded selection; it does
not retain every mesh, necessarily choose the struck subpart, or carve a blade-shaped
hole. These native boundary modes use a surface ribbon rather than newly exposed
geometry. Arbitrary slicing generates actual cap faces through the separate
pipeline. None of those distinctions should be hidden in UI or
release notes.

## Exact native integration surface

Paths are relative to `../ksa-game-assemblies/current/decomp/`.

| Source and symbol | Verified behavior and implication |
| --- | --- |
| `KSA/Program.cs`, `PrepareFrame` | Waits nearest-orbit, orbit, vehicle and cloth workers; applies all results; polls events and applies `InputEvents`; then starts nearest-orbit work; later computes/dispatches the next step. The mod injects its settled callback just after `InputEvents.ApplyInputEvents`, before nearest-orbit dispatch. |
| `KSA/Universe.cs`, `GetLastSimStep` | The already-applied simulation step. Use its unique timestamp and delta. GUI/player time must not drive damage; paused steps do not heat anything. |
| `KSA/Vehicle.cs`, `GetMatrixAsmb2Ego(Camera)`, `PosBodyToAsmb`, `Body2Ego` | Body coordinates are COM relative; assembly coordinates are not. `GetMatrixAsmb2Ego` subtracts `CenterOfMassAsmb` before placing a vehicle. Current cutter accepts body endpoints and constructs a body-to-ego matrix. |
| `KSA/Part.cs`, `MatrixAsmb2VehicleAsmb`, `MatrixAsmb2Ego`, `ScaleTotal` | Use the exact struck subpart's full matrix. Store scars in its local assembly frame. All transform and inverse operations stay double until renderer output. |
| `KSA/Part.cs`, `RayCastEgo`, `RayCastEgoSubPart` | Stock picking raycasts subpart view meshes; returned local hit and normal belong to `closestSubPart`, not the top-level part. The stock implementation uses only the first `MeshViewModule` and first primitive. It is an infinite ray, not swept finite-blade collision. |
| `KSA/MeshReference.cs`, `PositionsCompare`, `HostPrimitives`, `PrimitiveMaterialIds` | `PositionsCompare[primitive]` retains a flattened double-precision triangle list from each indexed CPU primitive. The cutter reads all primitives and all view modules, without changing shared assets. CPU normals, positions, UVs and indices are retained in `HostPrimitives`. |
| `KSA/Vehicle.cs`, `Split(Connection, IConnector, double, out PoseChange, string?)` | Splits the selected full-part tree, disconnects a connection, partitions symmetry, rebuilds both configurations, creates/registers/adopts the separated craft in the existing bubble, inherits linear and rotational motion, recomputes flight plans, transfers emitters and staging. An existing physics bubble is required. Zero impulse avoids an artificial explosive launch. |
| `KSA/PartFailure.cs`, `TryFindSoleCrossing` | Native shedding's safe graph condition: exactly one crossing, with endpoints equal to the subtree root and its tree parent. The cutter mirrors that condition and rejects subpart endpoint crossings. A single `TreeParent` relation alone does not prove that braces or extra links are safe to sever. |
| `KSA/PartFailure.cs`, `IsolateAndDestroy` | Internal blast-free primitive: repeatedly split severable connections, `ShedSubPartDebris`, then destroy the isolated vehicle. It can abandon a partly completed isolation if native splitting fails. Initial optional mode therefore requires current native signature, no crew, no custom subpart connections and sufficient fragment slots. |
| `KSA/PartFailure.cs`, `CollectSubPartDebris` | Eligible subparts have `PartModelModule`, `MeshViewModule`, `ColliderModule`, and neither `Control` nor `IVASeat`. Merely drawing a mesh is insufficient. |
| `KSA/Vehicle.cs`, `SpawnSubPartDebris` | Creates a **new** `Part` from the subpart's template. It assigns cuboid inertia from mesh extents and passed mass, registers a debris vehicle, adopts the current bubble, and inherits angular-point velocity plus native outward separation. It neither removes the source mesh nor subtracts source mass. Its stock caller destroys the source immediately afterward. Never call this alone to claim that a live subpart was detached. |
| `KSA/PartFailure.cs`, debris constants | Native bubble guard is 30 vehicles. Subpart shedding has maximum two pieces, nominal 1.5 m/s separation, and mass heuristic capped at 25% of host inert mass with a min(5 kg, host mass) floor. These are visual debris approximations, not exact fragment mass conservation. |
| `KSA/Part.cs`, `RemoveSubPart`; `KSA/Vehicle.cs`, `UpdateVehicleConfiguration` | Removal does remove subtree-module membership and bounds; reconfiguration rebuilds tree module/state aggregation, mass properties, collision compound, control, aero and flight-computer configuration. This is not a complete live-subpart-detachment or save-persistence transaction. |
| `KSA/Part.cs`, constructor, `GetReferenceWithChildren`; `KSA/PartTree.cs`, `Deserialize` | Template subparts are reconstructed first and serialized subparts/states are matched by position. Omitting a runtime subpart from the save is not a durable tombstone and can restore or misassociate geometry/state. Arbitrary live removal needs explicit save migration, identity and tombstone design. |

## Contact algorithm and bounds

`CutGeometry.cs` is independent of game assemblies and is linked directly into the
console test project. It implements double-precision segment-to-triangle distance,
including endpoints, coplanar edges and stationary contact. Relative motion uses the
previous and current endpoints in each target subpart's own frame. This naturally
includes target translation/rotation and avoids carrying stale camera-relative
positions across an origin shift. Samples are spaced by roughly the 25 mm blade
radius. A conservative radius expansion covers the gap between samples for the
linear endpoint sweep; it can report a contact slightly outside the nominal radius.
It is not exact continuous collision of curved blade/target motion between frames.

Immutable mesh triangles are cached in a local-space BVH. Broadphase query boxes
include the swept endpoints and a conservative inverse-transform norm; narrowphase
triangles are transformed to the wielder's body frame so nonuniform display scale
does not silently change the physical blade radius. Geometry remains double precision.
World-space floats, a cursor ray and collider-only picking are not used for damage.

Current explicit limits: 500,000 triangles per cached mesh, 4,000,000 cached triangles
in total, 8,192 cached meshes,
32,768 candidate parts/subparts per step and 100,000 candidate triangle tests per step.
Part references are deduplicated before scanning; history membership uses a set so
larger scans do not incur a quadratic cleanup pass. Budget warnings append to the
contact/structural-cut status rather than hiding the reason a hot part cannot be cut.
Candidates sort by distance and then stable runtime instance identity. A mesh or
step exceeding a budget is skipped/reported; skipped geometry never receives inferred
damage. A relative endpoint jump over 0.75 m skips contact for that subpart for the
entire step and refreshes its baseline, preventing teleports from slicing a long path. Steps over
0.15 s do not accumulate heat. Extremely fast swings or warp therefore intentionally
lose cut fidelity. A later fixed-rate contact sampler can improve that tradeoff.

Heat is per full part, once per consumed step, with 50% cooldown rate when contact
ends. The threshold is configurable. The cutter tries one structural cut per heated
part with a one-second retry interval. Scars are rate limited and individual local
strokes are length limited; the renderer separately bounds retained scar count.

## Integration contract

```csharp
var cutter = new Purr2D2.Cutting.SaberCutter();
cutter.Scar = renderer.AddScar;
// Only in the settled pre-dispatch callback; caller deduplicates applied step time.
cutter.Update(kitten, baseBody, tipBody, appliedStep.DeltaTime, playerSeconds);
```

`baseBody` and `tipBody` are fresh emitter endpoints in the EVA kitten's COM-relative
BODY coordinates, with deployment already applied. Main runtime must stop/reset the
cutter when the saber is off, the owner changes, loading replaces the world, or the
pose becomes unavailable. Surface heat is transient; ordinary native vessel changes
are serialized by KSA. Scar visuals are transient in this initial implementation.

`Scar(Part, startLocal, endLocal, normalLocal, playerSeconds)` stores the exact struck
subpart. `part.Tree.OwningVehicle` resolves its current owner after a full-part split;
the original `Part` object persists and no world transform should be frozen. Optional
native subpart debris is a new object: the cutter re-emits the immediate hit only on
a newly spawned matching template, when that struck template was actually selected.
It cannot transfer every historical scar to every clone without richer provenance.

The simple main hook at `InputEvents` needs no extra wait because nearest-orbit work
has not restarted. A hook at `GetJobSimStep` instead must explicitly join
`JobSystems.NearestOrbitAndPerformanceWorker` before changing flight plans; see
`../unscience/ksa-abstractions.lib/PhysicsFrameHook.cs`. Never restructure trees or
dispose collision shapes from StarMap GUI callbacks while vehicle workers run.

## Risks addressed by the arbitrary clipping pipeline

The CPU art data makes plane clipping possible. The implemented geometry and native
adapter route is described in `research-slicing.md`; its design and live acceptance
must account for all of these concrete issues together:

1. Validate closed manifold solids and identify connected components. KSA art includes
   glass, shells, multiple primitive materials, open seams and zero-volume surfaces;
   a render mesh is not automatically a physical solid.
2. Clip every triangle against a finite kerf or chosen plane, interpolate normals/UVs,
   retain primitive/material partitions, weld intersection contours with scale-aware
   tolerances, and triangulate caps with holes. A triangle fan fails on concave or
   nested contours. Reject ambiguous/nonmanifold results atomically.
3. Build unique owned CPU/GPU meshes and view geometry per damaged instance.
   `PartModel.Get` caches by template model id; mutating a shared `MeshReference` or
   shared `PartModel.Template` would cut every instance of that asset. Clone identities
   and create explicit ownership/deferred GPU disposal. `../unscience/pebbles.lib/Assets/ClutterGeometry.cs`
   demonstrates copying `MeshAsset` streams and the private-setter `HostPrimitives`/
   `PrimitiveMaterialIds` assignment, but a dynamic cut also needs render registration
   and all `PositionsCompare`, bounds and device representations rebuilt consistently.
4. Generate new physical shapes. `KSA/ConvexHullColliderTemplate.cs` supports convex
   hulls from CPU mesh points, with valid nondegenerate volume and a shape centre offset.
   One convex hull fills concavities. Concave fragments need bounded convex decomposition
   into a compound, or preauthored hull pieces; a static triangle collision mesh is not
   a drop-in replacement for an arbitrary moving vessel's rigid-body collision shape.
5. Integrate volume, mass, COM and full inertia for each closed fragment. Partition
   resources, tanks, wiring, engine modules, control and crew according to authored
   structural semantics. Splitting a tank's art does not partition its substance store.
   Preserve momentum and a coherent body/assembly origin through COM changes.
6. Persist fragment topology, mesh recipes, cap/material identities, source asset version,
   resource allocation and stable instance references. Make failure rollback atomic.
7. Give exposed cap faces their own charred PBR surface and HDR emissive cooling material;
   place seam geometry on each physical fragment, with inward/outward normals and a
   finite kerf. A projected overlay on an uncut shell cannot reveal a cross-section.

Preauthored cuttable panels, trusses and hull sections remain useful reference assets
alongside the arbitrary clipper and native-boundary cutter. Blender MCP
can create watertight fragment meshes, interior wall thickness and cap surfaces,
individual simplified convex collision meshes, seam coordinates and dedicated
char/normal/roughness/emission textures. Author them as full parts connected along
chosen seams when physical preservation matters, with each fragment's mass and
collider explicit. The actual native `Split` then remains responsible for ownership,
resources and rigid-body adoption, while cap geometry is revealed on both sides.
That provides controllable physics without depending on open stock meshes and creates
a reference corpus for validating the arbitrary clipper against known solids.

The artist should deliver reproducible `.blend` sources and GLB export scripts; use
metres, apply transforms, verify outward winding, preserve distinct subpart mesh names,
and export collision-only hull meshes independently of detailed render geometry.
Keep source meshes/atlases immutable. Bake cap UVs with a shared seam coordinate and
store cap anchors relative to each fragment, so detached pieces retain the same scar.

## Community code worth reusing conceptually

- `../unscience/graffiti.lib/DecalPicker.cs`: native precise mesh hits, subpart-local
  identity and normals; `DecalAnchors.cs` composes transforms/inverses in double and
  explains why cached ego coordinates drift.
- `../unscience/graffiti.lib/DecalRenderer.cs`, `DecalShaders.cs`, `GraffitiPatches.cs`:
  resolved-depth projected decals after `RenderTarget.ResolveAttachments`, including
  reverse-Z reconstruction and per-frame-in-flight descriptor ownership. A projected
  hot scorch pass is a useful later improvement on ribbon strips. It cannot by itself
  create cap geometry and can bleed onto nearby receivers unless clipping is constrained.
- `../unscience/pebbles.lib/Assets/ClutterGeometry.cs`: CPU mesh copy, inverse-transpose
  normals, UV preservation, uint indices and owned-mesh cleanup.
- `../unscience/sphinx.lib/CollisionGeometry.cs`: conservative collider validation,
  finite coordinates, degenerate triangles and explicit geometry budgets.
- `../unscience/garrys-torch.lib/WeldEngine.cs`, `WeldCollisionPatches.cs`: structural
  changes through the settled mutation seam and physical ownership/collision care.

## Required live acceptance checks

Use a disposable save with an EVA kitten beside an uncrewed vessel. Confirm a blade
barely missing a hull never heats it; contact, stationary contact and a fast swing
produce scars on the contacted surface; pause and warp never multiply damage; moving
and rotating targets retain local scars. Cut a single tree connection, verify both
fragments retain sensible mass, motion, colliders and resources, then save/reload.
Repeat with stock symmetry, reinforced graphs, controlled fragments, origin/bubble
changes, edited craft and high debris counts. Verify feedback explains rejected cuts.

For the opt-in native fragmentation mode, verify that no explosion is spawned,
surrounding connected trees survive, the isolated source mesh is gone, eligible
subparts become physical debris without visible duplicates, and a crew-assigned
vessel is refused. Native stock fragmentation itself is allowed to discard art and
use approximate debris mass; do not present this mode as exact mesh slicing.

Inspect ribbon depth, normals and cooling from both sides in HDR/night/day. In the
arbitrary mode, additionally inspect generated cap faces, bore holes, charred
material, emissive cooling and the collision approximation in an actual KSA session.
