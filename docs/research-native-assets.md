# Native geometry feasibility and practice targets

## What was actually tested

The supplied `ksa-game-assemblies/current/Content` snapshot contains XML and default
vehicle descriptions, but no GLB files and no `Core/Meshes` directory. Real native
geometry was found in the user-supplied Flexo reference's private asset tree:

```text
/Users/asherwin/repos/meow-sci/flexo-private-assets/assets/Meshes/
```

That tree's `version.json` identifies **2026.10.10.5554**. Its structural, fairing,
electrical and FuelTankB asset XML and GameData XML were byte-compared with the supplied
**2026.10.19.5638** snapshot. All compared files are identical. This validates the
current template-to-mesh mapping; it does not prove that the missing 5638 GLB bytes
are unchanged. No proprietary mesh or texture is packaged with this mod.

The structural atlas used has SHA-256
`efb7cd5ea08469e88fa3c6b5be08c7f284182b1ddb60d2eeff05ba12582c381b`.
Its XML has SHA-256
`de2326720e8e78ac0d088c1af6bcddf7f8073fbb1c4b717ec7694fa6138f661e`.

A temporary .NET harness links the actual repository `CutGeometry.cs` and
`PlaneSlicer.cs`. A Python extractor reads native glTF POSITION, NORMAL, TEXCOORD_0
and index accessors, retaining the native mesh-array naming behavior and ignoring
editor `_VM` meshes. For assembled parts, it applies current XML subpart position,
scale and the exact `QuaternionEx.CreateFromXyzRadians` formula. Normals use inverse
scale plus rotation; negative scale reverses winding. Zero source normals remain zero
for the slicer's documented geometric-normal fallback.

Both the initial single-solid path and the current production `TrySliceCompound`
path were exercised. The latter preserves each authored PartModel group, validates
its closed solids, clips each solid independently and groups output into two rigid
halves. Intersections between independently authored rods remain additive compound
geometry; this is not a Boolean union of the artwork.

Each dataset was sliced through its bounds center with X, Y, Z and normalized
`(1, .37, .23)` normals. These are geometry tests, not a running game's mesh/collider,
save/load or native vehicle-replacement acceptance. Extracted proprietary data,
temporary scripts and full machine results stay in ignored `artifacts/native-geometry-validation/`.

## Native candidate findings

These are the original inert-only baseline findings. Current production slicing adds panel backing, functional-part support, occupied-vessel support and repeated generated-fragment cuts; see [current slicing behavior](research-slicing.md) and [acceptance](ACCEPTANCE.md). The eligibility recommendations below describe that earlier baseline.

The important distinction is between a closed individual subpart mesh and an assembled
game Part. Passing one decorative subpart does not establish that the complete Part is
a single suitable solid or that it has supported modules and positive inert mass.

| Native Part template | Render triangles | Center-plane results | Interpretation |
| --- | ---: | --- | --- |
| `CoreFairingA_Prefab_AeroshroudBA` | 436 | 4/4 pass, two pieces each | Best native candidate. Static mesh, authored inert mass and native colliders. |
| `CoreFairingA_Prefab_AeroshroudBB` | 436 | 4/4 pass, two pieces each | Best native candidate; different skin variant of the same class. |
| `CoreFairingA_Prefab_AeroshroudCA` | 494 | X/Z/oblique pass; Y rejected | Current Y output fails the closed-manifold audit; preserve source. |
| `CoreFairingA_Prefab_AeroshroudCB` / `CC` | 814 each | X/Z/oblique pass; Y rejected | Same output-manifold limitation. |
| `CoreFairingA_Prefab_AeroshroudAA` / `AB` | 396 each | 0/4 | Source is not a closed consistently oriented 2-manifold. |
| `CoreFairingA_Prefab_NoseconeA` / `B` / `D` | 960 / 1,344 / 936 | 0/4 | Assembled source fails closed-manifold audit. |
| `CoreFairingA_Prefab_SRBForwardSkirtA` / `B` | 1,922 / 2,554 | 0/4 | Assembled source fails closed-manifold audit. |
| `CoreElectricalA_Prefab_BayFuelcellSmall` | 4,694 | 0/4 | Static positive-mass candidate from XML; render assembly is not closed. |
| `CoreStructuralA_Prefab_StrutA` / `B` / `C` | 988 / 5,208 / 2,928 | Current compound path 4/4, two rigid halves each | Geometry passes with authored grouping; no authored inert mass. |
| Other 12 `CoreStructuralA` prefabs | 336–8,610 | 0/4 | At least one authored source group fails closed-manifold audit. |

The native AeroshroudBA/BB mesh IDs are respectively
`CoreFairingA_Subpart_AeroshroudBSkinA` and `CoreFairingA_Subpart_AeroshroudBSkinB`.
Their PartGameData declares `HollowOpenTruncatedConeMass`, Aluminum 2014, 6 mm wall
thickness, 180° sweep, base radius .320 m and top radius .150 m. BA's authored height
is 1.087 m; BB's is 1.389 m. Each has one static PartModel/MeshView subpart and root
colliders. There are no resource, actuation, crew, glass or internal rendering modules
in those definitions. This is compatible with the static inert-module whitelist,
subject to the runtime checks for crew, live physics bubble, topology and scaling.

Across all current XML files, structural StrutA/B/C/D, engine plates and surface mounts
have no authored mass contribution, including their SubPart templates and merged
PartGameData/SubPartGameData. `InertMass.CreateComponents` only creates mass from
authored `AsmbMassTemplate`s or `IInertMass` modules (tank, parachute, solid grain);
it does not infer mass from the render mesh. RadialDecouplerLargeA has two explicit
cuboid masses totaling 138 kg and a Decoupler module, which must be inactive for the
current runtime guard; its geometry fails independently. These structural templates
are therefore poor first physics-replacement tests despite some compound geometry passes.

Many individual native structural meshes pass all four geometry planes: FootA/B,
LugA/B/C, StrutA/B/C, TrussStrutA, TrussBarA, TrussCrossbarA, TrussBracingStrutA,
DriveHousingB and HingeInnerB. Those names carry the `CoreStructuralA_Subpart_`
prefix. They are not independently proven spawnable, positive-mass whole-Part targets.
The common open endcaps, covers, tubes and several mounting meshes fail the source
manifold audit as expected. The initial assembled StrutA single-solid path rejected
three planes for contour crossings and produced 41 independent pieces for its oblique
plane. The production compound path now preserves those independent solids' original
rigidity and passes all four planes with two halves, as do StrutB and StrutC.

The larger aeroshrouds initially failed Y cuts at a missing shared cap boundary vertex.
After contour-level canonicalization, their Y outputs still fail the mandatory
closed-manifold audit. Those exact fixtures remain available to the slicer owner;
rejection leaves the game source unchanged. No closure or volume guard was weakened.

## Practical game acceptance candidates

None of the supplied `defaultvehicles/{Rocket,Gemini7,Banjo,Hunter,Polaris}/vehicle.xml`
files references AeroshroudBA/BB or the structural test prefabs. Rocket's NoseconeC
is a fuel-tank part with a Tank module, so it is not an inert replacement candidate.
Use a purpose-built uncrewed target craft rather than treating the stock rocket as
an eligibility test.

First test the two original practice parts below, then an isolated native AeroshroudBA
or BB as an uncrewed root or simple leaf. Place it within the EVA kitten's active
physics bubble. Avoid reinforced crosslinks, active modules, tanks, crew seats and
complex multi-parent attachment graphs. Sweep near its center with a plane that fully
crosses the small shroud. Passing requires two closed rendered fragments, cut-face
cooling, finite positive masses, plausible separation, and successful save/reload.

## Original native practice targets

`Assets/PracticeTargets.xml` and `Assets/Models/Purr2D2_Practice.glb` provide two
original, editor-placeable native parts under the Structural tag:

| Part template | Display name | Geometry | Mass | Collision |
| --- | --- | --- | ---: | --- |
| `Purr2D2_PracticeBillet` | Purr2D2 Practice Billet | Closed box, 1.2 × .36 × .36 m, 12 triangles | 25 kg | Matching native box |
| `Purr2D2_PracticeTube` | Purr2D2 Practice Hollow Tube | Closed 32-sided annular solid, X-axis length 1.2 m, radii .22/.15 m, 256 triangles | 8 kg | Conservative solid cylinder envelope |

The tube includes outer walls, inner walls and annular endcaps; its bore stays visually
open and appears as a hole in transverse caps. Its conservative collider fills that
bore, consistent with the approved convex-envelope policy for concave fragment physics.
This intentionally tests visual slicing and cap holes rather than hollow collision.
CustomMass uses finite positive principal specific inertia for the authored tube.

Each Part has one opaque PartModel and MeshView subpart, root inert mass, root collider
and two native connectors. The shared PBR material is the mod's original hilt material;
no Core model or texture is copied. The native loader must include
`Assets/PracticeTargets.xml` in `mod.toml`. The generator is reproducible:

```sh
python3 tools/create_practice_targets.py
```

The exported GLB itself was re-extracted and fed to the production PlaneSlicer.
Both targets pass all four representative center planes and produce two pieces.
The tube's transverse and oblique cuts each report one cap hole; longitudinal cuts
produce the expected open half-tube shape with closed wall cross-sections. These
results establish geometry readiness, not native asset startup or in-game behavior.

`art/slicing-preview.png` renders the actual production compound output for the original
tube's oblique cut. It separates the two halves, shades existing surfaces as metal and
new cap triangles orange, and visibly retains the bore. Its label states “Geometry
validation preview — not in-game.” The exported output contains 3,016 triangles,
including 1,328 cap triangles; layout translation and display colors do not alter the
validated mesh geometry.

## Source points

- Current `MeshAtlasFileReference.DoLoad` indexes `gltf.meshes[]` by `Name`; it does
  not use node transforms. `MeshReference.Load` imports normals and UVs with host indices.
- Current `PartTemplate.ApplyGameData` appends authored inert masses and component
  templates; ignoring GameData would give incorrect eligibility conclusions.
- Current `InertMass.ComputeTotalAsmb` and `CreateComponents` define actual mass sources.
- Current `TransformReference.RotationValue` calls `QuaternionEx.CreateFromXyzRadians`.
- Flexo `src/ksa/{catalog,assetsXmlSerializer,exportGlb,partXmlSerializer}.ts` documents
  native mesh naming, mandatory PBR slots and native geometry/GameData component forms.
- The core inert-module whitelist permits InertMass, ColliderModule, MeshViewModule and
  PartModelModule, plus automatic wetness/temperature effects, empty deformation state
  and an inactive Decoupler under explicit guards. Crew/bubble/topology checks remain.

No native game process was available for startup, rigid-body replacement, texture
appearance, fragment save/load or collision acceptance. Keep those checks explicit
before describing all native vessel art as generally sliceable.
