using System.Threading;
using KSA;

namespace Purr2D2.Rendering;

/// <summary>One main-thread registration cursor; each advance performs at most one native call.</summary>
internal sealed class AuthoredDebrisRegistration : IAuthoredDebrisRegistration
{
    private readonly PreparedAuthoredDebrisAsset _prepared;
    private readonly KSA.Mod _mod;
    private int _assetIndex;
    private bool _registered;

    public PartTemplate? Template { get; private set; }
    public bool Done => Template != null;

    internal AuthoredDebrisRegistration(PartTemplate existing)
    {
        Template = existing;
        _prepared = null!;
        _mod = null!;
    }

    internal AuthoredDebrisRegistration(PreparedAuthoredDebrisAsset prepared, KSA.Mod mod)
    {
        _prepared = prepared;
        _mod = mod;
    }

    /// <summary>Advance one OnDataLoad, bundle registration, or template lookup operation.</summary>
    public void AdvanceOne()
    {
        if (Done) return;
        if (_assetIndex < _prepared.Bundle.Assets.Count)
        {
            _prepared.Bundle.Assets[_assetIndex++].OnDataLoad(_mod);
            return;
        }
        if (!_registered)
        {
            _mod.Register(_prepared.Bundle);
            RuntimeFragmentAssets.RecordAuthoredDebrisRegistration(_prepared.Bundle);
            _registered = true;
            return;
        }
        Template = ModLibrary.Get<PartTemplate>(_prepared.Id);
    }
}

internal static partial class RuntimeFragmentAssets
{
    /// <summary>Capture only a path on the joined main thread; no Mod crosses into the worker.</summary>
    internal static string CaptureAuthoredDebrisDirectory()
    {
        if (!_initialized || _mod == null) throw new InvalidOperationException("Runtime slice assets are unavailable.");
        return Path.Combine(_mod.DirectoryPath, "Generated", "Slices");
    }

    /// <summary>Install the worker/main-thread bridge after RuntimeFragmentAssets.Initialize.</summary>
    internal static void InstallAuthoredDebrisCallbacks()
    {
        AuthoredDebrisAssets.CaptureDirectory = CaptureAuthoredDebrisDirectory;
        AuthoredDebrisAssets.PrepareOnWorker = PrepareAuthoredDebrisWorker;
        AuthoredDebrisAssets.BeginRegistration = BeginAuthoredDebrisRegistration;
    }

    /// <summary>Pure-data preparation and persistent XML writes. Never reads KSA world or registry state.</summary>
    internal static PreparedAuthoredDebrisAsset[] PrepareAuthoredDebrisWorker(
        AuthoredDebrisShape[] shapes, string directory, CancellationToken cancel)
    {
        var serializer = FragmentTemplateLoader.CreateSerializer();
        var prepared = new PreparedAuthoredDebrisAsset[shapes.Length];
        Directory.CreateDirectory(directory);
        lock (StorageGate) RefreshDiskUsage(directory);
        try
        {
            for (int i = 0; i < prepared.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var (id, xml) = AuthoredDebrisAssets.Build(shapes[i], cancel);
                var bundle = FragmentTemplateLoader.Read(serializer, xml, id, shapes[i].Surfaces.Length);
                cancel.ThrowIfCancellationRequested();
                lock (StorageGate)
                {
                    string path = Path.Combine(directory, id + ".xml");
                    bool exists = File.Exists(path);
                    if (!exists && (_diskBytes + xml.Length > Volatile.Read(ref _diskCapacity)
                        || _diskEntries >= Volatile.Read(ref _entryCapacity)))
                        throw new InvalidDataException("Persistent debris asset budget is full. Increase the disk or entry limit in Settings.");
                    WriteImmutable(path, xml);
                    if (!exists)
                    {
                        _diskBytes = checked(_diskBytes + xml.Length);
                        _diskEntries++;
                    }
                }
                prepared[i] = new(id, bundle);
            }
            return prepared;
        }
        finally { lock (StorageGate) RefreshDiskUsage(directory); }
    }

    /// <summary>Called on the main thread after detached worker preparation completes.</summary>
    internal static IAuthoredDebrisRegistration BeginAuthoredDebrisRegistration(PreparedAuthoredDebrisAsset prepared)
    {
        if (!_initialized || _mod == null) throw new InvalidOperationException("Runtime slice assets are unavailable.");
        if (ModLibrary.TryGet<PartTemplate>(prepared.Id, out var existing) && existing != null)
            return new AuthoredDebrisRegistration(existing);
        return new AuthoredDebrisRegistration(prepared, _mod);
    }

    internal static void RecordAuthoredDebrisRegistration(AssetBundle bundle) =>
        _registeredTemplates += bundle.Assets.OfType<PartTemplate>().Count();

}
