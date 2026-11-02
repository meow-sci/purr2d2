using System;
using System.Linq;
using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Pose;
using RenderCore;
using RenderCore.Animation;

internal static class SaberClashPoseChecks
{
    internal static void Run()
    {
        PalmDownGesture();
        var kitten = new KittenEva();
        CharacterModel model = kitten.Renderable.Avatar.Core.CharacterModel;
        // Include visual-only scale that is absent from Core.Scale, plus a rotated
        // nonuniform component as supplied by the final native draw callback.
        model.Transform = float4x4.CreateScale(.02f, .035f, .05f)
            * float4x4.CreateRotationX(-MathF.PI / 2) * float4x4.CreateRotationZ(-MathF.PI / 2)
            * float4x4.CreateRotationY(.37f);
        kitten.Renderable.Avatar.Expressions.ExpressionRootIndex = 16;
        kitten.Renderable.Avatar.Expressions.Angry = [new AnimationAssetRef()];
        float4x4 drawn = default;
        using var saber = new SaberPoseController((_, _, emitter) => drawn = emitter);
        using var somersault = new SomersaultPoseController();
        Check(saber.Bind(kitten), "saber pose binds before the visual somersault layer");
        saber.Update(.1, true); saber.Update(.1, true);
        Check(somersault.Bind(kitten), "somersault can bind after the saber tail");
        somersault.SetMotion(SomersaultDirection.Forward, .35f, 1, true);
        Check(model.AnimProcessors.Count == 4
            && SaberPoseController.IsProcessorFor(model.AnimProcessors[1], kitten)
            && SaberPoseController.IsClashTailFor(model.AnimProcessors[^1], kitten),
            "the clash tail runs after the hand solve and final somersault root");

        KSA.Program.FrameNumber = 10;
        KSA.Program.RenderedViewport = KSA.Program.MainViewport;
        TransformTRS[] before = model.Sample();
        Check(!saber.CanBeginClash(4), "clashes reject a pose until its main-view emitter has been published");
        model.Publish();
        float4x4 emitterBefore = drawn;
        Check(saber.HasFreshClashSnapshot && saber.TryBeginClash(4),
            "a fresh matching main-view snapshot starts one clash hold");
        Check(AnimationDataRef.SampleCalls == 1,
            "the optional angry clip is sampled once when the avatar binds");

        somersault.SetMotion(SomersaultDirection.Forward, 1.45f, .4f, true);
        KSA.Program.FrameNumber++;
        TransformTRS[] held = model.Sample();
        model.Publish();
        Check(saber.ClashHeld, "the held pose remains active before 0.4 supported seconds");
        foreach (int bone in new[] { 0, 3, 4, 5, 6, 10, 11, 12 })
            Near(held[bone].Translation, before[bone].Translation,
                "the clash replays the complete local kitten pose, including chest, arms and legs");
        Near(drawn.Translation, emitterBefore.Translation,
            "the saber emitter stays at the captured body-local pose while the body and somersault continue");
        Near(drawn.Z.XYZ, emitterBefore.Z.XYZ,
            "the held emitter direction remains captured through a changing somersault pose");
        Near(drawn.X.XYZ, emitterBefore.X.XYZ,
            "clash hold does not apply observed nonuniform or draw-only scale twice");
        Check(held[17].Translation == new float3(9, 8, 7),
            "the angry expression overlays the frozen pose during the hold");

        saber.UpdateClash(4.399);
        Check(saber.ClashHeld, "the hold uses the supported-time deadline rather than wall time");
        saber.UpdateClash(4.401);
        Check(!saber.ClashHeld, "the pose hold releases after 0.4 supported seconds");
        somersault.SetMotion(SomersaultDirection.Forward, 1.45f, .4f, true);
        KSA.Program.FrameNumber++;
        TransformTRS[] angryAfterHold = model.Sample();
        model.Publish();
        Check(angryAfterHold[17].Translation == new float3(9, 8, 7),
            "the angry face remains visible after the 0.4-second body-pose hold");
        saber.UpdateClash(4.501);
        KSA.Program.FrameNumber++;
        TransformTRS[] returned = model.Sample();
        model.Publish();
        Check(returned[17].Translation != new float3(9, 8, 7),
            "the angry expression ends after 0.5 supported seconds");

        KSA.Program.FrameNumber += 2;
        Check(!saber.CanBeginClash(5), "stale main-view snapshots cannot begin another clash");
        saber.ClearClash();
        KSA.Program.FrameNumber++;
        model.Sample(); model.Publish();
        Check(saber.TryBeginClash(6), "a new fresh sample can begin a later clash");
        saber.Unbind();
        Check(!saber.ClashHeld && !model.AnimProcessors.Any(p => SaberPoseController.IsClashTailFor(p, kitten))
            && !model.AnimProcessors.Any(p => SaberPoseController.IsProcessorFor(p, kitten)),
            "unbinding removes the hand and clash tail and clears the held pose");

        Console.WriteLine("PASS: pose clash snapshot, whole-pose hold, emitter hold, angry timer and cleanup");
    }

    private static void PalmDownGesture()
    {
        var kitten = new KittenEva();
        var model = kitten.Renderable.Avatar.Core.CharacterModel;
        model.Skeleton = new(
            ["Root", "Chest_M", "Shoulder_R", "Elbow_R", "Wrist_R", "Index_R", "Middle_R", "Ring_R", "Pinky_R"],
            [-1, 0, 1, 2, 3, 4, 4, 4, 4],
            [new(0,0,0), new(0,50,0), new(-8,0,0), new(-5,-12,0), new(0,-10,0),
                new(-2,0,4), new(-.6f,0,4), new(.6f,0,4), new(2,0,4)]);
        using var saber = new SaberPoseController();
        Check(saber.Bind(kitten), "drop gesture resolves the hand and all four finger roots");
        saber.Update(.1,true); saber.Update(.1,true);
        var holding = model.Sample();
        saber.PalmDown = true;
        saber.Update(.1,true);
        Check(saber.PalmDownBlend > 0 && saber.PalmDownBlend < 1, "drop arm and paw animate through an intermediate pose");
        saber.Update(.1,true); saber.Update(.1,true);
        var released = model.Sample();
        var world = model.Skeleton.WorldTransforms;
        var across = world[5].Translation - world[8].Translation;
        var along = (world[5].Translation + world[6].Translation + world[7].Translation + world[8].Translation) / 4 - world[4].Translation;
        var normal = float3.Normalize(float3.Cross(across,along));
        Check(float3.Dot(normal,-float3.UnitY) > .999f,
            "the full release pose points the palm down using both knuckle and wrist axes");
        Check(released[5].Rotation != holding[5].Rotation && released[8].Rotation != holding[8].Rotation,
            "release visibly opens the index and pinky instead of retaining the held curl");
    }

    private static void Near(float3 actual, float3 expected, string message) => Check((actual - expected).Length() < .003f, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Clash pose check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
