using Brutal.Numerics;
using KSA;
using Purr2D2.Slicing;

namespace Purr2D2.Cutting;

/// <summary>Assisted selection and confirmed manual-surface target policy.</summary>
internal sealed partial class PartSliceTargeting
{
    private const int MaxCandidateSubparts = 32_768;
    private const int MaxCachedTriangles = 4_000_000;
    private KittenEva? _wielder;
    internal bool BudgetLimited { get; private set; }
    internal int SlicedParts { get; private set; }
    internal Func<Part, SlicePlane, SliceAttempt>? SliceHandler { get; set; }
    internal string Status { get; private set; } = "Ready";
    internal void Reset()
    {
        SlicedParts = 0; _wielder = null; BudgetLimited = false; Status = "Ready";
    }

    private bool CanCut(Part full, out string reason) => CanCut(full, _wielder, out reason);
    private static bool CanCut(Part full, KittenEva? wielder, out string reason)
    {
        var owner = full.Tree?.OwningVehicle;
        reason = "The source part is unavailable.";
        if (owner == null || owner.IsDisposed || owner is KittenEva) return false;
        if (!owner.HasPhysicsBubble) reason = "Slicing requires a physics bubble";
        else if (!ReferenceEquals(owner.Parent, wielder?.Parent)) reason = "Slicing requires the same parent body";
        else if (owner.BubbleVehicleCount >= NativeSlicePhysics.VehicleLimit)
            reason = $"Physics vehicle limit reached ({NativeSlicePhysics.VehicleLimit} local vehicles). Raise the limit or remove generated debris in Settings.";
        else if (full.IsAttachedInternal) reason = "Internal parts cannot be selected independently";
        else { reason = ""; return true; }
        return false;
    }

    // Old durable fragment templates placed their complete pick mesh on the root.
    // Native Part.RayCastEgo visits only MeshView modules on SubParts. Repair each
    // instance during its constructor, before any PartTree owns the module lists.
    internal static bool RepairLegacyFragmentPicker(Part full)
    {
        if (full.IsSubPart || full.Tree != null
            || !full.Template.Id.StartsWith("Purr2D2_Fragment_", StringComparison.Ordinal)) return false;
        var rootViews = full.Modules.Get<MeshViewModule>();
        if (rootViews.IsEmpty) return false;
        var mesh = rootViews[0].MeshView;
        foreach (var sub in full.SubParts)
            foreach (var existing in sub.Modules.Get<MeshViewModule>())
                if (ReferenceEquals(existing.MeshView, mesh)) return false;
        foreach (var sub in full.SubParts)
        {
            // The complete hull is in root coordinates. Only an identity visual
            // child can host it without changing the native ray or physical shape.
            if (!sub.MatrixAsmb2ParentAsmb.Equals(double4x4.Identity)
                || !sub.Modules.Get<MeshViewModule>().IsEmpty
                || !(sub.Modules.HasAny<PartModelModule>() || sub.Modules.HasAny<PartModelDynamicModule>()
                    || sub.Modules.HasAny<PartModelGlassModule>())) continue;
            var picker = new MeshViewModule(full.Template.Id + "_legacy_pick", mesh) { Parent = sub };
            // A childless subpart's SubtreeModules aliases its Modules; the native helper
            // registers the module once with the subpart and once with the full part.
            picker.AddNewTo(sub);
            sub.UpdateBounds();
            full.UpdateBounds();
            return true;
        }
        return false;
    }
    private static CutPoint ToPoint(double3 value) => new(value.X, value.Y, value.Z);
    private static double3 ToGame(CutPoint value) => new(value.X, value.Y, value.Z);
    private static CutPoint Transform(CutPoint value, double4x4 matrix) => ToPoint(ToGame(value).Transform(matrix));
}
