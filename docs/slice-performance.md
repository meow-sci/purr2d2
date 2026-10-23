# Slice performance measurements and preparation

The first slice after launch can pay for compiling large replacement and native
connection-splitting methods. The existing `Validation` phase starts before the
call to `TryReplace`, so its time includes that method's first compilation as well
as validation and rollback setup. It is not a measurement of validation alone.

## Targeted compilation preparation

Purr2D2 prepares a small set of slicing methods in the background after its
patches are installed. This compiles code without calling the methods or creating
vehicles. Slice preparation can continue concurrently; native isolation waits
until the preparation attempt finishes. Failure falls back to ordinary runtime
compilation. Cancellation and unload never wait for the background thread.

Preparing a method does not recursively prepare every callee. Closed generic
instantiations, engine initialization, native work and subsequent runtime
recompilation can still contribute to a first slice. The diagnostics distinguish
compilation from other costs; preparation does not remove the underlying
vehicle work.

The final source and fragment validation remains in place. A physics step runs
between isolation and replacement, so the swap must still reject stale state.

## Export and persisted identities

The exporter no longer creates a complete temporary GLB just to hash it. It
incrementally hashes a versioned, deterministic representation of the fragment.
New identities use a separate format version; existing generated GLBs, XML and
their save references remain readable and are never rewritten by this change.
Keep `Generated/Slices/` when updating the mod.

The final GLB writer also avoids copying the entire binary payload and final
output into additional arrays. Buffer changes preserve the GLB bytes for the
same input, including every position, normal, UV, material and picking triangle.
The full picking mesh and existing collider construction are retained.

The canonical identity uses length-delimited UTF-8 and little-endian numbers. It
includes ordered render attributes at exported float precision, material/glass
identity, closed-solid provenance, the full picker's original face order, emitted
collider geometry, mass and principal inertia. Prepared positions must already
be float32 values, as required by the slicing precision pass, so picker vertex
deduplication cannot change without changing the identity. Normals and UVs are
hashed at their exported precision. Non-finite values and invalid Unicode text
are rejected; cancellation is checked between bounded hash chunks.

## Reading the diagnostics

The original preparation/isolation/swap summary remains compact. Each operation
also records its source template, owner part count, source parts including
internal attachments, and external isolation boundary count. Expanded summaries
wait until both the operation's frame and its next three frame intervals have
completed. Overlapping isolation/swap windows finish before either is formatted.

Native phases retain wall time, main-thread JIT time/method/IL-byte counts,
main-thread allocations, global GC collection counts and global pause duration.
Frame windows cover `PrepareFrame` entry to the next entry, and the hook tail
covers settled-hook return to the next entry. Separate measurements wrap the
existing vehicle-solver wait, derived-data flush, resource flush, and vehicle
preparation/ground-clutter/solver dispatch. They do not add a physics wait or
enable the game's global profiler.

Telemetry uses bounded storage and drops excess operations rather than growing
without limit. A missing interval baseline is labeled partial; unavailable
counters or missing segments are reported explicitly. Cancellation stops unused
sampling while allowing pending windows to finish. World changes clear pending
windows. Idle hooks do not allocate.

## Comparing runs

Use the same intact stock part, vessel connections and scene for cold and warm
comparisons. A later cut on a detached fragment may have no connections to split;
near-zero isolation on that cut does not measure warmed splitting of the
original vessel.

1. Launch the game and record the compilation-preparation result.
2. Cut the selected stock part and retain the preparation, isolation, swap and
   delayed frame-window diagnostics.
3. Repeat on an equivalent intact vessel in the same session, then test a recut
   of a generated fragment separately.
4. Repeat across fresh launches. Compare both the isolation frame and swap frame,
   along with the following three frames and their vehicle-solver waits.

Per-thread JIT counters report compilation on the sampled thread. Allocation
counts likewise cover that thread, whereas GC counts and pause durations are
process-wide and can reflect background-worker activity. Overlapping phase and
frame samples must not be added together. A frame interval includes pacing and
other work outside the native slice transaction; it is not GPU execution time.

These managed checks do not establish native game frame-time improvements.
Native acceptance must also cover cancellation between isolation and swap,
pause/resume, occupied neighboring branches, repeated cuts, and loading saves
containing both old and newly generated fragments.

## Managed verification (2026-10-23)

Release compilation passes with zero warnings/errors. Full managed regression
checks, six stock worker/export/collider fixtures, current-game metadata/IL
inspection, native XML decoding of old/new fragment identities, staged payload,
ZIP content and checksum checks pass. The frame-hook test decodes the actual
current `PrepareFrame` IL and checks the four replacement call stacks, unchanged
remaining instructions/labels, rejection of changed seams, and propagation of
native exceptions. It does not start the game.

The warmup probe resolves and prepares 47 selected methods with no failures.
In separate fresh ARM64 processes, the current bodies compiled in 16.553 ms
(`TryReplace`) and 9.124 ms (connection-based `Vehicle.Split`); after the
production warmup, preparing each again compiled zero additional methods/IL
bytes. Only ignored pure-IL test copies have their PE machine fields adapted
for ARM64. Hashes of all original host DLLs remain unchanged.

Four warmed full-export allocation samples, summing both fragments, gave:

| Fixture | Previous exporter | New exporter | Reduction |
|---|---:|---:|---:|
| Fuel tank LF3W3HA | 98.221 MB | 68.888 MB | 29.334 MB |
| Service module SetAHeightB | 483.841 MB | 339.220 MB | 144.621 MB |

MB means 1,000,000 bytes allocated on the worker thread, not retained memory or
GC pauses. Timings were noisy; these measurements do not establish a latency
reduction. Stock and authored fixture GLBs match the old writer byte-for-byte
after substituting only the fixed-width identity names. Frozen authored v3
fixtures are committed under `tests/fixtures/`; proprietary stock assets stay
in ignored local artifacts.

Reproduce from the repository root:

```sh
dotnet build Purr2D2.slnx -c Release
python3 tools/validate.py --run-tests --stage artifacts/stage/purr2d2
python3 tools/check_fragment_templates.py
python3 tools/check_slice_warmup.py
python3 tools/check_stock_slices.py --fixture artifacts/checks/stock-slices.json
```

The stock check requires a previously captured local fixture, or an installed
`Content/Core` directory as described by `tools/check_stock_slices.py --help`.
The warmup probe uses the built production mod and the configured game snapshot;
it does not execute simulation methods or measure in-game frame duration.
