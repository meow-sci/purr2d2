using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Purr2D2.Slicing;

internal static class SliceJitCallGraphChecks
{
    private const BindingFlags Methods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static int _executed;

    internal static void Run()
    {
        ClosedCallSitesAndNoExecution();
        BoundsAndCancellation();
        FailureDoesNotStopOtherRoots();
        Console.WriteLine("PASS: bounded JIT graph preserves closed generics/delegates, respects boundaries/cancellation, and never invokes behavior");
    }

    private static SliceJitCallGraph.Plan Build(MethodBase[] roots, Func<MethodBase, bool>? include = null,
        int depth = 5, int methods = 64, int bytes = 32 * 1024, TimeSpan? time = null, CancellationToken cancel = default)
        => SliceJitCallGraph.Build(roots, include ?? Included, cancel, depth, methods, bytes, time ?? TimeSpan.FromSeconds(5));

    private static bool Included(MethodBase method) => method.DeclaringType != typeof(Blocked)
        && method.DeclaringType?.Assembly == typeof(SliceJitCallGraphChecks).Assembly;

    private static MethodInfo Get(Type type, string name) => type.GetMethod(name, Methods)!;

    private static void ClosedCallSitesAndNoExecution()
    {
        var entry = Get(typeof(Fixture<int>), nameof(Fixture<int>.Entry));
        var plan = Build([entry, entry, typeof(Fixture<int>).TypeInitializer!, Get(typeof(Fixture<>), nameof(Fixture<int>.Entry)),
            Get(typeof(SliceJitCallGraphChecks), nameof(NativeBoundary))]);
        Check(plan.Failures == 0 && !plan.Limited && plan.Methods.Distinct().Count() == plan.Methods.Count, "closed call graph is deduplicated and resolves without failures");
        Check(plan.Methods.Any(m => m.DeclaringType == typeof(Fixture<int>) && m.Name == nameof(Fixture<int>.Leaf)), "generic caller context closes child methods");
        Check(plan.Methods.Any(m => m.IsGenericMethod && m.GetGenericArguments().SequenceEqual([typeof(int)]) && m.Name == nameof(GenericLeaf)), "MethodSpec tokens preserve closed generic method arguments");
        Check(plan.Methods.Any(m => m.Name.Contains("<Entry>", StringComparison.Ordinal)), "delegate function-pointer targets are selected");
        Check(plan.Methods.All(m => !m.ContainsGenericParameters && !(m.IsConstructor && m.IsStatic)) && plan.Skipped >= 3,
            "open definitions, type initializers and native stubs are excluded");
        Check(plan.Boundaries > 0 && plan.Methods.All(m => m.DeclaringType != typeof(Blocked) && m.Name != nameof(ForbiddenLeaf)), "a denied boundary is never traversed");
        foreach (var method in plan.Methods) RuntimeHelpers.PrepareMethod(method.MethodHandle);
        Check(_executed == 0, "metadata traversal and PrepareMethod execute no fixture body, constructor or type initializer");
    }

    private static void BoundsAndCancellation()
    {
        var entry = Get(typeof(Fixture<int>), nameof(Fixture<int>.Entry));
        var laterPhase = Get(typeof(SliceJitCallGraphChecks), nameof(LaterPhase));
        var capped = Build([entry, laterPhase], methods: 2);
        Check(capped.Limited && capped.Methods.Count == 2 && capped.Methods.Contains(laterPhase), "all phase roots take precedence over a wide branch at the method cap");
        var shallow = Build([entry], depth: 0);
        Check(shallow.Methods.SequenceEqual([entry]), "zero depth selects the explicit root only");
        int entryBytes = entry.GetMethodBody()!.GetILAsByteArray()!.Length;
        var ilCapped = Build([entry], bytes: entryBytes);
        Check(ilCapped.Limited && ilCapped.IlBytes <= entryBytes, "selected IL remains within the byte budget");
        var timed = Build([entry], time: TimeSpan.Zero);
        Check(timed.Limited && timed.Methods.Count == 0, "the elapsed-time budget can stop before any body is selected");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { Build([entry], cancel: cancel.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private static void FailureDoesNotStopOtherRoots()
    {
        var entry = Get(typeof(Fixture<int>), nameof(Fixture<int>.Entry));
        var later = Get(typeof(SliceJitCallGraphChecks), nameof(LaterPhase));
        var plan = Build([entry, later], method => method == entry ? throw new MissingMethodException("fixture changed") : Included(method));
        Check(plan.Failures == 1 && plan.Errors.Single().Contains("fixture changed") && plan.Methods.Contains(later),
            "one incompatible target reports normal fallback while later phase roots remain usable");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception("JIT graph: " + message); }

    private static class Fixture<T>
    {
        static Fixture() { _executed++; }
        internal static void Entry()
        {
            Leaf(); GenericLeaf<T>();
            Action action = () => GenericLeaf<T>();
            action();
            switch (_executed) { case 0: Leaf(); break; case 4: GenericLeaf<T>(); break; default: Blocked.Touch(); break; }
            _ = new ConstructorFixture();
        }
        internal static void Leaf() { _executed++; GenericLeaf<T>(); }
    }
    private static void GenericLeaf<T>() { _executed += typeof(T).Name.Length; }
    private static void LaterPhase() { _executed++; }
    private static void ForbiddenLeaf() { _executed++; }
    private static class Blocked { internal static void Touch() => ForbiddenLeaf(); }
    private sealed class ConstructorFixture { internal ConstructorFixture() { _executed++; } }
    [DllImport("jit-warmup-fixture-must-never-load")]
    private static extern void NativeBoundary();
}
