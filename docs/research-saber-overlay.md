# Saber overlay and multiple EVA kittens

The regular settings window retains Equip, Ignite, Swing, explicit slicing, flight-computer targeting and preferences. Appearance and character alignment use aligned forms; operational status and errors replace persistent instructional paragraphs. Show/Hide saber overlay is a session toggle independent of panel visibility and saber equipment.

The three overlay surfaces use the main ImGui viewport's foreground draw list, with alpha 128/255 fills and no windows, title bars or drag handles. A top-right EVA list shows independently checked kittens; the fixed black swing circle sits left of the smaller red slice circle at the right side's vertical centre. Layout follows the viewport and UI font scale. The list scrolls when necessary.

This approach follows the KSA Broadcast Telemetry Overlay's [foreground drawing and work-area anchors](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/OverlayRenderer.cs) and [panel layout](https://github.com/elevatorctln/KSA-Public-Telemetry/blob/90608fb465f87d6008208798cc4d20ed8182c9a0/Rendering/PanelHost.cs), as documented in the local KSA modding skill. Its drawing approach supplies no input handling, so the saber controls implement their own geometric hit tests rather than transparent square windows.

## Input and temporary pose

Narrow prefixes on Program.OnMouseButton, OnCursorPos and OnScroll consume game input owned by the overlay. Paired filters on the native ImGui backend consume those same owned button and wheel events, so covered stock windows cannot react too; other backend input remains untouched. Circular tests leave square corners click-through. A captured press keeps its release even outside the widget, while native camera and ImGui drags retain their own releases. A focus-loss hook clears capture even when the HUD is hidden. Overlay input also suppresses the slice picker for that frame, preventing the slice button itself from selecting a part underneath it.

The gesture snapshots grip and blade direction, maps pointer displacement into a bounded radial offset, and applies short response smoothing. Horizontal movement adjusts lateral grip and yaw; vertical movement adjusts grip height and pitch. The existing arm IK still limits physical reach. Release eases back in 0.24 seconds and restores the original unnormalized direction exactly. Temporary offsets never write settings. Pause freezes motion; hiding the overlay, changing owner or loading a world releases the temporary pose.

## Kitten ownership

Each EVA object has one blade state, pose processor and audio owner. The controlled kitten's fields alias its record so existing slicing, ordinary swing and contact paths remain scoped to that kitten. Switching control cancels an unfinished slice, but preserves other kittens' sabers. Checkbox changes are queued to the joined simulation handoff. Boarding/disposal removes the record, restores MMU visibility and stops owned sound channels. World/editor reset releases all records.

One shared renderer submits each posed saber and its light emitters. KSA's native hilt instancing copies the transform for each draw; separate renderer instances would conflict with shared render hooks. Noncontrolled kitten processors may publish poses, but only the main gameplay viewport refreshes the frame used to authorize a slice.

## Picker and validation

The target marker is larger, with green eligibility feedback and red rejection feedback. Native hover candidates remain available for red display without authorizing a cut. Click validation and queued/stroke-start reach checks remain unchanged. Hover reuses the native picker and does not scan meshes.

Managed checks cover bounded gestures, exact restoration, pointer ownership, hover rejection and per-kitten lifetime behavior. Host metadata checks validate input hook signatures. Actual appearance, mouse behavior, spatial audio and simultaneous rendering still require the game.
