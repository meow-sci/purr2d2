# Acceptance record

## Undrop arrival easing

Undrop captures the dropped saber's current world frame and flies it toward the current hand pose using cubic ease-out, slowing to zero relative arrival speed over the existing 0.45 seconds. The return follows a moving hand, preserves orientation/scale interpolation and has no distance cutoff. This replaces the previous symmetric easing curve.

Focused drop checks pass (31 assertions), as do kitten lifecycle checks (202 assertions), including decreasing travel over equal time intervals, exact endpoints, no overshoot, negligible terminal speed, recall after a million-metre drift, and following a moving hand without changing the captured starting frame. The Release build has zero warnings/errors; source/stage validation, ZIP byte equality, checksum and staged DLL equality pass. Evidence: `artifacts/undrop-easeout-{core,kittens,build,package}.log`. The full managed matrix was not rerun for this easing-only follow-up. Live visual acceptance remains pending: recall nearby and distant sabers while standing and moving, and inspect the deceleration into the paw.

## Saber drop, visual scaling and ground-object cuts (2026-11-01)

The BFL9K controls now expose **Drop / Undrop**. Drop extends the arm, turns the open paw downward and releases a hidden native vehicle carrying the hilt's collider and mass. The visible hilt, blade, lighting and spatial audio follow that vehicle, preserving ignition and inherited motion. Native physics supplies gravity, collision and free drift. Undrop removes the physical proxy and animates the saber back to a fresh hand pose; an offscreen hand waits for a pose before completing recall. Detached sabers cannot perform held attacks or clashes. Unequip, owner loss, reset and unload clean up the proxy. Saving recalls transient dropped equipment before native save capture, retaining the per-kitten equipment and ignition choices. The collision shape covers the hilt, not the blade.

The canonical length limit is **50 m**. Manual contact detection and the saber pose have no locomotion-mode restriction, including jumping and somersaults. Sabers and capes derive their full affine scale from the final native character model transform, covering body scaling and visual-only/nonuniform scaling without any Unscience reference, reflection or shared contract. Blade reach, thickness, lights and contact sampling follow that observed scale. Cape cloth resets only when the observed scale changes; ordinary motion retains the simulation.

Manual sweeps and assisted clicks can cut native ground clutter. Capturing is bounded on the main thread, geometry/export preparation uses the background worker, and native replacement creates and validates every fragment before excluding the original. Rocks use the contact plane, with a centered retry when necessary. Split fragments conserve mass and receive a small equal-and-opposite separation velocity. Generated cut caps use the existing cooling glow. Exterior material IDs retain native RGB10 tint and luminosity across generated-asset reuse, save restoration and recuts. An early, guarded native shader extension applies that tint only to marked fragment exteriors; unknown shader contracts retain native displacement instead of creating incorrectly shaded fragments.

Trees with compatible opaque geometry can split and emit bounded flame particles at moving cut ends. **Tree cut fire duration** defaults to 10 seconds and accepts 0–60 seconds, with zero disabling fire. Native foliage uses opacity, transmission and/or double-sided rendering that the fragment material path cannot reproduce. Such trees, including the full tested stock tree, deliberately fall back to native whole-tree physical displacement and a small toppling impulse. This fallback preserves foliage and has no artificial cut-end fire. Assisted selection respects nearer parts, terrain, kittens and clutter; a refused part still occludes objects behind it.

Automated evidence includes native KSA binding/proxy-template checks, isolated runtime/pose/audio/save regressions, uniform and nonuniform visual-scale checks, real stock rock contact and centered cuts, a real stock trunk split, and full-tree foliage fallback. The stock geometry remains under ignored `artifacts/` and is not redistributed. The tint checks compile and link the actual native opaque, wet, dynamic-unmarked and raytraced shader variants. These checks do not run a GPU scene or a live game.

Final validation covers all **41 managed suites**, with no outstanding failures. The broad run initially found an overly tight float-derived pose tolerance and the old v4 fragment identity goldens; both were corrected, and the complete CoreChecks and FragmentAssetChecks suites then passed separately. The v5 goldens were independently derived from the authored fixture and frozen collider, reproducing the old v4 values before deriving the new ones. Evidence: `artifacts/feature-managed-final.log`, `feature-core-final.log`, `feature-fragments-final.log` and `feature-host-checks.log`. The final Release build has zero warnings/errors; source/stage metadata, saber shader compile/link and the 128-byte ABI pass. The ZIP matches staging byte-for-byte and its checksum verifies (`feature-release-final.log`, `feature-package-final.log`). Additional reproducible checks are `tools/check_clutter_slices.py` with a local game `Content/Core` containing GLBs, and `tools/check_clutter_tint.py` with the current reference `Content/Core` shaders. Implementation used GPT-6.1 Sol / xhigh agents; independent GPT-6 Astra / xhigh review identified and verified fixes for scale, tint and foreground-occlusion issues.

Native gameplay acceptance remains pending:

- [ ] Drop lit and unlit sabers on terrain, moving vessels and in space; inspect the extended palm-down open paw, hilt collision, inherited motion, stable blade/light/audio and animated recall. Repeat while airborne, paused, switching kittens and with the original kitten offscreen; save/reload and remove the owner.
- [ ] Sweep a blade through parts, rocks and trees while jumping and somersaulting. Test both assisted and manual cuts with nearer occluders, refused parts and terrain.
- [ ] Change Unscience body and visual-only scale before and after equipping, while several kittens have capes, and while a saber is dropped. Inspect hilt fit, 50 m nominal reach, thickness, lights, contact reach and cape motion. Repeat with Unscience absent and both load orders.
- [ ] Cut rocks at off-center contacts; inspect separation, collision, native surface colors, cooling caps and saved/reloaded/recut fragments in raster and raytraced views. Fell stock foliage trees and verify that leaves remain intact. With a compatible opaque tree, inspect moving cut-end fire at 0, 10 and 60 seconds.
- [ ] Repeatedly cancel preparation, switch worlds and disable/unload the mod; verify no orphan proxies, missing original clutter, lingering flames or stale native owners. Measure frame time with many fragments and scaled kittens.

## Furce per-item rotation handle (2026-10-30)

Either Furce capture mode now exposes a solid white handle at 25% opacity just outside the top of THE FURCE. Its diameter is one quarter of the small buttons. Dragging reveals an upper semicircular track: the top is neutral, and each midline endpoint maps to one full signed turn, proportional to arc distance. Release retains the handle and item orientation; new captures reset to neutral. Each held vessel, debris object or native ground object rotates about its own origin along an axis parallel to the kitten's body +X facing direction. Multi-part vessels stay intact. Breakup pieces inherit the displayed orientation and continue rotating from that pose without a double rotation.

Focused managed checks pass for ten mixed captures, differently oriented objects, a rotated kitten frame, fixed individual positions, intact part ownership, signed full-turn endpoints, the selected surface pose target, final release orientation and rotated breakup. UI checks cover size/opacity, proportional arc travel at three viewport sizes, clamping through the bottom angle seam, regrabs before the next handoff, final release sampling, stale capture generations, geometry changes, hidden controls, Escape and focus loss. Input callbacks publish intent; the existing settled physics handoff applies it. Core, scroll routing and actual KSA host checks pass. Release builds with zero warnings/errors. Evidence: `artifacts/checks/furce-rotation-{ui,controller,core,host,build}.log`.

Native visual acceptance remains pending: capture one intact multi-part vessel, then a mixed Angry Furce group; drag both directions through the full arc, stop partway, regrab and turn the kitten. Confirm each item rotates in place, the handle stays on the upper track, and camera controls do not move while dragging. Repeat after spreading, moving and breaking up captured vessels; release/throw, switch kittens, hide the overlay and resize the viewport. Inspect the tiny handle and its track at each UI scale and near the adjacent small controls.

## Furce breakup preserves dents (2026-10-30)

Furce breakup no longer rejects dented vessels. Capture retains each native dent field and its source assembly transform; planning copies whole overlapping fields to each output chunk, including peeled branches and fields owned by another part or an invisible container. Hidden, massless carrier subparts keep the fields separate, preserving KSA's per-module dent count and safety scale. Native `ApplySaveData` initializes their private lists and simulation/render state, and a construction hook recreates the modules before native subpart save data is restored. The original mesh references remain unchanged. Pending preparation is invalidated if source damage changes.

The actual KSA host checks pass for compound chunks with eighteen dents across three fields, transformed/recentered assembly coordinates, native vessel-reference XML serialization and `PartTree.Deserialize`, raw fields and effective depths after reload, distant-field rejection, subpart refinement, changed-damage detection and balanced global dent counts after disposal. The managed save/reload fixtures remove only GPU/collider modules, preserving the generated templates, hierarchy, transforms and mass. Existing Furce host checks and fragment-template checks pass. The Release build has zero warnings/errors; source/stage validation and archive/checksum verification pass. Evidence: `artifacts/checks/force-dents-host.log`, `force-dents-fragments.log`, `force-dents-build.log`, and `force-dents-validation.log`.

Native visual acceptance remains pending: dent several vessel parts, break the vessel up at low and high piece limits, inspect dents across chunk boundaries, release/throw, save, restart and reload. Confirm the dents remain and carriers stay hidden in both editor catalogs. Keep `Generated/Slices/` beside the save when updating the mod.

## Runtime networking acceptance

- [ ] On Windows, launch with gatOS/unscience/purrTTY enabled: each entry reports runtime networking ready and only default networking assemblies.
- [ ] Start gatOS: its servers and VM initialize without missing TcpListener members. Repeat after changing mod order and with each mod alone.
- [ ] Confirm normal Purr2D2 game hooks and save/load still work. Managed load-context checks do not establish this native acceptance.

## Generated debris editor visibility (2026-10-25)

Sliced fragments and Force debris must stay registered in KSA's shared part library so native vehicles and saves can resolve their template IDs. The vehicle editor also enumerates that library, and our generated templates lacked its `Hidden` tag. This exposed debris roots in the parts catalog and generated visual subparts in the separate Add SubParts menu.

The detached fragment loader now adds the resolved native `EditorTag.Hidden` to every generated part/subpart. Startup restoration applies the same rule to existing `Generated/Slices/` bundles, covering older fragments and Force debris. Only in-memory editor tags change; template identities, part/subpart roles, generated XML/GLB bytes and normal authored practice parts retain their existing behavior. Exported XML is deliberately unchanged because persistent files must remain byte-identical when reused at the same content ID.

The new visibility regression reproduces the original failure. `python3 tools/check_fragment_templates.py` and `python3 tools/check_force_host.py` pass with the actual managed KSA types, covering fresh fragments, frozen v3 saved fragments, simple/compound Force debris, idempotence and preserved tags/identities. The test harnesses supply the reference snapshot's omitted `System.IO.Hashing` dependency. `dotnet build Purr2D2.slnx -c Release --no-restore` passes with zero warnings/errors and produces the installable folder and ZIP.

Native acceptance remains pending: install the rebuilt mod and restart KSA, slice a vehicle, then create a new vehicle and check both editor catalogs. Save/restart/reload and repeat with existing sliced fragments and Force debris. Confirm debris remains visible and usable in flight, ordinary parts/practice targets remain available, and repeating a cut does not report modified persistent assets. Keep `Generated/Slices/` alongside the saves.

## Manual contact contour recovery (2026-10-25)

Manual cuts retain the preferred sweep plane and original eight small adjustments, then try sixteen wider tilts up to 0.6 radians (34.4 degrees). If all 25 attempts fail, up to three sections aligned with the captured part axes follow, ordered by closeness to the sweep direction. All candidates pass through the confirmed hit, subject to the existing microscopic topology nudge. Axis recovery can substantially change seam orientation and reports that adjustment in the completion message. Every accepted result still passes closure, additive volume, principal-frame, float32, geometry-budget, export and collider checks before publication. Impossible contacts retain the existing refusal and cannot fall back to a center cut or authored-subpart breakup.

Contour proximity now measures actual point-to-segment distance instead of sharing a cross-product tolerance scaled by both segment lengths. A long edge can no longer inflate the contact distance around a tiny edge. A synthetic micron-clearance notch reproduces a false crossing rejection with the original predicate and produces closed, volume-conserving halves with the corrected predicate. Actual crossing sections still refuse without partial output. Folded-source regressions cover both wider-angle and final part-axis recovery, deterministic assets, contact anchoring, conserved mass, invalid axes and cancellation.

The reported `CoreFuelTankA_Prefab_LF1WHalfHB` uses 3,168 authored triangles. Twelve deterministic sampled surface contacts all refused with the original nine-plane implementation. The new worker exports valid fragments for **9/12**, retaining refusal for three contacts that cannot satisfy all checks. Geometry-only acceptance is not counted as an exported cut: two additional sections pass clipping but fail downstream validation. This sample demonstrates recovery, not universal support. The stock harness now includes this small tank, with permanent contact regressions exercising both the 24-attempt wider-angle result and the 26-attempt axis result, including exported/reloaded closure, mass and volume. Its assisted halves/quarters/eighths and eight fresh seeds also pass. All six existing cached stock fixtures pass, including both Height B reload/recuts and the larger tank's repeated cuts.

Small-tank meshes come from the local private asset snapshot identifying build 2026.10.10.5554. Its tank asset XML is byte-identical to the configured current reference, and all six overlapping extracted fixtures exactly match the existing cached stock baseline. The generated small-tank fixture and proprietary assets remain ignored and unpackaged. Evidence: `artifacts/checks/contact-small-tank-original.log`, `contact-small-tank-axis-probe.log`, `contact-small-tank-stock-final.log`, `contact-contour-stock-all.log`, `contact-contour-fragments-final.log`, `contact-contour-core.log`, `contact-contour-build-final.log` and `contact-contour-assets-final.log`. Release build and final source/stage validation pass with zero compiler warnings/errors.

The irrecoverable small-tank contact also has a permanent no-publication regression. Production JIT warmup passes for all 1,303 selected bodies, including cancellation checks, using the locally installed MathNet.Numerics 5.0 dependency omitted from the reference DLL snapshot (`contact-contour-warmup-final.log`). The broader managed/shader run recorded 1,815 PASS lines but remained unsuccessful on the existing Force UI label/tooltip assertion in `tests/force/ForceUiChecks.cs:833`; an isolated recheck repeated that failure. Evidence: `contact-contour-validation.log` and `contact-contour-force-ui-followup.log`. That historical run was not fully green. The October 2026 Unscience compatibility follow-up corrects the stale test expectation: the existing two-line layout places `THE FURCE` above the circle center and its hint below, with both horizontally centered and no hover tooltip. No runtime UI behavior changed.

Native acceptance remains pending: sweep the small Gemini7 tank at varied positions and directions, inspect reported axis-adjusted seams, release/regrab during preparation, verify refusal preserves the source, and inspect quiet replacement, motion and save/load of the resulting fragments. The extra attempts run on the existing background worker; native frame time and worst-case preparation latency have not been measured.

## Cape only (2026-10-24)

The user removed the hood from the feature. Each enabled kitten now loads, updates and draws only its cape; the hood renderable, model declaration, model file, generator code and obsolete fit checks are removed. The per-kitten checkbox is **Cape**. The cape geometry, worn Light/black Dark materials, default-off option, global simulation limit and overlay-wide backpack visibility retain their existing behavior.

The controller owns one renderable instead of two, avoiding hood loading, animation updates and drawing in each viewport. The Debug build passes with zero warnings/errors. Evidence: `artifacts/checks/cape-only-runtime-build.log`.

The actual KSA/Brutal loader accepts the cape GLB with zero inverse-bind/rest error. Native cloth motion and pose-cache checks pass, including **0 B** managed allocation on warmed paths. The cape retains 425 vertices, 768 triangles and 117 cloth nodes. Evidence: `artifacts/checks/cape-only-host.log`; the standalone CPU timings exclude game frame time and GPU work.

Asset validation and the Release build pass with zero build warnings/errors. Source/stage validation passes; all **39 archive files** match staging, no hood files are shipped or declared, staged assets/README/DLL match source and Release, and the ZIP checksum verifies. The cape GLB and all six cloth textures remain byte-identical to `b8dfead`. Evidence: `artifacts/checks/cape-only-assets.log`, `cape-only-release.log`, `cape-only-stage.log` and `cape-only-package.log`. The preserved installable archive is `artifacts/dist/purr2d2-cape-only-0.2.0.zip`. Restart KSA after replacing the package to release cached renderables and assets.

## Manual cuts at the blade contact (2026-10-24)

Manual SWING detection retains the earliest confirmed contact on its approximated blade ribbon within the struck mesh, with deterministic ties and a frozen cursor across budgeted updates. Dense meshes use the best confirmed hit after two further updates, 50 ms of refinement, or entry into the final 50 ms of the existing 150 ms query lifetime. Invalidated or expired queries cannot publish. The existing 4,096-triangle, discovery, primitive and frame-read limits and 0.5 ms soft budget remain in force. Detection does not order contacts globally across every nearby mesh.

The contact point and swing-plane normal travel through capture into the worker in the original vehicle assembly frame. Manual clipping tries that plane first, four cardinal tilts at 0.06 radians, then four diagonal tilts at 0.18 radians. All nine planes pass through the same hit. The existing microscopic topology adjustment still applies; an impossible contact cut is refused. Assisted clipping retains its previous candidate policy. A stationary blade pivot and axial thrust have deterministic orientation fallbacks.

Focused regressions pass for entry/exit ordering with reversed triangles, coplanar contact, thrust, stationary pivots, deferred refinement, late first hits, expiry, moving/scaled targets, reflected subpart forwarding, cooldown/lifecycle behavior, anchored exported fragments and unchanged assisted slicing. The full managed/shader suite passes; the worker checks were rerun after the final candidate-angle adjustment. The Release build passes with zero warnings/errors. Evidence: `artifacts/checks/contact-target-focused.log`, `contact-runtime-focused.log`, `contact-worker-final.log`, `contact-anchored-validation.log` and `contact-anchored-build-final.log`.

Two actual surface contacts on each of six cached stock fixtures (tank, capsule and service modules A–D) produce **12/12 successful cuts**, with 35 total worker candidate attempts. All 12 exported cap checks pass; the largest cap-vertex distance from the output plane is **6.75e-8 m**, and every output plane passes through its requested hit. Evidence: `artifacts/checks/ContactStockChecks/symmetric-policy-results.txt`. The isolated tested candidate helper is byte-identical to production. These fixtures do not guarantee every contact or topology can be sliced.

Warmed Release comparisons against the previous implementation used the actual probe with the same host stubs, tiering disabled, and median times from nine batches of 200 updates:

| Fixture, with the 0.5 ms soft budget | Previous ms/update | Updated ms/update |
| --- | ---: | ---: |
| 24 triangles, early hit | 0.0008 | 0.0018 |
| 4,096 triangles, late hit with bounds-rejectable misses | 0.3196 | 0.1182 |
| 8,192 triangles, early hit followed by refinement | 0.0008 | 0.0799 |
| 4,096 overlapping triangle bounds, no hit | 0.4531 | 0.4424 |

Triangle intersection tests allocate 0 B. The retained query adds 88 B of fixed state, with no allocation per tested triangle. Earlier acceptance can now wait for bounded refinement. These synthetic CPU measurements exclude native scene access, rendering and GPU cost; they are not an in-game frame-time guarantee. Evidence: `artifacts/checks/contact-performance-final.log`.

Native acceptance remains pending: swing across visibly different points on the practice billet, tank and previously cut fragments; verify that opening starts at the touched area and follows the swing. Check dense meshes, moving/rotated/scaled targets, release/regrab and sustained manual swings, and compare frame times with the previous build.

## Forward hood drape, worn brown cloth and backpack visibility (2026-10-24)

The second in-game review showed the resting cape intersecting the MMU backpack and the hood's front opening exposing too much helmet. Backpack visibility now follows the mod's enabled session for all EVA kittens, independently of saber/cape equipment. It restores each attachment's prior visibility on disable, removal/boarding, world reset, fault or disposal; originally hidden attachments remain hidden. Stowing a saber and temporarily hiding the HUD no longer reveal the backpack. The native flag controls attachment animation/drawing, not RCS input or flight physics.

Focused per-kitten checks pass for un-equipped and uncontrolled kittens, original enabled/disabled states, native re-enabling, repeated toggles, fault cleanup, saber stow, boarding and reset. A warmed 128-refresh measurement allocates **0 B** and uses exactly one existing fleet scan per refresh. The standalone Force/saber pose tests and cape settings persistence checks also pass. Evidence: `artifacts/checks/hood-refine-kittens.log`, `artifacts/checks/hood-refine-force-pose.log`, `artifacts/checks/hood-refine-cape-settings.log`.

At commit `b8dfead`, the hood rounded and offset its crown, covered more of the forehead and had uneven brow edges and side folds, with one side hanging 50 mm lower. The stock-mesh check covered **372/376 forehead samples**, kept **77/77 central-face visor samples visible** with a **6.7° margin**, and measured **33.5 mm minimum radial helmet clearance**. The 13 covered visor samples had at least **46.7 mm radial clearance**. The rear cowl overlap point was unchanged. Radial clearance is not minimum Euclidean distance. Historical evidence: `art/cape-helmet-fit-metrics.json` at commit `b8dfead` and `artifacts/checks/hood-refine-fit.log`. This raised-hood design is superseded by the small lowered hood above. The local stock-mesh calibration was an offline preview, not an in-game capture; stock meshes are not distributed.

Light-side cloth now uses muted dusty brown with broad fading, gentle grime and irregular fine fibers. Its separate normal and matte ORM maps remove the old embossed grid; maximum decoded normal tilt is **2.03°**. All maps remain 256 × 256. The two added shared maps add no draw calls, material instances, shader samples or simulation work. Dark diffuse and the original shared normal/ORM maps remain byte-identical. The full generator produces byte-identical outputs across two runs. Evidence: `artifacts/checks/hood-refine-textures.log`, `hood-refine-assets.log`, and `art/brown-cloth-preview.png`.

The actual KSA/Bepu and GLTF harness passes with both final models, zero inverse-bind/rest error and unchanged cape motion. The hood retains **430 vertices / 800 triangles / one bone**; cape geometry, simulation limits and disabled gates are unchanged. Two warmed cape updates take **0.421 ms median / 0.546 ms p95**, with **0 B** managed allocation in this run. These are standalone CPU measurements, not in-game frame times or GPU timings. Evidence: `artifacts/checks/hood-refine-host.log`.

Debug and Release builds pass with zero warnings/errors. Source, staged assets and metadata validate; all **40 archive files** match staging, staged assets/README match source, the staged DLL matches the final Release DLL and the ZIP checksum verifies. Evidence: `artifacts/checks/hood-refine-debug.log`, `hood-refine-release.log`, `hood-refine-stage.log`, `hood-refine-staged-assets.log` and `hood-refine-package.log`. The installable archive is `artifacts/dist/purr2d2-0.2.0.zip`.

Native visual acceptance remains pending: inspect the new Light finish in bright and dim lighting, forehead/face clearance from several angles, neck overlap while walking and turning, and backpack restoration when disabling Purr2D2. Restart KSA after replacing the package so cached models and textures reload. This pass reran the focused cape/pose/runtime checks; broader regression results below belong to their recorded earlier runs.

## Cape and hood fit correction (2026-10-24)

The in-game screenshots showed the original hood mostly hidden inside the helmet and a cape extending below the feet. The replacement hood follows the current native helmet attachment matrix, with an explicit metres-to-centimetres conversion and the existing head-socket fallback. Its crown is taller, the open face clears the visor, and the lower cowl tapers into the cape. The cape now has a curved 48 cm shoulder seam, 86 cm hem width and 50 cm authored drop, placing its static hem about 3 cm above the stock soles.

The optional stock-mesh fit check reads 5,166 helmet vertices and 333 visor vertices from local GLTF references. It intersects same-height rays with exact generated hood triangle sections: 2,965 helmet vertices outside the face opening have at least **27.4 mm radial clearance**, every visor vertex remains inside the opening with at least **4.04° angular margin**, and the hood apex is **127.5 mm above the helmet crown**. Radial clearance is not a minimum Euclidean distance. Historical evidence: `art/cape-helmet-fit-metrics.json` at commit `9e70989`; the current report describes the later forward-drape refinement above. Source asset checks pass; regeneration is byte-for-byte deterministic across nine outputs. The updated software preview uses original measured-size proxies, not stock character meshes or an in-game capture.

The native KSA/Bepu harness passes with the revised geometry. Cached per-node body clearance preserves the curved shoulder seam without forcing its first row backward; airflow and partial lift retain downward pull during movement. At 3 m/s, mean hem height rises from −0.425 m to −0.165 m and rear position moves from −0.249 m to −0.667 m, then the hem settles back to −0.426 m after stopping. Peak structural stretch is at most **1.268×** rest length through forward/backward/sideways/8 m/s motion, and at most **1.196×** during the stopping phases. Pins remain fixed and all results stay finite. The actual native GLTF loader accepts both meshes with zero inverse-bind/rest error, and hood placement checks cover native transforms, multiple view placements, rotation/scale and missing-helmet fallback. Evidence: `artifacts/checks/capes-fit-host.log`.

The cape retains 117 cloth nodes, four bounded native substeps per update and the two-update frame cap. No runtime collision queries or additional solver passes were added. In this focused host run, two warmed cape updates took **0.410 ms median / 0.515 ms p95**, with **0 B** managed allocation per update; these are cloth CPU measurements, not game frame times or GPU timings. Disabled-path, lifecycle, scheduling and global-setting checks also pass. Evidence: `artifacts/checks/capes-fit-kittens.log` and `artifacts/checks/capes-fit-settings.log`.

The Release build passes with zero warnings/errors. Source/stage validation passes, all 38 archive files match staging, staged cape assets match source, the DLL matches Release, and the ZIP checksum verifies. Evidence: `artifacts/checks/capes-fit-release.log`, `artifacts/checks/capes-fit-validation.log`, `artifacts/checks/capes-fit-package.log`. The focused cape/runtime/settings checks were rerun for this correction; the broader regression results below describe their earlier runs.

In-game acceptance is still needed for the revised assets: front/side/rear helmet coverage, the neck overlap while turning, walking and stopping, both materials, MMU equipment and somersaults. Restart KSA after replacing the assets to reload the cached model IDs.

## Cape performance review (2026-10-24)

The global **Cape settings → Maximum simulated capes** preference accepts 0–16 and defaults to 4. Zero restores enabled capes to their static pose at the next settled update, even while paused. Raising the limit shares the existing two-update frame budget; it does not increase that budget. Selection still favors the nearest recently main-view-visible capes, and scheduling follows owner indices so distance-rank changes cannot starve a selected cape.

An explicit registry removes the disabled population scan. With no opted-in owners or pending cleanup, the cape update exits before camera, time, motion or resource work. The native render postfix remains installed with a cheap gate. Distant first-use model setup is deferred until a visible main or secondary game camera is within 60 m; existing models are retained to avoid repeated GPU setup. Idle simulations release once. Shared authored positions avoid repeated trigonometry, and per-cape pose caches recalculate node rotations only after a cloth revision changes. Native skinning output is still refreshed as required by KSA.

The actual KSA/Bepu host harness confirms unchanged motion, fixed pins, finite input handling, reset/disposal, authored pose equivalence for all 117 nodes, and zero managed allocations in warmed simulation/cache paths. In this run, advancing the maximum two capes took **0.412 ms median / 0.492 ms p95**. For 1,000 pose calculations, recomputation took 20.958 ms and revision-check plus cached-node-copy took 0.087 ms; both allocated 0 B. These cache measurements exclude native skeleton processing, skinning and GPU work. Evidence: `artifacts/checks/capes-performance-host.log`.

Focused runtime checks pass for limits 0/1/4/16, 17 nearby enabled owners, changing distance ranks, reducing the limit without scheduling stale candidates, deferred setup through secondary views, hidden/unavailable cameras, setup at the exact 60 m boundary, pause/warp, quick toggles, boarding, teardown and fault recovery. Warmed empty and enabled manager paths allocate 0 B. Settings checks cover defaults, older JSON, bounds, save/load, autosave and reset. Evidence: `artifacts/checks/capes-performance-kittens.log` and `artifacts/checks/capes-performance-settings.log`.

The final Release build has zero warnings/errors. The full managed/shader regression suite passes, with generated fixtures rebuilt against their already-restored dependencies; source and staged assets also pass validation. All 36 archive files match staging, the staged DLL matches Release, and the archive SHA-256 verifies. Evidence: `artifacts/checks/capes-performance-release.log`, `artifacts/checks/capes-performance-regressions.log` and `artifacts/checks/capes-performance-package.log`.

Cold setup remains a separate cost: the first native cloth step in the standalone process took **54.826 ms**, and a later new cape after runtime warmup took **1.719 ms**. Those timings exclude model loading and do not establish KSA frame times. Nothing is prewarmed while capes are off. In-game first-use hitches, steady CPU/GPU cost, and visual fit still require native acceptance.

## Initial cape and hood implementation (2026-10-24)

Each EVA kitten starts with **Cape and hood** off in its existing per-kitten settings window. Enabling it supplies brown cloth for Light and black for Dark, independently of ignition and blade color. The option follows the existing session-only per-kitten appearance lifetime. The cape uses an original shoulder-pinned grid with KSA's native parachute cloth solver; the oversized open hood follows the final head pose and uses authored folds. Both use native two-sided skinned PBR materials, prepass and shadows, without changing shared glTF materials or the stock kitten/parachute data.

The 117-node cape uses artificial gravity and bounded surface-relative airflow in every environment. Forward walking lifts it behind the wearer; stopping restores its drape. A conservative back plane prevents the sheet crossing through its wearer. There are no world collision queries or self-collision. The cape and hood together contain 855 vertices / 1,568 triangles and use four shared 256 × 256 maps. `art/cape-cloth-preview.png` is a software asset preview with a display-only helmet guide, **not an in-game screenshot**.

The scheduler selects at most four nearby, recently main-view-visible capes within 25 m. It targets 30 Hz, fairly staggers at most two cloth updates per frame, and discards catch-up work. Each update batches up to four 120 Hz substeps into one native call. Other capes release their native simulation after 1.5 seconds of idle time and use their rest pose; rendering ends at 60 m. Pause freezes cloth, unsupported warp resets once, and secondary views consume the same simulation. Off by default creates no cape controllers, renderables or cloth simulations. Per-kitten failures latch until an off/on retry; an unexpected cape-manager failure cannot suspend saber functionality.

Focused runtime checks pass for default-off and per-kitten intent, near/far visibility, nearest-four selection, the two-call limit and fair scheduling, pause/warp, opt-out, boarding, world teardown and failure recovery. The actual managed KSA/Bepu checks cover standing/forward/backward/sideways motion, stopping, fixed pins, bounded high-speed stretch, non-finite input, reset and disposal. The actual KSA/Brutal glTF loader extracts 118 cape bones and one hood bone; all rest skin matrices have zero measured error and all skin-index slots, including zero-weight slots, map correctly. Asset checks cover geometry, weights, normals, tangents, open-face topology, material references, textures and deterministic regeneration.

Warmed Release cloth-only measurements on this ARM64 development host, with 180 warm-up updates and 600 measured updates per case:

| Capes advanced in one batch | Median | 95th percentile | Managed allocation/update |
| --- | --- | --- | --- |
| 1 | 0.223 ms | 0.257 ms | 0 B |
| 2 (runtime maximum per frame) | 0.414 ms | 0.481 ms | 0 B |
| 4 (stress measurement) | 0.739 ms | 0.805 ms | 0 B |
| 8 (stress measurement) | 1.475 ms | 1.581 ms | 0 B |

These are steady cloth CPU timings, excluding initial loading, render submission and GPU work; they are not measured KSA frame times. Evidence: `artifacts/checks/capes-cloth-host.log`. The batched-call optimization reduced repeated solver setup while retaining the same bounded substep count and passing the same behavior tests.

The final Release build passes with zero warnings/errors. The full managed/shader regression suite and source/stage asset validation pass. All 36 archive entries match staging, the staged DLL matches Release, and the ZIP's SHA-256 verifies. Evidence: `artifacts/checks/capes-release.log`, `artifacts/checks/capes-validation.log` and `artifacts/checks/capes-package.log`.

Native acceptance remains pending: inspect shoulder/helmet fit, MMU clearance, cloth lighting and shadows for both styles and multiple kitten rigs; walk, run, stop, move backward/sideways and somersault on Earth and airless bodies; compare pause/warp, camera changes and multiple viewports; test more than four nearby capes and distance transitions; exercise opt-out, boarding, global disable and world reload. Compare game CPU/GPU frame times and memory against all-capes-off, including repeated toggles and stock deployed parachutes.

## Dark saber welding sparks (2026-10-24)

Dark sabers emit short colored slag streaks from the deployed blade. Each burst retains its small emission offset relative to the kitten and follows the kitten's current translation in each viewport, with the spray orientation fixed at birth. Subsequent paw rotation does not drag existing sparks. Simple analytic spray/fall and quadratic cooling use player time. Orbital velocity is never integrated with that visual clock: sparks stay near the saber on the ground, in orbit, while paused and during time warp. The shader multiplies the saber RGB directly, without a white core. Light-type BFL9K blades do not emit this effect.

Dark's shared effects expose amount, frequency, size, brightness, lifetime and spray speed, with autosave and profile reset. Fixed arrays cap all sabers together at 16 live bursts, eight sparks per burst and 32 tracked sources. Each burst uses one instanced draw of four vertices per spark in the existing glow pipeline. Distant/subpixel sources and offscreen bursts are skipped, with a 12–24 m fade. Excess work is dropped without catch-up. No emitters, GPU buffers, textures, physics queries or lights are added. Native Particles off suppresses the effect.

Focused VFX checks pass for bounded defaults/invalid tuning, zero-effect controls, distance fade, repeated viewport submissions, frequency edits, clock resets, missed windows and zero-allocation scheduling. Core settings checks cover legacy defaults, all six saved values and reset. Both GLSL pairs compile/link with the reflected 128-byte interface intact. The complete managed/shader regression run passes (`artifacts/checks/dark-sparks-validation.log`). Release builds with zero warnings/errors; source/stage validation passes, all 29 packaged files match staging, and the Release DLL and archive checksum match. Other logs: `artifacts/checks/dark-sparks-settings.log`, `dark-sparks-release.log` and `dark-sparks-stage.log`.

Orbital follow-up checks pass for the production emission frame at 0–100,000 m/s, paused/normal/1,000× simulation advance, multiple camera translations, fixed birth orientation, invalid input and zero allocations. A Release build of the committed baseline plus the spark fix passes with zero warnings/errors; source/stage validation and package integrity pass in that temporary snapshot. This isolates the fix from unrelated unfinished Force edits that prevented the shared-tree build. Logs: `artifacts/checks/spark-orbital-vfx.log`, `spark-orbital-isolated-release.log` and `spark-orbital-stage.log`.

Native ground behavior was confirmed by the user; the orbital follow-up still needs an in-game retest. Compare landed and orbital kittens at high speed, including pauses, time warp, swings and multiple viewports: particles should retain the same small spray/fall around the kitten without orbital streaks. Also inspect saturated red/green/purple sabers for matching-color bright births and complete fading; compare Light/Dark, ignition/retraction, Particles off and world reload. Adjust all six controls, disable each emission control with zero, restart and reset Dark to verify persistence/isolation. Compare frame times at defaults and all maxima with many equipped kittens; the hard budgets are verified structurally, not a measured GPU time guarantee.

## Repeated manual cuts and Force group spacing (2026-10-24)

An active manual cut uses a green square made from corner brackets. White “slicing...” sits above the white part name, both centered above the marker. Projection, owner/source validity and viewport clipping still apply. The Slice grace / reach tooltip uses the requested blade-length multiplier description.

A held Swing gesture can accept another rendered-surface contact after the active transaction finishes and a 0.25-second player-time cooldown passes. Detection stays paused during the transaction and cooldown, then restarts from a fresh pose/probe. Refusals and failures also throttle retries. Release and return do not create new cuts; accepted work can finish after release. Lifecycle invalidation retains the existing settled cancellation path.

Move Force reads Shift at wheel-event time. Shift-wheel up compresses captured object positions around the capture-time group centroid; Shift-wheel down expands them. The operation preserves object geometry/orientation and the group's translation, works across vessels and native clutter, and smooths toward a bounded target in the settled update. Plain wheel input retains depth control. Release and Throw use the displayed spacing; capture/lifecycle resets restore the initial spacing.

THROW is a transient upward slider: every press begins at 25 m/s, dragging upward selects up to 200 m/s, and the matching mouse release snapshots and queues one throw. A click-and-release preserves the original 25 m/s behavior. The chosen speed appears beside the nub and never persists between gestures. Hiding/focus loss, modal/control/lifecycle changes and Escape cancel the gesture rather than throwing.

Focused checks pass for manual-contact gesture/runtime and targeting, manual marker layout, Force arithmetic/controller/UI and the transient throw drag. They cover .249/.250 cooldown boundaries and fresh poses, sequential held cuts without overlap, explicit cancel/release paths, initial-idle Shift-wheel, centroid-preserving mixed spread, default/max throw speeds, one-shot release snapshots, cancelled gestures and active-pill rendering.

The final Release build has zero warnings/errors. Source/stage validation passes; all 29 archive files match staging and the archive SHA-256 verifies. Build and package logs: `artifacts/checks/slice-force-release.log` and `artifacts/checks/slice-force-stage.log`. The package is `artifacts/dist/purr2d2-controls-0.2.0.zip`.

Native acceptance remains pending: inspect marker labels near viewport edges and on long part names; keep Swing held across multiple accepted/refused cuts, release while a cut prepares, and confirm the cooldown; compress and expand mixed Force captures while dragging and changing Shift, then release/throw and switch owner; click-release THROW at 25 m/s, drag to 200 m/s, release outside the pill, and cancel through each lifecycle path.


## Mockup overlay refinement (2026-10-24)

Force warning/error text is centered above Move Force with a 25-scaled-pixel gap; ordinary picking, capture, holding and release chatter stays hidden. Saber text is centered 25 scaled pixels below the lowest jump button, in a wrap area 50% wider than before. Both areas center each measured line independently and keep explicit paragraph breaks. Force settings show rounded current values when closed and beside the active nub, with no numeric wrapping. Display rounding preserves the actual distance setting. Count and Distance have the requested short tooltips.

Force controls run from Count and Distance near the bottom toward Force and Angry Force on the right, with Angry Force raised along the circle. The existing edge gaps are preserved. The jump diamond moves upward by three quarters of a button diameter. Blue action rims use the same 25%-opacity white as the black controls. Status boxes, expanded pills and outside value labels participate in viewport bounds and overlay position clamping.

Focused CoreChecks, ForceUiChecks, ForceControllerChecks (169 assertions) and OverlayTextChecks pass. Coverage includes warning severity and clearing, closed/active rounded values without wrapping, exact tooltips and rim colors, per-line centering, explicit newlines, Unicode-safe long-word wrapping, button spacing, menu/viewport bounds and custom offsets. Narrow viewports compact the Force group to prevent overlap and retain space for full-size numeric labels; 1920×1080 and 800×600 retain the shared control scale.

The final Release build has zero warnings/errors. Source/stage validation passes; all 29 ZIP entries match staging, the staged DLL matches Release, and the archive SHA-256 verifies. Build and package logs: `artifacts/checks/mockup-ux-release.log` and `artifacts/checks/mockup-ux-stage.log`.

Native acceptance remains pending: inspect the layout and multiline alignment at different UI scales, show Force errors and successful actions to confirm quiet normal operation, drag fractional distances while watching closed/open displays, and test both control groups at each viewport edge with custom offsets.


## Overlay polish, automatic preferences and movement smoothing (2026-10-24)

Shared settings now save automatically to the mod's `settings.json` after one quiet second, including Force overlay sliders and both overlay position groups. Orderly shutdown flushes pending changes. Reset all settings deletes saved preferences and restores defaults without recreating the file until another change. Initial graphics reservations still require a restart. Settings uses collapsible Global BFL9K settings, Force settings, Overlay UI Positions and Character & grip groups, with Slice resources last and Reset all settings at the bottom. Tooltips use short paragraphs and wrap.

Manual and assisted pending cuts show “slicing...” beside the part marker. Ignite/Retract shares Slice's gap from Swing. Force's four small controls use that same circle-edge gap both from Move Force and from their neighbors. Movement circles show centered arrows; blue actions share their fill, border and text styling. Black drag circles and Force settings controls/pills have a faint white rim at 25% opacity. Force slider nubs span 80% of the pill width and show the current value in regular-size text to the right, aligned with the nub.

Force drag and wheel targets pass through a bounded, time-based critically damped spring in the settled physics handoff. Releasing the pointer settles toward the requested position; re-grabbing starts at the displayed position. Explicit Release/Throw uses the smoothed position, and capture/lifecycle resets clear movement state. The somersault combo interval defaults to 0.75 seconds and is adjustable from 0.1–1.5 seconds under Character & grip, with automatic persistence.

Managed and shader checks pass. The broad run passed every harness except a layout assertion that assumed a point outside Slice could not fall inside Ignite; the corrected CoreChecks rerun passes while retaining all-pairs circle separation checks. The final Force UI rerun includes rims and active-slider draw ordering. Release builds with zero warnings/errors; source/stage validation passes, all 29 archive files match staging, and the Release DLL and SHA-256 checksum match. Logs: `artifacts/checks/ui-refinements-validation.log`, `ui-refinements-core-rerun.log`, `ui-refinements-force-ui.log`, `ui-refinements-release.log` and `ui-refinements-stage.log`.

Native acceptance remains pending: check visual spacing and slider values at multiple UI scales; adjust settings and overlay sliders, close the window, restart and confirm persistence; reset and confirm defaults after restart; swing into a part and observe the pending marker; drag/scroll and re-grab held groups, release and throw mid-settle; exercise double/triple jumps near the configured minimum/default/maximum intervals.


## Force overlay sliders and capped nearby capture (2026-10-24)

Count and Distance join Angry Force and Force in an arc below the left movement
circle. The new circles match the existing small-button radius and Move's black
50% opacity. Holding opens a same-width downward pill; relative vertical dragging
preserves the value on press, with the thumb's top representing the minimum and
bottom representing the maximum. Releasing collapses the pill. The controls write
directly to the existing 1–512 capture limit and 1–50 m radius preferences, shared
with Settings and Save preferences. Expanded bounds and status space move together
with the Force control offsets. Release, Escape, focus/HUD loss, modal dialogs,
owner changes and layout changes end the slider gesture.

Count overflow now succeeds quietly, superseding the refusal behavior recorded
below. The selected target always occupies the first slot; the remaining slots
take the closest eligible objects across vessels and native physical clutter.
Priority uses squared distance from the selected surface hit to each object's
reference origin, avoiding additional surface scans. Existing mesh/collider checks
still determine radius eligibility. Native overlap collection is bounded and sorts
once, with stable-identity deduplication before the cap. A limit of one skips the
proximity search. Independent geometry/query budgets and transactional rollback
remain in effect.

Focused automated checks cover slider rendering, shared values, integer rounding,
range clamps, out-of-bounds motion and cancellation; layout checks include compact,
scaled and offset work areas. The mixed capture matrix covers caps 1, 32, 128 and
512, either primary type, duplicate identities and reversed enumeration. Actual
KSA/Bepu host checks exercise native overlap ordering and existing physics
contracts. Native visual/gameplay acceptance remains pending: hold each slider
at several values, drag past both ends and release outside it, resize or hide the
HUD during a drag, compare Settings values, save/reload, and capture an oversized
mixed group to confirm count, nearest selection, release/throw and frame times.

The full managed/shader suite and source/stage validation pass
(`artifacts/checks/force-controls-validation.log`). Release packaging passes with
zero warnings/errors (`artifacts/checks/force-controls-release.log`); all 29 archive
files match staging and the Release DLL, and the SHA-256 checksum verifies.

## Bounded runtime mesh growth (2026-10-23)

An exhausted runtime mesh arena can now request growth at the settled pre-draw
handoff. Automatic growth defaults to enabled, with 512/256 MiB vertex/index
ceilings above the 128/64 MiB initial reservation. Settings exposes the toggle,
live ceilings, actual shared-buffer sizes, operation count and status. Initial
reservation changes still take effect after saving and restarting; lowering a
ceiling does not shrink live allocations.

Growth preserves existing offsets and appends owned ranges after all existing
native assets. Only exhausted channels grow. Pending private uploads drain before
replacement; the native queue locks and device-idle boundary exclude submissions
during the copy and publication. Old allocation lengths are copied, with explicit
memory barriers. The ray-tracing descriptor set and its cached rebuild inputs are
updated together. Existing registered fragments remain pinned. Startup historical
assets still load into the native buffers outside the runtime slice reservation;
the UI distinguishes those global sizes from runtime ownership.

Host adaptation is limited to the audited KSA/Vulkan assembly identities and
private field contracts. Allocation and pre-publication failures retain the old
buffers. Unknown copy completion or failed descriptor rollback retains both
generations and disables growth instead of freeing potentially referenced memory.
Canceled pending slices do not request a replacement. Published eviction, buffer
shrinking and lazy historical loading are not part of this change.

Targeted settings/UI checks cover defaults, opt-out, persistence, initial/maximum
bounds and separate global-memory snapshots. Production allocator/transaction
checks cover native gaps, independent channels, fragmentation, integer overflow,
published-offset stability, cancellation, old-size copy contracts, allocation
failure and publication rollback. Live Vulkan and native visual acceptance remain
pending: force several growths with small initial arenas, both with and without
ray tracing; inspect stock meshes, cut opaque/glass fragments, shadows, picking,
thumbnails and secondary viewports. Cancel while growth waits, change ceilings,
resize/rebuild the renderer after growth, save/reload generated fragments, and
exercise allocation failures with Vulkan synchronization validation enabled.

The full managed/shader suite passes (`artifacts/bounded-growth-checks.log`),
including the capture-limit regressions. Final Release build passes with zero
warnings/errors (`artifacts/bounded-growth-build.log`). Actual game-assembly and
production-backend IL checks pass through `tools/check_mesh_growth_host.py`,
including the five-module compatibility guard, frame/queue ordering, RT bindings,
old-size copies, memory barriers and dedicated allocation cleanup. Source/stage
validation passes; all 29 archive entries match staging, the release DLL and
README match, and the archive checksum agrees. These checks do not execute Vulkan.

## Configurable Force capture count (2026-10-23)

The combined Force capture limit now defaults to 128 vessels, debris and physical
ground objects, increased from 32. Settings exposes **Force capture limit** from
1–512 and saves it with the other preferences. A clicked capture retains its
chosen limit through the settled handoff. Changing the setting affects subsequent
captures and does not release an existing hold. Count overflow still refuses the
whole group before any vessel detach or native clutter promotion. Count refusals
name the configured limit and point to Settings; native proximity traversal
budget refusals now report geometry separately.

Core preference checks, Force UI checks and the mixed Force controller fixture
pass. The controller fixture has 205 assertions, including exact limits and
overflow at 1, 32, 128 and 512, vessel-only, clutter-only and mixed groups, either
available primary, duplicate native identities, invalid limits and rollback.
Preference checks cover legacy files, bounds and save/reload; UI checks preserve
the clicked limit when preferences change before resolution. Actual KSA/Bepu
host checks pass. The Release build has zero warnings/errors, and source and
staged asset validation pass.

Native acceptance remains pending: test crowded ground scenes with 128 held
objects and increase toward 512 as appropriate for frame times. Confirm the
Settings control and save/reload, exact-count acceptance and over-count refusal,
release and throw with each primary type. The independent native query and
geometry traversal budgets are unchanged; raising the capture count does not
bypass them.

## Stable Force targeting and slice resource controls (2026-10-23)

Force and Angry Force now display CANCEL while selecting; pressing the active
button cancels, and pressing the other switches mode. Deferred picking carries
the aiming ray relative to the owner so the next physics step does not leave it
behind at orbital speed. Vessel markers follow their selected local surface;
grounded clutter markers retain a planet-fixed anchor instead of a frozen
inertial point. This follows the same current-frame/local-anchor principles used
by Unscience decals, while retaining the settled handoff required by clutter.

Clutter aim tolerance defaults to 0.20 m and is adjustable from 0–0.50 m. A bounded
native sphere sweep runs only when exact picking finds no object or kitten
blocker. It uses the actual collider contact, checks terrain along both the
original ray and the assisted contact segment, and respects the owner's reach.
Zero disables assistance. Stock tree trunk/root colliders are eligible; decorative
trees without colliders remain unavailable. The marker does not read live physics
from the render/UI callback.

Slice resources now expose usage, configurable budgets and cleanup actions in
Settings. Defaults are 128/64 MiB vertex/index memory, 2048 MiB saved assets,
8192 fragment entries and 128 local physics vehicles. Graphics reservations take
effect after saving and restarting; storage and physics admission limits apply
immediately. Completed unpublished mesh allocations can be reclaimed, including
automatically under pressure. Published native assets remain pinned. Debris
cleanup removes only uncrewed, untracked, entirely generated slice vehicles,
after confirmation and a settled-state recheck. It retains registered graphics
memory and saved assets, so exhausting published mesh memory still needs a
restart. Existing assets remain reusable after lowering storage limits.

Geometry admission is also configurable here: defaults of 2,048 closed solids /
120,000 source triangles, with ranges of 128–16,384 / 1,000–1,000,000. Each cut
captures one immutable budget before native copying; shell preparation, planar
and faceted cuts, authored fallback, float32 normalization/audits and export use
that snapshot. Combined output and per-fragment triangle allowances scale at
2× and 5/3× respectively. Existing saved topology loads against supported ceilings
after preferences are lowered; recuts use the newly selected admission limits.
Managed regressions cover the 2,078-solid failure case through production worker
export, valid clipping above 120,000 source triangles, GLB output above 200,000
triangles, multi-island groups, backing expansion, generated/closed fast paths,
fallback aggregate limits, lower output/normalization allowances, snapshot
isolation, UI bounds and preference persistence. The in-game controls and complex
car model still require native acceptance; these fixtures do not measure native
frame times or guarantee that every mesh passes the independent contour/collider
constraints.

Focused regressions cover orbital translation, rotating frames, current markers,
selection identity, tolerance bounds and capture, exact-hit priority, terrain
occlusion, native sphere/box/capsule/compound queries, resource persistence,
allocation retirement/publication, cleanup eligibility and confirmation UI.
Native host checks are recorded in
`artifacts/checks/ForceHostChecks/latest-forgiving-picking.log`. Exact and assisted
clutter queries also pass a native regression with the camera 1500 m away and a
rock 20 m from the kitten, preserving owner-based reach while extending the ray
search bound (`artifacts/checks/ForceHostChecks/latest-camera-range.log`).
The full managed/shader regression suite passes in
`artifacts/force-resources-checks.log`. The final Release build has zero warnings
or errors (`artifacts/force-resources-build.log`); source/stage validation passes,
and all 29 ZIP files, the release DLL, README and archive checksum agree.

Native acceptance remains pending: point and click at orbital vessels, Luna rocks
and Earth tree trunks while moving, zooming and rotating the camera; verify the
marker stays attached and CANCEL is visible. Compare tolerance zero/default/max,
including rocks behind terrain. Repeatedly slice, cancel during upload, inspect
usage and clear unused caches; then confirm debris removal frees physics capacity
without deleting occupied, controlled, camera-followed or mixed craft. Save/reload
remaining generated fragments and verify restart-only graphics settings.

## Saber halo over water (2026-10-23)

Replaced the host's destination-alpha-dependent additive blend with a dedicated
`One + One` RGB blend that preserves attachment alpha. Open-water alpha can be
zero, which previously erased the background under the halo ribbon. Both saber
styles, arcs and sparks use the corrected pipeline. The late translucency hook,
reverse-Z depth test and disabled depth writes are unchanged.

- Release build passes with zero warnings/errors (`artifacts/water-blend-build.log`).
- `SaberBlendChecks` uses the production Vulkan attachment state to verify faded
  edges, HDR emission and overlapping ribbons against alpha 0, 0.25, 0.5 and 1;
  it also reproduces the former zero-alpha black-border failure. Checks pass in
  `artifacts/water-blend-checks.log` and are included in `tools/validate.py --run-tests`.
- Source and staged Light/Dark GLSL compile/link with the 128-byte ABI
  (`artifacts/water-blend-shaders.log`). Asset/stage validation passes; all 29
  archive entries match the staged payload and the archive checksum matches.

Native visual confirmation remains pending: inspect both saber styles and arcs
over ocean, coastlines, terrain and atmosphere, including overlapping blades,
occluding vessels, MSAA changes and a secondary viewport. The faded halo must
leave the background intact and must not alter sunflare/transmittance behavior.

## Saber and Force refinements (2026-10-23)

Light and Dark effect defaults now match the requested profiles, with up to 20 arcs,
frequency 10, brightness 6 and width 4 supported by settings, runtime tuning and
shaders. Existing saved profiles are preserved; reset a profile to adopt its new
defaults. Shared Settings includes a 1–50 m Angry Force radius (10 m default) and
independent horizontal/vertical offsets for the complete left Force and right
saber/movement control groups. Offsets persist with Save preferences and clamp the
groups inside the viewport. Kitten rows and the Settings button stay fixed.

The per-kitten button toggles a regular movable/resizable ImGui window with a title
bar close button and ordinary scroll behavior. It no longer blocks interaction
outside the window. Existing native camera drags and overlay drags retain their
input ownership when crossing it. Ignite is now below and closer to Slice. The
somersault diamond is shifted down by half a button diameter and its neighboring
buttons are closer by the same amount.

Force depth scrolling works while hovering the Move circle as well as during a
drag; repeated events accumulate before the settled physics update. Angry Force
uses the chosen radius for both vessel mesh surfaces and physical clutter, centered
on the selected hit. The Force paw target is halfway lower toward its sampled
resting height while retaining the measured arm solve.

Focused checks cover profile defaults/reset/persistence, expanded VFX limits and
shader ABI, grouped layout bounds/hit regions, hover/drag wheel movement and
release/throw sequencing, radius bounds and pose composition. Actual KSA/Bepu host
checks cover collider overlap boundaries at 1, 2, 10 and 50 m. Logs are under
`artifacts/checks/ForceHostChecks/latest-refinements.log`,
`artifacts/refinements-build.log` and `artifacts/refinements-checks.log`.
The integrated build has zero warnings/errors. All regression groups pass; the
full run caught one kitten-profile assertion still expecting the old Light arc
count, which was updated to 8 and passed in the targeted rerun recorded at
`artifacts/refinements-kitten-checks.log`. Source/staged assets and the final ZIP
checksum also pass validation.

Native in-game acceptance remains pending: verify the new profile appearance,
toggle/move/resize/titlebar-close the kitten window, drag each position slider and
save/reload preferences, inspect the revised button spacing and lowered paw, and
scroll over the Move circle with captured clutter without holding the mouse.

## Overlay activation and refined layout (2026-10-23)

Purr2D2 starts disabled each session, independently of saved preferences. The native game menu adds **Purr2D2 → Enabled**, which activates the overlay. Disabling hides its UI immediately and releases sabers, Force captures, pending cuts and somersault state at the next settled update. A rapid off/on pair still performs that cleanup before restarting. Persisted fragment registration remains available while disabled.

Settings now opens from a dedicated button below the top-right kitten rows. Its window contains shared blade length, volume, character/grip preferences, saving and diagnostics. Per-kitten controls and appearance stay in the overlay; the F7 action shortcuts and old control-panel actions are removed. Native settings gestures retain their releases and backend input, and foreground controls clip around both Settings and the kitten popup. Ignite/Slice sit below-left of Swing, Force/Angry Force below-right of Move, and the somersault diamond remains below Swing. The white swing indicator and drag normalization share the full circle radius minus the marker radius, replacing the old 62% drawing limit.

Release builds with zero warnings/errors. The full managed regression suite and source/staged asset validation pass, including new production activation checks and circle-edge/layout checks across compact, scaled and offset viewports. Actual KSA frame/worker hook and fragment XML checks pass. All 26 ZIP files match staging and the SHA-256 verifies. Logs: `artifacts/checks/overlay-layout-build.log`, `overlay-layout-checks.log` and `overlay-layout-host-checks.log` in the same directory.

Native acceptance remains pending: launch with existing preferences and confirm no UI or F7 actions until enabling through the menu. Exercise every overlay control, the shared Settings window and per-kitten popups. Move Settings across the circles and test sliders, scrolling, dragging out of the window and native camera gestures. Drag Swing to every edge, release and re-grab. Disable during an active saber, Force capture, picker, cut and somersault; verify resources stop and physics resumes. Test rapid off/on, pause/resume, HUD/focus changes, boarding, loading and viewport resizing.

## Local ground clutter Force support (2026-10-23)

Force can select resident physical clutter with a native collider raycast, including static instances and previously displaced or sleeping rocks. Static capture invokes KSA's own displacement routine and queues its normal rendering exclusion/placement update. The game retains ownership of the collider, shape and displaced object. Held clutter becomes kinematic, with zero solver velocity and scoped contact suppression; release restores its original inertia and inherited motion, and Throw adds the existing outward 25 m/s.

Angry Force starts from either a vessel or a rock and gathers both kinds within 2 m of the primary surface hit. Vessels use mesh-surface distance; clutter uses a native sphere/collider overlap. The shared 32-object cap, deduplication and query budgets are checked before capture. The mixed group preserves relative poses and uses the existing movement control and independent Force hand pose.

GUI code snapshots a fresh camera ray into the parent frame. Both hover and click queries resolve only after physics workers join. Native identities include the original clutter key, bubble generation and simulation/shape/handle checks. Losing the owner's local bubble, changing owner/world, teardown or a partial capture releases reachable bodies and removes contact suppression. This implementation adds no orbital conversion, long-distance streaming or persistence. Terrain and decorative clutter without physical colliders remain ineligible.

The Release build passes with zero warnings/errors, and the full regression suite passes. The production mixed-controller fixture passes 78 assertions covering either primary type, the 2 m boundary, stable identity deduplication, the combined 32-object limit, shared transforms, release/throw, partial failures and local-bubble cleanup. Actual Bepu checks cover sphere/box/convex/compound/rotated collider overlap, stationary kinematic integration and full inertia restoration. Actual KSA host checks invoke the production private-field/displacement bridges, install the contact prefix on the native callback and verify native-clear invalidation. The final staged package passes validation; all 26 ZIP entries match staging and its SHA-256 verifies. Logs: `artifacts/clutter-build.log`, `clutter-checks.log`, `clutter-stage-checks.log`, `clutter-package-checks.log`, and `artifacts/checks/ForceControllerChecks/latest.log` / `ForceHostChecks/latest.log`.

Native acceptance remains pending: capture an untouched rock and confirm its original instanced copy disappears, move/turn/drag/re-grab, release, throw and recapture it after it sleeps. Start Angry Force from both a rock and a vessel with mixed objects just inside and outside 2 m; verify the shared limit refuses an oversized group without partial capture. Check a lit saber, pause/resume, focus/HUD changes, boarding, loading and a local physics-bubble change. Confirm collision/gravity resume, held objects do not push nearby vehicles, no duplicate visuals remain, and frame times stay acceptable with 32 mixed objects. Automated managed checks cannot establish these native visual and gameplay outcomes.

## Force capture, movement and throwing (2026-10-23)

The left overlay adds Force and Angry Force pickers, changing to Release and Throw while objects are held. Angry Force includes distinct vehicles with a mesh surface within 2 m of the selected hit. The group preserves each vehicle's relative position and orientation. A camera-plane drag plus wheel depth changes a persistent kitten-relative offset; releasing and re-grabbing does not spring back. The independent left-paw pose composes with the saber's right paw and the final somersault root rotation.

All scene writes occur in the existing settled handoff. Capture detaches native vehicles from their bubbles and an immutable identity snapshot filters their orphan-worker adoption. Held objects therefore receive no ordinary physics integration. Release writes their current rigid pose and inherited velocity, including angular transport; Throw adds 25 m/s outward. State uses the applied simulation epoch and the parent's inertial frame, with orbit readers joined before replacing flight plans. The native analytic overwrite is used without a full trajectory forecast on every move. Owner/world/parent/topology changes clear the hold, and capture/release failures cannot retain solver exclusion.

Work is bounded: 32 objects per group, 1,000 m target range, 25 m total offset, 8 m per drag, and 20 Hz hover queries. Angry proximity uses a bounded exact surface search; exceeding its budget refuses the capture. This initial implementation covered vessels and native debris; the follow-up above adds local physical ground clutter through its separate native storage.

The Release build and full existing regression suite pass, including the new persistent drag/layout, rigid-body math and independent Force pose checks. Focused production-controller fixtures cover capture rollback, reader-join failures, solver exclusion, inherited spin/velocity, release/throw, topology changes and stale ownership. The production UI partial is checked for deferred clicks, native UI priority, pending movement before throw, stale selections and cleanup. Actual KSA IL checks pass 120 assertions for Force/somersault patch composition in both orders; existing native frame-hook, XML and fragment checks also pass. Logs: `artifacts/force-build.log`, `force-checks.log`, `force-controller-checks.log`, `force-template-checks.log`, `force-host-checks.log`, and `artifacts/checks/force-ui-validation.log` / `force-host.log`.

Native acceptance remains pending. On the ground and in orbit, capture a single vessel, then a group straddling the 2 m threshold; move, turn and translate the kitten, drag/re-grab, change depth, release and throw. Confirm inherited motion without a position jump, collision/gravity resuming, and no change to unrelated vehicles. Repeat with a lit saber, a somersault, pause/resume, rotating parents and multiple viewports. Exercise Escape/right-click, covered ImGui windows, rapid click/release, focus loss, HUD hiding, control changes, boarding, target destruction/slicing and loading. Verify no input reaches camera or covered controls, no object stays frozen after cleanup, and acceptable frame times with 32 held objects. Managed checks do not establish these native outcomes.

## Light/Dark saber profiles and overlay controls (2026-10-23)

The user confirmed the initial shimmer/arc implementation works in game. This follow-up adds per-kitten Light/Dark selection and replaces the overlay's color-only picker with a saber popup. Color and type belong to the selected kitten for the session. Shimmer amount/speed and arc count/frequency/amplitude/brightness/width belong to independent shared Light and Dark profiles; the popup identifies the affected type. Save preferences is available in the popup, and resetting effects changes only that type. The main F7 window retains the remaining global controls.

Light and Dark use separately registered shader entry points and cached pipelines with shared source includes and a 128-byte interface. Light paths/shimmer are smooth; Dark paths/shimmer are jagged and irregular. Shared profile clocks preserve phase when fractional speed/frequency controls change. Count is capped at eight arcs/176 vertices per saber, still in one additional instanced draw; zero count/brightness skips that draw. Extended amplitude/width is included in culling bounds. The two profiles persist in settings JSON, with independent defaults for older or partial files.

Release build passes with zero warnings/errors. The targeted core/settings, per-kitten runtime/pose and VFX harnesses pass, including profile isolation, partial/null/legacy JSON, effects-only reset, per-kitten color/type isolation, fractional phase continuity and allocation bounds. Both source and staged shader pairs compile/link with matching 128-byte layouts; independent shaderc compilation passes. Source/stage validation and all 26 packaged files/checksum pass. Logs: `artifacts/checks/saber-profiles-release.log`, `profile-core.log`, `profile-kittens.log`, `profile-vfx.log`, `profile-shaders.log`, `profile-assets.log`, `saber-profiles-stage.log` and `saber-profiles-package.log` in the same directory.

Native acceptance for the new popup and styles remains pending: open it for an uncontrolled kitten with F7 closed, change color and type without changing another kitten, then edit a shared profile while two same-type and one opposite-type sabers are visible. Verify only the matching type changes. Save/restart and check both profiles; per-kitten color/type choices remain session-only. Exercise small-window scrolling, pointer capture during sliders/color picking, outside-click dismissal, control changes/boarding/HUD hiding, end-on blades and secondary viewports. Compare Light/Dark appearance and frame times at default and maximum settings.

## Blade shimmer and electrical ribbons (2026-10-23)

The existing HDR glow pass now includes subtle travelling blade shimmer and brief, blade-colored electrical ribbons. Arc paths use each kitten's stable seed and local emitter frame; they follow the current deployment and paw pose independently of camera direction. Geometry and draw submissions are bounded per saber, with screen-size and distance reductions. No particle emitter, additional scene light, collision or cutting behavior is introduced. The cosmetic clock continues during pause, matching existing spark cooling.

At most three arcs use six to ten segments each (66 vertices maximum), submitted in one extra instanced draw per eligible saber per viewport. Bursts last 0.24–0.38 seconds within staggered two-second windows; inactive instances skip path generation. The shared push-constant block is 128 bytes. Managed VFX checks cover that production layout, finite detail bounds, short/end-on blades, independent viewport detail, deterministic seeds, long-running/wrapping clocks and allocation-free policy calculations. Actual GLSL compilation/linking, reflected shader layout and SPIR-V validation pass. These checks do not execute the shader on a GPU.

Release build passes with zero warnings/errors. Full source/staged validation and all ten isolated managed harnesses pass, including the new VFX checks. The ZIP's 21 files match staging and its SHA-256 checksum verifies. Logs: `artifacts/checks/saber-vfx-release-build.log`, `artifacts/checks/saber-vfx-validation.log`, `artifacts/checks/saber-vfx-spirv.log` and `artifacts/checks/saber-vfx-package.log`.

Native acceptance remains pending. Inspect short/default/12 m blades through ignition and retraction; look along the blade and orbit around it; swing, drag the paw and somersault. Check multiple differently colored kittens in both main and secondary viewports, changing view order and control without changing the electrical pattern. Inspect against space, atmosphere and clouds and behind an intervening vessel, with MSAA on/off and after resizing. Check distant arcs fade without obvious popping, the core and tip stay readable, contact sparks retain their appearance, and shutdown/loading/boarding leave no effects behind. Compare CPU/GPU frame times at the same resolution and camera with one and many sabers, including close-up overlapping blades; bounded draw counts are not measured FPS acceptance.

## Sequenced saber toggles and ignite control (2026-10-22)

EVA checkboxes now raise the equipped hilt before ignition. Disabling keeps the blade, hand pose and audio alive while retracting, then lowers the arm before removing the hilt and restoring the MMU cosmetics. Switching direction preserves the current blade length and arm blend. Pause holds every stage; control changes preserve each kitten's pending sequence. Boarding/world changes retain immediate resource cleanup.

A blue IGNITE/RETRACT circle above-left of SWING uses the same queued request for the currently controlled EVA kitten. Its radius matches SLICE and its fill alpha is 64/255 (25%). Label and checkbox reflect the latest queued intent. Circular hit testing consumes paired presses/releases without a surrounding square, and the new control stays separate from the list, swing, slice and somersault controls across the tested viewport sizes.

Release build passes with zero warnings/errors against the shared checkout, including the concurrent somersault additions. Production per-kitten runtime/pose checks cover stage order, sound-path retention, pause, interrupted equip, reversed retraction, control switching, button targeting, queued double-clicks and cleanup. Overlay checks cover circle geometry and pointer capture from 240×220 through 3840×2160. Logs: `artifacts/checkbox-build.log`, `artifacts/checkbox-kitten-checks.log`, `artifacts/checkbox-overlay-checks.log`.

Native retest: enable/disable with each checkbox and the new circle; confirm full raise/ignite and retract/lower/stow with spatial sound. Reverse during every stage, pause/resume, switch control between two EVA kittens, board during a transition, and exercise the controls beside the new jump animations. In-game appearance and audio acceptance remain unverified.

## Material grouping and staged native isolation (2026-10-22)

New fragment assets merge render surfaces by durable material ID and glass flag into one visual SubPart, while preserving separate cap surfaces and full picking geometry. Original closed-solid grouping must survive export/reload for repeat cuts: flattening adjacent closed cubes into one authored group was shown to reinterpret volume 2 as 0.048 of backed panels. The merged format therefore preserves topology provenance independently of render-module membership. Existing generated assets remain valid and are not rewritten.

Native isolation snapshots the original external boundaries, splits the parent first, then resolves each remaining boundary's current owner. Bulk-change scopes close on every touched owning tree before the hook returns. Isolation happens only after the full swing is presented and all fragment assets/instances are ready. The final fragment creation/source removal swap occurs on a later safe update, with live motion and revalidated source geometry. Cancelling between those steps retains the intact source and separated neighboring vessels; it does not rejoin native connections.

Release builds with zero warnings/errors. Full managed checks, actual KSA XML decoding, host metadata/IL inspection, staged assets and archive integrity/checksum checks pass. New fixtures cover material/glass aliases, cap identity, exact exported attributes, touching-solid provenance, missing/malformed topology rejection, incremental restoration, parent-first owner changes, docking reroots, bulk-scope failure cleanup, stale/disposed sources, and cancellation between isolation and swap. Logs: `artifacts/slice-performance-build.log`, `artifacts/slice-performance-checks.log`, `artifacts/slice-performance-template-checks.log`, and `artifacts/slice-performance-host-checks.log`.

All six stock fixtures pass the worker/export/collider pipeline. The tank exports 2 render surfaces per fragment instead of 58, and passes persisted XML/GLB cuts through halves, quarters and eighths plus eight fresh cut seeds. The medium capsule drops from 165/145 surfaces to 3/4. Service-module variants A–D drop from 286–532 surfaces to 2 per fragment; height B specifically drops from 437/532 to 2/2. The earlier Height B run refused both repeat cuts because of the mismatched compound weld tolerance; the 2026-10-23 precision revision supersedes that result with two successful exported/reloaded seed-731 recuts. These are asset/module count reductions, not measured FPS gains. Stock logs are under `artifacts/merged-stock-*.log`; the old-exporter comparisons are `artifacts/baseline-stock-service-b-recut.log` and `artifacts/baseline-stock-service-b-audits.log`.

### User-provided baseline

The user supplied these timings from the preceding build, whose commit still includes isolation. No source-part identities or full-frame measurements were supplied. Values are milliseconds; rows retain the supplied order.

| Cut | Background | Largest preparation update | Commit | Validation | Isolation | Drain | Registration | Motion | Source removal |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 4066.4 | 7.34 | 36.25 | 11.23 | 18.44 | 0.13 | 0.36 | 1.69 | 4.13 |
| 2 | 400.2 | 0.60 | 4.50 | 0.10 | 3.78 | 0.37 | 0.13 | 0.06 | 0.07 |
| 3 | 516.4 | 0.37 | 0.54 | 0.11 | 0.00 | 0.00 | 0.31 | 0.03 | 0.06 |
| 4 | 766.2 | 0.40 | 0.38 | 0.06 | 0.01 | 0.00 | 0.20 | 0.03 | 0.06 |
| 5 | 3699.9 | 5.88 | 3.03 | 0.11 | 2.62 | 0.00 | 0.22 | 0.02 | 0.04 |
| 6 | 2048.9 | 0.92 | 0.83 | 0.10 | 0.50 | 0.00 | 0.15 | 0.02 | 0.07 |

Validation plus isolation account for 29.67 ms of the first 36.25 ms commit. The two near-zero-isolation commits take 0.38–0.54 ms. These observations support moving isolation earlier, but do not establish the cause of the first validation spike or measure engine follow-up, GC pauses, next-frame solver waits or FPS. The new diagnostics separate the earlier isolation pass from final swap time; their sum must be considered when comparing total main-thread work.

Native retest: compare cold and repeated cuts on the same leaf, root and mid-stack parts; record both isolation and swap timing plus full-frame timing. Exercise docking-root restoration, occupied neighboring branches, pause/resume, 1×/5×, cancellation between isolation and swap, stowing/switching kittens, source destruction and loading. Confirm source/fragment colliders never coexist across a physics step, separated branches preserve their crew/resources, and fragments use the source's current motion. Inspect glass/cooling caps, cut merged fragments again, and save/reload both new and old generated assets. Native frame-time improvements remain unmeasured for this revision.

## Varied repeat cuts, palette and interaction defaults (2026-10-22)

Targeted strokes run at half speed (1.24 seconds); ordinary Swing remains 0.62 seconds. Reach is twice the current deployed blade length from the live hilt to the hovered ray hit. Crew aboard no longer blocks targeting or replacement. Native destruction handles occupants of the isolated source, while separated branches retain their crew and roster assignments. Generated picking meshes now live on a native visual subpart; a constructor hook repairs old saved fragment instances before tree ownership.

Cuts first try coarse jagged facets with seeded angles and offsets, then validated planar alternatives. Untouched triangles retain their tessellation, avoiding threefold growth per repeat cut. All six stock fixtures pass the worker/export/collider pipeline: Gemini7 tank, medium capsule and four service-module heights. The tank passes exported XML/GLB reload through halves, quarters and eighths, with six faceted cuts and one plane fallback. Eight independent tank seeds pass (seven faceted). Capsule and service fixtures use planar fallbacks. Managed geometry checks cover true noncoplanar caps, shared seams, normals, UVs, holes, volume, inertia, reproducibility, cancellation and malformed-input rejection.

Each EVA row has an independent color swatch/picker without numeric RGB fields. New kittens cycle the exact requested RGB8 red, green, yellow and purple palette. Manual colors survive control changes; world reset restarts palette assignment. Picker clicks cannot toggle equipment or click through into the scene. It closes on hide, focus loss, control change, boarding or reset. Defaults are 1.4 m, grip (-24.5,-14.7,16), aim (.22,.45,.6) and curl .12 rad; saved explicit preferences are retained. Swing/slice circles are larger/smaller and farther left; the slice control toggles to CANCEL while selecting.

Release builds with zero warnings/errors. Full managed checks, actual KSA XML decoding, host metadata/IL compatibility and staged asset validation pass. Logs: `artifacts/slice-v2-build.log`, `artifacts/slice-v2-checks.log`, `artifacts/slice-v2-template-checks.log`, `artifacts/slice-v2-host-checks.log`, and `artifacts/stock-faceted-final.log`. Third-generation stock tank CPU/export/reload checks took 0.54–0.68 seconds locally; native game-thread timings and FPS remain unmeasured.

Native retest: verify the new defaults with fresh preferences; check palette, independent lighting and popup capture on controlled/uncontrolled kittens; hide during a picker drag, release and reopen. Toggle SLICE/CANCEL twice. Target a large part by its near edge at the 2× boundary, cut an occupied source and a neighboring occupied branch, and confirm crew assignment behavior. Slice new and old saved fragments through eighths and save/reload. Confirm the full slower animation precedes breakup and that complex-part fallback preparation remains responsive.

## Saber overlay and per-kitten equipment (2026-10-22)

The settings panel now has compact action rows and aligned preference controls, with contextual status instead of instructional paragraphs. Show/Hide saber overlay toggles the three chromeless, half-transparent surfaces together. EVA checkboxes equip and ignite independent kitten sabers; switching control preserves them. The fixed swing pad drives a temporary bounded arm pose and eases back on release. The red slice circle arms the existing picker, whose enlarged marker stays red for a rejected candidate and green for an eligible point. See [overlay implementation](research-saber-overlay.md).

Native retest: toggle repeatedly and close the settings panel; the overlay should remain usable independently. Enable two EVA kittens, switch control, retract one and board the other. Confirm separate poses, blades, illumination, sound and cleanup. Drag every direction and beyond the circle, release outside, re-grab during return, pause/resume, hide the HUD, resize and change UI scale. No gesture should move the widget, steer the camera, alter saved alignment or slice a part under the overlay. A camera drag that starts outside must still release normally. Click the red circle and inspect the enlarged green/red picker on reachable, distant and occupied targets. Check list scrolling with many kittens and confirm normal Swing still works.

Validation passes: Release build with zero warnings/errors; 44 overlay gesture/layout/input checks; 15 per-kitten lifetime/pose checks; existing geometry, blade, contact and targeting checks, including rejected-hover feedback. Host metadata/IL checks cover both game and ImGui-backend input seams. The staged folder, release DLL, ZIP contents and checksum agree. Logs are under `artifacts/overlay-build.log`, `artifacts/overlay-checks.log`, `artifacts/overlay-host-checks.log` and `artifacts/fragment-ownership-checks.log`. Build and managed checks cannot establish native visual/input acceptance or in-game frame times.

## Reduced breakup-frame work (2026-10-22)

Following a report of a remaining slight stutter, native fragment preparation now retains the real Part and detached PartTree for the final swap. It eliminates the throwaway preflight/descriptor copies, repeated mass/configuration repair and duplicate fragment orbit refresh. Indivisible native preparation units yield between frames; cancellation skips unstarted work. The final isolation, vehicle adoption and source removal remain on the joined game thread. Diagnostics now separate these costs and identify the slowest preparation unit.

The detached-fragment fixture checks single construction, ownership transfer without cloning, cancellation, exactly-once disposal and preparation failures against a small host model. Host metadata checks validate the actual native construction/adoption signatures. In-game retest must compare first and repeated tank/service-module cuts using both the preparation-unit and commit-phase timings; no measured FPS improvement is claimed from these fixtures.

## Cursor-selected slicing, normal swings and cosmetic sparks (2026-10-22)

The user observed a tank physically split with the previous implementation, but reported failures on service modules/capsules and severe slowdown while touching parts. They requested removal of contact-progress tracking and all autopilot functionality, retaining a first-class slicing swing and visual contact sparks.

The first asynchronous revision kept rendering responsive but still delayed the animation until plane preparation and then cut it short on a missing-collider error. The user's 4K/60 FPS video, `media/2026-10-22 20-23-33.mp4` in the surrounding workspace, shows this failure on a service module. Its diagnostics report 1551 ms background preparation, 1.30 ms largest preparation update and no native commit. These are observations of the previous revision, not acceptance of the fix.

**Swing / Alt+F7** is restored. **Select part to slice / Ctrl+Alt+F7** now arms point-and-click part selection. An in-range click starts the original 0.62-second swing without waiting for geometry; even preparation errors let it finish. Ready results commit on the next settled update after final-pose presentation. Late preparation leaves normal flight and ordinary swings available. Reach is checked at selection, and the flight-computer target is independent. Background CPU work, incremental capture/registration, nonblocking uploads and cosmetic 20 Hz contact sparks remain.

The missing-collider failure came from using a plain XML serializer: KSA's unannotated component list requires element overrides. The private worker serializer now retains the exported collider, picking and render modules. Validation rejects missing components before registration, and native preflight failures after successful registration no longer quarantine a reusable template.

Slicing reach now follows the actual visible blade: hilt-to-selected-surface distance must not exceed deployed blade length. Both native hover and fallback ray selection enforce it, with another check at stroke start. The previous 3 m minimum, extra metre and automatic extension are removed. The regular swing audio fix also keeps the cutting sizzle exclusive to slicing strokes.

### Automated evidence

- Release build against KSA 2026.10.19.5638 succeeds without warnings/errors.
- Managed checks exercise blade/settings behavior, 0.62-second swing timing at 30/60/144 FPS, immediate motion with a late worker result, cursor selection/occlusion, empty-space refusal, camera-distance-independent reach, movement after selection, incremental fallback and commit guards, worker publication/nonblocking cancellation/failure handoff, final-pose presentation with early/late results, exactly-once completion, geometry clipping and physics math, float32 repair, and GLB serialization.
- Blade-reach fixtures cover exact tip distance, just-outside refusal with a 0.25 m blade, longer/partially extended/retracted lengths, fallback parity, movement before the stroke, and rotated hilt offsets where the kitten centre is outside blade reach. These and the contact fixtures pass in `artifacts/blade-reach-checks.log`; build results are in `artifacts/blade-reach-build.log`.
- A separate check using actual KSA 2026.10.19.5638 managed types reproduces the plain-serializer component loss and verifies that the corrected loader preserves normal/glass render modules, picking, collision mesh references, subparts and mass. It does not initialize native templates or start a physics/graphics world. On ARM the check uses an ignored copy with only the unsigned IL-only assembly's COFF machine fields adapted; the original DLL and distribution are unchanged.
- Contact checks exercise two-sided intersection, blade-tip bounds, rate limiting, continuation past the triangle budget into later primitives, current-transform validation, disposed targets and visual effects on crewed vessels. A warmed 624-part stand-in scene over 1,800 frames allocates **zero managed bytes** for sustained contact. This is a managed probe measurement, not a native game FPS result.
- Locally installed stock assets pass automatic selection, closed-surface/volume checks, principal-frame float32 conversion and actual GLB export: `CoreFuelTankA_Prefab_LF3W3HA`, `CoreCommandA_Prefab_MediumCapsuleVariantA`, and `CoreServiceModuleA_Prefab_SetAHeightA/B/C/D`. Tank axial/oblique and capsule axial cuts also pass. Proprietary source assets are not committed or packaged.
- Service modules required repairing float-collapsed edge T-junctions, increasing compound/export group limits, and increasing the bounded convex-hull input-point budget. The native GLB writer is exercised by these fixtures so its group limit cannot silently disagree with preflight.
- All six stock fixtures also exercise the actual worker export, prepacked upload sizes, separate full-detail picking/compact collision meshes, and Bepu reconstruction of the saved collider. The local run produced 10–276 collision points per fragment, compared with roughly 7,000–50,000 render/picking points. CPU preparation was about 1.1–1.7 seconds off the game thread; these timings exclude game asset registration, graphics and world mutation. Full worker coverage exposed and fixed the old 100,000-triangle per-piece refusal on taller service modules; the limit now matches the exporter's 200,000-triangle bound.

Local ignored logs: `artifacts/point-slice-build.log`, `artifacts/point-slice-checks.log`, `artifacts/point-template-checks.log`, and `artifacts/point-slice-host-checks.log`. Stock geometry evidence from the unchanged geometry/export pipeline remains in `artifacts/async-stock-all.log`. Run the following checks from the repository:

```sh
dotnet build Purr2D2.slnx -c Release
python3 tools/validate.py --run-tests --stage artifacts/stage/purr2d2
python3 tools/check_fragment_templates.py
python3 tools/check_stock_slices.py /path/to/Content/Core
dotnet run --file tools/inspect_host.cs -- /path/to/KSA.dll bin/Release/net10.0/Purr2D2.dll
```

### Required native retest

These checks do not execute KSA's Vulkan/FMOD pipeline, native fragment spawning or save reconstruction. The previous user-observed tank cut does not establish acceptance of this replacement workflow.

1. Beside an uncrewed Gemini7, press **Select part to slice** from an unequipped state, point at its service module and click. Confirm a marker names the exact part, ignition and the immediate full 0.62-second swing, then separating textured pieces with sparks and cooling cut caps. Repeat on the tank, all service-module variants and an empty capsule. Check that preparation keeps rendering responsive and that a ready result has no extra delay after the stroke. Record the preparation/commit timings, especially for a cold first slice. Inspect glass, door details, normals and transformed parts. A preparation refusal must let the full swing finish and leave the source intact.
2. Set another vessel as the flight-computer target and confirm cursor slicing still selects the clicked part. Click empty space and an out-of-range part; neither may silently pick a nearby substitute. Test 0.25 m, 1 m and 12 m blades: the selected surface must be within that length of the hilt, and slicing must not extend it. Check while igniting, camera zoomed out and the kitten rotated. Move out of reach before the stroke starts; it must refuse. Move away or change the flight-computer target after a valid stroke; the pending cut must continue. Hide the kitten during the stroke (motion waits) and after it (ready breakup can proceed). Cancel during selection, reading, worker calculation, upload and the finished-stroke waiting period; also switch kittens and board. No cancelled request should mutate a part or block rendering while cleanup finishes. Retry after cleanup. Occupied vessels must refuse structural slicing.
3. Check the restored **Swing / Alt+F7** separately, including while a completed slicing stroke waits for its pieces. It should match the original speed and full recovery. For the historical cosmetic-only revision, hold and manually sweep a lit blade across a part. That revision produces sparks without destruction; the current intentional-contact revision below supersedes this behavior. Move clear; sparks must stop emitting. Verify contact effects on a crewed vessel. Check sustained contact frame time against the prior slowdown.
4. Check pause/resume, 1×/2×/5× and higher warp, HUD visibility, ignition/retraction, walking and MMU movement. Confirm no autopilot panel or flight-control override remains.
5. Inspect the blade and sparks against Earth, atmosphere/clouds, dark space and an intervening vessel; retain correct occlusion. Repeat after resizing and changing MSAA, and in a secondary viewport. Inspect hand alignment and sound.
6. Verify fuel is emptied only from the isolated source, surviving neighboring parts remain usable, and fragment mass/motion are plausible. Test another cut on generated wreckage. Geometry/arena/native isolation refusals must keep the original or report a retained isolated source.
7. Save, quit and reload with `Generated/Slices/` preserved. Check generated template lookup, geometry, collision and inertia; saved cut caps reload cold. Exit normally and inspect logs for graphics/audio/physics cleanup errors.

The enclosing convex hull collider may fill visible recesses or holes. Functional modules and attached IVA furnishings are removed when a part becomes wreckage. Complex geometry can add preparation latency after the swing, but CPU calculations and disk I/O no longer run in the frame callback. Native registration/preflight calls have a soft frame budget; the final native topology transaction remains atomic. Actual frame-time acceptance still requires the game.


## Quiet breakup revision (2026-10-23)

Implemented: boost-invariant inherited-spin motion; named 0.24 m/s relative opening with balanced momentum; selected-source draining and engine/actuator quiescence before the intervening solver; persistent firing-nozzle cache clearing; owned source/neighbor and replacement/sibling contact suppression until native overlap clearance; deterministic inert authored-subpart fallback through the ordinary durable asset/preflight transaction. Cancellation after isolation retains a separated empty source with engines off. The two-safe-update lifecycle remains required.

Managed validation covers zero/surface/orbital velocity boosts, unequal masses and shifted geometric COM, opening energy, source-only drain ordering, native collision-pair duplicates/copying/clearance, retained overlap after cancellation/disable, world/bubble/disposal cleanup, unload handoff to native retirement, stale-nozzle clearing, and deterministic bounded module-free fallback including the production geometry-worker path. Actual KSA IL/install/JIT checks verify all three module-update call sites; these checks do not run a physics world.

Release build passes with zero warnings/errors. Source/stage validation, existing managed suites, the new targeted motion/isolation/contact/quiescence/fallback suites, and actual KSA IL/install/JIT/XML checks pass. The initial full validation run exposed a test-stub warning in the new contact harness; its corrected harness was rebuilt and passes separately. Logs are under `artifacts/checks/quiet-breakup-*.log`.

Native acceptance remains pending: slice full/empty tanks, firing/idle engines, extended/retracted legs (free and ground-supported), occupied capsules and generated fragments at surface/orbital speeds and with off-center fuel/rotation. Verify no slice-caused blast, spin spike, contact failure or delayed failure cascade; preserve fueled neighbors. Exercise every cancellation/spawn failure phase, world/bubble changes, long-lived hull overlap, and save/load of both sliced and authored-fallback assets. Record pre/post-isolation/post-swap/next-solver motion, failure events and protection lifetime. Terrain remains collidable; this implementation does not claim blanket native explosion immunity or measured performance improvement.

## Repeat-slice precision revision (2026-10-23)

Rebuilt geometry checks pass local topology tolerance, exact common compound plane, rotated/scaled/translated multiscale solids, deterministic face-order welding, and copied-input T-junction repair at a large assembly offset. Negative generated-input cases reject missing surfaces, nonmanifold duplicate faces, duplicate shells and irrecoverable float collapse; failed generated geometry cannot receive authored panel backing or authored-breakup fallback.

The stock regression uses the locally preserved `artifacts/checks/stock-slices.json` extracted from stock assets. The configured 2026.10.19.5638 reference snapshot retains XML but does not include the atlas GLBs needed to regenerate that fixture. An older local Linux asset checkout identifies build 2026.8.19.5261 and was not substituted for the baseline. No proprietary assets are committed. All six stock fixtures pass. Both Height B exported/reloaded halves successfully recut at seed 731 with mass/volume conservation and no authored fallback; the prior exemption is removed. All seven tank repeat transactions reach halves, quarters and eighths with true facets, and all eight fresh tank seeds use true facets. Full stock results and Release/asset checks are recorded in `artifacts/checks/repeat-slicing-*.log`.

Native acceptance remains pending: perform recuts after actual game save/load, including transformed attached fragments, and inspect material/glass seams, cap appearance, positive mass/inertia and collision behavior. Finite float precision, mass/resource budgets, invalid contours, general mesh self-intersections and unsupported native transforms can still cause explicit refusal.

## Configurable assisted slice reach revision (2026-10-23)

Settings now exposes **Slice grace / reach**, from 1× to 4× visible blade length, defaulting to the previous 2× allowance. The row explains the distance rule and shows effective maximum selection distance in metres at current and full deployment. Changes apply before a stroke starts; **Save preferences** persists `SliceReachMultiplier`. Old files without this property retain exactly the previous 2× behavior. Finite extremes clamp to 1–4× and nonfinite multipliers recover 2×.

One shared helper calculates maximum hilt-to-hit distance for native hover, fallback broadphase/selection and stroke-start validation. Targeting takes that explicit maximum in metres without another multiplier. Accepted strokes use separate ownership/geometry checks and can finish after moving out of reach; range-bound APIs reject infinite distance. Visible blade length, physical contact and Force clutter tolerance retain their existing behavior.

Rebuilt targeted checks pass: **595 CoreChecks PASS checks** and **95 TargetSelectionChecks/contact PASS checks**, with no compiler warnings/errors. Coverage includes defaults and legacy settings, custom save/load, finite clamps and nonfinite recovery, exact/just-outside 1×/2×/4× boundaries at 12.5%/50%/full deployment, hover/fallback/incremental agreement, camera-independent surface distances, rotated/moving live hilts, changed range before acceptance, accepted completion after movement, disposed target refusal and infinite-range refusal. The generated CoreChecks project was regenerated from the current harness after its older source list omitted the previous revision's `ClosedMeshPreparation`; no production fix was needed for that stale project. Release build against KSA 2026.10.19.5638 passes with zero warnings/errors and rebuilds the installable folder/archive. Logs are under `artifacts/checks/slice-grace-core.log`, `artifacts/checks/slice-grace-targeting.log` and `artifacts/checks/slice-grace-build.log`.

Native acceptance remains pending: inspect the Settings row and wrapped metre readout at small window sizes/UI scales; compare red/green hover and clicked fallback selection at 1×/2×/4×; change reach while selection/preparation is pending; verify the live hilt is rechecked before motion and movement after acceptance does not cancel the cut. Save preferences, restart and confirm the chosen multiplier. Verify ordinary blade contact and Force near-miss selection are unchanged. The managed checks do not establish native visual/input acceptance.

## Bounded intentional SWING contact cutting (2026-10-24)

Implemented: a monotonic ID for each real SWING press/regrab; one accepted or refused rendered-surface contact per gesture; lit/full-deployment, changing-drag and physical-motion gates; incremental bounded candidate discovery and target-frame reads; frozen swept-ribbon queries with triangle cursors and explicit age/discontinuity invalidation; two-sided/coplanar/grazing/axial contact confirmation; world-scale motion/tolerance handling; structural/subpart freshness checks; and the shared quiet async cut transaction without the assisted animation. Ordinary release/recovery and intervening solver/render gaps preserve accepted preparation. Explicit hide/focus/pose/owner/world/stow/disable or Force conflicts cancel through the settled handoff. Resource maintenance is blocked while manual work/detection is active. Manual cuts use the automatic geometry chooser; an exactly swing-aligned seam is not implemented.

Managed fixtures compile the production sweep, candidate probe, gesture state, targeting policy and manual runtime partial. They cover thin surfaces missed by endpoint segments, coplanar/grazing/axial cases, rotating/nonuniformly scaled/translating targets, moving visual subparts, budget continuation with frozen endpoints, query expiry, discovery limits, no idle/ignition/return cuts, one transaction per press, no-step/no-fresh handoffs, release/recovery while waiting for the worker, isolated ownership rebasing and commit on a later valid update, cancellation and conflicts. Host boundaries remain stubbed; passing these does not establish gameplay or native performance.

Validation passes: the complete source/stage/shader and managed run reports **1,543 PASS lines** with no compiler warnings/errors or failed checks. After the final edge-case additions, projects regenerated from the current `tools/validate.py` source lists report **132 TargetSelectionChecks/contact PASS checks** and **27 ManualContactRuntimeChecks PASS checks**. The final Release build against KSA 2026.10.19.5638 has zero warnings/errors and rebuilds the installable folder and ZIP. Logs: `artifacts/checks/manual-final-managed.log`, `artifacts/checks/manual-final-TargetSelectionChecks.log`, `artifacts/checks/manual-final-ManualContactRuntimeChecks.log`, and `artifacts/checks/manual-final-build.log`. These are managed/build results; observed contact timings in stub scenes do not establish native frame time.

Native acceptance remains pending: sweep thin legs at 30/60/144 FPS and supported simulation speeds; test moving/rotating/scaled targets, blade base/middle/tip and maximum length, visible recess misses, occupied craft, no self/other-kitten cuts, and no automatic debris recursion. Verify idle/ignition/recovery touches stay cosmetic. Profile cold/warm discovery and query timing separately on dense craft/debris, record saturation/expiry/missed-contact rates, and compare the proposed 0.25 ms p95 warm target with the initial 0.5 ms soft update budget. Test long preparation after release, pause/resume, rendering gaps, SLICE/Force conflicts, every cancellation/failure phase, source movement/replacement/disposal and save/load recuts. Hard unit caps and the soft clock are not measured real-time guarantees.

## Double and triple somersault jumps (2026-10-24)

Implemented: up to three jumps per airborne combo, with each extra deliberate press within an inclusive 0.5 seconds of the previous accepted press. A monotonic unscaled clock captures input before observing newly applied solver results; the timestamp survives the settled queue and worker delay. Follow-ups keep the original direction and add the first launch's upward speed without replacing ascent/descent, horizontal velocity or reference-frame motion. Prepared-worker transactions consume each impulse once, preserve native liftoff/contact history and await applied jump acknowledgement. Extra smooth visual turns accumulate without snapping or discarding prior progress, and the pose adapter renders through three full rotations. Landing closes the combo; pause closes it and cancels an undispatched boost while retaining an already applied boost for acknowledgement after resume. Native collisions and tumbling remain authoritative.

Managed fixtures cover exact/just-outside/decimal-roundoff boundaries, the chained third window, rejected duplicates/fourth presses, additive ascent/descent boosts, distinct coordinate frames, current fractional rotation, complete double/triple turns, rendered second/third poses, short early-contact recovery without rewinding completed turns, paired release/press input, delayed workers, repeated settled ticks, pause before dispatch and before acknowledgement, contact between queue and preparation, failure atomicity and ownership cleanup. The integration fixture links the production input partial and motion controller: first/second native results are applied without a prior controller update, then real input requests refresh acknowledgement before eligibility checks. Low-frame-rate processing retains the original press timestamp. Draw-time availability has no state-changing refresh.

Validation passes on projects regenerated from the current `tools/validate.py` source lists: **628 CoreChecks**, **110 SomersaultWorkerChecks**, **52 SomersaultPoseChecks** and **534 ForcePoseChecks PASS lines**, plus the production somersault runtime arbitration suite. There are no C# compiler warnings/errors in these runs. The Release build against KSA 2026.10.19.5638 passes with zero warnings/errors and rebuilds the installable folder and ZIP. Source/stage validation passes; the ZIP has one `purr2d2/` root, only the mod DLL, byte-identical staged contents and a valid SHA256 checksum. Logs: `artifacts/checks/multi-final-affected.log`, individual `multi-final-<suite>.log` files, `multi-final-build.log` and `multi-final-package.log`. No unrelated full regression suite or native gameplay run was performed for this revision.

Native acceptance remains pending on Earth and Luna: all four initial directions from standing/running; deliberate second/third presses near each half-second boundary; late/fourth presses and held buttons; mixed direction buttons; ascent/descent boosts; low frame rates; early contact, slopes and tumbling landings; pause before/after boost dispatch and resume; manual saber drags, assisted slicing and Force composition; control/owner/world changes, RCS, unsupported warp, disable, boarding and unload. Observe every full turn, momentum addition and input release in the real game. Managed checks do not establish native animation appearance, landing behavior or frame time.

## Unscience co-load compatibility (2026-10-25)

The stale Force UI assertion described above is corrected without changing the runtime layout.
The focused `ForceUiChecks` project passes with the exact title/hint text, both labels horizontally
centered on opposite sides of the circle center, and no hover tooltip. Full validation is tracked
separately; this focused result does not replace the historical evidence.

The paired compatibility revision passes the complete `tools/validate.py --run-tests` check,
including source/staged shader compilation, after that test-only correction. Purr2D2 and Unscience
Release builds have zero warnings/errors; Unscience's 18 managed suites and the source-linked
frame/save interoperability harness pass. The harness reproduces the original conflicts and tests
both corrected installation orders, deferral/rejection/replay and removal. Its host/runtime are
fixtures; these results do not establish native physics, GPU lifetime or optimized-JIT acceptance.

Native acceptance remains pending. Record KSA, StarMap, Harmony and both mod revisions, manifest
order and observed diagnostics for each run:

- [ ] Start with both mod load orders. Confirm frame hooks install without patch failures and
  Purr2D2's settled update, Unscience's queued mutations and native physics each advance normally.
- [ ] Exercise Garry's Torch weld movement and Dent Wizard launches while Purr2D2 is enabled,
  including paused/resumed simulation, F2 and active orbit-hover work. Check following, motion and
  cleanup without duplicate updates, worker faults or hangs.
- [ ] Save and repeatedly reload generated Purr2D2 fragments alongside Unscience scene setups.
  Preserve `Generated/Slices/`; confirm native fragments and Unscience records restore, while
  Purr2D2's session-owned equipment/holds and queued actions clear. Repeat with a vanilla save and
  a new system; no old-world action should affect reconstructed objects.
- [ ] Submit a direct `Universe.DeserializeSave` request that Unscience preflight rejects. Confirm
  the native scene and Purr2D2 equipment, colors, capes and holds remain intact. Then perform a
  valid direct load; old ownership clears at replay before reconstruction and later updates work.
- [ ] Press and hold Space with an ordinary vessel, an unowned EVA and the active Purr2D2
  somersault owner. Native jump input must remain available to unowned targets; owned somersault
  requests must execute once. Repeat while typing in ImGui/console, changing control, disabling
  Purr2D2 and loading a scene; no consumed input or pending boost should leak to another vehicle.
- [ ] Switch a kitten with a queued or active somersault into Unscience Iron Man mode. Space must
  reach vessel controls and Purr2D2 must leave the non-character worker's motion/inputs untouched.
  Return to EVA mode and verify a fresh explicit somersault request works; other kittens retain
  their existing behavior.

## Per kitten save and restore (2026-10-25)

Individual color, Light/Dark type, cape preference and equipment intent now persist in native-save `purr2d2.json`, including seated and disabled-mode saves. Roster-aware profiles survive boarding and runtime cleanup; only successful scene replacement changes the logical scene. Missing/invalid whole sidecars produce defaults, while unavailable individual records remain preserved until explicit recovery. This supersedes the earlier session-only equipment/appearance policy, including the old co-load checklist's expectation that saved kitten choices clear. Force holds, gestures, queued actions and worker activity still clear.

The final Release build against KSA 2026.10.19.5638 has zero warnings/errors. Production storage/session/native-hook fixtures pass **49 scenarios / 223 assertions**, including both Harmony installation orders; production kitten/cape/pose/profile integration passes **29 runtime assertions**. These cover pending toggles, seated/disabled capture, replacement EVA bindings, identity and seat conflicts, explicit recovery, nested/skipped/failed native operations, quarantine rollback, unknown data retention, a 129-enabled-saber restore, and zero managed allocations with one fleet scan per warmed refresh. The new suites are integrated into `tools/validate.py --run-tests`.

Final regression execution passes **35/35 managed test executables** after rebuilding the affected persistence projects. Source/stage asset and metadata validation and shader checks pass. The ZIP contains 39 files identical to the staged payload, the staged DLL matches the final Release DLL, only the mod DLL is packaged, and the SHA256 agrees. Final per-suite output and exit codes are under `artifacts/checks/persistence-regression/`, including `summary.log`.

Native acceptance remains pending:

- [ ] Use KSA Save / Load and restart with two kittens sharing a character asset but different color/type/cape/equipment choices. Confirm settings return only to their owners after enabling the mod.
- [ ] Board, save while seated, reload, then disembark. Repeat while Purr2D2 is disabled, with hidden HUD, and across editor visits; confirm dormant preferences and lazy resource recreation.
- [ ] Repeatedly load A → B → A, including native-only saves and new systems. Check one pose/attachment/light/audio owner per kitten and stable native resource counts; no queued cut, Force hold, somersault or old contact velocity may resume.
- [ ] Exercise both mod installation orders and standalone Purr2D2, UI/console/automatic saves and deferred/direct loads. Both sidecars must match the same XML after overwrite. Include unreadable files, invalid sidecars, missing assets and sidecar publication failure.
- [ ] Save while equipping/retracting or with a checkbox request pending; final intent should return through the normal sequence. Test recovery of unavailable records and arbitrary rename/duplicate-seat conflicts without retargeting another kitten.
- [ ] Audit late-UI loads and GPU/FMOD/cloth lifetime in the native game. Passive observers preserve standalone native timing; managed Harmony fixtures do not certify optimized-JIT hooks or universal draw-list lifetime safety.

Focused evidence: `artifacts/checks/kitten-save-hooks.log`, `artifacts/checks/kitten-profile-runtime.log` and `artifacts/checks/kitten-persistence-build.log`. See [the implemented contract](persistence.md) and [source investigation](research-persistence.md).


## Saber clashes and interaction controls (2026-10-26)

The design and implementation contracts are recorded in [interaction-features-plan.md](interaction-features-plan.md). Implementation used GPT-6 Luna / max, with GPT-6.1 Sol / xhigh research and independent review plus orchestrator review.

Automated verification passed:

- The complete validator workflow passed **39/39 managed harness projects**, including the production clash coordinator, pose hold, assisted-slice gates, auto-swing easing, deployment/persistence, capture growth, and existing regressions. Generated projects built and ran the current sources with cached dependency restores (`dotnet run --no-restore`). Source and staged Light/Dark shader includes, compilation/linking, and the 128-byte push-constant ABI passed. See `artifacts/checks/interaction-full-validation.log`.
- Debug and Release solution builds both passed with **zero warnings and errors**. See `artifacts/checks/interaction-debug-build.log` and `interaction-release-build.log`.
- The Force host-contract/native-clutter checks and post-Release source/stage validation passed. The ZIP contains 39 files under one `purr2d2/` root; every file matches staging, the staged DLL matches the Release DLL, and its SHA-256 matches the generated checksum. See `artifacts/checks/interaction-force-host.log`, `interaction-package-validation.log`, and `interaction-package-integrity.log`.
- Warmed automatic-swing evaluation, sphere projection, and the production clash detector's distant-pair and stable-overlap fixtures report zero managed allocations. These isolated checks establish bounded behavior and allocation properties; native frame time and visual quality still need game testing.

The Release artifact is `artifacts/dist/purr2d2-0.2.0.zip`. No native in-game acceptance run was performed. The following checks remain necessary:

- [ ] Cross two blades from controlled and background kittens. Confirm a single spark/sound event, both visual poses held for 0.4 seconds, and both available angry faces for 0.5 seconds. Continue overlapping, separate, and clash again. Repeat during a somersault and with a Force hand active. Native locomotion must remain functional.
- [ ] Repeat after pause/resume, a world reload, and stowing one participant. Check that effects are visible/audible and no owner or audio channel remains stuck. Try partial and differently sized blades, and confirm no contact beyond either visible tip.
- [ ] Click and drag each kitten's deployment bar, including uncontrolled kittens: click position maps directly to extension, the blade moves quickly to the chosen fraction, release retains it, endpoints work, and ordinary toggles resume matching bar/blade animation. Hide the HUD, change focus, resize, open a modal, switch owner, and remove a kitten while dragging.
- [ ] Set independent lengths, change the global default, reset one kitten to default, then save/load, board/disembark, and reload an older save. Confirm overrides, inheritance, and enabled intent return to the correct kittens.
- [ ] Click AUTO SWING to toggle, drag its downward pill through 0.5–3 seconds per one-way stroke, and release to enable. Check a shallow upward arc, smooth endpoints, correct button color, partial extension, and two background kittens continuing independently. Clash during each direction, pause/resume, use manual SWING or SLICE, and retract; there must be no phase catch-up after a hold or unwanted cut from automatic motion.
- [ ] Hold Angry Furce to size a kitten-centered 2–50 m preview, moving up and down. Release into target picking with the chosen radius, then click a target and confirm the sphere disappears. Hold THROW to choose 10–250 m/s; each press starts at 10 m/s.
- [ ] Capture a mixed vessel/clutter group at the chosen initial radius. Move and spread it with Shift-wheel over the game viewport; no additional nearby objects should join after capture. Check the capture cap and staged breakup retain their existing behavior.
- [ ] Move the camera through/behind the sphere and near its edge; verify finite 2-pixel dots, eight distinct meridians and latitude rings, Light green/Dark red, and hidden-UI/modal behavior. Profile dense holds and many distant sabers in the native game.

## Dedicated clash recording (2026-10-26)

Clashes now use the supplied `saber_clash.ogg` as a non-looping spatial cue, with no ignition fallback. The source, repository, staged copy and ZIP entry have identical bytes (SHA-256 `dcf4da17373dee9117b47aa16bf718740e32acf16cc637227cb25e443873041f`). The recording is mono Vorbis at 44,100 Hz and lasts 1.190476 seconds.

The focused `AudioChecks` harness passes, including independent clash/ignition playback, pause, gain, cleanup and missing-asset behavior. `tools/generate_audio.py --check` passes and leaves the synthesized contact WAV unchanged. The Release solution build has zero warnings/errors; source/stage validation passes, all 40 ZIP files match staging, the staged DLL matches Release, and the archive checksum agrees. Evidence: `artifacts/checks/clash-audio-release-build.log`, `clash-audio-package-validation.log`, and `clash-audio-package-integrity.log`. The complete managed suite was not rerun for this audio replacement.

Native audition remains pending: check the new clash cue's balance and spatial position during repeated contacts, pause/resume and simultaneous ignition.

## Dedicated somersault swoosh (2026-10-26)

Somersaults now use the supplied `sommersault_swoosh.ogg` as a non-looping spatial cue for either saber type. The source, repository, staged copy and ZIP entry are byte-identical; provenance and SHA-256 are recorded in [research-audio.md](research-audio.md). The recording is mono Vorbis at 22,050 Hz and lasts 1.024580 seconds. The existing base gain is preserved at 0.525, independently of ignition tuning, and there is no ignition fallback.

Focused `AudioChecks` pass for both saber types, missing-cue behavior, independent ignition playback, spatial follow, pause, gain and cleanup. The audio generator check passes without changing the contact WAV. The Release solution build has zero warnings/errors; source/stage validation passes, all 41 ZIP files match staging, the staged DLL matches Release, and the archive checksum agrees. Evidence: `artifacts/checks/swoosh-audio-release-build.log`, `swoosh-audio-package-validation.log`, and `swoosh-audio-package-integrity.log`. The complete managed suite was not rerun for this audio replacement.

No audio placeholders remain. Native audition is pending: check the new swoosh during repeated somersault jumps, pause/resume, both saber types and simultaneous ignition or clash effects.

## Mouse swing swoosh (2026-10-26)

Mouse SWING uses the supplied `saber_swoosh.ogg`, copied unchanged as a non-looping spatial cue. The recording is mono Vorbis at 22,050 Hz and lasts 0.780771 seconds. Initial playback requires four pixels of mouse movement at normal overlay scale; repeats require distinct held movement and at least 1,250 ms of monotonic time. Release/repress preserves the cooldown. Stationary holds, automatic return and automatic or keyboard swings cannot trigger the cue. Blocked or stale input is discarded, including movement sampled during the final clash frame. See [the implementation plan](interaction-features-plan.md#7-mouse-swing-audio) and [audio provenance](research-audio.md).

Focused cached runs of `CoreChecks`, `ManualContactRuntimeChecks`, `TargetSelectionChecks` and `AudioChecks` pass. Coverage includes the exact cooldown boundary, no queued replay without new motion, threshold/duplicate/invalid input, stale poses, pause/warp/modal/owner changes, clash sampling and consumption, independent audio lifetimes and zero managed allocations in warmed gate evaluation. The audio generator check passes with the contact WAV unchanged. The complete managed suite was not rerun for this feature.

The Release solution build has zero warnings/errors. Source/stage validation passes; all 42 ZIP files match staging, the staged DLL matches Release, the checksum agrees, and the source/repository/stage/ZIP swoosh bytes match SHA-256 `be8c19b37ad7cab7bb090e83c5be83b02cd7b365e907752a7086ef6469e728cf`. Evidence: `artifacts/checks/manual-swing-audio-checks.log`, `manual-swing-release-build.log`, `manual-swing-package-validation.log` and `manual-swing-package-integrity.log`.

Native audition is pending: drag through a small movement and then continuously, hold still, release/repress rapidly, pause/resume and clash while dragging. Confirm the cue's volume, spatial position, 1.25-second minimum spacing and fresh-motion behavior in KSA.

## Absolute deployment slider (2026-10-26)

The per-kitten deployment bar now maps the pointer directly to 0–100% on press, drag and release. Manual requests set a target without snapping the visible blade. Supported player time moves it toward that target at one full range per 0.15 seconds, clamped exactly without overshoot. Release retains the target; repeated identical requests do not restart motion or repeat changed-intent writes. Zero retracts quickly while retaining the equipped hilt. Ordinary ignition/retraction timing, background ownership, pause and clash deferral retain their existing behavior.

Cached `CoreChecks` (including `OverlayDragChecks`), `KittenSaberChecks` and `KittenPersistenceRuntimeChecks` pass. Coverage includes absolute fractional clicks, re-grab/release, invalid and extreme coordinates, animated targets, retargeting/reversal, exact settling, pause, ordinary toggle takeover, independent background kittens, repeated-target deduplication, deferred clash animation and saved enabled intent. The complete managed suite was not rerun for this correction.

The Release solution build has zero warnings/errors. Source/stage validation passes; all 42 ZIP files match staging, packaged assets and README match source, the staged DLL matches Release, and the archive checksum agrees. Evidence: `artifacts/checks/absolute-deployment-release-build.log`, `absolute-deployment-package-validation.log` and `absolute-deployment-package-integrity.log`.

Native interaction acceptance remains pending: click near each end and the middle with lit and unlit sabers, drag quickly in both directions, release at a partial extension, and repeat with background kittens, pause and a clash. Confirm the chosen position feels direct and the blade catches up quickly before holding exactly there.

## Furce session workflow, hover and control fixes (2026-10-26)

This revision supersedes the earlier held-group growth interaction. Holding ANGRY FURCE opens a reversible 2–50 m radius pill, initially 2 m, with a kitten-centered sphere. Release retains that radius and enters target picking; the sphere follows the current target preview. A separate target click captures the group and hides the sphere. Held-group growth code and its controller partial are removed; initial proximity capture, limits, rollback and breakup remain. Holding THROW always opens a separate upward 10–250 m/s pill starting at 10 m/s, without requiring Shift.

Both the Auto Swing duration and Angry Furce radius use regular-size right-side pill values with shadows and viewport bounds. The sphere now consists of filled 2-pixel dots and only appears during radius sizing or Angry target selection. Visible copy uses Furce/FURCE. Both Dark ambient loops use gain 0.49 instead of 0.70, a further 30% reduction; Light ambience and action cues retain their previous gains.

Furce vessel hover presentation now refreshes from the native current-frame picker without increasing full-query frequency. Pending hits remain visually distinct; capture always uses its independent settled query. Clutter retains bounded settled lookup cadence and render metadata projection. A final source review caught and removed a native clutter validity check from drawing; a regression asserts render presentation never calls it. Warmed native and clutter preview/cache checks report zero managed allocations.

Shift-wheel over an owned capture in the main viewport now reaches compression/spread. The production native callback and ImGui suppression share an ownership predicate, so the event is applied once. Ordinary world scrolling still passes through, and ordinary wheel over THE FURCE retains depth movement. Fractional input and Shift-only horizontal fallback are covered, alongside focus/modal/owner/window/UI blockers.

Focused cached checks passed: `CoreChecks`, `ForceUiChecks` (radius, dots, sphere lifecycle, hover/cache and throw behavior), `FurceScrollRoutingChecks`, `ForceControllerChecks` (298 assertions), `ForceTargetingChecks` (13 groups), `ForceMathChecks`, `ForceThrowDragChecks`, `AudioChecks`, `ForcePoseChecks`, `SliceResourceUiChecks`, and the cached `FragmentTemplateChecks`/frame-hook harness. The complete managed suite was not rerun. Independent review used GPT-6.1 Sol / xhigh; implementation used GPT-6 Luna / max with orchestrator integration.

The final Release solution build passed with zero warnings/errors. Source/stage validation passes; all 42 ZIP files match staging, packaged assets and README match source, the staged DLL matches Release, and the archive checksum agrees. The removed growth source is absent from production and validator inputs. Evidence: `artifacts/checks/furce-flow-release-build.log`, `furce-flow-package-validation.log` and `furce-flow-package-integrity.log`.

Native gameplay acceptance remains pending: exercise the complete radius/aim/capture/throw sequence, verify dot size and right-side values at the current UI scale, test world Shift-wheel on a multi-object hold, and audition the quieter Dark loops. Vessel previews are updated each frame; physical clutter still uses bounded lookup cadence.

## KSA 2026.10.25.5707 upgrade (2026-10-29)

**Span.** OLD is the recorded baseline, KSA 2026.10.19.5638 (`ksa-game-assemblies` commit `edda844`, read from the `ksa-game-assemblies_prev` worktree). NEW is KSA 2026.10.25.5707 (`4b3e9e2281c6eec25c9ab77ddf2961ec53ec3dc8`, `../ksa-game-assemblies/current`). `version.json` covers revisions 5638→5707 contiguously, but its changelog lists only 5639–5703; revisions 5704–5707 have no entries and were covered only by the decomp/IL/Content review. Both snapshots hold the same 38 DLL filenames. Every Brutal and Planet module changed (changelog 5652, Brutal package update), as did KSA (MVID `a6318490-1e4f-4d4c-9f1a-b48f97f44be4`); BepuPhysics, BepuUtilities and CommunityToolkit.HighPerformance are identical. Assembly versions other than KSA remain 1.0.0.0, so no reference or package changes were needed. StarMap.API 0.3.6 and Harmony 2.4.2 are unchanged compile-only references; no loader change is required.

**Fixes for confirmed 5707 breaks.**

- *Legacy saved-fragment picking (regression).* In 5638, `Part(PartInstance, Part)` chained into the patched `Part(string, PartTemplate, PartInstance, Part)` constructor. In 5707, it and the new editor-clone `Part(Part, Part)` call a private initializer directly. Save loading (`PartTree.Deserialize` → `RegenerateFromPartInstance`) therefore skipped `RepairLegacyFragmentPicker`, and pre-v3 fragments with a root pick mesh lost native hover, click-to-slice and Furce ray picking. All three public entry constructors are now postfixed. A new real-IL check follows each public constructor's `: this(...)` chain and requires exactly one postfix per construction. Its negative control shows the 5638 hook set leaves save loading unpatched.
- *Picker module registration (latent regression, exposed by the fix above).* `Part.SubtreeModules` now aliases `Modules` for childless subparts, so the old three `Add` calls would have registered the picker twice. The repair now uses native `Module<T>.AddNewTo`. The contact stub models the 5707 alias, and the focused test asserts single registration. Against the old code, that test fails.
- *Render-order contract.* Changelog 5663 replaced `PlanetTransparenciesRenderer.RenderAtmosphereOnly` with `RenderAtmosphereAndClouds`, called at the same `RenderViewport` position: after the opaque pass, before sunbloom, the MSAA bridge, the saber translucency pass and `ResolveAttachments`. Secondary views now composite clouds before the blade, as the main view already did. `tools/inspect_host.cs` expects the new name. The unmodified inspector failed only this stage, and the updated one still rejects the 5638 host.
- *Mesh growth re-audit.* The five pinned modules all changed, so growth was correctly refused until it was audited. These were unchanged: `DeviceMeshInterleaved`, `Device`, `Queue`, `StagingPool`, `CommandBufferPool`, `RaytracingRenderer`, `RaytraceBlasSystem` and the top-level `RaytraceAllocator`. Every raster, thumbnail and raytracing draw still binds the current shared handles. RT shaders still read geometry through set 0 bindings 3/4 (no buffer references). Changelog 5670 (scratch, SBT and acceleration-structure address alignment) caches no shared-buffer address, and BLAS creation still waits on its staging submit. The new timeline `Submit` overload still goes through the locked queue wrapper. The `Core.Renderer` changes are limited to the HDR swapchain and UI image. All existing structural IL contracts pass against 5707, so the pins now name the 5707 modules. `DeviceEx` gained an `AllocationInfo.CreateInfo.AllocateFlags` channel; `DedicatedMeshBufferAllocator` now refuses it rather than dropping flags. The host check proves `RaytraceAllocator` still chains `DEVICE_ADDRESS` through `Next`, and it rejects the 5638 identities. A negative control without the allocator guard fails.

**Audited as compatible, no change required.**

- *Frame and workers.* `PrepareFrame` is unchanged, with identical IL offsets, and so are the worker preparation order, `KittenEva`, `InputEvents`, `JobSystems`, `VehicleUpdateTask`, `PhysicsBubble`, `ConstraintSim`, `NarrowPhaseCallbacks` and `BubbleClutterStatics`.
- *Saves and roster.* `GameSave`, `UncompressedSave` and `UniverseData` are unchanged.
- *Rendering and assets.*
  - The ImGui backend mouse and scroll hook bodies are unchanged.
  - The saber pipeline's offscreen color, depth and sample formats are unchanged, including with HDR output.
  - Point and forward lights are unchanged, including the stock 16-light arrays and the environment-light transpiler.
  - `ShaderReloader.GetModPath` is unchanged.
  - The source-generated GLTF JSON reader accepts every shipped GLB.
  - The cape hook's arguments are unchanged, and the cape GLB loads with zero inverse-bind error.
- *Slicing physics.*
  - The `Vehicle.Split`/adoption/worker bindings are unchanged.
  - The capsule's new 500 kg `RadiationShield` is part of both source and native mass.
  - `Tank.DepleteAll` also empties stored gas.
  - The cap `AddInstance` overload body is identical.
- *Radiation.* `Universe.ApplyVehicleSolvers` can now destroy a lethally dosed EVA kitten. This happens before the settled update, on the same ordering as existing in-solver destruction, so existing owner cleanup applies. Retired kittens cannot be on EVA, and the new roster dose/retired data only adds bytes to the hashed `universe.xml`.
- *Clutter.* Furce reflection targets are unchanged.

**Pre-existing defect found, not fixed here.** `PartTreeRenderData.Compose` reaches the private `PartModel.AddInstance(PerInstanceData, PerInstanceDent, IViewport, int)` overload only in the raytraced IVA branch; every other view writes `PartModel.ViewportData.InstanceList` directly. The same code is in 5638, so the cooling-cap emission prefix cannot tint caps in ordinary exterior views on either build. No native observation of glowing caps is recorded. A candidate fix is a prefix on public `PartModel.WriteInstancesToGpu(IViewport, int)` that rewrites generated `_cap` entries in that list, keeping the existing prefix for raytraced IVA. It needs native visual confirmation first.

**Automated results** on final sources (`b68124a` plus working tree), all logged under `artifacts/checks/ksa5707-final-*.log` with commands and target paths:

- *Builds.* Debug and Release solution builds have zero warnings and errors.
- *Validator.* `validate.py --run-tests` passes all 40 managed suites (2,380 PASS lines), source and staged Light/Dark shader compile/link, the 128-byte push-constant ABI, and source/stage metadata.
- *Host checks.*
  - `inspect_host.cs` passes 142 checks on the Release DLL.
  - `check_fragment_templates.py` passes, including the new Part-constructor coverage.
  - `check_force_host.py` passes 289 checks.
  - `check_mesh_growth.py` and `check_mesh_growth_host.py` pass.
  - `check_cape_cloth.py` passes: zero inverse-bind error; two capes take 0.414 ms median per update with 0 B allocated (standalone CPU).
  - `check_runtime_bootstrap.py` passes all 19 isolated cases, plus source parity with gatOS `399456f` and unscience `8c07ef0`.
- *JIT warmup.* `check_slice_warmup.py` prepares 1,300/1,300 bodies (98,061 IL bytes, 795 boundaries, failed 0). It needs MathNet.Numerics 5.0.0, which both snapshots omit; the copy used, NuGet `lib/net6.0` (SHA-256 `83b41a60…`), is identical to the installed game's. The same HEAD against 5638 selects 1,303 bodies. The delta comes entirely from changed host bodies (PartTree module save/derived paths, crew dose crediting) and is recorded in `ksa5707-slice-warmup-graph-diff.log`; roots and budgets are unchanged.
- *Stock slices.* `check_stock_slices.py` passes 36 checks on seven fixtures, including small-tank contact recovery after 24 and 26 attempts. This is historical-geometry coverage, not proof of 5707 meshes: the run uses 5707 `Content/Core` XML plus the 19 mesh atlases from the private 2026.10.19.5638 asset set (`flexo-private-assets` `ed6aa28`). Those atlases are byte-identical to the installed 2026.10.13.5602 and 2026.8.19.5261 sets, and the tank/capsule/service-module asset XML is byte-identical across that set, 5638 and 5707. The 5707 GLBs themselves are absent from the snapshot. The fixture stays under ignored `artifacts/`.
- *Package.* The archive integrity check is separate from stage validation. `purr2d2-0.2.0.zip` has 42 files under one `purr2d2/` root, every file is identical to stage, the staged DLL matches the Release DLL, Purr2D2.dll is the only binary, no host, test or stock files are present, and the checksum verifies (SHA-256 `da319724366e91cdbb6de992b0675c4281eba4e9a8940efae7cdaafe1d949550`).

**Native acceptance remains pending.** The local installations are 2026.10.13.5602 (macOS) and 2025.12.31.3103 (Wine), so no 5707 game was run and nothing was installed. Managed and host checks passed; that does not establish native support. In addition to the existing pending checklists, run these with disposable save copies, keeping `settings.json`, per-save `purr2d2.json` and every file under `Generated/Slices/`:

- [ ] Startup, standalone and with Unscience in both load orders: no patch failures, mesh growth status Ready, and extended environment lighting available.
- [ ] Load a save containing a pre-v3 fragment with a root pick mesh. Native hover, click-to-slice, Furce hover and Furce ray must select it, with exactly one picker module on its visual subpart. Reload v3/merged fragments and newly exported fragments.
- [ ] Force several mesh growths with small initial arenas, with ray tracing off and on (including raytraced IVA). Inspect stock and cut opaque/glass fragments, shadows, thumbnails, picking and secondary views. Resize or rebuild the renderer and toggle HDR after growth; use Vulkan synchronization validation if available.
- [ ] Toggle HDR off, then on live, at MSAA 1× and 4×. Check Light/Dark blades, arcs, slag and cut sparks, bloom/CMAA haloes, Furce sphere colors over the HDR scene, and shutdown with sabers lit.
- [ ] On the ground, push the blade into terrain and grass (now in the depth-normal pre-pass), with AO on and off. In a secondary viewport with clouds, check the blade, hilt, cape and point lights together.
- [ ] Watch cap cooling in an exterior view and in raytraced IVA, to confirm the pre-existing emission gap above.
- [ ] Slice the shielded `MediumCapsuleVariantA` with crew and a gas-holding tank. Mass checks must pass with no "Native fragment mass changed" refusal.
- [ ] Let an equipped, Furce-holding EVA kitten (with a background equipped kitten) die of radiation, including just after a somersault launch. Expect no fault, released holds, stopped audio and removed pose/cape owners. Save and reload with sick, dead and retired kittens, with both 5638- and 5707-written sidecars.
- [ ] Windows multi-monitor: overlay hit-testing and click/scroll suppression after a primary-monitor reconnect and a DPI change. Use MMU left/right thrust with a deployed saber, and audition every FMOD cue and its cleanup.

## Cut-cap cooling glow in every raster view (2026-10-29)

This supersedes the "pre-existing defect found, not fixed here" paragraph of the KSA 2026.10.25.5707 upgrade record above.

Generated cut caps use the `Purr2D2_CutSurface` material, whose emissive map is the stock uniform `EmptyWhite`. The cooling effect therefore depends on per-instance state bits from stock `MeshIndirect.frag`: bit 6 suppresses emission, and bit 7 replaces the sampled emission with the instance color. In 5638 and 5707, the only hook was a prefix on private `PartModel.AddInstance(PerInstanceData, PerInstanceDent, IViewport, int)`. Batched `PartTreeRenderData.Compose` reaches that overload only for raytraced IVA. Every other view writes `PartModel.ViewportData.InstanceList` directly, so exterior, map, secondary and non-raytraced IVA caps were submitted with neither bit set. Going by the shader code, they were probably shown at the full white emissive sample instead of glowing orange and cooling to dark. No native screenshot of that defect was recorded.

A second prefix on public `PartModel.WriteInstancesToGpu(IViewport viewport, int frameIndex)` now rewrites that view's generated cap entries in `InstanceList` immediately before upload. The existing `AddInstance` prefix remains for raytraced IVA, which bypasses the list. The pure state logic lives in `src/Core/SliceCapEmission.cs` and applies the same rule to both paths:
- A cap cut less than 18 seconds ago gets bit 7, with the unchanged cooling color: 0xFFA528 when fresh, fading through orange to a dim red.
- Older and saved caps get bit 6 and are drawn dark.
- Other native bits are preserved, and reapplying the rule is idempotent, so instances that pass through both hooks are unaffected.

The upload prefix runs for every part model in every view each frame. Ordinary models exit on an allocation-free mesh-id test, and each cap's heat-owner id is parsed once per model and cached (main render thread only).

Verification:
- `SliceCapEmissionChecks` (CoreChecks) covers heat-owner parsing for merged and older cap names, rejection of non-cap names, the fresh color, monotonic cooling, cold and invalid ages, the stock bit values, bit preservation and idempotence.
- `CapEmissionHostChecks` (`check_fragment_templates.py`) verifies on actual 5707 IL that `PartModelRenderer.UpdateRenderData` uploads every static part model through `WriteInstancesToGpu`, and that this method uploads the per-viewport `ViewportData.InstanceList` the prefix rewrites.
- `inspect_host.cs` checks both hook signatures and the Harmony argument names `instanceData` and `viewport`.

The complete matrix passes on these sources (`artifacts/checks/capglow-final-*.log`):
- Debug and Release builds have zero warnings and errors.
- The validator runs 40 suites with 2,388 PASS lines, plus shaders and ABI.
- Host checks: the inspector passes 146 checks, the fragment harness 13, Force host 289; mesh growth, cape cloth, bootstrap with peer parity, and warmup (1,300/1,300) all pass.
- Stock slices pass 36 checks.
- Archive integrity passes: 42 files, SHA-256 `02c9ae4153466e8d2ee3ddae68f6846357a4b99e4c9457af59c8fd6ae4d24e72`.

No GPU work runs in these checks.

Native visual acceptance remains pending:

- [ ] Slice a part in an exterior view, then watch the new cap faces glow orange, fade through red within about 18 seconds, and stay dark. Repeat in a secondary viewport, in map view, and in IVA with ray tracing off and on.
- [ ] Reload a save containing older fragments. Their caps must stay dark, with no white emissive faces. Recut a fragment and confirm only the new caps glow.
- [ ] Profile a scene with many fragments; the upload prefix must not add measurable frame time.
