using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Runtime;

namespace Purr2D2.Slicing;

/// <summary>Compile the measured cold isolation/swap path without executing game behavior.</summary>
internal static class SliceJitWarmup
{
    internal const int MaximumDepth = 4, MaximumMethods = 1800, MaximumIlBytes = 768 * 1024;
    private static readonly TimeSpan MaximumTime = TimeSpan.FromSeconds(8);
    private static readonly BackgroundJitWarmup Attempt = new(Prepare,
        summary => Console.WriteLine("[purr2d2] Slice JIT warmup: " + summary));
    internal static bool Completed => Attempt.Completed;
    internal static void Start() => Attempt.Start();
    internal static void Cancel() => Attempt.Dispose();

    private static string Prepare(CancellationToken cancel)
    {
        // These initialize only mod-owned samplers/rings/formatters. In particular, do
        // not call native game methods, allocate a dummy vehicle or run a type initializer.
        var elapsed = Stopwatch.StartNew();
        int prepared = 0;
        var resolution = new SliceJitCallGraph.Plan();
        foreach (var initialize in new Action[] { SliceDiagnosticCounters.Warmup, SliceCommitTimings.Warmup,
            SliceFrameDiagnostics.Warmup, SliceWorkerDiagnostics.Warmup })
        {
            cancel.ThrowIfCancellationRequested();
            try { initialize(); }
            catch (Exception error) { resolution.Failed(initialize.Method.DeclaringType!.Name, error); }
        }
        var plan = BuildPlan(cancel, resolution, MaximumTime - elapsed.Elapsed);
        foreach (var method in plan.Methods)
        {
            cancel.ThrowIfCancellationRequested();
            // Check between individual preparations. Unload never waits for the
            // one body currently being compiled; the worker observes cancellation
            // before the next body. Tiered/virtual dispatch may still JIT later.
            if (elapsed.Elapsed >= MaximumTime) { plan.Limited = true; break; }
            try { RuntimeHelpers.PrepareMethod(method.MethodHandle); prepared++; }
            catch (Exception error) { plan.Failed(method.DeclaringType!.Name + "." + method.Name, error); }
        }
        return $"prepared {prepared} methods; skipped {plan.Skipped} open/abstract/bodyless methods; failed {plan.Failures}"
            + $"; targeted graph {plan.Methods.Count} bodies/{plan.IlBytes} IL bytes, depth {MaximumDepth}, boundaries {plan.Boundaries}"
            + (plan.Limited ? "; budget reached; normal JIT fallback" : "; within budget")
            + (plan.Failures == 0 ? "" : "; normal JIT fallback [" + string.Join(" | ", plan.Errors) + "]");
    }

    // Shared with the real-assembly probe: selecting a plan is reflection/IL only.
    private static SliceJitCallGraph.Plan BuildPlan(CancellationToken cancel, SliceJitCallGraph.Plan? resolution = null,
        TimeSpan? maximumTime = null)
    {
        resolution ??= new();
        var plan = SliceJitCallGraph.Build(ResolveRoots(), IncludeCallee, cancel,
            MaximumDepth, MaximumMethods, MaximumIlBytes, maximumTime ?? MaximumTime);
        plan.Failures += resolution.Failures;
        foreach (string error in resolution.Errors) if (plan.Errors.Count < 4) plan.Errors.Add(error);
        return plan;

        IEnumerable<MethodBase> ResolveRoots()
        {
            foreach (var target in Targets())
            {
                cancel.ThrowIfCancellationRequested();
                MethodBase[] methods;
                try { methods = target.Resolve().ToArray(); }
                catch (Exception error) { resolution.Failed(target.Name, error); continue; }
                foreach (var method in methods) yield return method;
            }
        }
    }

    private readonly record struct Target(string Name, Func<IEnumerable<MethodBase>> Resolve);
    private const BindingFlags Methods = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Static | BindingFlags.Instance;

    private static IEnumerable<Target> Targets()
    {
        // The probe measured ~16 ms in TryReplace and ~9 ms in this connection
        // overload of Split. Compile their real bodies; never invoke them here.
        yield return Named(typeof(NativeSlicePhysics), "TryReplace");
        yield return new("Vehicle.Split(Connection, IConnector, double, out PoseChange, string)", () =>
            [typeof(Vehicle).GetMethod(nameof(Vehicle.Split), Methods, null,
                [typeof(Part.Connection), typeof(Part.Connection.IConnector), typeof(double), typeof(PoseChange).MakeByRefType(), typeof(string)], null)
                ?? throw new MissingMethodException("The native connection-based Vehicle.Split overload is unavailable.")]);
        // Resolve/compile measured native phases before optional frame diagnostics.
        foreach (var target in NativeTargets()) yield return target;
        yield return Selected("Purr2D2.Runtime.SliceWorkerHooks", ["DispatchPrefix", "RunPrefix", "RunFinalizer",
            "ChildPrefix", "ChildFinalizer", "EnterStage"], typeof(SliceJitWarmup).Assembly);
        yield return Selected("Purr2D2.Slicing.SliceWorkerDiagnostics", ["Dispatch", "BeginRun", "EndRun", "EnterStage",
            "BeginChild", "EndChild", "Joined", "Reset"], typeof(SliceJitWarmup).Assembly);

        string[] physics = ["TryIsolate", "ValidateSource", "ValidatePreparedFragments", "SourceMass", "SourceParts",
            "ValidateTree", "CanBake", "Pair", "SplitBoundary", "RollBackFragment"];
        foreach (string name in physics) yield return Named(typeof(NativeSlicePhysics), name);
        yield return Named(typeof(IsolatedSliceSource), "IsCurrent");
        yield return Named(typeof(SliceSourceCapture), "IsCurrent");
        yield return Named(typeof(SliceSourceCapture), "Models");
        yield return Named(typeof(SliceSourceFrame), "SameTransform");
        foreach (string name in new[] { "Tensor", "ParallelAxis", "ApplyTensor", "TryAngularVelocity" })
            yield return Named(typeof(SlicePrincipalFrame), name);
        yield return Named(typeof(ArbitrarySlicer), "Isolate");
        yield return Named(typeof(ArbitrarySlicer), "Commit");
        foreach (string name in new[] { "WaitVehicleSolver", "FlushDirtyDerived", "FlushDirtyResources", "PrepareAndDispatchVehicles", "SettledUpdate", "Frame" })
            yield return Named(typeof(GameHooks), name);
        yield return new("NativeSlicePhysics.IsolationBulkChanges", () =>
        {
            var type = typeof(NativeSlicePhysics).GetNestedType("IsolationBulkChanges", BindingFlags.NonPublic)
                ?? throw new MissingMemberException("Native isolation staging helper is unavailable.");
            return type.GetConstructors(Methods).Cast<MethodBase>().Concat(type.GetMethods(Methods)
                .Where(m => m.Name is "Include" or "Dispose"));
        });
        // This is a bounded selection of generated children of the methods above,
        // not an assembly scan. Record equality/printing and capture/export workers
        // are unrelated to the native transaction and deliberately absent.
        yield return Generated(typeof(NativeSlicePhysics), physics.Append("TryReplace").ToArray());
        yield return Generated(typeof(IsolatedSliceSource), ["IsCurrent"]);
    }

    private static IEnumerable<Target> NativeTargets()
    {
        yield return Selected("KSA.Vehicle", ["UpdateVehicleConfiguration", "UpdateAfterPartTreeModification",
            "UpdateCollisionGeometry", "AdoptSeparatedVehicle", "Dispose", "RemoveFromCurrentBubble", "RemoveFromBubble",
            "SetFlightPlan", "UpdatePerFrameData", "set_BodyRates", "DepleteConsumables"]);
        yield return Selected("KSA.Universe", ["DestroyVehicle"]);
        yield return Selected("KSA.PartTree", ["Split", "ReinitializeDerivedValues", "RecomputeAllDerivedData",
            "SetRootPose", "BeginBulkChange", "EndBulkChange", "Dispose"]);
        yield return Selected("KSA.Part", ["Dispose", "PartitionSymmetryLinksByTree"]);
        yield return Selected("KSA.Part+Connection", ["Disconnect"]);
        yield return Selected("KSA.VehicleUpdateState", ["InitializeUpdateData", "Dispose"]);
        yield return Selected("KSA.BepuHandles", ["Dispose", "DisposeTerrainShapes"]);
        yield return Selected("KSA.ModuleList", ["Dispose"]);
        yield return Selected("KSA.PartTreeRenderData", ["Release"]);
        yield return Selected("KSA.PhysicsStates", ["GetStatesCci", "ComputeOrbit", "UpdateFromAnalytic"]);
        yield return Selected("KSA.VehicleReferenceFrameEx", ["CciStatesToCcf"]);
        foreach (var target in ColliderTargets()) yield return target;
        // ModuleList.Dispose dispatches through IModuleTypeList. Include concrete
        // lists used by tanks/structural wreckage without scanning game assemblies.
        foreach (string module in new[] { "Tank", "InertMass", "ColliderModule", "Control", "FxDeformation" })
        {
            string name = module;
            yield return new("Module<" + name + ">.List.Dispose", () =>
            {
                var type = NativeType("KSA.Module`1+List").MakeGenericType(NativeType("KSA." + name));
                var moduleType = NativeType("KSA." + name);
                return [type.GetMethod("Dispose", Methods) ?? throw new MissingMethodException(type.FullName, "Dispose"),
                    moduleType.GetMethod("Dispose", Methods & ~BindingFlags.DeclaredOnly)
                        ?? throw new MissingMethodException(moduleType.FullName, "Dispose")];
            });
        }
    }

    private static IEnumerable<Target> ColliderTargets()
    {
        // Vehicle.UpdateCollisionGeometry and BepuHandles.Dispose call these
        // exact compound/terrain teardown paths. ShapeBatch uses virtual dispatch,
        // so explicitly close its compound implementations as well.
        var assembly = typeof(BepuPhysics.Collidables.Shapes).Assembly;
        yield return Selected("BepuPhysics.Collidables.Shapes", ["RemoveAndDispose", "RecursivelyRemoveAndDispose"], assembly);
        yield return Selected("BepuPhysics.Collidables.CompoundBuilder", ["BuildKinematicCompound", "Dispose"], assembly);
        foreach (var shape in new[] { typeof(BepuPhysics.Collidables.Compound), typeof(BepuPhysics.Collidables.BigCompound) })
        {
            yield return Selected(shape.FullName!, ["Dispose", "CreateShapeBatch"], assembly);
            Type closed = assembly.GetType("BepuPhysics.Collidables.CompoundShapeBatch`1", true)!.MakeGenericType(shape);
            yield return new(closed.Name + " compound disposal", () => closed.GetMethods(Methods)
                .Where(m => m.Name is "Dispose" or "RemoveAndDisposeChildren"));
        }
    }

    private static Type NativeType(string name) => typeof(Vehicle).Assembly.GetType(name, true)!;

    private static Target Selected(string typeName, string[] names, Assembly? assembly = null) => new(typeName + " selected phase methods", () =>
    {
        var type = (assembly ?? typeof(Vehicle).Assembly).GetType(typeName, true)!;
        var methods = type.GetMethods(Methods).Where(m => names.Contains(m.Name, StringComparer.Ordinal)).ToArray();
        var missing = names.Where(n => !methods.Any(m => m.Name == n)).ToArray();
        if (missing.Length != 0) throw new MissingMethodException(typeName + ": " + string.Join(", ", missing));
        return methods;
    });

    private static bool IncludeCallee(MethodBase method)
    {
        var type = method.DeclaringType;
        if (type == null) return false;
        Type owner = type;
        while (owner.DeclaringType != null) owner = owner.DeclaringType;
        string name = owner.FullName ?? "";
        if (owner.Assembly == typeof(SliceJitWarmup).Assembly)
        {
            if (name == "Purr2D2.Runtime.SliceWorkerHooks") return method.Name is "DispatchPrefix" or "RunPrefix" or "RunFinalizer"
                or "ChildPrefix" or "ChildFinalizer" or "EnterStage";
            return name is "Purr2D2.Slicing.NativeSlicePhysics" or "Purr2D2.Slicing.IsolatedSliceSource"
                or "Purr2D2.Slicing.SliceSourceCapture" or "Purr2D2.Slicing.SliceSourceFrame" or "Purr2D2.Slicing.SlicePrincipalFrame"
                or "Purr2D2.Slicing.ArbitrarySlicer" or "Purr2D2.Runtime.GameHooks"
                or "Purr2D2.Slicing.SliceCommitTimings" or "Purr2D2.Slicing.SliceFrameDiagnostics"
                or "Purr2D2.Slicing.SliceDiagnosticRing" or "Purr2D2.Slicing.SliceCounterSnapshot"
                or "Purr2D2.Slicing.SliceCounterDelta" or "Purr2D2.Slicing.SliceDiagnosticCounters"
                or "Purr2D2.Slicing.SliceWorkerDiagnostics" or "Purr2D2.Slicing.SliceWorkerStamp"
                or "Purr2D2.Slicing.SliceWorkerDelta" or "Purr2D2.Slicing.SliceWorkerMetric" or "Purr2D2.Slicing.SliceWorkerSample"
                or "Purr2D2.Slicing.SliceWorkerRunToken" or "Purr2D2.Slicing.SliceWorkerScopeToken";
        }
        if (owner.Assembly == typeof(Vehicle).Assembly)
        {
            // Universe/Program traversal would reach the whole application. Only
            // the transaction's directly relevant global handoff methods qualify.
            if (name == "KSA.Universe") return method.Name is "DestroyVehicle" or "HandOffCameras" or "ReleasePortraitCamera" or "get_CurrentSystem";
            return name is "KSA.Vehicle" or "KSA.Part" or "KSA.PartTree" or "KSA.PartTreeRenderData"
                or "KSA.VehicleUpdateState" or "KSA.VehicleUpdateData" or "KSA.VehicleProperties"
                or "KSA.PhysicsStates" or "KSA.ReadOnlyPhysicsStates" or "KSA.KinematicStates" or "KSA.BubbleOrigin"
                or "KSA.ModuleList" or "KSA.ModuleStateList" or "KSA.Module`1" or "KSA.ModuleStateful`4"
                or "KSA.FlightPlan" or "KSA.PatchedConic" or "KSA.Orbit" or "KSA.StateVectors"
                or "KSA.VehicleReferenceFrameEx" or "KSA.SituationEx" or "KSA.OffsetMassProperties" or "KSA.MassProperties"
                or "KSA.CollisionAvoidancePair" or "KSA.BepuHandles" or "KSA.ShapesUnlock"
                or "KSA.SequenceList" or "KSA.SequencePerformanceList"
                or "KSA.Tank" or "KSA.InertMass" or "KSA.ColliderModule" or "KSA.Control" or "KSA.FxDeformation";
        }
        // Math bodies and only the closed container/LINQ/buffer instantiations
        // actually called by selected transaction bodies. No reflection, IO,
        // task scheduling, logging, renderer or general BCL graph traversal.
        if (type.Namespace == "Brutal.Numerics" || (type.Namespace ?? "").StartsWith("BepuUtilities", StringComparison.Ordinal)) return true;
        if (type.Assembly == typeof(BepuPhysics.Collidables.Shapes).Assembly)
            return name is "BepuPhysics.Collidables.Shapes" or "BepuPhysics.Collidables.CompoundBuilder"
                or "BepuPhysics.Collidables.Compound" or "BepuPhysics.Collidables.BigCompound"
                or "BepuPhysics.Collidables.Box" or "BepuPhysics.Collidables.Triangle"
                or "BepuPhysics.Collidables.ShapeBatch" or "BepuPhysics.Collidables.ShapeBatch`1"
                or "BepuPhysics.Collidables.CompoundShapeBatch`1" or "BepuPhysics.Collidables.TypedIndex"
                or "BepuPhysics.RigidPose" or "BepuPhysics.Trees.Tree";
        if (type == typeof(Enumerable)) return true;
        string ns = type.Namespace ?? "";
        return (type == typeof(Array) || ns.StartsWith("System.Collections.Generic", StringComparison.Ordinal)
                || ns == "System.Linq" || ns == "Brutal.Collections" || ns == "CommunityToolkit.HighPerformance.Buffers")
            && (type.GetGenericArguments().Any(RelevantArgument)
                || method.IsGenericMethod && method.GetGenericArguments().Any(RelevantArgument));
    }

    private static bool RelevantArgument(Type type) => type.Assembly == typeof(Vehicle).Assembly
        || type.Assembly == typeof(SliceJitWarmup).Assembly || type.Namespace == "Brutal.Numerics"
        || type.GetGenericArguments().Any(RelevantArgument);

    private static Target Named(Type type, string name) => new(type.Name + "." + name, () =>
        [type.GetMethod(name, Methods) ?? throw new MissingMethodException(type.FullName, name)]);

    private static Target Generated(Type owner, string[] names) => new(owner.Name + " selected generated helpers", () =>
    {
        var selected = new List<MethodBase>();
        foreach (var type in owner.GetNestedTypes(BindingFlags.NonPublic))
        {
            if (!type.IsDefined(typeof(CompilerGeneratedAttribute), false)) continue;
            var methods = type.GetMethods(Methods).Where(m => names.Any(n => m.Name.StartsWith("<" + n + ">", StringComparison.Ordinal))).ToArray();
            if (methods.Length == 0) continue;
            selected.AddRange(methods);
            selected.AddRange(type.GetConstructors(Methods));
        }
        // Captured local functions may be emitted directly on the owner rather
        // than its generated closure type, depending on the compiler's lowering.
        selected.AddRange(owner.GetMethods(Methods).Where(m => m.IsDefined(typeof(CompilerGeneratedAttribute), false)
            && names.Any(n => m.Name.StartsWith("<" + n + ">", StringComparison.Ordinal))));
        return selected;
    });
}
