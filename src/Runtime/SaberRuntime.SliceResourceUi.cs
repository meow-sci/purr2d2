using Brutal.ImGuiApi;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private bool _confirmSliceDebrisRemoval;

    private void DrawSliceResourceSettings()
    {
        var usage = _sliceResourceUsage;
        ImGui.TextWrapped($"Vertex memory: {SliceMemoryLabel(usage.VertexBytesUsed)} / {SliceMemoryLabel(usage.VertexBytesCapacity)}\n"
            + $"Index memory: {SliceMemoryLabel(usage.IndexBytesUsed)} / {SliceMemoryLabel(usage.IndexBytesCapacity)}\n"
            + $"Loaded fragment templates: {usage.RuntimeTemplateCount}; prepared: {usage.PreparedCacheCount}; uploads: {usage.ActiveUploadJobs}\n"
            + $"Preparation failures: {usage.FailedPreparationCount}; hot cut surfaces: {usage.HotCapCount}");
        ImGui.TextWrapped($"Saved slice assets: {SliceMemoryLabel(usage.DiskBytes)} / {SliceMemoryLabel(usage.DiskByteCapacity)}; "
            + $"{usage.DiskEntries} / {usage.DiskEntryCapacity} entries\n"
            + $"Vehicles in local physics bubble: {_sliceResourceVehicleCount} / {_settings.SlicePhysicsVehicleLimit}\n"
            + $"Removable generated slice debris in this system: {_sliceResourceDebrisCount}");
        ImGui.TextWrapped($"Unused upload memory available to recover: {SliceMemoryLabel(usage.ReclaimableVertexBytes + (long)usage.ReclaimableIndexBytes)}. "
            + $"Recovered this session: {SliceMemoryLabel(usage.ReclaimedVertexBytes + usage.ReclaimedIndexBytes)}.");

        if (BeginSaberSettingsTable("##SliceResources", "Physics vehicle limit"))
        {
            int vertex = _settings.SliceVertexArenaMiB, index = _settings.SliceIndexArenaMiB;
            int disk = _settings.SliceAssetDiskMiB, entries = _settings.SliceAssetEntryLimit;
            int vehicles = _settings.SlicePhysicsVehicleLimit;
            bool memoryChanged = false, liveChanged = false;
            try
            {
                SaberSettingsRow("Vertex memory");
                memoryChanged |= ImGui.SliderInt("##SliceVertexArenaMiB", ref vertex,
                    SliceResourceLimits.MinimumVertexArenaMiB, SliceResourceLimits.MaximumVertexArenaMiB, "%d MiB");
                ShowSaberControlTip("Save preferences and restart KSA to change the memory reserved for generated fragment vertices.");
                SaberSettingsRow("Index memory");
                memoryChanged |= ImGui.SliderInt("##SliceIndexArenaMiB", ref index,
                    SliceResourceLimits.MinimumIndexArenaMiB, SliceResourceLimits.MaximumIndexArenaMiB, "%d MiB");
                ShowSaberControlTip("Save preferences and restart KSA to change the memory reserved for generated fragment triangles.");
                SaberSettingsRow("Slice storage");
                liveChanged |= ImGui.SliderInt("##SliceAssetDiskMiB", ref disk,
                    SliceResourceLimits.MinimumAssetDiskMiB, SliceResourceLimits.MaximumAssetDiskMiB, "%d MiB");
                ShowSaberControlTip("Applies now. Stops new fragment files at this budget; existing files and saves stay intact even when already over budget.");
                SaberSettingsRow("Slice entry limit");
                liveChanged |= ImGui.SliderInt("##SliceAssetEntryLimit", ref entries,
                    SliceResourceLimits.MinimumAssetEntryLimit, SliceResourceLimits.MaximumAssetEntryLimit);
                ShowSaberControlTip("Applies now. Stops new cache entries at this limit; existing asset files stay intact.");
                SaberSettingsRow("Physics vehicle limit");
                liveChanged |= ImGui.SliderInt("##SlicePhysicsVehicleLimit", ref vehicles,
                    SliceResourceLimits.MinimumPhysicsVehicleLimit, SliceResourceLimits.MaximumPhysicsVehicleLimit);
                ShowSaberControlTip("Applies now. New cuts stop before they would exceed this number of vehicles in the local physics bubble. Existing vehicles are kept.");
            }
            finally { ImGui.EndTable(); }
            if (memoryChanged || liveChanged)
            {
                _settings.SliceVertexArenaMiB = vertex; _settings.SliceIndexArenaMiB = index;
                _settings.SliceAssetDiskMiB = disk; _settings.SliceAssetEntryLimit = entries;
                _settings.SlicePhysicsVehicleLimit = vehicles;
                _settings.Validate();
                _preferencesSavedUntil = 0;
                if (liveChanged) ApplySliceResourceSettings();
            }
        }
        ImGui.TextWrapped("Save preferences and restart KSA to change graphics memory sizes. Physics and storage limits apply now; Save preferences keeps them for future runs.");
        ImGui.TextWrapped("Loaded fragment meshes stay in graphics memory until KSA restarts. Clearing unused caches recovers unused preparations and upload memory; it preserves saved asset files.");

        bool maintain = CanMaintainSliceResources;
        if (!maintain || _sliceResourceDebrisCount <= 0) _confirmSliceDebrisRemoval = false;
        ImGui.BeginDisabled(!maintain);
        try
        {
            if (ImGui.Button("Clear unused slice caches") && maintain) QueueSliceCacheClear();
            ImGui.BeginDisabled(_sliceResourceDebrisCount <= 0);
            try
            {
                if (ImGui.Button("Remove generated slice debris") && maintain && _sliceResourceDebrisCount > 0)
                    _confirmSliceDebrisRemoval = true;
            }
            finally { ImGui.EndDisabled(); }
        }
        finally { ImGui.EndDisabled(); }
        if (!maintain) ImGui.TextDisabled("Cleanup is available in an active world when slicing and Force are idle.");

        if (_confirmSliceDebrisRemoval)
        {
            ImGui.TextWrapped($"Remove {_sliceResourceDebrisCount} generated debris vehicle(s) from this system? "
                + "Saved fragment files will stay on disk. Loaded meshes will keep their graphics memory until KSA restarts.");
            if (ImGui.Button("Confirm debris removal"))
            {
                if (CanMaintainSliceResources) QueueSliceDebrisClear();
                _confirmSliceDebrisRemoval = false;
            }
            ImGui.SameLine();
            if (ImGui.Button("Keep debris")) _confirmSliceDebrisRemoval = false;
        }
        if (!string.IsNullOrWhiteSpace(_sliceResourceStatus)) ImGui.TextWrapped(_sliceResourceStatus);
    }

    private static string SliceMemoryLabel(long bytes) => $"{bytes / (1024d * 1024):0.#} MiB";
}
