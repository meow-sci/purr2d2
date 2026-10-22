using Brutal.Numerics;

// This harness links the production EVA controller, geometry and snapshot code.
// Only the host boundary is replaced; no game assembly is loaded.
namespace KSA;

public static class Double3Ex
{
    public static double3 Transform(this double3 value, double4x4 matrix) => double3.Transform(value, matrix);
    public static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
    public static double3 NormalizeOrZero(this double3 value) => double3.NormalizeOrZero(value);
    public static doubleQuat Inverse(this doubleQuat value) => doubleQuat.Inverse(value);
}

public static class Program { public static Vehicle? ControlledVehicle; }

[Flags]
public enum ThrusterMapFlags
{
    None = 0, TranslateForward = 1, TranslateBackward = 2, TranslateRight = 4,
    TranslateLeft = 8, TranslateDown = 16, TranslateUp = 32,
}
public enum FlightComputerAttitudeMode { Manual, Auto }
public enum VehicleReferenceFrame { EclBody, Other }
public enum FlightComputerAttitudeTrackTarget { None, Custom, Other }
public enum FlightComputerManualThrustMode { Direct, Pulse }
public enum FlightComputerRollMode { Coupled, Decoupled, Up }
public enum FlightComputerRCSMode { Disabled, Enabled }
public enum KittenControlMode { View, Direct }
public enum LocomotionMode { Mmu, Other }
public sealed class LocomotionState { public LocomotionMode Mode = LocomotionMode.Mmu; }

public struct ManualControlInputs { public ThrusterMapFlags ThrusterCommandFlags; }
public sealed class ParentBody { public doubleQuat GetCce2Cci() => doubleQuat.Identity; }
public sealed class Orbit { public ParentBody Parent = new(); }
public static class VehicleReferenceFrameEx
{
    public static doubleQuat GetEclBody2Cci(doubleQuat value) => value;
    public static double3 QuaternionToEulerAngles(this VehicleReferenceFrame frame, doubleQuat value) => double3.Zero;
}

public sealed class VehicleConfigInfo { public bool HasRcs = true; }
public sealed class FlightComputer
{
    public FlightComputerAttitudeMode AttitudeMode = FlightComputerAttitudeMode.Manual;
    public VehicleReferenceFrame AttitudeFrame = VehicleReferenceFrame.Other;
    public FlightComputerAttitudeTrackTarget AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.Other;
    public double3 CustomAttitudeTarget = new(.3, -.4, .5);
    public FlightComputerManualThrustMode ManualThrustMode = FlightComputerManualThrustMode.Direct;
    public FlightComputerRollMode RollMode = FlightComputerRollMode.Coupled;
    public FlightComputerRCSMode RCSMode = FlightComputerRCSMode.Disabled;
    public float RateLimit = .37f;
    public readonly VehicleConfigInfo VehicleConfig = new();
    public void SetInertialNullRot()
    {
        AttitudeMode = FlightComputerAttitudeMode.Auto;
        AttitudeFrame = VehicleReferenceFrame.EclBody;
        AttitudeTrackTarget = FlightComputerAttitudeTrackTarget.None;
    }
}

public class Vehicle
{
    private ManualControlInputs _manualControlInputs;
    public ManualControlInputs ManualInputs
    {
        get => _manualControlInputs;
        set => _manualControlInputs = value;
    }
    public string Id = "fixture vessel";
    public bool IsDisposed, IsEditedVehicle;
    public bool HasPhysicsBubble = true;
    public ParentBody Parent = new();
    public readonly Orbit Orbit = new();
    public double BoundingSphereRadiusBody = 3;
    public double TotalMass = 10;
    public double3 Position, Velocity, BodyRates, CenterOfMassAsmb;
    public doubleQuat Body2Cci = doubleQuat.Identity;
    public Vehicle? Target;
    public object? ControlPart, ControlConnector;
    public readonly FlightComputer FlightComputer = new();
    public readonly PartTree Parts = new();
    public double3 GetPositionCci() => Position;
    public double3 GetVelocityCci() => Velocity;
    public doubleQuat GetBody2Cci() => Body2Cci;
}

public sealed class KittenEva : Vehicle
{
    public readonly LocomotionState LocomotionState = new();
    public KittenControlMode ControlMode = KittenControlMode.View;
    public void SetControlMode(KittenControlMode mode)
    {
        if (mode == ControlMode) return;
        ControlMode = mode;
        ManualInputs = default;
    }
}

public sealed class PartTree
{
    public readonly List<Part> Parts = [];
    public readonly ThrusterStates States = new();
}
public sealed class Part
{
    private static uint _nextId;
    public readonly uint InstanceId = ++_nextId;
    public readonly List<Part> SubParts = [];
    public readonly ModuleCollection Modules = new();
    public double4x4 MatrixAsmb2VehicleAsmb = double4x4.Identity;
    public Part? PartParent;
    public double3 Scale = double3.One, PositionParentAsmb;
    public doubleQuat Asmb2ParentAsmb = doubleQuat.Identity;
    public Part FullPart => PartParent?.FullPart ?? this;
}
public sealed class ModuleCollection
{
    private readonly List<object> _items = [];
    public void Add(object item) => _items.Add(item);
    public IEnumerable<T> Get<T>() => _items.OfType<T>();
}
public sealed class MeshReference
{
    public double3[][]? PositionsCompare;
    public MeshReference Get() => this;
}
public sealed class MeshViewModule(MeshReference mesh) { public MeshReference MeshView = mesh; }
public sealed class PartModelModule { public required PartModel PartModel; }
public sealed class PartModelDynamicModule { public required PartModel PartModelDynamic; }
public sealed class PartModelGlassModule { public required PartModel PartModelGlass; }
public sealed class PartModel { public required PartModelTemplate Template; }
public sealed class PartModelTemplate { public MeshReference? Mesh; }

public sealed class ThrusterController
{
    public bool IsActive = true;
    public double MinimumPulseTime = .03;
}
public sealed class ThrusterControllerState
{
    public bool IsPropellantAvailable = true;
    public double3 IntendedForce;
    public ThrusterMapFlags ControlMap;
}
public sealed class ThrusterControllerGlobalState { }
public struct EmptyStruct { }
public sealed class ThrusterStates
{
    public readonly List<ThrusterEntry> ModulesAndStates = [];
}
public readonly record struct ThrusterEntry(ThrusterController Module, ThrusterControllerState State);
public static class ModuleStateful<TModule, TState, TGlobal, TFx>
{
    public static ThrusterStates InitializeHotPathList(ThrusterStates states) => states;
}
