using System.Diagnostics;
using System.Xml.Serialization;
using Brutal;
using Brutal.GltfApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using Purr2D2.Slicing;

namespace Purr2D2.Rendering;

internal sealed record LoadedFragment(CpuFragmentAsset Data, MeshReference[] Meshes, AssetBundle Bundle);
internal sealed record LoadedSlice(CpuSlice Data, LoadedFragment[] Fragments);

internal static partial class RuntimeFragmentAssets
{
    internal static (string Directory, KSA.Mod Mod) CaptureWorkerContext()
    {
        if (!_initialized || _mod == null) throw new InvalidOperationException("Runtime slice assets are unavailable.");
        return (Path.Combine(_mod.DirectoryPath, "Generated", "Slices"), _mod);
    }

    /// <summary>Worker-only CPU loading. The native loader reads private GLB buffers and
    /// creates detached meshes, exactly as MeshAtlasFileReference.Load does at startup.
    /// OnDataLoad on MeshReference only sets its own identity/mod pointer; no registry,
    /// renderer, shared mesh allocator, world, or global shape pool is accessed here.</summary>
    internal static LoadedSlice LoadDetached(CpuSlice data, string directory, KSA.Mod mod, CancellationToken cancel)
    {
        Directory.CreateDirectory(directory);
        var serializer = new XmlSerializer(typeof(AssetBundle)); // Do not touch XmlHelper's shared cache.
        var loaded = new LoadedFragment[data.Fragments.Length];
        for (int i = 0; i < loaded.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var fragment = data.Fragments[i];
            string glbPath = Path.Combine(directory, fragment.Id + ".glb"), xmlPath = Path.Combine(directory, fragment.Id + ".xml");
            long existing = 0; int entries = 0;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                existing = checked(existing + new FileInfo(path).Length);
                if (Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase)) entries++;
            }
            long additional = (File.Exists(glbPath) ? 0 : fragment.Glb.Length) + (File.Exists(xmlPath) ? 0 : fragment.Xml.Length);
            if (existing + additional > DiskCapacity || (!File.Exists(xmlPath) && entries >= EntryCapacity))
                throw new InvalidDataException("Persistent slice asset budget is full; existing save assets are retained.");
            WriteImmutable(glbPath, fragment.Glb); WriteImmutable(xmlPath, fragment.Xml);
            var meshes = new MeshReference[fragment.MeshNames.Length];
            using (var gltf = new GltfLoader(glbPath))
                for (int m = 0; m < meshes.Length; m++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var mesh = new MeshReference { Id = fragment.MeshNames[m], Simple = false, Interleaved = true };
                    mesh.OnDataLoad(mod);
                    mesh.Load(gltf, m, createDeviceMesh: false); // CPU only; never invokes Bind/Shared.Build.
                    meshes[m] = mesh;
                }
            using var xmlStream = new MemoryStream(fragment.Xml, writable: false);
            var bundle = serializer.Deserialize(xmlStream) as AssetBundle ?? throw new InvalidDataException("Fragment XML could not be decoded.");
            bundle.Assets.RemoveAll(asset => asset is MeshAtlasFileReference);
            // Bytes are durable; stop retaining large duplicate GLB/XML buffers in the handoff.
            loaded[i] = new(fragment with { Glb = [], Xml = [] }, meshes, bundle);
        }
        return new(data with { Fragments = loaded.Select(f => f.Data).ToArray() }, loaded);
    }

    private static void WriteImmutable(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException("Persistent slice asset was modified: " + path);
            return;
        }
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Main-thread-only, bounded registration/upload/preflight. No synchronous GPU fence wait.</summary>
    internal sealed class UploadJob : IDisposable
    {
        private readonly LoadedSlice _source;
        private readonly IEnumerator<int> _steps;
        private StagingPool? _pending;
        private string? _registering;
        internal readonly List<PreparedPhysicsFragment> Fragments = new();
        internal bool Done { get; private set; }
        internal string? Error { get; private set; }
        internal UploadJob(LoadedSlice source) { _source = source; _steps = Prepare().GetEnumerator(); }
        internal void Advance()
        {
            if (Done) return;
            long start = Stopwatch.GetTimestamp(); int units = 0;
            try
            {
                do
                {
                    if (!_steps.MoveNext()) { Done = true; _steps.Dispose(); break; }
                    // Submission or a zero-timeout fence poll consumes this frame's GPU work.
                    if (_pending != null) break;
                } while (++units < 32 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1.5);
            }
            catch (Exception error)
            {
                Error = "Could not prepare slice assets: " + error.Message; Done = true;
                if (_registering != null) FailedPreparations[_registering] = Error + " Restart before retrying this same fragment.";
                _steps.Dispose();
            }
        }
        private IEnumerable<int> Prepare()
        {
            foreach (var loaded in _source.Fragments)
            {
                var data = loaded.Data;
                if (FailedPreparations.TryGetValue(data.Id, out var failed)) throw new InvalidOperationException(failed);
                if (Prepared.TryGetValue(data.Id, out var cached))
                { Fragments.Add(new(cached with { SourceToFragment = data.Frame.SourceToFragment }, data.Frame, data.PositiveSide)); continue; }
                PartTemplate? template = null;
                if (ModLibrary.TryGet<PartTemplate>(data.Id, out var restored)) template = restored;
                if (template == null)
                {
                    uint vertexBytes = checked((uint)data.Uploads.Sum(u => (long)u.VertexBytes));
                    uint indexBytes = checked((uint)data.Uploads.Sum(u => (long)u.IndexBytes));
                    if (vertexBytes > VertexCapacity - _vertexUsed || indexBytes > IndexCapacity - _indexUsed)
                        throw new InvalidOperationException("Runtime slice mesh arena is full. Save and restart to reclaim it.");
                    uint vertex = _vertexBase + _vertexUsed, index = _indexBase + _indexUsed;
                    _vertexUsed += vertexBytes; _indexUsed += indexBytes;
                    var regions = new List<UploadRegion>();
                    for (int i = 0; i < loaded.Meshes.Length; i++)
                    {
                        var mesh = loaded.Meshes[i];
                        if (i < data.Uploads.Length)
                        {
                            var upload = data.Uploads[i];
                            var device = MakeDeviceMesh(mesh.HostMesh, vertex, index);
                            if ((uint)device.VerticesSize != upload.VertexBytes || (uint)device.IndicesSize != upload.IndexBytes)
                                throw new InvalidDataException("Prepared mesh and upload sizes disagree.");
                            mesh.DeviceMeshesInterleaved = [device];
                            regions.Add(new(upload.Bytes, 0, upload.VertexBytes, vertex, false));
                            regions.Add(new(upload.Bytes, upload.VertexBytes, upload.IndexBytes, index, true));
                            vertex += (uint)upload.VertexBytes; index += (uint)upload.IndexBytes;
                        }
                        yield return 0;
                    }
                    int region = 0, consumed = 0;
                    while (region < regions.Count)
                    {
                        _pending = SubmitBatch(regions, ref region, ref consumed);
                        yield return 0;
                        while (!_pending.WaitFor(0)) yield return 0;
                        _pending.Dispose(); _pending = null; // Fence already signalled; Dispose cannot stall.
                    }
                    // Nothing becomes visible in the native registry before every upload completed.
                    _registering = data.Id;
                    foreach (var mesh in loaded.Meshes)
                    {
                        if (!ModLibrary.Register(mesh)) throw new InvalidOperationException("Generated mesh ID was unexpectedly registered.");
                        yield return 0;
                    }
                    foreach (var asset in loaded.Bundle.Assets)
                    {
                        // Runtime meshes are already loaded. Keep atlas only in the durable XML.
                        if (asset is not MeshAtlasFileReference) asset.OnDataLoad(_mod!);
                        yield return 0;
                    }
                    _mod!.Register(loaded.Bundle);
                    template = ModLibrary.Get<PartTemplate>(data.Id);
                }
                yield return 0;
                var prepared = new PreparedFragment(template, data.Id, data.Frame.SourceToFragment, data.Extents, data.MassKg);
                NativeSlicePhysics.Preflight(prepared);
                prepared = prepared with { NativeReady = true };
                Prepared[data.Id] = prepared;
                Fragments.Add(new(prepared, data.Frame, data.PositiveSide));
                _registering = null;
                yield return 0;
            }
        }
        // Called only at shutdown or after Done. Normal cancellation drains preparation in
        // the background of subsequent frames so partially registered IDs remain reusable.
        public void Dispose() { _steps.Dispose(); _pending?.Dispose(); _pending = null; }
    }

    private readonly record struct UploadRegion(byte[] Bytes, int Start, int Count, uint Destination, bool Index);
    private static unsafe StagingPool SubmitBatch(List<UploadRegion> regions, ref int region, ref int consumed)
    {
        const int MaximumBatch = 512 * 1024;
        var renderer = Program.GetRenderer();
        var pool = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
        try
        {
            var staging = pool.AddStagingBuffer(new ByteSize((uint)MaximumBatch));
            var commands = pool.NextCommandBuffer(); commands.Begin();
            using (var mapped = staging.Map())
            {
                var destination = mapped.AsSpan(); int written = 0;
                while (region < regions.Count && written < MaximumBatch)
                {
                    var item = regions[region];
                    int count = Math.Min(item.Count - consumed, MaximumBatch - written);
                    item.Bytes.AsSpan(item.Start + consumed, count).CopyTo(destination.Slice(written, count));
                    var copy = new VkBufferCopy { SrcOffset = new ByteSize((uint)written), DstOffset = new ByteSize(item.Destination + (uint)consumed), Size = new ByteSize((uint)count) };
                    commands.CopyBuffer(staging.VkBuffer, item.Index ? DeviceMeshInterleaved.Shared.IndexAllocation.VkBuffer : DeviceMeshInterleaved.Shared.VertexAllocation.VkBuffer, copy);
                    written += count; consumed += count;
                    if (consumed == item.Count) { region++; consumed = 0; }
                }
            }
            var barrier = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.TransferWriteBit, DstAccessMask = VkAccessFlags.VertexAttributeReadBit | VkAccessFlags.IndexReadBit };
            commands.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.VertexInputBit, VkDependencyFlags.None,
                new ReadOnlySpan<VkMemoryBarrier>(ref barrier), default, default);
            commands.End(); pool.Submit();
            return pool;
        }
        catch { pool.Dispose(); throw; }
    }
}
