# Saber audio implementation and research

Verified against the supplied KSA **2026.10.19.5638** decompiled sources and DLLs.
The ksa-coffer corpus is pinned to 2026.10.10.5554; its named APIs were checked against
the current source rather than assumed unchanged. These notes describe implementation
and static verification; native playback still needs an in-game acceptance pass.

## Assets and provenance

The activation and idle recordings were supplied by the user in the feature directory.
Their original files are unmodified. This project does not claim authorship or establish
redistribution rights for those supplied recordings. The two optional additional sounds
are original procedural synthesis generated from this repository's Python code.

| Runtime file | Source | Preparation |
| --- | --- | --- |
| `Assets/Audio/saber-ignite.wav` | `feature/purr2d2-light-sabers/sounds/lightsaber-ignite.wav` | Entire clip, 98,000 frames at 44,100 Hz (2.22222 s), stereo averaged to mono. No resampling, normalization or trimming. |
| `Assets/Audio/saber-hum.wav` | `feature/purr2d2-light-sabers/sounds/lightsaber-idle.wav` | Stereo averaged to mono; 35 ms cosine overlap joins end to beginning. Original 95,091 frames becomes 93,547 frames at 44,100 Hz (2.12125 s). No resampling or normalization. |
| `Assets/Audio/saber-retract.wav` | `tools/generate_audio.py` | Original descending oscillator/noise effect, fixed seed, mono 16-bit PCM, 48 kHz, 0.28 s. |
| `Assets/Audio/saber-contact.wav` | `tools/generate_audio.py` | Original periodic oscillator/noise sizzle, fixed seed, mono 16-bit PCM, 48 kHz, 2 s. |

SHA-256 of original input recordings:

```text
lightsaber-ignite.wav e7000a7881e7663e73f1bd99df1618e5c347fa2f9a2738b537254354746ce528
lightsaber-idle.wav   e30a4532683ab70c083d98ee17a0d6f18579c9d640715543022c660342c99c6b
```

SHA-256 of prepared runtime WAVs:

```text
saber-ignite.wav  96822dc90660c4e779f56384797d888a87ac19fee16085b188e1a55afbd7acb5
saber-hum.wav     42ea1c2d9b135de8573f593541ef5b0f3d06aba6d49c155c47f99a780795a744
saber-retract.wav d4ca047eaae354f0242f666c9f5709c4c6b12463f018474fbf74234b02977de2
saber-contact.wav 8fcaeaa8e56a2b8f89d8da99f8e652980065b503d1c20ac7e3e996c3a0ece567
```

Regenerate optional synthesized sounds only:

```sh
python3 tools/generate_audio.py --check
```

Explicitly reprepare the supplied activation/idle recordings as well (from this repository):

```sh
python3 tools/generate_audio.py --check \
  --supplied-sounds-dir ../../feature/purr2d2-light-sabers/sounds
```

The default command deliberately does not write activation or hum. The explicit input
option reads the supplied files and writes only this repository's prepared outputs.
It prints the input/output hashes, RMS, duration, sample count and loop seam jump.
The source files' extra WAV metadata is not propagated into the mono runtime WAVs.

The overlap removes 1,544 source frames from the idle loop period. Its 1,544-frame tail
is blended into the first 1,544 frames with complementary half-cosine weights. Playback
then wraps into the source's next sample. This preserves ordinary sample-to-sample
continuity without a silent interval. The prepared hum's boundary difference is
140 PCM counts (0.0043 full scale), within ordinary adjacent-sample variation.
The loop should still be auditioned for perceptual repetition and any codec-specific
boundary behavior in FMOD. XML bounds are explicit inclusive PCM sample indices:
hum 0–93,546; contact 0–95,999.

## Runtime API

`src/Audio/SaberAudio.cs`, namespace `Purr2D2.Audio`, owns channels for one equipped saber.

```csharp
void Initialize();
void Update(KittenEva owner, double3 emitterBody, float deployment,
    bool targetOn, bool paused, float volume, float swingSpeed = 0, float contact = 0);
void Suspend();
void Stop();
void Reset();
void Shutdown();
void Dispose();
```

`emitterBody` is the final posed hilt location relative to the kitten body origin,
in body coordinates. `swingSpeed` is blade-tip speed relative to the kitten in m/s,
and `contact` is an optional 0–1 sizzle intensity. Update after camera/pose evaluation,
once on the main thread per frame, including frames with the HUD hidden.

The implementation resolves four `SoundBehavior` assets, routes them through the
game's SFX bus, starts channels paused while their first position is configured,
and retains handles to move all active sounds. Activation/retraction play on target
state transitions; the native hum loop follows deployment. A transition while paused
is deferred until resume. Rapid reversal cuts the earlier transition. Hum pitch and
gain can respond slightly to relative swing speed. The optional sizzle is a separate
native loop. It is silent unless the caller supplies contact intensity.

When an equipped kitten's main-view pose is temporarily stale or offscreen, call
`Suspend()` instead of `Stop()`. It pauses held channels while preserving owner,
target state and pending transitions. A fresh `Update()` resumes and relocates them;
returning to view while already lit does not trigger another activation sound.
Use `Stop()` for actual unequip/reset, which intentionally clears transition history.

Channel acquisition/stealing retries are bounded to four starts per continuous event,
with increasing 250 ms cooldowns. A deliberate stop, new contact, blade toggle or
owner reset permits a new budget. Missing assets and playback faults produce bounded
diagnostics instead of repeated per-frame exceptions. XML audio resources belong to
KSA; this class stops its channels and does not release the engine's shared WAV handles.

The supplied activation recording is much quieter than idle (RMS approximately 0.030
versus 0.407). Its original gain was preserved. Audition transition/idle balance in
game before selecting final gains or deciding whether to normalize the recording.

## Spatial coordinates and vacuum

`GameAudio.GetAudioCamera()` returns `Program.MainViewport.BaseCamera`. KSA keeps FMOD's
listener at its default origin and passes camera-relative view-space source positions.
Never set FMOD listener attributes globally from this mod. The emitter follows this path:

```csharp
var position = camera.GetPositionEgo(owner) + emitterBody.Transform(owner.Body2Cce);
var spatial = new SpatialAudio(position, camera.GetVelocityEgo(owner), 1.0);
channel.SetSpatialAudio(spatial);
```

`SpatialAudio(Astronomical, offset)` expects an ego/world displacement; it does not
transform a body/assembly-local offset. Stock `Vehicle.UpdateAudio` transforms nozzle
offsets through `Body2Cce` before calling that constructor. The explicit pressure 1.0
is intentional fictional suit/saber sound in vacuum, with ordinary 3D panning and
attenuation. `DistanceFilter="false"` alone would not bypass the stock pressure LPF.
The stock wrapper applies a 160–18,000 Hz low-pass based on pressure; the older corpus's
claim that this alone completely silences sound in vacuum is inaccurate.

Raw FMOD Doppler is disabled per channel to prevent orbital or camera-follow velocity
artifacts. Relative hand motion can modulate pitch through the explicit swing input.

## Pause, warp and lifetime

Sound assets declare `SimSpeed="Ignore"`. The caller passes `paused=true` for simulation
pause or unsupported time warp; the audio class pauses existing channels and starts no
new ones. The default `SimSpeed="All"` is unsuitable for stable saber audio: its formula
already yields a 1.2 pitch multiplier at 1×, sets zero pitch at pause, and stops sounds
above 10×. `GameAudio.PlaySound` also refuses new channels above 10× unless `alwaysPlay`
is supplied. This implementation retains that start gate and expects explicit caller
pause policy. On resume it restarts a missing hum within its bounded retry budget.

Call `Reset()` before replacing a save/world, on boarding/unequip, and on leaving the
flight scene. Owner identity changes/disposal also stop the previous channels. Native
channels do not clean themselves up merely because a vehicle is destroyed.
`Universe.DeserializeSave` destroys old vehicles but only clears stock explosion audio.

Call `Shutdown()` from a prefix on `Program.Dispose` (or an earlier guaranteed teardown
point). This immediately stops channels and permanently marks the instance as having
no live mixer. `Program.Dispose` calls `GameAudio.Release` before StarMap invokes mod
unload, so StarMap unload alone is too late for dependable native cleanup.
Do not use `GameAudio.System.IsNull()` as the only post-release check: the stored
native handle is not reset by `GameAudio.Release`.

`CompoundSound` was intentionally not used: current `Play` starts its start and main
sound simultaneously, and `SetPaused` excludes the precreated stop sound. Separate
channels make saber reversal, pause, and scene resets explicit.

## Verified game/reference sources

Paths below are relative to the workspace root:

- `repos/ksa-game-assemblies/current/version.json`: build identity.
- `repos/ksa-game-assemblies/current/decomp/KSA/GameAudio.cs`: system creation, playback,
  SFX group, 10× gate, active-channel processing and system release.
- `repos/ksa-game-assemblies/current/decomp/KSA/{SpatialAudio,Camera,ChannelWrapper}.cs`:
  camera-relative coordinates, pressure filtering, gain/pitch and channel lifetime.
- `repos/ksa-game-assemblies/current/decomp/KSA/{SoundReference,SoundFileReference,LoopBounds,SpatialSoundData}.cs`:
  XML registration, native loop flags/points and distance policy.
- `repos/ksa-game-assemblies/current/decomp/KSA/{CompoundSound,CompoundChannelWrapper}.cs`:
  start/loop/tail behavior and pause limitation.
- `repos/ksa-game-assemblies/current/decomp/KSA/{Program,Universe,Vehicle}.cs`:
  audio update in PostRender, camera-before-render order, save replacement and disposal.
- `repos/ksa-game-assemblies/current/decomp/Brutal.FmodApi/{Fmod,CreateSoundExInfo}.cs`:
  native wrapper APIs. `new CreateSoundExInfo()` initializes required `CbSize`;
  `default(CreateSoundExInfo)` does not.
- `repos/unscience/byo-music.lib/VesselSound.cs`: raw file playback and per-frame 3D
  attributes, completion handling and source identity ownership.
- `.agents/skills/ksa-coffer-skill/references/howto/audio/`: community approaches and
  source-pinned examples, especially KSACars and KSArmory.

## Verification and remaining acceptance

`tools/generate_audio.py --check` verifies mono 16-bit PCM, exact frame counts, no clipping,
non-silent levels, silent one-shot endpoints and loop seams within ordinary signal
variation. It also checks the sound XML's file paths, SFX/simulation-speed declarations,
mono format and exact loop endpoints against the prepared WAVs. A second generation
verified identical byte hashes for the procedural files. The main mod must compile
against the current host DLLs. Native playback cannot be validated by compiling.

An actual host `AssetBundle`/`XmlSerializer` harness was compiled, but it could not execute
the supplied Windows x86-64 `KSA.dll` on the available osx-arm64 .NET runtime. The full
host XML deserialization check therefore remains for a compatible game runtime. XML
element names/attributes were cross-checked against current host source, including
`AssetBundle`, `SoundReference`, `SoundFileReference`, `LoopData` and `AudioSyncPointData`.

In game, verify supplied activation timing and level, repeated hum loops, near/far and
left/right camera movement, SFX/master sliders, vacuum, hidden HUD, rapid toggle/reversal,
pause/resume, warp/resume, repeated contacts, EVA/boarding, owner destruction, save reload,
channel stealing and exit. Confirm no old-world loop survives scene replacement.
