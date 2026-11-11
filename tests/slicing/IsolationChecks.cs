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
        DebrisClassification();
        Console.WriteLine("PASS: native isolation ordering, changing owners, bulk finalization and stale source guards");
    }
    private static void Reset()
    {
        Vehicle.SplitOrder.Clear(); Vehicle.SplitTreeSizes.Clear(); PartTree.Created.Clear();
        Vehicle.ThrowOnSplit = 0; Vehicle.RerootAfterFirstSplit = null; Universe.CurrentSystem = new();
        QuietSliceContacts.Protections = 0;
        NativeSlicePhysics.FailPreparedAfterDrain = false;
        KSA.Program.ControlledVehicle = null;
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
        Check(isolated!.ContentsReleased && isolated.Owner.Drained && isolated.Owner.EnginesOff && !original.Drained,
            "source drains and engines switch off only after external resource branches separate");
        Check(isolated.Neighbors.Length == 3 && isolated.Neighbors.All(v => !v.Drained),
            "transaction retains exact neighboring owners without draining their contents");
        isolated.Dispose();
        Check(isolated.ContentsReleased && isolated.Owner.Drained, "cancellation does not claim released contents were restored");
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
        var massSource = new Part("fallback source");
        var massOwner = new Vehicle("fallback owner", [massSource]);
        NativeSlicePhysics.FailPreparedAfterDrain = true;
        Check(!NativeSlicePhysics.TryIsolate(massSource, Ready, out var retained, out var massReason, new())
            && retained is { ContentsReleased: true } && retained.Owner == massOwner
            && !massOwner.IsDisposed && massReason.Contains("isolated source mass"),
            "native fallback mismatch retains the separated empty source before fragment creation");
        retained!.Dispose();

        Reset();
        var root = new Part("rocket"); var selected = new Part("selected"); var child = new Part("child");
        Part.Attach(root, selected); Part.Attach(selected, child); var original = new Vehicle("fixture", [root, selected, child]);
        Check(!NativeSlicePhysics.TryIsolate(selected, [new(false), new(true)], out _, out _, new()) && Vehicle.SplitOrder.Count == 0,
            "unprepared fragments fail before native mutation");
        Vehicle.ThrowOnSplit = 2;
        Check(!NativeSlicePhysics.TryIsolate(selected, Ready, out _, out var reason, new()) && reason.Contains("partial native separation"),
            "split failure keeps the source and reports completed separations");
        Check(QuietSliceContacts.Protections == 1 && !original.Drained,
            "partial isolation protects completed separations without draining a still-connected host");
        CheckClosed();

        Reset();
        root = new("rocket"); selected = new("selected"); child = new("child");
        Part.Attach(root, selected); Part.Attach(selected, child); original = new("fixture", [root, selected, child]);
        original.Parts.ThrowOnEnd = true;
        Check(!NativeSlicePhysics.TryIsolate(selected, Ready, out _, out _, new()), "bulk finalization failure refuses the swap");
        Check(PartTree.Created.All(tree => tree.OwningVehicle is not { IsDebris: true }),
            "failed bulk finalization cannot classify from potentially stale control or seat membership");
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
    private static void DebrisClassification()
    {
        Reset();
        var root = new Part("capsule") { HasControl = true };
        var cut = new Part("cut tank"); var offcut = new Part("offcut");
        Part.Attach(root, cut); Part.Attach(cut, offcut);
        var original = new Vehicle("rocket", [root, cut, offcut]);
        Check(NativeSlicePhysics.TryIsolate(cut, Ready, out var isolated, out _, new()), "classification fixture isolates its tank");
        Check(!original.IsDebris && isolated!.Owner.IsDebris
            && isolated.Neighbors.Single(v => v != original).IsDebris,
            "inert source/offcut become debris after bulk closure while the control-bearing rocket remains a vehicle");

        Reset(); root = new("capsule") { HasControl = true }; cut = new("tank"); offcut = new("panel");
        Part.Attach(root, cut); Part.Attach(cut, offcut); original = new("partial", [root, cut, offcut]);
        Vehicle.ThrowOnSplit = 2;
        Check(!NativeSlicePhysics.TryIsolate(cut, Ready, out _, out _, new())
            && !original.IsDebris && cut.Tree!.OwningVehicle!.IsDebris,
            "completed partial separations classify inert detached trees without relabeling the control-bearing source");

        Reset(); root = new("untouched"); original = new("untouched", [root]);
        Check(NativeSlicePhysics.TryIsolate(root, Ready, out _, out _, new()) && !original.IsDebris,
            "an isolation with no native split never reclassifies an unrelated existing vehicle");
        var occupied = new Vehicle("occupied", [new("seat")]) { Crew = [new() { AssignedKittenHash = new(1) }] };
        var controlled = new Vehicle("controlled", [new("active")]); KSA.Program.ControlledVehicle = controlled;
        var edited = new Vehicle("edited", [new("editor")]) { IsEditedVehicle = true };
        foreach (var protectedVehicle in new Vehicle[] { occupied, controlled, edited, new KittenEva() })
        {
            SliceDebrisClassification.MarkSeparated(protectedVehicle);
            Check(!protectedVehicle.IsDebris, "classification preserves occupied/controlled/edited/kitten ownership: " + protectedVehicle.Id);
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}
