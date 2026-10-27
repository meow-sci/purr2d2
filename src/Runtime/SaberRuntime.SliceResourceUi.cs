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
        ImGui.TextWrapped($"Unused upload memory available to recover: {SliceMemoryLabel(usage.ReclaimableVertexBytes + (long)usage.ReclaimableIndexBytes)}.\n\n"
            + $"Recovered this session: {SliceMemoryLabel(usage.ReclaimedVertexBytes + usage.ReclaimedIndexBytes)}.");
        ImGui.TextWrapped($"Automatic growth: {(usage.GrowthEnabled ? "on" : "off")}; vertex limit {SliceMemoryLabel(usage.VertexBytesMaximum)}, index limit {SliceMemoryLabel(usage.IndexBytesMaximum)}.\n"
            + $"Growth operations this session: {usage.GrowthCount}{(usage.GrowthPending ? " (waiting)" : "")}\n"
            + $"Shared game mesh buffers: {SliceMemoryLabel((long)usage.GlobalVertexBufferBytes)} vertices / {SliceMemoryLabel((long)usage.GlobalIndexBufferBytes)} indices.");
        ImGui.TextWrapped("Shared game buffers include stock meshes and fragments restored at startup, beyond the runtime slice reservation shown above.");
        if (!string.IsNullOrWhiteSpace(usage.GrowthStatus)) ImGui.TextWrapped(usage.GrowthStatus);

        if (BeginSaberSettingsTable("##SliceResources", "Source triangle limit"))
        {
            int vertex = _settings.SliceVertexArenaMiB, index = _settings.SliceIndexArenaMiB;
            bool autoGrow = _settings.SliceAutoGrow;
            int vertexMaximum = _settings.SliceVertexMaximumMiB, indexMaximum = _settings.SliceIndexMaximumMiB;
            int disk = _settings.SliceAssetDiskMiB, entries = _settings.SliceAssetEntryLimit;
            int vehicles = _settings.SlicePhysicsVehicleLimit;
            int solids = _settings.SliceSolidLimit, triangles = _settings.SliceTriangleLimit;
            bool memoryChanged = false, liveChanged = false;
            try
            {
                SaberSettingsRow("Closed solid limit");
                liveChanged |= ImGui.SliderInt("##SliceSolidLimit", ref solids,
                    SliceResourceLimits.MinimumSolidLimit, SliceResourceLimits.MaximumSolidLimit);
                ShowSaberControlTip("Maximum closed solids in a part being sliced and in each resulting fragment. Open panels and sharp folds can create separate solids.\n\nApplies to the next cut; larger limits require more preparation work.");
                SaberSettingsRow("Source triangle limit");
                liveChanged |= ImGui.SliderInt("##SliceTriangleLimit", ref triangles,
                    SliceResourceLimits.MinimumTriangleLimit, SliceResourceLimits.MaximumTriangleLimit);
                ShowSaberControlTip("Maximum triangles while copying, preparing and subdividing the source hull. Backing open panels adds triangles.\n\nCombined cut output may use twice this count; each fragment may use five thirds. Applies to the next cut and saves automatically.");
                SaberSettingsRow("Initial vertex memory");
                memoryChanged |= ImGui.SliderInt("##SliceVertexArenaMiB", ref vertex,
                    SliceResourceLimits.MinimumVertexArenaMiB, SliceResourceLimits.MaximumVertexArenaMiB, "%d MiB");
                ShowSaberControlTip("Startup reservation for generated fragment vertices.\n\nApplies after restarting KSA.");
                SaberSettingsRow("Initial index memory");
                memoryChanged |= ImGui.SliderInt("##SliceIndexArenaMiB", ref index,
                    SliceResourceLimits.MinimumIndexArenaMiB, SliceResourceLimits.MaximumIndexArenaMiB, "%d MiB");
                ShowSaberControlTip("Startup reservation for generated fragment indices.\n\nApplies after restarting KSA.");
                SaberSettingsRow("Automatic growth");
                liveChanged |= ImGui.Checkbox("##SliceAutoGrow", ref autoGrow);
                ShowSaberControlTip("Grow buffers as needed, up to the limits below; off leaves current allocations unchanged.\n\nGrowth can briefly pause rendering and temporarily needs both old and new buffers.");
                SaberSettingsRow("Maximum vertex memory");
                liveChanged |= ImGui.SliderInt("##SliceVertexMaximumMiB", ref vertexMaximum,
                    vertex, SliceResourceLimits.MaximumVertexArenaMiB, "%d MiB");
                ShowSaberControlTip("Maximum runtime vertex capacity; applies now.\n\nLowering it does not shrink existing allocations; stock and startup-restored meshes use additional memory.");
                SaberSettingsRow("Maximum index memory");
                liveChanged |= ImGui.SliderInt("##SliceIndexMaximumMiB", ref indexMaximum,
                    index, SliceResourceLimits.MaximumIndexArenaMiB, "%d MiB");
                ShowSaberControlTip("Maximum runtime index capacity; applies now.\n\nLowering it does not shrink existing allocations.");
                SaberSettingsRow("Slice storage");
                liveChanged |= ImGui.SliderInt("##SliceAssetDiskMiB", ref disk,
                    SliceResourceLimits.MinimumAssetDiskMiB, SliceResourceLimits.MaximumAssetDiskMiB, "%d MiB");
                ShowSaberControlTip("Limits new fragment files to this disk budget and applies now.\n\nExisting files and saves stay intact, even when they exceed the budget.");
                SaberSettingsRow("Slice entry limit");
                liveChanged |= ImGui.SliderInt("##SliceAssetEntryLimit", ref entries,
                    SliceResourceLimits.MinimumAssetEntryLimit, SliceResourceLimits.MaximumAssetEntryLimit);
                ShowSaberControlTip("Limits new cache entries and applies now.\n\nExisting asset files stay intact.");
                SaberSettingsRow("Physics vehicle limit");
                liveChanged |= ImGui.SliderInt("##SlicePhysicsVehicleLimit", ref vehicles,
                    SliceResourceLimits.MinimumPhysicsVehicleLimit, SliceResourceLimits.MaximumPhysicsVehicleLimit);
                ShowSaberControlTip("Limits new cuts to this vehicle count in the local physics bubble; applies now.\n\nExisting vehicles stay intact.");
            }
            finally { ImGui.EndTable(); }
            if (memoryChanged || liveChanged)
            {
                _settings.SliceVertexArenaMiB = vertex; _settings.SliceIndexArenaMiB = index;
                _settings.SliceAutoGrow = autoGrow;
                _settings.SliceVertexMaximumMiB = vertexMaximum; _settings.SliceIndexMaximumMiB = indexMaximum;
                _settings.SliceAssetDiskMiB = disk; _settings.SliceAssetEntryLimit = entries;
                _settings.SlicePhysicsVehicleLimit = vehicles;
                _settings.SliceSolidLimit = solids; _settings.SliceTriangleLimit = triangles;
                _settings.Validate();
                // Validation can raise a maximum to match an edited startup size;
                // only growth limits apply live, never the startup reservation.
                ApplySliceResourceSettings();
            }
        }
        var geometry = new SliceGeometryBudget(_settings.SliceSolidLimit, _settings.SliceTriangleLimit);
        ImGui.TextWrapped($"Geometry limits: {geometry.Solids:N0} solids; {geometry.SourceTriangles:N0} source triangles; "
            + $"{geometry.OutputTriangles:N0} combined output triangles; {geometry.FragmentTriangles:N0} triangles per fragment.\n"
            + $"Separate export limits: {SliceResourceLimits.MaximumFragmentVertices:N0} unique positions per fragment and {SliceResourceLimits.MaximumRenderSurfaces:N0} render surfaces.");
        ImGui.TextWrapped("Startup graphics reservations take effect after restarting KSA. Geometry limits apply to the next cut; other limits apply now. Settings save automatically.");
        ImGui.TextWrapped("Registered fragment meshes remain allocated. Automatic growth adds space within the chosen limits.\n\nClearing unused caches recovers unpublished uploads and keeps saved asset files.");

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
        if (!maintain) ImGui.TextDisabled("Cleanup is available in an active world when slicing and Furce are idle.");

        if (_confirmSliceDebrisRemoval)
        {
            ImGui.TextWrapped($"Remove {_sliceResourceDebrisCount} generated debris vehicle(s) from this system?\n\n"
                + "Saved fragment files stay on disk.\n\nLoaded meshes keep their graphics memory until KSA restarts.");
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
