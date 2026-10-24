using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Brutal;
using Brutal.GltfApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using Purr2D2.Core;
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
        var serializer = FragmentTemplateLoader.CreateSerializer();
        var loaded = new LoadedFragment[data.Fragments.Length];
        for (int i = 0; i < loaded.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var fragment = data.Fragments[i];
            string glbPath = Path.Combine(directory, fragment.Id + ".glb"), xmlPath = Path.Combine(directory, fragment.Id + ".xml");
            lock (StorageGate)
            {
                RefreshDiskUsage(directory);
                long additional = (File.Exists(glbPath) ? 0 : fragment.Glb.Length) + (File.Exists(xmlPath) ? 0 : fragment.Xml.Length);
                if ((additional > 0 && _diskBytes + additional > Volatile.Read(ref _diskCapacity))
                    || (!File.Exists(xmlPath) && _diskEntries >= Volatile.Read(ref _entryCapacity)))
                    throw new InvalidDataException("Persistent slice asset budget is full. Increase the disk or entry limit in Settings; existing save assets are retained.");
                try { WriteImmutable(glbPath, fragment.Glb); WriteImmutable(xmlPath, fragment.Xml); }
                finally { RefreshDiskUsage(directory); }
            }
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
            var bundle = FragmentTemplateLoader.Read(serializer, fragment.Xml, fragment.Id, fragment.Uploads.Length);
            FragmentTopology.Remember(XDocument.Parse(Encoding.UTF8.GetString(fragment.Xml)));
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
        private DetachedSliceFragment? _nativePreparing;
        private SliceMeshArena.Allocation? _allocation;
        private SliceMeshGrowthDemand? _growthDemand;
        internal SliceMeshGrowthDemand? GrowthDemand => _growthDemand;
        private bool _cancelled, _disposed;
        internal readonly List<PreparedPhysicsFragment> Fragments = new();
        internal bool Done { get; private set; }
        internal string? Error { get; private set; }
        internal string SlowestStep { get; private set; } = "none";
        internal double SlowestStepMs { get; private set; }
        private string _step = "asset setup";
        internal UploadJob(LoadedSlice source)
        {
            _source = source; _steps = Prepare().GetEnumerator(); _activeUploadJobs++; UploadJobs.Add(this);
        }
        internal void Advance()
        {
            if (Done) return;
            long start = Stopwatch.GetTimestamp(); int units = 0;
            try
            {
                if (_growthPending) { TryDrainForGrowth(); return; }
                do
                {
                    long stepStarted = Stopwatch.GetTimestamp();
                    bool more = _steps.MoveNext();
                    double elapsed = Stopwatch.GetElapsedTime(stepStarted).TotalMilliseconds;
                    if (elapsed > SlowestStepMs) { SlowestStepMs = elapsed; SlowestStep = _step; }
                    if (!more) { Done = true; _steps.Dispose(); break; }
                    // Submission or a zero-timeout fence poll consumes this frame's GPU work.
                    // A native unit is indivisible; never stack a second one in this frame.
                    if (_pending != null || _steps.Current != 0) break;
                } while (++units < 32 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1.5);
            }
            catch (Exception error)
            {
                Error = error.Message; Done = true;
                _growthDemand?.Cancel();
                if (_registering != null) FailedPreparations[_registering] = Error + " Asset registration was interrupted; restart to reload it.";
                _steps.Dispose();
            }
        }
        private IEnumerable<int> Prepare()
        {
            foreach (var loaded in _source.Fragments)
            {
                // Published registration must drain to keep native IDs complete.
                // Before publication, cancellation may abandon the reserved region
                // after completing only its already-submitted GPU copies.
                if (_cancelled) yield break;
                var data = loaded.Data;
                if (FailedPreparations.TryGetValue(data.Id, out var failed)) throw new InvalidOperationException(failed);
                Prepared.TryGetValue(data.Id, out var cached);
                PartTemplate? template = cached?.Template;
                if (ModLibrary.TryGet<PartTemplate>(data.Id, out var restored)) template = restored;
                if (template == null)
                {
                    uint vertexBytes = checked((uint)data.Uploads.Sum(u => (long)u.VertexBytes));
                    uint indexBytes = checked((uint)data.Uploads.Sum(u => (long)u.IndexBytes));
                    if (_arena == null) throw new InvalidOperationException("The runtime slice mesh arena is unavailable.");
                    while (!_arena.TryAllocate(vertexBytes, indexBytes, out _allocation) || _allocation == null)
                    {
                        _growthDemand ??= new(vertexBytes, indexBytes);
                        if (!RequestGrowth(_growthDemand, out string reason)) throw new InvalidOperationException(reason);
                        _step = "waiting for mesh growth";
                        yield return 1;
                        if (_cancelled) yield break;
                    }
                    _growthDemand?.Cancel(); _growthDemand = null;
                    uint vertex = _allocation.VertexOffset, index = _allocation.IndexOffset;
                    var regions = new List<UploadRegion>();
                    for (int i = 0; i < loaded.Meshes.Length; i++)
                    {
                        if (_cancelled) yield break;
                        _step = "mesh binding";
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
                        if (_cancelled) yield break;
                        _step = "GPU upload";
                        _pending = SubmitBatch(regions, ref region, ref consumed);
                        yield return 0;
                        while (_pending != null && !_pending.WaitFor(0)) yield return 0;
                        _pending?.Dispose(); _pending = null; // Fence already signalled; Dispose cannot stall.
                        if (_cancelled) yield break;
                    }
                    // Nothing becomes visible in the native registry before every upload completed.
                    // From this point an interrupted registration can still leave native references;
                    // the allocation must remain pinned even if the fragment never spawns.
                    if (_cancelled) yield break;
                    _arena.Publish(_allocation); _allocation = null;
                    _registering = data.Id;
                    foreach (var mesh in loaded.Meshes)
                    {
                        _step = "mesh registration";
                        if (!ModLibrary.Register(mesh)) throw new InvalidOperationException("Generated mesh ID was unexpectedly registered.");
                        yield return 0;
                    }
                    foreach (var asset in loaded.Bundle.Assets)
                    {
                        _step = "template initialization";
                        // Runtime meshes are already loaded. Keep atlas only in the durable XML.
                        if (asset is not MeshAtlasFileReference) asset.OnDataLoad(_mod!);
                        yield return 1;
                    }
                    _step = "bundle registration";
                    _mod!.Register(loaded.Bundle);
                    template = ModLibrary.Get<PartTemplate>(data.Id);
                    _registeredTemplates++;
                    _registering = null; // A preflight failure can retry a completely registered template.
                }
                yield return 0;
                if (_cancelled) continue;
                var prepared = new PreparedFragment(template, data.Id, data.Frame.SourceToFragment, data.Extents, data.MassKg);
                // Resolve/cook each lazy collider in its own settled preparation frame.
                // These native global shape pools must never be touched by the CPU worker.
                foreach (var component in template.Components.OfType<ColliderModule.Template>())
                    foreach (var collider in component.Colliders)
                    {
                        _step = "native collider preparation";
                        collider.Resolve();
                        if (!collider.GetShape().Exists) throw new InvalidOperationException("Prepared fragment collider could not be cooked.");
                        yield return 1;
                        if (_cancelled) break;
                    }
                if (_cancelled) continue;
                _step = "native fragment construction";
                _nativePreparing = new(prepared);
                yield return 1;
                if (_cancelled)
                {
                    _nativePreparing.Dispose(); _nativePreparing = null;
                    yield return 1;
                    continue;
                }
                _step = "native fragment tree preparation";
                _nativePreparing.PrepareTree();
                yield return 1;
                prepared = prepared with { NativeReady = true };
                Prepared[data.Id] = prepared;
                Fragments.Add(new(prepared, data.Frame, data.PositiveSide, _nativePreparing));
                _nativePreparing = null;
                _registering = null;
                yield return 0;
            }
        }
        internal void Cancel() { _cancelled = true; _growthDemand?.Cancel(); }
        // A requested growth pauses iteration before another batch is recorded. Every
        // extant private pool was submitted immediately; zero-timeout polling drains
        // it without invoking Dispose's submit path on any unrecorded work.
        internal bool TryDrainForGrowth()
        {
            if (_pending == null) return true;
            if (!_pending.WaitFor(0)) return false;
            _pending.Dispose(); _pending = null;
            return true;
        }
        // Called only at shutdown or after Done. Normal cancellation drains preparation in
        // the background of subsequent frames so partially registered IDs remain reusable.
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _growthDemand?.Cancel();
            try
            {
                _steps.Dispose();
                // Native StagingPool.Dispose waits for submitted copies before returning.
                // Never retire an unpublished range before that ownership boundary.
                _pending?.Dispose(); _pending = null;
                if (_allocation != null) { _arena!.RetireUnpublished(_allocation); _allocation = null; }
                _nativePreparing?.Dispose(); _nativePreparing = null;
                foreach (var fragment in Fragments) fragment.Native.Dispose();
            }
            finally { _activeUploadJobs--; UploadJobs.Remove(this); }
        }
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
