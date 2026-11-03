using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Runtime;

// The real runtime gesture and pure drag run against managed window/appearance boundaries.
internal sealed partial class SaberRuntime
{
    private bool BFL9KUiVisible = true, _overlayLayoutAvailable = true;
    private string? _fault;
    private KittenEva? AppearanceKitten;
    private readonly HashSet<KittenEva> _currentKittens = new();
    private readonly Dictionary<KittenEva, KittenSaber> _kittenSabers = new();
    private SaberOverlayLayout _overlayLayout = SaberOverlayLayout.Create(float2.Zero, new(1920, 1080), 16, 3);
    private SaberOverlayLayout CurrentSaberOverlayLayout() => _overlayLayout;
    private static readonly ImColor8 MovementButtonFill = new(0, 0, 255, 64);
    private readonly List<(KittenEva Owner, float Spin, double Duration)> _queuedDrops = new();
    private enum SaberTogglePhase { None, Equipping, Retracting, Unequipping }
    private sealed class KittenSaber
    {
        internal readonly FakeBlade Blade = new();
        internal readonly FakePose Pose = new();
        internal readonly SaberDropSequence Drop = new();
        internal SaberTogglePhase TogglePhase = SaberTogglePhase.None;
    }
    private sealed class FakeBlade { internal bool Equipped = true; }
    private sealed class FakePose { internal bool HasFreshBodyFrame = true; }
    private void QueueSaberDrop(KittenEva owner, float spinMultiplier = 0,
        double returnDurationSeconds = SaberDropSequence.DefaultReturnDuration)
        => _queuedDrops.Add((owner, spinMultiplier, returnDurationSeconds));

    private static SaberRuntime Scene(out KittenEva owner, out KittenSaber saber)
    {
        Program.Modal = Program.Latch = ImGui.Io.AppFocusLost = false;
        ImGui.Viewport.Pos = float2.Zero; ImGui.Viewport.Size = new(1920, 1080);
        var runtime = new SaberRuntime();
        owner = new(); saber = new();
        runtime.AppearanceKitten = owner;
        runtime._currentKittens.Add(owner); runtime._kittenSabers.Add(owner, saber);
        return runtime;
    }

    internal static void RunDropSpinRuntimeChecks()
    {
        var r = Scene(out var owner, out var saber);
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        Check(r._dropSpinDrag.Held && r._queuedDrops.Count == 0, "pressing Drop chooses spin without queueing a premature action");
        r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(r._queuedDrops.Count == 1 && r._queuedDrops[0] == (owner, 0, 1d), "a tap queues one zero-spin drop for its exact appearance owner");
        r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(r._queuedDrops.Count == 1, "paired native and backend release callbacks cannot duplicate a drop");

        r = Scene(out owner, out saber);
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        var bottom = r._overlayLayout.DropCenter + new float2(0, r._overlayLayout.DropPillTravel);
        r.CompleteSaberDropSpinDrag(bottom);
        Check(r._queuedDrops.Count == 1 && r._queuedDrops[0].Spin == 2,
            "a release-only pointer update selects maximum spin without an intervening cursor callback");
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        Check(r._dropSpinDrag.SpinMultiplier == 0 && r._dropSpinValueText == "0.00x", "every new runtime press resets the transient spin and value label");
        r.UpdateSaberDropSpinDrag(r._overlayLayout.DropCenter + new float2(0, r._overlayLayout.DropPillTravel * .5f));
        DropDrawText.Calls.Clear(); r.DrawSaberDropSpinValueLabel(default, r._overlayLayout);
        Check(DropDrawText.Calls.Count == 2 && DropDrawText.Calls.All(c => c.Size == ImGui.GetFontSize() && c.Wrap == 0 && c.Text == "1.00x")
            && DropDrawText.Calls[0].Position == DropDrawText.Calls[1].Position + new float2(r._overlayLayout.AutoSwingValueShadowOffset)
            && DropDrawText.Calls[0].Color.R == 0 && DropDrawText.Calls[1].Color.R == 255,
            "the live spin value uses regular font size with a black shadow and no wrapping");

        foreach (bool returning in new[] { false, true })
        foreach (var invalidation in new (string Name, Action<SaberRuntime, KittenEva, KittenSaber> Apply)[] {
            ("owner switch", (x, _, _) => x.AppearanceKitten = new()),
            ("removed owner", (x, o, _) => x._currentKittens.Remove(o)),
            ("disposed owner", (_, o, _) => o.IsDisposed = true),
            ("replaced equipment record", (x, o, _) => x._kittenSabers[o] = new()),
            ("unequipped saber", (_, _, s) => s.Blade.Equipped = false),
            ("retraction", (_, _, s) => s.TogglePhase = SaberTogglePhase.Retracting),
            ("stale hand", (_, _, s) => s.Pose.HasFreshBodyFrame = false),
            ("missing physics bubble", (_, o, _) => o.HasPhysicsBubble = false),
            ("hidden HUD", (x, _, _) => x.BFL9KUiVisible = false),
            ("fault", (x, _, _) => x._fault = "test"),
            ("modal", (_, _, _) => Program.Modal = true),
            ("game pointer latch", (_, _, _) => Program.Latch = true),
            ("focus loss", (_, _, _) => ImGui.Io.AppFocusLost = true),
            ("moved layout", (x, _, _) => x._overlayLayout = x._overlayLayout with { SwingCenter = x._overlayLayout.SwingCenter + new float2(1, 0) }),
            ("missing layout", (x, _, _) => x._overlayLayout = default),
            ("unpublished layout", (x, _, _) => x._overlayLayoutAvailable = false),
            ("changed drop phase", (_, _, s) => s.Drop.BeginReturn()),
        })
        {
            if (returning && invalidation.Name is "stale hand" or "missing physics bubble") continue;
            if (!returning && invalidation.Name == "changed drop phase") continue;
            r = Scene(out owner, out saber);
            if (returning) saber.Drop.Released();
            r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
            var release = r._overlayLayout.DropCenter;
            invalidation.Apply(r, owner, saber); r.CompleteSaberDropSpinDrag(release);
            Check(!r._dropSpinDrag.Held && r._queuedDrops.Count == 0,
                invalidation.Name + " cancels a held " + (returning ? "Undrop" : "Drop") + " without a false release action");
        }

        r = Scene(out owner, out saber); r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        r.UpdateSaberDropSpinDrag(new(-1, 10)); r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(!r._dropSpinDrag.Held && r._queuedDrops.Count == 0, "leaving the main window cancels the spin gesture before returning to release");
        r = Scene(out owner, out saber); r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        Check(r.FilterSaberDropSpinKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press)
            && r.FilterSaberDropSpinKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Repeat)
            && r.FilterSaberDropSpinKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Release),
            "Escape cancels Drop and owns its repeat and matching release");
        r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(!r._dropSpinDrag.Held && r._queuedDrops.Count == 0, "a mouse release after Escape cannot perform a canceled drop");

        r = Scene(out owner, out saber); saber.Drop.BeginDrop();
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        Check(!r._dropSpinDrag.Held && r._queuedDrops.Count == 1, "Undrop while reaching remains an immediate press action");
        r = Scene(out owner, out saber); saber.Drop.BeginDrop(); saber.Drop.Advance(1); saber.Drop.Released();
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        Check(r._dropSpinDrag.Held && r._dropDragReturning && r._queuedDrops.Count == 0
            && r._dropSpinValueText == "1000 ms", "pressing Undrop opens the one-second pill without starting recall early");
        r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(r._queuedDrops.Count == 1 && r._queuedDrops[0] == (owner, 0, 1d),
            "a tap on Undrop queues exactly one default one-second return");
        r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(r._queuedDrops.Count == 1, "paired release callbacks cannot duplicate a recall");

        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        bottom = r._overlayLayout.DropCenter + new float2(0, r._overlayLayout.DropPillTravel);
        r.UpdateSaberDropSpinDrag(bottom);
        DropDrawText.Calls.Clear(); r.DrawSaberDropSpinValueLabel(default, r._overlayLayout);
        Check(r._dropSpinValueText == "200 ms" && DropDrawText.Calls.Count == 2
            && DropDrawText.Calls.All(c => c.Size == ImGui.GetFontSize() && c.Wrap == 0 && c.Text == "200 ms"),
            "the fastest Undrop setting displays a regular-font 200 ms value beside its thumb");
        r.CompleteSaberDropSpinDrag(bottom);
        Check(r._queuedDrops.Count == 2 && r._queuedDrops[1] == (owner, 0, .2),
            "the bottom of the runtime pill queues a 200 ms recall with no drop spin");
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        Check(r._dropSpinValueText == "1000 ms" && r._dropSpinDrag.ReturnDurationSeconds == 1,
            "a new Undrop gesture defaults to one second after a previous fast recall");
        var midpoint = r._overlayLayout.DropCenter + new float2(0, r._overlayLayout.DropPillTravel * .5f);
        r.CompleteSaberDropSpinDrag(midpoint);
        Check(r._queuedDrops.Count == 3 && Math.Abs(r._queuedDrops[2].Duration - .6) < 1e-8,
            "Undrop samples the release coordinate even without a cursor update before release");

        r = Scene(out owner, out saber); saber.Drop.Released();
        saber.Pose.HasFreshBodyFrame = false; owner.HasPhysicsBubble = false;
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(r._queuedDrops.Count == 1 && r._queuedDrops[0].Duration == 1,
            "Undrop can still be queued for an offscreen owner without a current physics bubble");

        r = Scene(out owner, out saber); saber.Drop.Released();
        r.BeginSaberDropSpinDrag(owner, r._overlayLayout.DropCenter, r._overlayLayout);
        r.FilterSaberDropSpinKey(Program.Window, GlfwKey.Escape, GlfwKeyAction.Press);
        r.CompleteSaberDropSpinDrag(r._overlayLayout.DropCenter);
        Check(r._queuedDrops.Count == 0 && !r._dropDragReturning,
            "Escape cancels Undrop and clears the return gesture without recalling the hilt");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Drop spin runtime check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
