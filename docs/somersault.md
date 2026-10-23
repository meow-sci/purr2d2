# Grounded somersault controls

The saber overlay adds forward, backward, left and right somersault buttons in a diamond below the swing control. Each circle shares the slice button's radius and has a blue fill at 25% opacity (8-bit alpha 64). The buttons appear only for the controlled EVA kitten in grounded locomotion with terrain contact, including standing and walking. They disappear in flight and reappear after landing. Checkbox rows continue to control saber equipment independently.

Each press requests one move. Holding a button, repeated presses while busy and dragging across a button cannot queue another jump. Hidden circles are click-through; an already captured release is still consumed. The layout reserves the same space in flight so the other controls do not move when the buttons disappear.

The move works with or without a saber. Direction is relative to the kitten's heading at takeoff; rotating the camera during flight does not redirect it. Forward/backward are somersaults and left/right are airborne sideways flips. The default profile targets a 1.3 m apex above takeoff and approximately 1.5 m of level-ground travel. Local gravity determines launch speed and animation duration, so lower-gravity moves take longer. Slopes, drag and collisions can change the actual height, distance and landing time.

Starts require normal 1× simulation speed, RCS disabled, upright terrain contact and an active native physics bubble. The controls show a short availability reason when visible but disabled. A current saber swing or unfinished slice stroke must finish first. Beginning a somersault exits uncommitted slice selection; background cutting after a fully presented stroke continues normally. Saber click-and-drag remains available throughout the flip, with the hand pose applied before the full-body rotation. New canned swing and slice strokes are blocked during the move.

## Physics ownership

`SomersaultMotionController` validates and patches the sole virtual `Vehicle.PrepareWorker(SimStep)` call in `Universe.PrepareVehicleWorkers`. Its wrapper calls the native preparation first, including derived EVA character/ladder snapshots, then modifies only the requested kitten's prepared worker state. It applies a bounded, surface-relative velocity delta once, takes the prepared body off rails, and initializes native airborne state without leaving a second stock jump impulse armed. Public vehicle state is updated through the game's normal solver results.

The queued request remains associated with its original kitten. Launch is acknowledged through applied locomotion state before the visual sequence advances. KSA then owns trajectory integration, gravity and collisions. There are no repeated position writes or intentional physical body rotations. The move consumes applied simulation timestamps once; it freezes while paused. Landing follows native contact/state, not the estimated flight timer. Early collision recovers toward upright; a longer fall completes one turn and holds the airborne pose rather than repeating it or snapping to the ground.

Movement and grab inputs are scoped to that kitten's prepared snapshot during the action. Held stock jump has a release latch to prevent a second automatic jump on landing. Control changes, boarding/disposal, world reload, editor entry and shutdown release owned state. Enabling RCS or unsupported time warp hands motion back to the game. Hiding the overlay or losing focus clears pointer state without cancelling an already launched flight.

The implementation refines the original research plan: its launch transaction modifies the final prepared worker snapshot, avoiding a split public-velocity mutation and a separate snapshot patch.

## Visual pose and optional tuck

`SomersaultPoseController` binds independently of saber equipment. It validates all skeleton roots and the model-to-body centre-of-mass conversion, then composes a procedural turn about that pivot. Rotation uses a continuous angle through a full turn; it does not interpolate between equivalent 0° and 360° quaternion endpoints.

Tuck is enabled only when recognized left/right hip-knee-ankle chains have valid hierarchy, distinct bones and plausible rest geometry. Supported rigs bend both legs and optionally the spine. Validated arm chains can fold into a carrying pose; the right arm remains under saber grip control when its saber is active. Unknown or incompatible leg rigs retain the full-body turn without tuck. No new model, texture or Blender asset is required for this implementation.

The ordered local pose is native animation → optional tuck → saber grip → full-body turn. Equip/unbind changes preserve that ordering. The game then reconstructs the final skeleton for each viewport, so the hand-mounted saber, helmet and other native attachments use the flipped pose. Main-view publication keeps the existing body-space freshness checks for hilt, contact, lights and audio. The visual turn never changes the physical collider or mass; the tuck does not let the kitten fit through smaller gaps.

## Validation and limits

Managed checks cover profile scaling with gravity, directional frame conversions, once-only request/worker handoff, native launch acknowledgement, held-jump behavior, pause and ownership changes, full turns and bone-length preservation, fallback rigs, explicit pose ordering, main/secondary view publication, responsive circular hit regions and input capture. Host inspection validates the actual KSA 2026.10.19.5638 metadata and worker-call ordering. These checks do not execute the game's native terrain solver or render a real kitten.

In-game acceptance remains necessary on Earth and Luna: all four moves from standing and walking; RCS rejection; held jump and repeated clicks; early contact, slopes and vessel impacts; pause/resume; saber equipped/stowed and checkbox transitions; follow camera, portraits and secondary viewports; and switching kittens, boarding, save/load or unloading during flight. Exact stock-rig tuck support and appearance must be observed in the game. Vessel-deck launches, swimming, ladder launches, free-flight flips and near-zero gravity are not supported.

Source investigation and alternative designs are recorded in [the research plan](research-somersault.md).
