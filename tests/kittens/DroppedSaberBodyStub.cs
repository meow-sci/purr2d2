using Brutal.Numerics;
using KSA;

namespace Purr2D2.Runtime;

/// <summary>Native integration boundary; production Drop.cs owns every sequence and lifecycle operation.</summary>
internal sealed class DroppedSaberBody : IDisposable
{
    internal Vehicle Vehicle { get; } = new("Dropped native hilt");
    internal bool Disposed;
    internal float SpinMultiplier;
    private double4x4 _frame;
    internal static bool TryCreate(KittenEva owner, double4x4 frame, float spinMultiplier,
        out DroppedSaberBody? result, out string reason)
    {
        result = new() { _frame = frame * double4x4.CreateTranslation(owner.GetPositionEcl()), SpinMultiplier = spinMultiplier };
        reason = ""; return true;
    }
    internal bool TryFrame(out double4x4 frame) { frame = _frame; return !Disposed; }
    internal void MoveTo(double4x4 frame) => _frame = frame;
    public void Dispose() { Disposed = true; Vehicle.IsDisposed = true; }
}
