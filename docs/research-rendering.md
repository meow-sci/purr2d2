# Lightsaber rendering and art contract

Verified against the local `ksa-game-assemblies/current` snapshot, build 2026.10.19.5638. The coffer articles are useful starting points but target the older 5554 build; the implementation uses the current members below.

## Implemented route

The authored hilt uses KSA's native GLTF/PBR renderer. `GltfPbrSystem.CreateAsset` imports the mesh, `GpuMaterialSystem` imports the PNG set, and `StaticMeshRenderable` registers the main/prepass/shadow buckets. The renderer replaces the hilt GLTF's material slots with the explicit XML PBR material before constructing its renderable. KSA owns loaded mesh/material assets; this mod only unregisters its own renderable's bucket handles.

The hilt is submitted inside the pose processor's final-skeleton callback. `Program.RenderGame` and `RenderViewport` call `SuperMeshRenderSystem.ClearBuckets` **before** late EVA character animation. Submitting in `ClearBuckets` is valid for static props but would use the previous wrist pose. The pose callback gives the current viewport's emitter-to-Ego matrix after animation and before the buckets are consumed, keeping hand and hilt together.

The blade is a camera-facing capsule ribbon with a bright white core and a colored analytic halo. A four-vertex, bufferless triangle strip uses `gl_VertexIndex`; a 128-byte push block contains MVP, HDR color, capsule dimensions, animation and camera parameters. It uses KSA's reverse-Z depth test without depth writes and additive blending in a `SuperMeshRenderSystem.RenderTranslucencyPass` postfix, after atmosphere/cloud/ocean composition and before the main view's bloom. The halo is visible without bloom; the game's bloom can add further spill. Length comes from the controller's deployment animation. There is no blade mesh asset to animate or reallocate.

The dedicated saber blend state adds source RGB to destination RGB (`One + One`) and writes RGB only, preserving the attachment's sunflare/transmittance alpha. KSA's `RenderingPresets.BlendState.BlendAdditive` instead delegates to `Presets.BlendAttachment.BlendColorAdditive`, whose destination RGB factor is `DstAlpha`. `Water/Ocean.frag` emits zero alpha over open water, and the ocean's `Utils.BlendTransmittance` retains it. Using that preset erased the water color inside the halo geometry, producing a dark border even where emitted RGB faded to zero. Setting fragment alpha to one could not repair that fragment's destination RGB multiplication. The local blend state fixes both saber styles, arcs and cut sparks without changing shared game presets, depth behavior or render order. `TransparencyMsaaCompose.glsl` documents the attachment alpha's transmittance role; `PostProcess/sunbloom.frag` consumes it.

The original opaque-pass postfix made the blade disappear against Earth: without blade depth, atmosphere composition uses the distant background's depth to attenuate the already-added blade light. The late pass fixes that ordering without writing an opaque silhouette for the halo or disabling hull/terrain occlusion. Sparks use the same late pass. These small local effects do not sample atmospheric transmittance themselves; realistic long-distance fog attenuation is not implemented.

When `useCustomRenderPass=true`, KSA closes the translucency rendering scope before the postfix. The mod forces tracked color/depth attachment barriers, opens its own load/store scope, and always closes it in `finally`. It draws into `ColorAttachment`/`DepthAttachment` (multisampled when enabled), leaving resolve to the game. When the flag is false, the caller still owns the rendering scope and the mod only appends draws. Empty/unprepared views do not open an extra scope. The native PBR hilt and point lights keep their existing submission paths.

This small shader provides precise control of the white core and colored halo. The current `Content/Core/Shaders/Mesh/ModelPbr.frag` calls `GetMaterialData(..., emissive)` but never adds the returned emissive value to its output, so an emissive PNG on this particular native static renderer cannot produce a glowing blade. Native **part** rendering is different and does support emissive textures: `MeshIndirect.frag`'s `ENABLE_EMISSIVE` branch adds the red-channel grayscale mask as white emission. Setting bit 7 of `PartModel.PerInstanceData.StateBitFlag` additionally selects packed `EmissiveColor`; bit 6 disables emission. Turning a temporary hand prop into a full part model would require early instance-buffer upload despite the wrist's late per-view pose.

The user's successful Flexo carbon rod is consistent with this distinction. Read-only Flexo sources at `/Users/asherwin/repos/meow-sci/flexo` export `<PartModel>` plus a composited diffuse and grayscale `<Emissive>` texture; `src/ksa/types.ts` documents independent diffuse coverage and mask strength, and pairs the result with a matching native `<Light>`. Its emissive UI does not yet expose the packed per-instance color override. This native route is a valid alternative for an editor-placeable saber/rod part; the hand-held blade currently uses a dedicated small shader to avoid early part-buffer/late skeleton timing surgery.

Two native point lights approximate illumination along the blade. The prefix on `Program.UpdateRenderingResources` submits them after `LightSystem.ClearLights` and before `WriteForwardLights`/`UpdateRenderResources`. The earlier pose's body-relative emitter is reprojected using the current kitten/camera transform, so it survives camera motion and floating-origin changes. Illumination can trail fast wrist animation by one visual frame; the blade and hilt themselves use the final current pose. A pose callback is too late to upload native lights for that same frame.

Sparks store a part reference, local contact position/normal and birth time. Each draw resolves the current vehicle and part transform, so sparks follow moving parts. The queue is bounded to 64 bursts with a 0.7-second lifetime; expired entries are removed without a captured predicate allocation. There is no accumulated scorch ribbon or scar history. Structural cut caps remain native fragment geometry and cool through `RuntimeFragmentAssets`.

Contact emission uses `SaberContactProbe`: a current-segment, two-sided triangle intersection at 20 Hz, separate from all structural cutting. It refreshes nearby render-mesh references at most every 0.5 seconds, reuses list storage, resumes budget-limited searches and validates a previous hit with current transforms. It checks at most 256 mesh entries and 4,096 triangles per sample. No BVH rebuild, cut coverage, source-mesh preparation or vessel mutation runs for visual contact.

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
- `decomp/KSA/SuperMeshRenderSystem.cs`: `RenderTranslucencyPass` scope ownership and attachment selection.
- `decomp/KSA/PlanetTransparenciesRenderer.cs`, `Content/Core/Shaders/Atmosphere/Atmosphere.comp`: atmosphere composition uses the opaque scene depth.
- `decomp/KSA.Rendering/RenderTarget.cs`: `Samples`, `ColorImage`, `DepthImage`, `SetupGraphicsPipeline`.
- `decomp/KSA/RenderingPresets.cs`: reverse-Z no-write depth and blend presets.
- `decomp/KSA.Rendering.Lighting/Light.cs`, `ClusteredLightSystem.cs`: point-light creation and per-viewport submissions.
- `decomp/KSA/Part.cs`, `PartTree.cs`, `Vehicle.cs`: part-local transforms, current owning vehicle, assembly/body frame conversion.
- `Content/Core/Shaders/Mesh/ModelPbr.frag`, `Content/Core/Shaders/Common/MaterialSet.glsl`: verified native emissive omission.

The local unscience references `thug-life.lib/ThugLifeQuadRenderer.cs`, `ThugLifeRenderPatches.cs` demonstrate the render seam and reverse-Z pipeline setup. `graffiti.lib/DecalRenderer.cs` and `DecalAnchors.cs` demonstrate projected decals and persistent part-local anchors; a depth-projected scar pass is the next quality step if surface ribbons prove insufficient.

## Actual generated slice surfaces

The opt-in arbitrary plane slicer has a separate native geometry path in `RuntimeFragmentAssets`, rather than using scar ribbons to disguise an unchanged part. `FragmentGlbWriter` emits a self-contained GLB atlas containing the clipped surface triangles with their interpolated normals/UVs, one mesh per material, real cap triangles, and a complete indexed collider/picking mesh. Outer groups reference the source native PBR material IDs. The cap references `Purr2D2_CutSurface`: dark metal diffuse/ORM textures and a white emissive mask, with packed orange native emission cooling during the current session. The cap remains real dark geometry after cooling. Normal/source surface shaders are unchanged.

The preparation adapter transforms each clipped piece into its own principal-inertia/COM frame, quantizes positions to the exact float32 values to be exported, repairs collapsed slivers and re-audits oriented closed manifolds and volume. Compounds retain the clipper's closed-solid ranges for this audit so overlapping authored solids do not require a Boolean union. The worker reduces the collision geometry to convex-hull vertices, separately from full-detail picking and render meshes. This native convex envelope approximates concave/hollow fragments; the visible cap holes and surface geometry remain actual clipped triangles. Functional modules are discarded and crew presence does not block slicing; unsupported source geometry remains a technical refusal.

### Fixed native mesh buffers

`DeviceMeshInterleaved` normally allocates global vertex/index offsets in its constructor. Its backing buffers are built once at startup; runtime constructor calls would append outside the existing buffers. Worse, `Shared.Rebuild` uses the new allocation size while copying the old allocation, so allowing its running counters to grow after startup would make ray-tracing toggles unsafe. The adapter never does that.

`Initialize` runs during ImmediateLoad, before `Shared.Build`, and reserves aligned vertex/index regions from saved preferences (128/64 MiB by default) by advancing the native public counters once. These initial reservations support 32–1024 MiB vertices and 16–512 MiB indices and require saving preferences and restarting KSA. Uploads use absolute offsets in a disjoint-range allocator: later growth appends at the actual shared allocation ends, preserving stock meshes that follow the original reservation. Uploads create a `DeviceMeshInterleaved` plain-field object through `RuntimeHelpers.GetUninitializedObject` and explicitly fill every instance field. The exact all-public schema is checked before reservation and in the host inspector. No native mesh constructor runs at runtime. Each upload completes before native publication or source replacement.

Automatic growth defaults to runtime slice ceilings of 512/256 MiB, configurable live up to 1024/512 MiB. It grows only exhausted vertex/index channels, normally geometrically, and preserves all existing byte offsets. Allocation pressure pauses the upload and requests maintenance at the audited `PrepareFrame` settled handoff, before new draw recording. Outstanding private uploads drain first. Queue submission locks and a device-idle boundary protect replacement; new buffers remain private until copies of the old allocation lengths complete. Native ray-tracing descriptor bindings and cached rebuild inputs are refreshed together. The native `Shared.Rebuild()` is not used to resize: its new-size copy length is unsafe for growth. Global allocation counters advance only with committed buffer capacity so later native renderer rebuilds retain the new size.

Growth failure before publication keeps the old buffers. If GPU completion or descriptor rollback becomes uncertain, both buffer generations are retained and further growth is disabled for the session. Growth respects owned-capacity ceilings, native 32-bit counters and device limits, but temporarily needs old plus new shared allocations; those include stock and startup-restored assets. The Settings snapshot reports both runtime ownership and global buffer sizes. Lowering a maximum or disabling growth never shrinks existing allocations.

A free-list allocator reclaims only unpublished upload regions after their copies finish, through cleanup or automatically under pressure. Publication still pins an allocation before the first native registration, including partially registered failures. Clearing preparation records or generated debris does not release published regions. Published-mesh eviction and lazy historical loading are separate future work. Failed native registration remains unavailable until restart rather than being presented as reusable.

### Persistent assets and cooling

Successful preparation writes immutable content-derived GLB/XML assets under the installed mod's `Generated/Slices` directory. XML registers one native root Part with a ConvexHull collider and exact principal-axis CustomMass. Each original closed solid gets a native render SubPart containing that solid's material groups; every mesh is authored in the same principal/COM frame and the SubParts have identity transforms. The first visual subpart owns the complete-mesh MeshView so native hover raycasts can select every surface. A constructor hook repairs older instances whose MeshView lived on the root, without rewriting their durable files. This preserves the compound's closed-solid partition through repeated cuts and save/reload. There are at most 2,046 solid/material render groups plus separate full picking and reduced collider meshes per result; larger compounds are refused before source mutation. These files must remain alongside saves containing generated fragments. ImmediateLoad restores their asset bundles before native save recreation and explicitly reports a missing GLB. Persistent cache creation defaults to 2048 MiB and 8192 fragment entries, with live Settings limits up to 16384 MiB and 65536 entries. Reaching the limit refuses new files and never deletes old save dependencies; existing immutable assets remain reusable even after a budget is lowered below current usage. Startup reload currently binds picking/collider meshes into the normal shared buffers as well, although they are not drawn; this costs some duplicate GPU storage.

The production cap-emission prefix targets the exact private `PartModel.AddInstance(PerInstanceData, PerInstanceDent, IViewport, int)` submission. It changes state bits 6/7 and packed emissive color only for generated `_cap` model instances. Current-session heat is keyed by immutable result ID, so identical generated meshes share a cooling age. `ClearHeat()` makes saved fragments cold when changing/loading worlds; no mandatory wall-clock reheating is introduced. Re-slicing a cap keeps it in the cap material group rather than accidentally creating an always-emissive ordinary surface.

The generated root's mass/collider describe the complete fragment once. Its render SubParts contain no duplicate mass or collider modules, and the complete collider/picking mesh has no rendering module. Thus original visuals are replaced by the actual clipped geometry without drawing a hidden full-source duplicate.

### Slice resource maintenance

The former 30-vehicle refusal borrowed `PartFailure.MaximumVehiclesPerBubble`, a stock debris policy. `PhysicsBubble` uses a growing vehicle list and `Vehicle.Split` does not impose that cap. Purr2D2 now uses its own configurable cut-admission budget (128 by default, 30–512), still reserving isolation edges and up to eight fragments before mutation. It does not patch stock debris eviction or silently delete vehicles.

Settings reads a settled snapshot of memory, asset and local vehicle usage. Explicit confirmed debris removal runs at the joined handoff, joins orbit/performance readers, snapshots the registry and revalidates every candidate. It removes only generated, uncrewed debris with no stock/mixed full parts and excludes controlled and camera-followed vehicles. Pending slices and Force holds block maintenance. Durable files, templates and registered mesh ranges remain intact.
