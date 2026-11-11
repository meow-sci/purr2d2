using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

// Deliberately references only the BCL. Loading/compiling native method bodies does
// not require initializing the game, creating a universe or invoking vehicle APIs.
internal static class SliceWarmupProbe
{
    private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags HiddenInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Main(string[] args)
    {
        string mode = args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "--warm";
        bool baseline = mode == "--cold-baseline";
        if (mode != "--warm") args = args.Skip(1).ToArray();
        string modPath = Path.GetFullPath(args[0]);
        string[] paths = args.Skip(1).Select(Path.GetFullPath).ToArray();
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string path in paths)
            {
                string dll = Path.Combine(path, name.Name + ".dll");
                if (File.Exists(dll)) return context.LoadFromAssemblyPath(dll);
            }
            return null;
        };
        Console.WriteLine($"Runtime {Environment.Version}; architecture {RuntimeInformation.ProcessArchitecture}; actual mod {modPath}");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(modPath);
        var warmup = assembly.GetType("Purr2D2.Slicing.SliceJitWarmup", true)!;
        var start = warmup.GetMethod("Start", HiddenStatic)!;
        var completed = warmup.GetProperty("Completed", HiddenStatic)!;
        var cancel = warmup.GetMethod("Cancel", HiddenStatic)!;
        if (mode is "--cancel-before" or "--cancel-running")
        {
            if (mode == "--cancel-running") start.Invoke(null, null);
            var cancelling = Stopwatch.StartNew();
            cancel.Invoke(null, null);
            Check(cancelling.Elapsed < TimeSpan.FromMilliseconds(250), "production cancellation returns without joining the compiler worker");
            for (int i = 0; i < 64; i++) start.Invoke(null, null);
            Wait();
            Check((bool)completed.GetValue(null)!, mode + " safely releases readiness and prevents a second attempt");
            Console.WriteLine("PASS: cancellation used production entrypoints; no game behavior invoked.");
            return;
        }
        var definitions = (IEnumerable)warmup.GetMethod("Targets", HiddenStatic)!.Invoke(null, null)!;
        var roots = new List<MethodBase>();
        foreach (object target in definitions)
        {
            var resolve = (Delegate)target.GetType().GetProperty("Resolve")!.GetValue(target)!;
            roots.AddRange((IEnumerable<MethodBase>)resolve.DynamicInvoke()!);
        }
        roots = roots.Distinct().ToList();
        var plan = warmup.GetMethod("BuildPlan", HiddenStatic)!.Invoke(null, [CancellationToken.None, null, null])!;
        var targets = (List<MethodBase>)plan.GetType().GetField("Methods", HiddenInstance)!.GetValue(plan)!;
        int failures = Value<int>("Failures"), ilBytes = Value<int>("IlBytes");
        int cap = (int)warmup.GetField("MaximumMethods", HiddenStatic)!.GetRawConstantValue()!;
        int ilCap = (int)warmup.GetField("MaximumIlBytes", HiddenStatic)!.GetRawConstantValue()!;
        Console.WriteLine($"Graph selection: {targets.Count} methods/{ilBytes} IL bytes; failed {failures}; limited {Value<bool>("Limited")}; errors: "
            + string.Join(" | ", Value<List<string>>("Errors")));
        Check(failures == 0, "all current snapshot roots and callsite tokens resolve");
        Check(!Value<bool>("Limited") && targets.Count <= cap && ilBytes <= ilCap,
            "current snapshot reaches the targeted graph within method, IL-byte and elapsed-time budgets");
        Check(targets.Count > roots.Count * 2 && targets.Count > 100, "IL traversal reaches meaningful deeper callees beyond the explicit roots");
        Check(targets.All(m => !m.ContainsGenericParameters && !m.IsAbstract && !(m.IsConstructor && m.IsStatic)
            && (m.Attributes & MethodAttributes.PinvokeImpl) == 0), "only closed managed bodies are selected; no type initializer or P/Invoke is prepared");
        var allowed = warmup.GetMethod("IncludeCallee", HiddenStatic)!;
        Check(targets.All(m => (bool)allowed.Invoke(null, [m])!), "every selected body stays inside the production allowlist");
        var programFrame = GameAssembly().GetType("KSA.Program", true)!.GetMethod("OnFrame",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)!;
        var readFile = typeof(File).GetMethod(nameof(File.ReadAllText), [typeof(string)])!;
        Check(!(bool)allowed.Invoke(null, [programFrame])! && !(bool)allowed.Invoke(null, [readFile])!,
            "actual application-frame and file-IO methods are rejected as traversal boundaries");
        Check(!targets.Any(m => m.DeclaringType!.FullName is "KSA.Program" or "System.IO.File" or "System.Reflection.MethodBase"),
            "whole application, IO and reflection execution remain traversal boundaries");
        var replace = targets.Single(m => m.DeclaringType!.FullName == "Purr2D2.Slicing.NativeSlicePhysics" && m.Name == "TryReplace");
        var split = targets.Single(m => m.DeclaringType!.FullName == "KSA.Vehicle" && m.Name == "Split");
        string[] expected = ["KSA.Part+Connection", "KSA.Part+Connection+IConnector", "System.Double", "KSA.PoseChange&", "System.String"];
        Check(split.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(expected), "actual current connection-based Vehicle.Split signature resolves exactly");
        Check(targets.Any(m => m.Name.StartsWith("<SourceParts>g__Add", StringComparison.Ordinal)), "the recursive generated SourceParts local function is selected explicitly");
        Check(targets.Any(m => m.Name.StartsWith("<TryReplace>b__", StringComparison.Ordinal)), "the generated swap selectors are selected explicitly");
        Check(targets.Any(m => m.IsGenericMethod && m.GetGenericArguments().Any(t => t.FullName == "KSA.Tank")
            || m.DeclaringType!.GetGenericArguments().Any(t => t.FullName == "KSA.Tank")), "native module access is resolved as a closed Tank specialization");
        Check(targets.Any(m => m.DeclaringType!.Namespace == "System.Collections.Generic" && m.DeclaringType.GetGenericArguments().Any(t => t.FullName == "KSA.Vehicle")),
            "closed collections used by the native transaction are included");
        var representative = new List<MethodBase> { replace, split };
        Add("KSA.Vehicle", "UpdateVehicleConfiguration");
        Add("KSA.Vehicle", "UpdateAfterPartTreeModification");
        Add("KSA.VehicleUpdateState", "InitializeUpdateData");
        Add("KSA.VehicleUpdateData", ".ctor");
        Add("KSA.Vehicle", "CreateOrUpdateColliderCompound");
        Add("KSA.PartTree", "ReinitializeDerivedValues");
        Add("KSA.Universe", "DestroyVehicle");
        Add("KSA.Vehicle", "Dispose", m => m.GetParameters().Length == 1);
        Add("KSA.BepuHandles", "DisposeTerrainShapes");
        Add("BepuPhysics.Collidables.Shapes", "RecursivelyRemoveAndDispose");
        Add("BepuPhysics.Collidables.BigCompound", "Dispose");
        Add("BepuPhysics.Trees.Tree", "Dispose");
        Add("KSA.ColliderModule", "Dispose");
        Add("KSA.FxDeformation", "Dispose");
        Add("KSA.PhysicsStates", "ComputeOrbit");
        Add("KSA.VehicleReferenceFrameEx", "CciStatesToCcf");
        Add("Purr2D2.Runtime.SliceWorkerHooks", "EnterStage");
        Add("Purr2D2.Slicing.SliceWorkerDiagnostics", "BeginChild");
        Add("Purr2D2.Slicing.SliceDebrisClassification", "MarkSeparated");
        Add("Purr2D2.Slicing.SliceSourceMass", "Resolve");
        var tankList = targets.Single(m => m.DeclaringType!.Name == "List" && m.DeclaringType.GetGenericArguments().Any(t => t.FullName == "KSA.Tank") && m.Name == "Dispose");
        representative.Add(tankList);
        Console.WriteLine($"Resolved {roots.Count} roots -> {targets.Count} distinct managed bodies, {ilBytes} IL bytes; closed generic bodies {targets.Count(m => m.IsGenericMethod || m.DeclaringType!.IsConstructedGenericType)}; boundaries {Value<int>("Boundaries")}; caps {cap} methods/{ilCap} bytes.");
        foreach (var group in targets.GroupBy(m => m.DeclaringType!.Assembly.GetName().Name).OrderBy(g => g.Key)) Console.WriteLine($"  {group.Key}: {group.Count()} bodies");

        _ = JitInfo.GetCompiledMethodCount(true); _ = JitInfo.GetCompiledILBytes(true); _ = JitInfo.GetCompilationTime(true);
        if (baseline)
        {
            var cold = representative.Select(method => Measure(method, false)).ToArray();
            Check(cold.Skip(2).Count(count => count != 0) >= 3, "multiple deeper native bodies are independently cold in the fresh baseline process");
            Console.WriteLine("PASS: cold baseline measured in a separate process without invoking game behavior.");
            return;
        }

        // The production summary is opt-in telemetry. Enable it in this isolated
        // probe so the runner can verify every selected body was actually prepared.
        // No user settings or gameplay state are changed by the probe process.
        assembly.GetType("Purr2D2.Core.DebugTimings", true)!
            .GetField("Enabled", HiddenStatic)!.SetValue(null, true);
        start.Invoke(null, null);
        for (int i = 0; i < 64; i++) start.Invoke(null, null);
        Wait();
        // JitInfo's thread-local counters isolate these calls from unrelated workers.
        foreach (var method in representative) Measure(method, true);
        Console.WriteLine("PASS: production worker completed once; deeper split/configuration/destruction/motion and closed module disposal bodies require zero additional JIT; no game behavior invoked.");

        T Value<T>(string name) => (T)plan.GetType().GetField(name, HiddenInstance)!.GetValue(plan)!;
        Assembly GameAssembly() => roots.Single(m => m.DeclaringType!.FullName == "KSA.Vehicle" && m.Name == "Split").DeclaringType!.Assembly;
        void Add(string owner, string name, Func<MethodBase, bool>? match = null)
        {
            var selected = targets.Where(m => m.DeclaringType!.FullName == owner && m.Name == name && (match?.Invoke(m) ?? true)).ToArray();
            Check(selected.Length != 0, owner + "." + name + " is reached");
            representative.AddRange(selected);
        }
        void Wait()
        {
            var deadline = Stopwatch.StartNew();
            while (!(bool)completed.GetValue(null)!)
            {
                if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new Exception("Warmup completion timed out.");
                Thread.Sleep(1);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(MethodBase method, bool requireWarm)
    {
        long beforeCount = JitInfo.GetCompiledMethodCount(true), beforeBytes = JitInfo.GetCompiledILBytes(true);
        TimeSpan beforeJit = JitInfo.GetCompilationTime(true);
        long before = Stopwatch.GetTimestamp();
        RuntimeHelpers.PrepareMethod(method.MethodHandle);
        double wall = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
        long count = JitInfo.GetCompiledMethodCount(true) - beforeCount, bytes = JitInfo.GetCompiledILBytes(true) - beforeBytes;
        double jit = (JitInfo.GetCompilationTime(true) - beforeJit).TotalMilliseconds;
        Console.WriteLine($"{(requireWarm ? "AFTER WARMUP" : "COLD BASELINE")} {method.DeclaringType!.Name}.{method.Name}: wall={wall:F3} ms; JIT={jit:F3} ms; methods={count}; ILbytes={bytes}");
        if (requireWarm) Check(count == 0 && bytes == 0, method.DeclaringType.Name + "." + method.Name + " is already compiled");
        return count;
    }

    private static void Check(bool result, string description)
    {
        if (!result) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
