using System.Reflection;
using System.Reflection.Emit;
using Brutal.Concurrency.Jobs;
using HarmonyLib;
using KSA;
using KSA.Concurrency;
using Purr2D2.Slicing;

namespace Purr2D2.Runtime;

/// <summary>Optional diagnostics only. This patch group never changes scheduling, profiler flags,
/// game state or native exceptions. All selected child work is joined before Run returns.</summary>
internal static class SliceWorkerHooks
{
    private static Dictionary<MethodBase, SliceWorkerStage> _childStages = new();
    private static AccessTools.FieldRef<BepuWorkerDispatcher, DynamicWorkerPool>? _dispatcherPool;
    private static Harmony? _harmony;

    internal static void Install(Harmony owner)
    {
        Uninstall();
        var harmony = new Harmony(owner.Id + ".slice-worker-diagnostics");
        _harmony = harmony;
        try
        {
            var run = Require(typeof(VehicleUpdateTask), nameof(VehicleUpdateTask.Run));
            // Validate the production transformation before installing any part of the group.
            _ = InsertRunStages(PatchProcessor.GetOriginalInstructions(run)).ToList();
            var children = ChildTargets();
            _dispatcherPool = AccessTools.FieldRefAccess<BepuWorkerDispatcher, DynamicWorkerPool>("_pool");
            Volatile.Write(ref _childStages, children.ToDictionary(x => (MethodBase)x.Method, x => x.Stage));
            harmony.Patch(Require(typeof(JobScheduler), nameof(JobScheduler.ExecuteJobs)),
                prefix: new HarmonyMethod(typeof(SliceWorkerHooks), nameof(DispatchPrefix)));
            harmony.Patch(run, prefix: new HarmonyMethod(typeof(SliceWorkerHooks), nameof(RunPrefix)),
                finalizer: new HarmonyMethod(typeof(SliceWorkerHooks), nameof(RunFinalizer)),
                transpiler: new HarmonyMethod(typeof(SliceWorkerHooks), nameof(InsertRunStages)));
            foreach (var (method, _) in children)
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(SliceWorkerHooks), nameof(ChildPrefix)),
                    finalizer: new HarmonyMethod(typeof(SliceWorkerHooks), nameof(ChildFinalizer)));
            SliceWorkerDiagnostics.Supported = true;
            Console.WriteLine("[purr2d2] Slice physics-worker diagnostics available (bounded dispatch windows; selected joined child scopes).");
        }
        catch (Exception error)
        {
            Uninstall();
            Console.WriteLine("[purr2d2] Slice physics-worker diagnostics unavailable: " + error.GetBaseException().Message);
        }
    }

    internal static void Uninstall()
    {
        SliceWorkerDiagnostics.Supported = false;
        SliceWorkerDiagnostics.Reset();
        try { _harmony?.UnpatchAll(_harmony.Id); _harmony = null; }
        catch (Exception error)
        {
            // Capture is already disabled and its epoch cancelled. Keep the owner for
            // a later cleanup retry; optional diagnostics must not prevent gameplay cleanup.
            Console.WriteLine("[purr2d2] Slice physics-worker diagnostic cleanup failed: " + error.GetBaseException().Message);
        }
        // Keep immutable metadata mappings for callbacks already inside a native method.
        // No engine instances are retained; later Install replaces them before publishing support.
    }

    internal static (MethodInfo Method, SliceWorkerStage Stage)[] ChildTargets() =>
    [
        (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.BeginStepFrame)), SliceWorkerStage.BubbleBegin),
        (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.StepFrameTo)), SliceWorkerStage.BubbleStep),
        (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.EndStepFrame)), SliceWorkerStage.BubbleEnd),
        (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.RunPostWorkAndCluster)), SliceWorkerStage.BubblePostWork),
        (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.RunVehiclePostWork)), SliceWorkerStage.VehiclePostWork),
        (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.RunCluster)), SliceWorkerStage.Cluster),
        (Require(typeof(PhysicsBubble), "BeginBubbleTerrainStep"), SliceWorkerStage.Terrain),
        (Require(typeof(PhysicsBubble), "UpdateBubbleTerrainBlocks"), SliceWorkerStage.Terrain),
        (Require(typeof(ConstraintSim), nameof(ConstraintSim.UpdateTerrainForNextStep)), SliceWorkerStage.Terrain),
        (Require(typeof(PhysicsBubble), "SnapOriginToLeader"), SliceWorkerStage.Origin),
        (Require(typeof(PhysicsBubble), "FullPhysicsConstrainedStep"), SliceWorkerStage.ConstrainedStep),
        (Require(typeof(PhysicsBubble), "FullPhysicsUnconstrainedStep"), SliceWorkerStage.UnconstrainedStep),
        (Require(typeof(ConstraintSim), nameof(ConstraintSim.DetectCollisions)), SliceWorkerStage.CollisionDetection),
        (Require(typeof(ConstraintSim), nameof(ConstraintSim.Simulate)), SliceWorkerStage.ConstraintSimulation),
        // RunBody has a function-pointer local that Harmony/MonoMod cannot copy on
        // this host. The concrete interface entry covers remote joined workers;
        // caller/fallback bodies remain inside the enclosing simulation scope.
        (Require(typeof(BepuWorkerDispatcher), typeof(IBepuDispatch).FullName + ".RunWorker"), SliceWorkerStage.BepuWorker)
    ];

    private static MethodInfo Require(Type type, string name)
    {
        var methods = AccessTools.GetDeclaredMethods(type).Where(method => method.Name == name).ToArray();
        if (methods.Length != 1 || methods[0].ContainsGenericParameters || methods[0].ReturnType != typeof(void)
            || methods[0].GetMethodBody() == null)
            throw new InvalidOperationException("Unsupported physics diagnostic seam: " + type.FullName + "." + name);
        return methods[0];
    }

    internal static IEnumerable<CodeInstruction> InsertRunStages(IEnumerable<CodeInstruction> instructions)
    {
        var codes = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
        (MethodInfo Method, SliceWorkerStage Stage)[] targets =
        [
            (Require(typeof(VehicleUpdateTask), "TrimBubbles"), SliceWorkerStage.TrimBubbles),
            (Require(typeof(VehicleUpdateTask), "AssignOrphanVehicles"), SliceWorkerStage.AssignOrphans),
            (Require(typeof(VehicleUpdateTask), "MergeBubbles"), SliceWorkerStage.MergeBubbles),
            (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.Prepare)), SliceWorkerStage.PrepareBubbles),
            (Require(typeof(VehicleUpdateTask), "BuildContactIslands"), SliceWorkerStage.BuildIslands),
            (FindIslandBatch(), SliceWorkerStage.IslandBatch),
            (Require(typeof(VehicleUpdateTask), "CommitIslandClearances"), SliceWorkerStage.CommitClearances),
            (Require(typeof(VehicleUpdateTask), "ReassignDivergentVehicles"), SliceWorkerStage.ReassignVehicles),
            (Require(typeof(VehicleUpdateTask), "SplitBubbles"), SliceWorkerStage.SplitBubbles),
            (Require(typeof(PhysicsBubble), nameof(PhysicsBubble.PublishResults)), SliceWorkerStage.PublishResults)
        ];
        var insertions = new List<(int Index, SliceWorkerStage Stage)>();
        int previous = -1;
        foreach (var (method, stage) in targets)
        {
            int index = codes.FindIndex(instruction => instruction.Calls(method));
            if (index <= previous || codes.Count(instruction => instruction.Calls(method)) != 1
                || codes[index].opcode != OpCodes.Call && codes[index].opcode != OpCodes.Callvirt
                || index > 0 && codes[index - 1].opcode.OpCodeType == OpCodeType.Prefix)
                throw new InvalidOperationException("Unsupported VehicleUpdateTask.Run diagnostic stage: " + stage);
            insertions.Add((index, stage)); previous = index;
        }
        var marker = AccessTools.Method(typeof(SliceWorkerHooks), nameof(EnterStage));
        foreach (var (index, stage) in insertions.AsEnumerable().Reverse())
        {
            var first = new CodeInstruction(OpCodes.Ldc_I4, (int)stage);
            // A branch to the call must also take its marker; preserve native exception boundaries.
            first.labels.AddRange(codes[index].labels); codes[index].labels.Clear();
            first.blocks.AddRange(codes[index].blocks); codes[index].blocks.Clear();
            codes.InsertRange(index, [first, new CodeInstruction(OpCodes.Call, marker)]);
        }
        return codes;

        MethodInfo FindIslandBatch()
        {
            var matches = codes.Select(instruction => instruction.operand).OfType<MethodInfo>()
                .Where(method => method.DeclaringType == typeof(ParallelBatch) && method.Name == "Execute"
                    && method.IsGenericMethod && method.GetGenericArguments()[0].DeclaringType == typeof(VehicleUpdateTask)
                    && method.GetGenericArguments()[0].Name == "ContactIsland").Distinct().ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Unsupported vehicle contact-island batch seam.");
            return matches[0];
        }
    }

    private static void DispatchPrefix(JobScheduler __instance)
    {
        if (!ReferenceEquals(__instance, JobSystems.VehicleSolver)) return;
        try { SliceFrameDiagnostics.DispatchWorker(); }
        catch { SliceWorkerDiagnostics.Reset(); }
    }
    private static void RunPrefix(ref SliceWorkerRunToken __state)
    {
        try { __state = SliceWorkerDiagnostics.BeginRun(); }
        catch { SliceWorkerDiagnostics.Reset(); }
    }
    private static Exception? RunFinalizer(Exception? __exception, SliceWorkerRunToken __state)
    {
        try { SliceWorkerDiagnostics.EndRun(__state, __exception != null); }
        catch { SliceWorkerDiagnostics.Reset(); }
        return __exception;
    }
    private static void EnterStage(SliceWorkerStage stage)
    {
        try { SliceWorkerDiagnostics.EnterStage(stage); }
        catch { SliceWorkerDiagnostics.Reset(); }
    }
    private static void ChildPrefix(object __instance, MethodBase __originalMethod, ref SliceWorkerScopeToken __state)
    {
        try
        {
            if (__instance is BepuWorkerDispatcher dispatcher
                && (_dispatcherPool == null || !ReferenceEquals(_dispatcherPool(dispatcher), JobSystems.VehicleWorkerPool))) return;
            if (Volatile.Read(ref _childStages).TryGetValue(__originalMethod, out var stage))
                __state = SliceWorkerDiagnostics.BeginChild(stage);
        }
        catch { SliceWorkerDiagnostics.Reset(); }
    }
    private static Exception? ChildFinalizer(Exception? __exception, SliceWorkerScopeToken __state)
    {
        try { SliceWorkerDiagnostics.EndChild(__state); }
        catch { SliceWorkerDiagnostics.Reset(); }
        return __exception;
    }
}
