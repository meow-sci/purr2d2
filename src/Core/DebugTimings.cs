namespace Purr2D2.Core;

/// <summary>Opt-in telemetry gate shared by the settled thread and diagnostic workers.</summary>
internal static class DebugTimings
{
    internal static volatile bool Enabled;
}
