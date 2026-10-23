using Brutal.Numerics;
using KSA;
using Purr2D2.Slicing;

internal static class IsolationChecks
{
    private static readonly PreparedPhysicsFragment[] Ready = [new(true), new(true)];
    private static void Main()
    {
        RootLeafAndMidStack();
        DockingReroot();
        RefusalsAndBulkCleanup();
        Console.WriteLine("PASS: native isolation ordering, changing owners, bulk finalization and stale source guards");
    }
    private static void Reset()
    {
        Vehicle.SplitOrder.Clear(); Vehicle.SplitTreeSizes.Clear(); PartTree.Created.Clear();
        Vehicle.ThrowOnSplit = 0; Vehicle.RerootAfterFirstSplit = null; Universe.CurrentSystem = new();
    }
    private static void RootLeafAndMidStack()
    {
        Reset();
        var root = new Part("root"); var a = new Part("a"); var b = new Part("b");
        Part.Attach(root, a); Part.Attach(root, b); _ = new Vehicle("root vessel", [root, a, b]);
        Check(NativeSlicePhysics.TryIsolate(root, Ready, out var isolated, out _, new()), "root isolates its original child boundaries");
        Check(Vehicle.SplitOrder.SequenceEqual(["a", "b"]) && isolated!.Owner.Parts.Count == 1, "root isolation preserves both neighboring branches");
        CheckClosed();

        Reset();
        root = new("root"); var leaf = new Part("leaf"); Part.Attach(root, leaf);
        var original = new Vehicle("leaf vessel", [root, leaf]);
        Check(NativeSlicePhysics.TryIsolate(leaf, Ready, out isolated, out _, new()), "leaf isolates its parent boundary");
        Check(isolated!.Owner != original && original.Parts.Members.SequenceEqual([root]), "leaf context retains the new native owner");
        CheckClosed();

        Reset();
        root = new("rocket"); var selected = new Part("selected"); a = new("a"); b = new("b"); var neighbor = new Part("neighbor");
        Part.Attach(root, selected); Part.Attach(root, neighbor); Part.Attach(selected, a); Part.Attach(selected, b);
        original = new Vehicle("mid stack", [root, selected, a, b, neighbor]);
        Check(NativeSlicePhysics.TryIsolate(selected, Ready, out isolated, out _, new()), "mid-stack source isolates successfully");
        Check(Vehicle.SplitOrder.SequenceEqual(["selected", "a", "b"]), "parent boundary is split before every child");
        Check(Vehicle.SplitTreeSizes.SequenceEqual([5, 3, 2]), "only the first split rebuilds the original rocket");
        Check(original.Parts.Members.SequenceEqual([root, neighbor]) && isolated!.Owner.Parts.Members.SequenceEqual([selected]),
            "changing-owner isolation keeps the original rocket and selected shell intact");
        Check(PartTree.Created[1].OuterBegins == 2, "all later child splits share one retained outer staging scope");
        CheckClosed();
    }
    private static void DockingReroot()
    {
        Reset();
        var root = new Part("rocket"); var selected = new Part("dock"); var child = new Part("old dock root");
        Part.Attach(root, selected); Part.Attach(selected, child);
        _ = new Vehicle("docked", [root, selected, child]); Vehicle.RerootAfterFirstSplit = child;
        Check(NativeSlicePhysics.TryIsolate(selected, Ready, out var isolated, out _, new()), "dock reroot cannot hide an original child boundary");
        Check(Vehicle.SplitOrder.SequenceEqual(["dock", "dock"]) && isolated!.Owner.Parts.Members.SequenceEqual([selected]),
            "snapshot boundaries re-resolve owner even when the second split detaches the source again");
        CheckClosed();
    }
    private static void RefusalsAndBulkCleanup()
    {
        Reset();
        var root = new Part("rocket"); var selected = new Part("selected"); var child = new Part("child");
        Part.Attach(root, selected); Part.Attach(selected, child); var original = new Vehicle("fixture", [root, selected, child]);
        Check(!NativeSlicePhysics.TryIsolate(selected, [new(false), new(true)], out _, out _, new()) && Vehicle.SplitOrder.Count == 0,
            "unprepared fragments fail before native mutation");
        Vehicle.ThrowOnSplit = 2;
        Check(!NativeSlicePhysics.TryIsolate(selected, Ready, out _, out var reason, new()) && reason.Contains("partial native separation"),
            "split failure keeps the source and reports completed separations");
        CheckClosed();

        Reset();
        root = new("rocket"); selected = new("selected"); child = new("child");
        Part.Attach(root, selected); Part.Attach(selected, child); original = new("fixture", [root, selected, child]);
        original.Parts.ThrowOnEnd = true;
        Check(!NativeSlicePhysics.TryIsolate(selected, Ready, out _, out _, new()), "bulk finalization failure refuses the swap");
        CheckClosed();

        Reset(); selected = new("selected"); original = new("fixture", [selected]);
        Check(NativeSlicePhysics.TryIsolate(selected, Ready, out var isolated, out _, new()) && isolated!.IsCurrent(out _),
            "already isolated source has a valid context without structural changes");
        original.IsDisposed = true;
        Check(!isolated!.IsCurrent(out _), "disposed source cannot enter the swap"); original.IsDisposed = false;
        original.Parts.IsDisposed = true;
        Check(!isolated.IsCurrent(out _), "disposed source tree cannot enter the swap"); original.Parts.IsDisposed = false;
        original.Parts.OwningVehicle = null;
        Check(!isolated.IsCurrent(out _), "a tree with changed ownership cannot enter the swap"); original.Parts.OwningVehicle = original;
        Universe.CurrentSystem = new();
        Check(!isolated.IsCurrent(out _), "save-system replacement invalidates stale isolated handles");
        Universe.CurrentSystem = original.System;
        selected.MatrixAsmb2VehicleAsmb = double4x4.CreateTranslation(new double3(.01, 0, 0));
        Check(!isolated.IsCurrent(out _), "subsequent source assembly edits invalidate the isolated handle");
    }
    private static void CheckClosed() => Check(PartTree.Created.All(t => t.Depth == 0), "every retained native tree leaves bulk mode");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
