# Compatibility and integration map

The authority for this build is the supplied `ksa-game-assemblies/current` snapshot, build `2026.10.19.5638`. Implementation articles in the downloaded KSA coffer target `2026.10.10.5554`; their names and behavior were verified in the newer decompiled sources. StarMap API package `0.3.6` and Harmony `2.4.2` are compile-only references; the actual game/loader installation must supply compatible assemblies.

| Inspected source | Commit |
|---|---|
| ksa-game-assemblies | `edda84495fcdc5592c80e1e6442573cd3419354a` |
| StarMap | `78816daffe69c863e1a59627c45ae010e00aea39` |
| Unscience | `1829b3b298dd0fa5a67342cafff29074c8c29286` |
| Flexo | `e804879abfc1d45ca1c174a9c95c5fded58c1bf2` |

## Engine integration points

| Component | Engine surface | Compatibility behavior |
|---|---|---|
| Entry | StarMapMod, ImmediateLoad(KSA.Mod), AllModsLoaded(), AfterGui(double), Unload() | Attribute signatures validated against supplied API |
| Safe mutation | Private `Program.PrepareFrame(double,double)` IL, after `InputEvents.ApplyInputEvents()` | Requires unique ordered applied-solver/input/dispatch calls; refuses incompatible layout |
| Input/preparation | `Program.OnFrameEditor(double)` postfix | Inside ImGui frame, runs while HUD hidden |
| EVA target selection | `Vehicle.Target`, `Vehicle.SetTarget(IOrbiter)` | Mod dropdown queues changes through the settled handoff; selecting a ring does not require stock EVA flight-computer buttons |
| Ring surface snapshot | `PartModelModule.Template.Mesh`, fallback `MeshViewModule.MeshView`, all `MeshReference.PositionsCompare` primitives | Walk full/nested part instances; explicitly compose local transforms and subtract current COM; refuse missing, invalid or over-budget geometry |
| Ring preview | Private `Program.OnFrameViewports(double)` postfix; main camera `EgoToScreen` / `ScreenToEgoRay` | HUD-visible ImGui overlay uses framebuffer/logical-pixel conversion; marks sampled cut ring, MMU orbit and entry |
| Hull-point selection | `Program.OnMouseButton(GlfwWindow,GlfwMouseButton,GlfwButtonAction,GlfwModifier)` prefix | Armed scene selection consumes the left press and matching release before stock dispatch; UI clicks and other mouse actions keep stock behavior |
| MMU translation | Private `Vehicle._manualControlInputs : ManualControlInputs`; `PhysicsBubble.FlightComputerInputsFor`; native thruster states | Exact input field checked by host inspector; requires MMU mode and active fueled translation authority; publishes six discrete minimum-impulse pulse commands |
| Ring attitude | `FlightComputer` Auto / EclBody / Custom, `FlightComputerRollMode.Up` | Coupled roll tracks the full desired blade attitude; snapshot/restore covers mode, frame, tracker, custom target, pulse/RCS/roll/rate settings and kitten control mode |
| World reset | `Universe.DeserializeSave(UniverseData)` prefix and `Program.OnGameLoaded()` postfix | Discards queued commands, old owners, channels, heat and marks |
| Shutdown | `Program.Dispose()` prefix | Releases owned resources before FMOD/Vulkan teardown; StarMap unload is idempotent |
| Avatar access | `KittenEva.Renderable`, private `KittenRenderable._characterAvatar` | Cached reflection; missing field/rig disables equip with status |
| Pose | `CharacterModel.AnimProcessors`, `IAnimProcessor` local/skeleton callbacks | Updates arm only; removes only its own processor |
| Hilt | Native GltfFile/PbrMaterial/StaticMeshRenderable | Shared mod assets remain engine-owned |
| Blade | SuperMeshRenderSystem.ClearBuckets/RenderTranslucencyPass; ShaderReference; HDR target | Draws after atmosphere using existing color/depth attachments; per-view submissions and sample/format-specific pipelines |
| Light | Program.UpdateRenderingResources prefix; LightSystem.CreateLightInstance | Runs after ClearLights and before forward/deferred light uploads |
| Audio | GameAudio, SoundBehavior, IChannel, SpatialAudio | Own channels only; SFX/master mixing respected; stops before mixer release |
| Contact | MeshViewModule.MeshView.PositionsCompare, part/body/Ego matrices | Double precision transforms; bounded triangle query; missing geometry does not damage |
| Separation | Vehicle.Split, Part.Connection/PartTree | Uses native topology transfer; unsupported graphs are left intact |
| Fragmentation | Exact private static PartFailure.IsolateAndDestroy(Vehicle,Part,List<Vehicle>) | Optional, disabled if signature is absent; no global explosion patches |
| Arbitrary geometry | PartModelModule templates and native host primitives | Closed authored solids only; interpolate attributes, cap and audit before replacing anything |
| Fragment upload | DeviceMeshInterleaved.Shared startup counters and native Bind | Reserve a bounded arena before binding; require the exact plain-field layout, never advance global counters at runtime |
| Molten caps | Private PartModel.AddInstance(PerInstanceData,PerInstanceDent,IViewport,int) | Changes emission on submitted generated cap instances only; saved caps reload cold |
| Generated physics | ConvexHullColliderTemplate, CustomMass, SpawnSubPartDebris, private Vehicle.BodyRates setter | Preflight native shapes, restore principal inertia and conserve CCI momentum before source deletion |
| Generated saves | XmlLoader.Load<AssetBundle> during ImmediateLoad; PartInstance.InstanceOf | Register immutable generated IDs before save validation; preserve Generated/Slices with affected saves |

Full path/symbol evidence and caveats are in the `research-*.md` documents, including the slicing geometry, native physics and actual native-asset feasibility reports. Flexo is reference material, not a runtime dependency. Unscience is likewise a source of demonstrated patterns; no Unscience assembly is required.

## Ownership and time

Simulation commands are applied after the preceding worker results and game input are committed, before the next nearest-orbit/physics workers start. UI and render callbacks never split vessels. The injected callback does not replace `GetJobSimStep`, avoiding the specific handoff seam used by Unscience. Future changes to the engine or another transpiler may still require compatibility review.

The blade/heat consume each applied simulation timestamp once, suspend at pause/warp, and reject large step gaps. Pose snapshots are captured from the main game view, in the kitten's body frame. Camera-relative floats are used only at final rendering submission. Audio uses the same body-space emitter plus the engine camera's spatial transform.

EVA guidance is local: the target must share the kitten's parent body, remain in the physics bubble, lie within its bounding radius plus 500 m and have at most 20 m/s COM relative speed. Ring flight additionally limits target spin and combined tracking demand against conservative native pulse authority. Capture searches target-body X/Y/Z axes; a selected station and entry are retained across engagement. COM or recursive part identity/transform changes invalidate the cached geometry. Direct approach, stopping and recovery segments are checked against the target snapshot; unsupported geometry or an obstructed route is refused. These checks do not establish structural cutting eligibility or plan a full orbital rendezvous.

On release, world reset, shutdown or a simulation exception, the controller clears its native translation command and restores its captured flight-computer settings. Pause/warp clears translation without advancing a cutting lap. The added managed geometry, snapshot and controller checks run production logic with stubs; native MMU physics, viewport interaction and end-to-end cuts still require the in-game acceptance checklist.

KSA owns the resulting split vessels and their native saves. The mod owns its current controlled-kitten equip state, transient heat/scars, channels and GPU pipelines. Equip is reset on a target/world change; no object pointers or GPU handles are serialized. User preferences use a bounded, validated JSON file and atomic replacement. Arbitrary slicing additionally persists immutable native GLB/XML definitions under `Generated/Slices`; those files are dependencies of saves that reference their generated IDs. Cache budgets refuse new work instead of evicting assets needed by older saves.

## Updating the game

1. Obtain one coherent new DLL/decomp snapshot; do not mix versions.
2. Inspect every integration above and update the exact frame seam/fragmentation signature checks first.
3. Build the entire mod, run managed and asset validation, and recompile both shaders.
4. Validate the staged ZIP contains no host/runtime assemblies.
5. Complete the native acceptance checklist before claiming support for the new snapshot.

Do not infer native runtime/platform support from a successful macOS managed compilation. The provided game assemblies are Windows x64; no KSA native game session has been exercised by the build checks alone.
