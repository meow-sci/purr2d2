using Brutal.GlfwApi;
using Brutal.Numerics;
using KSA;

// Only the runtime's UI surfaces are stubbed. The transformed IL, signatures, game
// solver/part types and Harmony instruction decoder all come from the actual host.
namespace Purr2D2.Runtime
{
    internal sealed class SaberRuntime
    {
        internal void BeforePhysics(double dt) { }
        internal void Frame(double dt) { }
        internal void DrawSliceOverlay() { }
        internal bool FilterOverlayMouseButton(GlfwWindow w, GlfwMouseButton b, GlfwButtonAction a) => false;
        internal bool FilterOverlayCursor(GlfwWindow w) => false;
        internal bool FilterOverlayScroll(GlfwWindow w, double2 o) => false;
        internal bool SuppressOverlayImGuiMouseButton(GlfwWindow w, GlfwMouseButton b, GlfwButtonAction a) => false;
        internal bool SuppressOverlayImGuiScroll(GlfwWindow w) => false;
        internal void OverlayFocusLost() { }
        internal void ResetWorld() { }
        internal void Shutdown() { }
        internal static void Report(string message, Exception error) { }
    }
}
namespace Purr2D2.Cutting
{
    internal static class PartSliceTargeting
    {
        internal static void RepairLegacyFragmentPicker(Part part) { }
    }
}
