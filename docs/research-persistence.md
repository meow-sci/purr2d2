# Per kitten customization with native game saves

Purr2D2 can save each kitten's saber color, Light/Dark choice, cape preference and equipment intent alongside a native KSA save. Unscience supplies the working storage and restore pattern. The necessary Purr2D2 change is to separate durable kitten profiles from disposable EVA, pose, audio and cape objects, then rebind those profiles after KSA reconstructs the scene.

This source investigation informed the implemented persistence feature. See [the current save format, runtime behavior and verification](persistence.md); the design below records the research and alternatives, including the originally proposed feature wrapper that the smaller implementation replaces with versioned kitten records. Sources were checked on 2026-10-25 against Purr2D2 `589de8258848a814eef25ad734c45ea4e5e2c803`, Unscience `1829b3b298dd0fa5a67342cafff29074c8c29286` and game assemblies `edda84495fcdc5592c80e1e6442573cd3419354a` (KSA **2026.10.19.5638**). The coffer's older persistence and kitten-restoration articles supplied navigation; current local code supplies the contracts below.

## How Unscience saves and restores

The entry point is [UnscienceSaves](../../unscience/unscience/UnscienceSaves.cs), backed by [the persistence library](../../unscience/ksa-abstractions.lib/Persistence/). Each feature implements `ISaveParticipant`, or exposes several participants through `ISaveParticipantSource`.

| Stage | Source and verified behavior |
| --- | --- |
| Capture | `NativeSaveHooks.cs:78-82` observes completed `GameSave.Populate()`. `UnscienceSaves.cs:17,35-39` captures detached feature DTOs and associates the result with the exact native save object through `ConditionalWeakTable<GameSave, SaveDocument>`. |
| Publish | `NativeSaveHooks.cs:85-92` publishes only after the original `UncompressedSave.Write()` returns `true`. `UnscienceSaves.cs:48-67` requires a matching earlier capture; a `false` native return discards its capture. It never substitutes whatever state happens to exist later. |
| Store | `SaveStorage.cs:12-50` writes `unscience.json` beside `universe.xml`, hashes the completed XML, flushes a same-directory temporary file and replaces the sidecar. |
| Prepare a load | `NativeSaveHooks.cs:95-109` defers a file load to Unscience's frame boundary, then invokes `Loading`. `UnscienceSaves.cs:70-77` reads the sidecar and prepares restoration without changing the world. |
| Release old ownership | `NativeSaveHooks.cs:137-151,181-195` validates native prerequisites, joins workers, clears pending old-world edits and resets participants before native destruction. Cleanup runs in reverse participant order. |
| Replay | `NativeSaveHooks.cs:154-156` invokes restore after native deserialization. `SceneSaveCoordinator.cs:120-145` replays prepared actions in restore order, once, and retains records that fail or warn. |
| Finish or fail | `NativeSaveHooks.cs:112-134` and `SceneSaveCoordinator.cs:148-164` unwind transaction context. An unreadable native file returns without reconstruction; the old scene must survive. |

The participant contract in [ISaveParticipant.cs](../../unscience/ksa-abstractions.lib/Persistence/ISaveParticipant.cs) separates `CaptureState()`, synchronous `ResetState()` and `PrepareRestore()`. Preparation validates detached data and returns an action to run against the reconstructed world. It does not retain old game objects for replay.

The document in `SaveDocument.cs:7-20` has a document schema version, the `UniverseSha256`, a creation timestamp, warnings and versioned feature records. `SaveStorage` bounds size to 32 MiB, rejects duplicate JSON properties and unsupported document versions, and checks the XML hash before restore. `SaveJson` also bounds depth and validates numeric serialization. Purr2D2's much smaller records can use tighter size and entry limits.

Missing sidecars mean a native-only scene with default mod choices. A corrupt, mismatched or unsupported whole document is ignored with a warning. For a valid document, unknown feature versions and invalid or incompletely restored feature records are retained as opaque payloads for subsequent saves. This distinction matters: malformed whole files are not promised the same recovery as valid feature records. Unscience retains an entire feature record on a restore warning; that record takes precedence over current edits until the user explicitly chooses the current setup (`SceneSaveCoordinator.cs:35-54,61-93,120-170`).

Atomic sidecar replacement does not make the native save and JSON one transaction. Native overwrite replaces the save directory before writing its files; a crash or later sidecar failure can leave a native-only save. Sidecars must be written after every successful native overwrite, and the whole save directory must be copied together. Asset libraries are separate dependencies.

## What Purr2D2 currently loses

`SaberRuntime.Kittens.cs:14,34-55` stores per-kitten choices together with live resources in a dictionary keyed by `KittenEva` reference. Color, `SaberType`, cape opt-in and equipment intent are not part of `SaberSettings`. `RefreshOverlayKittens()` disposes and removes a record when its EVA disappears (`:139-154`); `GetKittenSaber()` gives a new EVA its next palette color and default Light type (`:158-166`).

`SaberRuntime.ResetWorld()` calls `ResetKittenSabers()` and clears gestures and effects (`SaberRuntime.cs:217-227`). Native reconstruction invokes cleanup through `GameHooks.BeforeDeserializeSave`, and `Program.OnGameLoaded` invokes it again (`GameHooks.cs:63-66,232-250`). The first settled update also resets when the system reference changes (`SaberRuntime.cs:87-90`). Those are valid resource-lifetime behaviors, but cannot also own durable profile deletion.

Global preferences already have their own path: `settings.json` beside the installed mod, loaded at `SaberRuntime.cs:47-54`, observed by `SaberSettingsAutosaver`, and flushed at shutdown. Preserve that role for shared effects, alignment, volume, UI and performance limits.

## Kitten identity across boarding and loading

KSA has no immutable per-person UUID in the current native roster. [KittenRosterEntryData.cs:10-17](../../ksa-game-assemblies/current/decomp/KSA/KittenRosterEntryData.cs) serializes `Name`, `Character` and `AssignedVehicleId`. `NameHash` is not serialized; it is recomputed from the name (`:37-38,68-70`). `KittenRosterData.cs:64-110` generates unique roster names and selects character assets from a shared catalog. A character asset identifies an archetype, not one kitten.

The native identity transitions are decisive:

- `KittenEva.BoardVehicle()` uses the EVA `Id` to locate the roster member, assigns a seat and destroys the EVA object with `CrewDisposition.Boarded` (`KittenEva.cs:891-914`).
- Disembarking constructs a new `KittenEva` with `id: rosterEntry.Name` and `characterId: rosterEntry.Character` (`EVADoor.cs:195-209`).
- `Vehicle.SerializeSave()` writes the vehicle ID (`Vehicle.cs:1173`); `KittenEva.SerializeSave()` writes its character (`KittenEva.cs:1055`). `CreateKittenFromSaveData()` constructs a new EVA/renderable from the saved identity (`KittenEva.cs:101`).
- Seated kittens are represented by roster assignments and a separate, lazily created renderable (`IVASeat.cs:79,146`), not individual EVA vehicles.
- `Universe.DeserializeSave()` installs the reconstructed vehicles and incoming roster before returning (`Universe.cs:2419-2472`). Bind after that complete operation, not during individual EVA constructors.

The recommended locator is **the full roster name scoped to this native save**, with the expected native character asset as a consistency guard. Validate full-name uniqueness and native hash uniqueness: `KeyHash.cs:38` uses a case-insensitive 32-bit hash, so storing only the hash weakens identity checking. Require one compatible live EVA for the roster entry before binding resources; reject conflicting seat/EVA assignments.

Keep the profile when its kitten boards, is unassigned, is KIA or has no renderable yet. These states need no active saber/cape resources. Rebind when that roster member next becomes an EVA. Persistence of choices does not itself add capes or held sabers to seated rendering.

For genuinely rosterless mod-spawned kittens, support a separate `standalone-eva` locator using the exact saved EVA ID plus character guard. It has a narrower lifetime guarantee and must not silently become a roster lookup. Unscience's DOH adapter is a useful example of this case.

An arbitrary external rename cannot be recovered reliably. `Vehicle.SetName()`/`CelestialSystem.Rename()` update the vehicle identity without updating its roster entry. A future supported rename needs an explicit roster-aware profile migration; similar names or matching character assets are insufficient. A mod-generated profile GUID does not solve this native lookup problem. Likewise, duplication is not proof of a new roster person: copied occupied seats can contain the original occupant identity. Reject ambiguous simultaneous bindings rather than sharing the person's effects across clones.

## Proposed state and ownership

Use **`<native save directory>/purr2d2.json`**, with its own versioned document and XML fingerprint. A first feature record named `purr2d2.kittens`, version 1, can contain a bounded list of records with this shape:

```text
locator
  kind                   roster | standalone-eva
  rosterName             for a roster member
  evaVehicleId           for a rosterless EVA
  characterAssetId       native identity guard, not a person ID
settings
  saberColor             finite RGB in the supported range
  saberType              Light | Dark
  capeEnabled
  saberEnabled           desired settled equipment/ignition state
```

| State | Persistence policy |
| --- | --- |
| Individual saber color, Light/Dark choice, cape opt-in | Save with the kitten; restore when a compatible EVA exists. Current cape brown/black selection derives from `SaberType`, so a duplicate color/style field is unnecessary. |
| Equipment and ignition | Save the final user intent, including a pending checkbox request; resume through the normal equip sequence. Do not checkpoint the exact arm blend or blade deployment fraction. The current UI combines equipment and ignition, so one intent flag matches it. |
| Shared Light/Dark effect profiles, blade length, grip, volume, UI and cape budget | Keep existing global preference semantics. Snapshot them separately only if exact per-save shared appearance becomes a requirement. Future per-kitten overrides belong in the versioned profile rather than being inferred from globals. |
| Runtime pose processors, lights, FMOD channels, GPU resources, cape cloth deformation and avatar references | Recreate through normal ownership APIs. Never serialize them. |
| Swing/slice gestures, Force holds, somersaults, queued actions and background work | Reset as transient activity. Saving a saber's enabled intent must not resume a cut or Force command. |
| Generated fragment models and external assets | Existing native save/template persistence remains responsible. Keep `Generated/Slices/` and installed models/textures/audio available; the kitten sidecar does not embed these files. |

Here Light/Dark means the existing `SaberType`, which also controls cape appearance. If a later light-side/dark-side character alignment becomes independent of saber type, add a distinct versioned setting. If it changes the native character asset, record baseline and effective character identities explicitly so the guard does not reject the mod's own changes.

Introduce a durable scene registry keyed by these locators and keep `_kittenSabers` as transient bindings. UI edits update the durable profile and apply its values to the current binding. Boarding destroys only the binding. Disabling the mod releases effects but retains loaded choices; enabling reapplies them. The existing session enable switch can still start off each launch, with saved recipes dormant until activation. Save capture must work while disabled.

## Integration without a second load scheduler

Adapt the pure DTO, storage and validation patterns into Purr2D2's namespace. Their source is MIT licensed; preserve Unscience's copyright/permission notice if copying substantial code. The current `ksa-abstractions.lib` is a broad game utility library, so depending on it solely for these small contracts adds unnecessary coupling.

Do not install another unchanged `NativeSaveHooks` stack:

1. Its callbacks are single-host static properties (`NativeSaveHooks.cs:18-25`). `UnscienceSaves` assigns them with `=` and clears them on disposal (`UnscienceSaves.cs:25-32,109-118`). A second host on the same library would replace or erase the first host's callbacks.
2. `SaveStorage.FileName` is hardcoded to `unscience.json`. Purr2D2 must not own that file.
3. An isolated copy still conflicts at the frame seam. `PhysicsFrameHook.Transpile` requires the unique original `Universe.GetJobSimStep` call, then replaces it (`PhysicsFrameHook.cs:79-102`). A second copy can no longer find the required original call. Purr2D2 deliberately leaves that call intact and orders its transformation after `MeowSci.Unscience` (`GameHooks.cs:26-32,119-125`). Preserve this arrangement.

The preferred first implementation is passive native save/load observers plus Purr2D2's existing settled update:

1. Observe an executed `GameSave.Populate()` and capture detached logical profiles against that exact save object. Observe successful native `Write()` and publish only its matching capture to `purr2d2.json`. Report native failure separately from sidecar failure.
2. At an actually executed `UncompressedSave.Load()`, stage and validate the incoming sidecar in a scoped transaction outside `SaberRuntime.ResetWorld`. Do not replace active logical profiles at a request that another mod deferred or refused. Observe final `__runOriginal` decisions and unwind contexts with finalizers.
3. At actual native reconstruction, join workers and release old runtime ownership using the existing cleanup boundary. Tag pending work with a scene generation. An unreadable native file that never reaches reconstruction leaves both current profiles and effects intact. A direct deserialize/new-system operation without a matching file transaction must not reuse a previous file's sidecar.
4. Commit the incoming logical profiles only after successful native reconstruction. A successful vanilla/missing-sidecar load commits empty/default profiles. A successful standalone new-system operation commits a new empty scene; nested `LoadSystem` calls during deserialization belong to the surrounding transaction rather than clearing it twice. If native reconstruction fails after cleanup, report failure and retain incoming detached data for recovery without applying it to a partly rebuilt world.
5. Rebind on the next existing Purr2D2 settled update after successful reconstruction. For file loads, wait until the enclosing load and its `OnGameLoaded` cleanup finish. Direct `DeserializeSave` and `LoadSystem` callers need their own successful-completion signal: they do not necessarily call `OnGameLoaded`. Run system-change cleanup first, then bind the committed scene generation once. The current `OnGameLoaded` cleanup and `_system` check must not erase staged or committed profiles. Co-loaded Unscience can replay later than Purr2D2's callback in the frame, making next-frame rebind intentional.
6. Separate `ReleaseRuntimeBindings` from `ClearSceneProfiles`. Ordinary disable, editor transitions and runtime faults release ownership without silently deleting durable choices. Process scene transaction bookkeeping even while activation's early-return path prevents rendering. Allocate cape/pose/audio resources only when enabled and eligible.

Passive observers do not add a second global deferral policy. Co-loaded Purr2D2 benefits from Unscience's queued replay; standalone behavior retains the current native load timing. Deferring restoration alone does not solve old GPU/UI references during cleanup. Audit late-UI load/resource disposal explicitly before claiming equivalent standalone frame safety. If that audit requires universal load deferral, extract a single shared broker with subscription tokens and reference-counted hook ownership; the current Unscience callback properties are not such an API.

Validation should reject malformed locators, duplicate names/hashes, conflicting targets, unknown enums, nonfinite values, excessive entry counts and unsupported asset identities before applying anything. Missing or ambiguous identities remain unresolved with visible diagnostics. Preserve their original valid records for future saves rather than substituting the controlled kitten. Prefer per-kitten retained records so one unresolved kitten does not freeze edits to every other kitten; if reusing Unscience's whole-feature retention unchanged, expose that coarser behavior clearly.

## Implementation order and acceptance

1. Add game-independent profile DTOs, bounds, migrations and hash-bound storage, with round-trip and corrupt/foreign/future-data checks.
2. Refactor the current per-kitten choices into the durable registry; keep rendering, physics and audio behavior in disposable bindings. Verify boarding and disable/re-enable before adding native-save hooks.
3. Add capture/write pairing and scoped passive load observers. Cover successful/false/throwing/skipped writes, unreadable loads, deferred requests, direct deserialization, new systems, repeated cleanup and `OnGameLoaded` ordering with source-linked fixtures.
4. Restore settings through existing color/type, equip and cape ownership paths. Check that palette defaults apply only to new identities, and that pending user intent is captured without restoring transient commands.
5. Extend the current frame/save interoperability harness for both mod installation orders, standalone Purr2D2, co-loaded Unscience and removal. Confirm both sidecars belong to the same native XML after overwrite. Update README and acceptance/compatibility policies when persistence is implemented.

Native acceptance must include restart; A-to-B-to-A loads; two kittens using the same character asset; EVA-to-board-to-save-to-load-to-EVA; saved seated/unassigned kittens; rosterless kittens; explicit identity changes; conflicting copied seats; vanilla/new scenes; disabled and hidden-HUD loads; missing assets; and stable pose/light/audio/cloth resource counts across repeated loads. Save while equipping/retracting and while a UI request is pending; confirm intent returns without resuming gestures. Inject sidecar failure and verify the native save still loads with a warning.

This investigation inspected production sources and their existing checks; it ran no builds, tests or native game session. Existing managed results in Unscience's `saves.tests/README.md`, `doh.tests/README.md` and Purr2D2's `docs/ACCEPTANCE.md` establish fixture coverage, not acceptance of this proposed persistence feature.

## Reference adapters

- [DOH persistence](../../unscience/doh.lib/DohSubmod.Persistence.cs), especially `:36-59` capture, `:60-75` cleanup without despawning and `:77-143` rebind/material-set reclamation. It restores native-saved EVA objects rather than spawning duplicates. Its EVA-only registry needs the roster-aware extension above.
- [Kitten Animations persistence](../../unscience/kitten-animations.lib/KittenAnimationsSubmod.Persistence.cs), `:60` reset and `:69` restore, plus `KittenAnimationsSubmod.cs:35` replacement-instance detection. Clip identity uses source/label, not a runtime catalog index; scratch pose data stays transient.
- [VehicleProvider](../../unscience/ksa-abstractions.lib/VehicleProvider.cs), `:30`, rejects ambiguous vehicle matches. Use the same exact-target policy for live bindings while adding full roster-string checks.
- [Unscience acceptance policy](../../unscience/plans/saves-acceptance.md) and [save integration map](../../unscience/scope/saves.md) distinguish managed coverage, native checks, retained-record behavior and non-atomic overwrite limits.
