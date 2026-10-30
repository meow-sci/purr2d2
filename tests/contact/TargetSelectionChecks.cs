using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Cutting;

internal static class TargetSelectionChecks
{
    private static void Main() { AssistedSliceSelection(); BladeReach(); ConfigurableReach(); HoverPreview(); PointerOcclusion(); IncrementalSelection(); LegacyFragmentPicking(); ContactEffectsChecks.Run(); ManualContactChecks.Run(); Console.WriteLine("PASS: explicit part selection and commit guards"); }
    private static double Reach(double visibleLength, float multiplier = SaberLimits.DefaultSliceReachMultiplier) =>
        SaberLimits.SliceSelectionReach(visibleLength, multiplier);
    private static MeshReference NewMesh() => new() { PositionsCompare = [[new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1)]] };
    private static void LegacyFragmentPicking()
    {
        var root = new Part();
        root.Template.Id = "Purr2D2_Fragment_fixture";
        var pickMesh = NewMesh();
        root.Modules.Add(new MeshViewModule(pickMesh));
        var first = new Part { PartParent = root };
        var second = new Part { PartParent = root };
        foreach (var child in new[] { first, second })
        {
            child.Modules.Add(new PartModelModule { PartModel = new() { Template = new() { Mesh = NewMesh() } } });
            root.SubParts.Add(child);
        }
        Check(PartSliceTargeting.RepairLegacyFragmentPicker(root), "legacy fragment gains native subpart picking before tree creation");
        var view = first.Modules.Get<MeshViewModule>()[0];
        Check(view.Parent == first && ReferenceEquals(view.MeshView, pickMesh)
            && ReferenceEquals(first.SubtreeModules, first.Modules) && Occurrences(first.Modules.Get<MeshViewModule>(), view) == 1
            && Occurrences(root.SubtreeModules.Get<MeshViewModule>(), view) == 1
            && first.BoundsUpdates == 1 && root.BoundsUpdates == 1,
            "compatibility picker uses the complete hull mesh, registers once with its aliased visual subpart and the full part, and updates bounds");
        Check(!PartSliceTargeting.RepairLegacyFragmentPicker(root)
            && first.Modules.Get<MeshViewModule>().Length == 1 && second.Modules.Get<MeshViewModule>().IsEmpty,
            "repeated compatibility repair cannot add duplicate pick modules to another visual child");
        var stock = new Part(); stock.Modules.Add(new MeshViewModule(pickMesh)); stock.SubParts.Add(new Part());
        Check(!PartSliceTargeting.RepairLegacyFragmentPicker(stock), "compatibility repair never modifies stock templates");
        var transformed = new Part(); transformed.Template.Id = "Purr2D2_Fragment_transformed";
        transformed.Modules.Add(new MeshViewModule(pickMesh));
        var moved = new Part { PartParent = transformed, PositionParentAsmb = new(1, 0, 0) };
        moved.Modules.Add(new PartModelModule { PartModel = new() { Template = new() { Mesh = pickMesh } } });
        transformed.SubParts.Add(moved);
        Check(!PartSliceTargeting.RepairLegacyFragmentPicker(transformed), "a transformed child cannot misplace a legacy hull picker");
        transformed.Tree = new Vehicle().Parts; moved.PositionParentAsmb = default;
        Check(!PartSliceTargeting.RepairLegacyFragmentPicker(transformed), "compatibility repair cannot mutate an owned native tree");
    }
    private static int Occurrences(Span<MeshViewModule> modules, MeshViewModule module)
    {
        int count = 0;
        foreach (var item in modules) if (ReferenceEquals(item, module)) count++;
        return count;
    }
    private static void BladeReach()
    {
        var f = new Fixture();
        var hilt = new double3(0, -.5, 0);
        Check(f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, Reach(.25), out var target, out _),
            "a selected surface exactly twice the visible blade length from the hilt is reachable");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, new(0, -.5001, 0), Reach(.25), out _, out _),
            "short blade refuses a surface just beyond its real 2x boundary");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, new(0, -2.0001, 0), Reach(1), out _, out _)
            && f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, new(0, -2, 0), Reach(1), out _, out _),
            "one metre visible blade reaches exactly two metres and no farther");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, Reach(.125), out _, out _),
            "a partially extended blade cannot use its configured full reach");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, 0, out _, out _)
            && !f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, double.NaN, out _, out _),
            "retracted or invalid blade length cannot select a part");
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, hilt, Reach(.25), out _, out _)
            && !f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, new(0, -.5001, 0), Reach(.25), out _, out _),
            "fallback picking uses the same hilt-to-surface range independently of camera distance");
        Check(!f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -.6, 0), Reach(.25), default).Success && f.SliceCalls == 0,
            "movement beyond reach before the stroke cannot trigger a cut");

        f.Target.CenterOfMassAsmb = new(0, 30, 0);
        Check(f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, hilt, Reach(.25), out _, out _),
            "a nearby hull hit stays reachable when the vessel centre of mass is far away");
        f.Target.CenterOfMassAsmb = default;
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, new(0, 3, 0), default, Reach(1), out _, out _),
            "a distant hovered hull hit stays unreachable when the vessel centre of mass is at the hilt");

        f.Wielder.PositionEgo = new(1000, 2, -3000);
        f.Target.PositionEgo = new(1000, 0, -3000);
        f.Wielder.Body2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, Math.PI / 2);
        var rotatedHilt = new double3(-1.75, 0, 0);
        Check(f.Cutter.TryPointTarget(f.Wielder, f.Hull, f.Target.PositionEgo, rotatedHilt, Reach(.251), out _, out _)
            && f.Cutter.TryFindSliceTarget(f.Wielder, f.Target.PositionEgo - double3.UnitY * 80,
                double3.UnitY, rotatedHilt, Reach(.251), out _, out _),
            "rotated hilt determines reach even when the kitten centre is farther than blade length");
    }
    private static void ConfigurableReach()
    {
        foreach (float multiplier in new[] { 1f, 2f, 4f })
        foreach (double deployment in new[] { .125, .5, 1.0 })
        {
            var f = new Fixture();
            double visibleLength = 1.4 * deployment, reach = Reach(visibleLength, multiplier);
            var hilt = new double3(0, -reach, 0);
            var preview = f.Cutter.PreviewPointTarget(f.Wielder, f.Hull, default, hilt, reach, out bool allowed, out _);
            Check(preview?.Part == f.Hull && allowed
                && f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, hilt, reach, out var picked, out _)
                && picked!.Part == preview!.Part,
                $"hover and fallback agree at the {multiplier}x boundary with {deployment:P0} blade deployment");
            hilt.Y -= .0001;
            preview = f.Cutter.PreviewPointTarget(f.Wielder, f.Hull, default, hilt, reach, out allowed, out var reason);
            Check(preview != null && !allowed && reason.Contains($"{reach:F2} m") && !reason.Contains("twice")
                && !f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, hilt, reach, out _, out _),
                $"hover and fallback refuse just beyond the {multiplier}x deployed boundary");
            using var selection = f.Cutter.BeginSelection(f.Wielder, new(0, -80, 0), double3.UnitY,
                new(0, -reach, 0), reach);
            while (!selection.Done) selection.Advance();
            Check(selection.Target?.Part == f.Hull && f.SliceCalls == 0,
                "incremental selection uses the same effective reach without cutting");
        }

        var live = new Fixture();
        Check(live.Cutter.TryPointTarget(live.Wielder, live.Hull, default, new(0, -3, 0), Reach(1, 4), out var target, out _),
            "four times grace can select a surface three metres from a one metre blade");
        Check(!live.Cutter.CheckSliceTarget(live.Wielder, target!, new(0, -3, 0), Reach(1, 1), out _)
            && live.Cutter.CheckSliceTarget(live.Wielder, target!, new(0, -3, 0), Reach(1, 4), out _),
            "a changed reach value is revalidated immediately before accepting the stroke");
        Check(live.Cutter.CheckSliceTarget(live.Wielder, target!, new(0, -1, 0), Reach(1, 1), out _)
            && !live.Cutter.CheckSliceTarget(live.Wielder, target!, new(0, -1.0001, 0), Reach(1, 1), out _),
            "stroke-start validation measures the live hilt against the locked surface point");
        Check(!live.Cutter.CheckSliceTarget(live.Wielder, target!, default, double.PositiveInfinity, out _)
            && !live.Cutter.TryPointTarget(live.Wielder, live.Hull, default, default, double.PositiveInfinity, out _, out _)
            && !live.Cutter.TryFindSliceTarget(live.Wielder, new(0, -80, 0), double3.UnitY,
                default, double.PositiveInfinity, out _, out _),
            "range-bound selection and revalidation reject infinite reach instead of bypassing the grace setting");
        live.Target.PositionEgo = new(100, 0, 0);
        Check(live.Cutter.CheckPendingSliceTarget(live.Wielder, target!, out _)
            && live.Cutter.CommitAcceptedSlice(live.Wielder, target!, default).Success && live.SliceCalls == 1,
            "an accepted stroke finishes after moving away without an infinite reach sentinel");
        live.Target.IsDisposed = true;
        Check(!live.Cutter.CommitAcceptedSlice(live.Wielder, target!, default).Success && live.SliceCalls == 1,
            "accepted stroke completion still rejects disposed geometry");

        foreach (double invalid in new[] { 0, -1, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            Check(Reach(invalid, 4) == 0, "invalid or retracted visible length cannot authorize selection at any grace");
        foreach (float invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
            Check(Reach(.5, invalid) == 1, "invalid grace recovers the exact prior two-times allowance");
        Check(Reach(.5, -1) == .5 && Reach(.5, 99) == 2, "reach calculation clamps grace to the configured 1x–4x range");
    }
    private static void PointerOcclusion()
    {
        var fixture = new Fixture();
        fixture.Wielder.PositionEgo = new(0, 2, 0);
        var rear = new Part { Tree = fixture.Target.Parts, PositionParentAsmb = new(0, 1, 0) };
        rear.Modules.Add(new MeshViewModule(NewMesh()));
        fixture.Target.Parts.Parts.Add(rear);
        Check(fixture.Cutter.TryFindSliceTarget(fixture.Wielder, new(0, -10, 0), double3.UnitY, default, 3, out var picked, out _)
            && picked!.Part == fixture.Hull, "cursor chooses the front surface even when another part is closer to the kitten");
        Check(fixture.Cutter.TryPointTarget(fixture.Wielder, new Part { PartParent = rear }, new(0, 1, 0), default, 3, out picked, out _)
            && picked!.Part == rear, "native subpart hover selects its full structural part");
    }
    private static void HoverPreview()
    {
        var f = new Fixture();
        var preview = f.Cutter.PreviewPointTarget(f.Wielder, f.Hull, default, new(0, -2.001, 0), Reach(1), out bool allowed, out string reason);
        Check(preview?.Part == f.Hull && !allowed && reason.Contains("slice reach"),
            "out-of-range surface remains available for red hover feedback");
        Check(!f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, new(0, -2.001, 0), Reach(1), out var target, out _) && target == null,
            "rejected hover cannot become an authorized click target");
        preview = f.Cutter.PreviewPointTarget(f.Wielder, f.Hull, default, new(0, -1, 0), Reach(1), out allowed, out _);
        Check(preview?.Part == f.Hull && allowed && f.SliceCalls == 0, "reachable preview is green without performing a cut");
        f.Target.Crew = [new IVASeat { AssignedKittenHash = new(1) }];
        preview = f.Cutter.PreviewPointTarget(f.Wielder, f.Hull, default, default, 1, out allowed, out _);
        Check(preview?.Part == f.Hull && allowed, "occupied vessels receive the same reachable hover feedback");
    }
    private static void IncrementalSelection()
    {
        var fixture = new Fixture(addMesh: false);
        var vertices = new double3[30_000];
        for (int i = 0; i < vertices.Length; i += 3)
        { vertices[i] = new(-1, 0, -1); vertices[i + 1] = new(1, 0, -1); vertices[i + 2] = new(0, 0, 1); }
        fixture.Hull.Modules.Add(new MeshViewModule(new() { PositionsCompare = [vertices] }));
        using var selection = fixture.Cutter.BeginSelection(fixture.Wielder, new(0, -1, 0), double3.UnitY, default, 3);
        selection.Advance();
        Check(!selection.Done && fixture.SliceCalls == 0, "detailed target search yields before scanning an entire mesh");
        int updates = 1;
        while (!selection.Done && updates++ < 200) selection.Advance();
        Check(selection.Done && selection.Target?.Part == fixture.Hull && updates >= 5 && fixture.SliceCalls == 0,
            "target search resumes across frames and cannot cut while preparing");
        fixture.Target.IsDisposed = true;
        Check(!fixture.Cutter.CheckSliceTarget(fixture.Wielder, selection.Target!, new(0, -1, 0), 3, out _),
            "disposing a target invalidates the prepared selection");
    }
    private static void AssistedSliceSelection()
    {
        var f = new Fixture();
        // No Update/contact/heat or slicing-mode toggle is required by the explicit action.
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -1, 0), double3.UnitY, default, 3, out var target, out var reason)
            && target!.Part == f.Hull, "cursor ray selects the visible hull without prior contact: " + reason);
        Check(f.SliceCalls == 0, "selection alone never mutates a vessel");
        var cut = f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -1, 0), 3, new(new(1, 0, 0), 0));
        Check(cut.Success && f.SliceCalls == 1 && f.Cutter.SlicedParts == 1,
            "explicit action invokes mesh slicing without any contact-cutting mode");
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -80, 0), double3.UnitY, default, 3, out _, out _),
            "zooming the camera out does not change reach from the kitten");
        Check(!f.Cutter.TryFindSliceTarget(f.Wielder, new(8, -1, 0), double3.UnitY, default, 3, out _, out _),
            "clicking empty space never falls back to a nearby part");
        Check(!f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -1, 0), -double3.UnitY, default, 3, out _, out _),
            "a part behind the camera ray cannot be selected");
        Check(f.Cutter.TryPointTarget(f.Wielder, f.Hull, default, default, 3, out var hovered, out _) && hovered!.Part == f.Hull,
            "native hover can lock the exact clicked part without scanning");
        f.Target.Crew = [new IVASeat { AssignedKittenHash = new(1) }];
        Check(f.Cutter.TryFindSliceTarget(f.Wielder, new(0, -1, 0), double3.UnitY, default, 3, out var occupied, out _)
            && occupied!.Part == f.Hull, "fallback selection permits occupied vessels");
        Check(f.Cutter.CommitAssistedSlice(f.Wielder, target!, new(0, -1, 0), 3, default).Success
            && f.SliceCalls == 2, "crew boarding after selection cannot refuse the destructive commit");
        f.Target.Crew = [];
        f.Target.PositionEgo = new(10, 0, 0);
        Check(!f.Cutter.CheckSliceTarget(f.Wielder, target!, default, 3, out _), "out-of-reach click is refused");
        Check(f.Cutter.CheckPendingSliceTarget(f.Wielder, target!, out _), "moving after an in-range stroke does not cancel the pending result");
        f.Target.PositionEgo = default;
        f.Hull.PositionParentAsmb = new(0, 1, 0);
        Check(!f.Cutter.CheckSliceTarget(f.Wielder, target!, new(0, -1, 0), 3, out _), "part transform changes invalidate the prepared plane");
        f.Hull.PositionParentAsmb = default;
        f.Hull.Tree = new Vehicle().Parts;
        Check(!f.Cutter.CheckSliceTarget(f.Wielder, target!, new(0, -1, 0), 3, out _), "reparented part cannot use the old vessel plane");

        var transformed = new Fixture(addMesh: false);
        transformed.Target.PositionEgo = new(1000, 2000, -3000);
        transformed.Wielder.PositionEgo = transformed.Target.PositionEgo;
        transformed.Target.CenterOfMassAsmb = new(.2, .4, -.7);
        transformed.Target.Asmb2Ego = doubleQuat.CreateFromAxisAngle(double3.UnitZ, .6);
        var sub = new Part { PartParent = transformed.Hull, Tree = transformed.Target.Parts, PositionParentAsmb = new(.1, .2, .3) };
        sub.Modules.Add(new PartModelDynamicModule { PartModelDynamic = new() { Template = new() { Mesh = NewMesh() } } });
        transformed.Hull.SubParts.Add(sub);
        var matrix = sub.MatrixAsmb2Ego(transformed.Target.GetMatrixAsmb2Ego(KSA.Program.Camera!));
        var point = double3.Zero.Transform(matrix);
        var direction = double3.TransformNormal(double3.UnitY, matrix).NormalizeOrZero();
        Check(transformed.Cutter.TryFindSliceTarget(transformed.Wielder, point - direction * 10, direction, default, 3, out var picked, out reason)
            && picked!.Part == transformed.Hull && transformed.Cutter.CheckSliceTarget(transformed.Wielder, picked, new(0, -1, 0), 3, out _),
            "transformed render-only tank subpart resolves to the full part in assembly coordinates: " + reason);
    }

    private sealed class Fixture
    {
        public readonly KittenEva Wielder = new();
        public readonly Vehicle Target = new();
        public readonly Part Root = new() { DisplayName = "root" };
        public readonly Part Hull = new();
        public readonly PartSliceTargeting Cutter = new();
        public int SliceCalls;
        private readonly Part.Connection _boundary;

        public Fixture(bool addMesh = true)
        {
            KSA.Program.EditorFlag = false;
            KSA.Program.Camera = new();
            Target.Parent = Wielder.Parent;
            Root.Tree = Hull.Tree = Target.Parts;
            Target.Parts.Parts.AddRange([Root, Hull]);
            Root.TreeChildren.Add(Hull);
            Hull.TreeParent = Root;
            _boundary = new(Root, Hull);
            Root.Connections.Add(_boundary);
            Hull.Connections.Add(_boundary);
            if (addMesh) Hull.Modules.Add(new MeshViewModule(NewMesh()));
            Universe.CurrentSystem = new();
            Universe.CurrentSystem.All.Items = [Wielder, Target];
            Cutter.SliceHandler = (part, plane) => { SliceCalls++; return new(true, "fixture slice"); };
        }

    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Target selection regression failed: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
