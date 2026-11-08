# Host metadata and frame-loop inspection

`tools/inspect_host.cs` reads the actual KSA and built mod PE files using `System.Reflection.Metadata` and `PEReader`. It does **not** use `Assembly.Load`, instantiate KSA/StarMap types, invoke native code, or require a Windows x64 runtime. This makes it usable on the current macOS ARM64 development host even though KSA.dll's PE machine is AMD64.

From the repository root, after building the mod with .NET 10:

```sh
dotnet build Purr2D2.csproj -p:KSAFolder="$KSA_DLL_DIR"
dotnet run --file tools/inspect_host.cs -- ../ksa-game-assemblies/current/dll/KSA.dll bin/Debug/net10.0/Purr2D2.dll
```

Use `--file` explicitly: without it, `dotnet run tools/inspect_host.cs` selects the existing repository project and treats the C# filename as an application argument.

## Checks performed

- Exact parameter/return types, visibility, static/instance flags and non-generic status for all patched methods: `Program.PrepareFrame`, `OnFrameEditor`, `OnDrawUiThreadSafe`, `OnMouseButton`, `OnCursorPos`, `OnScroll`, `OnFocus`, `OnKey`, `OnGameLoaded`, `Dispose`, `UpdateRenderingResources`, `ImGuiBackendGlfwImpl.OnMouseButton`/`OnScroll`, `Universe.DeserializeSave`, `SuperMeshRenderSystem.ClearBuckets` and `RenderTranslucencyPass`. Cursor slicing runs after `OnDrawUiThreadSafe`, where native main-view picking has been refreshed; `OnFrameViewports` is too early for current-frame hover hits.
- Render and input hook parameter names that Harmony binds by name.
- Actual host IL order for `RenderGame`, `RenderViewport` and `RenderEditor`: translucency follows the opaque scene and applicable atmosphere/MSAA composition, and precedes bloom or attachment resolve. This guards the late saber pass against host render-order changes; it does not execute GPU commands. Since 2026.10.25.5707, secondary viewports call `PlanetTransparenciesRenderer.RenderAtmosphereAndClouds` (formerly `RenderAtmosphereOnly`) at the same position, so their saber pass now also follows cloud composition.
- The private instance fields `KittenRenderable._characterAvatar : KSA.CharacterAvatar` and `Vehicle._threadWorkerUpdateState : KSA.VehicleUpdateState`. Somersault motion writes prepared-worker `ManualControlInputs`; no private `_manualControlInputs` binding remains.
- Native replacement physics members: `Vehicle.DepleteConsumables`, `UpdateVehicleConfiguration`, `UpdateAfterPartTreeModification`, private `AdoptSeparatedVehicle(Vehicle, PhysicsBubble, double3)`, `CreateVehicle`, `GenerateSplitId`, `PartTree.CreateFromNewPartTree`, `Part.StructuralPart` and `LookupCollection<T>.Deregister`. Quiet subpart breakup prepares authored subparts itself; `PartFailure.IsolateAndDestroy` is no longer a production binding.
- The public native `Vehicle.Split(Connection, IConnector, double, out PoseChange, string)` overload and `PartTree.BeginBulkChange` / `EndBulkChange` methods used by staged source isolation.
- The exact private PartModel submission overload used for generated cap cooling, and the complete plain-public-field layout of `DeviceMeshInterleaved` used to initialize objects inside the startup-reserved fragment arena without growing native counters at runtime.
- The actual decoded `Program.PrepareFrame` IL contains exactly one call to each apply/input/dispatch method in the production hook's required order. Vehicle membership refresh and dirty derived/resource flushes must run after the hook and before vehicle dispatch, so early isolation receives that engine follow-up before the next swap update. The inspector reads operands correctly, including two-byte opcodes and variable-size switch tables, and resolves call tokens through metadata. Negative controls ensure the same validator rejects missing, duplicate and reordered calls.
- Exactly one public concrete `[StarMapMod]` class, `Purr2D2.Mod`, a public parameterless constructor, and the three attributed public instance lifecycle callbacks: `BeforeMain()`, `AfterGui(double)` and `Unload()`, all void. `BeforeMain` calls the checked source/load hook installer exactly once. The public no-argument `AllModsLoaded()` initializer is invoked by a direct native postfix. The obsolete ImmediateLoad/AllModsLoaded attributes must be absent: KSA removed `Mod.PrepareSystems` and added an optional progress argument to `ModLibrary.LoadAll`, which the inspected StarMap wrapper forwards unchanged. The native `ReadSource`, `LoadAll`, `PrepareAll` and surviving UI loader seams are signature-checked.
- The built mod references KSA, StarMap.API and Harmony; its KSA assembly-reference version matches the inspected host. All assembly references are listed from metadata without resolving or loading them.

The tool exits `0` when the checks pass, `1` when inputs cannot be read or compatibility checks fail, and `2` for an invalid invocation. Failures name the changed method/field and the expected shape. Re-check the hook or rebuild against the intended snapshot instead of treating a changed host as supported.

## Verified result

The host is `KSA 2026.11.10.5801` (snapshot commit `4de698e8cf248c29fc95f188fb258775d6e574a3`, module MVID `a064c78d-2b9f-4ac3-ae4e-1cb22ed69344`), PE machine `Amd64`. The mod is `Purr2D2 0.2.0.0`, referencing `StarMap.API 0.3.6.0`, `0Harmony 2.4.2.0` and the matching KSA version. The final inspector passes 163 checks (`artifacts/checks/upgrade-5801/inspect-host-complete.log`). The old template Part constructor reflection failed before repair; it now binds `TransformDataReference<SubPartTemplate>` instead of `PartInstance`. The one-argument `Part(PartInstance)` save constructor also reaches the private initializer directly in 5801; its fourth explicit postfix preserves the exactly-once public-constructor coverage invariant. The early source hook and direct load completion callback replace the removed/changed startup seams. Decoded frame offsets remain identical to 5707:

| IL offset | Method |
| --- | --- |
| `00D4` | `Universe.ApplyOrbitSolvers()` |
| `00F2` | `Universe.ApplyVehicleSolvers()` |
| `0110` | `Universe.ApplyClothSolvers()` |
| `017B` | `InputEvents.ApplyInputEvents()` |
| `01A8` | `Program.RefreshVehiclesInFrame()` |
| `0263` | `Universe.ExecuteNextClothSolvers(double, SimStep)` |
| `0268` | `PartTree.FlushDirtyDerived()` |
| `026D` | `PartTree.FlushDirtyResourceManagers()` |
| `0274` | `Universe.ExecuteNextVehicleSolvers(double, SimStep)` |
| `027B` | `Universe.ExecuteNextOrbitSolvers(double, SimStep)` |

These results establish **metadata and IL layout compatibility**. They do not establish that Harmony accepts the production patches in KSA's actual runtime, that the transpiler's emitted result executes correctly, or that gameplay/rendering is correct. The real game acceptance checks remain necessary.

## Restricted-environment fallback

The file-based app command creates its SDK cache in the user's platform application-data directory. In this restricted workspace that cache write is denied. Validation was run using a temporary project outside the checkout that includes the same tracked `.cs` file; no additional project is tracked or shipped. The following creates that disposable launcher in `/tmp` and runs it:

```sh
python3 - <<'PY'
from pathlib import Path
from xml.sax.saxutils import escape
out = Path('/tmp/purr2d2-host-inspector')
out.mkdir(exist_ok=True)
source = escape(str(Path('tools/inspect_host.cs').resolve()), {'"': '&quot;'})
(out / 'HostInspector.csproj').write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup><Compile Include="{source}" Link="Program.cs" /></ItemGroup>
</Project>
''')
PY
dotnet run --project /tmp/purr2d2-host-inspector/HostInspector.csproj -- ../ksa-game-assemblies/current/dll/KSA.dll bin/Debug/net10.0/Purr2D2.dll
```
