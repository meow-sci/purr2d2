using Brutal.Numerics;
using Purr2D2.Core;

namespace Purr2D2.Pose;

/// <summary>Pure rig validation and row-vector pose geometry; never writes a physical body.</summary>
internal static class SomersaultPoseMath
{
    internal readonly record struct Leg(int Hip, int Knee, int Ankle);
    internal readonly record struct TuckRig(Leg Left, Leg Right, int Spine, float3 Across, float3 Up, float3 Forward);

    internal static bool Finite(float3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    internal static bool TryRoots(ReadOnlySpan<int> parents, out int[] roots)
    {
        var found = new List<int>();
        for (int i = 0; i < parents.Length; i++)
        {
            if (parents[i] < 0) found.Add(i);
            int node = i, steps = 0;
            while (node >= 0)
            {
                if (node >= parents.Length || ++steps > parents.Length) { roots = []; return false; }
                node = parents[node];
            }
        }
        roots = found.ToArray();
        return roots.Length > 0;
    }

    internal static bool TryPivot(float scale, float3 centerOfMass, out float3 pivot)
    {
        pivot = float3.Zero;
        if (!float.IsFinite(scale) || scale <= 1e-6f || !Finite(centerOfMass)
            || !double4x4.Invert(SaberPoseMath.ModelToBody(scale, centerOfMass), out double4x4 bodyToModel)) return false;
        pivot = float3.Pack(bodyToModel.Translation);
        return Finite(pivot);
    }

    internal static bool PivotNearRig(float3 pivot, ReadOnlySpan<float4x4> restWorld)
    {
        if (!Finite(pivot) || restWorld.Length == 0) return false;
        float3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (float4x4 bone in restWorld)
        {
            if (!Finite(bone.Translation)) return false;
            min = float3.Min(min, bone.Translation);
            max = float3.Max(max, bone.Translation);
        }
        float extent = (max - min).Length();
        float3 nearest = new(Math.Clamp(pivot.X, min.X, max.X), Math.Clamp(pivot.Y, min.Y, max.Y), Math.Clamp(pivot.Z, min.Z, max.Z));
        return float.IsFinite(extent) && extent > .1f && (pivot - nearest).Length() <= extent * .5f;
    }

    internal static float3 Axis(SomersaultDirection direction) => direction switch
    {
        // Model +X points left, +Y up, +Z forward. Head first moves towards the named direction.
        SomersaultDirection.Forward => float3.UnitX,
        SomersaultDirection.Backward => -float3.UnitX,
        SomersaultDirection.Left => -float3.UnitZ,
        SomersaultDirection.Right => float3.UnitZ,
        _ => float3.Zero,
    };

    internal static float4x4 Rotation(SomersaultDirection direction, float radians, float3 pivot) =>
        float4x4.CreateTranslation(-pivot)
        * float4x4.CreateFromQuaternion(floatQuat.CreateFromAxisAngle(Axis(direction), radians))
        * float4x4.CreateTranslation(pivot);

    internal static bool TryBuildWorld(ReadOnlySpan<int> parents, ReadOnlySpan<float4x4> local,
        Span<float4x4> world)
    {
        if (parents.Length != local.Length || local.Length != world.Length) return false;
        // No dependence on an importer-specific bone ordering, no recursion or per-frame allocations.
        for (int i = 0; i < local.Length; i++)
        {
            float4x4 matrix = local[i];
            int parent = parents[i], steps = 0;
            while (parent >= 0)
            {
                if (parent >= local.Length || ++steps > local.Length) return false;
                matrix *= local[parent];
                parent = parents[parent];
            }
            if (!Finite(matrix.Translation) || !Finite(matrix.X.XYZ) || !Finite(matrix.Y.XYZ)
                || !Finite(matrix.Z.XYZ)) return false;
            world[i] = matrix;
        }
        return true;
    }

    internal static bool TryResolveTuck(IReadOnlyList<string>? names, ReadOnlySpan<int> parents,
        ReadOnlySpan<float4x4> restWorld, out TuckRig rig)
    {
        rig = default;
        if (names is null || names.Count != parents.Length || restWorld.Length != parents.Length) return false;
        // These are candidates, not a promise about the proprietary kitten rig. Names alone never
        // enable tuck: both chains, rest geometry and the anatomical frame must agree below.
        Leg left = new(Find(names, "Hip_L", "Thigh_L", "UpperLeg_L"),
            Find(names, "Knee_L", "Calf_L", "LowerLeg_L"), Find(names, "Ankle_L", "Foot_L"));
        Leg right = new(Find(names, "Hip_R", "Thigh_R", "UpperLeg_R"),
            Find(names, "Knee_R", "Calf_R", "LowerLeg_R"), Find(names, "Ankle_R", "Foot_R"));
        if (!Chain(left, parents) || !Chain(right, parents)) return false;
        int[] legBones = [left.Hip, left.Knee, left.Ankle, right.Hip, right.Knee, right.Ankle];
        if (legBones.Distinct().Count() != 6 || Descendant(left.Hip, right.Hip, parents)
            || Descendant(right.Hip, left.Hip, parents)) return false;
        float3 pelvis = (restWorld[left.Hip].Translation + restWorld[right.Hip].Translation) * .5f;
        float3 across = SaberPoseMath.Unit(restWorld[left.Hip].Translation - restWorld[right.Hip].Translation, float3.Zero);
        int chest = Find(names, "Chest_M", "Chest");
        float3 up = chest >= 0 ? SaberPoseMath.Unit(restWorld[chest].Translation - pelvis, float3.Zero) : float3.UnitY;
        if (float3.Dot(across, float3.UnitX) < .7f || float3.Dot(up, float3.UnitY) < .7f) return false;
        float3 forward = SaberPoseMath.Unit(float3.Cross(across, up), float3.Zero);
        up = SaberPoseMath.Unit(float3.Cross(forward, across), float3.Zero);
        if (forward.LengthSquared() < .9f || !RestLeg(left, restWorld, up) || !RestLeg(right, restWorld, up)) return false;
        int spine = Find(names, "Spine1_M", "Spine_M");
        if (spine < 0 || chest < 0 || !Descendant(chest, spine, parents)
            || Descendant(left.Hip, spine, parents) || Descendant(right.Hip, spine, parents)) spine = -1;
        rig = new(left, right, spine, across, up, forward);
        return true;
    }

    // A bounded compact leg target uses measured axes, rather than assuming any joint's local X.
    internal static float3 ThighDirection(TuckRig rig) =>
        SaberPoseMath.Unit(rig.Forward * .95f + rig.Up * .31f, rig.Forward);
    internal static float3 CalfDirection(TuckRig rig) =>
        SaberPoseMath.Unit(-rig.Forward * .72f - rig.Up * .69f, -rig.Up);

    internal static bool TryGuardArm(IReadOnlyList<string>? names, ReadOnlySpan<int> parents,
        ReadOnlySpan<float4x4> restWorld, bool left, out Leg arm)
    {
        arm = new(-1, -1, -1);
        if (names is null || names.Count != parents.Length || restWorld.Length != parents.Length) return false;
        string side = left ? "_L" : "_R";
        arm = new(Find(names, "Shoulder" + side), Find(names, "Elbow" + side), Find(names, "Wrist" + side));
        if (!Chain(arm, parents)) return false;
        float upper = (restWorld[arm.Knee].Translation - restWorld[arm.Hip].Translation).Length();
        float lower = (restWorld[arm.Ankle].Translation - restWorld[arm.Knee].Translation).Length();
        return float.IsFinite(upper) && float.IsFinite(lower) && upper is > .1f and < 200f && lower is > .1f and < 200f;
    }

    private static bool Chain(Leg leg, ReadOnlySpan<int> parents) => leg.Hip >= 0 && leg.Knee >= 0
        && leg.Ankle >= 0 && leg.Hip != leg.Knee && leg.Knee != leg.Ankle
        && Descendant(leg.Knee, leg.Hip, parents) && Descendant(leg.Ankle, leg.Knee, parents);
    private static bool RestLeg(Leg leg, ReadOnlySpan<float4x4> rest, float3 up)
    {
        float3 upper = rest[leg.Knee].Translation - rest[leg.Hip].Translation;
        float3 lower = rest[leg.Ankle].Translation - rest[leg.Knee].Translation;
        float upperLength = upper.Length(), lowerLength = lower.Length();
        return float.IsFinite(upperLength) && float.IsFinite(lowerLength) && upperLength is > .1f and < 200f
            && lowerLength is > .1f and < 200f && float3.Dot(upper / upperLength, up) < -.2f
            && float3.Dot(lower / lowerLength, up) < -.2f;
    }
    private static bool Descendant(int bone, int ancestor, ReadOnlySpan<int> parents)
    {
        for (int steps = 0; bone >= 0 && bone < parents.Length && steps < parents.Length; steps++)
        {
            if (bone == ancestor) return true;
            bone = parents[bone];
        }
        return false;
    }
    private static int Find(IReadOnlyList<string> names, params string[] candidates)
    {
        int found = -1;
        for (int i = 0; i < names.Count; i++)
            if (candidates.Any(n => string.Equals(n, names[i], StringComparison.OrdinalIgnoreCase)))
            {
                if (found >= 0) return -1; // An ambiguous alias is unsafe to bend.
                found = i;
            }
        return found;
    }
}
