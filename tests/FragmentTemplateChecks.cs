using System.Text;
using System.Xml.Serialization;
using KSA;
using Purr2D2.Rendering;

// Runs against the real KSA types, without creating a renderer, registry or physics world.
internal static class FragmentTemplateChecks
{
    private static void Main()
    {
        var xml = Encoding.UTF8.GetBytes("""
            <Assets>
              <MeshAtlas Id="fragment_atlas" Path="fixture.glb"/>
              <SubPart Id="fragment_solid">
                <PartModel Id="skin"><Mesh Id="skin_mesh"/><Material Id="metal"/><ShadowCaster>true</ShadowCaster></PartModel>
                <PartModelGlass Id="window"><Mesh Id="window_mesh"/><Material Id="glass"/></PartModelGlass>
              </SubPart>
              <Part Id="fragment">
                <MeshView Id="pick"><Mesh Id="pick_mesh"/></MeshView>
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
        var hull = root.Components.OfType<ColliderModule.Template>().Single().Colliders.OfType<ConvexHullColliderTemplate>().Single();
        if (hull.Mesh.Id != "collision_mesh" || root.SubPartInstances.Count != 1
            || root.InertMasses.Single() is not CustomMassTemplate mass || mass.Mass.Kg != 42)
            throw new Exception("Fragment references, subparts or mass were not preserved.");
        if (!double.IsNaN(mass.Mass)) throw new Exception("Worker must not initialize native template data.");
        if (bundle.Assets.OfType<MeshAtlasFileReference>().Any()) throw new Exception("CPU-loaded atlas must not be registered again.");
        // Repeat to catch serializer state leaking across cuts; no global cache is involved.
        FragmentTemplateLoader.Read(serializer, xml, "fragment", 2);
        Console.WriteLine("PASS: actual KSA XML mappings retain render/glass/picking/collider modules and mass; plain serializer regression rejected");
    }
}
