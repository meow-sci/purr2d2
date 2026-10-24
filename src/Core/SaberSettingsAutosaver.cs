namespace Purr2D2.Core;

/// <summary>Tracks the complete settings object and writes it after changes settle.</summary>
public sealed class SaberSettingsAutosaver
{
    private const double SaveDebounceSeconds = 1;
    private const double FailedSaveRetrySeconds = 5;

    private readonly string _path;
    private string _snapshot;
    private bool _dirty;
    private double _lastChangeTime;
    private double _retryAfter;

    public string? LastError { get; private set; }
    public bool HasPendingChanges => _dirty;

    public SaberSettingsAutosaver(string path, SaberSettings initialSettings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(initialSettings);
        _path = path;
        _snapshot = initialSettings.Snapshot();
    }

    /// <summary>Detects any settings change and saves it after one quiet second.</summary>
    public bool Observe(SaberSettings settings, double monotonicSeconds)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Track(settings, monotonicSeconds);

        if (!_dirty || !double.IsFinite(monotonicSeconds)
            || monotonicSeconds - _lastChangeTime < SaveDebounceSeconds
            || monotonicSeconds < _retryAfter)
            return false;

        return TrySave(settings, monotonicSeconds);
    }

    /// <summary>Writes a pending change immediately, for orderly shutdown.</summary>
    public bool Flush(SaberSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Track(settings, double.NaN);
        return !_dirty || TrySave(settings, 0);
    }

    /// <summary>Forgets pending changes and treats the current settings as the new baseline.</summary>
    public void Rebase(SaberSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _snapshot = settings.Snapshot();
        _dirty = false;
        _lastChangeTime = 0;
        _retryAfter = 0;
        LastError = null;
    }

    /// <summary>Deletes persisted preferences and makes the current settings the clean baseline.</summary>
    public bool DeletePreferencesAndRebase(SaberSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Rebase(settings);
        try
        {
            File.Delete(_path);
            File.Delete(_path + ".tmp");
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            return false;
        }
    }

    private void Track(SaberSettings settings, double monotonicSeconds)
    {
        string current = settings.Snapshot();
        if (string.Equals(current, _snapshot, StringComparison.Ordinal)) return;

        _snapshot = current;
        _dirty = true;
        _lastChangeTime = double.IsFinite(monotonicSeconds) ? monotonicSeconds : 0;
        _retryAfter = 0;
        LastError = null;
    }

    private bool TrySave(SaberSettings settings, double monotonicSeconds)
    {
        try
        {
            settings.Save(_path);
            _snapshot = settings.Snapshot();
            _dirty = false;
            _retryAfter = 0;
            LastError = null;
            return true;
        }
        catch (Exception error)
        {
            LastError = error.Message;
            _retryAfter = monotonicSeconds + FailedSaveRetrySeconds;
            return false;
        }
    }
}
