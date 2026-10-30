# Purr2d2 integration audit

Read every section for a KSA upgrade, including unchanged and optional features. Paths below are
relative to the purr2d2 root; host names are search targets, not promises about future versions.
Discover new production consumers with the searches in SKILL.md. The initial inventory was read
from code targeting KSA 2026.10.19.5638 and revised for 2026.10.25.5707; current source and actual
NEW DLLs win over this snapshot.

## 1. Entry, frame handoff, input and diagnostics

**Mod:** `src/Mod.cs`, `src/Runtime/GameHooks.cs`, `SliceWorkerHooks.cs`, runtime initialization
and `Shared/RuntimeBootstrap.cs`.
**Host:** `KSA/Program.cs`, `Universe.cs`, `InputEvents.cs`, `PartTree.cs`, `JobSystems.cs`,
`VehicleUpdateTask.cs`, `PhysicsBubble.cs`, `ConstraintSim.cs`, `KittenEva.cs`,
`KSA.Concurrency.BepuWorkerDispatcher`, Brutal `JobScheduler`/`DynamicWorkerPool`,
`RenderCore.Input.ImGuiBackendGlfwImpl`, and StarMap lifecycle definitions.

- Verify one public entry class and `ImmediateLoad(KSA.Mod)`, `AllModsLoaded()`, `AfterGui(double)`,
  `Unload()` signatures. ImmediateLoad must reserve meshes/register saved templates before native
  binding/save validation; AllModsLoaded installs patches before JIT warmup.
- `Program.PrepareFrame(double,double)` is a private IL transformation, not just a method patch.
  Re-prove unique `JobSystems.VehicleSolver`/`JobScheduler.Wait`, then applied orbit/vehicle/cloth
  solvers, `InputEvents.ApplyInputEvents`, and the later cloth dispatch, dirty-derived/resource
  flushes, vehicle dispatch and orbit dispatch. SettledUpdate runs after input, before new readers.
  Vehicle membership refresh must still follow mutation before dispatch. Check argument 2 remains
  player dt. Wrappers preserve evaluation stack, labels, exception boundaries and call semantics.
- Keep `GetJobSimStep` untouched and Harmony `after = ["MeowSci.Unscience"]` where present.
  Preserve native worker joins and dirty flushes; never move structural mutation into UI/render.
- Recheck menu, editor (`OnFrameEditor`), overlay (`OnDrawUiThreadSafe`, after current picking),
  `OnGameLoaded`, `Dispose`, native mouse/cursor/scroll/focus/key callbacks, EVA `OnKey(GlfwKeyEvent)`
  and backend input suppression. Suppressing both native/backend routes must consume only owned
  gestures, once; world Shift-wheel and ordinary camera/UI input must route correctly.
- Worker diagnostics reflect `_pool` and patch explicit `IBepuDispatch.RunWorker`, job dispatch,
  `VehicleUpdateTask.Run` stage calls, bubble/terrain/collision/constraint helpers. Review the
  current `ChildTargets` and `InsertRunStages` in full. These optional patches can fail silently;
  they must neither change scheduling nor swallow native exceptions. A function-pointer local
  previously made `RunBody` unpatchable; do not substitute it without proving compatibility.
- Check main initialization faults and each optional feature's status/logs, not only the Ready
  message. Shared networking policy is process-level and must remain free of scene ownership.
- Audit `src/Slicing/SliceJitWarmup.cs` roots and `SliceJitCallGraph.cs` boundaries against NEW,
  including string-built `KSA.Module` nested generic lists, disposal methods and Bepu shape batches.
  Resolve/compile the actual bounded target graph without invoking game behavior. Changed roots,
  missing dependencies or increased graph size need investigation; do not simply raise budgets or
  remove failing roots. Preserve cancellation and unload behavior.

**Checks/docs:** `tools/inspect_host.cs`, `tools/check_fragment_templates.py`, frame/worker tests,
`tools/check_runtime_bootstrap.py`, `docs/host-validation.md`, `docs/slice-performance.md`.

## 2. Native topology, replacement physics and quiet breakup

**Mod:** `src/Slicing/NativeSlicePhysics.cs` and partials, `DetachedSliceFragment.cs`,
`QuietSubpartBreakup.cs`, `QuietSliceQuiescence.cs`, `NativeSliceQuiescence.cs`,
`QuietSliceContacts.cs`, `QuietSliceMotion.cs`, `SlicePrincipalFrame.cs`, `SliceColliderGeometry.cs`.
**Host:** `Vehicle`, `VehicleUpdateState`, `Part`, `PartTree`, `PartFailure`, `PartFailureEvent`,
`PhysicsBubble`, `CollisionAvoidancePair`, `NarrowPhaseCallbacks`, native collider/mass templates.

- Recheck exact `Vehicle.Split(Connection,IConnector,double,out PoseChange,string)`, bulk-change
  scopes, connection direction/root reversal, separation/adoption ownership and resource flushes.
- Private bindings: `Vehicle._threadWorkerUpdateState : VehicleUpdateState`, the non-public
  `BodyRates` setter, and `AdoptSeparatedVehicle(Vehicle,PhysicsBubble,double3)`; its cached open
  instance delegate also takes the receiver. Quiet subpart breakup prepares authored subparts in the
  mod; `PartFailure.IsolateAndDestroy` is no longer a production binding (only a test stub). Check
  source disposal and crew/content transfer, and follow all native adoption effects.
- A generated replacement is inert wreckage, not a functional tank/engine/seat. Validate positive
  dry mass, custom mass/inertia/principal axes, convex hulls and current bubble ownership. Preserve
  CCI linear/angular motion and Galilean invariance at high orbital velocity. Resample motion at
  swap; an earlier isolation snapshot is not current physics.
- Durable, registered, GPU-ready fragments precede irreversible isolation. Commit only at the
  joined handoff; remove the source only after replacements are ready. Revalidate pending work
  after movement/topology changes. Cancellation after isolation can retain the separated empty
  shell; do not promise a rollback the current implementation does not provide.
- Quiet module ticking rewrites **callers** (`PhysicsBubble.UpdateModulesJob.ExecuteOnBubble`,
  `ApplySingleVehicleMotion`), historically three `UpdateModules` calls. Inspect callers/IL for
  inlining and additional paths; zero stale nozzle performance/thrust when held. Recheck
  `PartFailure.Detect` and private `DetectStructuralFailure(VehicleUpdateState,SimStep)` observers.
  Do not introduce global damage immunity or swallow existing failures.
- Native avoidance links use `SuppressAllContacts` only for transaction-owned participants.
  Adoption may omit suppression; duplicate links and first-match lookup matter. Preserve terrain
  and unrelated craft collisions, actual-overlap retirement, bubble changes and unload semantics.

**Checks/docs:** quiet/isolation/detached/physics tests, fragment host check,
`docs/research-slicing-physics.md`, `docs/slicing-improvements-plan.md` and force-breakup docs.

## 3. Geometry capture, picking, fragment XML and durable assets

**Mod:** `src/Cutting/`, `src/Rendering/ManualContactProbe.cs`, `SaberContactProbe.cs`,
`RuntimeFragmentAssets*`, `FragmentTemplateLoader.cs`, `FragmentAssetWriter.cs`,
`FragmentGlbWriter.cs`, `FragmentTopology.cs`, `AuthoredDebrisAssets.cs`.
**Host/Content:** `PartPicker`, camera ray/projection, `PartModelModule` and static/dynamic/glass
templates, host primitives/`PositionsCompare`, `XmlLoader`, `AssetBundle`, `ModLibrary`,
`PartTemplate`, `PartInstance`, `MeshAtlas`/GLB/PBR/picking/collider XML.

- Audit coordinate chains: mesh/local/subpart → assembly → vehicle body → CCI/bubble/ego/screen,
  units, matrix order, normals and double-to-float boundary. Keep high-speed recuts precise.
- Stock mesh topology, subparts, material grouping, instance transforms and GLB schemas can change
  without API breaks. Recheck actual NEW XML/atlases, not only cached stock triangles.
- Cosmetic contact probes cannot initiate structural cuts. Current intentional manual SWING
  contact **can** queue cutting; automatic/recovery/clash motion cannot. Some old research says all
  contact cutting is retired: current production and later acceptance entries supersede it.
- Preserve visible-blade/range/pose checks, bounded discovery/triangles/time, frozen sweep identity,
  per-gesture ownership, stale-target refusal, background preparation and settled-only commit.
- Generated XML decoding must retain component templates, glass/static modules, subparts,
  picking and mass; the dedicated loader exists because plain serialization lost these.
  Check the Part constructor repair for legacy root picking and native picking of new subparts.
- Keep content-addressed generated IDs/material references stable across upgrade/restart; register
  historical XML before missing-part validation. Missing GLBs must produce explicit failure.
  Disk limits must refuse work rather than evict assets referenced by other saves. Generated debris
  remains hidden from editor menus without preventing save instantiation.

**Checks/docs:** stock-slice, target/contact, fragment/export/template tests;
`docs/research-slicing.md`, `research-assets.md`, `research-native-assets.md`,
`research-cutting.md`, `research-slicing-physics.md`.

## 4. Startup mesh reservation and runtime buffer growth — highest risk

**Mod:** `RuntimeFragmentAssets.cs`, `.Async.cs`, `.Resources.cs`, `.Growth.cs`,
`NativeMeshGrowthHost.cs`, `NativeMeshGrowthBackend.cs`, `DedicatedMeshBufferAllocator.cs`,
`src/Core/SliceMeshArena.cs`, `SliceMeshGrowth.cs` and slice resource maintenance.
**Host:** `DeviceMeshInterleaved`/`Shared`, `Device`/Vulkan `Queue`, `BufferEx`, `StagingPool`,
`Core.Renderer`/`CommandBufferPool`, `RaytracePipeline`, `RaytracingRenderer`, BLAS helpers.

- Startup reservation must precede Shared.Build/Bind. Exact plain public instance fields are
  checked: `HostMesh`, `HasNormal`, `HasUV`, `VertexStride`, `VerticesOffset`, `VerticesSize`,
  `IndexCount`, `IndexType`, `IndicesStride`, `IndicesOffset`, `IndicesSize`. Audit types,
  initialization, strides/alignment and any new constructor state; names alone are insufficient.
  Private runtime uploads cannot race binding or grow global counters outside the audited path.
- Growth pins **five module MVIDs**: KSA, Brutal.Vulkan, Brutal.Vulkan.Abstractions,
  Planet.Render.Core and Planet.Core (confirm via the actual types in `ValidateLayout`).
  Inspect every contract in `tests/MeshGrowthHostChecks.cs` against NEW, not only GUID equality.
- Private field contracts: `Device._queues`, `Queue._lock`, `RaytracePipeline._createInfo`,
  `_descriptorSet`; RT cached vertex/index arrays and descriptor bindings historically 3/4.
  Check dictionary/lock types, queue discovery, lock order, submit/wait behavior and native rebuilds.
- Allocation flags: `DedicatedMeshBufferAllocator` forwards only `AllocationInfo.CreateInfo.Next`
  and refuses the `AllocateFlags` channel added in 2026.10.25.5707. Re-prove that the top-level
  `KSA.Rendering.Raytracing.RaytraceAllocator` still supplies `DEVICE_ADDRESS` through `Next`, that
  RT shaders still read geometry through those descriptors (not buffer references), and that no BLAS,
  TLAS or instance data caches shared-buffer device addresses.
- Existing raster draws, GLB meshes, thumbnails, BLAS and secondary views must read current shared
  handles or have every cache updated. Re-prove absence of newly cached VkBuffer/device addresses.
- Drain private uploads before growth; exclude submissions, wait device idle, copy **full old
  allocation lengths**, preserve offsets, usage/device-address allocator flags, alignment and
  device limits, then publish shared buffers/counters and RT descriptor/cache together.
- Preserve memory barriers/fence completion and allocator cleanup. Prepublication failure keeps
  old buffers. Unknown submission completion or failed descriptor rollback retains both generations
  and disables growth. Never free memory that the GPU might still reference.
- Review `tools/check_mesh_growth_host.py` real-host IL coverage and production transaction tests
  after fixes. Updating MVIDs without this audit only disables a safety mechanism.

**Checks/docs:** both mesh-growth scripts, resource/UI tests, native growth acceptance checklist
in `docs/ACCEPTANCE.md`, `docs/slice-performance.md` and the compatibility record.

## 5. Saber rendering, environment lights and shader ABI

**Mod:** `src/Rendering/SaberRenderer*`, `SaberBlendState.cs`, `SaberVfx.cs`, `DarkSaberSparks.cs`,
`EnvironmentLightingPatch.cs`, `ForceCaptureBubble.cs`, `Assets/Shaders/`.
**Host/Content:** `SuperMeshRenderSystem.ClearBuckets`/`RenderTranslucencyPass`,
`Program.UpdateRenderingResources`, RenderGame/Viewport/Editor ordering, `ShaderReference`,
HDR/depth/MSAA targets, native light systems and stock forward-light GLSL.

- `tools/inspect_host.cs` pins each render path's call order by exact callee name. In 5707,
  `RenderAtmosphereOnly` became `RenderAtmosphereAndClouds` at the same `RenderViewport` position;
  confirm semantics before renaming an expected stage, never just to make the check pass.
- Recheck Harmony-bound callback argument names as well as signatures, per-view submission,
  command-buffer ownership, formats/sample counts, reverse-Z/depth, blend state, atmosphere/water
  composition, bloom/resolve order and pipeline recreation/disposal. Only native views prove draw
  correctness; a successful shader compiler does not prove compatible attachments or ordering.
- Validate source **and staged** shader includes and path tracking (historically requiring Content
  in dependency paths), descriptor/push-constant layout, packing and C#↔GLSL offsets; currently 128
  bytes. Re-evaluate host includes/loader/compiler behavior, not just vendored shader text.
- Point lights must be added after native ClearLights and before forward/deferred uploads.
  EnvironmentLightingPatch changes only the main clustered view's selected 8/16 limit. Check the
  actual `WriteForwardLights(IViewport,ref UboLightingData)` branch, private `_forwardCandidates`,
  all CPU array capacities, native packing and stock shader array sizes/dynamic loop. Rejection
  must preserve incoming IL, selection logic and secondary-view budgets; setting defaults off.
- Cap emission prefix is the exact private
  `PartModel.AddInstance(PerInstanceData,PerInstanceDent,IViewport,int)` overload. Preserve struct
  meaning and target only generated cap instances; saved caps reload cold. Check `Dispose` timing
  before Vulkan teardown and ownership of shared assets versus mod-owned GPU objects.

**Checks/docs:** saber shader script via validator, blend/VFX/light tests and fragment host check;
`docs/research-rendering.md`, `docs/COMPATIBILITY.md`, water/lighting acceptance entries.

## 6. Kitten avatars, animation processors, capes and expressions

**Mod:** `src/Pose/`, `src/Capes/`, `src/Runtime/SaberRuntime.Kittens.cs`, `.Capes.cs`, `.Clash.cs`.
**Host/Content:** `KittenEva.Renderable`, private `KittenRenderable._characterAvatar`,
`CharacterAvatar`, `CharacterModel.AnimProcessors`, `IAnimProcessor`, skeletons/GLB joints,
expression catalogs and character render/material systems.

- Recheck reflected avatar field **type** in every consumer. Audit animation processor callback
  order/local-vs-skeleton transforms and per-view pose freshness; preserve root/legs/native
  locomotion, independent saber/Furce arms, somersault layering and clash-tail-last behavior.
- Validate actual NEW rig hierarchy/bind poses, metre/model scale, bone/finger discovery and hilt
  emitter alignment. Names include Shoulder_R/Elbow_R/Wrist_R, Chest_M or Spine1_M; read current
  discovery code for all alternatives. Name survival does not prove unchanged axes/rest poses.
- Clashes use the live angry-expression catalog/root and hold poses briefly. Do not assume old
  private expression-cache tricks from reference mods are part of this implementation.
- Cape hook is `KittenRenderable.UpdateRenderData`; check viewport/transform semantics, skinned
  mesh/skeleton loader, cloth bone weights, collision/pose caches, update budgets, material binding
  and disabled/seated/background lifetime. Current assets are cape-only; old hood plans are history.
- Remove only owned processors/attachments; prevent duplicate owners after boarding/reload/control
  changes. If assets need reauthoring, update authored sources/generators and validation together;
  Blender may be used when available, but a game update alone does not require regenerating art.

**Checks/docs:** pose/clash/kittens suites, cape asset check via validator and `check_cape_cloth.py`;
`docs/research-pose.md`, `research-capes.md`, interaction and acceptance records.

## 7. Somersault/MMU and Furce native ownership

**Mod:** `SomersaultMotionController.cs`, somersault runtime partials,
`SaberRuntime.Kittens.cs`, all `src/Force/` (including breakup and clutter).
**Host:** `Universe.PrepareVehicleWorkers(SimStep)`, `Vehicle.PrepareWorker(SimStep)`,
`TakeUpdateStateIfOrphaned`, `VehicleUpdateState`, EVA locomotion/physics/MMU,
prepared-worker `ManualControlInputs`, `BubbleOrigin`, `BubbleClutterStatics`, `ConstraintSim`, Bepu.

- Both somersault and Furce transform worker preparation: preserve their composition in either
  patch order, native prepare/sync/dispatch ordering and exact-once owned writes. A worker with
  `IsKitten=false` releases somersault ownership before touching motion or input (Unscience Iron Man).
  Check Space arbitration, queued launch/boost acknowledgements, ground/ocean/warp and world epochs.
  Re-prove virtual base/EVA PrepareWorker order through ladder detection, character-input copies,
  PrepareFromVehicle and VehicleUpdateData.Prepare before applying owned overrides.
- Audit body/physics/bubble/CCI/CCF rotations and velocity conventions, gravity/jump tuning and MMU
  attachment visibility leases. The current code writes prepared-worker inputs; older host-validation
  prose mentions a private `_manualControlInputs` adapter that is no longer a production binding.
  Follow the actual implementation. Do not infer motion correctness from compatible numeric types.
- Furce capture/release must preserve native body ownership/inertia and only exclude captured
  workers/contact pairs. Verify targeting freshness, bounded query/cache behavior, disposed/bubble
  checks, group motion/spread, capture limits, rollback and staged native breakup.
- Clutter reflection spans `_statics`, `_dynamicBodies`, `_displacedBodyHandles`,
  `_pendingExclusions`, `_describedOriginPositionBub`, `_describedCelestial`, `_describedEcotypes`,
  `_instanceData`; nested CellKey/ClutterPhysicalInstance/PendingHit and native value records;
  `TryDisplaceInstance` including by-ref/out types; and `Clear(ConstraintSim)`. Read
  `ClutterForceNative.cs` in full for dictionary value fields and emitted delegate IL.
- `KSA.NarrowPhaseCallbacks.AllowContactGeneration` uses Harmony `ConstraintSim ___Sim` injection.
  Verify the backing field name/type and overload, native displacement/shape handling, generation
  invalidation, placement reconstruction and exclusion publication. Retain unrelated collisions.

**Checks/docs:** force host/Bepu checks, somersault/force/input suites;
`docs/somersault.md`, `docs/force-breakup.md`, `docs/research-somersault.md`.

## 8. Save transactions, kitten identity and cleanup

**Mod:** `KittenSaveHooks.cs`, `KittenIdentityIndex.cs`, persistence runtime partials,
`src/Persistence/`, `GameHooks` world-reset prefixes.
**Host:** `GameSave.Populate`, bool `UncompressedSave.Write`, `UncompressedSave.Load`,
`Universe.DeserializeSave(UniverseData)`, `LoadSystem(string)`, `Program.OnGameLoaded`,
`JobSystems.WaitAll`, UniverseData roster/EVA/crew/seat schemas and native save file layout.

- Check exact signatures, execution timing, nested calls, return/exception behavior and
  `__runOriginal`; observers run last after `MeowSci.Unscience`. A skipped/deferred/rejected original
  cannot clear the current scene or publish a sidecar. Actual reconstruction joins jobs before
  releasing old bindings; failed joins abort reconstruction. Retain idempotent post-load cleanup.
- Preserve exact capture/native snapshot pairing, generation tokens, atomic sidecar publication
  and hash of the correct `universe.xml`. Follow NEW file write/rename paths and format changes.
  Settings, per-save purr2d2.json and Generated/Slices serve different lifetimes.
- Persistent color/type/cape/equipment/length choices survive boarding and disabled saves; transient
  holds/cuts/gestures/worker commands do not. Review roster and standalone-EVA identity, duplicate
  names/seat conflicts, character asset IDs, quarantine/rollback, unknown-record preservation and
  unavailable-record recovery. Never transfer choices based on character asset or display name alone.

**Checks/docs:** persistence/kittens hooks and runtime fixtures, both Harmony orders;
`docs/persistence.md`, `docs/research-persistence.md`, latest acceptance entries.

## 9. Audio and packaging ecosystem

**Mod:** `src/Audio/SaberAudio.cs`, `ForceBreakupAudio.cs`, `Assets/Sounds.xml` and mod manifests.
**Host:** `GameAudio`, `SoundBehavior`, `IChannel`, `SpatialAudio`, FMOD/Brutal.Fmod and mixing.

- Check sound XML/asset IDs, channel creation/loop/stop semantics, spatial coordinate transforms,
  distance and gain units, master/SFX mixing, pause/warp behavior and emitter following. Verify
  Light/Dark loops and ignition/retraction/contact/clash/manual-swing/somersault/breakup cues.
  Channels must stop before audio shutdown; all owners clean up on scene replacement.
- Supplied recordings have provenance; do not regenerate or replace them to resolve an unrelated
  API update. Managed audio fixtures cannot establish native balance, attenuation or FMOD lifetime.
- StarMap/Harmony updates are separate ecosystem changes unless required by NEW. Verify actual
  loader-provided versions and loop patches; do not add runtime copies to the package to resolve
  missing host DLLs. Brutal/Planet/Bepu ship with the game and their drift is part of this upgrade.
- Shared networking bootstrap policy is synchronized with peer mods when changed; peers may be
  read-only. Report required peer follow-up instead of modifying them outside task authorization.

**Checks/docs:** audio harness, asset validator, runtime-bootstrap check and final archive integrity;
`docs/research-audio.md`, `Shared/README.md`, manifest and README.
