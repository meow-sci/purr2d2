namespace Purr2D2.Runtime;

// Compile the production activation partial; fake only its UI and world boundaries.
internal sealed partial class SaberRuntime
{
    private bool _disposed, _windowVisible, _settingsWindowBoundsAvailable, _overlayShown, _hasResources;
    private string? _fault;
    private int _resets;
    private void SetSaberOverlayVisible(bool visible) => _overlayShown = visible;
    private void ResetWorld() { _resets++; _hasResources = false; }

    internal static void RunActivationChecks()
    {
        var runtime = new SaberRuntime();
        Check(!runtime._enabled && !runtime._windowVisible && !runtime._overlayShown && !runtime.ApplyActivation()
            && runtime._resets == 0, "a new session starts disabled with no UI or world work");
        runtime._fault = "previous failure";
        runtime.SetEnabled(true);
        Check(runtime._overlayShown && !runtime._windowVisible && runtime._fault == null && runtime.ApplyActivation(),
            "enabling shows the overlay and recovers a previous runtime fault without opening settings");
        runtime._windowVisible = runtime._settingsWindowBoundsAvailable = runtime._hasResources = true;
        runtime.SetEnabled(true);
        Check(runtime._windowVisible && runtime._hasResources && runtime._resets == 0,
            "repeated enable requests preserve current resources and UI");
        runtime.SetEnabled(false);
        Check(!runtime._enabled && !runtime._overlayShown && !runtime._windowVisible && !runtime._settingsWindowBoundsAvailable
            && runtime._hasResources && runtime._resets == 0,
            "disabling hides UI immediately and defers native cleanup while workers may be running");
        Check(!runtime.ApplyActivation() && !runtime._hasResources && runtime._resets == 1,
            "the next settled handoff releases resources and stops runtime work");
        runtime.SetEnabled(false); runtime.ApplyActivation();
        Check(runtime._resets == 1, "disabled frames and duplicate disables do not repeat cleanup");
        runtime.SetEnabled(true); runtime._hasResources = true;
        runtime.SetEnabled(false); runtime.SetEnabled(true);
        Check(runtime.ApplyActivation() && !runtime._hasResources && runtime._resets == 2,
            "rapid off/on still performs the pending cleanup before restarting");
        runtime._disposed = true;
        runtime.SetEnabled(false);
        Check(runtime._enabled && runtime._resets == 2, "disposed runtimes ignore further activation requests");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("Activation check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
