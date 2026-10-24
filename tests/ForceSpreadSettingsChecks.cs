using Purr2D2.Core;

internal static class ForceSpreadSettingsChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Main()
    {
        var defaults = new SaberSettings();
        Check(defaults.ForceMaximumSpread == ForceSpreadLimits.DefaultMaximum,
            "new settings use the 20x maximum shared spread");

        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-force-spread-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string legacyPath = Path.Combine(directory, "legacy.json");
            File.WriteAllText(legacyPath, "{\"Volume\":0.42}");
            var legacy = SaberSettings.Load(legacyPath);
            Check(legacy.Volume == .42f && legacy.ForceMaximumSpread == ForceSpreadLimits.DefaultMaximum,
                "legacy settings without a spread cap preserve the default and other preferences");

            string autosavePath = Path.Combine(directory, "autosave.json");
            var baseline = new SaberSettings();
            var autosaver = new SaberSettingsAutosaver(autosavePath, baseline);
            var changed = new SaberSettings { ForceMaximumSpread = 73.5f, Volume = .37f };
            Check(!autosaver.Observe(changed, 10) && autosaver.HasPendingChanges,
                "changing the spread cap marks settings dirty and observes the autosave debounce");
            Check(autosaver.Observe(changed, 11.1) && !autosaver.HasPendingChanges,
                "a settled spread cap change is autosaved");
            var restored = SaberSettings.Load(autosavePath);
            Check(restored.ForceMaximumSpread == 73.5f && restored.Volume == .37f,
                "autosave roundtrip retains the chosen cap and unrelated settings");

            var minimum = new SaberSettings { ForceMaximumSpread = 0 };
            minimum.Validate();
            Check(minimum.ForceMaximumSpread == ForceSpreadLimits.MinimumMaximum,
                "finite values below the range clamp to 1x");

            var maximum = new SaberSettings { ForceMaximumSpread = 120 };
            maximum.Validate();
            Check(maximum.ForceMaximumSpread == ForceSpreadLimits.Maximum,
                "finite values above the range clamp to 100x");

            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var recovered = new SaberSettings { ForceMaximumSpread = invalid, Volume = .29f };
                recovered.Validate();
                Check(recovered.ForceMaximumSpread == ForceSpreadLimits.DefaultMaximum && recovered.Volume == .29f,
                    "nonfinite maximum spread falls back to 20x without changing unrelated settings");
            }

            Console.WriteLine("PASS: Force spread default, legacy compatibility, autosave, and finite 1x–100x validation");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
