# Saber audio implementation and research

Verified against the supplied KSA **2026.10.19.5638** decompiled sources and DLLs.
The ksa-coffer corpus is pinned to 2026.10.10.5554; its named APIs were checked against
the current source rather than assumed unchanged. These notes describe implementation
and static verification; native playback still needs an in-game acceptance pass.

## Assets and provenance

The idle recording was supplied by the user in the feature directory.
Their original files are unmodified. This project does not claim authorship or establish
redistribution rights for those supplied recordings. The contact sound is
original procedural synthesis generated from this repository's Python code. The Light/Dark
ignition, static and saber close recordings were also supplied by the user and are copied unchanged from the workspace root.

| Runtime file | Source | Preparation |
| --- | --- | --- |
| `Assets/Audio/light_saber_on.ogg` | User-supplied `light_saber_on.ogg` | Unmodified mono Vorbis, 22,050 Hz, 2.143673 s; plays once when a Light saber ignites. |
| `Assets/Audio/dark_saber_on.ogg` | User-supplied `dark_saber_on.ogg` | Unmodified mono Vorbis, 22,050 Hz, 2.143673 s; plays once when a Dark saber ignites. |
| `Assets/Audio/saber-hum.wav` | `feature/purr2d2-light-sabers/sounds/lightsaber-idle.wav` | Stereo averaged to mono; 35 ms cosine overlap joins end to beginning. Original 95,091 frames becomes 93,547 frames at 44,100 Hz (2.12125 s). No resampling or normalization. |
| `Assets/Audio/saber_close.ogg` | User-supplied `saber_close.ogg` | Unmodified mono Vorbis, 22,050 Hz, 1.187392 s; plays once on retraction. |
| `Assets/Audio/light_side_static.ogg` | User-supplied `light_side_static.ogg` | Unmodified mono Vorbis, 44,100 Hz, 4.77059 s; full-file loop. |
| `Assets/Audio/dark_side_static.ogg` | User-supplied `dark_side_static.ogg` | Unmodified mono Vorbis, 48,000 Hz, 8.422646 s; full-file loop. |
| `Assets/Audio/saber-contact.wav` | `tools/generate_audio.py` | Original periodic oscillator/noise sizzle, fixed seed, mono 16-bit PCM, 48 kHz, 2 s. |

SHA-256 of original input recordings:

```text
lightsaber-idle.wav   e30a4532683ab70c083d98ee17a0d6f18579c9d640715543022c660342c99c6b
```

SHA-256 of prepared runtime WAVs:

```text
saber-hum.wav     42ea1c2d9b135de8573f593541ef5b0f3d06aba6d49c155c47f99a780795a744
saber-contact.wav 8fcaeaa8e56a2b8f89d8da99f8e652980065b503d1c20ac7e3e996c3a0ece567
```

Regenerate the optional synthesized contact sound only:

```sh
python3 tools/generate_audio.py --check
```

Explicitly reprepare the supplied idle recording as well (from this repository):

```sh
python3 tools/generate_audio.py --check \
  --supplied-sounds-dir ../../feature/purr2d2-light-sabers/sounds
```

The default command deliberately does not write the hum or supplied Ogg recordings. The explicit input
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

## Light/Dark ambient layer

Each deployed saber plays its type's static recording alongside the existing hum,
ignition/retraction and contact effects. Light uses the calmer recording; Dark uses
the more violent recording. Type is independent of blade color. Changing type stops
only the previous static channel and starts the new one on the next unpaused update.
The layer uses the moving hilt's 3D position, the existing 1–35 m spatial range and
Sfx routing, with gain `Volume × Deployment × 0.30`. Thus it grows with extension,
recedes with distance, and respects both the mod and game volume controls. It shares
the hum's pause, suspension, retry limits, mute, retraction and cleanup behavior.
Ogg files are included in both the staged folder and release ZIP. In-game listening
is still needed to assess the supplied loop seams and balance against the hum.

## Runtime API

`src/Audio/SaberAudio.cs`, namespace `Purr2D2.Audio`, owns channels for one equipped saber.

```csharp
void Initialize();
void Update(KittenEva owner, double3 emitterBody, float deployment,
    bool targetOn, bool paused, float volume, float swingSpeed = 0, float contact = 0, SaberType type = SaberType.Light);
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

The implementation resolves seven `SoundBehavior` assets, routes them through the
game's SFX bus, starts channels paused while their first position is configured,
and retains handles to move all active sounds. Activation/retraction play on target
state transitions; the native hum loop follows deployment. A transition while paused
is deferred until resume. Rapid reversal cuts the earlier transition. Hum pitch and
gain can respond slightly to relative swing speed. The optional sizzle is a separate
native loop. It is silent unless the caller supplies contact intensity.

Ordinary **Swing / Alt+F7** uses the original motion-driven hum pitch/gain swoosh.
Only the explicit slicing stroke supplies sizzle intensity; ordinary swing state
must not enable the cutting loop in either the frame or rendered-pose update.

When an equipped kitten's main-view pose is temporarily stale or offscreen, call
`Suspend()` instead of `Stop()`. It pauses held channels while preserving owner,
target state and pending transitions. A fresh `Update()` resumes and relocates them;
returning to view while already lit does not trigger another activation sound.
Use `Stop()` for actual unequip/reset, which intentionally clears transition history.

Channel acquisition/stealing retries are bounded to four starts per continuous event,
with increasing 250 ms cooldowns. A deliberate stop, new contact, blade toggle or
owner reset permits a new budget. Missing assets and playback faults produce bounded
diagnostics instead of repeated per-frame exceptions. XML audio resources belong to
KSA; this class stops its channels and does not release the engine's shared sound handles.

Ignition selects the current Light/Dark recording when the transition begins, including
a deferred ignition after pause. Switching style while already lit does not replay
ignition. The supplied recordings retain their original gain; audition transition/idle
balance in game before selecting final gains.

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

The managed `AudioChecks` harness runs the actual audio controller against a simulated
mixer to cover layering, live Light/Dark switching, emitter position, deployment/volume
gain, pause/resume, suspension, mute, retraction, world reset and shutdown. The kitten
runtime checks also cover forwarding an uncontrolled kitten's style to audio. These
checks do not execute FMOD or evaluate the audible distance curve or loop seams.

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
