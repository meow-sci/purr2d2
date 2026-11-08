using Brutal.ImGuiApi;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private void DrawSliceMethodSettings()
    {
        if (!BeginSaberSettingsTable("##SliceMethods", "Cut during ordinary movement")) return;
        try
        {
            bool passive = _settings.PassiveContactCutsEnabled;
            SaberSettingsRow("Cut during ordinary movement");
            if (ImGui.Checkbox("##PassiveContactCutsEnabled", ref passive)) _settings.PassiveContactCutsEnabled = passive;
            ShowSaberControlTip("Off: cuts start only while click-dragging SWING or after selecting a target with Slice. On: movement of your held, lit blade can also cut vessels, trees and rocks while walking or moving normally. Turning this off stops new passive cuts; accepted cuts can still finish.");

            SaberSettingsRow("Vessel contact");
            _settings.VesselContactDetector = SliceMethod("##VesselContactDetector", _settings.VesselContactDetector,
                ("legacy-ribbon", "Mesh ribbon"), ("cached-ribbon", "Cached mesh ribbon (experimental)"));
            ShowSaberControlTip("Mesh ribbon tests the moving blade against vessel triangles. Cached mesh ribbon adds copied-mesh spatial indexing and can find multiple vessel targets, but is experimental; native performance and in-game acceptance are not measured. Changes apply to new contacts.");
            SaberSettingsRow("Tree and rock contact");
            _settings.ClutterContactDetector = SliceMethod("##ClutterContactDetector", _settings.ClutterContactDetector,
                ("legacy-rays", "Sampled rays"), ("all-hit-rays", "All-hit sampled rays"), ("capsule-sweep", "Capsule sweep (experimental)"));
            ShowSaberControlTip("Sampled rays test blade paths against nearby ground objects; all-hit rays also find targets behind others. Capsule sweep tests a thick moving blade against resident collision shapes and confirms surface contacts. It is experimental and may miss or reject contacts. All methods depend on game-loaded clutter colliders. Changes discard unconfirmed contacts.");
            SaberSettingsRow("Contact cut plane");
            _settings.ContactPlaneStrategy = SliceMethod("##ContactPlaneStrategy", _settings.ContactPlaneStrategy,
                ("legacy-contact", "Legacy contact"), ("blade-strict", "Strict blade plane"),
                ("blade-limited", "Limited blade plane"), ("sweep-fit", "Sweep fit"));
            ShowSaberControlTip("Legacy tries additional fallback sections. Strict uses only the blade and movement plane; limited permits small tilts. Sweep fit estimates an anchored plane from the observed blade path. Applies to new contact cuts; Slice selection uses its own plane.");
            SaberSettingsRow("Processing");
            _settings.SliceExecutionMode = SliceMethod("##SliceExecutionMode", _settings.SliceExecutionMode,
                ("legacy-serial", "One cut at a time"), ("queued", "Queued (vessels, trees and rocks)"));
            ShowSaberControlTip("Queued processing can prepare several vessel and ground-object cuts concurrently. Changes wait for current work to finish. Accepted cuts can finish after input is released.");
            if (_settings.SliceExecutionMode == "queued")
            {
                int workers = _settings.SliceWorkerCount, pending = _settings.SlicePendingTargetLimit;
                SaberSettingsRow("Geometry workers");
                if (ImGui.DragInt("##SliceWorkerCount", ref workers, 1, 1, 4, "%d", ImGuiSliderFlags.AlwaysClamp)) _settings.SliceWorkerCount = workers;
                ShowSaberControlTip($"Up to four geometry workers use a separate serialized loader. The effective worker count is also limited to {Math.Max(1, Environment.ProcessorCount - 2)} on this computer. Changes apply after current work finishes.");
                SaberSettingsRow("Pending targets");
                if (ImGui.DragInt("##SlicePendingTargetLimit", ref pending, 1, 16, 512, "%d", ImGuiSliderFlags.AlwaysClamp)) _settings.SlicePendingTargetLimit = pending;
                ShowSaberControlTip("Bounds pending vessel, tree and rock slice targets together. Changes apply after current work finishes.");
                int memory = _settings.SliceWorkingMemoryMiB;
                SaberSettingsRow("Working memory (MiB)");
                if (ImGui.DragInt("##SliceWorkingMemoryMiB", ref memory, 1, 64, 1024, "%d", ImGuiSliderFlags.AlwaysClamp)) _settings.SliceWorkingMemoryMiB = memory;
                ShowSaberControlTip("Limits estimated and tracked queued cut buffers. Temporary geometry, detached meshes and native allocations are estimates rather than a hard total memory limit. Changes apply after current work finishes.");
            }

            if (SliceClutterCoverageHooks.ReadinessKnown)
            {
                bool extendCoverage = _settings.SliceClutterReachExtension;
                SaberSettingsRow("Experimental: extend clutter coverage");
                if (ImGui.Checkbox("##SliceClutterReachExtension", ref extendCoverage))
                    _settings.SliceClutterReachExtension = extendCoverage;
                ShowSaberControlTip("Experimental host-tested coverage tracking. Native KSA forest readiness, performance and acceptance have not been measured. Requests nearby ground-clutter cells out to the equipped blade's reach, up to 64 m; generation and physics installation happen asynchronously, and only verified cells count as ready. This can increase clutter generation and physics cost. Off by default.");
                if (_settings.SliceClutterReachExtension)
                {
                    var coverage = SliceClutterCoverageHooks.Status;
                    ImGui.TextWrapped($"Coverage {coverage.State}: {coverage.Requested:F1} m requested, {coverage.Admitted:F1} m admitted; {coverage.Readbacks}/{coverage.Expected} generated-cell readbacks and {coverage.ReadyCells}/{coverage.Expected} verified ready cells.");
                    if (_coverageDiagnostic.Length != 0) ImGui.TextWrapped(_coverageDiagnostic);
                }
            }
        }
        finally { ImGui.EndTable(); }
        bool queuedActive = _clutterQueue != null || _vesselQueue != null;
        if ((SliceWorkActive || _activeClutterDetector == "capsule-sweep") && ImGui.Button("Cancel pending cuts"))
            _cancelQueuedClutterRequested = true;
        if ((_settings.SliceExecutionMode == "queued") != queuedActive)
            ImGui.TextWrapped($"Requested: {_settings.SliceExecutionMode}; active: {(queuedActive ? "queued" : "legacy-serial")}. Waiting for the next settled update or current work to finish.");
        else if (_clutterQueue != null && (_clutterQueueWorkers != _settings.SliceWorkerCount || _clutterQueueLimit != _settings.SlicePendingTargetLimit))
            ImGui.TextWrapped($"Requested: {_settings.SliceWorkerCount} workers, {_settings.SlicePendingTargetLimit} pending targets; active: {_clutterQueueWorkers} workers, {_clutterQueueLimit} pending targets. Current work is finishing.");
        ImGui.TextWrapped("Cached ribbon, capsule sweep and extended clutter coverage are experimental. Host checks pass; native KSA performance and acceptance are not measured. Distant clutter needs resident colliders; optional extended coverage requests those cells while the blade is equipped.");
    }

    // Keep a known but unavailable requested preference intact until the user
    // explicitly selects the active fallback.
    private static string SliceMethod(string id, string requested, params (string Id, string Label)[] choices)
    {
        var selected = Array.FindIndex(choices, choice => choice.Id == requested);
        if (ImGui.BeginCombo(id, choices[selected < 0 ? 0 : selected].Label))
        {
            try
            {
                foreach (var choice in choices)
                    if (ImGui.Selectable(choice.Label, requested == choice.Id)) requested = choice.Id;
            }
            finally { ImGui.EndCombo(); }
        }
        if (Array.FindIndex(choices, choice => choice.Id == requested) < 0)
            ImGui.TextWrapped($"Requested: {requested}; active: {choices[0].Id}");
        return requested;
    }
}
