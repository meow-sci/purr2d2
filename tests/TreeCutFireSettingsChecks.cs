using Purr2D2.Core;

internal static class TreeCutFireSettingsChecks
{
    internal static void Run()
    {
        var settings = new SaberSettings();
        Check(!settings.PassiveContactCutsEnabled, "new and reset settings default to intentional cuts");
        settings.ContactPlaneStrategy = "sweep-fit";
        Check(settings.EffectiveContactPlaneStrategy == "sweep-fit", "fit plane preference is active");
        settings.ContactPlaneStrategy = "blade-strict";
        Check(settings.EffectiveContactPlaneStrategy == "blade-strict", "available plane preferences are active");
        Check(settings.TreeCutFireSeconds == 10, "new settings use ten seconds of tree fire");
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            settings.TreeCutFireSeconds = value;
            settings.Validate();
            Check(settings.TreeCutFireSeconds == 10, "nonfinite fire duration recovers the default");
        }
        settings.TreeCutFireSeconds = -1;
        settings.Validate();
        Check(settings.TreeCutFireSeconds == 0, "zero duration disables tree fire");
        settings.TreeCutFireSeconds = 500;
        settings.Validate();
        Check(settings.TreeCutFireSeconds == 60, "fire duration has a bounded lifetime");
        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-tree-fire-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "{\"BladeLength\":1.4}");
            var migrated = SaberSettings.Load(path);
            Check(migrated.TreeCutFireSeconds == 10 && !migrated.PassiveContactCutsEnabled,
                "old preference files receive the defaults including intentional cuts");
            settings.PassiveContactCutsEnabled = true;
            settings.Save(path);
            Check(SaberSettings.Load(path).PassiveContactCutsEnabled, "passive contact preference survives save/load");
            File.WriteAllText(path, "{\"VesselContactDetector\":\"unknown\",\"ClutterContactDetector\":null,\"ContactPlaneStrategy\":\"bad\",\"SliceExecutionMode\":\"invalid\",\"SliceWorkerCount\":99}");
            migrated = SaberSettings.Load(path);
            Check(migrated.VesselContactDetector == "legacy-ribbon" && migrated.ClutterContactDetector == "legacy-rays"
                && migrated.ContactPlaneStrategy == "legacy-contact" && migrated.SliceExecutionMode == "legacy-serial"
                && migrated.SliceWorkerCount == 4, "invalid methods and numeric limits are validated on load");
            foreach (float seconds in new[] { 0f, 7.5f, 60f })
            {
                settings.TreeCutFireSeconds = seconds;
                settings.Save(path);
                Check(SaberSettings.Load(path).TreeCutFireSeconds == seconds, "configured fire duration survives save/load");
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("Tree cut fire settings checks passed.");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
