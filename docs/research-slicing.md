# Arbitrary slicing: implemented route and remaining acceptance work

Arbitrary **plane** slicing is feasible with KSA's retained CPU mesh geometry. The
prototype now implements the geometry operation, cap generation, physical mass
properties, generated native fragment assets and a native replacement transaction.
This is a substantial extension beyond separating an existing part connection.
The managed fixtures exercise the real clipping implementation; the runtime native
graphics/physics/save transaction still requires an actual KSA session.

The initial physical collider encloses each generated rigid half with a convex hull.
The visual cap preserves holes and recesses; the collider can fill them. This is an
explicit approximation, not an exact collision mesh or convex decomposition.

## Asynchronous preparation

The runtime copies native model attributes and source identity on the settled game thread, in bounded batches. `SliceGeometryWork` receives owned arrays, material IDs, axes and dry mass. A single below-normal-priority worker performs shell backing, candidate clipping, float32 repair, mass/inertia calculation, convex-hull reduction and GLB/XML serialization. The successful cut is retained; commit does not clip or export it again. The swing starts from the clicked surface immediately, independently of plane publication and worker readiness.

The same worker writes immutable files and loads detached CPU meshes with `MeshReference.Load(createDeviceMesh: false)`. It deserializes XML using a private, lazily cached serializer; it never runs the registry-mutating template `OnDataLoad`, native physics, or graphics calls. **The serializer must supply KSA's component element overrides.** `PartTemplate.Components` is not self-annotated, so a plain `XmlSerializer(typeof(AssetBundle))` silently discards `Collider`, `MeshView`, `PartModel` and `PartModelGlass`. That caused the reported missing-native-collider failure. `FragmentTemplateLoader` maps the exact four exported module types and rejects a missing collider, picking mesh, mass or render surface before registration. The serializer is cached to avoid generating another dynamic assembly for every cut. The regression check uses the actual KSA managed types and reproduces the plain-serializer failure. Mesh identity initialization touches only the detached mesh. These boundaries were inspected in KSA 2026.10.19.5638; the game does not provide a general thread-safe world API.

On the game thread, `RuntimeFragmentAssets.UploadJob` prepares registration incrementally with a 1.5 ms soft budget. Prepacked vertex/index data is uploaded in at most 512 KiB batches to the already-reserved arena. A zero-timeout fence poll on later frames replaces the old per-surface `Bind`/GPU wait. Registry insertion and native collider preflight happen only after the uploads complete. Preflight distinguishes missing mass, missing collider modules and missing cooked collider handles. Only interrupted registration quarantines an ID; a preflight failure after completed registration can retry without poisoning the template cache. The budget cannot interrupt an individual native call.

Collision meshes contain the hull vertices computed with a private Bepu pool on the worker. Picking and visual meshes keep their full detail. The game cooks the much smaller collision point cloud during preparation; it does not search tens of thousands of render vertices during breakup. The GLB writer also avoids allocating tiny attribute arrays for every vertex. Export permits 200,000 triangles per fragment, matching its existing per-group limit; arena, material, hull-point, shell and disk budgets still apply.

`AssistedSliceSequence` requires presentation of the final main-view swing pose and a fully ready result before consuming one commit. Ready results commit immediately on the next settled update; late results leave the game running while preparation continues. Cancellation never joins the CPU task. Already-submitted uploads and partial asset registration drain over subsequent frames without spawning anything, keeping registered IDs reusable. Source ownership, transforms, mesh/material identities and mass are rechecked before replacement.

Native world isolation/spawn/removal remains one atomic operation on the game thread. Moving it onto the worker would race the engine. Diagnostics record background preparation time, the longest preparation update and final native commit time so any remaining engine-side hitch can be measured in-game. No native FPS claim follows from the managed fixture timings.

## Geometry contract

`src/Cutting/PlaneSlicer.cs` has two entry points:

```csharp
PlaneSlicer.TrySlice(faces, plane, out result, out reason, capMaterial: -1);
PlaneSlicer.TrySliceCompound(authoredGroups, plane, out result, out reason, capMaterial: -1);
```

`SliceVertex` contains double-precision position and normal plus `U` and `V`.
`SliceFace` contains three vertices and an integer material identity. Material
identities refer to source native PBR materials; new cap triangles receive the
dedicated `capMaterial`. `SlicePlane` means `dot(Normal, position) = Offset`;
nonunit plane normals and offsets are normalized together.

The runtime gathers source model primitives into **vehicle assembly physical metres**,
applying each subpart's complete `MatrixAsmb2VehicleAsmb`, including scale, and
inverse-transpose normals. The automatic slice plane uses the same frame. No source
scale is applied again when positioning the generated fragments. The geometry core
itself is coordinate agnostic, so the game-free tests also use simple local metres.

`PlaneSliceResult` returns the effective plane, source volume, contour/hole counts
and `SlicePiece` records. Every piece has faces, volume, centroid, a COM-centred full
inertia tensor at unit density, and its positive/negative side. The six inertia fields
`XX, YY, ZZ, XY, XZ, YZ` are actual tensor entries, including the signed off-diagonals.
The physics adapter scales them by density and rotates into principal axes.

All outputs are prepared before a result is published. Rejection returns no geometry
and a reason; it never edits the source mesh or authorizes a destructive fallback.

## Single-solid algorithm

1. Check complexity, finite attributes and bounded coordinates. Weld position seams
   while preserving separate corner normals, UVs and materials. Every welded edge
   must have exactly two oppositely directed face uses. Open meshes, duplicate faces,
   collapsed triangles and inconsistent winding are rejected.
2. Choose one stable plane. If it grazes existing vertices, shift its offset by a
   deterministic microscopic amount. The result reports the actual plane. Treating
   an exactly coplanar triangle as whichever half happened to be visited first would
   produce ambiguous seam ownership.
3. Clip every triangle into both half-spaces. Interpolate UVs and normals; original
   material identities remain unchanged. An intersection is cached by the welded
   source edge, giving both adjacent triangles exactly the same seam position.
4. Reconstruct seam contours from the intersection-edge graph. Every contour vertex
   must have degree two. Reject nonadjacent contour segments that cross or touch.
   The single-solid path does not silently Boolean-union intersecting shells.
5. Project contours onto an orthonormal plane basis. Partition the plane at all
   contour-vertex heights. In each open slab, sort boundary crossings and fill
   alternating inside intervals. This constructs convex trapezoidal cap cells and
   naturally excludes holes, handles nonconvex outlines and supports several
   independent cap regions. It avoids bridging a hole with an unstable diagonal.
6. Subdivide **both** the clipped side seam edges and the cap slab boundaries at the
   same shared vertices. Subdivide each convex cell with an interior fan. A fan across
   the entire outline would fill a hole; a cell fan does not. Sharing all boundary
   subdivisions also avoids T junctions that merely look closed while failing a
   physical manifold audit.
7. Native float-authored symmetry coordinates can differ by tiny amounts. When two
   contour heights fall inside the welding tolerance, canonicalize the actual cut
   points to that shared height, as well as the height list. Without this step, a
   short edge can straddle a slab midpoint but have no corresponding slab-boundary
   vertex. The bounded adjustment is checked again for contour intersections and
   volume conservation.
8. Orient caps outward on each half, with plane-space UVs in metres and independent
   hard cap normals. Re-audit every output edge for closure and consistent winding.
   Extract connected boundary components. An inward-wound sealed cavity is attached
   to its enclosing outward shell using solid-angle containment, rather than emitted
   as negative-mass debris.
9. Integrate signed tetrahedra for volume, first moments and the full second moments.
   Use a nearby reference origin to reduce cancellation, then shift inertia to the
   centroid. Reject microscopic components or invalid inertia. Require total output
   volume to agree with source volume within the stated numerical tolerance.

The method supports a closed tube cut across its bore, a side cut producing several
separate cap regions, a nonconvex U shape producing three solids, a sealed cavity
that the cutting plane misses, and slicing generated fragment geometry again.

## Authored rigid compounds

Real KSA part art frequently comprises independent closed rods, bolts, feet and
plates that intersect. Those surfaces were authored as a rigid aggregate, not as a
single Boolean-unioned solid. Their cross-section contours can legitimately cross.
Relaxing the single-solid contour guard would generate incorrect caps.

`TrySliceCompound` therefore accepts authored groups, ideally one subpart containing
all of its model/material primitives. Each group must independently pass closed,
oriented manifold validation. Its disconnected positive shells are handled as
separate solids; inward cavity shells remain attached to their enclosing material.
Each solid is clipped against **one global effective plane**. Solids entirely on one
side remain unchanged. The operation must cut actual material somewhere; a plane
passing only through empty space between bolts is refused.

The outputs are aggregated into exactly two rigid halves, retaining the original
part's aggregate rigidity on each side. This avoids turning a detailed strut into
dozens of separate bolt vehicles solely because its render surfaces were disconnected.
Mass properties and volumes are additive. Overlapping authored solids therefore
count overlapping volume twice, just as an additive compound model does; this is
deliberately **not Boolean union**. The adapter normalizes density to preserve the
source's actual total mass. The resulting distribution assumes uniform density
within the represented material, rather than inferring the densities of visible bolts.

`SlicePiece.ClosedSolids` retains the independently validated solid face arrays in
the same concatenation order as `Faces`. The exporter can transform and quantize
those ranges to float32, then call `TryAuditCompound`. Closure is checked separately
for every solid, so authored overlap remains allowed without dropping the audit.
The single-solid `TryAudit` remains available and is also used per compound solid.
Export volume must still agree with the double-precision source result.

This improves coverage of valid closed compounds. It does not repair open plates,
invent wall thickness, seal arbitrary holes in source art or accept a failed group
because some other group was valid.

## Measured native geometry coverage

The source-only harness also exercised locally available native GLB meshes with the
authored XML transforms; extraction provenance and asset eligibility are recorded
in `research-native-assets.md`. The four tested planes were centred X, Y, Z and one
oblique orientation. These are geometry results, not an in-game acceptance run.

| Source | Compound plane checks | Observation |
| --- | --- | --- |
| `CoreStructuralA_Prefab_StrutA`, `StrutB`, `StrutC` | 4/4 each | Exactly two rigid halves. Independent closed authored solids resolve the intersecting-contour problem; the old single-solid StrutA path passed only one plane and emitted 41 disconnected pieces there. |
| `CoreFairingA_Prefab_AeroshroudBA`, `AeroshroudBB` | 4/4 each | Native closed structural candidates pass all tested planes. |
| `CoreFairingA_Prefab_AeroshroudCA`, `AeroshroudCB`, `AeroshroudCC` | 3/4 each | Exact centred Y cut remains refused by the output manifold audit. |
| Original practice billet and closed tube | 4/4 each | Tube cut retains its cap bore; source geometry is redistributable and reproducible. |

For the remaining AeroshroudCA centred-Y case, offsets of 0 and `1e-7 m` fail while
`5e-7`, `1e-6`, `5e-6`, `1e-5` and `1e-4 m` pass. Many native vertices lie exactly
on that symmetry plane. The failure is a near-grazing microscopic-sliver case, not
a general inability to cap a closed fairing. The implementation retains its current
bounded nudge and strict output audit rather than accepting cracked geometry or
silently moving a requested cut farther. A nearby free plane succeeds.

Many other native parts include open plates or nonmanifold source groups. For
example, twelve of the fifteen assembled parts in the sampled structural bundle
are refused at source closure in the compound path. This is a meaningful coverage
limit: arbitrary-plane slicing works on supported closed solid/compound art, not
every stock mesh. Source repair or deliberate wall-thickness reconstruction would
need an additional explicit geometry-authoring policy and is not silently applied.

## Native implementation route

The local current source provides the necessary low-level pieces:

- `KSA/MeshReference.cs` retains every CPU primitive's positions, normals, UVs and
  indices. The runtime uses `HostPrimitives`, rather than stock picking's first
  primitive shortcut. Source assets remain immutable.
- `KSA/PartModel.cs`, `PartModel.Get` caches by model-template ID. Generated models
  therefore need unique immutable identities. Replacing a shared source mesh would
  change every vessel using it.
- `RenderCore/DeviceMeshInterleaved` uses shared device buffers whose capacities
  are determined at native binding. The runtime exporter reserves a dedicated mesh
  arena before that build and uploads generated meshes into owned ranges afterward.
  It validates the host field/layout assumptions and budgets. Reconstructing ordinary
  device-mesh objects after the fixed buffers are built would advance native offsets
  beyond the original allocation, so that is not treated as a safe runtime loader.
- Generated GLB/XML assets preserve material partitions and use a new native cap
  material. Persistent files and template IDs must be restored before native save
  deserialization; an in-memory template alone cannot make a saved fragment reload.
- `KSA/ConvexHullColliderTemplate.cs` can create a moving convex collider from the
  generated CPU points. Its native point reader uses the first primitive, so the
  exporter supplies a compact hull mesh, independent of visual material groups and
  the full-detail picking mesh.
- `KSA/Vehicle.cs`, `CreateVehicle`, `AddToBubble`, native split/isolation, and current
  mutable physics states allow replacement craft to inherit the original rigid-body
  point velocity. The physics adapter supplies exact mass properties from the clipped
  material and handles principal-axis orientation and momentum accounting.
- Structural mutation belongs in the settled main-thread frame seam after applied
  solver results and before any next-step worker snapshots. File preparation and GPU
  capacity checks must finish before isolating or deleting the source.

The renderer, native asset arena, principal-axis conversion, lifetime management and
transaction are substantial integration risks despite a passing managed build.
They require day/night viewing, real collision interaction, repeated cuts, failure
rollback, GPU arena exhaustion and save/restart/load acceptance in KSA.

## Tests and numerical limits

`tests/PlaneSliceChecks.cs` directly exercises `PlaneSlicer.cs`, including:

- Box, oblique and vertex-grazing cuts; original UV/material retention and opposite
  cap normals; exact half volumes, centroids and inertia.
- Nonconvex L and U solids, asymmetric three-fragment cuts and disconnected solids.
- One and two cap holes, curved hollow tubes, oblique hollow cross-sections, side
  cuts with several independent cap regions and enclosed cavities missed by the plane.
- Re-slicing generated geometry and deterministic arbitrary plane orientations.
- Closed overlapping compound solids, unchanged carried solids, two rigid half
  aggregation, additive volume and independent closure provenance.
- Open, duplicated, reversed-winding, nonintersecting and near-level cases, with
  conservative refusal or verified closed output as appropriate.

Current limits are 120,000 source faces; 240,000 total generated faces; 2,048 contour
vertices per individual solid slice; 30,000 cap cells; 100,000 shared edge/level
subdivisions; 64 boundary components per single-solid output; and 2,048 source compound
solids. The exporter has its own smaller native arena and fragment complexity limits.
Welding tolerance is `max(1e-9 m, source diagonal * 1e-8)`. The plane nudge is bounded
by 64 such tolerances, and contour height canonicalization by one tolerance. Tiny
features near these limits can be refused rather than producing unreliable colliders.

This is not a full three-dimensional self-intersection solver. Manifold winding,
projected contour crossings, output closure, containment and volume checks catch
many invalid inputs, but cannot prove that every connected source surface is a
globally valid embedded solid. Closed intersecting authored components are supported
explicitly through the compound path; arbitrary self-intersecting connected art is
outside the supported contract.

## Meaning of arbitrary slicing in this prototype

The explicit swing chooses a valid cutting plane through supported part geometry.
The operation cuts across that plane with zero kerf and generates exposed faces.
It does not follow a curved blade path or remove only the finite region visited
by the blade. Contact has no role in selecting or triggering a structural cut.

Exact concave collision can be pursued without discarding this geometry work. A
useful next route is to clip each of the original part's already-convex collider
children independently and preserve them as a compound of convex clipped shapes.
That preserves the original collider approximation without filling new gaps between
children. General exact moving-mesh collision still requires bounded volumetric
convex decomposition or a suitable constrained tetrahedralization; a single hull
cannot represent the bore of the demonstrated tube.

## Stock hull and functional-part support (2026-10-22)

`SliceShellBuilder` retains valid closed solids and gives open authored panels a 4 mm backing. It welds seam positions within 10 microns, removes duplicate/degenerate faces, orients connected panels, splits sharp folds, and separates pinched vertex fans by tens of microns. Exterior normals/UV/materials survive. The ordinary manifold/volume/cap audit still gates replacement. `SliceMeshPrecision` tests the actual principal-frame float32 geometry, trying bounded edge welding through 2 microns and cancelling collapsed internal face pairs. Export must stay closed and within 0.02% volume tolerance. These checks run when preparing the explicit swing. Float-collapsed T-junctions are stitched only at existing collinear boundary vertices; open holes are never filled by this precision repair.

`NativeSlicePhysics` no longer requires inert-only modules. It groups the struck structural part with attached IVA children, validates/cooks all generated pieces, splits external connectors, empties only the isolated owner through `Vehicle.DepleteConsumables`, rebuilds native state and preserves its assembly pose across the mass/COM change. Original modules and IVA furnishing geometry disappear with the replaced source. Dry mass includes those internal parts. Neighbor resource networks are rebuilt by native Split. Native fragments receive a balanced 0.24 m/s relative opening velocity across the cut, compensated in the angular-momentum solution. No propellant plume or retained functioning tank is claimed.

Runtime limits include 120,000 prepared source triangles, 2,048 authored solid groups, 100,000 triangles and 65,536 hull points per fragment, and 2,047 persisted solid/material groups per fragment. The source vessel isolation limit is 4,096 full parts. Glass uses persisted PartModelGlass modules. Dented source geometry, deployed cloth, occupied vessels, crossing contours and unsupported native connection graphs remain explicit refusals. Some oblique capsule planes still self-intersect after panel backing.

`tools/check_stock_slices.py /path/to/Content/Core` exercises locally installed stock assets without bundling them. It tests Gemini7 tank axial/oblique planes, a medium capsule exterior axial plane, and all four stock service-module height variants through automatic plane selection, preparation, clipping, principal frames, exact export precision, fragment budgets and the actual GLB writer. Source material IDs remain distinct in the fixtures. This does not execute native resource depletion, spawning, GPU uploads or save/load.

## Explicit cursor slicing (2026-10-22)

`PartSliceTargeting` selects the clicked part through native hover picking, with a bounded triangle-ray fallback for meshes without a native pick. It never selects a nearby substitute when clicking empty space. `SliceSourceCapture` copies source attributes once; `SliceGeometryWork` tries up to nine broad axis-aligned sections in part coordinates on the worker. Each candidate uses the same clipping, principal-frame and native float32 export budgets as fragment preparation. Both the successful geometry and effective plane are retained, so commit performs no second clip or seam nudge.

`AssistedSliceSequence` separates ignition readiness, visible swing progress, final-pose presentation and a consumed commit cue. The original ordinary swing is available independently. Both use the same 0.62-second player-time motion; simulation-step size does not advance, accelerate or cancel it. Native capture, registration and mutation stay in the joined solver seam; pure geometry and detached CPU loading run on the worker. Pose directions track the clicked surface through the stroke. Geometry, target identity and crew restrictions are rechecked before replacing it; reach is checked at selection, so drifting away after a valid stroke does not cancel the pending result. Failed geometry does not trigger destruction or a fallback target. This is the only structural slicing path. Autopilot, ring coverage, contact heat and automatic contact cutting have been removed; a separate bounded probe retains cosmetic contact sparks.
