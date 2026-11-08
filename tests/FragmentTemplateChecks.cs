using System.Text;
using System.Xml.Serialization;
using System.Xml.Linq;
using KSA;
using Purr2D2.Rendering;
using Purr2D2.Cutting;

// Runs against real KSA types and managed content collections, without a renderer or physics world.
internal static class FragmentTemplateChecks
{
    private static void Main()
    {
        FrameHooksChecks.Run();
        SliceDiagnosticsChecks.Run();
        SliceWorkerDiagnosticsChecks.Run();
        SliceWorkerHooksChecks.Run();
        QuietSliceHooksChecks.Run();
        EnvironmentLightingChecks.Run();
        ClutterFoliageHostChecks.Run();
        SwimmingAnimationHostChecks.Run();
        SaberWaterHostChecks.Run();
        CapEmissionHostChecks.Run();
        FragmentContentHostChecks.Run();
        CheckWoodMaterial();
        var xml = Encoding.UTF8.GetBytes("""
            <Assets>
              <MeshAtlas Id="fragment_atlas" Path="fixture.glb"/>
              <SubPart Id="fragment_solid">
                <MeshView Id="pick"><Mesh Id="pick_mesh"/></MeshView>
                <PartModel Id="skin"><Mesh Id="skin_mesh"/><Material Id="metal"/><ShadowCaster>true</ShadowCaster></PartModel>
                <PartModelGlass Id="window"><Mesh Id="window_mesh"/><Material Id="glass"/></PartModelGlass>
              </SubPart>
              <Part Id="fragment">
                <SubPart InstanceOf="fragment_solid"/>
                <Collider Id="collider"><ConvexHull Id="hull"><Mesh Id="collision_mesh"/></ConvexHull></Collider>
                <CustomMass><Mass Kg="42"/><MassSpecificInertia Ixx="1" Iyy="2" Izz="3"/></CustomMass>
              </Part>
            </Assets>
            """);
        bool rejected = false;
        try { FragmentTemplateLoader.Read(new XmlSerializer(typeof(AssetBundle)), xml, "fragment", 2); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("The component-loss regression must be detected before registration.");
        var serializer = FragmentTemplateLoader.CreateSerializer();
        var bundle = FragmentTemplateLoader.Read(serializer, xml, "fragment", 2);
        var root = bundle.Assets.OfType<PartTemplate>().Single(p => p.Id == "fragment");
        var visual = bundle.Assets.OfType<PartTemplate>().Single(p => p.Id == "fragment_solid");
        if (!root.IsHidden || !visual.IsHidden || root.IsSubPart || !visual.IsSubPart)
            throw new Exception("Generated debris must stay instantiable as parts/subparts but be absent from both editor catalogs.");
        if (root.Components.OfType<MeshViewModule.Template>().Any()
            || visual.Components.OfType<MeshViewModule.Template>().Single().MeshView.Ref != "pick_mesh")
            throw new Exception("Picking must stay on the visual subpart for native hover raycasts.");
        var hull = root.Components.OfType<ColliderModule.Template>().Single().Colliders.OfType<ConvexHullColliderTemplate>().Single();
        if (hull.Mesh?.Ref != "collision_mesh" || root.SubPartInstances.Count != 1
            || root.InertMasses.Single() is not CustomMassTemplate mass || mass.Mass.Kg != 42)
            throw new Exception("Fragment references, subparts or mass were not preserved.");
        if (!double.IsNaN(mass.Mass)) throw new Exception("Worker must not initialize native template data.");
        if (bundle.Assets.OfType<MeshAtlasFileReference>().Any()) throw new Exception("CPU-loaded atlas must not be registered again.");
        // Repeat to catch serializer state leaking across cuts; no global cache is involved.
        FragmentTemplateLoader.Read(serializer, xml, "fragment", 2);
        var document = XDocument.Parse(Encoding.UTF8.GetString(xml));
        var picker = document.Root!.Element("SubPart")!.Element("MeshView")!;
        picker.Remove();
        Reject(document, "Missing picking geometry must be rejected.");
        document.Root.Element("Part")!.Add(picker);
        FragmentTemplateLoader.Read(serializer, Encode(document), "fragment", 2);
        picker.Remove();
        document.Root.Add(new XElement("SubPart", new XAttribute("Id", "orphan"), picker));
        Reject(document, "An unattached template cannot supply the picking geometry.");
        var (fixture, materials) = FragmentAssetChecks.Fixture();
        var generated = FragmentAssetWriter.Build(fixture, materials, CancellationToken.None);
        var merged = FragmentTemplateLoader.Read(serializer, generated.Xml, generated.Id, generated.Uploads.Length);
        var mergedRoot = merged.Assets.OfType<PartTemplate>().Single(p => p.Id == generated.Id);
        var mergedVisual = merged.Assets.OfType<SubPartTemplate>().Single();
        if (!mergedRoot.IsHidden || !mergedVisual.IsHidden)
            throw new Exception("Newly sliced roots and visual subparts must be hidden from the vehicle editor.");
        if (mergedRoot.SubPartInstances.Count != 1 || mergedRoot.SubPartInstances.Single().Ref != mergedVisual.Id
            || mergedVisual.Components.OfType<PartModelModule.Template>().Count() != 3
            || mergedVisual.Components.OfType<PartModelGlassModule.Template>().Count() != 1
            || mergedVisual.Components.OfType<MeshViewModule.Template>().Single().MeshView.Ref != generated.Id + "_hull")
            throw new Exception("Actual KSA XML loader lost merged fragment material/glass/picking modules.");
        FragmentTopology.Remember(XDocument.Parse(Encoding.UTF8.GetString(generated.Xml)));
        if (FragmentTopology.Find(generated.Id) == null) throw new Exception("Generated XML did not restore its durable closed-solid metadata.");
        var savedXml = File.ReadAllBytes("tests/fixtures/fragment-v3.xml");
        var saved = XDocument.Parse(Encoding.UTF8.GetString(savedXml));
        string savedId = (string)saved.Root!.Element("Part")!.Attribute("Id")!;
        var savedBundle = FragmentTemplateLoader.Read(serializer, savedXml, savedId, 4);
        if (savedId == generated.Id || !savedBundle.Assets.OfType<PartTemplate>().Any(p => p.Id == savedId))
            throw new Exception("Canonical export must preserve older saved assets under their original IDs.");
        if (savedBundle.Assets.OfType<PartTemplate>().Any(p => !p.IsHidden))
            throw new Exception("Older saved fragments without editor tags must also be hidden when loaded.");
        // Exercise the shared visibility fix on normalized historical data, including
        // native tags that were already resolved by OnDataLoad.
        using var savedStream = new MemoryStream(savedXml, writable: false);
        var restored = (AssetBundle)serializer.Deserialize(FragmentTemplateLoader.NormalizeDocument(XDocument.Load(savedStream)).CreateReader())!;
        var restoredParts = restored.Assets.OfType<PartTemplate>().ToArray();
        if (restoredParts.Any(p => p.IsHidden)) throw new Exception("The frozen legacy fixture must reproduce unhidden debris.");
        var identities = restoredParts.Select(p => (p.Id, p.IsSubPart)).ToArray();
        var structural = new EditorTag("Structural");
        restoredParts[0].EditorTags.Add(structural);
        FragmentTemplateLoader.HideFromEditor(restored);
        FragmentTemplateLoader.HideFromEditor(restored);
        if (restoredParts.Any(p => !p.IsHidden || p.EditorTags.Count(t => t == EditorTag.Hidden) != 1)
            || !restoredParts[0].EditorTags.Contains(structural)
            || !identities.SequenceEqual(restoredParts.Select(p => (p.Id, p.IsSubPart)))
            || !savedXml.SequenceEqual(File.ReadAllBytes("tests/fixtures/fragment-v3.xml")))
            throw new Exception("Restored debris visibility must be idempotent and preserve tags, part identities and immutable save assets.");
        Console.WriteLine("PASS: actual KSA XML retains visual-subpart picking, legacy picking, render/glass/collider modules and mass; missing/unattached picking and plain serializer rejected");
        Console.WriteLine("PASS: actual KSA XML decodes generated merged fragment with one visual SubPart, four render surfaces and durable topology metadata");
        Console.WriteLine("PASS: actual KSA XML still decodes frozen v3 saved fragment under its original identity");
        Console.WriteLine("PASS: fresh and restored debris roots/subparts are editor-hidden; repeated normalization preserves native tags, identities and saved XML");

        void Reject(XDocument input, string message)
        {
            try { FragmentTemplateLoader.Read(serializer, Encode(input), "fragment", 2); }
            catch (InvalidDataException) { return; }
            throw new Exception(message);
        }
        static byte[] Encode(XDocument input) => Encoding.UTF8.GetBytes(input.ToString());
    }

    private static void CheckWoodMaterial()
    {
        using var stream = File.OpenRead("Assets/purr2d2.xml");
        var assets = (AssetBundle)new XmlSerializer(typeof(AssetBundle)).Deserialize(stream)!;
        var wood = assets.Assets.OfType<PbrMaterialReference>().Single(m => m.Id == FragmentAssetWriter.WoodCapMaterial);
        var metal = assets.Assets.OfType<PbrMaterialReference>().Single(m => m.Id == FragmentAssetWriter.CapMaterial);
        if (wood.DiffuseReference?.Ref != "Purr2D2_WoodCutSurface_Diffuse"
            || wood.NormalReference?.Ref != "Purr2D2_WoodCutSurface_Normal"
            || wood.PBRMap?.Ref != "Purr2D2_WoodCutSurface_AoRoughMetal"
            || wood.EmissiveMap != null || wood.AlphaMap != null || metal.EmissiveMap?.Ref != "EmptyWhite")
            throw new Exception("Native asset XML lost opaque nonemissive wood maps or changed the generic metal material.");
        var (fixture, materials) = FragmentAssetChecks.Fixture();
        var generated = FragmentAssetWriter.Build(fixture with { CutKind = SliceCutKind.Wood }, materials, default);
        var decoded = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), generated.Xml, generated.Id, generated.Uploads.Length);
        var woodModel = decoded.Assets.OfType<PartTemplate>().SelectMany(p => p.Components).OfType<PartModelModule.Template>()
            .Single(p => p.Material?.Ref == FragmentAssetWriter.WoodCapMaterial);
        if (woodModel.Mesh?.Ref.EndsWith("_cap", StringComparison.Ordinal) != true || !woodModel.ShadowCaster)
            throw new Exception("Native generated wood cap XML lost its real opaque shadow-casting model.");
        Console.WriteLine("PASS: actual native XML loader retains separate opaque nonemissive wood texture maps, original generic metal emission and generated solid wood cap model");
    }
}
