using KSA;
using Purr2D2.Rendering;
using Purr2D2.Slicing;

internal static class DetachedSliceChecks
{
    private static void Main()
    {
        var prepared = new PreparedFragment(new PartTemplate(), "fixture");
        Part.Constructions = PartTree.Constructions = 0;
        using (var pending = new DetachedSliceFragment(prepared))
        {
            var root = pending.Root;
            Check(!pending.Ready && root.Tree == null, "part construction does not create a native world/tree");
            pending.PrepareTree();
            Check(pending.Ready && root.Tree!.OwningVehicle == null, "prepared tree remains detached and invisible");
            var tree = root.Tree!;
            pending.Dispose(); pending.Dispose();
            Check(root.Disposals == 1 && tree.Disposals == 1, "cancel releases a detached tree exactly once");
        }
        Check(Part.Constructions == 1 && PartTree.Constructions == 1,
            "preparation constructs exactly one real fragment and one tree");

        using (var pending = new DetachedSliceFragment(prepared))
        {
            pending.PrepareTree();
            var root = pending.Root;
            var vehicle = new Vehicle(root);
            pending.TransferredTo(vehicle);
            Check(!pending.Ready && ReferenceEquals(vehicle.Parts.Root, root), "commit reuses the staged root without cloning it");
            bool reused = false;
            try { _ = pending.Root; reused = true; } catch (InvalidOperationException) { }
            Check(!reused, "a transferred root cannot be reused by a second commit");
            pending.Dispose();
            Check(root.Disposals == 0, "job cleanup leaves native-owned fragments alive");
            vehicle.Dispose();
            Check(root.Disposals == 1, "native vehicle owns final fragment teardown");
        }

        using (var pending = new DetachedSliceFragment(prepared))
        {
            var root = pending.Root;
            pending.Dispose();
            Check(root.Disposals == 1, "cancel between construction and tree preparation releases the part");
        }
        using (var pending = new DetachedSliceFragment(prepared))
        {
            pending.PrepareTree();
            var root = pending.Root;
            var vehicle = new Vehicle(root); // Models a native constructor throwing after adopting Parts.
            pending.Dispose();
            Check(root.Disposals == 0, "constructor failure after native adoption leaves rollback in sole control");
            vehicle.Dispose();
            Check(root.Disposals == 1, "rollback disposes a partially adopted tree once");
        }
        PartTree.FailAfterTransfer = true;
        using (var pending = new DetachedSliceFragment(prepared))
        {
            var root = pending.Root;
            bool failed = false;
            try { pending.PrepareTree(); } catch (InvalidOperationException) { failed = true; }
            Check(failed, "native tree preparation failure is observed");
            pending.Dispose();
            Check(root.Disposals == 1 && root.Tree!.Disposals == 1, "partially prepared detached trees are released after failure");
        }
        PartTree.FailAfterTransfer = false;
        int before = Part.Constructions;
        bool invalid = false;
        try { using var pending = new DetachedSliceFragment(prepared with { Template = new PartTemplate { Mass = 0 } }); }
        catch (InvalidOperationException) { invalid = true; }
        Check(invalid && Part.Constructions == before + 1, "invalid native mass is refused in staging before any vehicle creation");
        Console.WriteLine("PASS: staged single-instance construction, exact ownership handoff, cancellation and native failure cleanup");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Detached slice check: " + message);
    }
}
