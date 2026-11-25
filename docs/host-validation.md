# Host metadata and frame-loop inspection

`tools/inspect_host.cs` reads the actual KSA and built mod PE files using `System.Reflection.Metadata` and `PEReader`. It does **not** use `Assembly.Load`, instantiate KSA/StarMap types, invoke native code, or require a Windows x64 runtime. This makes it usable on the current macOS ARM64 development host even though KSA.dll's PE machine is AMD64.

From the repository root, after building the mod with .NET 10:

```sh
dotnet build Purr2D2.csproj -p:KSAFolder="$KSA_DLL_DIR"
dotnet run --file tools/inspect_host.cs -- "$KSA_DLL_DIR/KSA.dll" bin/Debug/net10.0/Purr2D2.dll
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
- Save/load seams added for 5949 (rev 5916 live system switching): `UncompressedSave.Load` hands the private `ApplyLoaded` to `SaveSystemSwap.RequestLoad` via a function pointer and never reconstructs directly. `ApplyLoaded` calls `Universe.DeserializeSave` once and then `Program.OnGameLoaded`. `SaveSystemSwap.OnFrame` calls `Program.SwapSystem` before invoking the queued load, or `SaveSystemRestart.RestartInto`. `SwapSystem` joins jobs, destroys vehicles and bubbles, then calls `Universe.CreateSystem`, without `LoadSystem`. `RestartInto` calls `Environment.Exit`. The checks also cover the `GameSave.Populate`/`UncompressedSave.Write` signatures, the Harmony-bound `LoadSystem(id)` and `DeserializeSave(universeData)` names, and `ArrivalService.Update` evaluating vehicles through the private `Check(Tracked, double, long)` used by the dropped-hilt filter. Against 5901 these checks fail (`artifacts/checks/upgrade-5949/inspect-host-old-negative.log`).
- Exactly one public concrete `[StarMapMod]` class, `Purr2D2.Mod`, a public parameterless constructor, and the three attributed public instance lifecycle callbacks: `BeforeMain()`, `AfterGui(double)` and `Unload()`, all void. `BeforeMain` calls the checked source/load hook installer exactly once. The public no-argument `AllModsLoaded()` initializer is invoked by a direct native postfix. The obsolete ImmediateLoad/AllModsLoaded attributes must be absent: KSA removed `Mod.PrepareSystems` and added an optional progress argument to `ModLibrary.LoadAll`, which the inspected StarMap wrapper forwards unchanged. The native `ReadSource`, `LoadAll`, `PrepareAll` and surviving UI loader seams are signature-checked.
- The built mod references KSA, StarMap.API and Harmony; its KSA assembly-reference version matches the inspected host. All assembly references are listed from metadata without resolving or loading them.

The tool exits `0` when the checks pass, `1` when inputs cannot be read or compatibility checks fail, and `2` for an invalid invocation. Failures name the changed method/field and the expected shape. Re-check the hook or rebuild against the intended snapshot instead of treating a changed host as supported.

## Verified result

The host is `KSA 2026.11.25.5949` (snapshot commit `618c2a1370df423099e297b20382aa419a4b04d3`, module MVID `1e1e0a8c-ba04-4f83-bfc4-663993881b08`), PE machine `Amd64`. The mod is `Purr2D2 0.2.0.0`, referencing `StarMap.API 0.3.6.0`, `0Harmony 2.4.2.0` and the matching KSA version. The inspector passes with 167 PASS lines on the unmodified sources and again on the final Release build (`artifacts/checks/upgrade-5949/inspect-host-initial.log`, `inspect-host-final.log`); no signature, private-field, constructor-chain, lifecycle or render-order check needed a repair for 5949. The four public `Part` constructors and the BeforeMain/ReadSource/LoadAll startup seams are unchanged from 5801, and every `Program.PrepareFrame` offset below is identical to 5901.

The previous record was 5901 (`8d2ad13693adc15005e2f110c42fa6575f70fd40`, MVID `ded30488-a614-431f-ab2e-8c395b14bb59`). 5901 inserted two native calls into `Program.PrepareFrame`, so offsets after them moved while the required order is unchanged. `KfsIntegration.BeforeInputEvents()` (a no-op unless the kFS prototype is enabled) now precedes `InputEvents.ApplyInputEvents()`; Purr2D2's settled update still runs immediately after input. `Editor?.UpdateAerodynamics()` (vehicle editor only) now runs between cloth dispatch and `PartTree.FlushDirtyDerived()`; the transformation requires order, uniqueness and stack shape, not adjacency, and no structural mutation moves across a worker boundary:

| IL offset (5801 → 5901 = 5949) | Method |
| --- | --- |
| `00D4` | `Universe.ApplyOrbitSolvers()` |
| `00F2` | `Universe.ApplyVehicleSolvers()` |
| `0110` | `Universe.ApplyClothSolvers()` |
| `017B` → `0180` | `InputEvents.ApplyInputEvents()` |
| `01A8` → `01AD` | `Program.RefreshVehiclesInFrame()` |
| `0263` → `0268` | `Universe.ExecuteNextClothSolvers(double, SimStep)` |
| `0268` → `027D` | `PartTree.FlushDirtyDerived()` |
| `026D` → `0282` | `PartTree.FlushDirtyResourceManagers()` |
| `0274` → `0289` | `Universe.ExecuteNextVehicleSolvers(double, SimStep)` |
| `027B` → `0290` | `Universe.ExecuteNextOrbitSolvers(double, SimStep)` |

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
