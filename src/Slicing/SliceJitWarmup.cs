using System.Reflection;
using System.Runtime.CompilerServices;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Runtime;

namespace Purr2D2.Slicing;

/// <summary>Compile the measured cold isolation/swap path without executing game behavior.</summary>
internal static class SliceJitWarmup
{
    private static readonly BackgroundJitWarmup Attempt = new(Prepare,
        summary => Console.WriteLine("[purr2d2] Slice JIT warmup: " + summary));
    internal static bool Completed => Attempt.Completed;
    internal static void Start() => Attempt.Start();
    internal static void Cancel() => Attempt.Dispose();

    private static string Prepare(CancellationToken cancel)
    {
        // These initialize only mod-owned samplers/rings/formatters. In particular, do
        // not call native game methods, allocate a dummy vehicle or run a type initializer.
        int prepared = 0, skipped = 0;
        var failures = new List<string>();
        foreach (var initialize in new Action[] { SliceDiagnosticCounters.Warmup, SliceCommitTimings.Warmup, SliceFrameDiagnostics.Warmup })
        {
            cancel.ThrowIfCancellationRequested();
            try { initialize(); }
            catch (Exception error) { failures.Add(initialize.Method.DeclaringType!.Name + ": " + error.Message); }
        }
        var seen = new HashSet<MethodBase>();
        foreach (var target in Targets())
        {
            cancel.ThrowIfCancellationRequested();
            try
            {
                foreach (var method in target.Resolve())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!seen.Add(method)) continue;
                    // PrepareMethod is not recursive. Every selected closed body,
                    // including its selected generated lambdas/local functions, is explicit.
                    if (method.ContainsGenericParameters || method.IsAbstract || method.GetMethodBody() == null)
                    { skipped++; continue; }
                    try { RuntimeHelpers.PrepareMethod(method.MethodHandle); prepared++; }
                    catch (Exception error) { failures.Add(target.Name + ": " + error.Message); }
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception error) { failures.Add(target.Name + ": " + error.Message); }
        }
        return $"prepared {prepared} methods; skipped {skipped} open/abstract/bodyless methods; failed {failures.Count}"
            + (failures.Count == 0 ? "" : "; normal JIT fallback [" + string.Join(" | ", failures.Take(4)) + "]");
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
