# purr2d2

Lightsabers and Force control for Kitten Space Agency: an authored hilt held in an EVA kitten's paw, extending/retracting blade, colored illumination, spatial sound, one-click part slicing, cosmetic contact sparks and movable captured objects.

Targets **KSA 2026.10.19.5638**, **StarMap.API 0.3.6**, and the loader's **Harmony 2.4.2**. This is an initial implementation with managed build/asset checks; native in-game acceptance is tracked in [docs/ACCEPTANCE.md](docs/ACCEPTANCE.md).

## Controls

Purr2D2 starts **disabled** each session. Open **Purr2D2 → Enabled** in the global game menu to activate it and show the overlay. Turning it off hides its UI and releases sabers, Force captures and pending actions at the next safe simulation update. Enablement is never saved with preferences.

Take control of an EVA kitten beside a vessel. Use the overlay **SWING** circle to move the saber. To break up a part, press **SLICE**, point at that part, and click. A large marker names the part under the pointer: green for an eligible point in reach, red with a reason otherwise. The action equips and ignites the saber, swings across the selected part, and replaces it with textured pieces, sparks and cooling cut edges. No contact history or ring flight is required. Touching a part produces **visual sparks only**; destruction happens only through an explicit slice.

The overlay has frameless, translucent controls. The top-right list equips and ignites each checked EVA kitten independently, including kittens you are not controlling. Checking raises the hilt before igniting; unchecking fully retracts the blade, then lowers the arm before removing the hilt. Toggling again mid-animation reverses smoothly, and pausing freezes the sequence. The blue **IGNITE / RETRACT** circle below-left of **SWING** performs the same sequence for the currently controlled EVA kitten; it matches the **SLICE** circle in size and uses 25% opacity. Each kitten's colored menu button opens its saber popup: color, Light/Dark type, and the selected type's shared effect controls. Hold and drag inside the black **SWING** circle to move the controlled kitten's paw and saber; the circle stays fixed, the white indicator can travel to its visible edge, and releasing smoothly restores the starting pose. The smaller red **SLICE** circle opens the part picker and changes to **CANCEL**; pressing it again cancels selection. The **Settings** button below the kitten rows toggles a window containing shared blade length, volume, grip/alignment, preferences and diagnostics. Per-kitten actions and appearance stay in the overlay. Closing Settings leaves the overlay active.

The **1.24-second slicing stroke** plays automatically after selecting a valid part. It starts as soon as the selected part is locked and the blade is ignited, without waiting for geometry preparation. Cut geometry is calculated once on a background worker. The full swing plays before breakup. Once the pieces are ready, one safe update separates the source from its neighbors; the following safe update replaces it with the fragments. If preparation takes longer, normal flying and overlay saber drags remain available while the pieces finish. Even a preparation failure lets the full animation finish before reporting the error. Target reading and GPU uploads are spread across frames instead of blocking on the entire cut. Slice diagnostics report preparation, native isolation and the final swap separately.

Selection uses the part under the pointer when the **hovered surface point is within twice the visible blade length of its hilt**. This grace allowance measures the ray hit, never the part's center, and scales with the current deployment. It does not extend the blade automatically. Empty space never selects a nearby substitute. Native hover picking also handles generated pieces; old saved fragments receive the same picking fix when loaded. Geometry without a native hover hit uses a bounded ray search after clicking.

All kitten actions use the overlay; the old F7 action shortcuts and control panel have been removed. **Escape** or **right-click** cancels an active target picker. Ignite and Slice sit below-left of Swing, the somersault diamond sits below it, and Force/Angry Force sit below-right of the left movement circle.

The overlay's left side has independent **FORCE** and **ANGRY FORCE** buttons. Press either, then click a vessel, debris or nearby physical ground object such as a rock. Force captures that object; Angry Force also captures surrounding vessels, debris and physical clutter within **2 m of the selected surface point**, preserving the group's arrangement. The kitten raises its free left paw while the group follows its position and orientation. A saber can remain equipped and lit throughout.

Hold and drag the left **MOVE** circle to translate the group in the camera plane; scroll while dragging to move it closer or farther. Releasing the mouse keeps the current offset. The next drag starts from there. The two buttons become **RELEASE**, which resumes ordinary physics, and **THROW**, which adds outward velocity away from the kitten before releasing. Right-click or Escape cancels target selection. Selecting a slice cancels the Force picker, and vice versa; the held group and saber equipment remain independent.

Force supports vessels and native debris on the ground and in orbit. Nearby clutter with a loaded physical collider uses KSA's own conversion into a movable physics object, including rocks already displaced or resting on the ground. Decorative clutter without collision and the terrain itself cannot be captured. Clutter support is local and session-only; it adds no orbital conversion, long-distance streaming or persistence. Changing the controlled kitten, boarding or loading clears captures. Hiding the HUD ends mouse gestures but keeps the group held; disabling Purr2D2 releases it. Native in-game acceptance remains pending.

The current limits are 32 captured objects across both kinds, a 1,000 m vessel picking range and 25 m of total movement from the initial group position. Clutter must be present in local physics. Dragging provides up to 8 m per gesture; each wheel tick changes depth by 0.5 m. Throw adds 25 m/s outward to each object's inherited motion. Angry Force measures vessel mesh surfaces and clutter collision surfaces, so an edge of a large object can qualify.

Reach is checked when selecting the part and again before the stroke starts, using the live hilt position. Moving away or changing the flight-computer target after a valid stroke does not undo it. Switching kittens, boarding, loading, stowing or cancelling clears the pending action. Cancellation before isolation leaves the vessel connected. During the short interval between isolation and replacement, cancellation keeps the source part and its contents, but native connections already separated are not rejoined. The animation waits while its main-view pose is hidden; once the complete stroke has been shown, looking away does not delay a ready breakup. The action does not steer the kitten or modify the flight computer's attitude controls. Autopilot, ring tracking, contact heat and automatic contact cutting have been removed.

The effects continue with the HUD hidden. Pause suspends blade motion, contact emission and sound. Supported simulation speeds extend to **5×**; ignition and swings run at normal player-time speed, independently of physics-step timing. Contact probing skips applied steps above 0.25 seconds. Ladder climbing, swimming, tumbling and righting stow the saber.

The panel retains blade length (**0.25–12 m**), volume and paw alignment; per-kitten color/type and effect controls live in the overlay popup. Length, volume and alignment apply to all equipped sabers; overlay drags never change saved alignment. Popup color and type edits affect only that kitten. Effect edits change every saber of the selected type. New kittens cycle through red **(143,16,16)**, green **(13,81,1)**, yellow **(49,49,5)** and purple **(98,9,96)** in 8-bit RGB. **Save preferences** writes shared settings to `settings.json` beside the installed mod. Old contact/autopilot preferences are ignored. Equipped sabers remain with their kittens when control switches; boarding, loading or entering the editor releases their resources. Equip choices, individual colors and per-kitten types last for the current enabled session; disabling the mod clears them. New kittens start with Light sabers; choosing Dark does not change their color.

Without saved preferences, defaults are **1.4 m** blade length, grip **(-24.5, -14.7, 16) cm**, blade direction **(0.22, 0.45, 0.6)** and finger curl **0.12 rad**. Existing saved alignment and length are retained.

## Slicing and effects

Lit blades have a subtle travelling shimmer and brief electrical arcs around the blade. The bright core stays readable while the surrounding glow ripples. Arcs follow the paw and current blade length, inherit each kitten's blade color, and fade out at a distance. These cosmetic effects use player time, including while paused, like the existing cooling sparks.

Open a kitten’s saber popup to adjust shimmer amount/speed and arc count/frequency/amplitude/brightness/width for its selected **Light** or **Dark** profile. The shared-effects heading identifies which type will change. **Save preferences** retains both profiles across restarts; **Reset effects** restores only the selected type, leaving the other profile and unrelated preferences intact. Set shimmer amount or arc count to zero to disable that effect; zero shimmer speed freezes its motion. Arc count is a maximum (0–8); distance and short deployment can reduce the visible count. Higher arc frequency makes bursts more frequent and shorter without speeding up their path motion.

Light sabers use smooth ribbon paths and gentle, consistent shimmer; Dark sabers use jagged arcs and coarse, erratic shimmer. The types have independent shader entry points and cached pipelines with a shared 128-byte interface. Electrical ribbons use the existing depth-tested HDR pass and are batched into at most one additional draw per eligible blade per viewport, with bounded arc geometry and reduced detail at a distance. They add no particle emitters or dynamic lights. Background heat distortion is not part of this effect; native appearance and frame-time acceptance still require an in-game check.

The slice clips the actual native mesh triangles, preserves UVs/materials and glass layers, and seals new surfaces. Supported open hull panels receive a thin backing. The resulting native wreckage has generated colliders, volume-weighted mass, principal-axis inertia and conserved momentum. Hot cut rims cool into persistent dark caps; a gentle separating impulse opens the seam. Geometry failures report a reason and leave the original intact.

Each new fragment combines surfaces sharing a material and glass setting into one visual subpart, with cut caps kept separate. This reduces render modules, draw submissions and preparation steps without changing the exported triangles or materials. Existing saved fragments keep their original assets.

First-cut preparation also warms the expensive slicing code in the background. Exporting avoids a throwaway identity GLB and redundant buffer copies while retaining full picking geometry. Delayed diagnostics separate native phases, compilation, allocations, GC pauses, engine follow-up and subsequent solver waits. See [slice performance measurements](docs/slice-performance.md) for interpreting these logs and comparing equivalent cuts.

Cuts vary their angle and position and try a coarse zig-zag seam first. Difficult geometry falls back to a valid angled or straight section. Generated pieces can be selected and cut again; the stock Gemini7 tank is checked through halves, quarters and eighths using the exported meshes. Untouched triangles keep their original tessellation so repeated cuts avoid unnecessary mesh growth.

Fuel tanks, capsules and service bays/modules are supported by this path, including occupied vessels. Fuel is emptied only from the isolated source part. Functional modules and attached capsule IVA furnishings are discarded; their dry mass contributes to the wreckage. Crew in the destroyed part follows the game's normal destruction behavior; crew in separated neighboring parts stays with those parts. Unbakeable dents, deployed parachutes, invalid cut contours and unsupported connection graphs remain technical limitations. Collision uses an enclosing convex hull per fragment, so visible recesses or holes can still block objects.

Contact sparks use a separate read-only probe: at most 20 samples per second, a bounded mesh/triangle search, reused storage and one triangle recheck for a sustained touch. There is no heat accumulation, coverage record, scar history or repeated slicing attempt in the contact loop. The Structural editor category includes **Purr2D2 Practice Billet** and **Purr2D2 Practice Hollow Tube** for repeatable testing.

Generated fragment GLBs/XML live under **`Generated/Slices/` in the installed mod directory**. Keep that folder when updating and copy it alongside saves containing sliced parts: native saves store template IDs, not geometry. Existing entries are retained for other saves; budgets can refuse further slices. Saved cut faces reload cold. See [cutting architecture](docs/research-cutting.md), [slicing details](docs/research-slicing.md) and [acceptance status](docs/ACCEPTANCE.md).

## Build

Requires the .NET 10 SDK and an authorized matching set of KSA DLLs. Game and loader DLLs are compile inputs and are never included in the mod package.

The default discovers `../ksa-game-assemblies/current/dll/`. Alternatively set `KSA_DLL_DIR`, pass `-p:KSAFolder=/absolute/path/to/dll`, or copy `Local.Build.props.example` to `Local.Build.props` and fill in your path. StarMap.API and Harmony are pinned compile-only NuGet dependencies.

```sh
dotnet build Purr2D2.slnx -c Release
```

The Release build produces the installable folder `artifacts/stage/purr2d2/`, `artifacts/dist/purr2d2-0.2.0.zip`, and its `.zip.sha256` checksum using built-in MSBuild tasks. No packaging scripts or Python installation are required. The archive name follows the project version; `mod.toml` must match that version or the build fails. Add `-p:CreateModZip=false` for just the folder. Debug builds compile without changing the Release distribution; `dotnet clean Purr2D2.slnx -c Release` removes its generated folder, ZIP and checksum.

Every Release build recreates the folder from the project's explicit file set, so stale files and host DLLs cannot survive a rebuild. The ZIP contains one `purr2d2/` folder. Extract that folder into your configured KSA/StarMap mod directory and enable the `purr2d2` mod ID. A game restart is needed. Preserve existing `settings.json` and `Generated/Slices/` when updating.

Optional development checks are separate from building the distribution. `--run-tests` also requires `glslangValidator` from the Vulkan SDK, found on `PATH`, under `VULKAN_SDK`, or in a standard `~/VulkanSDK/*/macOS/` installation. It compiles and links the saber shaders, checks the reflected 128-byte push-constant layout against the production C# declaration, and exercises VFX detail/time bounds without starting KSA:

```sh
python3 tools/validate.py --run-tests --stage artifacts/stage/purr2d2
python3 tools/check_fragment_templates.py
python3 tools/check_force_host.py
```

This repository contains exactly one runtime `.csproj`; validation generates temporary harness projects under ignored `artifacts/`. The fragment-template check uses the actual managed KSA XML types without starting graphics or physics. On ARM hosts it adapts only the PE machine fields in an ignored copy of the unsigned IL-only game assembly; the original game files and mod distribution are unchanged. The solution and all mod code target `net10.0`.

## Assets and implementation

- `Assets/Models/` and `Assets/Textures/`: original hilt and PBR maps, generated by `tools/create_hilt_portable.py`; inspected preview and metadata under `art/`. The optional Blender importer/studio script is `tools/create_hilt.py`; no `.blend` is included because Blender could not start in this environment.
- `Assets/Shaders/`: depth-tested HDR core/halo and spark rendering. Native point lights illuminate nearby geometry. Flexo's native emissive **PartModel** approach is also documented in [rendering research](docs/research-rendering.md).
- `Assets/Audio/`: a supplied idle recording prepared for mono spatial playback, Light/Dark ignition and ambient Ogg recordings, the supplied saber close sound, plus generated supplementary contact audio. See [audio provenance and preparation](docs/research-audio.md).
- `src/Pose/`: independent saber and Force hand overlays; preserves stock root/leg animation and derives the saber emitter from the live paw skeleton.
- `src/Force/`: bounded object picking, rigid group motion, solver exclusion and release/throw handoffs.
- `src/Runtime/`: one StarMap entry/controller; a checked `PrepareFrame` insertion after applied input and before new workers ensures structural mutations happen safely. It leaves `GetJobSimStep` available to other mods' wrappers.
- `src/Cutting/`: on-demand target selection, automatic slice-plane planning and validated native mesh replacement.

Start with the [implementation plan](docs/IMPLEMENTATION_PLAN.md), then [compatibility map](docs/COMPATIBILITY.md) and [acceptance record](docs/ACCEPTANCE.md). Source code is MIT; supplied audio provenance is recorded separately.
