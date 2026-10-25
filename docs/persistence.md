# Per kitten save and restore

KSA's normal Save / Load now carries individual saber colors, Light/Dark types, cape opt-ins and saber enabled intent. The same logical choices survive boarding, disembarking, editor visits and disabling/re-enabling Purr2D2. Each application launch still starts with Purr2D2 disabled; loaded equipment resumes when enabled. Shared preferences continue to autosave to the installed mod's `settings.json`.

## Save format and failure behavior

`purr2d2.json` lives beside `universe.xml` in each native save directory. Copy the entire directory when moving saves. Keep the installed models, textures and audio, and preserve `Generated/Slices/` for saves containing generated fragments; the kitten sidecar does not embed assets.

Document schema 1 contains `UniverseSha256` and a `Kittens` array. Each record has an `Identity` (`Kind`, `Id`, `Character`), `Version` and detached JSON `State`. Version 1 state has finite normalized `Red`, `Green`, `Blue`, a string `Type` (`Light` or `Dark`), `CapeEnabled` and `SaberEnabled`. Current cape color derives from saber type. Shared length, grip, effects, volume, UI and cape simulation limits remain global preferences.

`GameSave.Populate()` captures logical state against the exact native save object and its native snapshot. Only a successful, actually executed `UncompressedSave.Write()` can publish that matching capture. Failed, skipped, throwing or unmatched writes never publish fresh kitten data. The sidecar hashes the completed XML, flushes a same-directory temporary file, then replaces its own file. It never writes Unscience's sidecar.

Loading checks the XML fingerprint, document version, duplicate JSON properties and kitten identities, JSON depth, a 4 MiB size limit and a 4,096-record limit. A missing or invalid whole sidecar yields default kitten choices after a successful native load, with invalid files reported in the log/settings. Supported records validate their required settings and ranges. Invalid settings and future record versions remain opaque records and are saved again without applying them; valid unknown settings fields also survive edits. Missing kittens retain their recipes without a live binding.

Settings → **Kitten save and restore** shows status and the last warning. **Discard unavailable kitten records** explicitly releases opaque records so future saves can use current choices. Original saves change only when saved again. Ordinary edits never silently replace a future or invalid record.

Native files and the sidecar are not one atomic transaction. A process interruption or sidecar-write failure can leave a native-only save. Native overwrite replaces the directory, so a sidecar must be republished for every successful overwrite. A file that KSA cannot read leaves the current scene and profiles intact. If native reconstruction fails after cleanup starts, incoming recipes remain quarantined and effects stay suspended until a successful scene load.

## Identity and runtime ownership

KSA has no immutable per-person UUID. A roster locator uses the full native roster name scoped to this save, guarded by the character asset ID. Character assets are shared archetypes and cannot identify a person alone. Rosterless mod-spawned EVA kittens use a separate exact EVA-ID locator.

The identity index rejects duplicate native hashes/live identities, stale roster hashes, mismatched characters, conflicting occupied seats and incompatible roster assignments. KIA, unassigned and seated people retain dormant profiles. Boarding destroys only the runtime binding; disembarking creates a new EVA object and resolves the same roster person. This does not add clothing or sabers to seated rendering. Arbitrary foreign renames need an explicit roster migration and cannot be guessed safely.

Profiles contain no game objects, GPU handles, FMOD channels, pose processors or cloth state. Rebinding uses the normal equip sequence and lazy cape setup. It restores final enabled intent, including pending checkbox changes, rather than an exact partially extended blade or arm blend. Swing/slice gestures, Force holds, somersaults, contact history and queued/background actions reset. Resource cleanup preserves profiles; successful native-only/direct/new-system scene replacement clears them.

Identity indexing is lazy: one roster/fleet/seat snapshot is shared by a batch of new bindings. A stable fleet refresh keeps its existing single fleet scan and allocates no managed memory. User edits and save captures revalidate identity; they do not impose another scan on every unchanged frame. Bulk restore enters the normal equip stage directly at the settled boundary, avoiding the UI request queue's 128-entry cap. Cape simulation retains its existing budget.

## Native integration and Unscience coexistence

- `KittenProfiles.cs` owns detached profiles and preserves unavailable records.
- `KittenSaveStorage.cs` owns bounded, validated, hash-bound JSON and atomic sidecar replacement.
- `KittenSaveSession.cs` pairs captures and scopes file loads/reconstruction generations. Nested operations cannot let an older completion overwrite a newer scene.
- `KittenSaveHooks.cs` passively observes native capture/write/load/deserialization/new-system methods. Last-priority observers honor skipped originals and return original native exceptions.
- `KittenIdentityIndex.cs` checks native roster, EVA and seat identity.
- `SaberRuntime.Persistence.cs` binds profiles to existing per-kitten saber/cape ownership. System bookkeeping runs before the disabled-mode early return.

There is no additional `PrepareFrame` transformation or load scheduler. Purr2D2 preserves the original `GetJobSimStep` seam and its ordering after `MeowSci.Unscience`. An initial request deferred by Unscience creates no Purr2D2 transaction; the executed replay does. Old bindings release at the existing joined reconstruction boundary. Logical profiles commit after reconstruction; resource rebinding occurs on a subsequent settled update, after file-load `OnGameLoaded` cleanup. Direct deserialization and new systems complete without requiring that callback. A later `OnGameLoaded` error cannot reactivate the previous scene's profiles.

Standalone Purr2D2 retains KSA's current load timing. Passive observation and deferred rebinding do not establish universal safety for resources referenced by late-UI draw lists. Co-loaded Unscience supplies its own whole-load deferral. Native lifetime checks below remain necessary; this change introduces no replacement global deferral policy.

## Verification

`tools/validate.py --run-tests` includes source-linked persistence fixtures:

- `KittenSaveChecks` patches fake native signatures with real Harmony and production hooks. It covers capture pairing, false/throwing/skipped operations, deferral in both installation orders, unreadable loads, worker/prefix/reconstruction failures, trailing callbacks, nested operations, stale generations, storage rejection and preserved records.
- `KittenPersistenceRuntimeChecks` links production kitten, cape, pose, identity and persistence code with native/render/audio boundary fakes. It covers pending intent, boarding/seated/disabled capture, replacement EVA binding, repeated cleanup, first-system observation, identity conflicts and a 129-enabled-saber restore. Warm stable refreshes assert zero managed allocations and one fleet scan each.

Build and managed fixtures do not certify native graphics, audio or cloth lifetime. The current run and outstanding native acceptance are recorded in [ACCEPTANCE.md](ACCEPTANCE.md). Source research and the original design are in [research-persistence.md](research-persistence.md).
