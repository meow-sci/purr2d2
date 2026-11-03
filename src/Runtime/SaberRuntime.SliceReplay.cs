using Brutal.Numerics;
using Purr2D2.Cutting;
using Purr2D2.Rendering;
using Purr2D2.Slicing.Detection;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private readonly SliceReplayFixtureCapture<double3> _sliceReplayCapture = new();

    private bool ArmSliceReplayCapture()
    {
        string directory = Path.GetDirectoryName(_settingsPath) ?? AppContext.BaseDirectory;
        return _sliceReplayCapture.Arm(Path.Combine(directory, "slice-replay-capture.json"));
    }

    private void AdvanceSliceReplayCapture()
    {
        if (_sliceReplayCapture.State == SliceReplayFixtureCapture<double3>.CaptureState.Copying)
            _sliceReplayCapture.Advance();
    }

    private void OfferSliceReplayContact(ManualContactProbe.Contact contact, string detector)
    {
        if (_sliceReplayCapture.State != SliceReplayFixtureCapture<double3>.CaptureState.Armed) return;
        if (contact.HitPrimitives is not { } primitives)
        { _sliceReplayCapture.RejectArmedContact("Replay fixture contact has no copied hit-mesh primitives."); return; }
        var sweep = new BladeSweep(ReplayPoint(contact.PreviousBaseLocal), ReplayPoint(contact.PreviousTipLocal),
            ReplayPoint(contact.CurrentBaseLocal), ReplayPoint(contact.CurrentTipLocal), contact.SweepTolerance);
        _sliceReplayCapture.Offer(primitives, static point => new(point.X, point.Y, point.Z), sweep,
            contact.HitTime01, contact.AlongBlade01, contact.FullMeshScan, detector,
            () => _manualProbe.IsEvidenceCurrent(contact));
    }

    private static CutPoint ReplayPoint(double3 value) => new(value.X, value.Y, value.Z);
}
