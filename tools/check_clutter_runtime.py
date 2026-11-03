#!/usr/bin/env python3
"""Run the production clutter slice orchestrator against controlled scene boundaries."""
import sys

from validate import DEFAULT_SOURCE, _run_managed_project


if __name__ == '__main__':
    required = [DEFAULT_SOURCE / path for path in (
        'src/Core/BackgroundSliceWork.cs', 'src/Slicing/ClutterSlicer.cs',
        'src/Slicing/ClutterSliceQueue.cs', 'src/Slicing/SliceWorkerPool.cs',
        'tests/trees/ClutterSlicerHostStubs.cs', 'tests/trees/ClutterSlicerChecks.cs')]
    code, output = _run_managed_project(DEFAULT_SOURCE, 'ClutterSlicerChecks', required,
        'internal static class Program { static void Main() => ClutterSlicerChecks.Run(); }')
    print(output)
    sys.exit(code)
