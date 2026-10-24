using Brutal.ImGuiApi;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    public static void Main()
    {
        const uint mib = 1024 * 1024;
        var runtime = new SaberRuntime
        {
            _sliceResourceUsage = new(32 * mib, 128 * mib, 8 * mib, 64 * mib,
                12, 5, 1, 2, 0, 256 * mib, 2048L * mib, 24, 8192, 2 * mib, mib, 3 * mib, mib,
                true, 512 * mib, 256 * mib, 900UL * mib, 300UL * mib, 2, true, "Waiting for uploads."),
            _sliceResourceVehicleCount = 42, _sliceResourceDebrisCount = 7, _sliceResourceStatus = "Resource snapshot ready."
        };
        void Draw(params string[] clicks)
        {
            ImGui.Reset(); foreach (string click in clicks) ImGui.Clicks.Add(click);
            runtime.DrawSliceResourceSettings();
            Check(ImGui.Balanced, "resource controls balance disabled scopes");
        }
        Draw();
        Check(ImGui.Text.Any(text => text.Contains("32 MiB / 128 MiB") && text.Contains("8 MiB / 64 MiB"))
            && ImGui.Text.Any(text => text.Contains("42 / 128") && text.Contains("24 / 8192"))
            && ImGui.Text.Contains("Resource snapshot ready.") && runtime._cacheClears == 0 && runtime._debrisClears == 0,
            "resource UI displays the immutable memory, disk, physics and status snapshots without maintenance");
        Check(ImGui.Text.Any(text => text.Contains("vertex limit 512 MiB") && text.Contains("index limit 256 MiB")
                && text.Contains("2 (waiting)") && text.Contains("900 MiB vertices / 300 MiB indices"))
            && ImGui.Text.Contains("Waiting for uploads."),
            "growth status and actual shared game allocations are shown separately from slice capacity");

        ImGui.Reset(); ImGui.SliderChanges["##SliceVertexArenaMiB"] = 256;
        ImGui.SliderChanges["##SliceIndexArenaMiB"] = 128;
        runtime.DrawSliceResourceSettings();
        Check(runtime._settings.SliceVertexArenaMiB == 256 && runtime._settings.SliceIndexArenaMiB == 128
            && runtime._appliedSettings == 1 && runtime._preferencesSavedUntil == 0
            && ImGui.Text.Any(text => text.Contains("Save preferences and restart KSA")),
            "initial memory sliders explain restart and synchronize ceilings without resizing the initial reservation");
        ImGui.Reset(); ImGui.SliderChanges["##SliceAssetDiskMiB"] = 4096;
        ImGui.SliderChanges["##SliceAssetEntryLimit"] = 12000; ImGui.SliderChanges["##SlicePhysicsVehicleLimit"] = 256;
        runtime.DrawSliceResourceSettings();
        Check(runtime._settings.SliceAssetDiskMiB == 4096 && runtime._settings.SliceAssetEntryLimit == 12000
            && runtime._settings.SlicePhysicsVehicleLimit == 256 && runtime._appliedSettings == 2,
            "storage and physics edits are validated and queued for immediate application together");
        ImGui.Reset(); ImGui.CheckboxChanges["##SliceAutoGrow"] = false;
        ImGui.SliderChanges["##SliceVertexMaximumMiB"] = 768; ImGui.SliderChanges["##SliceIndexMaximumMiB"] = 320;
        runtime.DrawSliceResourceSettings();
        Check(!runtime._settings.SliceAutoGrow && runtime._settings.SliceVertexMaximumMiB == 768
            && runtime._settings.SliceIndexMaximumMiB == 320 && runtime._appliedSettings == 3
            && runtime._settings.SliceVertexArenaMiB == 256 && runtime._settings.SliceIndexArenaMiB == 128,
            "growth toggle and ceilings apply immediately without changing startup reservations");
        ImGui.Reset(); ImGui.SliderChanges["##SliceVertexMaximumMiB"] = 1;
        ImGui.SliderChanges["##SliceIndexMaximumMiB"] = 1;
        runtime.DrawSliceResourceSettings();
        Check(runtime._settings.SliceVertexMaximumMiB == 256 && runtime._settings.SliceIndexMaximumMiB == 128,
            "growth controls cannot set ceilings below the selected startup reservation");

        Draw("Clear unused slice caches");
        Check(runtime._cacheClears == 1 && runtime._debrisClears == 0,
            "cache clearing queues only cache maintenance");
        Draw("Remove generated slice debris");
        Check(runtime._confirmSliceDebrisRemoval && runtime._debrisClears == 0
            && ImGui.Text.Any(text => text.Contains("Remove 7") && text.Contains("Saved fragment files will stay")),
            "debris removal first displays a count and file-preservation confirmation without removing anything");
        Draw("Keep debris");
        Check(!runtime._confirmSliceDebrisRemoval && runtime._debrisClears == 0,
            "declining confirmation keeps debris and dismisses the prompt");
        Draw("Remove generated slice debris"); Draw("Confirm debris removal");
        Check(!runtime._confirmSliceDebrisRemoval && runtime._debrisClears == 1,
            "explicit confirmation queues one debris removal and clears its confirmation state");
        Draw("Confirm debris removal");
        Check(runtime._debrisClears == 1, "a later click cannot repeat a completed removal confirmation");

        Draw("Remove generated slice debris"); runtime.CanMaintainSliceResources = false;
        Draw("Clear unused slice caches", "Remove generated slice debris", "Confirm debris removal");
        Check(!runtime._confirmSliceDebrisRemoval && runtime._cacheClears == 1 && runtime._debrisClears == 1,
            "a busy or unavailable world disables maintenance and discards an older confirmation");
        runtime.CanMaintainSliceResources = true; runtime._sliceResourceDebrisCount = 0;
        Draw("Remove generated slice debris", "Confirm debris removal");
        Check(!runtime._confirmSliceDebrisRemoval && runtime._debrisClears == 1,
            "zero eligible debris cannot open or accept a removal confirmation");
        Console.WriteLine("Slice resource UI checks passed (snapshot usage, initial memory, bounded live growth, cleanup confirmation and availability).");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Slice resource UI check failed: " + message); }
}
