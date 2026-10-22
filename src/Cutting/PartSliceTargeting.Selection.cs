using Brutal.Numerics;
using KSA;

namespace Purr2D2.Cutting;

internal sealed record SliceTarget(Part Part, Vehicle Vehicle, double3 HitAssembly, double4x4 PartTransform);

internal sealed partial class PartSliceTargeting
{
    // Explicit action: select visible geometry once, independently of heat/sweep history.
    // The selected FC vessel scopes selection; never silently cut a different vessel.
    internal bool TryFindSliceTarget(KittenEva wielder, double3 baseBody, double reach, Vehicle? selected,
        out SliceTarget? target, out string reason)
    {
        BudgetLimited = false;
        target = null;
        SliceTarget? found = null;
        reason = "No nearby part found. Move closer or increase the blade length.";
        if (_wielder != wielder) { Reset(); _wielder = wielder; }
        if (Program.EditorFlag || Universe.CurrentSystem == null || Program.GetMainCamera() is not { } camera
            || !ToPoint(baseBody).Finite || !double.IsFinite(reach) || reach <= 0) return false;
        var origin = baseBody.Transform(double4x4.CreateFromQuaternion(wielder.Body2Ego)) + camera.GetPositionEgo(wielder);
        double best = reach * reach;
        int tests = 0, parts = 0;
        foreach (var item in Universe.CurrentSystem.All.AsSpan())
        {
            if (item is not Vehicle vehicle || vehicle == wielder || vehicle is KittenEva
                || vehicle.IsDisposed || vehicle.IsEditedVehicle || !ReferenceEquals(vehicle.Parent, wielder.Parent)
                || (selected != null && selected != vehicle)) continue;
            if ((camera.GetPositionEgo(vehicle) - origin).Length() > vehicle.BoundingSphereRadiusBody + reach) continue;
            var vehicleToEgo = vehicle.GetMatrixAsmb2Ego(camera);
            foreach (var full in vehicle.Parts.Parts)
            {
                if (full.IsAttachedInternal) continue;
                if (!Scan(full)) { reason = "Nearby geometry exceeds the slice selection budget."; return false; }
                foreach (var sub in full.SubParts)
                    if (!Scan(sub)) { reason = "Nearby geometry exceeds the slice selection budget."; return false; }
            }

            bool Scan(Part part)
            {
                if (++parts > MaxCandidateSubparts) return false;
                var matrix = part.MatrixAsmb2Ego(in vehicleToEgo);
                if (!double4x4.Invert(matrix, out var inverse)) return true;
                var localOrigin = ToPoint(origin.Transform(inverse));
                double norm = Math.Sqrt(inverse.M11 * inverse.M11 + inverse.M12 * inverse.M12 + inverse.M13 * inverse.M13
                    + inverse.M21 * inverse.M21 + inverse.M22 * inverse.M22 + inverse.M23 * inverse.M23
                    + inverse.M31 * inverse.M31 + inverse.M32 * inverse.M32 + inverse.M33 * inverse.M33);
                var query = CutBounds.Sweep(localOrigin, localOrigin, localOrigin, localOrigin, reach * norm);
                _testedMeshes.Clear();
                bool render = false;
                foreach (var model in part.Modules.Get<PartModelModule>())
                { render = true; if (!Mesh(model.PartModel.Template.Mesh?.Get())) return false; }
                foreach (var model in part.Modules.Get<PartModelDynamicModule>())
                { render = true; if (!Mesh(model.PartModelDynamic.Template.Mesh?.Get())) return false; }
                foreach (var model in part.Modules.Get<PartModelGlassModule>())
                { render = true; if (!Mesh(model.PartModelGlass.Template.Mesh?.Get())) return false; }
                if (!render) foreach (var view in part.Modules.Get<MeshViewModule>()) if (!Mesh(view.MeshView)) return false;
                return true;

                bool Mesh(MeshReference? reference)
                {
                    if (reference == null || !_testedMeshes.Add(reference)) return true;
                    var mesh = GetMesh(reference);
                    if (mesh == null) return true;
                    mesh.Query(query, _triangleCandidates);
                    foreach (int index in _triangleCandidates)
                    {
                        // This is a button-triggered scan, not the per-frame contact budget.
                        if (++tests > MaxCachedTriangles) return false;
                        var t = mesh.Triangles[index];
                        var triangle = new CutTriangle(Transform(t.A, matrix), Transform(t.B, matrix), Transform(t.C, matrix));
                        var hit = CutGeometry.ClosestPoint(ToPoint(origin), triangle);
                        double distance = (hit - ToPoint(origin)).LengthSquared;
                        if (distance > best || !double.IsFinite(distance)) continue;
                        if (distance == best && found != null && part.FullPart.InstanceId >= found.Part.InstanceId) continue;
                        best = distance;
                        var assemblyHit = ToGame(hit).Transform(inverse).Transform(part.MatrixAsmb2VehicleAsmb);
                        found = new(part.FullPart, vehicle, assemblyHit, part.FullPart.MatrixAsmb2VehicleAsmb);
                    }
                    return true;
                }
            }
        }
        target = found;
        if (target == null) return false;
        return CanCut(target.Part, out reason);
    }

    internal bool CheckSliceTarget(KittenEva wielder, SliceTarget target, double3 baseBody, double reach, out string reason)
    {
        reason = "Slice cancelled: the selected part changed or moved out of reach.";
        if (_wielder != wielder || target.Vehicle.IsDisposed || target.Part.Tree?.OwningVehicle != target.Vehicle
            || !target.Part.MatrixAsmb2VehicleAsmb.Equals(target.PartTransform)
            || Program.GetMainCamera() is not { } camera || Program.EditorFlag) return false;
        var origin = baseBody.Transform(double4x4.CreateFromQuaternion(wielder.Body2Ego)) + camera.GetPositionEgo(wielder);
        var hit = target.HitAssembly.Transform(target.Vehicle.GetMatrixAsmb2Ego(camera));
        double distance = (hit - origin).Length();
        return double.IsFinite(distance) && distance <= reach && CanCut(target.Part, out reason);
    }

    internal SliceAttempt CommitAssistedSlice(KittenEva wielder, SliceTarget target, double3 baseBody, double reach, SlicePlane plane)
    {
        if (!CheckSliceTarget(wielder, target, baseBody, reach, out string reason)) return new(false, reason);
        var result = SliceHandler?.Invoke(target.Part, plane) ?? new(false, "Slicing is unavailable.");
        Status = result.Message;
        if (result.Success) SlicedParts++;
        return result;
    }
}
