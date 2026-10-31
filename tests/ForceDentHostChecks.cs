using System.Xml.Serialization;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KSA.Deformation;
using Purr2D2.Force;
using Purr2D2.Rendering;

internal static class ForceDentHostChecks
{
    internal static void Run()
    {
        var quality = GameSettings.Current.Graphics.VesselDeformationQuality;
        bool enabled = GameSettings.Current.Graphics.VesselDeformation;
        var harmony = new Harmony("purr2d2.tests.dents");
        var trees = new List<PartTree>();
        int dentedBefore = FxDeformation.DentedPartCount;
        try
        {
            GameSettings.Current.Graphics.VesselDeformationQuality = GameSettings.DeformationQuality.Ultra;
            GameSettings.Current.Graphics.VesselDeformation = true;
            ForceDebrisDeformation.Install(harmony);
            Check(!ForceDebrisDeformation.IsCarrier("stock_dents0")
                && !ForceDebrisDeformation.IsCarrier(AuthoredDebrisAssets.Prefix + "fixture_node0"),
                "the deformation hook is limited to generated dent carriers");

            // Three damaged source fields exceed the native ten-dent per-module limit.
            // They overlap both pieces; a fourth, far-away field must not travel with them.
            var fields = Enumerable.Range(0, 3).Select(index => Field(index * .1)).ToArray();
            var source = new[]
            {
                new ForceBreakupSourceNode(0, -1, true, Geometry(0, new(-1, 0, 0)), fields[0]),
                new ForceBreakupSourceNode(1, -1, true, Geometry(1, new(1, 0, 0)), fields[1]),
                new ForceBreakupSourceNode(2, -1, true, null, fields[2]),
                new ForceBreakupSourceNode(3, -1, true, null, Field(1000)),
                new ForceBreakupSourceNode(4, -1, true, Geometry(2, double3.Zero))
            };
            var pieces = ForceBreakupPlanner.Build(source, 2, 100);
            Check(pieces.Length == 2 && pieces.All(piece => piece.Shape.Deformations.SequenceEqual(fields)),
                "breakup retains whole overlapping fields across chunk boundaries, including fields on invisible nodes");
            Check(pieces.All(piece => piece.Shape.Deformations.Sum(field => field.Dents.Length) > FxDeformation.MaxDentsCeiling),
                "coarsened debris preserves more than ten dents without merging or truncating native fields");

            foreach (var piece in pieces)
            {
                var (id, xml) = AuthoredDebrisAssets.Build(piece.Shape);
                var bundle = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), xml, id, piece.Shape.Surfaces.Length);
                var templates = bundle.Assets.OfType<PartTemplate>().ToArray();
                Check(templates.Count(template => ForceDebrisDeformation.IsCarrier(template.Id)) == fields.Length
                    && templates.Where(template => ForceDebrisDeformation.IsCarrier(template.Id)).All(template =>
                        template.IsSubPart && template.IsHidden && template.Components.Count == 0 && template.InertMasses.Count == 0),
                    "durable carrier templates are hidden, massless and contain no rendering or functional modules");
                // Exercise native Part construction, tree state, serialization and Deserialize
                // without a GPU/physics world. Only strip visual/collider components; keep
                // the actual generated hierarchy, transforms, mass and carrier templates.
                foreach (var template in templates)
                {
                    template.Components.Clear();
                    template.OnDataLoad(null!);
                }
                var root = new Part(id, templates.Single(template => template.Id == id));
                var tree = PartTree.CreateFromNewPartTree(root);
                trees.Add(tree);
                for (int i = 0; i < fields.Length; i++)
                    ForceDebrisDeformation.Restore(root.SubParts[piece.Shape.Nodes!.Length + i], fields[i]);

                var expectedDents = WorldDents(tree, piece.Position);
                Check(expectedDents.Length == fields.Sum(field => field.Dents.Length),
                    "native rendering sees every transferred field immediately after preparation");
                var sourceDents = fields.SelectMany(field => field.Dents.Select(dent =>
                {
                    dent.Center = float3.Pack(double3.Transform(double3.Unpack(dent.Center) * field.Scale,
                        field.Rotation) + field.Position);
                    dent.Direction = float3.NormalizeOrZero(float3.Pack(double3.Transform(
                        double3.Unpack(dent.Direction), field.Rotation)));
                    dent.Radius *= (float)field.Scale.X;
                    dent.Depth *= field.EffectiveScale * (float)field.Scale.X;
                    return dent;
                })).ToArray();
                CheckDents(sourceDents, expectedDents,
                    "translated, rotated and scaled chunks keep the source's assembly-space dent geometry");
                uint running = 0;
                var saved = root.GetReferenceWithChildren(ref running);
                Check(saved.SubPartInstances!.Sum(instance => instance.Components.OfType<FxDeformation.SaveData>()
                        .Sum(data => data.Dents.Count)) == expectedDents.Length,
                    "native vessel save writes the private dent lists on carrier subparts");
                // Same native mapping as XmlHelper, restricted to the only saved
                // module in this fixture; its global initializer scans unrelated
                // game types whose dependencies are omitted from the DLL snapshot.
                var components = new XmlAttributes();
                components.XmlElements.Add(new("FxDeformationData", typeof(FxDeformation.SaveData)));
                var overrides = new XmlAttributeOverrides();
                overrides.Add(typeof(PartInstance), nameof(PartInstance.Components), components);
                var serializer = new XmlSerializer(typeof(PartInstance), overrides);
                using var stream = new MemoryStream();
                serializer.Serialize(stream, saved);
                stream.Position = 0;
                var restoredData = (PartInstance)serializer.Deserialize(stream)!;
                restoredData.OnDataLoad(null!);
                var restoredTree = PartTree.Deserialize(restoredData);
                trees.Add(restoredTree);
                CheckDents(expectedDents, WorldDents(restoredTree, piece.Position),
                    "native XML save/reload restores dent positions, directions, radii and effective depths");
                for (int i = 0; i < fields.Length; i++)
                {
                    var carrier = restoredTree.Root.SubParts[piece.Shape.Nodes!.Length + i];
                    Check(ForceDebrisDeformation.Matches(carrier, fields[i], nativeRoundTrip: true),
                        "native reload preserves the complete raw field and its original safety scale");
                    var captured = ForceDebrisDeformation.Capture(carrier, carrier.MatrixAsmb2VehicleAsmb)!;
                    Check(ForceDebrisDeformation.Matches(carrier, captured), "unchanged captured damage stays current");
                    carrier.Modules.Get<FxDeformation>()[0].ApplySaveData(
                        new FxDeformation.SaveData { InstanceOf = FxDeformation.TEMPLATE_ID }, restoredTree.States);
                    Check(!ForceDebrisDeformation.Matches(carrier, captured), "a dent edit invalidates pending breakup capture");
                }
            }

            // A peeled branch must receive damage owned by the full part, even when
            // the full part itself has no visible geometry.
            var peeled = ForceBreakupPlanner.Build([
                new(0, -1, true, null, fields[0]),
                new(0, 0, false, Geometry(0, new(-1, 0, 0))),
                new(0, 1, false, Geometry(1, new(1, 0, 0)))
            ], 2, 100);
            Check(peeled.Length == 2 && peeled.All(piece => piece.Shape.Deformations.Length == 1),
                "subpart refinement preserves the parent-owned dent field on both peeled chunks");
        }
        finally
        {
            foreach (var tree in trees) tree.Dispose();
            harmony.UnpatchAll(harmony.Id);
            GameSettings.Current.Graphics.VesselDeformationQuality = quality;
            GameSettings.Current.Graphics.VesselDeformation = enabled;
        }
        Check(FxDeformation.DentedPartCount == dentedBefore, "debris cleanup balances KSA's global dented-part count");
    }

    private static AuthoredDebrisDeformation Field(double offset)
    {
        var dents = Enumerable.Range(0, 6).Select(index => new Dent(new(index * .01f, 0, 0),
            new(.2f, -.9f, .3f), 3, .03f + index * .001f)).ToArray();
        float scale = DentField.EffectiveScale(dents, default, in FxDeformation.Shared.Params);
        return new(new(offset, .25, -.5), doubleQuat.CreateFromYawPitchRoll(.4, -.2, .3), new(1.25), dents, scale);
    }

    private static AuthoredDebrisNode Geometry(int index, double3 center) => new(index,
        [new("native_mesh", "native_material", false)], center, doubleQuat.Identity, double3.One,
        center, new(2), 8);

    private static Dent[] WorldDents(PartTree tree, double3 piecePosition)
    {
        tree.Dents.EnsureBuilt(tree, 1);
        return tree.Dents.Dents.ToArray().Select(dent =>
        {
            dent.Center += float3.Pack(piecePosition);
            return dent;
        }).ToArray();
    }

    private static void CheckDents(Dent[] expected, Dent[] actual, string message)
    {
        Check(expected.Length == actual.Length && expected.Zip(actual).All(pair =>
            (pair.First.Center - pair.Second.Center).LengthSquared() < 1e-10f
            && (pair.First.Direction - pair.Second.Direction).LengthSquared() < 1e-10f
            && Math.Abs(pair.First.Radius - pair.Second.Radius) < 1e-6f
            && Math.Abs(pair.First.Depth - pair.Second.Depth) < 1e-6f), message);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
