using Brutal.GlfwApi;
using Brutal.Numerics;
using KSA;
using RenderCore.Input;

// Only the runtime's UI surfaces are stubbed. The transformed IL, signatures, game
// solver/part types and Harmony instruction decoder all come from the actual host.
namespace Purr2D2.Runtime
{
    internal sealed class SaberRuntime
    {
        internal void BeforePhysics(double dt) { }
        internal void Frame(double dt) { }
        internal void DrawSliceOverlay() { }
        internal void DrawMenu() { }
        internal bool FilterOverlayMouseButton(GlfwWindow w, GlfwMouseButton b, GlfwButtonAction a) => false;
        internal bool FilterOverlayCursor(GlfwWindow w) => false;
        internal bool FilterOverlayScroll(GlfwWindow w, double2 o) => false;
        internal bool DropSpinKeyResult = false;
        internal int DropSpinKeyCalls, ForceKeyCalls;
        internal bool FilterSaberDropSpinKey(GlfwWindow w, GlfwKey k, GlfwKeyAction a)
        { DropSpinKeyCalls++; return DropSpinKeyResult; }
        internal bool FilterForceKey(GlfwWindow w, GlfwKey k, GlfwKeyAction a)
        { ForceKeyCalls++; return false; }
        internal int SpaceSomersaultFilterCalls;
        internal bool SpaceSomersaultFilterResult;
        internal bool SpaceSomersaultFilterThrows;
        internal KittenEva? LastSpaceSomersaultOwner;
        internal GlfwKeyEvent LastSpaceSomersaultKeyEvent;
        internal bool FilterSpaceSomersaultKey(KittenEva owner, GlfwKeyEvent keyEvent)
        {
            SpaceSomersaultFilterCalls++;
            LastSpaceSomersaultOwner = owner;
            LastSpaceSomersaultKeyEvent = keyEvent;
            if (SpaceSomersaultFilterThrows) throw new InvalidOperationException("stub filter failure");
            return SpaceSomersaultFilterResult;
        }
        internal int HudButtonSuppressions;
        internal bool SuppressOverlayImGuiMouseButton(GlfwWindow w, GlfwMouseButton b, GlfwButtonAction a)
        { HudButtonSuppressions++; return false; }
        internal bool SuppressOverlayImGuiScroll(GlfwWindow w) => false;
        internal bool DeferHudFocusLoss = false;
        internal bool HudBackendResult = false, HudGameBlock = false;
        internal int HudButtonRoutes, HudScrollRoutes, HudCursorRoutes, HudKeyRoutes;
        internal bool BlockHudGameInput(GlfwWindow w) => HudGameBlock;
        internal bool RouteHudBackendButton(GlfwWindow w, GlfwMouseButton b, GlfwButtonAction a)
        { HudButtonRoutes++; return HudBackendResult; }
        internal bool RouteHudBackendScroll(GlfwWindow w, double2 o)
        { HudScrollRoutes++; return HudBackendResult; }
        internal void RouteHudBackendCursor(GlfwWindow w) { HudCursorRoutes++; }
        internal bool RouteHudBackendKey(GlfwWindow w, GlfwKey k, GlfwKeyAction a)
        { HudKeyRoutes++; return HudBackendResult; }

        internal int OverlayFocusLostCalls;
        internal int PendingSpaceSomersaultCancelCalls;
        internal void OverlayFocusLost() => OverlayFocusLostCalls++;
        internal void CancelPendingSpaceSomersault() => PendingSpaceSomersaultCancelCalls++;
        internal void ResetWorld() { }
        internal void Shutdown() { }
        internal static string? LastReportMessage;
        internal static Exception? LastReportException;
        internal static void Report(string message, Exception error)
        {
            LastReportMessage = message;
            LastReportException = error;
        }
    }
}
namespace Purr2D2.Cutting
{
    internal static class PartSliceTargeting
    {
        internal static void RepairLegacyFragmentPicker(Part part) { }
    }
}
