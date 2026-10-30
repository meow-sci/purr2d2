---
name: upgrade-ksa
description: >-
  Upgrade or assess purr2d2 for an updated Kitten Space Agency game-assemblies snapshot.
  Compare old and new DLLs, decompiled code and Content; audit frame/worker Harmony hooks,
  reflection, GPU mesh growth, shaders, native slicing, poses, audio and save compatibility;
  repair confirmed regressions and run purr2d2's managed, host-contract, packaging and native
  acceptance checks. Use for KSA version bumps, game-update impact reviews or compatibility
  repairs in purr2d2, not routine feature development.
---

# Upgrade purr2d2 for a KSA update

Finish the requested upgrade, including confirmed runtime compatibility repairs and validation.
If the request is assessment-only, produce the impact review without changing production code.
A successful build is only one check: private bindings, IL assumptions, native ownership and
rendering can break while the entire mod compiles. Preserve feature behavior and durable user data;
do not obtain a green result by deleting features, weakening guards or suppressing failing tests.

Paths in commands are relative to the **purr2d2 repository root**. Markdown links resolve from
this skill. Read applicable repository instructions and the current files rather than treating
the inventory below as permanently exhaustive.

## 1. Establish the actual upgrade span

Read [COMPATIBILITY.md](../../../docs/COMPATIBILITY.md),
[host-validation.md](../../../docs/host-validation.md), the latest relevant entries in
[ACCEPTANCE.md](../../../docs/ACCEPTANCE.md), [README.md](../../../README.md),
[Directory.Build.props](../../../Directory.Build.props) and
[Purr2D2.csproj](../../../Purr2D2.csproj). Inspect `git status --short` first; preserve existing work.

Resolve these inputs from the request, local configuration, sibling checkouts and assembly-repo
history before asking for missing information:

| Input | Meaning |
|---|---|
| `NEW` | Coherent target snapshot: `version.json`, `dll/`, `decomp/`, and available `Content/` |
| `OLD` | Prior verified snapshot, preferably matching the recorded purr2d2 compatibility baseline |
| Recorded baseline | Version and assembly-repo commit in `docs/COMPATIBILITY.md`; distinguish build/managed validation from native acceptance |
| Live installation | Game, loader, Harmony and enabled mod versions used for native tests, if available |

A typical snapshot is `../ksa-game-assemblies/current/`; another checkout also has its own
`current/` directory. Accept other layouts. Resolve symlinks and absolute paths, read both version
files, and record snapshot commit, DLL assembly versions and relevant module MVIDs. The initial
recorded baseline for this skill is **KSA 2026.10.19.5638**, assembly-repo commit
`edda84495fcdc5592c80e1e6442573cd3419354a`, StarMap.API **0.3.6**, Harmony **2.4.2**.
These identify a historical baseline, not a restriction to that version or a native support claim.

If `OLD` is absent but its commit exists locally, extract the needed historical files to a disposable
directory with `git show`/`git archive`; do not switch or modify the read-only reference checkout.
If the available `OLD` differs from the recorded baseline, review the intervening span too. With
only `NEW`, proceed with builds, host checks and current-source inspection, but report the missing
drift evidence. Missing proprietary GLBs or a runnable game limit specific checks, not all work.

Report the resolved build pair, baseline gaps and the DLL set to be used before repairing anything.
Keep DLLs/decomp/Content from the same build; when a Content snapshot omits binaries, identify the
source and version of any installation assets used to supplement it. Do not substitute older assets
without proving equivalence for the files under test.

## 2. Rediscover the complete integration surface

Purr2d2 has **one runtime project**, `Purr2D2.csproj`, and direct game access across many `src/`
folders. There is no gatOS-style accessor firewall, `[KsaAnchor]` census or `scope/` tree here.
Use the compatibility table plus production code as the inventory, not a sibling mod's checklist.

Read [references/integration-audit.md](references/integration-audit.md) in full for every upgrade.
It maps the current subsystems, fragile bindings and review targets. Extend that inventory for
newly added features. Useful searches from the repository root:

```sh
rg -n 'using (KSA|Brutal|RenderCore|Core|StarMap|HarmonyLib)|KSA\.' src Shared
rg -n 'AccessTools|BindingFlags|GetField\(|GetProperty\(|GetMethod\(|GetNestedType\(|CreateDelegate|FieldRef|GetType\(' src Shared
rg -n 'harmony\.Patch|HarmonyMethod|Transpil|___[A-Za-z]|StructLayout|FieldOffset|ModuleVersionId|Audited|2026\.' src Shared tests tools docs Purr2D2.csproj
```

Search method bodies too: `nameof` patch targets, `Require(...)` helpers, dynamically constructed
type names, native shader IDs, bone/mesh names and callback parameter names escape a simple
string-reflection search. Follow every `Install` path from `src/Mod.cs` and runtime initialization,
including optional groups that catch exceptions and merely report unavailable status.

Read `NEW/version.json` and `OLD/version.json` and inspect `fromRevision`, `toRevision` and
`commits[]`; do not assume cumulative logs. Filter for frame/job/input, EVA/locomotion/animation,
Part/PartTree/breakup, Bepu/bubbles/clutter, saves/roster, mesh/render/Vulkan/ray tracing,
lighting/shaders, FMOD and Brutal changes. Compare relevant source and Content even without a
changelog hit. Record uncovered revision gaps; changelog silence does not clear a subsystem.

For each integration row, record mod site, old/new host evidence, result and required runtime
check. Distinguish a new regression, an existing defect and a historical limitation. An unchanged
signature still needs review for units, coordinate frames, gating, worker ownership and lifetime.

## 3. Compile against NEW, without accidentally testing OLD

Inspect the current props, local overrides and test scripts first. At this skill's creation:

- `Local.Build.props` is imported first; an explicit `KSAFolder` wins over the fallback to
  `KSA_DLL_DIR`, then `../ksa-game-assemblies/current/dll`. Use a command-line property to make the
  build unambiguous. Unlike unscience, purr2d2 joins paths with `/`; a trailing separator is not required.
- `ValidateHostReferences` requires the complete `HostAssembly` list. Diff DLL filenames and
  assembly identities before diagnosing hundreds of missing-namespace errors. Assembly splits or
  renames can require updating references and test generators while namespaces stay unchanged.
- `TreatWarningsAsErrors=true`; distinguish nullability/toolchain drift, missing dependencies,
  restore problems and packaging failures from game API changes.
- Release recreates **local** stage/ZIP/checksum outputs; it does not install into the live game.
  The solution has no test project; `dotnet test` is not the regression suite.

Set `KSA_DLL_DIR` to the absolute `NEW/dll` directory for Python host checks as well. Example (POSIX
shell; substitute the resolved path, and adapt syntax for PowerShell):

```sh
export KSA_DLL_DIR='/absolute/path/to/new-snapshot/dll'
dotnet build Purr2D2.slnx -c Debug -p:KSAFolder="$KSA_DLL_DIR"
dotnet build Purr2D2.slnx -c Release -p:KSAFolder="$KSA_DLL_DIR"
```

**Test-path trap:** `tools/validate.py::_run_managed_project` currently hard-codes the sibling
`../ksa-game-assemblies/current/dll` for Numerics/Bepu and extra host references; it does **not**
honor `KSA_DLL_DIR`. `tools/check_stock_slices.py` uses that helper too. Confirm that path resolves
to `NEW`. If it does not, adapt the helper to accept the target directory consistently (retaining
the default), or run from an isolated writable layout with matching sibling references. Never
repoint or mutate a read-only reference checkout. Inspect generated harness HintPaths before
crediting their results to `NEW`; environment variables alone do not prove which DLLs were used.

Rebuild generated harnesses from current source lists. Cached `--no-build` executables, previously
generated projects and old PE-adapted host copies are not new-version evidence. If references or
dependency versions changed, refresh restore artifacts; do not rely on stale `--no-restore` output.

## 4. Repair silent and semantic breaks

Work through **every section** of the integration audit, including render/GPU and reflection
checks on a green build. Confirm exact overloads, static/instance and return types, visibility,
field types, Harmony argument names, nested generic types, branch/control flow and native ordering.
Where decomp and the actual DLL disagree, PE metadata/IL and the running target DLL are authoritative.

Prioritize the settled frame seam, prepared-worker handoff and scene-replacement ordering, then
native replacement/mesh growth, then the remaining subsystems. Preserve the original simulation
semantics and diagnostic-only nature of worker instrumentation. Update compatibility tests to
assert the new proven contract, including rejection of ambiguous/missing/reordered IL and unchanged
behavior for unowned objects. Do not just edit golden strings or expected instruction counts.

**Mesh growth has an intentional version lock.**
`src/Rendering/NativeMeshGrowthHost.cs` pins MVIDs of five modules, not just KSA's display version.
Audit the full shared-buffer/queue/ray-tracing transaction described in the reference before
changing those GUIDs. Record old/new identities and supporting method/IL evidence. Keep rejection
tests for unaudited modules. If the transaction cannot be validated, retain the guard and report
mesh growth as unavailable; do not silently certify the entire upgrade or force-enable it.

For an authorized upgrade, make confirmed fixes and the corresponding validation/doc changes in
the same work. Broader redesign or altered gameplay warrants clarification; routine compatible
repairs do not need a second approval. Reference mods and the KSA coffer provide patterns, not
authority for NEW signatures. Locate and read the available KSA modding skill when deeper engine
research is needed; its corpus may target an older build. Do not copy the sibling skills' stale
`scope/` links, fixed project counts, obsolete API names or approval gates.

## 5. Run the verification matrix

Use .NET 10 and `glslangValidator` (PATH or `VULKAN_SDK`; see README). Save commands, actual target
paths, exit codes and output under ignored `artifacts/checks/` with an upgrade-specific prefix.
Run the following against the final repaired sources and a freshly rebuilt Release payload;
inspect failures individually rather than assuming an aggregate PASS-line count proves success:

```sh
python3 tools/validate.py --run-tests --stage artifacts/stage/purr2d2
dotnet run --file tools/inspect_host.cs -- "$KSA_DLL_DIR/KSA.dll" bin/Release/net10.0/Purr2D2.dll
python3 tools/check_fragment_templates.py
python3 tools/check_force_host.py
python3 tools/check_mesh_growth.py
python3 tools/check_mesh_growth_host.py
python3 tools/check_cape_cloth.py
python3 tools/check_runtime_bootstrap.py --entry-assembly bin/Release/net10.0/Purr2D2.dll
python3 tools/check_slice_warmup.py --host-dll-dir "$KSA_DLL_DIR" --configuration Release
```

| Check | Evidence and limits |
|---|---|
| `validate.py --run-tests` | Source/stage manifests, assets, isolated managed harnesses, source/staged Light/Dark shaders and reflected 128-byte push-constant ABI. Many gameplay tests use stubs, not actual host hooks. Harness count changes: report the actual run. |
| `inspect_host.cs` | Real PE signatures, selected private fields, entry lifecycle and frame/render IL ordering. No game execution. Use the disposable-project fallback in `docs/host-validation.md` if file-app cache creation is unavailable. |
| `check_fragment_templates.py` | Real managed XML loader/export preservation, production settled-frame transform, quiet module callers, worker diagnostics and guarded environment-light IL. Read its current source list and tests to establish coverage. |
| `check_force_host.py` | Actual worker-handoff composition, clutter reflection/promotion/contact contracts and managed Bepu checks; not a running native world. |
| `check_mesh_growth.py` / `check_mesh_growth_host.py` | Pure allocation/transaction cases plus production host identities, queue/render ordering, buffer/descriptor IL. Neither executes Vulkan. |
| `check_cape_cloth.py` | Actual host GLB/skin/skeleton decoding and CPU cloth checks/benchmark; not native fit or draw acceptance. |
| `check_runtime_bootstrap.py` | Fresh-process networking binding regressions and built entry initializer. If peers are available, repeat `--peer-repo <path>` for gatOS/unscience to check shared-source/harness parity; read `Shared/README.md` before changing that shared policy. |
| `check_slice_warmup.py` | Production method selection, bounded JIT graph and cancellation, not game behavior. Use repeated `--dependency-dir <path>` only for identified matching managed dependencies absent from the snapshot. Report such dependencies (e.g. historical MathNet.Numerics), not a game API failure. |

For stock geometry, run `python3 tools/check_stock_slices.py /absolute/path/to/NEW/Content/Core`
when the matching mesh atlases exist. If only `--fixture artifacts/checks/stock-slices.json` is
available, identify its build/provenance and report it as historical geometry regression coverage,
not validation of NEW stock meshes. Keep proprietary fixtures ignored and out of the payload.

The host scripts may adapt PE machine flags of **ignored unsigned IL-only test copies** on ARM.
Never edit source snapshot DLLs, ship adapted copies, or relax adaptation checks for mixed/native
images. If NEW changes that assumption, use metadata-only inspection or a compatible runtime.

After the final Release build, validate package integrity separately: derive the archive name from
the project version; require matching `mod.toml`, exactly one `purr2d2/` root, all ZIP files identical
to stage, staged DLL identical to `bin/Release/net10.0/Purr2D2.dll`, and matching `.zip.sha256`.
The sole shipped assembly is Purr2D2.dll: no KSA/Brutal/Planet/Bepu, StarMap/Harmony, runtime DLLs,
test artifacts or proprietary stock fixtures. Do not infer ZIP integrity from stage validation.

## 6. Native acceptance and durable-data checks

Use a matching supported game installation and disposable copies of saves for destructive tests.
Read the latest applicable `docs/ACCEPTANCE.md` checklists; historical pending items stay pending
until observed. Minimum upgrade pass:

- **Startup/lifecycle:** standalone and Unscience co-load in both installation orders; no swallowed
  patch failures, broken startup bindings or duplicate updates. Check unavailable feature statuses,
  pause/resume, warp, editor/HUD-hidden paths, repeated enable/disable, scene changes and shutdown.
- **Equipment/render/audio:** multiple controlled/background kittens, boarding/disembarking,
  Light/Dark blades, fractional deployment, manual/automatic swings, clashes and expressions,
  somersaults, Furce hand and optional capes together. Check locomotion, hilt/paw alignment, colors,
  water/atmosphere/depth/secondary views, lights off/on/off, spatial cues/loops and cleanup.
- **Input/motion/Furce:** camera/ImGui/modal/focus routing, Space for owned/unowned EVA and an Iron
  Man-mode kitten, jump combos and MMU visibility; vessel/clutter capture, radius/throw/spread,
  release/disposal/bubble changes and staged breakup. Unowned native controls and physics stay intact.
- **Slicing:** assisted selection and intentional manual contact, moved/stale targets, true cuts
  and backed panels, native subpart fallback, glass/materials, cooling caps, small/large stock parts
  and repeated exported/reloaded cuts. Check mass/inertia/CCI motion, quiet isolation/cancellation,
  connected/docked/occupied vessels, terrain/unrelated collisions and budget refusal without loss.
- **Resources:** force arena exhaustion/growth with small initial settings; test ray tracing both
  off/on, multiple growths, cancellation, renderer rebuild/resize, secondary views, shadows,
  thumbnails and picking. Exercise failure/rollback with Vulkan synchronization validation when
  available. Record frame time, worker diagnostics and resource counts; managed timing is not FPS.
- **Saves:** old and newly generated fragments after restart, overwritten saves and A → B → A,
  native-only/direct/new-system loads, deferred/rejected/skipped/failed operations, disabled/seated
  kitten saves, missing/invalid sidecars and identity/seat conflicts. Choices return only to their
  owners; transient holds, gestures, queued cuts/boosts and old world pointers never resume.

Preserve installed `settings.json`, native saves, per-save `purr2d2.json`, and **all**
`Generated/Slices/` XML/GLBs. Generated IDs are immutable save dependencies, not an evictable cache.
Do not test cleanup by deleting them or replace an installed mod folder with the clean stage
without retaining that data. A native save format change requires checking the sidecar's
`universe.xml` hash/pairing and roster/seat identity rules, not just JSON deserialization.

If native access is unavailable, complete the static/managed/package work and explicitly list the
remaining acceptance scenarios. Report “managed/host checks passed; native acceptance pending”
instead of claiming full game/platform support. Never convert pending checks into passes.

## 7. Update the compatibility record and finish

Update documentation in lockstep with confirmed integration changes:

- `docs/COMPATIBILITY.md`: target build/commit, integration table, guards/degraded behavior and
  upgrade guidance; distinguish statically audited, managed-tested and natively accepted surfaces.
- `docs/host-validation.md`: actual DLL/entry versions, inspected signatures and current IL ordering
  evidence; update the tool when required coverage changes, not just the prose.
- `docs/ACCEPTANCE.md`: append dated results, commands/logs, tested versions/load order, failures,
  skipped checks and pending native work; preserve historical observations and supersede them
  explicitly. Do not rewrite prior runs as successful.
- `README.md` and affected `docs/research-*.md`, `docs/persistence.md`, `docs/somersault.md`,
  `docs/force-breakup.md` or performance docs when their current contracts changed. Reconcile older
  plans with implemented behavior rather than taking historical proposals as today's specification.
- This skill/reference inventory, host-version diagnostics and test generators when the upgrade
  changes their assumptions. Search old version strings/GUIDs; update active contracts while
  preserving dated historical evidence. Keep project/manifest versions aligned if a mod-version
  change is part of the task; a game update alone does not mandate a package or loader bump.

Final report: OLD/NEW/recorded baseline and real DLL paths; relevant revision span/gaps; findings
with mod locations and old/new host evidence; fixes and any unavailable features; actual build,
managed/host/shader/package results; durable-data implications; and remaining native work. Explain
pre-existing failures separately from update regressions. The upgrade is fully validated only
when the required native pass also succeeds. Do not install, publish, commit or modify sibling
reference repos unless that work was separately requested.
