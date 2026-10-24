using KSA;

namespace Purr2D2.Rendering;

internal static partial class RuntimeFragmentAssets
{
    // These XML-only assets share the existing storage budget and startup reload.
    // Their native mesh/material references already exist; no mesh arena space is used.
    private static PartTemplate RegisterAuthoredDebris(byte[] xml, string id, int surfaces)
    {
        if (ModLibrary.TryGet<PartTemplate>(id, out var existing) && existing != null) return existing;
        var (directory, mod) = CaptureWorkerContext();
        var bundle = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), xml, id, surfaces);
        lock (StorageGate)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, id + ".xml");
            RefreshDiskUsage(directory);
            if (!File.Exists(path) && (_diskBytes + xml.Length > Volatile.Read(ref _diskCapacity)
                || _diskEntries >= Volatile.Read(ref _entryCapacity)))
                throw new InvalidDataException("Persistent debris asset budget is full. Increase the disk or entry limit in Settings.");
            try { WriteImmutable(path, xml); }
            finally { RefreshDiskUsage(directory); }
        }
        foreach (var asset in bundle.Assets) asset.OnDataLoad(mod);
        mod.Register(bundle);
        _registeredTemplates += bundle.Assets.OfType<PartTemplate>().Count();
        return ModLibrary.Get<PartTemplate>(id);
    }
}
