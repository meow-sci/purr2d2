using Purr2D2.Core;

internal static class CapeSettingsChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Main()
    {
        Check(SaberSettings.MinimumCapeSimulationLimit == 0
            && SaberSettings.MaximumCapeSimulationLimit == 16
            && SaberSettings.DefaultCapeSimulationLimit == 4,
            "cape simulation limits expose the shared 0–16 range and default of 4");
        Check(new SaberSettings().CapeSimulationLimit == SaberSettings.DefaultCapeSimulationLimit,
            "new settings use the default cape simulation limit");

        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-cape-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string missingPath = Path.Combine(directory, "missing.json");
            Check(SaberSettings.Load(missingPath).CapeSimulationLimit == SaberSettings.DefaultCapeSimulationLimit,
                "a missing settings file uses the default cape simulation limit");

            string legacyPath = Path.Combine(directory, "legacy.json");
            File.WriteAllText(legacyPath, "{\"Volume\":0.42}");
            var legacy = SaberSettings.Load(legacyPath);
            Check(legacy.Volume == .42f && legacy.CapeSimulationLimit == SaberSettings.DefaultCapeSimulationLimit,
                "legacy JSON without the cape limit keeps other preferences and uses the default");

            var belowMinimum = new SaberSettings { CapeSimulationLimit = -1 };
            belowMinimum.Validate();
            Check(belowMinimum.CapeSimulationLimit == SaberSettings.MinimumCapeSimulationLimit,
                "negative cape limits clamp to zero");

            var aboveMaximum = new SaberSettings { CapeSimulationLimit = int.MaxValue };
            aboveMaximum.Validate();
            Check(aboveMaximum.CapeSimulationLimit == SaberSettings.MaximumCapeSimulationLimit,
                "cape limits above the maximum clamp to 16");

            string savedPath = Path.Combine(directory, "saved.json");
            foreach (int limit in new[] { SaberSettings.MinimumCapeSimulationLimit, 11, SaberSettings.MaximumCapeSimulationLimit })
            {
                new SaberSettings { CapeSimulationLimit = limit }.Save(savedPath);
                Check(SaberSettings.Load(savedPath).CapeSimulationLimit == limit,
                    $"saving and loading preserves cape simulation limit {limit}");
            }

            var autosaver = new SaberSettingsAutosaver(savedPath, new SaberSettings());
            var changed = new SaberSettings { CapeSimulationLimit = 9 };
            Check(!autosaver.Observe(changed, 10) && autosaver.HasPendingChanges,
                "changing only the cape limit marks the settings dirty and observes the save pause");
            Check(autosaver.Observe(changed, 11.01) && !autosaver.HasPendingChanges
                && SaberSettings.Load(savedPath).CapeSimulationLimit == 9,
                "autosave change detection persists the cape simulation limit");

            var reset = new SaberSettings();
            Check(reset.CapeSimulationLimit == SaberSettings.DefaultCapeSimulationLimit,
                "a fresh settings object restores the cape limit default for Reset all settings");
            Check(autosaver.DeletePreferencesAndRebase(reset)
                && !File.Exists(savedPath) && !autosaver.HasPendingChanges
                && SaberSettings.Load(savedPath).CapeSimulationLimit == SaberSettings.DefaultCapeSimulationLimit,
                "reset deletes saved preferences and leaves the default cape limit as the clean baseline");

            Console.WriteLine("PASS: cape simulation default, legacy JSON, clamping, persistence, autosave and reset");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
