using Purr2D2.Core;

internal static class SpaceSomersaultSettingsChecks
{
    private static readonly (bool Up, bool Down, bool Left, bool Right, SomersaultDirection Expected)[] DirectionCases =
    {
        (false, false, false, false, SomersaultDirection.Forward),
        (false, false, false, true,  SomersaultDirection.Right),
        (false, false, true,  false, SomersaultDirection.Left),
        (false, false, true,  true,  SomersaultDirection.Forward),
        (false, true,  false, false, SomersaultDirection.Backward),
        (false, true,  false, true,  SomersaultDirection.Right),
        (false, true,  true,  false, SomersaultDirection.Left),
        (false, true,  true,  true,  SomersaultDirection.Forward),
        (true,  false, false, false, SomersaultDirection.Forward),
        (true,  false, false, true,  SomersaultDirection.Right),
        (true,  false, true,  false, SomersaultDirection.Left),
        (true,  false, true,  true,  SomersaultDirection.Forward),
        (true,  true,  false, false, SomersaultDirection.Forward),
        (true,  true,  false, true,  SomersaultDirection.Right),
        (true,  true,  true,  false, SomersaultDirection.Left),
        (true,  true,  true,  true,  SomersaultDirection.Forward),
    };

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Main()
    {
        CheckDirectionCases();
        CheckSettingDefaultsAndPersistence();
        Console.WriteLine("PASS: Space somersault arrow directions and setting defaults, autosave, and reset");
    }

    private static void CheckDirectionCases()
    {
        foreach (var (up, down, left, right, expected) in DirectionCases)
        {
            SomersaultDirection actual = SomersaultKeyboardDirection.Resolve(up, down, left, right);
            Check(actual == expected,
                $"arrow keys up={up}, down={down}, left={left}, right={right} resolve to {expected}, not {actual}");
        }
    }

    private static void CheckSettingDefaultsAndPersistence()
    {
        var defaults = new SaberSettings();
        Check(defaults.SpaceSomersaultEnabled,
            "new settings enable Space-bar somersaults by default");

        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-space-somersault-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string missingPath = Path.Combine(directory, "missing.json");
            Check(SaberSettings.Load(missingPath).SpaceSomersaultEnabled,
                "a missing settings file enables Space-bar somersaults by default");

            string legacyPath = Path.Combine(directory, "legacy.json");
            File.WriteAllText(legacyPath, "{\"Volume\":0.42}");
            var legacy = SaberSettings.Load(legacyPath);
            Check(legacy.Volume == .42f && legacy.SpaceSomersaultEnabled,
                "legacy JSON without the new field preserves existing values and defaults the feature on");

            string savedPath = Path.Combine(directory, "saved.json");
            var enabled = new SaberSettings();
            var autosaver = new SaberSettingsAutosaver(savedPath, enabled);
            var disabled = new SaberSettings { SpaceSomersaultEnabled = false };
            Check(!autosaver.Observe(disabled, 10), "a changed option waits for the autosave pause");
            Check(autosaver.Observe(disabled, 11.01), "the changed option saves after the autosave pause");
            Check(!SaberSettings.Load(savedPath).SpaceSomersaultEnabled,
                "explicit false survives autosave and reload");

            var reset = new SaberSettings();
            Check(reset.SpaceSomersaultEnabled,
                "a fresh settings object restores the enabled default for Reset all settings");
            Check(autosaver.DeletePreferencesAndRebase(reset),
                "reset deletes saved preferences and rebases the autosaver to the new defaults");
            Check(!File.Exists(savedPath) && !autosaver.HasPendingChanges
                && !autosaver.Observe(reset, 12),
                "reset remains clean and does not immediately rewrite deleted preferences");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
