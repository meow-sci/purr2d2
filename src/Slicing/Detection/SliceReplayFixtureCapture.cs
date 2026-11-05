using System.Diagnostics;
using System.Text.Json;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Detection;

/// <summary>
/// One-shot developer capture of a confirmed mesh interval. Live source arrays and
/// validity callbacks stay on the settled thread; only a copied CutTriangle array
/// is handed to the background JSON writer.
/// </summary>
internal sealed class SliceReplayFixtureCapture<T>
{
    internal enum CaptureState { Idle, Armed, Copying, Writing, Complete, Failed }

    internal const int MaximumTriangles = 40_000;
    internal const int MaximumPrimitives = 512;
    internal const int PrimitiveBudgetPerAdvance = 64;
    internal const int TriangleBudgetPerAdvance = 2_048;
    internal const double MillisecondBudgetPerAdvance = .25;
    internal const long MaximumFixtureBytes = 16L * 1024 * 1024;

    private CaptureState _state;
    private string _status = "Replay fixture capture is off.";
    private string? _path;
    private string _detector = "unknown";
    private bool _fullMeshScan;
    private double _hitTime, _along;
    private BladeSweep _sweep;
    private CachedRibbonCapture<T>? _copy;
    private Func<bool>? _sourceCurrent;
    private Task? _writer;

    internal CaptureState State { get { RefreshWriter(); return _state; } }
    internal string Status { get { RefreshWriter(); return _status; } }
    internal string? OutputPath => _path;

    internal void RejectArmedContact(string reason)
    { if (_state == CaptureState.Armed) Fail(reason); }

    internal bool Arm(string outputPath)
    {
        RefreshWriter();
        if (_state is CaptureState.Armed or CaptureState.Copying or CaptureState.Writing) return false;
        if (string.IsNullOrWhiteSpace(outputPath)) return Fail("Replay fixture output path is empty.");
        try { _path = Path.GetFullPath(outputPath); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        { return Fail("Replay fixture path is invalid: " + error.Message); }
        _copy = null; _sourceCurrent = null; _writer = null;
        _status = "Armed for the next confirmed vessel mesh contact.";
        _state = CaptureState.Armed;
        return true;
    }

    /// <summary>Offer exactly one already-confirmed hit. False means capture was not armed.</summary>
    internal bool Offer(T[][] primitives, Func<T, CutPoint> point, BladeSweep sweep,
        double hitTime, double along, bool fullMeshScan, string detector, Func<bool> sourceCurrent)
    {
        if (_state != CaptureState.Armed) return false;
        ArgumentNullException.ThrowIfNull(point);
        ArgumentNullException.ThrowIfNull(sourceCurrent);
        if (primitives == null || primitives.Length == 0 || primitives.Length > MaximumPrimitives
            || !sweep.TrySplit(new(16))
            || !double.IsFinite(hitTime) || hitTime is < 0 or > 1
            || !double.IsFinite(along) || along is < 0 or > 1 || !sourceCurrent())
            return Fail("Replay fixture contact or source evidence is invalid.");
        long triangles = 0;
        try
        {
            foreach (var primitive in primitives)
            {
                triangles = checked(triangles + (primitive?.Length ?? 0) / 3);
                if (triangles > MaximumTriangles) return Fail($"Replay fixture exceeds the {MaximumTriangles:N0}-triangle capture limit.");
            }
        }
        catch (OverflowException) { return Fail("Replay fixture triangle count overflowed its capture limit."); }
        if (triangles == 0) return Fail("Replay fixture has no complete mesh triangles.");
        detector = detector switch { "cached-ribbon" => "cached-ribbon", "legacy-ribbon" => "legacy-ribbon", _ => "unknown" };
        _detector = detector; _fullMeshScan = fullMeshScan; _hitTime = hitTime; _along = along; _sweep = sweep;
        _sourceCurrent = sourceCurrent;
        _copy = new CachedRibbonCapture<T>(primitives, point);
        _status = $"Copying {triangles:N0} triangles from the confirmed hit mesh.";
        _state = CaptureState.Copying;
        return true;
    }

    /// <summary>Call once per settled update while copying; source data is never read on a worker.</summary>
    internal void Advance(int primitiveBudget = PrimitiveBudgetPerAdvance,
        int triangleBudget = TriangleBudgetPerAdvance, double millisecondBudget = MillisecondBudgetPerAdvance)
    {
        RefreshWriter();
        if (_state != CaptureState.Copying || _copy == null || _sourceCurrent == null) return;
        if (!double.IsFinite(millisecondBudget) || millisecondBudget <= 0)
        { Fail("Replay fixture copy budget is invalid."); return; }
        long started = Stopwatch.GetTimestamp();
        bool TimeAvailable() => Stopwatch.GetElapsedTime(started).TotalMilliseconds < millisecondBudget;
        CachedRibbonCapture<T>.Progress progress;
        try { progress = _copy.Advance(primitiveBudget, triangleBudget, _sourceCurrent, TimeAvailable); }
        catch (Exception error)
        { Fail("Replay fixture copy failed: " + error.GetBaseException().Message); return; }
        if (progress.Status == CachedRibbonCapture<T>.State.Pending)
        {
            _status = $"Copying replay mesh: {progress.TrianglesCopied:N0} triangles copied this update.";
            return;
        }
        if (progress.Status != CachedRibbonCapture<T>.State.Complete)
        { Fail(progress.Status == CachedRibbonCapture<T>.State.TooLarge
            ? "Replay fixture exceeds the copied mesh memory limit."
            : "Replay fixture source changed during capture."); return; }
        CutTriangle[] snapshot;
        try { snapshot = _copy.Take(); }
        catch (Exception error) { Fail("Replay fixture snapshot failed: " + error.Message); return; }
        if (!_sourceCurrent()) { Fail("Replay fixture source changed before file writing."); return; }
        _copy = null; _sourceCurrent = null;
        var path = _path!;
        var sweep = _sweep; var detector = _detector; var fullMeshScan = _fullMeshScan;
        var hitTime = _hitTime; var along = _along;
        _status = "Writing copied replay fixture.";
        _state = CaptureState.Writing;
        _writer = Task.Run(() => WriteFixture(path, snapshot, sweep, detector, fullMeshScan, hitTime, along));
    }

    private void RefreshWriter()
    {
        if (_state != CaptureState.Writing || _writer is not { IsCompleted: true } writer) return;
        if (writer.IsFaulted) Fail("Replay fixture write failed: " + writer.Exception!.GetBaseException().Message);
        else if (writer.IsCanceled) Fail("Replay fixture write was cancelled.");
        else { _state = CaptureState.Complete; _status = "Replay fixture saved."; }
        _writer = null;
    }

    private bool Fail(string reason)
    { _copy = null; _sourceCurrent = null; _state = CaptureState.Failed; _status = reason; return false; }

    private static void WriteFixture(string path, CutTriangle[] triangles, BladeSweep sweep,
        string detector, bool fullMeshScan, double hitTime, double along)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new IOException("Output directory is missing.");
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.SequentialScan))
            using (var bounded = new BoundedWriteStream(file, MaximumFixtureBytes))
            using (var json = new Utf8JsonWriter(bounded))
            {
                json.WriteStartObject();
                json.WriteString("source", "runtime-copied-mesh");
                json.WriteString("frame", "hit-part-local");
                json.WriteString("detector", detector);
                json.WriteBoolean("fullMeshScan", fullMeshScan);
                json.WriteNumber("hitTime01", hitTime);
                json.WriteNumber("alongBlade01", along);
                json.WriteStartArray("triangles");
                foreach (var triangle in triangles)
                {
                    json.WriteStartArray(); WritePoint(json, triangle.A); WritePoint(json, triangle.B); WritePoint(json, triangle.C); json.WriteEndArray();
                }
                json.WriteEndArray();
                json.WriteStartArray("intervals"); json.WriteStartObject();
                WritePointProperty(json, "base0", sweep.Base0); WritePointProperty(json, "tip0", sweep.Tip0);
                WritePointProperty(json, "base1", sweep.Base1); WritePointProperty(json, "tip1", sweep.Tip1);
                json.WriteNumber("tolerance", sweep.Tolerance);
                json.WriteEndObject(); json.WriteEndArray(); json.WriteEndObject(); json.Flush();
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static void WritePointProperty(Utf8JsonWriter json, string name, CutPoint point)
    { json.WritePropertyName(name); WritePoint(json, point); }
    private static void WritePoint(Utf8JsonWriter json, CutPoint point)
    { json.WriteStartArray(); json.WriteNumberValue(point.X); json.WriteNumberValue(point.Y); json.WriteNumberValue(point.Z); json.WriteEndArray(); }

    private sealed class BoundedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long _written;
        private void Check(int count)
        {
            if (count < 0 || _written > maximumBytes - count)
                throw new InvalidDataException($"Replay fixture exceeds the {maximumBytes / (1024 * 1024)} MiB output limit.");
            _written += count;
        }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
        public override void Flush() => inner.Flush();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
