using Brutal.Numerics;
using Purr2D2.Core;

internal static class HudPlacementChecks
{
    public static void Run()
    {
        var original = SaberOverlayLayout.Create(new(100, 200), new(1920, 1080), 16, 20, 32, false);
        var delta = new float2(-2500, 700);
        var moved = original.TranslateControls(delta).TranslateList(new(100, -40));
        Check(moved.Hit(original.SwingCenter + delta) == SaberOverlayHit.Swing
            && moved.Hit(original.DropCenter + delta) == SaberOverlayHit.Drop
            && moved.ListMin == original.ListMin + new float2(100, -40),
            "panel translation preserves exact circular hits and moves the kitten list independently");
        Check(moved.DeploymentBarMin(0, 0) == original.DeploymentBarMin(0, 0) + new float2(100, -40)
            && moved.AutoSwingValueEnvelopeMax == original.AutoSwingValueEnvelopeMax + delta,
            "transient sliders, value labels and list rows follow their owning panel");
        var force = ForceOverlayLayout.Create(original, new(100, 200), new(1920, 1080));
        var movedForce = force.Translate(delta);
        Check(movedForce.Hit(force.MoveCenter + delta) == ForceOverlayHit.Move
            && movedForce.HitRotation(force.RotationHandleCenter(1) + delta, 1)
            && movedForce.ControlsMax == force.ControlsMax + delta,
            "detached Force controls retain rotation and complete transient bounds");
        Check(HudPlacement.ClampPosition(new(-5000, 9000), new(400, 600), new(-1920, 0), new(1920, 1080)) == new float2(-1920, 480),
            "restoration clamps both axes while preserving negative desktop coordinates");
        Check(HudPlacement.TitleVisible(new(-1800, 20), new(400, 600), new(-1920, 0), new(1920, 1080), 30)
            && !HudPlacement.TitleVisible(new(-5000, 20), new(400, 600), new(-1920, 0), new(1920, 1080), 30),
            "a real secondary monitor is retained and a removed monitor is recognized");
        Check(HudPlacement.ClampPosition(new(float.NaN), new(400, 600), new(10, 20), new(200, 100)) == new float2(10, 20),
            "invalid and oversized panels recover to a reachable title bar");
        string path = Path.Combine(Path.GetTempPath(), "purr2d2-hud-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, "{\"SaberControlsOffsetX\":0.2,\"ForceControlsOffsetY\":-0.3}");
            var settings = SaberSettings.Load(path);
            Check(!settings.SaberHudWindow.Detached && settings.KittenListOffsetX == 0 && settings.SaberControlsOffsetX == .2f,
                "existing preferences retain their positions and default to attached panels");
            settings.SaberHudWindow = new() { Detached = true, HasPosition = true, X = -1200, Y = 300, AlwaysOnTop = true };
            settings.ForceHudWindow.Detached = true; settings.KittenHudWindow.Detached = true;
            settings.KittenListOffsetX = -.25f; settings.KittenListOffsetY = .2f;
            settings.Save(path);
            var loaded = SaberSettings.Load(path);
            Check(loaded.SaberHudWindow is { Detached: true, HasPosition: true, X: -1200, Y: 300, AlwaysOnTop: true }
                && loaded.ForceHudWindow.Detached && loaded.KittenHudWindow.Detached
                && loaded.KittenListOffsetX == -.25f && loaded.KittenListOffsetY == .2f,
                "all panel modes, desktop positions and independent list placement survive restart");
            loaded.KittenHudWindow = null!; loaded.SaberHudWindow.X = float.PositiveInfinity;
            loaded.Validate();
            Check(loaded.KittenHudWindow != null && !loaded.SaberHudWindow.HasPosition,
                "corrupt or null panel preferences remain recoverable");
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS: independent HUD geometry, desktop recovery and backwards-compatible placement persistence");
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
