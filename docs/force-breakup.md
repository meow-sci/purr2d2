# Force expansion breakup

Shift + wheel down over Move Force, while idle or dragging, requests breakup at the next joined physics handoff. The input direction triggers it even when spread is already at 400%, or a later wheel event compresses before the handoff. Depth scrolling, compression and smoothing alone never trigger breakup. Owner changes and movement reset discard pending requests.

The controller validates the hold, snapshots authored visual nodes at full-part and subpart boundaries, checks the complete 512-object limit and prepares all native roots before committing any source. Each visual node becomes one inert vehicle. Existing meshes/materials are reused; dynamic model surfaces become static wreckage and glass keeps its rendering pass. Missing geometry, unbakeable dents and deployed parachutes refuse preparation. Invisible containers contribute dry mass to the visible pieces. Mass is distributed by authored volume within each part, with a small positive floor for decorative pieces; invisible-part mass is redistributed across the debris.

Replacement templates contain rendering, picking, native mass/inertia and a centered box collider. They contain no engines, tanks, controls or seats. Their XML is append-only and shares the existing `Generated/Slices/` entry/disk limits and early startup reload. No new GPU mesh allocation is needed. Keep these files and any source generated slice assets with saves containing the debris.

Commit creates all replacements as native orphan vehicles with inherited rigid motion, depletes source consumables, stops its audio and calls native vehicle destruction. It never applies a part-failure event, explosion or random debris shedding. Crew follows native destruction, as with slicing. Sibling collision-avoidance pairs suppress initial overlap after release and retire when native physics observes clearance. Terrain and unrelated craft retain normal collision behavior.

The controller inverts the displayed group spread/translation to place the new pieces in the same hold frame. It preserves the original group center, untouched peers and pose target. Each committed source is replaced and its workers published immediately, so a later native failure cannot orphan earlier replacements. Release and throw use the existing controller paths. New Force debris is a single physical object and does not recursively break apart on further expansion.

## Verification

Automated checks cover input direction and deferred execution, maximum-spread requests, owner/reset cancellation, mixed debris/clutter holds, replacement without position jumps after compression/translation, subsequent spacing/movement/throw, whole-group admission failure, and recovery after a later commit fails. Host checks deserialize the generated XML with real KSA types and inspect the depletion/destruction and overlap-protection contracts.

In-game acceptance remains required:

- Capture a fueled multi-part vehicle; compress first, move it, then expand while hovering Move Force. Confirm every visible part/subpart remains held and spreads smoothly, with no ignition flash, explosion sound or missing geometry.
- Repeat while dragging, at the spread ceiling, with rotating vehicles, and with Angry Force holding additional vehicles and clutter. Confirm unchanged peers and paw aim do not jump.
- Release while siblings still overlap, release after they clear, throw, then recapture. Check sibling clearance, terrain contacts, debris motion and normal capture behavior.
- Save and reload the debris, including glass and scaled parts. Confirm durable templates resolve and old source vehicles/tank contents do not return.
- Confirm preparation refusals for resource limits, unavailable geometry, dented surfaces and deployed parachutes leave sources connected. Check occupied-vehicle destruction follows the same behavior as slicing.
