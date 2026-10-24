using Purr2D2.Core;

internal static class ForceBreakupSettingsChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static void Main()
    {
        var defaults = new SaberSettings();
        Check(defaults.ForceBreakupGroanPixels == 6f && defaults.ForceBreakupSplitPixels == 18f,
            "new settings start with small continuous pixel thresholds");
        Check(defaults.ForceBreakupPieceLimit == ForceBreakupLimits.Default,
            "new settings use the default breakup debris limit");

        string directory = Path.Combine(Path.GetTempPath(), "purr2d2-force-breakup-settings-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string missingFile = Path.Combine(directory, "missing.json");
            var missing = SaberSettings.Load(missingFile);
            Check(missing.ForceBreakupGroanPixels == 6f && missing.ForceBreakupSplitPixels == 18f
                && missing.ForceBreakupPieceLimit == ForceBreakupLimits.Default,
                "a missing settings file preserves threshold and debris-limit defaults");

            string legacyFile = Path.Combine(directory, "legacy.json");
            File.WriteAllText(legacyFile, "{\"Volume\":0.42}");
            var legacy = SaberSettings.Load(legacyFile);
            Check(legacy.Volume == .42f && legacy.ForceBreakupGroanPixels == 6f
                && legacy.ForceBreakupSplitPixels == 18f
                && legacy.ForceBreakupPieceLimit == ForceBreakupLimits.Default,
                "older JSON without breakup fields preserves defaults and other values");

            string savedFile = Path.Combine(directory, "saved.json");
            var saved = new SaberSettings
            {
                ForceBreakupGroanPixels = 9.25f,
                ForceBreakupSplitPixels = 27.5f,
                ForceBreakupPieceLimit = 173,
            };
            saved.Save(savedFile);
            var restored = SaberSettings.Load(savedFile);
            Check(restored.ForceBreakupGroanPixels == 9.25f && restored.ForceBreakupSplitPixels == 27.5f
                && restored.ForceBreakupPieceLimit == 173,
                "settings persistence retains both thresholds and a debris limit above its default");

            string defaultSnapshot = new SaberSettings().Snapshot();
            string changedSnapshot = new SaberSettings { ForceBreakupPieceLimit = 51 }.Snapshot();
            Check(defaultSnapshot != changedSnapshot,
                "changing the debris limit changes the settings snapshot used by autosave");

            var nonFinite = new SaberSettings
            {
                ForceBreakupGroanPixels = float.NaN,
                ForceBreakupSplitPixels = float.PositiveInfinity,
                Volume = .37f,
                ClutterPickTolerance = .13f,
            };
            nonFinite.Validate();
            Check(nonFinite.ForceBreakupGroanPixels == 6f && nonFinite.ForceBreakupSplitPixels == 18f,
                "nonfinite thresholds recover their defaults");
            Check(nonFinite.Volume == .37f && nonFinite.ClutterPickTolerance == .13f,
                "threshold validation leaves unrelated Force and audio settings alone");

            var low = new SaberSettings { ForceBreakupGroanPixels = -12, ForceBreakupSplitPixels = 0 };
            low.Validate();
            Check(low.ForceBreakupGroanPixels == 1f && low.ForceBreakupSplitPixels == 2f,
                "low values clamp to valid ranges while preserving threshold order");

            var lowPieceLimit = new SaberSettings { ForceBreakupPieceLimit = ForceBreakupLimits.Minimum - 1 };
            lowPieceLimit.Validate();
            Check(lowPieceLimit.ForceBreakupPieceLimit == ForceBreakupLimits.Minimum,
                "breakup debris limits clamp to the shared minimum");

            var high = new SaberSettings { ForceBreakupGroanPixels = 90, ForceBreakupSplitPixels = 1 };
            high.Validate();
            Check(high.ForceBreakupGroanPixels == 64f && high.ForceBreakupSplitPixels == 65f,
                "upper groan bound still leaves at least one pixel before the split threshold");

            var highPieceLimit = new SaberSettings { ForceBreakupPieceLimit = ForceBreakupLimits.Maximum + 1 };
            highPieceLimit.Validate();
            Check(highPieceLimit.ForceBreakupPieceLimit == ForceBreakupLimits.Maximum,
                "breakup debris limits clamp to the shared maximum");

            var unordered = new SaberSettings
            {
                ForceBreakupGroanPixels = 23.5f,
                ForceBreakupSplitPixels = 4,
                Volume = .31f,
                ClutterPickTolerance = .17f,
            };
            unordered.Validate();
            Check(unordered.ForceBreakupGroanPixels == 23.5f && unordered.ForceBreakupSplitPixels == 24.5f,
                "split threshold is raised to remain one pixel beyond groan threshold");
            Check(unordered.Volume == .31f && unordered.ClutterPickTolerance == .17f,
                "ordered threshold correction does not mutate unrelated fields");

            Console.WriteLine("PASS: Force breakup threshold and debris-limit defaults, JSON compatibility, persistence, snapshot changes, bounds and ordering");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
