using Brutal.Numerics;
using KSA;

namespace Purr2D2.Runtime;

/// <summary>Native integration boundary; production Drop.cs owns every sequence and lifecycle operation.</summary>
internal sealed class DroppedSaberBody : IDisposable
{
    internal Vehicle Vehicle { get; } = new("Dropped native hilt");
    internal bool Disposed;
    private double4x4 _frame;
    internal static bool TryCreate(KittenEva owner, double4x4 frame, out DroppedSaberBody? result, out string reason)
    {
        result = new() { _frame = frame * double4x4.CreateTranslation(owner.GetPositionEcl()) };
        reason = ""; return true;
    }
    internal bool TryFrame(out double4x4 frame) { frame = _frame; return !Disposed; }
    public void Dispose() { Disposed = true; Vehicle.IsDisposed = true; }
}
