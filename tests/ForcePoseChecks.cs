using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Pose;
using RenderCore;
using RenderCore.Animation;

// Host boundaries are the existing somersault fixture. These checks run the actual
// three controllers and real Numerics, not a replacement arm solver or pose order.
internal static class ForcePoseChecks
{
    public static void Main()
    {
        Geometry();
        LeftHandAndLifetime();
        Composition();
        UnsupportedRigs();
        Console.WriteLine("Force pose checks passed (left hand, measured IK, independent saber/MMU ownership, pose composition, safe fallback).");
    }

    private static void Geometry()
    {
        Near(ForcePoseMath.AimModel(double3.UnitX), float3.UnitZ, "body forward becomes model forward");
        Near(ForcePoseMath.AimModel(new double3(0, -1, 0)), float3.UnitX, "body left becomes model left");
        Near(ForcePoseMath.AimModel(new double3(0, 0, -1)), float3.UnitY, "body up becomes model up");
        foreach (double3? invalid in new double3?[] { null, double3.Zero, new(double.NaN, 0, 0), new(0, double.PositiveInfinity, 0) })
            Near(ForcePoseMath.AimModel(invalid), float3.UnitZ, "missing or invalid aim uses a finite forward gesture");
        Check(SomersaultPoseMath.Finite(ForcePoseMath.AimModel(new(double.MaxValue, double.MaxValue, 0))),
            "large finite aim directions cannot overflow pose geometry");
        float3 shoulder = new(8, 58, 0), elbow = new(13, 46, 0), wrist = new(13, 36, 0);
        foreach (float3 aim in new[] { float3.UnitZ, float3.UnitX, -float3.UnitX, float3.UnitY, -float3.UnitZ, new float3(float.NaN) })
        {
            Check(ForcePoseMath.TrySolve(shoulder, elbow, wrist, aim, out float3 solvedElbow, out float3 solvedWrist),
                "left arm solves ordinary, sideways, upward, behind and invalid aim safely");
            Check(MathF.Abs((solvedElbow - shoulder).Length() - 13) < .001f
                && MathF.Abs((solvedWrist - solvedElbow).Length() - 10) < .001f,
                "Force IK preserves both measured left-arm lengths");
            float3 raisedTarget = shoulder + ForcePoseMath.ReachDirection(aim) * (23 * .88f);
            Check(solvedWrist.X > shoulder.X && solvedWrist.Z > shoulder.Z
                && MathF.Abs(solvedWrist.Y - (raisedTarget.Y + wrist.Y) * .5f) < .001f,
                "every gesture retains outward/forward reach at half the previous lift from the native paw height");
        }
        Check(!ForcePoseMath.TrySolve(shoulder, shoulder, wrist, float3.UnitZ, out _, out _)
            && !ForcePoseMath.TrySolve(new(float.NaN), elbow, wrist, float3.UnitZ, out _, out _),
            "missing or nonfinite arm geometry refuses a pose");
    }

    private static void LeftHandAndLifetime()
    {
        var kitten = new KittenEva();
        CharacterModel model = kitten.Renderable.Avatar.Core.CharacterModel;
        model.Skeleton = FingerRig();
        var stock = new StockFingerPose(); model.AnimProcessors.Add(stock);
        TransformTRS[] native = model.Sample();
        float nativeWristHeight = model.Skeleton.WorldTransforms[9].Translation.Y;
        using var force = new ForcePoseController();
        Check(force.Bind(kitten) && force.Eligible && model.AnimProcessors.Count == 2,
            "Force binds an independent left-hand overlay with no saber equipped");
        Check(force.Bind(kitten) && model.AnimProcessors.Count == 2, "repeated Force bind reuses its processor");
        force.Update(0, true); Check(force.Blend == 0, "activation while paused leaves the arm at its sampled baseline");
        force.Update(.1, true); Check(force.Blend > 0 && force.Blend < 1, "the Force hand raises gradually");
        force.Update(.1, true); Check(force.Active && force.Blend == 1, "the Force hand reaches full strength independently of equipment");
        TransformTRS[] held = model.Sample();
        var left = new HashSet<int>([7, 8, 9, 18, 19, 20, 21]);
        for (int i = 0; i < native.Length; i++)
        {
            Near(held[i].Translation, native[i].Translation, "Force never changes sampled bone translations");
            Near(held[i].Scale, native[i].Scale, "Force never changes sampled bone scales");
            if (!left.Contains(i)) SameRotation(held[i].Rotation, native[i].Rotation,
                "right paw, legs, spine and roots retain the native sampled pose");
        }
        float3 shoulder = model.Skeleton.WorldTransforms[7].Translation;
        float3 elbow = model.Skeleton.WorldTransforms[8].Translation;
        float3 wrist = model.Skeleton.WorldTransforms[9].Translation;
        float raisedHeight = shoulder.Y + ForcePoseMath.ReachDirection(float3.UnitZ).Y * (23 * .88f);
        Check(wrist.X > shoulder.X && wrist.Y > nativeWristHeight && wrist.Y < shoulder.Y && wrist.Z > shoulder.Z
            && MathF.Abs(wrist.Y - (raisedHeight + nativeWristHeight) * .5f) < .004f
            && MathF.Abs((elbow - shoulder).Length() - 13) < .001f && MathF.Abs((wrist - elbow).Length() - 10) < .001f,
            "the live processor lowers the Force lift by half without stretching its rig");
        for (int i = 18; i < 22; i++) SameRotation(held[i].Rotation, floatQuat.Identity,
            "discovered left fingers open towards their reference rotations");
        Check(kitten.Renderable.Avatar.Attachments.Mmu.Enabled, "Force pose does not acquire or change MMU visibility ownership");
        foreach (double dt in new[] { 0, -1, double.NaN, double.PositiveInfinity }) force.Update(dt, false);
        Check(force.Blend == 1, "pause and invalid frame times do not advance gesture release");
        force.Update(.05, false); Check(force.Blend > 0 && force.Blend < 1, "release eases the left arm back towards native animation");
        foreach (LocomotionMode mode in new[] { LocomotionMode.Grounded, LocomotionMode.Airborne, LocomotionMode.Mmu,
                     LocomotionMode.Tumbling, LocomotionMode.Rightening })
        {
            kitten.LocomotionState.Mode = mode; force.Update(.1, true); model.Sample();
            Check(force.Active, "ground, airborne and MMU locomotion retain the independent Force hand");
        }
        kitten.LocomotionState.Mode = LocomotionMode.Ladder;
        TransformTRS[] ladder = model.Sample();
        Check(!force.Eligible && !force.Active, "a ladder releases the Force gesture instead of taking over the climbing hand");
        for (int i = 0; i < native.Length; i++) SameRotation(ladder[i].Rotation, native[i].Rotation,
            "unsupported locomotion retains the full native sampled pose");
        kitten.LocomotionState.Mode = LocomotionMode.Mmu;
        force.Update(.1, true); Check(force.Active, "returning from unsupported locomotion resumes an active capture gesture");

        var local = native.ToArray(); IAnimProcessor processor = model.AnimProcessors[1];
        for (int i = 0; i < 100; i++) processor.UpdateLocalPose(float4x4.Identity, model.Skeleton, local, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            native.CopyTo(local, 0);
            processor.UpdateLocalPose(float4x4.Identity, model.Skeleton, local, 0);
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "the render-time Force hand overlay allocates no frame memory");

        force.Unbind();
        Check(model.AnimProcessors.Count == 1 && ReferenceEquals(model.AnimProcessors[0], stock)
            && kitten.Renderable.Avatar.Attachments.Mmu.Enabled && force.Blend == 0,
            "Force cleanup removes only its processor and leaves native state and MMU visibility intact");
        Check(force.Bind(kitten), "the Force pose can rebind after a normal release");
        kitten.IsDisposed = true; force.Update(.1, true);
        Check(force.Kitten is null && model.AnimProcessors.Count == 1, "a removed kitten releases the Force pose at the next settled update");
    }

    private static void Composition()
    {
        foreach (string order in new[] { "SFM", "SMF", "FSM", "FMS", "MSF", "MFS" })
        {
            var kitten = new KittenEva();
            CharacterModel model = kitten.Renderable.Avatar.Core.CharacterModel;
            model.Skeleton = FingerRig();
            float4x4 emitter = default;
            using var saber = new SaberPoseController((_, _, value) => emitter = value);
            using var force = new ForcePoseController();
            using var motion = new SomersaultPoseController();
            foreach (char bind in order)
                Check(bind switch { 'S' => saber.Bind(kitten), 'F' => force.Bind(kitten), _ => motion.Bind(kitten) },
                    "saber, Force and motion bind in every attachment order");
            saber.Update(.1, true); saber.Update(.1, true);
            force.Update(.1, true); force.Update(.1, true);
            motion.SetMotion(SomersaultDirection.Forward, 0, 0, false);
            TransformTRS[] held = model.Sample(); model.Publish();
            float4x4[] upright = model.Skeleton.WorldTransforms.ToArray();
            float4x4 uprightEmitter = emitter;
            force.Update(.1, false); force.Update(.1, false);
            TransformTRS[] rightOnly = model.Sample(); model.Publish();
            foreach (int bone in new[] { 4, 5, 6, 22, 23, 24, 25 }) SameRotation(held[bone].Rotation, rightOnly[bone].Rotation,
                "Force activation never changes the concurrent right-paw saber pose");
            force.Update(.1, true); force.Update(.1, true);
            motion.SetMotion(SomersaultDirection.Forward, .7f, 0, true);
            model.Sample(); model.Publish();
            Check(SomersaultPoseMath.TryPivot(.01f, kitten.CenterOfMassAsmbF, out float3 pivot), "composition pivot resolves");
            float4x4 rotation = SomersaultPoseMath.Rotation(SomersaultDirection.Forward, .7f, pivot);
            for (int i = 0; i < upright.Length; i++) Near(model.Skeleton.WorldTransforms[i].Translation,
                (upright[i] * rotation).Translation, "both hand poses receive the final somersault root rotation exactly once");
            Near(emitter.Translation, (uprightEmitter * rotation).Translation,
                "the saber publishes its final transformed grip while Force remains active");
            motion.SetMotion(SomersaultDirection.Forward, .7f, 1, true);
            model.Sample();
            Check(force.Active && saber.Active, "tuck preserves independent ownership of both active hand poses");
            force.Unbind();
            Check(model.AnimProcessors.Count == 4 && saber.Active && motion.Eligible
                && SaberPoseController.IsClashTailFor(model.AnimProcessors[^1], kitten)
                && !kitten.Renderable.Avatar.Attachments.Mmu.Enabled,
                "Force unbind preserves the lit hand/clash pair, motion processors and saber-owned MMU override");
            Check(force.Bind(kitten), "Force can attach again after the motion layer has already bound");
            force.Update(.1, true); force.Update(.1, true);
            saber.Unbind();
            Check(force.Active && model.AnimProcessors.Count == 3 && kitten.Renderable.Avatar.Attachments.Mmu.Enabled,
                "stowing the saber leaves the Force hand and motion layers active and restores saber-owned MMU state");
            motion.SetMotion(SomersaultDirection.Forward, .7f, 0, true);
            model.Sample();
            Near(model.Skeleton.WorldTransforms[9].Translation, (upright[9] * rotation).Translation,
                "stowing the right saber cannot duplicate or remove the left hand's root rotation");
            motion.Unbind();
            Check(force.Active && model.AnimProcessors.Count == 1, "ending a somersault preserves the equipment-independent Force hand");
            model.Sample();
            Near(model.Skeleton.WorldTransforms[9].Translation, upright[9].Translation,
                "ending motion clears root rotation residue from the active Force hand");
            Check(saber.Bind(kitten), "right saber rebinds independently after motion has released its root transform");
            saber.Update(.1, true); saber.Update(.1, true); model.Sample(); model.Publish();
            Near(emitter.Translation, uprightEmitter.Translation,
                "reequipping the saber after a Force-only interval cannot inherit a stale somersault transform");
            Check(model.AnimProcessors.Count == 3
                && SaberPoseController.IsClashTailFor(model.AnimProcessors[^1], kitten),
                "repeated layer transitions never duplicate hand or clash-tail processors");
        }
    }

    private static void UnsupportedRigs()
    {
        foreach (string failure in new[] { "names", "chain", "crosshand", "cycle" })
        {
            var kitten = new KittenEva(); CharacterModel model = kitten.Renderable.Avatar.Core.CharacterModel;
            if (failure == "names") model.Skeleton.BoneNames = null;
            else if (failure == "chain") model.Skeleton.NodeInfo[8].ParentIndex = 6;
            else if (failure == "crosshand") model.Skeleton.NodeInfo[7].ParentIndex = 6;
            else model.Skeleton.NodeInfo[0].ParentIndex = 1;
            using var force = new ForcePoseController();
            Check(!force.Bind(kitten) && !force.Eligible && model.AnimProcessors.Count == 0
                && kitten.Renderable.Avatar.Attachments.Mmu.Enabled,
                "unknown names, unsafe arm ancestry and cyclic rigs fail before any pose or cosmetic state is installed");
        }
        var changed = new KittenEva();
        using var bound = new ForcePoseController(); Check(bound.Bind(changed), "valid rig binds before a runtime hierarchy change");
        bound.Update(.1, true); bound.Update(.1, true);
        changed.Renderable.Avatar.Core.CharacterModel.Skeleton.NodeInfo[8].ParentIndex = 3;
        changed.Renderable.Avatar.Core.CharacterModel.Sample();
        Check(!bound.Eligible && !bound.Active, "a changed hierarchy disables the pose safely inside its render callback");
        bound.Dispose(); bound.Dispose();
        Check(changed.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0,
            "repeated Force disposal releases its callback exactly once");
    }

    private static Skeleton FingerRig()
    {
        Skeleton basic = Skeleton.KnownRig();
        string[] names = basic.BoneNames!.Concat(new[] { "Index1_L", "Middle1_L", "Ring1_L", "Thumb1_L",
            "Index1_R", "Middle1_R", "Ring1_R", "Thumb1_R" }).ToArray();
        int[] parents = basic.NodeInfo.ToArray().Select(n => n.ParentIndex).Concat([9, 9, 9, 9, 6, 6, 6, 6]).ToArray();
        float3[] offsets = basic.DefaultLocalPose!.AsSpan().ToArray().Select(m => m.Translation)
            .Concat(new[] { new float3(2, 0, 3), new(1, 0, 4), new(-1, 0, 3), new(3, 0, 1),
                new(-2, 0, 3), new(-1, 0, 4), new(1, 0, 3), new(-3, 0, 1) }).ToArray();
        return new Skeleton(names, parents, offsets);
    }

    private sealed class StockFingerPose : IAnimProcessor
    {
        public float Priority => 5;
        public bool ShouldUpdate { get; set; } = true;
        public bool CanCacheAnimation => true;
        public void UpdateLocalPose(float4x4 transform, Skeleton skeleton, Span<TransformTRS> localPose, float dt)
        {
            for (int i = 18; i < 26; i++)
                localPose[i].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitX, .6f);
        }
        public void UpdateSkeleton(float4x4 transform, Skeleton skeleton, float dt) { }
    }

    private static void SameRotation(floatQuat actual, floatQuat expected, string message)
    {
        Near(float3.Transform(float3.UnitX, actual), float3.Transform(float3.UnitX, expected), message);
        Near(float3.Transform(float3.UnitY, actual), float3.Transform(float3.UnitY, expected), message);
    }
    private static void Near(float3 actual, float3 expected, string message) => Check((actual - expected).Length() < .004f, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Force pose check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
