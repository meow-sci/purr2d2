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
    private static void Main(string[] args)
    {
        bool baseline = args[0] == "--cold-baseline";
        if (baseline) args = args.Skip(1).ToArray();
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
        var definitions = (IEnumerable)warmup.GetMethod("Targets", HiddenStatic)!.Invoke(null, null)!;
        var targets = new List<MethodBase>();
        foreach (object target in definitions)
        {
            var resolve = (Delegate)target.GetType().GetProperty("Resolve")!.GetValue(target)!;
            targets.AddRange((IEnumerable<MethodBase>)resolve.DynamicInvoke()!);
        }
        targets = targets.Distinct().ToList();
        var replace = targets.Single(m => m.DeclaringType!.FullName == "Purr2D2.Slicing.NativeSlicePhysics" && m.Name == "TryReplace");
        var split = targets.Single(m => m.DeclaringType!.FullName == "KSA.Vehicle" && m.Name == "Split");
        string[] expected = ["KSA.Part+Connection", "KSA.Part+Connection+IConnector", "System.Double", "KSA.PoseChange&", "System.String"];
        Check(split.GetParameters().Select(p => p.ParameterType.FullName).SequenceEqual(expected), "actual current connection-based Vehicle.Split signature resolves exactly");
        Check(targets.All(m => m.DeclaringType!.Assembly == assembly || m == split), "the game target is confined to the measured native Split body");
        Check(targets.Count is > 20 and < 100, "the target list is bounded to the native transaction and its mod helpers");
        Check(targets.Any(m => m.Name.StartsWith("<SourceParts>g__Add", StringComparison.Ordinal)), "the recursive generated SourceParts local function is selected explicitly");
        Check(targets.Any(m => m.Name.StartsWith("<TryReplace>b__", StringComparison.Ordinal)), "the generated swap selectors are selected explicitly");
        Console.WriteLine($"Resolved {targets.Count} distinct target methods; open generic methods {targets.Count(m => m.ContainsGenericParameters)}.");

        _ = JitInfo.GetCompiledMethodCount(true); _ = JitInfo.GetCompiledILBytes(true); _ = JitInfo.GetCompilationTime(true);
        if (baseline)
        {
            Measure(replace, false); Measure(split, false);
            Console.WriteLine("PASS: cold baseline measured in a separate process without invoking game behavior.");
            return;
        }

        var start = warmup.GetMethod("Start", HiddenStatic)!;
        var completed = warmup.GetProperty("Completed", HiddenStatic)!;
        start.Invoke(null, null);
        for (int i = 0; i < 64; i++) start.Invoke(null, null);
        var deadline = Stopwatch.StartNew();
        while (!(bool)completed.GetValue(null)!)
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new Exception("Warmup completion timed out.");
            Thread.Sleep(1);
        }
        // JitInfo's thread-local counters isolate these calls from unrelated workers.
        Measure(replace, true);
        Measure(split, true);
        Console.WriteLine("PASS: production worker completed once; both measured cold bodies require zero additional JIT compilation; no game behavior invoked.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Measure(MethodBase method, bool requireWarm)
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
    }

    private static void Check(bool result, string description)
    {
        if (!result) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
