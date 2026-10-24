using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Pose;
using RenderCore;

internal static class SomersaultPoseChecks
{
    public static void Main()
    {
        Geometry();
        FallbackAndLifetime();
        OrderedPoseAndPublication();
        Console.WriteLine("Somersault pose checks passed.");
    }

    private static void Geometry()
    {
        float3 pivot = new(2, 35, 6), head = pivot + float3.UnitY;
        foreach ((SomersaultDirection direction, float3 expected) in new[] {
            (SomersaultDirection.Forward, float3.UnitZ), (SomersaultDirection.Backward, -float3.UnitZ),
            (SomersaultDirection.Left, float3.UnitX), (SomersaultDirection.Right, -float3.UnitX) })
        {
            float4x4 quarter = SomersaultPoseMath.Rotation(direction, MathF.PI / 2, pivot);
            Near(float3.Transform(head, quarter) - pivot, expected, "head moves towards each named direction");
            Near(float3.Transform(pivot, quarter), pivot, "visual pivot remains stationary");
            Near(float3.Transform(head, SomersaultPoseMath.Rotation(direction, MathF.PI, pivot)),
                pivot - float3.UnitY, "continuous scalar angle visits an inverted half turn");
            Near(float3.Transform(head, SomersaultPoseMath.Rotation(direction, MathF.Tau, pivot)),
                head, "one full turn finishes at the sampled native pose");
        }
        Check(SomersaultPoseMath.TryRoots([-1, 0, -1, 2], out int[] roots)
            && roots.SequenceEqual([0, 2]), "every independent rig root is carried");
        Check(!SomersaultPoseMath.TryRoots([1, 0], out _) && !SomersaultPoseMath.TryRoots([-1, 5], out _),
            "cyclic and out-of-range hierarchies cannot become eligible");
        Check(SomersaultPoseMath.TryPivot(.01f, new(.1f, .2f, .3f), out float3 modelPivot), "physical COM resolves a model pivot");
        Near(float3.Pack(double3.Transform(double3.Unpack(modelPivot), SaberPoseMath.ModelToBody(.01f, new(.1f,.2f,.3f)))),
            float3.Zero, "model pivot maps exactly to the physical COM origin");
        Check(!SomersaultPoseMath.TryPivot(0, float3.Zero, out _) &&
            !SomersaultPoseMath.TryPivot(.01f, new(float.NaN, 0, 0), out _), "invalid scale or COM refuses visual rotation");
    }

    private static void FallbackAndLifetime()
    {
        var kitten = new KittenEva();
        CharacterModel model = kitten.Renderable.Avatar.Core.CharacterModel;
        List<string> names = model.Skeleton.BoneNames!;
        model.Skeleton.BoneNames = null;
        using var motion = new SomersaultPoseController();
        Check(motion.Bind(kitten) && motion.Eligible && !motion.SupportsTuck && model.AnimProcessors.Count == 2,
            "unknown bone names retain an equipment-independent rigid flip");
        motion.SetMotion(SomersaultDirection.Left, MathF.PI / 2, 1, true);
        TransformTRS[] first = model.Sample();
        float3 point = model.Skeleton.WorldTransforms[17].Translation;
        model.Sample();
        Near(model.Skeleton.WorldTransforms[17].Translation, point, "fresh sampled baselines cannot accumulate rotations");
        Near(first[0].Scale, float3.One, "visual flip never shrinks the kitten");
        foreach (float turn in new[] { MathF.Tau, 2 * MathF.Tau })
        {
            motion.SetMotion(SomersaultDirection.Left, turn + MathF.PI / 2, 0, true);
            model.Sample();
            Near(model.Skeleton.WorldTransforms[17].Translation, point,
                "production pose renders the second and third turn beyond the original one-turn clamp");
            motion.SetMotion(SomersaultDirection.Left, turn + MathF.PI, 0, true);
            model.Sample();
            Check((model.Skeleton.WorldTransforms[17].Translation - point).Length() > 1,
                "each additional turn visibly progresses through another inverted pose");
        }
        var other = new KittenEva();
        using var otherMotion = new SomersaultPoseController();
        Check(otherMotion.Bind(other), "second kitten binds independently");
        other.Renderable.Avatar.Core.CharacterModel.Sample();
        Near(other.Renderable.Avatar.Core.CharacterModel.Skeleton.WorldTransforms[17].Translation, new(4, 23, 0),
            "one kitten's flip never moves another kitten's roots");
        model.Skeleton.BoneNames = names;
        using var saber = new SaberPoseController();
        Check(saber.Bind(kitten) && model.AnimProcessors.Count == 3
            && SaberPoseController.IsProcessorFor(model.AnimProcessors[1], kitten),
            "equipping a saber after binding inserts the hand solve between tuck and final flip");
        saber.Unbind();
        Check(model.AnimProcessors.Count == 2 && motion.Eligible, "stowing saber preserves the motion layer");
        motion.Unbind();
        Check(model.AnimProcessors.Count == 0, "unbind removes only the motion-owned processors");

        var corrupt = new KittenEva();
        corrupt.Renderable.Avatar.Core.CharacterModel.Skeleton.NodeInfo[0].ParentIndex = 1;
        using var bad = new SomersaultPoseController();
        Check(!bad.Bind(corrupt) && !bad.Eligible
            && corrupt.Renderable.Avatar.Core.CharacterModel.AnimProcessors.Count == 0,
            "invalid hierarchy fails before any processor or launch eligibility is installed");
        var badCom = new KittenEva { CenterOfMassAsmbF = new(1000, 0, 0) };
        Check(!bad.Bind(badCom) && !bad.Eligible, "a finite but distant COM cannot turn the kitten around an unsafe pivot");
    }

    private static void OrderedPoseAndPublication()
    {
        var kitten = new KittenEva();
        CharacterModel model = kitten.Renderable.Avatar.Core.CharacterModel;
        float4x4 drawn = default;
        int draws = 0;
        using var saber = new SaberPoseController((_, _, frame) => { drawn = frame; draws++; });
        Check(saber.Bind(kitten), "saber binds first");
        saber.Update(.1, true); saber.Update(.1, true);
        using var motion = new SomersaultPoseController();
        Check(motion.Bind(kitten) && motion.SupportsTuck
            && SaberPoseController.IsProcessorFor(model.AnimProcessors[1], kitten),
            "measured leg geometry enables tuck and reorders an already equipped hand solver");
        motion.SetMotion(SomersaultDirection.Forward, 0, 1, true);
        model.Sample(); model.Publish();
        float4x4 tuckEmitter = drawn;
        float3 upper = model.Skeleton.WorldTransforms[11].Translation - model.Skeleton.WorldTransforms[10].Translation;
        float3 lower = model.Skeleton.WorldTransforms[12].Translation - model.Skeleton.WorldTransforms[11].Translation;
        Check(MathF.Abs(upper.Length() - 17) < .001f && MathF.Abs(lower.Length() - 17) < .001f
            && upper.Z > 14 && upper.Y > 0 && lower.Z < -10,
            "tuck bends both measured leg segments without changing their lengths");
        float3 leftUpper = model.Skeleton.WorldTransforms[8].Translation - model.Skeleton.WorldTransforms[7].Translation;
        float3 leftLower = model.Skeleton.WorldTransforms[9].Translation - model.Skeleton.WorldTransforms[8].Translation;
        Check(MathF.Abs(leftUpper.Length() - 13) < .001f && MathF.Abs(leftLower.Length() - 10) < .001f
            && leftUpper.Z > 1 && leftLower.Z > 1, "free arm folds into a measured guard while preserving segment lengths");

        motion.SetMotion(SomersaultDirection.Forward, MathF.PI / 2, 1, true);
        model.Sample(); model.Publish();
        Check(SomersaultPoseMath.TryPivot(.01f, kitten.CenterOfMassAsmbF, out float3 pivot), "fixture COM pivot resolves");
        float4x4 expected = tuckEmitter * SomersaultPoseMath.Rotation(SomersaultDirection.Forward, MathF.PI / 2, pivot);
        Near(drawn.Translation, expected.Translation, "hilt publishes the final flipped grip position");
        Near(drawn.Z.XYZ, expected.Z.XYZ, "fallback hand rig publishes the flipped blade direction");
        Near(drawn.X.XYZ, expected.X.XYZ, "hilt roll follows the same full-body rotation");
        Check(saber.TryGetBodyFrame(out double4x4 body), "main-view emitter publishes a fresh body frame");
        Near(float3.Pack(body.Translation), float3.Pack((double4x4.Unpack(expected)
            * SaberPoseMath.ModelToBody(.01f, kitten.CenterOfMassAsmbF)).Translation),
            "audio/contact body snapshot shares the rendered flipped emitter");

        ulong generation = saber.MainPoseGeneration;
        KSA.Program.RenderedViewport = new Viewport(ViewportType.Secondary);
        model.Publish();
        Check(saber.MainPoseGeneration == generation && draws == 3, "secondary view republishes without refreshing main contact ownership");
        KSA.Program.RenderedViewport = new Viewport(ViewportType.CharacterPortrait);
        model.Publish();
        Check(draws == 3, "portrait view never publishes a gameplay blade");
        KSA.Program.RenderedViewport = KSA.Program.MainViewport;

        motion.Unbind();
        Check(model.AnimProcessors.Count == 1 && saber.Eligible, "motion cleanup preserves an independently equipped saber");
        model.Sample(); model.Publish();
        float4x4 upright = drawn;
        motion.Bind(kitten);
        motion.SetMotion(SomersaultDirection.Right, 1, 1, false);
        model.Sample(); model.Publish();
        Near(drawn.Translation, upright.Translation, "inactive motion releases the hand's root transform without pose residue");

        // Same candidate names with an invalid leg topology must fall back rather than bend an arm.
        motion.Unbind();
        model.Skeleton.NodeInfo[11].ParentIndex = 4;
        Check(motion.Bind(kitten) && !motion.SupportsTuck, "leg-name matches with a wrong ancestry disable only tuck");
        motion.Dispose();
        Check(model.AnimProcessors.Count == 1, "dispose removes callbacks and keeps foreign/saber processors");
        saber.Unbind();
        Check(saber.Bind(kitten) && model.AnimProcessors.Count == 1, "disposed motion leaves no static callback that reinstalls its processors");
    }

    private static void Near(float3 actual, float3 expected, string message) => Check((actual - expected).Length() < .003f, message);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Somersault pose check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
