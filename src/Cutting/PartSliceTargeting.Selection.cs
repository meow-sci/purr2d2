using Brutal.Numerics;
using KSA;

namespace Purr2D2.Cutting;

internal sealed record SliceTarget(Part Part, Vehicle Vehicle, double3 HitAssembly, double4x4 PartTransform);

internal sealed partial class PartSliceTargeting
{
    internal sealed class Selection : IDisposable
    {
        internal SliceTarget? Target;
        internal string Reason = "No part under the pointer. Click a visible vessel part.";
        internal bool Done;
        internal IEnumerator<int>? Steps;
        internal void Advance()
        {
            if (Done) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            int units = 0;
            do { if (!Steps!.MoveNext()) { Done = true; Dispose(); break; } }
            while (++units < 8 && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < 1.5);
        }
        public void Dispose() { Steps?.Dispose(); Steps = null; }
    }

    // All selection entry points take the maximum hilt-to-hit distance in metres.
    internal Selection BeginSelection(KittenEva wielder, double3 rayOrigin, double3 rayDirection, double3 baseBody, double reach)
    {
        if (_wielder != wielder) { Reset(); _wielder = wielder; }
        var request = new Selection();
        request.Steps = SelectSteps(request, wielder, rayOrigin, rayDirection, baseBody, reach).GetEnumerator();
        return request;
    }

    // Synchronous wrapper is only used by the small managed fixtures. Runtime advances
    // the same iterator across frames; cold selection never builds a fleet-sized BVH.
    internal bool TryFindSliceTarget(KittenEva wielder, double3 rayOrigin, double3 rayDirection, double3 baseBody, double reach,
        out SliceTarget? target, out string reason)
    {
        using var request = BeginSelection(wielder, rayOrigin, rayDirection, baseBody, reach);
        while (!request.Done) request.Advance();
        target = request.Target; reason = request.Reason;
        return target != null && CanCut(target.Part, out reason);
    }

    private IEnumerable<int> SelectSteps(Selection request, KittenEva wielder, double3 rayOrigin, double3 rayDirection, double3 baseBody, double reach)
    {
        if (Program.EditorFlag || Universe.CurrentSystem == null || Program.GetMainCamera() is not { } camera
            || !ToPoint(rayOrigin).Finite || !ToPoint(rayDirection).Finite || rayDirection.LengthSquared() < .5
            || !ToPoint(baseBody).Finite || !double.IsFinite(reach) || reach <= 0) yield break;
        var origin = baseBody.Transform(wielder.Body2Ego) + camera.GetPositionEgo(wielder);
        double best = double.PositiveInfinity;
        int tests = 0, nodes = 0;
        var meshes = new List<MeshReference>();
        // Snapshot only references here; every part/mesh access stays on the game thread.
        foreach (var item in Universe.CurrentSystem.All.AsSpan().ToArray())
        {
            if (item is not Vehicle vehicle || vehicle == wielder || vehicle is KittenEva || vehicle.IsDisposed || vehicle.IsEditedVehicle
                || !ReferenceEquals(vehicle.Parent, wielder.Parent)) continue;
            if ((camera.GetPositionEgo(vehicle) - origin).Length() > vehicle.BoundingSphereRadiusBody + reach) continue;
            foreach (var full in vehicle.Parts.Parts.ToArray())
            {
                if (full.IsAttachedInternal) continue;
                foreach (var part in new[] { full }.Concat(full.SubParts.ToArray()))
                {
                    if (++nodes > MaxCandidateSubparts) { request.Target = null; request.Reason = "Nearby geometry exceeds the selection budget."; yield break; }
                    if (vehicle.IsDisposed || full.Tree?.OwningVehicle != vehicle) continue;
                    var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
                    var matrix = part.MatrixAsmb2Ego(in vehicleMatrix);
                    if (!double4x4.Invert(matrix, out var inverse)) continue;
                    CollectMeshes(part, meshes);
                    foreach (var mesh in meshes)
                    {
                        if (mesh.PositionsCompare is not { } primitives) continue;
                        // Conservative world-space sphere avoids reading distant triangles.
                        double scale = Math.Max(Math.Abs(part.ScaleTotal.X), Math.Max(Math.Abs(part.ScaleTotal.Y), Math.Abs(part.ScaleTotal.Z)));
                        if ((origin - double3.Zero.Transform(matrix)).Length() > mesh.BoundingSphereRadius * scale + reach) continue;
                        foreach (var vertices in primitives)
                        {
                            if (vertices == null) continue;
                            for (int i = 0; i + 2 < vertices.Length; i += 3)
                            {
                                if (++tests > MaxCachedTriangles) { request.Target = null; request.Reason = "Nearby geometry exceeds the selection budget."; yield break; }
                                var triangle = new CutTriangle(ToPoint(vertices[i].Transform(matrix)), ToPoint(vertices[i + 1].Transform(matrix)), ToPoint(vertices[i + 2].Transform(matrix)));
                                if (triangle.A.Finite && triangle.B.Finite && triangle.C.Finite && triangle.Normal.LengthSquared > .5)
                                {
                                    if (CutGeometry.TryRayTriangle(ToPoint(rayOrigin), ToPoint(rayDirection), triangle, out double distance)
                                        && (distance < best || (distance == best && (request.Target == null || full.InstanceId < request.Target.Part.InstanceId))))
                                    {
                                        best = distance;
                                        var hit = rayOrigin + rayDirection * distance;
                                        request.Target = new(full, vehicle, hit.Transform(inverse).Transform(part.MatrixAsmb2VehicleAsmb), full.MatrixAsmb2VehicleAsmb);
                                    }
                                }
                                if (tests % 256 == 0) yield return 0;
                            }
                        }
                    }
                    yield return 0;
                }
            }
        }
        if (request.Target is { } target && !CheckSliceTarget(wielder, target, baseBody, reach, out var refusal))
        { request.Target = null; request.Reason = refusal; }
    }

    internal bool TryPointTarget(KittenEva wielder, Part part, double3 hitEgo, double3 baseBody, double reach, out SliceTarget? target, out string reason)
    {
        var preview = PreviewPointTarget(wielder, part, hitEgo, baseBody, reach, out bool allowed, out reason);
        target = allowed ? preview : null;
        return allowed;
    }

    // Retain a rejected surface for red hover feedback without authorizing a cut.
    internal SliceTarget? PreviewPointTarget(KittenEva wielder, Part part, double3 hitEgo, double3 baseBody, double reach,
        out bool allowed, out string reason)
    {
        _wielder = wielder; allowed = false;
        reason = "Point at a vessel part.";
        var full = part.StructuralPart;
        if (full.Tree?.OwningVehicle is not { } vehicle || Program.GetMainCamera() is not { } camera
            || !double4x4.Invert(vehicle.GetMatrixAsmb2Ego(camera), out var inverse)) return null;
        var candidate = new SliceTarget(full, vehicle, hitEgo.Transform(inverse), full.MatrixAsmb2VehicleAsmb);
        allowed = CheckSliceTarget(wielder, candidate, baseBody, reach, out reason);
        return candidate;
    }

    private static void CollectMeshes(Part part, List<MeshReference> meshes)
    {
        meshes.Clear(); bool render = false;
        foreach (var model in part.Modules.Get<PartModelModule>()) { render = true; Add(model.PartModel.Template.Mesh?.Get()); }
        foreach (var model in part.Modules.Get<PartModelDynamicModule>()) { render = true; Add(model.PartModelDynamic.Template.Mesh?.Get()); }
        foreach (var model in part.Modules.Get<PartModelGlassModule>()) { render = true; Add(model.PartModelGlass.Template.Mesh?.Get()); }
        if (!render) foreach (var model in part.Modules.Get<MeshViewModule>()) Add(model.MeshView);
        void Add(MeshReference? mesh) { if (mesh != null && !meshes.Contains(mesh)) meshes.Add(mesh); }
    }

    // Reach is already the maximum hilt-to-hit distance in metres, including configured grace.
    internal bool CheckSliceTarget(KittenEva wielder, SliceTarget target, double3 baseBody, double reach, out string reason)
    {
        if (!CheckPendingSliceTarget(wielder, target, out reason)) return false;
        var camera = Program.GetMainCamera()!;
        var origin = baseBody.Transform(double4x4.CreateFromQuaternion(wielder.Body2Ego)) + camera.GetPositionEgo(wielder);
        var hit = target.HitAssembly.Transform(target.Vehicle.GetMatrixAsmb2Ego(camera));
        double distance = (hit - origin).Length();
        if (!double.IsFinite(distance) || !double.IsFinite(reach) || reach <= 0 || distance > reach)
        { reason = $"Selected point is {distance:F2} m from the hilt; slice reach is {reach:F2} m. Move closer or adjust Blade length or Slice grace / reach in Settings."; return false; }
        return true;
    }

    // Accepted strokes may finish after moving out of reach, but never on stale geometry/ownership.
    internal bool CheckPendingSliceTarget(KittenEva wielder, SliceTarget target, out string reason)
    {
        reason = "The selected part is no longer available.";
        if (_wielder != wielder || target.Vehicle.IsDisposed || target.Part.Tree?.OwningVehicle != target.Vehicle
            || !target.Part.MatrixAsmb2VehicleAsmb.Equals(target.PartTransform)
            || Program.GetMainCamera() is not { } camera || Program.EditorFlag
            || !ToPoint(target.HitAssembly.Transform(target.Vehicle.GetMatrixAsmb2Ego(camera))).Finite) return false;
        return CanCut(target.Part, out reason);
    }

    internal SliceAttempt CommitAssistedSlice(KittenEva wielder, SliceTarget target, double3 baseBody, double reach, SlicePlane plane)
    {
        if (!CheckSliceTarget(wielder, target, baseBody, reach, out string reason)) return new(false, reason);
        return CommitAcceptedSlice(wielder, target, plane);
    }

    internal SliceAttempt CommitAcceptedSlice(KittenEva wielder, SliceTarget target, SlicePlane plane)
    {
        if (!CheckPendingSliceTarget(wielder, target, out string reason)) return new(false, reason);
        var result = SliceHandler?.Invoke(target.Part, plane) ?? new(false, "Slicing is unavailable.");
        Status = result.Message;
        if (result.Success) SlicedParts++;
        return result;
    }
}
