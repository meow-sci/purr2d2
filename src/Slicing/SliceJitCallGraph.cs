using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

namespace Purr2D2.Slicing;

/// <summary>Read closed call sites, never run their bodies or their type initializers.</summary>
internal static class SliceJitCallGraph
{
    internal sealed class Plan
    {
        internal readonly List<MethodBase> Methods = [];
        internal readonly List<string> Errors = [];
        internal int Failures, Skipped, Boundaries, IlBytes;
        internal bool Limited;
        internal void Failed(string name, Exception error)
        {
            Failures++;
            if (Errors.Count < 4) Errors.Add(name + ": " + error.Message);
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    internal static Plan Build(IEnumerable<MethodBase> roots, Func<MethodBase, bool> include,
        CancellationToken cancel, int maximumDepth, int maximumMethods, int maximumIlBytes, TimeSpan maximumTime)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMethods);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumIlBytes);
        var plan = new Plan();
        var seen = new HashSet<MethodBase>();
        var pending = new Queue<(MethodBase Method, byte[] Il, int Depth)>();
        var elapsed = Stopwatch.StartNew();
        // Enqueue every explicit root before walking. A wide branch cannot crowd
        // later phases (notably destruction and motion) out of the bounded plan.
        foreach (var root in roots)
        {
            cancel.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= maximumTime) { plan.Limited = true; break; }
            Add(root, 0);
        }
        while (pending.TryDequeue(out var item))
        {
            cancel.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= maximumTime) { plan.Limited = true; break; }
            if (item.Depth >= maximumDepth) continue;
            try
            {
                foreach (var callee in Calls(item.Method, item.Il, cancel, plan))
                {
                    cancel.ThrowIfCancellationRequested();
                    if (elapsed.Elapsed >= maximumTime) { plan.Limited = true; break; }
                    Add(callee, item.Depth + 1);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (Exception error) { plan.Failed(item.Method.DeclaringType?.FullName + "." + item.Method.Name, error); }
        }
        return plan;

        void Add(MethodBase method, int depth)
        {
            if (!seen.Add(method)) return;
            // P/Invoke stubs, abstract/interface bodies and generic definitions
            // are boundaries even when a caller explicitly asks to include them.
            if (method.IsConstructor && method.IsStatic || method.ContainsGenericParameters || method.IsAbstract
                || (method.Attributes & MethodAttributes.PinvokeImpl) != 0)
            { plan.Skipped++; return; }
            try
            {
                if (!include(method)) { plan.Boundaries++; return; }
                byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null) { plan.Skipped++; return; }
                if (plan.Methods.Count >= maximumMethods || il.Length > maximumIlBytes - plan.IlBytes)
                { plan.Limited = true; return; }
                plan.Methods.Add(method); plan.IlBytes += il.Length;
                pending.Enqueue((method, il, depth));
            }
            catch (Exception error) { plan.Failed(method.DeclaringType?.FullName + "." + method.Name, error); }
        }
    }

    private static IEnumerable<MethodBase> Calls(MethodBase caller, byte[] il, CancellationToken cancel, Plan plan)
    {
        Type[] typeArguments = caller.DeclaringType?.GetGenericArguments() ?? Type.EmptyTypes;
        Type[] methodArguments = caller.IsGenericMethod ? caller.GetGenericArguments() : Type.EmptyTypes;
        for (int offset = 0; offset < il.Length;)
        {
            cancel.ThrowIfCancellationRequested();
            short value = il[offset++];
            if (value == 0xfe)
            {
                Require(1);
                value = (short)(0xfe00 | il[offset++]);
            }
            if (!OpCodesByValue.TryGetValue(value, out var opcode)) throw new InvalidDataException("Unknown IL opcode.");
            int size = opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => SwitchSize(),
                _ => 4
            };
            Require(size);
            // Includes call/callvirt/newobj and delegate ldftn/ldvirtftn sites.
            // ResolveMethod's context is essential: preserve actual closed
            // Module<Tank>.List / collection / LINQ specializations from the IL.
            if (opcode.OperandType == OperandType.InlineMethod)
            {
                MethodBase? method = null;
                try { method = caller.Module.ResolveMethod(BitConverter.ToInt32(il, offset), typeArguments, methodArguments); }
                catch (Exception error) { plan.Failed(caller.Name + " callsite", error); }
                if (method != null) yield return method;
            }
            offset += size;

            void Require(int length)
            {
                if (length < 0 || length > il.Length - offset) throw new InvalidDataException("Truncated IL operand.");
            }
            int SwitchSize()
            {
                Require(4);
                int count = BitConverter.ToInt32(il, offset);
                if (count < 0 || count > (il.Length - offset - 4) / 4) throw new InvalidDataException("Invalid IL switch.");
                return 4 + count * 4;
            }
        }
    }
}
