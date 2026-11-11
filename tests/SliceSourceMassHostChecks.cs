using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Brutal.Numerics;
using KSA;
using Purr2D2.Slicing;

// Exercise the production resolver with real native meshes, parts, trees and
// SetRootPose. The fallback comparison executes KSA's own managed mass routine.
internal static class SliceSourceMassHostChecks
{
    private static readonly MethodInfo NativeFallback = typeof(Vehicle).GetMethod("ComputeFallbackVehicleMassPropsAsmb",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run()
    {
        ModLibrary.TagTable.ClearCollections();
        var source = new ContentSource();
        var mod = new KSA.Mod { Id = "slice-mass-test", DirectoryPath = Directory.GetCurrentDirectory() };
        source.Mods.Add(mod);
        var row = XElement.Parse("""
            <MeshAtlas Id="SliceMassAtlas" Path="Assets/Models/Purr2D2_Practice.glb" HostOnly="true"/>
            """);
        ContentStamps.Stamp(row, mod.Id, 0, "mass-fixture.xml", 0, 0);
        source.Rows.Add(row);
        MeshAtlasFileReference? atlas = null;
        var trees = new HashSet<PartTree>();
        try
        {
            var errors = new ContentErrors();
            ContentLoader.Load(source, ModLibrary.TagTable,
                new ContentLoadOptions { Serial = true, ReadPayloads = false }, null, errors);
            Check(errors.Count == 0, "mass fixture loads real picking vertices without a renderer");
            atlas = ModLibrary.Get<MeshAtlasFileReference>("SliceMassAtlas");
            var mesh = ModLibrary.Get<MeshReference>("Purr2D2_PracticeBillet_Mesh");
            var rotation = doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 4);
            foreach (var scale in new[] { new double3(.5), double3.One, new double3(2), new double3(.5, 2, 3) })
            {
                var parent = Part("parent", mesh);
                var target = Part("target", mesh, scale);
                target.PositionParentAsmb = new(10000, -13, 8);
                target.Asmb2ParentAsmb = rotation;
                Attach(parent, target);
                var tree = Tree(parent);
                var beforePose = target.MatrixAsmb2VehicleAsmb;
                float predicted = SliceSourceMass.Resolve(target, [target]);
                Check(predicted > 0 && target.MatrixAsmb2VehicleAsmb == beforePose && target.Tree == tree,
                    $"attached fallback prediction keeps the live source intact at scale {scale}");
                Check(tree.Split(target, recomputeDerivedData: false), "native tree detaches the selected source");
                var detached = target.Tree!;
                trees.Add(detached);
                detached.SetRootPose(target.PositionParentAsmb, doubleQuat.Identity);
                Near(predicted, Fallback(detached), $"fallback prediction matches native float bounds after rotated isolation at scale {scale}");
            }

            var root = Part("retained-root", mesh, new(1, 2, .5));
            root.Asmb2ParentAsmb = rotation;
            var neighbor = Part("neighbor", mesh);
            neighbor.PositionParentAsmb = new(100, 0, 0);
            Attach(root, neighbor);
            var retained = Tree(root);
            float rootMass = SliceSourceMass.Resolve(root, [root]);
            Check(retained.Split(neighbor, recomputeDerivedData: false), "native tree detaches a neighboring branch");
            trees.Add(neighbor.Tree!);
            Near(rootMass, Fallback(retained), "retained source uses its assembly orientation and excludes neighbor bounds");

            var shell = Part("shell", mesh);
            var interior = Part("interior", mesh);
            shell.PositionParentAsmb = new(3, 4, 5);
            shell.Asmb2ParentAsmb = rotation;
            interior.PositionParentAsmb = new(6, 4, 5);
            Attach(shell, interior);
            var host = Part("host", mesh);
            Attach(host, shell);
            var assembly = Tree(host);
            float combined = SliceSourceMass.Resolve(shell, [shell, interior]);
            Check(assembly.Split(shell, recomputeDerivedData: false), "native tree detaches a shell with its interior");
            trees.Add(shell.Tree!);
            shell.Tree!.SetRootPose(shell.PositionParentAsmb, doubleQuat.Identity);
            Near(combined, Fallback(shell.Tree), "fallback bounds include every internally retained full part");

            var standalone = Part("standalone", mesh);
            var standaloneTree = Tree(standalone);
            var vehicle = Vehicle(standaloneTree);
            var props = default(VehicleProperties);
            props.InertMassPropsAsmb = (OffsetMassProperties)NativeFallback.Invoke(vehicle, null)!;
            props.TotalMassPropsAsmb = props.InertMassPropsAsmb;
            props.TotalMassPropsAsmb.Props.Mass += 1000;
            Field(typeof(Vehicle), "_props").SetValue(vehicle, props);
            standaloneTree.OwningVehicle = vehicle;
            Near(SliceSourceMass.Resolve(standalone, [standalone]), props.InertMassPropsAsmb.Props.Mass,
                "already isolated source reads native dry fallback mass without stored contents");

            foreach (float mass in new[] { 42f, .0005f, -1f, float.NaN, float.PositiveInfinity })
            {
                standalone.SetInertMassPropertiesAsmb(new OffsetMassProperties
                {
                    Offset = float3.Zero,
                    Props = new MassProperties { Mass = mass, Inertia = Symmetric3x3Ex.Identity }
                });
                float resolved = SliceSourceMass.Resolve(standalone, [standalone]);
                Check(resolved.Equals(mass), "authored dry mass and invalid authored values retain preflight semantics: " + mass);
            }

            var empty = new Part("empty", new PartTemplate { Id = "empty" });
            var emptyTree = Tree(empty);
            Near(SliceSourceMass.Resolve(empty, [empty]), Fallback(emptyTree), "empty bounds use the same native one-metre fallback cube");
        }
        finally
        {
            foreach (var tree in trees) tree.Dispose();
            if (atlas != null) foreach (var mesh in atlas.Meshes) mesh.Dispose();
            ModLibrary.TagTable.ClearCollections();
        }

        PartTree Tree(Part root)
        {
            var tree = PartTree.CreateFromNewPartTree(root);
            trees.Add(tree);
            return tree;
        }
    }

    private static Part Part(string id, MeshReference mesh, double3? scale = null)
    {
        var visual = new SubPartTemplate
        {
            Id = id + "-visual",
            Components = [new MeshViewModule.Template
            {
                Key = "picker", MeshView = new() { Ref = mesh.Id, Value = mesh }
            }]
        };
        var part = new Part(id, new PartTemplate
        {
            Id = id,
            SubPartInstances = [new() { Key = "skin", Ref = visual.Id, Value = visual }]
        });
        if (scale.HasValue) part.Scale = scale.Value;
        return part;
    }

    private static void Attach(Part parent, Part child)
    {
        parent.TreeChildren.Add(child);
        child.TreeParent = parent;
    }

    private static Vehicle Vehicle(PartTree tree)
    {
        var vehicle = (Vehicle)RuntimeHelpers.GetUninitializedObject(typeof(Vehicle));
        vehicle.Parts = tree;
        return vehicle;
    }

    private static float Fallback(PartTree tree)
    {
        var owner = tree.OwningVehicle;
        try { return ((OffsetMassProperties)NativeFallback.Invoke(Vehicle(tree), null)!).Props.Mass; }
        finally { tree.OwningVehicle = owner; }
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static void Near(float actual, float expected, string message) =>
        Check(float.IsFinite(actual) && actual > 0 && Math.Abs(actual - expected) <= Math.Max(.001, expected * 2e-5), message);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Slice source mass check failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
