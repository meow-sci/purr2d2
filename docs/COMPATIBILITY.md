# Compatibility and integration map

## Runtime networking startup

The entry module includes `Shared/RuntimeBootstrap.cs`, synchronized with gatOS/unscience. Its module initializer preloads the two networking framework assemblies into the default context; `AllModsLoaded` reports retained consumer bindings and non-default copies before Harmony installation/warmup. An incompatible networking report does not disable unrelated Purr2D2 gameplay. No game hook, save payload or scene-state ownership changes. The shared policy documents the early-loading limit and the isolated regression matrix.

The authority for this build is the supplied `ksa-game-assemblies/current` snapshot, **2026.11.10.5801**, snapshot commit `4de698e8cf248c29fc95f188fb258775d6e574a3` (checkout `d07b8700c347dc2e2ac1472e829b644fdc15ba8d`). The upgrade span is the previously managed/host-verified **2026.10.25.5707** (`4b3e9e2281c6eec25c9ab77ddf2961ec53ec3dc8`) → 5801. `version.json` covers 5707→5801 but lists changelog entries only through 5800; revision 5801 has source/IL evidence only. Native acceptance is pending; see the [dated upgrade record](ACCEPTANCE.md#ksa-202611105801-upgrade-2026-11-08). The snapshot includes DLLs, decompiled sources and text Content, with no stock GLBs. KSA coffer articles target 5554 and are secondary evidence. StarMap.API `0.3.6` and Harmony `2.4.2` remain compile-only dependencies.

| Inspected source | Commit |
|---|---|
| ksa-game-assemblies | `4de698e8cf248c29fc95f188fb258775d6e574a3` (2026.11.10.5801) |
| StarMap | `78816daffe69c863e1a59627c45ae010e00aea39` |
| Unscience | `1829b3b298dd0fa5a67342cafff29074c8c29286` |
| Flexo | `e804879abfc1d45ca1c174a9c95c5fded58c1bf2` |

## Engine integration points

| Component | Engine surface | Compatibility behavior |
|---|---|---|
| Entry/content startup | StarMapMod, BeforeMain(), AfterGui(double), Unload(); direct `ContentLoader.ReadSource` and `ModLibrary.LoadAll(IContentLoadProgress)` hooks | Early source postfix reserves mesh capacity and appends normalized persisted rows before native registration/resolution. Direct load postfix runs the no-argument initializer once after full load; avoids removed `Mod.PrepareSystems` and StarMap’s old forwarded-argument callback. Late/hot startup refuses explicitly |
| Generated slice restore | Installed `Generated/Slices/` XML/GLBs; native content source and post-load template lookup | Directory/file read and validation failures are logged and skipped per entry, with no partial file publication. Failed or missing native templates do not block mod initialization. Generated files remain unchanged and KSA handles unavailable parts in saves |
| Safe mutation | Private `Program.PrepareFrame(double,double)` IL, after `InputEvents.ApplyInputEvents()` | Requires unique ordered applied-solver/input/dispatch calls; refuses incompatible layout; runs after optional Harmony owner `MeowSci.Unscience` so its original-call validation precedes Purr2D2 wrappers |
| Input/preparation | `Program.OnFrameEditor(double)` postfix | Inside ImGui frame, runs while HUD hidden |
| EVA target selection | `Vehicle.Target`, `Vehicle.SetTarget(IOrbiter)` | Mod dropdown queues changes through the settled handoff; scopes the explicit slice without requiring stock EVA flight-computer buttons |
| Slice marker | Private `Program.OnDrawUiThreadSafe(double)` postfix; main camera `EgoToScreen` | Uses the current frame's native pick for hover and locked-part markers |
| Saved fragment hover | `Part(string, PartTemplate, TransformDataReference<SubPartTemplate>, Part)`, `Part(PartInstance)`, `Part(PartInstance, Part)` and `Part(Part, Part)` constructor postfixes | Adds legacy root picking meshes to identity visual children before tree ownership, registered once through native `Module<T>.AddNewTo`; new exports use native subpart picking directly. Since 2026.10.25.5707 save loading (`Part(PartInstance, Part)`) and editor cloning reach a private initializer without the string/template constructor; host IL checks require every public constructor to reach exactly one postfix; 5801 replaces the public template constructor’s `PartInstance` parameter with `TransformDataReference<SubPartTemplate>` and the one-argument save constructor now also bypasses every other public constructor |
| World reset | `Universe.DeserializeSave(UniverseData)` last-priority prefix and `Program.OnGameLoaded()` postfix | Skipped originals do not clear state; actual loads join all native jobs before discarding queued commands, old owners, channels, slice requests and sparks; post-load cleanup remains idempotent |
| Kitten persistence | `GameSave.Populate()`, bool `UncompressedSave.Write()`, `UncompressedSave.Load()`, `Universe.DeserializeSave(UniverseData)`, `Universe.LoadSystem(string)` | Passive last-priority hooks after Unscience; exact capture/snapshot pairing, XML hash, scoped generation tokens and executed-original guards; no second frame/load scheduler; roster/EVA/seat checks before binding |
| Shutdown | `Program.Dispose()` prefix | Releases owned resources before FMOD/Vulkan teardown; StarMap unload is idempotent |
| Avatar access | `KittenEva.Renderable`, private `KittenRenderable._characterAvatar` | Cached reflection; missing field/rig disables equip with status |
| Pose | `CharacterModel.AnimProcessors`, `IAnimProcessor` local/skeleton callbacks | Updates arm only; removes only its own processor |
| Hilt | Native GltfFile/PbrMaterial/StaticMeshRenderable | Shared mod assets remain engine-owned |
| Blade | SuperMeshRenderSystem.ClearBuckets/RenderTranslucencyPass; ShaderReference; HDR target | Draws after atmosphere using existing color/depth attachments; per-view submissions and sample/format-specific pipelines. 5801 uses `B10G11R11UfloatPack32` for the offscreen color target; pipelines and water attachments derive the actual color/depth/sample formats. RGB-only additive blending remains valid; secondary views composite clouds before the blade |
| Light | Program.UpdateRenderingResources prefix; LightSystem.CreateLightInstance | Runs after ClearLights and before forward/deferred light uploads |
| Extended environment lighting | `ClusteredLightSystem.WriteForwardLights(IViewport, ref UboLightingData)` transpiler; `UboLightingData.ForwardLightArray` and private `_forwardCandidates` capacity | Default-off saved preference; guarded replacement of the clustered branch's eight-light cap with a live 8/16 choice for the enabled mod's main clustered view. Requires sixteen-slot CPU storage; preserves selection, packing and secondary-view budgets. Incompatible installation/rebuild returns incoming IL intact, disables the feature and reports availability in settings. No shader or buffer-layout changes. |
| Audio | GameAudio, SoundBehavior, IChannel, SpatialAudio | Own channels only; SFX/master mixing respected; stops before mixer release |
| Contact | Static/dynamic/glass native render meshes, PositionsCompare, part/body/Ego matrices | Double precision transforms; 20 Hz bounded visual-only triangle query; no damage callback |
| Separation | Vehicle.Split, Part.Connection/PartTree, MarkAsDebris | Uses native topology transfer; unsupported graphs are left intact. After bulk closure, actual split participants without controls, crew or active/editor ownership become debris for native F11 cleanup; existing untracked vehicles are not relabeled |
| Arbitrary geometry | PartModelModule templates and native host primitives | Closed authored solids and backed open panels; interpolate attributes, cap and audit before replacement |
| Fragment upload | DeviceMeshInterleaved.Shared startup counters and native Bind | Reserve a bounded arena before binding; require the exact plain-field layout; private uploads never advance global counters |
| Runtime mesh growth | Pinned KSA, Brutal.Vulkan, Brutal.Vulkan.Abstractions, Planet.Render.Core and Planet.Core module MVIDs (audited for 2026.11.10.5801); private `Device._queues`, `Queue._lock`, `RaytracePipeline._createInfo`/`_descriptorSet`; `RaytraceAllocator`'s `Next`-chained device-address flag | Settled-frame replacement with queue exclusion, device idle, old-length copies and joint RT descriptor/cache publication. Any other module identity, a changed field contract or allocation flags outside `Next` leaves growth unavailable with a status message and keeps existing buffers |
| Molten caps | Public `PartModel.WriteInstancesToGpu(IViewport viewport, int frameIndex)` prefix and private `PartModel.AddInstance(PerInstanceData,PerInstanceDent,IViewport,int)` prefix | Changes emission on submitted generated cap instances only; saved caps reload cold. Batched `PartTreeRenderData.Compose` fills `PartModel.ViewportData.InstanceList` without calling `AddInstance` in every non-raytraced-IVA view, so the upload prefix rewrites that view's cap entries just before upload; raytraced IVA instances bypass the list and keep the submission prefix. Cold caps set state bit 6 (no emissive), because the cut-surface emissive map is uniform white; hot caps set bit 7 with a cooling color. Both rewrites are idempotent |
| Generated physics | ConvexHullColliderTemplate, CustomMass, PartTree.CreateFromNewPartTree, Vehicle.CreateVehicle, private AdoptSeparatedVehicle and _threadWorkerUpdateState, private BodyRates setter | Stage native roots/trees once, adopt into the existing bubble, retain principal inertia and conserve CCI momentum before source deletion |
| Generated saves | `ContentLoader.ReadSource` before native preload/full-load reset; `DataReference<T>`, `TransformDataReference<SubPartTemplate>`; runtime `TagTable` collections | Adapt legacy XML in memory to wrapped Components/SubParts/InertMasses, Key/Ref identities; register every generated row before resolving references and initializing. Immutable IDs/XML/GLBs remain unchanged on disk; preserve Generated/Slices with affected saves |
| Clutter appearance | Native private physical rows, `PackedColor` and new `PackedModifierColor`; PartModel shader flags 0–12 | Preserve both native colors during promotion; slice tint uses the already-composed PackedColor. Runtime marker moves to bit 13, luminosity uses high flag bits plus unused RGB10 alpha bits, retaining old mesh IDs and full stored precision |

Full path/symbol evidence and caveats are in the `research-*.md` documents, including the slicing geometry, native physics and actual native-asset feasibility reports. Flexo is reference material, not a runtime dependency. Unscience is likewise a source of demonstrated patterns; no Unscience assembly is required.

## Ownership and time

Simulation commands are applied after the preceding worker results and game input are committed, before the next nearest-orbit/physics workers start. UI and render callbacks never split vessels. The injected callback does not replace `GetJobSimStep`, avoiding the specific handoff seam used by Unscience. Future changes to the engine or another transpiler may still require compatibility review.

Purr2D2 orders its frame transformation after Unscience's exact Harmony owner, with no assembly
dependency. This lets Unscience validate the untouched native solver calls before Purr2D2 wraps
them, including when an older Unscience installer uses normal transpiler priority. The separate
last-priority `BeforeDeserializeSave(bool __runOriginal)` prefix observes a deferring prefix's
decision: a direct `Universe.DeserializeSave` call skipped by Unscience does not clear Purr2D2's
current session. Replay joins `JobSystems.WaitAll()` before cleanup and native reconstruction;
a failed join propagates and aborts reconstruction. File loads already defer at
`UncompressedSave.Load`, so their initial request never reaches the deserialize prefix. Their
existing `Program.OnGameLoaded` cleanup runs again after reconstruction with cleared ownership.
Per-kitten choices now use the separate Purr2D2 sidecar described below; shared preferences and generated-asset storage keep their existing roles.

For a shared kitten in Unscience Iron Man mode, its first-priority keyboard prefix owns vessel
input before Purr2D2's effectful Space prefix. Purr2D2 also checks the final prepared worker's
`IsKitten` flag and releases queued/active somersault ownership before writing any motion or input
fields when another mode selects vessel physics. This needs no cross-assembly mode lookup and
preserves normal EVA behavior. See [somersault ownership](somersault.md) for regression boundaries.

The blade consumes each applied simulation timestamp once, suspends when paused or above 5x, and rejects large step gaps. The explicit swing advances in player time only with a fresh visible pose and a valid applied simulation step. Pose snapshots are captured from the main game view, in the kitten's body frame. Camera-relative floats are used only at final rendering submission. Audio uses the same body-space emitter plus the engine camera's spatial transform.

Only the explicit swing can prepare or commit a structural cut. The hovered ray hit must be within twice the visible blade length of the live hilt at selection and stroke start. After the stroke, ownership/transform and native physics checks still apply; range is not imposed again. Crew aboard does not block slicing. The cosmetic contact probe is independent and cannot initiate a cut. Autopilot and flight-control overrides have been removed.

KSA owns the resulting split vessels and their native saves. The mod owns per-kitten equipment and colors, transient sparks and cap heat, channels and GPU pipelines. Switching control and boarding preserve logical equipment, color/type and cape profiles. Successful scene loads replace those profiles from the matching `purr2d2.json`; vanilla/direct/new-system loads use defaults. Live resources and transient actions still clear. No object pointers or GPU handles are serialized. User preferences use a bounded, validated JSON file and atomic replacement. Arbitrary slicing additionally persists immutable native GLB/XML definitions under `Generated/Slices`; those files are dependencies of saves that reference their generated IDs. Cache budgets refuse new work instead of evicting assets needed by older saves.

## Updating the game

1. Obtain one coherent new DLL/decomp snapshot; do not mix versions.
2. Inspect every integration above and update the exact frame seam/native replacement signature checks first.
   Re-pin mesh-growth module identities only after re-auditing the shared-buffer, queue and ray-tracing transaction (`.agents/skills/upgrade-ksa/references/integration-audit.md`, section 4).
3. Build the entire mod, run managed and asset validation, and recompile both shaders.
4. Validate the staged ZIP contains no host/runtime assemblies.
5. Complete the native acceptance checklist before claiming support for the new snapshot.

Do not infer native runtime/platform support from a successful macOS managed compilation. The provided game assemblies are Windows x64; no KSA native game session has been exercised by the build checks alone.

`python3 tools/check_fragment_templates.py` also checks the environment-light patch against real host IL: installation, exact instruction/branch preservation, live 8/16 selection, main-view isolation, shutdown and rejection of changed selection branches. Native acceptance remains a six-or-seven-saber scene with the option off/on/off, camera movement, nearby competing lamps and a GPU frame-time comparison. The stock shader's sixteen-element arrays and dynamic `forwardLightCount` loop must remain aligned with the CPU layout when updating the game.

Per-kitten schema, ownership and native timing limits are documented in [persistence](persistence.md). Standalone load observation retains native timing; managed fixtures do not establish late-UI GPU lifetime safety.

The 5801 atlas loader leaves IsReferenceable=false on deferred mesh rows. Furce authored-surface capture therefore checks exact registered mesh/material identity and loaded geometry instead of that legacy flag. See the [playtest follow-up](ACCEPTANCE.md#furce-atlas-capture-and-native-offcut-debris-2026-11-08).
