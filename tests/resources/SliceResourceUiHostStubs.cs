using Purr2D2.Core;
using Purr2D2.Rendering;

namespace Brutal.ImGuiApi
{
    public static class ImGui
    {
        internal static readonly HashSet<string> Clicks = [];
        internal static readonly Dictionary<string, int> SliderChanges = [];
        internal static readonly Dictionary<string, bool> CheckboxChanges = [];
        internal static readonly List<string> Text = [];
        private static readonly Stack<bool> Disabled = new();
        public static bool SliderInt(string label, ref int value, int min, int max, string format = "%d")
        {
            if (!SliderChanges.Remove(label, out var changed)) return false;
            value = Math.Clamp(changed, min, max); return true;
        }
        public static bool Button(string label) => Clicks.Remove(label) && !Disabled.Contains(true);
        public static bool Checkbox(string label, ref bool value)
        {
            if (!CheckboxChanges.Remove(label, out var changed)) return false;
            value = changed; return true;
        }
        public static void TextWrapped(string text) => Text.Add(text);
        public static void TextDisabled(string text) => Text.Add(text);
        public static void BeginDisabled(bool disabled) => Disabled.Push(disabled);
        public static void EndDisabled() => Disabled.Pop();
        public static void EndTable() { }
        public static void SameLine() { }
        internal static void Reset()
        { Clicks.Clear(); SliderChanges.Clear(); CheckboxChanges.Clear(); Text.Clear(); Disabled.Clear(); }
        internal static bool Balanced => Disabled.Count == 0;
    }
}

namespace Purr2D2.Rendering
{
    // The UI consumes immutable snapshots; no live rendering or native-world access enters this harness.
    internal readonly record struct SliceResourceUsage(uint VertexBytesUsed, uint VertexBytesCapacity,
        uint IndexBytesUsed, uint IndexBytesCapacity, int RuntimeTemplateCount, int PreparedCacheCount,
        int FailedPreparationCount, int HotCapCount, int ActiveUploadJobs, long DiskBytes, long DiskByteCapacity,
        int DiskEntries, int DiskEntryCapacity, uint ReclaimableVertexBytes, uint ReclaimableIndexBytes,
        long ReclaimedVertexBytes, long ReclaimedIndexBytes, bool GrowthEnabled = true,
        uint VertexBytesMaximum = 0, uint IndexBytesMaximum = 0, ulong GlobalVertexBufferBytes = 0,
        ulong GlobalIndexBufferBytes = 0, int GrowthCount = 0, bool GrowthPending = false,
        string GrowthStatus = "Not initialized");
}

namespace Purr2D2.Runtime
{
    internal sealed partial class SaberRuntime
    {
        private readonly SaberSettings _settings = new();
        private SliceResourceUsage _sliceResourceUsage;
        private int _sliceResourceVehicleCount, _sliceResourceDebrisCount;
        private string _sliceResourceStatus = "";
        private bool CanMaintainSliceResources = true;
        private int _appliedSettings, _cacheClears, _debrisClears;
        private void ApplySliceResourceSettings() => _appliedSettings++;
        private void QueueSliceCacheClear() => _cacheClears++;
        private void QueueSliceDebrisClear() => _debrisClears++;
        private static bool BeginSaberSettingsTable(string id, string? widestLabel = null) => true;
        private static void SaberSettingsRow(string label) { }
        private static void ShowSaberControlTip(string text) { }
    }
}
