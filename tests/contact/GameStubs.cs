using Brutal.Numerics;

// Only the host boundary is stubbed. The test project compiles the production cutter,
// contact geometry and slicer, and references the real game Numerics assembly.
namespace KSA
{
    public static class Double3Ex
    {
        public static double3 Transform(this double3 value, double4x4 matrix) => double3.Transform(value, matrix);
        public static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
        public static double3 NormalizeOrZero(this double3 value) => double3.NormalizeOrZero(value);
    }

    public static class Program
    {
        public static bool EditorFlag;
        public static Camera? Camera = new();
        public static Camera? GetMainCamera() => Camera;
    }

    public static class Universe
    {
        public static CelestialSystem? CurrentSystem;
        public static double SimulationSpeed = 1;
        public static double GetSimulationSpeed() => SimulationSpeed;
    }

    public sealed class CelestialSystem
    {
        public readonly HostCollection All = new();
    }

    public sealed class HostCollection
    {
        public object[] Items = [];
        public ReadOnlySpan<object> AsSpan() => Items;
    }

    public sealed class Camera
    {
        public double3 GetPositionEgo(Vehicle vehicle) => vehicle.PositionEgo;
    }

    public class Vehicle
    {
        public string Id = "fixture vessel";
        public bool IsDisposed, IsEditedVehicle;
        public bool HasPhysicsBubble = true;
        public bool IsControllable, IsDebris;
        public object Parent = new();
        public double BoundingSphereRadiusBody = 20;
        public double3 PositionEgo;
        public double3 CenterOfMassAsmb;
        public doubleQuat Asmb2Ego = doubleQuat.Identity;
        public doubleQuat Body2Ego = doubleQuat.Identity;
        public int BubbleVehicleCount = 1;
        public IVASeat[] Crew = [];
        public readonly PartTree Parts;
        public int SplitCalls, FragmentCalls, MarkAsDebrisCalls;

        public Vehicle() => Parts = new(this);

        public double4x4 GetMatrixAsmb2Ego(Camera camera) => double4x4.CreateFromQuaternion(Asmb2Ego)
            * double4x4.CreateTranslation(camera.GetPositionEgo(this) - CenterOfMassAsmb.Transform(Asmb2Ego));

        public Vehicle? Split(Part.Connection connection, Part.Connector endpoint, double impulse, out object? pose)
        {
            SplitCalls++;
            pose = null;
            return new Vehicle { Parent = Parent };
        }

        public void MarkAsDebris() { MarkAsDebrisCalls++; IsDebris = true; }
    }

    public sealed class KittenEva : Vehicle { }

    public sealed class HostList<T> : List<T> { public int Length => Count; }

    public sealed class PartTree(Vehicle owner)
    {
        public Vehicle OwningVehicle = owner;
        public readonly HostList<Part> Parts = [];
        public Part? Root => Parts.FirstOrDefault();
    }

    public sealed class Part
    {
        private static uint _nextId;
        public readonly uint InstanceId = ++_nextId;
        public string Id = "fixture hull";
        public string DisplayName = "fixture hull";
        public Part? PartParent, TreeParent;
        public PartTree? Tree;
        public readonly HostList<Part> TreeChildren = [], SubParts = [];
        public readonly List<Connection> Connections = [];
        public readonly ModuleCollection Modules = new(), SubtreeModules = new();
        public readonly PartTemplate Template = new();
        public int BoundsUpdates;
        public void UpdateBounds() => BoundsUpdates++;
        public bool IsAttachedInternal;
        public bool IsSubPart => PartParent != null;
        public Part FullPart => PartParent ?? this;
        public Part StructuralPart => FullPart;
        public double3 Scale = double3.One;
        public doubleQuat Asmb2ParentAsmb = doubleQuat.Identity;
        public double3 PositionParentAsmb;
        public double3 ScaleTotal => PartParent == null ? Scale : Scale * PartParent.ScaleTotal;
        public double4x4 MatrixAsmb2ParentAsmb => double4x4.CreateScale(Scale)
            * double4x4.CreateFromQuaternion(Asmb2ParentAsmb)
            * double4x4.CreateTranslation(PositionParentAsmb);
        public double4x4 MatrixAsmb2VehicleAsmb => PartParent == null ? MatrixAsmb2ParentAsmb
            : MatrixAsmb2ParentAsmb * PartParent.MatrixAsmb2VehicleAsmb;
        public double3 PositionVehicleAsmb => double3.Zero.Transform(MatrixAsmb2VehicleAsmb);
        public double3 PositionEgo(in double4x4 matrix) => PositionVehicleAsmb.Transform(matrix);
        public double4x4 MatrixAsmb2Ego(in double4x4 matrix) => MatrixAsmb2VehicleAsmb * matrix;

        public sealed class Connector(Part part)
        {
            public Part ConnectionPart = part;
        }

        public sealed class Connection(Part first, Part second)
        {
            public readonly Connector[] Connectors = [new(first), new(second)];
            public Part OtherPart(Part part) => part == first ? second : first;
        }
    }

    public sealed class PartTemplate { public string Id = "fixture template"; }

    public sealed class ModuleCollection
    {
        private readonly List<object> _items = [];
        private readonly Dictionary<Type, Array> _typed = new();
        public void Add(object item) { _items.Add(item); _typed.Clear(); }
        // The native ModuleList returns Span<T>. Mirror its allocation-free reads so
        // hot-loop measurements do not benchmark LINQ in the test boundary instead.
        public bool HasAny<T>() => !Get<T>().IsEmpty;
        public Span<T> Get<T>()
        {
            if (!_typed.TryGetValue(typeof(T), out var items)) _typed[typeof(T)] = items = _items.OfType<T>().ToArray();
            return (T[])items;
        }
    }

    public sealed class MeshReference
    {
        public double3[][]? PositionsCompare;
        public double BoundingSphereRadius = 20;
        public MeshReference Get() => this;
    }

    public sealed class MeshViewModule(MeshReference mesh)
    {
        public MeshReference MeshView = mesh;
        public Part? Parent;
        public MeshViewModule(string id, MeshReference mesh) : this(mesh) { }
    }

    public sealed class PartModelModule
    {
        public sealed class Template { public MeshReference? Mesh; }
        public required PartModel PartModel;
    }

    public sealed class PartModel
    {
        public required PartModelModule.Template Template;
    }
    public sealed class PartModelDynamicModule { public required PartModel PartModelDynamic; }
    public sealed class PartModelGlassModule { public required PartModel PartModelGlass; }

    public readonly record struct KeyHash(int Value)
    {
        public static KeyHash Zero => default;
    }

    public sealed class IVASeat
    {
        public KeyHash AssignedKittenHash;
    }

    public static class PartFailure
    {
        public const int MaximumVehiclesPerBubble = 30, MaximumSubPartDebris = 2;
        public static void CollectSubPartDebris(Part full, List<Part> destination)
        {
            destination.Clear();
            destination.AddRange(full.SubParts);
        }
        public static int SeverableConnectionCount(Part part) => part.Connections.Count;
        private static void IsolateAndDestroy(Vehicle vehicle, Part part, List<Vehicle> fragments)
        {
            vehicle.FragmentCalls++;
            part.Tree = null;
        }
    }
}

namespace Purr2D2.Cutting
{
    // Defined beside the engine-dependent arbitrary slicer in the production build.
    internal readonly record struct SliceAttempt(bool Success, string Message);
}
