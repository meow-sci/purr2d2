using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;

namespace KSA
{
    internal sealed class PbrMaterialReference
    {
        internal PbrMaterialReference Get() => this;
    }
}

namespace Purr2D2.Force
{
    internal enum BubbleFrame { Ccf }
    internal readonly record struct BubbleOrigin(double Time, BubbleFrame BubFrame);
    internal sealed record OwnerBubbleSnapshot(BubbleOrigin Origin);

    internal static class ClutterForceNative
    {
        internal static OwnerBubbleSnapshot? OwnerBubble(KittenEva owner) => new(new(0, BubbleFrame.Ccf));
    }

    internal static class ClutterCoordinateExtensions
    {
        internal static doubleQuat GetCci2Ccf(this object parent, double time) => doubleQuat.Identity;
        internal static doubleQuat GetBody2Cci(this Vehicle owner) => doubleQuat.Identity;
        internal static double3 GetPositionCci(this Vehicle owner) => owner.PositionEgo;
    }
}

namespace Purr2D2.Slicing
{
    internal sealed record IsolatedSliceSource(Part Source, Vehicle Owner, double4x4 PartTransform,
        double4x4 SourceToIsolated)
    {
        internal Vehicle[] Neighbors { get; init; } = [];
    }

    // ManualContactRuntimeChecks drives the production vessel coordinator while
    // controlling the separate clutter lane at the settled handoff boundary.
    internal sealed class ClutterSliceQueue
    {
        internal TaskScheduler Scheduler { get; } = TaskScheduler.Default;
        internal TaskScheduler LoaderScheduler { get; } = TaskScheduler.Default;
        internal int WorkerCount { get; } = 2;
        internal bool Active { get; set; }
        internal bool HasUpload { get; set; }
        internal int LoadSlots { get; set; }
        internal int Count { get; set; }
        internal int ReservedVehicles { get; set; }
        internal int ReservationCount { get; set; }
        internal int OtherCount { get; set; }
        internal long ChargedBytes { get; set; }
    }

    internal static class SliceSourceCapture
    {
        internal static List<(MeshReference? Mesh, PbrMaterialReference? Material, bool Glass)> Models(Part part) =>
            part.Modules.Get<MeshViewModule>().ToArray()
                .Select(module => ((MeshReference?)module.MeshView, (PbrMaterialReference?)null, false)).ToList();

        internal static IEnumerable<(MeshReference? Mesh, PbrMaterialReference? Material, bool Glass)> EnumerateModels(Part part) =>
            Models(part);
    }
}
