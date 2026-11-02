using Brutal.Numerics;
using KSA;
using Purr2D2.Pose;
using RenderCore;

internal static class SwimmingPoseChecks
{
    internal static void Run()
    {
        var kitten = new KittenEva();
        var model = kitten.Renderable.Avatar.Core.CharacterModel;
        float4x4 rendered = default;
        int draws = 0;
        using var saber = new SaberPoseController((_, _, frame) => { rendered = frame; draws++; });
        Check(saber.Bind(kitten), "swimming uses the same resolved saber arm and publication processors");
        saber.Update(.1, true); saber.Update(.1, true);
        Program.RenderedViewport = Program.MainViewport;

        foreach (var state in new[] {
            (LocomotionMode.Grounded, 0f, 0f, 0d, "walking before water"),
            (LocomotionMode.Swimming, 0f, .4f, -.2d, "stationary treading at the surface"),
            (LocomotionMode.Swimming, MathF.PI / 4, 1.1f, -2d, "moving with native swim tilt"),
            (LocomotionMode.Swimming, MathF.PI / 2, 2.2f, -20d, "fully submerged horizontal swimming"),
            (LocomotionMode.Mmu, .35f, 2.7f, -20d, "submerged MMU movement"),
            (LocomotionMode.Swimming, 0f, 3.3f, -.2d, "stationary treading after resurfacing"),
            (LocomotionMode.Grounded, 0f, 0f, 0d, "walking after leaving water") })
        {
            kitten.LocomotionState.Mode = state.Item1;
            kitten.Body2Cce = doubleQuat.CreateFromAxisAngle(double3.UnitY, state.Item2);
            kitten.PositionEcl = new(12, 4, state.Item4);
            model.Transform = NativeModelTransform(kitten);
            saber.Update(.1, true);
            Program.FrameNumber++;
            var baseline = SwimSample(model, state.Item3);
            var shown = model.Sample(baseline);
            model.Publish();
            Check(saber.Eligible && saber.Blend == 1 && saber.HasFreshBodyFrame && saber.HasFreshClashSnapshot,
                state.Item5 + " preserves the live hand, current contacts and clash snapshot");
            Check(saber.TryGetBladeSegment(1.7, out var emitter, out var tip), state.Item5 + " publishes a physical blade segment");
            var bodyToEgo = double4x4.CreateFromQuaternion(kitten.Body2Cce)
                * double4x4.CreateTranslation(kitten.PositionEcl);
            Near(double3.Transform(emitter, bodyToEgo), double3.Unpack(rendered.Translation),
                state.Item5 + " keeps contact and audio at the visible emitter");
            Near(double3.Transform(tip, bodyToEgo), double3.Unpack(float3.Transform(new(0, 0, 1.7f), rendered)),
                state.Item5 + " keeps contact at the visible tip through body tilt and submersion");
            Check(shown.Select((pose, bone) => pose.Translation == baseline[bone].Translation && pose.Scale == baseline[bone].Scale).All(value => value),
                state.Item5 + " preserves all native sampled joint offsets and scales");

            float3 before = rendered.Z.XYZ;
            saber.GripOffsetFromChestCm = new(-10, -5, 24);
            saber.BladeDirectionModel = new(.4f, .5f, .75f);
            saber.Swing(); saber.Update(.08, true);
            Program.FrameNumber++; model.Sample(baseline); model.Publish();
            Check(saber.HasFreshBodyFrame && (rendered.Z.XYZ - before).Length() > .01f,
                state.Item5 + " accepts aiming and a live swing over the sampled locomotion clip");
            saber.GripOffsetFromChestCm = new(-13, -8, 18);
            saber.BladeDirectionModel = new(.05f, .8f, .6f);
        }

        Check(saber.TryBeginClash(1, 1.7f), "a swimmer's fresh blade can enter the same clash hold");
        Check(saber.TryGetBodyFrame(out var held), "the swimming clash captures a body-local hand frame");
        kitten.LocomotionState.Mode = LocomotionMode.Swimming;
        kitten.PositionEcl = new(12, 4, -30);
        kitten.Body2Cce = doubleQuat.CreateFromAxisAngle(double3.UnitY, Math.PI / 2);
        model.Transform = NativeModelTransform(kitten);
        Program.FrameNumber++; model.Sample(SwimSample(model, 4.5f)); model.Publish();
        Check(saber.TryGetBodyFrame(out var duringHold) && saber.ClashHeld,
            "the clash remains live while the native swim clip and underwater body pose change");
        Near(duringHold.Translation, held.Translation, "the held hand follows the swimmer without changing its captured body-local position");
        saber.UpdateClash(2);
        Program.FrameNumber += 2;
        Check(!saber.HasFreshBodyFrame && !saber.TryGetBladeSegment(1.7, out _, out _) && !saber.CanBeginClash(3),
            "an unseen or culled swimmer still cannot authorize stale contacts or clashes");
        Program.FrameNumber++; model.Sample(SwimSample(model, 4.6f)); model.Publish();
        Check(saber.HasFreshBodyFrame && saber.MainPoseGeneration > 1 && draws > 14,
            "fresh swimming publication resumes without rebinding or reequipping");
        saber.Update(.1, false); saber.Update(.1, false);
        Check(!saber.Active && !saber.HasFreshBodyFrame, "explicit stow still releases the submerged hand");
    }

    private static float4x4 NativeModelTransform(KittenEva kitten) => float4x4.Pack(
        SaberPoseMath.ModelToBody(.01f, kitten.CenterOfMassAsmbF)
        * double4x4.CreateFromQuaternion(kitten.Body2Cce) * double4x4.CreateTranslation(kitten.PositionEcl));

    // Supplies the clip-sampling boundary before the real hand and clash processors,
    // matching AnimatedRenderable's ordering. These are changing swim-like joint
    // rotations and bobbing, rather than a frozen rest skeleton with a mode flag.
    private static TransformTRS[] SwimSample(CharacterModel model, float phase)
    {
        var pose = model.Skeleton.DefaultLocalPose!.AsSpan().ToArray().Select(TransformTRS.FromMatrix).ToArray();
        pose[0].Translation.Y += MathF.Sin(phase) * 2;
        pose[3].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitX, MathF.Sin(phase) * .1f);
        pose[4].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitZ, MathF.Sin(phase) * .6f);
        pose[5].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitX, MathF.Cos(phase) * .5f);
        pose[6].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitY, MathF.Sin(phase) * .3f);
        pose[7].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitZ, -MathF.Sin(phase) * .6f);
        pose[10].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitX, MathF.Cos(phase) * .4f);
        pose[13].Rotation = floatQuat.CreateFromAxisAngle(float3.UnitX, -MathF.Cos(phase) * .4f);
        return pose;
    }

    private static void Near(double3 actual, double3 expected, string message) => Check((actual - expected).Length() < 2e-5, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Swimming pose check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
