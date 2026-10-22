# Lightsaber rendering and art contract

Verified against the local `ksa-game-assemblies/current` snapshot, build 2026.10.19.5638. The coffer articles are useful starting points but target the older 5554 build; the implementation uses the current members below.

## Implemented route

The authored hilt uses KSA's native GLTF/PBR renderer. `GltfPbrSystem.CreateAsset` imports the mesh, `GpuMaterialSystem` imports the PNG set, and `StaticMeshRenderable` registers the main/prepass/shadow buckets. The renderer replaces the hilt GLTF's material slots with the explicit XML PBR material before constructing its renderable. KSA owns loaded mesh/material assets; this mod only unregisters its own renderable's bucket handles.

The hilt is submitted inside the pose processor's final-skeleton callback. `Program.RenderGame` and `RenderViewport` call `SuperMeshRenderSystem.ClearBuckets` **before** late EVA character animation. Submitting in `ClearBuckets` is valid for static props but would use the previous wrist pose. The pose callback gives the current viewport's emitter-to-Ego matrix after animation and before the buckets are consumed, keeping hand and hilt together.

The blade is a camera-facing capsule ribbon with a bright white core and a colored analytic halo. A four-vertex, bufferless triangle strip uses `gl_VertexIndex`; a 96-byte push block contains MVP, HDR color and capsule dimensions. It uses KSA's reverse-Z depth test without depth writes and additive blending inside a `SuperMeshRenderSystem.RenderMainPass` postfix. The halo is visible without bloom; the game's bloom can add further spill. Length comes from the controller's deployment animation. There is no blade mesh asset to animate or reallocate.

This small shader provides precise control of the white core and colored halo. The current `Content/Core/Shaders/Mesh/ModelPbr.frag` calls `GetMaterialData(..., emissive)` but never adds the returned emissive value to its output, so an emissive PNG on this particular native static renderer cannot produce a glowing blade. Native **part** rendering is different and does support emissive textures: `MeshIndirect.frag`'s `ENABLE_EMISSIVE` branch adds the red-channel grayscale mask as white emission. Setting bit 7 of `PartModel.PerInstanceData.StateBitFlag` additionally selects packed `EmissiveColor`; bit 6 disables emission. Turning a temporary hand prop into a full part model would require early instance-buffer upload despite the wrist's late per-view pose.

The user's successful Flexo carbon rod is consistent with this distinction. Read-only Flexo sources at `/Users/asherwin/repos/meow-sci/flexo` export `<PartModel>` plus a composited diffuse and grayscale `<Emissive>` texture; `src/ksa/types.ts` documents independent diffuse coverage and mask strength, and pairs the result with a matching native `<Light>`. Its emissive UI does not yet expose the packed per-instance color override. This native route is a valid alternative for an editor-placeable saber/rod part; the hand-held blade currently uses a dedicated small shader to avoid early part-buffer/late skeleton timing surgery.

Two native point lights approximate illumination along the blade. The prefix on `Program.UpdateRenderingResources` submits them after `LightSystem.ClearLights` and before `WriteForwardLights`/`UpdateRenderResources`. The earlier pose's body-relative emitter is reprojected using the current kitten/camera transform, so it survives camera motion and floating-origin changes. Illumination can trail fast wrist animation by one visual frame; the blade and hilt themselves use the final current pose. A pose callback is too late to upload native lights for that same frame.

Cut scars store a `Part` reference and local-space segment/normal. Each draw resolves `part.Tree.OwningVehicle` and composes `Part.MatrixAsmb2Ego(vehicle.GetMatrixAsmb2Ego(camera))`; native splits preserve that part reference and scars move with the resulting debris. A dark surface ribbon persists for three player-time minutes; a narrower orange/white ribbon cools exponentially over seconds. A 2.5 mm normal offset avoids z-fighting. There are at most 384 scars. These are surface visuals, not reconstructed cut geometry or a depth-projected decal: deeply curved surfaces and silhouette edges need a future mesh-aware scar/decal pass.

## GPU lifetime and viewports

Call `Prepare()` outside command recording, after render targets exist. It creates no per-frame geometry, textures or descriptor sets. Pipelines are cached by color format, depth format and sample count, because crew portraits and camera windows can differ from the main view. Target resizing does not invalidate those pipelines; changing formats/sample count creates a compatible pair. No waits or uploads occur in the draw callback.

`Program.Dispose` is patched before KSA destroys viewports, SuperMesh and the Vulkan device. The mod stops recording, waits idle and destroys its pipelines/layout and unregisters the hilt. `StarMapUnload` must not attempt fresh GPU teardown after this; it occurs after KSA has destroyed the device. Shader modules belong to `ModLibrary` and are never destroyed by the mod.

## Blender export contract

| Property | Contract |
| --- | --- |
| Runtime file | `Assets/Models/Purr2D2_Hilt.glb` |
| Named mesh/object | `Purr2D2_Hilt`; object and mesh data names identical |
| Coordinates | Metres; emitter is `(0,0,0)`; blade extends along local `+Z` |
| Hilt envelope | Body approximately `Z=-0.22..0`, grip center at `Z=-0.12` |
| Export | GLB 2.0, `export_yup=false`, no compression, normals and UVs, materials exported |
| Transforms | Apply translation, rotation and scale into vertices; runtime uses the emitter matrix |
| Textures | `Purr2D2_Hilt_Diffuse.png`, `_Normal.png`, `_ORM.png` in `Assets/Textures` |
| ORM channels | R=ambient occlusion, G=roughness, B=metallic |
| Normal convention | KSA's material reader flips the green channel; validate the final appearance in game |
| Geometry | One static hilt mesh, no rig or baked scene lights; no `_VM` twin required for this standalone GLTF route |

Retain the Blender source and generation script alongside the export. Inspect/reimport the exported GLB, checking bounds, material-slot count, UVs, normals and node transforms. Asset `Path` values resolve from the mod directory, not the XML's containing directory; the XML therefore includes the `Assets/` prefix.

## Source evidence

All paths below are under the read-only reference checkout `../ksa-game-assemblies/current/` relative to the mod repository:

- `decomp/KSA/StaticMeshRenderable.cs`: constructor, `Draw(ViewHandle)`, `Dispose`.
- `decomp/KSA/GltfPbrSystem.cs`: `CreateAsset`, scene graph and material/texture import.
- `decomp/KSA/GpuMaterialSystem.cs`, `PbrMaterialReference.cs`, `Gltf2Reference.cs`: native XML/material contracts.
- `decomp/KSA/Program.cs`: `UpdateRenderingResources`, `RenderViewport`, `RenderGame`, `Dispose`, `RebuildRenderer`.
- `decomp/KSA.Rendering/RenderTarget.cs`: `Samples`, `ColorImage`, `DepthImage`, `SetupGraphicsPipeline`.
- `decomp/KSA/RenderingPresets.cs`: reverse-Z no-write depth and blend presets.
- `decomp/KSA.Rendering.Lighting/Light.cs`, `ClusteredLightSystem.cs`: point-light creation and per-viewport submissions.
- `decomp/KSA/Part.cs`, `PartTree.cs`, `Vehicle.cs`: part-local transforms, current owning vehicle, assembly/body frame conversion.
- `Content/Core/Shaders/Mesh/ModelPbr.frag`, `Content/Core/Shaders/Common/MaterialSet.glsl`: verified native emissive omission.

The local unscience references `thug-life.lib/ThugLifeQuadRenderer.cs`, `ThugLifeRenderPatches.cs` demonstrate the render seam and reverse-Z pipeline setup. `graffiti.lib/DecalRenderer.cs` and `DecalAnchors.cs` demonstrate projected decals and persistent part-local anchors; a depth-projected scar pass is the next quality step if surface ribbons prove insufficient.

## Actual generated slice surfaces

The opt-in arbitrary plane slicer has a separate native geometry path in `RuntimeFragmentAssets`, rather than using scar ribbons to disguise an unchanged part. `FragmentGlbWriter` emits a self-contained GLB atlas containing the clipped surface triangles with their interpolated normals/UVs, one mesh per material, real cap triangles, and a complete indexed collider/picking mesh. Outer groups reference the source native PBR material IDs. The cap references `Purr2D2_CutSurface`: dark metal diffuse/ORM textures and a white emissive mask, with packed orange native emission cooling during the current session. The cap remains real dark geometry after cooling. Normal/source surface shaders are unchanged.

The preparation adapter transforms each clipped piece into its own principal-inertia/COM frame, quantizes positions to the exact float32 values to be exported, rejects collapsed triangles and re-audits oriented closed manifolds and volume. Compounds retain the clipper's closed-solid ranges for this audit so overlapping authored solids do not require a Boolean union. The combined hull mesh deduplicates equal positions before export and is limited to 8192 unique points to bound native hull construction. This native convex envelope is an approximate collider for concave/hollow fragments; the visible cap holes and surface geometry remain exact clipped triangles. Active modules/crew and unsupported source geometry are refused by the native physics adapter.

### Fixed native mesh buffers

`DeviceMeshInterleaved` normally allocates global vertex/index offsets in its constructor. Its backing buffers are built once at startup; runtime constructor calls would append outside the existing buffers. Worse, `Shared.Rebuild` uses the new allocation size while copying the old allocation, so allowing its running counters to grow after startup would make ray-tracing toggles unsafe. The adapter never does that.

`Initialize(KSA.Mod)` runs during ImmediateLoad, before `Shared.Build`, and reserves one aligned 32 MiB vertex region and 16 MiB index region by advancing the native public counters once. Runtime uploads suballocate strictly inside these regions. They create a `DeviceMeshInterleaved` plain-field object through `RuntimeHelpers.GetUninitializedObject` and explicitly fill **every** instance field: host mesh, normal/UV flags, vertex stride/offset/size, index count/type/stride/offset/size. The exact all-public field schema is checked before reservation and in the host PE inspector. No native constructor runs, no runtime counter grows, no other mod's allocations are modified and no counter rollback occurs. Existing native meshes occupy their normal allocations after the reserved hole. Each upload waits for its staging submission to finish before any source part can be replaced.

The arena is intentionally fixed for one session. Capacity checks occur before source topology changes; exhausted capacity refuses the cut with an actionable save/restart message. Allocated fragment offsets are not reused while native renderables may still reference them. An exceptional partially registered native asset is marked unavailable for retry until restart, rather than unregistering shared items or treating an unbound mesh as complete.

### Persistent assets and cooling

Successful preparation writes immutable content-derived GLB/XML assets under the installed mod's `Generated/Slices` directory. XML registers one native root Part with a complete-mesh MeshView, ConvexHull collider and exact principal-axis CustomMass. Each original closed solid gets a native render SubPart containing that solid's material groups; every mesh is authored in the same principal/COM frame and the SubParts have identity transforms. This preserves the compound's closed-solid partition through repeated cuts and save/reload. There are at most 63 solid/material render groups plus the complete collider mesh per result; larger compounds are refused before source mutation. These files must remain alongside saves containing generated fragments. ImmediateLoad restores their asset bundles before native save recreation and explicitly reports a missing GLB. Persistent cache creation is limited to 512 MiB and 1024 fragment entries; reaching the limit refuses new files and never deletes old save dependencies. Startup reload currently binds the complete collider mesh into the normal shared buffers as well, although it is not drawn; this costs some duplicate GPU storage.

The production cap-emission prefix targets the exact private `PartModel.AddInstance(PerInstanceData, PerInstanceDent, IViewport, int)` submission. It changes state bits 6/7 and packed emissive color only for generated `_cap` model instances. Current-session heat is keyed by immutable result ID, so identical generated meshes share a cooling age. `ClearHeat()` makes saved fragments cold when changing/loading worlds; no mandatory wall-clock reheating is introduced. Re-slicing a cap keeps it in the cap material group rather than accidentally creating an always-emissive ordinary surface.

The generated root's mass/collider describe the complete fragment once. Its render SubParts contain no duplicate mass or collider modules, and the complete collider/picking mesh has no rendering module. Thus original visuals are replaced by the actual clipped geometry without drawing a hidden full-source duplicate.
