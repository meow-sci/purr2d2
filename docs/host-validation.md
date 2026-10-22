# Host metadata and frame-loop inspection

`tools/inspect_host.cs` reads the actual KSA and built mod PE files using `System.Reflection.Metadata` and `PEReader`. It does **not** use `Assembly.Load`, instantiate KSA/StarMap types, invoke native code, or require a Windows x64 runtime. This makes it usable on the current macOS ARM64 development host even though KSA.dll's PE machine is AMD64.

From the repository root, after building the mod with .NET 10:

```sh
dotnet build Purr2D2.csproj --no-restore
dotnet run --file tools/inspect_host.cs -- ../ksa-game-assemblies/current/dll/KSA.dll bin/Debug/net10.0/Purr2D2.dll
```

Use `--file` explicitly: without it, `dotnet run tools/inspect_host.cs` selects the existing repository project and treats the C# filename as an application argument.

## Checks performed

- Exact parameter/return types, visibility, static/instance flags and non-generic status for all patched methods: `Program.PrepareFrame`, `OnFrameEditor`, `OnGameLoaded`, `Dispose`, `UpdateRenderingResources`, `Universe.DeserializeSave`, `SuperMeshRenderSystem.ClearBuckets` and `RenderMainPass`.
- Render-hook parameter names that Harmony binds by name.
- The private instance field `KittenRenderable._characterAvatar : KSA.CharacterAvatar`.
- The private instance field `Vehicle._manualControlInputs : KSA.ManualControlInputs` used to publish native EVA MMU pulse commands.
- The internal static native fragmentation method `PartFailure.IsolateAndDestroy(Vehicle, Part, List<Vehicle>) : void`.
- The exact private PartModel submission overload used for generated cap cooling, and the complete plain-public-field layout of `DeviceMeshInterleaved` used to initialize objects inside the startup-reserved fragment arena without growing native counters at runtime.
- The actual decoded `Program.PrepareFrame` IL contains exactly one call to each apply/input/dispatch method in the production hook's required order. The inspector reads operands correctly, including two-byte opcodes and variable-size switch tables, and resolves call tokens through metadata. Negative controls ensure the same validator rejects missing, duplicate and reordered calls.
- Exactly one public concrete `[StarMapMod]` class, `Purr2D2.Mod`, a public parameterless constructor, and the four expected public instance lifecycle callbacks with their actual full attribute names/signatures: `ImmediateLoad(KSA.Mod)`, `AllModsLoaded()`, `AfterGui(double)` and `Unload()`, all returning void.
- The built mod references KSA, StarMap.API and Harmony; its KSA assembly-reference version matches the inspected host. All assembly references are listed from metadata without resolving or loading them.

The tool exits `0` when the checks pass, `1` when inputs cannot be read or compatibility checks fail, and `2` for an invalid invocation. Failures name the changed method/field and the expected shape. Re-check the hook or rebuild against the intended snapshot instead of treating a changed host as supported.

## Verified result

The local host reports `KSA 2026.10.19.5638`, PE machine `Amd64`. The built mod reports `Purr2D2 0.2.0.0`; references include `StarMap.API 0.3.6.0`, `0Harmony 2.4.2.0`, and the matching KSA version. All checks passed. The decoded frame call positions were:

| IL offset | Method |
| --- | --- |
| `00D4` | `Universe.ApplyOrbitSolvers()` |
| `00F2` | `Universe.ApplyVehicleSolvers()` |
| `0110` | `Universe.ApplyClothSolvers()` |
| `017B` | `InputEvents.ApplyInputEvents()` |
| `0263` | `Universe.ExecuteNextClothSolvers(double, SimStep)` |
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
