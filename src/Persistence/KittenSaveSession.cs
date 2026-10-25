using System.Runtime.CompilerServices;

namespace Purr2D2.Persistence;

/// <summary>Pairs native transactions by identity; logical state works even when the mod is disabled.</summary>
internal sealed class KittenSaveSession
{
    private sealed record Capture(object? NativeData, KittenSaveDocument Document);
    internal sealed class FileLoad(object save, Func<object?> data, KittenSaveDocument? document)
    {
        internal readonly object Save = save;
        internal readonly Func<object?> Data = data;
        internal readonly KittenSaveDocument? Document = document;
        internal bool Used;
    }
    internal sealed record Reconstruction(long Generation, KittenSaveDocument? Incoming,
        KittenSaveDocument Previous, object? PreviousSystem, bool PreviousSuspended);

    private readonly ConditionalWeakTable<object, Capture> _captures = new();
    private readonly List<FileLoad> _loads = new();
    private long _generation;
    private object? _system;
    internal KittenProfiles Profiles { get; } = new();
    internal bool Suspended { get; private set; }
    internal string Status { get; private set; } = "Kitten choices are saved with KSA saves.";
    internal string? LastWarning { get; private set; }

    internal void Warn(string message)
    {
        Status = message;
        LastWarning = message;
        Console.WriteLine("[purr2d2] " + message);
    }

    internal void InvalidateCapture(object save) => _captures.Remove(save);
    internal void CaptureSave(object save, object? nativeData)
    {
        InvalidateCapture(save);
        _captures.Add(save, new(nativeData, Profiles.Capture()));
    }

    internal void WriteSave(object save, object? nativeData, string directory, bool success)
    {
        bool found = _captures.TryGetValue(save, out var capture);
        InvalidateCapture(save);
        if (!success) { Warn("KSA could not save; kitten settings were not written."); return; }
        if (!found || !ReferenceEquals(capture!.NativeData, nativeData))
        { Warn("Native save has no matching kitten capture; kitten settings were not written."); return; }
        KittenSaveStorage.Write(directory, capture.Document);
        Status = "Kitten choices saved with the game.";
    }

    internal FileLoad BeginFileLoad(object save, Func<object?> data, string directory)
    {
        KittenSaveDocument? document = null;
        try { document = KittenSaveStorage.Read(directory); }
        catch (Exception error) { Warn("Loading without kitten settings: " + error.Message); }
        var frame = new FileLoad(save, data, document);
        _loads.Add(frame);
        return frame;
    }

    internal void EndFileLoad(FileLoad? frame)
    {
        if (frame is not null) _loads.Remove(frame);
    }

    internal Reconstruction BeginReconstruction(object? nativeData, object? currentSystem, bool fileAllowed)
    {
        KittenSaveDocument? incoming = null;
        if (fileAllowed && _loads.Count != 0 && _loads[^1] is { Used: false } frame
            && ReferenceEquals(frame.Data(), nativeData))
        {
            incoming = frame.Document;
            frame.Used = true;
        }
        var token = new Reconstruction(++_generation, incoming, Profiles.Capture(), currentSystem, Suspended);
        Suspended = true;
        return token;
    }

    internal void FinishReconstruction(Reconstruction? token, object? system, Exception? error, bool systemChange)
    {
        if (token is null || token.Generation != _generation) return;
        _system = system;
        if (error is null)
        {
            Profiles.Import(token.Incoming, Warn);
            Suspended = false;
            if (Profiles.PreservedCount == 0) Status = "Kitten choices restored; effects resume when Purr2D2 is enabled.";
        }
        else if (systemChange && ReferenceEquals(token.PreviousSystem, system))
        {
            Profiles.Import(token.Previous, Warn);
            Suspended = token.PreviousSuspended;
            Warn(Suspended ? "Scene remains suspended after an incomplete load; reload a complete save to resume."
                : "System change failed; previous kitten choices retained.");
        }
        else
        {
            Profiles.Import(token.Incoming, Warn);
            Warn("Scene reconstruction failed; kitten choices retained without applying. Reload a complete save to resume.");
        }
    }

    internal void ObserveSystem(object? system)
    {
        if (ReferenceEquals(_system, system)) return;
        _system = system;
        ++_generation;
        Profiles.Import(null, Warn);
        Suspended = false;
    }

    internal void Clear()
    {
        _captures.Clear(); _loads.Clear();
        Profiles.Import(null, Warn);
        _system = null; Suspended = false; ++_generation;
        LastWarning = null;
    }
}
