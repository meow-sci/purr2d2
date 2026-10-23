# Slice performance measurements and preparation

The first slice after launch can pay for compiling replacement, native splitting,
motion and destruction code. The existing `Validation` phase starts before the
call to `TryReplace`, so its time includes that method's first compilation as well
as validation and rollback setup. It is not a measurement of validation alone.

The latest cold intact-tank sample had 21 owner parts and 10 isolation boundaries.
Its isolation and swap passes took 13.20 ms and 9.23 ms on separate frames, with
17.07 ms of main-thread compilation recorded across those passes. The following
vehicle-solver waits reached 21.24 ms and 43.62 ms. No collections or GC pauses
were recorded in those windows. These observations motivate deeper compilation
preparation and worker-side measurements; they do not establish whether the
remaining waits are worker compilation, physics work or scheduling delay.

## Targeted compilation preparation

Purr2D2 prepares selected slicing methods in the background after its patches are
installed. The selection follows bounded call paths from the native transaction,
including splitting, vehicle configuration, motion and source disposal, rather
than stopping at the outer methods. This compiles code without calling the game
methods or creating vehicles. Slice preparation can continue concurrently; native
isolation waits until the preparation attempt finishes. Failure falls back to
ordinary runtime compilation. Cancellation and unload never wait for the
background thread.

Preparing a method does not recursively prepare every callee. The mod reads IL
call sites with their closed generic type and method arguments, then prepares the
selected bodies explicitly. It follows only allowed native, mod, math and
collection types. Explicit phase roots take priority over deeper callees. Type
initializers, native imports, open generic definitions and abstract methods are
excluded. A changed or unsupported target is reported without stopping the
remaining selection.

Selection is capped at four call edges, 1,800 bodies and 768 KiB of IL. Preparation
has an eight-second soft budget checked between bodies; one in-progress runtime
compilation cannot be interrupted. The startup `prepared` count means successful
preparation requests, not newly compiled methods: generic sharing or existing
runtime code may satisfy a request without compilation.

Virtual implementations that cannot be resolved from those call sites,
unselected generic instantiations, engine initialization, native work and later
runtime recompilation can still contribute to a first slice. Preparation does
not remove the underlying vehicle work.

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

### Physics worker measurements

The worker report is attached to the interval that dispatched the physics job.
Its result is collected after the game's existing wait in the next frame,
including the last interval of an operation window. The frame's original
`vehicle wait` field still describes the wait performed in that frame, so it
normally belongs to the previous interval's dispatched job.

Each report separates dispatch-to-`Run` delay, elapsed `Run` time, and their
overlap with the following native wait. Dispatch-to-`Run` is not pure scheduler
latency: it also includes any work or compilation before the instrumented entry.
The after-`Run` tail includes scheduler completion and wake-up overhead.

Root stages cover bubble trimming, orphan assignment, merging, preparation,
contact-island construction, the joined island batch, clearance updates,
reassignment, splitting and result publication. These are intervals between
markers; they include loop and profiler overhead between those native calls.
Selected child scopes add detail for bubble stepping, per-vehicle terrain
updates, origin changes, collision detection, constraint simulation and Bepu
workers belonging to the vehicle worker pool.

Counters are sampled on the thread doing the work. Nested child scopes split
their intervals so helper-thread JIT totals do not count nested work twice.
Children executed by the root thread are labeled as detail already included in
the root total. Wall durations overlap across threads and include waits; do not
sum them to infer frame duration. Uncovered helper code and compilation needed
to enter or leave a callback are excluded, so the measured JIT total is scoped
coverage, not a process-wide total.

Worker records are allocated only during active operation windows, with fixed
arrays and no retained game objects. Workers accumulate counters; formatting
runs later on the main thread. Reset invalidates records without reusing storage
that an old worker may still access. Optional worker hooks install as a separate
group and report unavailability if the host no longer matches; their removal is
part of both unload and initialization-failure cleanup.

## Comparing runs

Use the same intact stock part, vessel connections and scene for cold and warm
comparisons. A later cut on a detached fragment may have no connections to split;
near-zero isolation on that cut does not measure warmed splitting of the
original vessel.

1. Launch the game and record the compilation-preparation result.
2. Cut the selected stock part and retain the warmup result, preparation,
   isolation, swap and delayed frame-window diagnostics, including worker data.
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
checks, current-game metadata/IL inspection, native XML decoding of old/new
fragment identities, staged payload, ZIP content and checksum checks pass. The
frame-hook test decodes the actual current `PrepareFrame` IL and checks the four
replacement call stacks, unchanged remaining instructions/labels, rejection of
changed seams, and propagation of native exceptions. The worker test verifies
ten ordered stage markers and fifteen child targets, then installs, prepares,
reinstalls and removes all seventeen real engine hooks. A separate production
DLL probe also verifies cleanup preserves another Harmony owner's patch. These
checks do not execute native physics or start the game.

The expanded warmup probe resolves 121 roots to 1,210 selected managed bodies
(93,600 IL bytes), including 485 closed generic bodies. All preparation requests
succeeded without reaching a budget; the ARM64 probe's background pass took
60.2 ms. In separate fresh processes, 24 representative preparation calls across
replacement, splitting, configuration, collider/terrain disposal, motion and
diagnostic helpers required zero additional compiled methods or IL bytes after
warmup. Cancellation before start and during preparation also passes. Only
ignored pure-IL test copies have their PE machine fields adapted for ARM64.
Hashes of all 38 original host DLLs remain unchanged. These compilation checks
do not establish in-game latency or later tiered-compilation behavior.

The previous exporter batch also passed six stock worker/export/collider
fixtures. Four warmed full-export allocation samples, summing both fragments,
gave:

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
If that snapshot omits a host dependency, use the repeatable `--dependency-dir`
option with a directory containing the matching assembly. The verification above
supplied MathNet.Numerics 5.0.0 from the local NuGet cache:

```sh
python3 tools/check_slice_warmup.py --dependency-dir "$HOME/.nuget/packages/mathnet.numerics/5.0.0/lib/net6.0"
```

This dependency is used only by the probe and is not copied into the mod package.
