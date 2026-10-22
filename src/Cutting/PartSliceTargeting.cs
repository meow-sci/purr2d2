using Brutal.Numerics;
using KSA;

namespace Purr2D2.Cutting;

/// <summary>Button-triggered geometry selection. No per-frame contact or damage update exists.</summary>
internal sealed partial class PartSliceTargeting
{
    private const int MaxTrianglesPerMesh = 500_000, MaxCandidateSubparts = 32_768;
    private const int MaxCachedMeshes = 8_192, MaxCachedTriangles = 4_000_000;
    private readonly Dictionary<MeshReference, CutMesh?> _meshes = new();
    private readonly List<int> _triangleCandidates = new();
    private readonly HashSet<MeshReference> _testedMeshes = new();
    private KittenEva? _wielder;
    private int _cachedTriangles;
    internal bool BudgetLimited { get; private set; }
    internal int SlicedParts { get; private set; }
    internal Func<Part, SlicePlane, SliceAttempt>? SliceHandler { get; set; }
    internal string Status { get; private set; } = "Ready";
    internal void Reset()
    {
        _meshes.Clear(); _triangleCandidates.Clear(); _testedMeshes.Clear();
        _cachedTriangles = SlicedParts = 0; _wielder = null; BudgetLimited = false; Status = "Ready";
    }

    private CutMesh? GetMesh(MeshReference reference)
    {
        if (_meshes.TryGetValue(reference, out var mesh))
        { if (mesh == null) BudgetLimited = true; return mesh; }
        if (_meshes.Count >= MaxCachedMeshes) { BudgetLimited = true; return null; }
        // A temporarily unavailable CPU mesh must be retried when loading finishes.
        if (reference.PositionsCompare is not { Length: > 0 } primitives) return null;
        var triangles = new List<CutTriangle>();
        foreach (var primitive in primitives)
        {
            if (primitive == null || primitive.Length % 3 != 0) continue;
            if (triangles.Count + primitive.Length / 3 > MaxTrianglesPerMesh
                || _cachedTriangles + triangles.Count + primitive.Length / 3 > MaxCachedTriangles)
            { _meshes[reference] = null; BudgetLimited = true; return null; }
            for (int i = 0; i < primitive.Length; i += 3)
            {
                var t = new CutTriangle(ToPoint(primitive[i]), ToPoint(primitive[i + 1]), ToPoint(primitive[i + 2]));
                if (t.A.Finite && t.B.Finite && t.C.Finite && CutPoint.Cross(t.B - t.A, t.C - t.A).LengthSquared > 1e-20) triangles.Add(t);
            }
        }
        mesh = triangles.Count > 0 ? new CutMesh(triangles.ToArray()) : null;
        _cachedTriangles += triangles.Count;
        if (mesh != null) _meshes[reference] = mesh;
        return mesh;
    }

    private bool CanCut(Part full, out string reason)
    {
        var owner = full.Tree?.OwningVehicle;
        reason = "The source part is unavailable.";
        if (owner == null || owner.IsDisposed || owner is KittenEva) return false;
        if (!owner.HasPhysicsBubble) reason = "Slicing requires a physics bubble";
        else if (!ReferenceEquals(owner.Parent, _wielder?.Parent)) reason = "Slicing requires the same parent body";
        else if (HasAssignedCrew(owner)) reason = "Crew aboard: cutting refused";
        else if (owner.BubbleVehicleCount >= PartFailure.MaximumVehiclesPerBubble) reason = "Debris budget reached";
        else if (full.IsAttachedInternal) reason = "Internal parts cannot be selected independently";
        else { reason = ""; return true; }
        return false;
    }

    private static bool HasAssignedCrew(Vehicle vehicle)
    { foreach (var seat in vehicle.Crew) if (seat.AssignedKittenHash != KeyHash.Zero) return true; return false; }
    private static CutPoint ToPoint(double3 value) => new(value.X, value.Y, value.Z);
    private static double3 ToGame(CutPoint value) => new(value.X, value.Y, value.Z);
    private static CutPoint Transform(CutPoint value, double4x4 matrix) => ToPoint(ToGame(value).Transform(matrix));
}
