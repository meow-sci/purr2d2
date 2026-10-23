using System.Diagnostics;
using Brutal.Numerics;
using KSA;
using Purr2D2.Cutting;
using Purr2D2.Rendering;

internal static class ContactEffectsChecks
{
    internal static void Run()
    {
        var triangle = new CutTriangle(new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1));
        Check(SaberContactProbe.TryIntersect(new(0, -1, 0), new(0, 1, 0), triangle, out var hit) && hit == default,
            "spark probe intersects the blade segment with a surface");
        Check(SaberContactProbe.TryIntersect(new(0, 1, 0), new(0, -1, 0), triangle, out _), "spark contact is two-sided");
        Check(!SaberContactProbe.TryIntersect(new(0, -2, 0), new(0, -1, 0), triangle, out _), "no sparks beyond the blade tip");
        Check(!SaberContactProbe.TryIntersect(new(3, -1, 0), new(3, 1, 0), triangle, out _), "no sparks beside the triangle");

        var owner = new KittenEva(); var vessel = new Vehicle { Parent = owner.Parent };
        var part = new Part { Tree = vessel.Parts };
        // The contact lies beyond the first sample's work budget in a multi-primitive mesh.
        var miss = new double3[3 * (SaberContactProbe.TriangleBudget + 17)];
        for (int i = 0; i < miss.Length; i += 3)
        { miss[i] = new(4, 0, -1); miss[i + 1] = new(6, 0, -1); miss[i + 2] = new(5, 0, 1); }
        var mesh = new MeshReference { PositionsCompare = [miss, [new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)]] };
        part.Modules.Add(new PartModelDynamicModule { PartModelDynamic = new() { Template = new() { Mesh = mesh } } });
        vessel.Parts.Parts.Add(part);
        Universe.CurrentSystem = new(); Universe.CurrentSystem.All.Items = [owner, vessel];
        KSA.Program.EditorFlag = false; KSA.Program.Camera = new();
        var probe = new SaberContactProbe();
        Check(!probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), 0, out _) && probe.TestedTriangles == SaberContactProbe.TriangleBudget,
            "cold contact search respects its triangle budget");
        Check(!probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), .01, out _) && probe.TestedTriangles == 0,
            "contact search is throttled between samples");
        Check(probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), .06, out var contact) && contact.Part == part,
            "budgeted search resumes into later primitives instead of starving them");
        Check(probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), .12, out _) && probe.TestedTriangles == 1,
            "sustained contact rechecks one cached triangle");
        // Occupied vessels also support cosmetic contact.
        vessel.Crew = [new() { AssignedKittenHash = new(1) }];
        Check(probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), .18, out _), "crew aboard still allows visual sparks");
        part.PositionParentAsmb = new(10, 0, 0);
        Check(!probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), .24, out _), "moving a part invalidates cached contact immediately");
        part.PositionParentAsmb = default;
        vessel.IsDisposed = true;
        Check(!probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), .30, out _), "disposed vessels cannot emit stale sparks");
        vessel.IsDisposed = false;
        // Stock-size candidate set; warm all host stubs before measuring production code.
        for (int i = 0; i < 623; i++)
        {
            var far = new Part { Tree = vessel.Parts, PositionParentAsmb = new(10 + i, 0, 0) };
            far.Modules.Add(new MeshViewModule(mesh)); vessel.Parts.Parts.Add(far);
        }
        for (int i = 0; i < 50; i++) probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), 1 + i * .06, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < 1800; i++) probe.TrySample(owner, new(0, -1, 0), new(0, 1, 0), 10 + i / 60.0, out _);
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allocated == 0, "warm sustained contact allocates no managed garbage");
        Check(vessel.FragmentCalls == 0 && vessel.SplitCalls == 0 && part.Tree == vessel.Parts,
            "contact never fragments, detaches, or destroys a part");
        Console.WriteLine($"CONTACT PERF: 624 parts, 1800 frames, {milliseconds / 1800:F6} ms/frame, {allocated} allocated bytes (stubbed host)");
    }
    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
