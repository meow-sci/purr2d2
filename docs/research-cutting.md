# Cutting architecture

The current gameplay is an explicit **Slice nearby part** action. `SaberRuntime.AssistedSlice` selects a visible nearby part once, checks up to nine candidate sections, aims and extends the blade, animates a 0.72-second stroke, then consumes one commit cue. Target, ownership, reach, crew and transform checks run again before native replacement. A failed attempt reports its reason; it never retries automatically while touching the part.

`PartSliceTargeting` is used only by that action. Its immutable mesh BVH cache accelerates nearest-surface selection. Geometry preparation and export are covered by [slicing research](research-slicing.md). Vessel mutation runs at the settled `PrepareFrame` seam after previous physics jobs have joined and before new jobs are dispatched.

`SaberContactProbe` is a separate read-only visual path. It emits sparks against static, dynamic and glass render meshes, including crewed targets. At 20 Hz it checks a cached contact triangle first, then uses a bounded resumable search of nearby mesh references. Native CPU arrays are read directly, with reusable candidate storage. The probe has no reference to the slicer and no destruction callback.

Autopilot, ring coverage, contact heat, sweep history and automatic connection/failure cutting have been removed. There are no persistent contact scars; only short spark bursts and the actual sliced fragments' cooling cut caps remain.

Managed validation covers explicit selection and commit guards, exactly-once animation completion, contact sampling limits, multi-primitive search continuation, stale-contact rejection and allocation-free sustained contact. Native rendering, spawning and save/load still require in-game acceptance.
