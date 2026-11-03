using Brutal.Numerics;
using HarmonyLib;
using KSA;
using Purr2D2.Force;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    private Harmony? _coverageHarmony;
    private KittenEva? _coverageOwner;
    private Celestial? _coverageParent;
    private ClutterEcotypePhysicalData[]? _coverageEcotypes;
    private long _coverageEpoch;
    private bool _coverageAttempted, _coverageHasPrevious;
    private double3 _coveragePreviousBase, _coveragePreviousTip;
    private double _coveragePreviousTime, _coverageQuietSince = double.NaN;
    private string _coverageDiagnostic = "Clutter coverage unavailable.";

    // The same joined handoff that publishes contact poses also publishes interest.
    // The hook capability remains unavailable until its native acceptance is complete.
    private void UpdateClutterCoverage()
    {
        if (!_settings.SliceClutterReachExtension)
        {
            if (_coverageAttempted)
            {
                ResetClutterCoverage();
                _coverageAttempted = false;
            }
            return;
        }
        if (!_coverageAttempted && _coverageHarmony != null)
        {
            _coverageAttempted = true;
            SliceClutterCoverageHooks.Install(_coverageHarmony, out _coverageDiagnostic);
        }
        if (!SliceClutterCoverageHooks.Installed) return;
        if (_owner is not { IsDisposed: false } owner || owner.Parent is not Celestial celestial)
        { ResetClutterCoverage(); return; }
        var bubble = ClutterForceNative.OwnerBubble(owner);
        var renderer = Program.GetPlanetRenderer()?.GroundClutterRenderer;
        if (bubble == null || bubble.Origin.BubFrame != BubbleFrame.Ccf || renderer == null
            || !renderer.PlanetPhysicalData.TryGetValue(celestial.Hash, out var ecotypes) || ecotypes == null)
        { ResetClutterCoverage(); _coverageDiagnostic = "Clutter coverage unavailable."; return; }
        if (ecotypes.Length > SliceClutterCoverageHooks.MaximumCandidates)
        { ResetClutterCoverage(); SliceClutterCoverageHooks.MarkLimited(); _coverageDiagnostic = "Clutter coverage limited or unknown."; return; }
        if (!ReferenceEquals(_coverageOwner, owner) || !ReferenceEquals(_coverageParent, celestial)
            || !SliceClutterCoverageHooks.SameEcotypes(_coverageEcotypes, ecotypes))
        {
            ResetClutterCoverage();
            _coverageOwner = owner; _coverageParent = celestial;
            _coverageEcotypes = (ClutterEcotypePhysicalData[])ecotypes.Clone();
        }
        var cciToCcf = celestial.GetCci2Ccf(bubble.Origin.Time);
        var ownerCci = owner.GetPositionCci();
        var ownerCcf = ownerCci.Transform(cciToCcf);
        var reach = new SliceClutterCoverageRadius(ownerCcf);
        bool bladeActive = _blade.Equipped && _blade.TargetOn && _blade.Deployment > .001f && _pose.Eligible;
        double3 bodyBase = default, bodyTip = default;
        bool hasSegment = bladeActive && _pose.TryGetBladeSegment(Length, out bodyBase, out bodyTip);
        if (hasSegment)
        {
            var bodyToCci = owner.GetBody2Cci();
            var bladeBase = (ownerCci + bodyBase.Transform(bodyToCci)).Transform(cciToCcf);
            var bladeTip = (ownerCci + bodyTip.Transform(bodyToCci)).Transform(cciToCcf);
            if (!reach.Include(bladeBase) || !reach.Include(bladeTip))
            { ResetClutterCoverage(); _coverageDiagnostic = "Clutter coverage unknown."; return; }
            if (_coverageHasPrevious && _playerTime >= _coveragePreviousTime && _playerTime - _coveragePreviousTime <= .2
                && (!reach.Include(_coveragePreviousBase) || !reach.Include(_coveragePreviousTip)))
            { ResetClutterCoverage(); _coverageDiagnostic = "Clutter coverage unknown."; return; }
            _coveragePreviousBase = bladeBase; _coveragePreviousTip = bladeTip;
            _coveragePreviousTime = _playerTime; _coverageHasPrevious = true;
            _coverageQuietSince = double.NaN;
        }
        else
        {
            bool pending = _clutterSlicer.CoverageTarget != null || _clutterQueue?.CoverageTargets.Any() == true;
            if (pending) _coverageQuietSince = double.NaN;
            else if (!bladeActive)
            {
                if (!double.IsFinite(_coverageQuietSince)) _coverageQuietSince = _playerTime;
                if (_playerTime - _coverageQuietSince > .5)
                { ResetClutterCoverage(); _coverageDiagnostic = "Clutter coverage quiet."; return; }
            }
            // A temporarily stale pose does not evict a warmed region while the
            // blade remains held and lit. Pending accepted targets pin it until
            // their source validation and commit have finished.
            if (_coverageHasPrevious
                && (!reach.Include(_coveragePreviousBase) || !reach.Include(_coveragePreviousTip)))
            { ResetClutterCoverage(); _coverageDiagnostic = "Clutter coverage unknown."; return; }
            if (!_coverageHasPrevious && !pending)
            { _coverageDiagnostic = "Waiting for an available blade pose."; return; }
        }
        if (_clutterSlicer.CoverageTarget is { } serial && !IncludePending(serial, celestial, ecotypes, ref reach))
        { SliceClutterCoverageHooks.MarkLimited(); _coverageDiagnostic = "Clutter coverage limited or unknown."; return; }
        if (_clutterQueue != null)
            foreach (var target in _clutterQueue.CoverageTargets)
                if (!IncludePending(target, celestial, ecotypes, ref reach))
                { SliceClutterCoverageHooks.MarkLimited(); _coverageDiagnostic = "Clutter coverage limited or unknown."; return; }
        var ccePosition = owner.GetPositionCce();
        double length = ccePosition.Length();
        if (!double.IsFinite(length) || length <= 0)
        { ResetClutterCoverage(); _coverageDiagnostic = "Clutter coverage unknown."; return; }
        var direction = (ccePosition / length).Transform(celestial.GetCce2Ccf());
        SliceClutterCoverageHooks.TryPublish(owner, _coverageEpoch, direction, ecotypes, reach.Reach, out _coverageDiagnostic);
    }

    private static bool IncludePending(ClutterForceTarget target, Celestial celestial,
        ClutterEcotypePhysicalData[] ecotypes, ref SliceClutterCoverageRadius reach)
    {
        if (!target.NativeAlive || !ReferenceEquals(target.Parent, celestial)
            || (uint)target.Key.EcotypeIndex >= (uint)ecotypes.Length) return false;
        var grid = ecotypes[target.Key.EcotypeIndex].VesselGrid;
        var center = grid.GetCellBoundingSphere(target.Key.Cell, out double cellRadius);
        return reach.Include(center, cellRadius + grid.InstanceReach);
    }

    private void ResetClutterCoverage()
    {
        SliceClutterCoverageHooks.Clear();
        unchecked { _coverageEpoch++; }
        _coverageOwner = null; _coverageParent = null; _coverageEcotypes = null;
        _coverageHasPrevious = false; _coverageQuietSince = double.NaN;
    }

}
