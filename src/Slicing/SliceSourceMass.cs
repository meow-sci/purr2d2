using Brutal.Numerics;
using Brutal.Render.Mesh;
using KSA;
using RenderCore;

namespace Purr2D2.Slicing;

/// <summary>Resolve dry mass in the assembly frame native isolation will leave behind.</summary>
internal static class SliceSourceMass
{
    internal static float Resolve(Part source, IReadOnlyList<Part> sources)
    {
        float authored = sources.Sum(part => part.ComputeSubtreeInertMass());
        // Invalid authored properties must still fail preflight.
        if (authored != 0) return authored;
        if (source.Tree?.OwningVehicle is { } owner && owner.Parts.Count == sources.Count
            && sources.All(part => part.Tree == owner.Parts)) return owner.InertMass;

        var min = new double3(double.MaxValue);
        var max = new double3(double.MinValue);
        var rotation = doubleQuat.Inverse(source.Asmb2ParentAsmb);
        foreach (var part in sources)
        {
            if (source.TreeParent is null)
            {
                // Native child separations keep the existing root's assembly frame.
                min = double3.Min(min, part.BoundingBoxVehicleAsmb.Min);
                max = double3.Max(max, part.BoundingBoxVehicleAsmb.Max);
                continue;
            }

            // Native SetRootPose keeps the detached root's position and scale,
            // resets its rotation, and rebases the other full parts with it.
            var position = source.PositionParentAsmb + (part.PositionParentAsmb - source.PositionParentAsmb).Transform(rotation);
            var orientation = part == source ? doubleQuat.Identity
                : part.Asmb2ParentAsmb.Concatenate(rotation).NormalizeOrIdentity();
            var matrix = double4x4.CreateScale(part.Scale) * double4x4.CreateFromQuaternion(orientation)
                * double4x4.CreateTranslation(position);
            foreach (var sub in part.SubParts)
            {
                var transform = float4x4.Pack(sub.MatrixAsmb2ParentAsmb * matrix);
                foreach (var view in sub.Modules.Get<MeshViewModule>())
                {
                    if (!view.MeshView.HostMesh.TryGetVertexList(MeshAttribute.Position, out var vertices)) continue;
                    foreach (var vertex in vertices.ToSpan<float3>())
                    {
                        var point = double3.Unpack(vertex.Transform(transform));
                        min = double3.Min(min, point);
                        max = double3.Max(max, point);
                    }
                }
            }
        }

        float x = (float)(max.X - min.X), y = (float)(max.Y - min.Y), z = (float)(max.Z - min.Z);
        if (!(x > 0) || !(y > 0) || !(z > 0)) x = y = z = 1;
        // Match Vehicle.ComputeFallbackVehicleMassPropsAsmb's float arithmetic.
        return 100f * MassGeometry.ComputeSolidCuboid(x, y, z).Volume;
    }
}
