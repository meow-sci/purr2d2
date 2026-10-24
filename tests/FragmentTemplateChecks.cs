using System.Text;
using System.Xml.Serialization;
using System.Xml.Linq;
using KSA;
using Purr2D2.Rendering;

// Runs against the real KSA types, without creating a renderer, registry or physics world.
internal static class FragmentTemplateChecks
{
    private static void Main()
    {
        FrameHooksChecks.Run();
        SliceDiagnosticsChecks.Run();
        SliceWorkerDiagnosticsChecks.Run();
        SliceWorkerHooksChecks.Run();
        QuietSliceHooksChecks.Run();
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
        if (root.Components.OfType<MeshViewModule.Template>().Any()
            || visual.Components.OfType<MeshViewModule.Template>().Single().MeshView.Id != "pick_mesh")
            throw new Exception("Picking must stay on the visual subpart for native hover raycasts.");
        var hull = root.Components.OfType<ColliderModule.Template>().Single().Colliders.OfType<ConvexHullColliderTemplate>().Single();
        if (hull.Mesh.Id != "collision_mesh" || root.SubPartInstances.Count != 1
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
        if (mergedRoot.SubPartInstances.Count != 1 || mergedRoot.SubPartInstances.Single().InstanceOf != mergedVisual.Id
            || mergedVisual.Components.OfType<PartModelModule.Template>().Count() != 3
            || mergedVisual.Components.OfType<PartModelGlassModule.Template>().Count() != 1
            || mergedVisual.Components.OfType<MeshViewModule.Template>().Single().MeshView.Id != generated.Id + "_hull")
            throw new Exception("Actual KSA XML loader lost merged fragment material/glass/picking modules.");
        FragmentTopology.Remember(XDocument.Parse(Encoding.UTF8.GetString(generated.Xml)));
        if (FragmentTopology.Find(generated.Id) == null) throw new Exception("Generated XML did not restore its durable closed-solid metadata.");
        var savedXml = File.ReadAllBytes("tests/fixtures/fragment-v3.xml");
        var saved = XDocument.Parse(Encoding.UTF8.GetString(savedXml));
        string savedId = (string)saved.Root!.Element("Part")!.Attribute("Id")!;
        var savedBundle = FragmentTemplateLoader.Read(serializer, savedXml, savedId, 4);
        if (savedId == generated.Id || !savedBundle.Assets.OfType<PartTemplate>().Any(p => p.Id == savedId))
            throw new Exception("Canonical export must preserve older saved assets under their original IDs.");
        Console.WriteLine("PASS: actual KSA XML retains visual-subpart picking, legacy picking, render/glass/collider modules and mass; missing/unattached picking and plain serializer rejected");
        Console.WriteLine("PASS: actual KSA XML decodes generated merged fragment with one visual SubPart, four render surfaces and durable topology metadata");
        Console.WriteLine("PASS: actual KSA XML still decodes frozen v3 saved fragment under its original identity");

        void Reject(XDocument input, string message)
        {
            try { FragmentTemplateLoader.Read(serializer, Encode(input), "fragment", 2); }
            catch (InvalidDataException) { return; }
            throw new Exception(message);
        }
        static byte[] Encode(XDocument input) => Encoding.UTF8.GetBytes(input.ToString());
    }
}
