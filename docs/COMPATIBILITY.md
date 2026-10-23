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
| EVA target selection | `Vehicle.Target`, `Vehicle.SetTarget(IOrbiter)` | Mod dropdown queues changes through the settled handoff; scopes the explicit slice without requiring stock EVA flight-computer buttons |
| Slice marker | Private `Program.OnFrameViewports(double)` postfix; main camera `EgoToScreen` | HUD-visible marker identifies the locked part during the swing |
| World reset | `Universe.DeserializeSave(UniverseData)` prefix and `Program.OnGameLoaded()` postfix | Discards queued commands, old owners, channels, slice requests and sparks |
| Shutdown | `Program.Dispose()` prefix | Releases owned resources before FMOD/Vulkan teardown; StarMap unload is idempotent |
| Avatar access | `KittenEva.Renderable`, private `KittenRenderable._characterAvatar` | Cached reflection; missing field/rig disables equip with status |
| Pose | `CharacterModel.AnimProcessors`, `IAnimProcessor` local/skeleton callbacks | Updates arm only; removes only its own processor |
| Hilt | Native GltfFile/PbrMaterial/StaticMeshRenderable | Shared mod assets remain engine-owned |
| Blade | SuperMeshRenderSystem.ClearBuckets/RenderTranslucencyPass; ShaderReference; HDR target | Draws after atmosphere using existing color/depth attachments; per-view submissions and sample/format-specific pipelines |
| Light | Program.UpdateRenderingResources prefix; LightSystem.CreateLightInstance | Runs after ClearLights and before forward/deferred light uploads |
| Audio | GameAudio, SoundBehavior, IChannel, SpatialAudio | Own channels only; SFX/master mixing respected; stops before mixer release |
| Contact | Static/dynamic/glass native render meshes, PositionsCompare, part/body/Ego matrices | Double precision transforms; 20 Hz bounded visual-only triangle query; no damage callback |
| Separation | Vehicle.Split, Part.Connection/PartTree | Uses native topology transfer; unsupported graphs are left intact |
| Arbitrary geometry | PartModelModule templates and native host primitives | Closed authored solids and backed open panels; interpolate attributes, cap and audit before replacement |
| Fragment upload | DeviceMeshInterleaved.Shared startup counters and native Bind | Reserve a bounded arena before binding; require the exact plain-field layout, never advance global counters at runtime |
| Molten caps | Private PartModel.AddInstance(PerInstanceData,PerInstanceDent,IViewport,int) | Changes emission on submitted generated cap instances only; saved caps reload cold |
| Generated physics | ConvexHullColliderTemplate, CustomMass, PartTree.CreateFromNewPartTree, Vehicle.CreateVehicle, private AdoptSeparatedVehicle and _threadWorkerUpdateState, private BodyRates setter | Stage native roots/trees once, adopt into the existing bubble, retain principal inertia and conserve CCI momentum before source deletion |
| Generated saves | XmlLoader.Load<AssetBundle> during ImmediateLoad; PartInstance.InstanceOf | Register immutable generated IDs before save validation; preserve Generated/Slices with affected saves |

Full path/symbol evidence and caveats are in the `research-*.md` documents, including the slicing geometry, native physics and actual native-asset feasibility reports. Flexo is reference material, not a runtime dependency. Unscience is likewise a source of demonstrated patterns; no Unscience assembly is required.

## Ownership and time

Simulation commands are applied after the preceding worker results and game input are committed, before the next nearest-orbit/physics workers start. UI and render callbacks never split vessels. The injected callback does not replace `GetJobSimStep`, avoiding the specific handoff seam used by Unscience. Future changes to the engine or another transpiler may still require compatibility review.

The blade consumes each applied simulation timestamp once, suspends when paused or above 5x, and rejects large step gaps. The explicit swing advances in player time only with a fresh visible pose and a valid applied simulation step. Pose snapshots are captured from the main game view, in the kitten's body frame. Camera-relative floats are used only at final rendering submission. Audio uses the same body-space emitter plus the engine camera's spatial transform.

Only the explicit swing can prepare or commit a structural cut. Its locked target must remain in reach, retain its ownership/transform, and pass the crew and native physics checks at commit. The cosmetic contact probe is independent and cannot initiate a cut. Autopilot and flight-control overrides have been removed.

KSA owns the resulting split vessels and their native saves. The mod owns its current controlled-kitten equip state, transient sparks and cap heat, channels and GPU pipelines. Equip is reset on a target/world change; no object pointers or GPU handles are serialized. User preferences use a bounded, validated JSON file and atomic replacement. Arbitrary slicing additionally persists immutable native GLB/XML definitions under `Generated/Slices`; those files are dependencies of saves that reference their generated IDs. Cache budgets refuse new work instead of evicting assets needed by older saves.

## Updating the game

1. Obtain one coherent new DLL/decomp snapshot; do not mix versions.
2. Inspect every integration above and update the exact frame seam/native replacement signature checks first.
3. Build the entire mod, run managed and asset validation, and recompile both shaders.
4. Validate the staged ZIP contains no host/runtime assemblies.
5. Complete the native acceptance checklist before claiming support for the new snapshot.

Do not infer native runtime/platform support from a successful macOS managed compilation. The provided game assemblies are Windows x64; no KSA native game session has been exercised by the build checks alone.
