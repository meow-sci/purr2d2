using System.Runtime.CompilerServices;
using Purr2D2.Cutting;

namespace Purr2D2.Slicing.Detection;

// Settled-thread-only copy from a verified immutable mesh. Source validity
// must be checked by the caller on every Advance and before worker submission.
// The result contains no source arrays, game objects, transforms or delegates.
internal sealed class CachedRibbonCapture<T>
{
    internal enum State { Pending, Complete, Invalid, TooLarge }
    internal readonly record struct Progress(State Status, int PrimitiveReads, int TrianglesCopied);
    private readonly T[][] _primitives;
    private readonly Func<T, CutPoint> _point;
    private int _primitive, _vertex, _count, _written;
    private bool _copying;
    private CutTriangle[]? _result;
    private State _status = State.Pending;

    internal CachedRibbonCapture(T[][] primitives, Func<T, CutPoint> point)
    { _primitives = primitives ?? throw new ArgumentNullException(nameof(primitives)); _point = point ?? throw new ArgumentNullException(nameof(point)); }

    internal Progress Advance(int primitiveBudget, int triangleBudget, Func<bool> sourceCurrent, Func<bool>? timeAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(sourceCurrent);
        primitiveBudget = Math.Max(0, primitiveBudget); triangleBudget = Math.Max(0, triangleBudget);
        int reads = 0, copied = 0;
        if (_status != State.Pending) return new(_status, 0, 0);
        if (!sourceCurrent()) { _status = State.Invalid; return new(_status, 0, 0); }
        while (_status == State.Pending && (timeAvailable?.Invoke() ?? true))
        {
            if (!_copying)
            {
                if (_primitive == _primitives.Length)
                {
                    // Conservative upper bound on actual cache arrays, including
                    // indices and at most two 64-byte nodes per triangle.
                    const int nodeUpperBytes = 128;
                    if ((long)_count * (Unsafe.SizeOf<CutTriangle>() + sizeof(int) + nodeUpperBytes) > CachedRibbonGeometry.MaximumBytes)
                    { _status = State.TooLarge; break; }
                    _result = new CutTriangle[_count]; _primitive = 0; _copying = true;
                    continue;
                }
                if (reads == primitiveBudget) break;
                int vertices = _primitives[_primitive++]?.Length ?? 0; reads++;
                long total = (long)_count + vertices / 3;
                if (total > int.MaxValue || total * (Unsafe.SizeOf<CutTriangle>() + sizeof(int) + 128L) > CachedRibbonGeometry.MaximumBytes)
                { _status = State.TooLarge; break; }
                _count = (int)total;
                continue;
            }
            if (_primitive == _primitives.Length) { _status = State.Complete; break; }
            if (_vertex == 0)
            {
                if (reads == primitiveBudget) break;
                reads++;
            }
            var primitive = _primitives[_primitive];
            if (primitive == null || _vertex + 2 >= primitive.Length)
            { _primitive++; _vertex = 0; continue; }
            if (copied == triangleBudget) break;
            var triangle = new CutTriangle(_point(primitive[_vertex]), _point(primitive[_vertex + 1]), _point(primitive[_vertex + 2]));
            if (!triangle.A.Finite || !triangle.B.Finite || !triangle.C.Finite || _written >= _result!.Length)
            { _status = State.Invalid; _result = null; break; }
            _result[_written++] = triangle; _vertex += 3; copied++;
            if (_vertex + 2 >= primitive.Length) { _primitive++; _vertex = 0; }
        }
        if (_status == State.Complete && (_written != _count || !sourceCurrent())) { _status = State.Invalid; _result = null; }
        return new(_status, reads, copied);
    }
    internal CutTriangle[] Take()
    {
        if (_status != State.Complete || _result == null) throw new InvalidOperationException("Capture is not complete.");
        var result = _result; _result = null; return result;
    }
}
