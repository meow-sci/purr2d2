#!/usr/bin/env python3
"""Run production manual-contact runtime and vessel queue checks in host fixtures."""
import sys

from validate import DEFAULT_SOURCE, _run_managed_project


if __name__ == '__main__':
    base = [DEFAULT_SOURCE / path for path in (
        'src/Cutting/PartSliceTargeting.cs', 'src/Cutting/PartSliceTargeting.Selection.cs',
        'src/Cutting/CutGeometry.cs', 'src/Cutting/PlaneSlicer.cs',
        'src/Cutting/ClosedMeshPreparation.cs', 'src/Cutting/AssistedSliceGeometry.cs',
        'src/Cutting/ContactSliceGeometry.cs', 'tests/contact/GameStubs.cs',
        'src/Rendering/ManualContactProbe.cs', 'src/Cutting/BladeSweep.cs',
        'src/Slicing/Detection/CachedRibbonGeometry.cs', 'src/Slicing/Detection/CachedRibbonCapture.cs',
        'src/Slicing/Detection/CachedRibbonStore.cs', 'src/Core/ManualContactGesture.cs',
        'src/Slicing/Detection/SliceActivationPolicy.cs')]
    required = [path for path in base if path.name not in (
        'TargetSelectionChecks.cs', 'ContactEffectsChecks.cs', 'ManualContactChecks.cs', 'SaberContactProbe.cs')]
    required += [DEFAULT_SOURCE / path for path in (
        'src/Slicing/Planes/ISlicePlaneStrategy.cs', 'src/Slicing/Planes/SliceSweepSamples.cs',
        'src/Slicing/SliceSourceFrame.cs', 'src/Slicing/SliceStrokeProvenance.cs',
        'src/Slicing/VesselSliceQueue.cs', 'src/Core/SaberOverlayDrag.cs',
        'src/Core/ManualSwingSwooshGate.cs', 'src/Core/AssistedSliceSequence.cs',
        'src/Core/SaberSwing.cs', 'src/Runtime/SaberRuntime.ManualContact.cs',
        'src/Runtime/SaberRuntime.OverlaySwing.cs', 'tests/contact/ManualRuntimeChecks.cs',
        'tests/contact/ManualRuntimeHostStubs.cs', 'tests/contact/ManualRuntimeQueueHostStubs.cs')]
    code, output = _run_managed_project(DEFAULT_SOURCE, 'ManualContactRuntimeChecks', required,
        'internal static class ManualContactRuntimeEntry { static void Main() { Purr2D2.Runtime.SaberRuntime.Run(); } }')
    print(output)
    sys.exit(code)
