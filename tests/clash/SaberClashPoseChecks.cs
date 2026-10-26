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
        var kitten = new KittenEva();
        CharacterModel model = kitten.Renderable.Avatar.Core.CharacterModel;
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

    private static void Near(float3 actual, float3 expected, string message) => Check((actual - expected).Length() < .003f, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Clash pose check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
