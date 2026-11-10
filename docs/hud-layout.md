# HUD layout controls

Choose **purr2d2 → Arrange HUD**, or enable **Arrange HUD** under **Settings → HUD Overlay Positions**. The Saber controls, Furce controls and kitten list each get a title bar. Drag their title bars to place them. Their gameplay actions are suspended while arranging. Choose **Done**, or turn off **Arrange HUD**, to lock the layout and restore the normal HUD appearance. Empty space between the circular controls remains available to the game when the HUD is locked.

Choose **Detach** on a panel to open it in a standalone window. Move it to another monitor using its title bar. Detached controls work normally after leaving arrangement mode, including held drags, wheel controls, Shift modifiers and Escape cancellation. The window keeps a title bar and offers **View → Always on top**. Detached panels size themselves to fit their controls, temporary slider labels and status text, using the monitor's font size. Targeting markers and Furce capture previews stay over the game scene.

Choose **Reattach**, or close the detached window, to return the panel to its saved position in the game viewport. Detaching does not replace that attached position. The kitten list's color picker opens alongside the list when it is detached.

Attached offsets, detached state, desktop positions and topmost choices save through the existing preferences autosave. Arrangement mode is temporary and does not persist across restarts. Windows whose saved monitor is missing return to the main display. **purr2d2 → Reset HUD layout** restores all three panels and their default positions, including hidden panels. Visibility switches continue to apply to both attached and detached panels.

Standalone windows require KSA's multiple-window mode. When KSA disables that support, panels use the attached HUD while retaining their saved detached preferences.

## Validation

`python3 tools/validate.py --run-tests` includes production HUD host tests against deterministic ImGui/GLFW boundaries, placement/settings round trips, transient geometry, monitor recovery, per-monitor fonts, detached wheel ownership and existing gameplay checks. `python3 tools/check_fragment_templates.py` exercises the actual host signatures and callback routing.

For live KSA verification, move each panel, detach it, leave arrangement mode and exercise every control. Test a custom drag followed immediately by a title-bar drag or Reattach click; overlapping panels; Shift-wheel and Escape in a detached Furce window; the detached kitten color picker; focus moving between game, panel and another app; hiding/reopening panels; a very small main viewport; different-DPI monitors; restart; and removal of a monitor. Check that native camera/picker input never receives panel gestures and world markers remain in the game viewport.
