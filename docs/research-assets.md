# Hilt asset pipeline

The shipping hilt is original procedural geometry and original generated texture
data. No third-party model, texture or logo was sampled. Its narrow ribbed grip,
metal splines, silver shroud, brass trim and cyan activation switch form a compact
science-fiction tool, with no copied film-prop design.

## Shipping contract

| Property | Value |
|---|---|
| GLB | `Assets/Models/Purr2D2_Hilt.glb` |
| glTF node / mesh | `Purr2D2_Hilt` / `Purr2D2_Hilt` |
| Material | `Purr2D2_Hilt_Material`, one primitive and one atlas |
| Coordinates | Meters; emitter `(0,0,0)`; blade local `+Z` |
| Hilt bounds | Body `z=-0.22` to `0`; nominal grip center `z=-0.12` |
| Packed channels | ORM: red AO, green roughness, blue metallic |
| PNGs | `Purr2D2_Hilt_Diffuse.png`, `_Normal.png`, `_ORM.png` in `Assets/Textures` |
| Vertex data | Baked positions, normals, nonzero UV0 and tangent basis |
| Preview | `art/purr2d2-hilt-preview.png`, native CPU material preview |

The normal map is a mild original directional brush texture. Atlas bands have
padded UV ranges to avoid neighboring swatch bleed. Textures are 1024 square.
Mesh surfaces intentionally intersect inside the prop where rings cover the grip;
this is a render prop, not a solid CAD volume or collider. The runtime defines
the separate saber sweep.

`tools/create_hilt_portable.py` is the exact source for the committed mesh,
textures, preview and metadata. Run it with Python 3 and Pillow. GLB construction
uses only the standard library; a small CPU rasterizer supplies the review image.

`tools/create_hilt.py` optionally imports the shipping GLB into Blender for further
manual modeling. It creates a new scene, packs its images, writes only that
scene's dependencies to `art/purr2d2-hilt.blend`, and restores the previous active
scene. The portable generator remains the editable geometry/texture source. The
Blender script does not export over the verified shipping GLB.

## Blender availability and verification limits

The connected Blender MCP status request hung twice and was interrupted without
any scene-changing operation. Background Blender 5.2.2 exited with signal 11
before Python, inside Metal GPU device detection, even with factory startup.
Only Metal is supported by the installed build. Launch Services also failed.
Therefore no `.blend` or Blender render is claimed in this checkout. Existing
user scenes were not modified. The GLB, PNGs and CPU preview are actual generated
artifacts.

The preview was visually inspected for silhouette, exposed material bands,
switch placement, rails and aperture. An overlapping emitter cap was corrected
to an annular lip to remove coplanar color flicker. The image is a CPU
approximation, not evidence of KSA rendering. Native material loading, lighting,
paw scale and clipping still require the game checklist. Metadata records the
generator's actual counts and bounds.

Final offline inspection reports 9,728 triangles, 7,558 vertices, no zero-area
geometry or UV triangles, normal length error below 9e-8 and tangent/normal dot
product below 7e-8. `tools/validate.py` passes source assets and metadata. The
optional Blender script is syntax checked but has not run successfully here.

## Reference evidence

The local KSA coffer article
`references/mods/ksarmory/features/blender-model-pipeline-and-checks.md` documents
the primitive-to-palette-to-GLB pattern, matching mesh/node names, baked transforms,
export without automatic Y-up conversion, and offline UV checks. No example code
was copied. Its KSACars companion
`references/mods/ksacars/features/car-part-assets-glb-pbr.md` documents native
PbrMaterial slots and mod-relative file paths.

Verified locally against the decompiled game:

- `KSA/PbrMaterialReference.cs`: native Diffuse, Normal, AoRoughMetal and optional
  Emissive material slots.
- `RenderCore.Gltf/GltfUtils.cs`: NORMAL, TANGENT and TEXCOORD_0 vertex semantics.
- `research-rendering.md` records the registered GltfFile and
  StaticMeshRenderable runtime setup.

The supplied Flexo source was inspected read-only:
`src/ksa/assetsXmlSerializer.ts` emits Diffuse, Normal, AoRoughMetal and optional
Emissive in native order; `src/ksa/modExport.ts` packs material channels and
separates opaque glow from glass. A native emissive map could illuminate the
cyan swatch later. The blade uses the runtime glow shader; the shipping hilt
supplies three maps and does not claim an emissive map.

## Further art work

Once Blender is available, run the importer/studio script, inspect at kitten
scale and optionally sculpt an original feline grip medallion. Keep emitter
origin and grip center stable. Re-export only after checking the same axes,
naming, atlas and triangle contracts, preserving UV area and explicit normals.
Judge lighting and burn effects inside KSA because external previews cannot
reproduce the native renderer.
