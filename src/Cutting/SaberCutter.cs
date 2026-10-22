using System;
using System.Collections.Generic;
using System.Reflection;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Cutting;

/// <summary>
/// Call once per consumed applied simulation step, after all solver results commit and before
/// the next solver dispatch. The caller must join the nearest-orbit reader before this call.
/// Inputs are metres in the wielder's centre-of-mass BODY frame, never absolute floats.
/// </summary>
public sealed class SaberCutter
{
    private const double Radius = .025;
    private const int MaxTrianglesPerMesh = 500_000;
    private const int MaxTriangleTestsPerStep = 100_000;
    private const int MaxCandidateSubparts = 32_768;
    private const int MaxCachedMeshes = 8_192;
    private const int MaxCachedTriangles = 4_000_000;
    private const double MaximumSweepTravel = .75;
    private readonly Dictionary<MeshReference, CutMesh?> _meshes = new();
    private readonly Dictionary<Part, History> _history = new();
    private readonly Dictionary<Part, HeatState> _heat = new();
    private readonly HashSet<Part> _touched = new();
    private readonly List<Part> _remove = new();
    private readonly List<Candidate> _candidates = new();
    private readonly HashSet<Part> _candidateParts = new();
    private readonly List<int> _triangleCandidates = new();
    private readonly HashSet<MeshReference> _testedMeshes = new();
    private readonly List<Vehicle> _fragments = new();
    private readonly List<Part> _nativeSubparts = new();
    private readonly HashSet<Vehicle> _beforeFragmentation = new();
    private KittenEva? _wielder;
    private int _cachedTriangles;

    public bool Enabled { get; set; } = true;
    public double ContactSeconds { get; set; } = .75;
    /// <summary>Opt-in: destroy the struck full part with native subpart-debris shedding.</summary>
    public bool FragmentStruckPart { get; set; }
    public bool ArbitrarySlicing { get; set; }
    internal Func<Part, SlicePlane, SliceAttempt>? SliceHandler { get; set; }
    public bool FragmentationAvailable => NativeFragment != null;
    public string Status { get; private set; } = "Ready";
    public int Contacts { get; private set; }
    public int TriangleTests { get; private set; }
    public int SeveredConnections { get; private set; }
    public int FragmentedParts { get; private set; }
    public int SlicedParts { get; private set; }
    public bool BudgetLimited { get; private set; }
    public double HottestContactSeconds { get; private set; }
    public int VehiclesInRange { get; private set; }
    public int CandidateParts => _candidates.Count;
    public int TestedParts { get; private set; }
    public int MeshesTested { get; private set; }
    public int MissingMeshes { get; private set; }
    public int DiscontinuousParts { get; private set; }
    public string ContactSurface { get; private set; } = "none";
    private double3 _baseBody, _tipBody;
    private string _nearestVehicle = "none";
    private double _nearestDistance = double.PositiveInfinity;
    /// <summary>Endpoints and normal belong to the exact struck subpart's local assembly frame.</summary>
    public Action<Part, double3, double3, double3, double>? Scar { get; set; }

    private readonly record struct History(CutPoint A, CutPoint B, Vehicle Owner);
    private readonly record struct Candidate(Part Part, Vehicle Vehicle, double Distance);
    private sealed class HeatState
    {
        public readonly CutHeat Heat = new();
        public Part? Surface;
        public double3 LocalHit;
        public double3 LocalNormal;
        public double LastScarTime = double.NegativeInfinity;
        public bool HasPreviousScar;
        public double3 PreviousScar;
        public double Cooldown;
        public SlicePlane Plane;
        public bool HasPlane, HasSweptPlane;
        public Vehicle? PlaneOwner;
        public string? LastCutStatus;
    }

    // This is the stock blast-free primitive. PartFailureEvent.Apply adds explosions around it;
    // binding the exact native method avoids replacing global explosion behavior or RNG.
    private static readonly Action<Vehicle, Part, List<Vehicle>>? NativeFragment = BindFragmentation();
    private static Action<Vehicle, Part, List<Vehicle>>? BindFragmentation()
    {
        try
        {
            var method = typeof(PartFailure).GetMethod("IsolateAndDestroy", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(Vehicle), typeof(Part), typeof(List<Vehicle>) }, null);
            return method?.CreateDelegate<Action<Vehicle, Part, List<Vehicle>>>();
        }
        catch { return null; }
    }

    public void Reset()
    {
        Suspend(); _meshes.Clear(); _cachedTriangles = 0; _wielder = null;
        Contacts = TriangleTests = SeveredConnections = FragmentedParts = SlicedParts = 0;
        HottestContactSeconds = 0; BudgetLimited = false; Status = "Ready";
    }

    /// <summary>Drop interrupted contact/sweep state while keeping mesh caches and lifetime statistics.</summary>
    public void Suspend()
    {
        _history.Clear(); _heat.Clear(); _candidates.Clear(); _candidateParts.Clear(); _touched.Clear(); _testedMeshes.Clear();
        Contacts = TriangleTests = 0; HottestContactSeconds = 0; BudgetLimited = false;
        ResetDiagnostics();
        Status = "Inactive";
    }

    private void ResetDiagnostics()
    {
        VehiclesInRange = TestedParts = MeshesTested = MissingMeshes = DiscontinuousParts = 0;
        ContactSurface = _nearestVehicle = "none";
        _nearestDistance = double.PositiveInfinity;
        _baseBody = _tipBody = double3.Zero;
    }

    public string GetDiagnostics() => FormattableString.Invariant($"""
        Status: {Status}
        Nearby vessels: {VehiclesInRange}; candidate parts/subparts: {CandidateParts}; tested: {TestedParts}
        Meshes tested: {MeshesTested}; unavailable: {MissingMeshes}; triangles tested: {TriangleTests}
        Contacts: {Contacts}; hottest: {HottestContactSeconds:F3} s; surface: {ContactSurface}
        Skipped motion jumps: {DiscontinuousParts}; budget limited: {BudgetLimited}
        Nearest vessel: {_nearestVehicle}; centre to blade midpoint: {_nearestDistance:F3} m
        Blade body metres: ({_baseBody.X:F3}, {_baseBody.Y:F3}, {_baseBody.Z:F3}) -> ({_tipBody.X:F3}, {_tipBody.Y:F3}, {_tipBody.Z:F3})
        Mesh cache: {_meshes.Count} entries / {_cachedTriangles} triangles
        """);

    public void Update(KittenEva wielder, double3 baseBody, double3 tipBody, double appliedDeltaTime, double nowPlayerSeconds)
    {
        Contacts = TriangleTests = 0; BudgetLimited = false; HottestContactSeconds = 0;
        if (_wielder != wielder) { Reset(); _wielder = wielder; }
        ResetDiagnostics();
        if (!Enabled || wielder.IsDisposed || Program.EditorFlag || Universe.CurrentSystem == null
            || Program.GetMainCamera() is not { } camera || !(appliedDeltaTime > 0) || appliedDeltaTime > .15
            || !double.IsFinite(appliedDeltaTime) || !double.IsFinite(nowPlayerSeconds))
        { Suspend(); return; }
        var a = ToPoint(baseBody); var b = ToPoint(tipBody);
        if (!a.Finite || !b.Finite || (b - a).Length < .025 || (b - a).Length > 3)
        { Suspend(); return; }
        Status = "Blade clear";
        _baseBody = baseBody; _tipBody = tipBody;
        var bodyToEgo = double4x4.CreateFromQuaternion(wielder.Body2Ego)
            * double4x4.CreateTranslation(camera.GetPositionEgo(wielder));
        if (!double4x4.Invert(bodyToEgo, out var egoToBody)) { Suspend(); return; }
        var aEgo = baseBody.Transform(bodyToEgo); var bEgo = tipBody.Transform(bodyToEgo);
        _touched.Clear(); _candidates.Clear(); _candidateParts.Clear();

        // Resolve a fresh membership snapshot, including debris, then sort deterministically.
        // Do not enumerate the live system during Split/CreateVehicle.
        var all = Universe.CurrentSystem.All.AsSpan();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] is not Vehicle vehicle || vehicle == wielder || vehicle is KittenEva
                || vehicle.IsDisposed || vehicle.IsEditedVehicle) continue;
            double centreDistance = (camera.GetPositionEgo(vehicle) - (aEgo + bEgo) * .5).Length();
            if (centreDistance < _nearestDistance) { _nearestDistance = centreDistance; _nearestVehicle = vehicle.Id; }
            if (centreDistance > vehicle.BoundingSphereRadiusBody + 4) continue;
            VehiclesInRange++;
            // Contact is geometric. Crew and physics eligibility only restrict structural changes.
            foreach (var full in vehicle.Parts.Parts)
            {
                Offer(full);
                foreach (var sub in full.SubParts) Offer(sub);
            }
            void Offer(Part part)
            {
                if ((part.Modules.HasAny<MeshViewModule>() || part.Modules.HasAny<PartModelModule>())
                    && _candidateParts.Add(part))
                {
                    var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
                    double distance = (part.PositionEgo(in vehicleMatrix) - aEgo).LengthSquared();
                    _candidates.Add(new(part, vehicle, distance));
                }
            }
        }
        _candidates.Sort((x, y) => { int c = x.Distance.CompareTo(y.Distance); return c != 0 ? c : x.Part.InstanceId.CompareTo(y.Part.InstanceId); });
        int considered = 0;
        foreach (var candidate in _candidates)
        {
            if (++considered > MaxCandidateSubparts || TriangleTests >= MaxTriangleTestsPerStep) { BudgetLimited = true; break; }
            Test(candidate);
        }

        // A full part heats once per step, even if several subparts/triangles touch the blade.
        // Iterate a snapshot: native splitting reassigns part.Tree and can dispose entire hosts.
        _remove.Clear();
        bool attemptedStructuralChange = false;
        foreach (var pair in _heat)
        {
            var full = pair.Key; var state = pair.Value;
            if (full.Tree?.OwningVehicle is not { IsDisposed: false }) { _remove.Add(full); continue; }
            bool contact = _touched.Contains(full);
            state.Cooldown = Math.Max(0, state.Cooldown - appliedDeltaTime);
            bool hot = state.Heat.Advance(contact, appliedDeltaTime, ContactSeconds);
            HottestContactSeconds = Math.Max(HottestContactSeconds, state.Heat.Seconds);
            if (contact && state.LastCutStatus != null) Status = state.LastCutStatus;
            if (contact && hot && state.Cooldown <= 0 && !attemptedStructuralChange)
            {
                attemptedStructuralChange = true;
                state.Cooldown = 1;
                if (TrySever(full, state, nowPlayerSeconds)) _remove.Add(full);
                else state.LastCutStatus = Status;
            }
            else if (!contact && state.Heat.Seconds <= 0) _remove.Add(full);
        }
        foreach (var part in _remove) _heat.Remove(part);
        // Histories must not retain vessels after leaving range, loading, or native destruction.
        _remove.Clear();
        foreach (var pair in _history)
            if (pair.Key.Tree?.OwningVehicle is not { IsDisposed: false } || !_candidateParts.Contains(pair.Key)) _remove.Add(pair.Key);
        foreach (var part in _remove) _history.Remove(part);
        if (BudgetLimited) Status = (Status == "Blade clear" ? "" : Status + ". ")
            + "Contact scan limited; untested parts do not heat";
        else if (Contacts == 0 && Status == "Blade clear")
        {
            if (VehiclesInRange == 0) Status = "Blade clear: no nearby vessels";
            else if (CandidateParts == 0) Status = "Nearby parts have no contact mesh geometry";
            else if (MeshesTested == 0) Status = "Contact mesh geometry unavailable";
        }

        void Test(Candidate candidate)
        {
            TestedParts++;
            var part = candidate.Part;
            var matrix = candidate.Vehicle.GetMatrixAsmb2Ego(camera);
            var partToEgo = part.MatrixAsmb2Ego(in matrix);
            if (!double4x4.Invert(partToEgo, out var egoToPart)) return;
            var localA = ToPoint(aEgo.Transform(egoToPart)); var localB = ToPoint(bEgo.Transform(egoToPart));
            if (!localA.Finite || !localB.Finite) return;
            var old = _history.TryGetValue(part, out var history) && history.Owner == candidate.Vehicle
                ? history : new History(localA, localB, candidate.Vehicle);
            _history[part] = new(localA, localB, candidate.Vehicle);
            var partToBody = partToEgo * egoToBody;
            var previousA = Transform(old.A, partToBody); var previousB = Transform(old.B, partToBody);
            if (!CutGeometry.MotionWithinLimit(previousA, previousB, a, b, MaximumSweepTravel))
            { DiscontinuousParts++; Status = "Motion discontinuity: contact skipped"; return; }
            // Frobenius norm bounds scale/shear of the inverse; broadphase is conservative.
            double inverseNorm = Math.Sqrt(egoToPart.M11 * egoToPart.M11 + egoToPart.M12 * egoToPart.M12 + egoToPart.M13 * egoToPart.M13
                + egoToPart.M21 * egoToPart.M21 + egoToPart.M22 * egoToPart.M22 + egoToPart.M23 * egoToPart.M23
                + egoToPart.M31 * egoToPart.M31 + egoToPart.M32 * egoToPart.M32 + egoToPart.M33 * egoToPart.M33);
            var query = CutBounds.Sweep(old.A, old.B, localA, localB, Radius * inverseNorm * 2);
            _testedMeshes.Clear();
            foreach (var view in part.Modules.Get<MeshViewModule>())
                if (TestMesh(view.MeshView)) return;
            // Some authored surfaces have a render model but no separate picking MeshView.
            foreach (var model in part.Modules.Get<PartModelModule>())
                if (model.PartModel.Template.Mesh?.Get() is { } reference && TestMesh(reference)) return;

            bool TestMesh(MeshReference reference)
            {
                if (!_testedMeshes.Add(reference)) return false;
                var mesh = GetMesh(reference);
                if (mesh == null) { MissingMeshes++; return false; }
                MeshesTested++;
                mesh.Query(query, _triangleCandidates);
                foreach (int index in _triangleCandidates)
                {
                    if (++TriangleTests > MaxTriangleTestsPerStep) { BudgetLimited = true; return true; }
                    var local = mesh.Triangles[index];
                    var triangle = new CutTriangle(Transform(local.A, partToBody), Transform(local.B, partToBody), Transform(local.C, partToBody));
                    if (!CutGeometry.SweepContact(previousA, previousB, a, b, triangle, Radius, out var hit)) continue;
                    var hitEgo = ToGame(hit).Transform(bodyToEgo);
                    var assemblyMatrix = part.MatrixAsmb2VehicleAsmb;
                    var currentA = Transform(localA, assemblyMatrix);
                    var currentB = Transform(localB, assemblyMatrix);
                    var previousAssemblyA = Transform(old.A, assemblyMatrix);
                    var previousAssemblyB = Transform(old.B, assemblyMatrix);
                    var motion = (currentA + currentB - previousAssemblyA - previousAssemblyB) * .5;
                    var direction = currentB - currentA;
                    var planeNormal = CutPoint.Cross(direction, motion);
                    bool swept = planeNormal.Length > 1e-5 && motion.Length > .002;
                    if (!swept)
                    {
                        var faceNormal = CutPoint.Cross(Transform(local.B, assemblyMatrix) - Transform(local.A, assemblyMatrix),
                            Transform(local.C, assemblyMatrix) - Transform(local.A, assemblyMatrix)).Normalized;
                        planeNormal = CutPoint.Cross(direction, faceNormal);
                        if (planeNormal.Length < 1e-8)
                            planeNormal = CutPoint.Cross(direction, Math.Abs(direction.Normalized.X) < .8 ? new CutPoint(1, 0, 0) : new CutPoint(0, 1, 0));
                    }
                    planeNormal = planeNormal.Normalized;
                    var plane = new SlicePlane(planeNormal, CutPoint.Dot(planeNormal, (currentA + currentB) * .5));
                    RegisterContact(part, hitEgo.Transform(egoToPart), ToGame(local.Normal), nowPlayerSeconds, plane, swept);
                    return true;
                }
                return false;
            }
        }
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

    private void RegisterContact(Part surface, double3 localHit, double3 localNormal, double now, SlicePlane plane, bool swept)
    {
        var full = surface.FullPart;
        if (!_touched.Add(full)) return;
        Contacts++;
        ContactSurface = $"{surface.Tree?.OwningVehicle?.Id} / {full.DisplayName} / {surface.Id}";
        // Reporting contact must not depend on the slower scar-emission interval.
        Status = $"Heating {full.DisplayName}";
        if (!_heat.TryGetValue(full, out var state)) _heat.Add(full, state = new());
        if (!ReferenceEquals(state.PlaneOwner, full.Tree?.OwningVehicle))
        {
            state.HasPlane = state.HasSweptPlane = false;
            state.LastCutStatus = null;
            state.PlaneOwner = full.Tree?.OwningVehicle;
        }
        // Keep a deliberate swing plane when the blade comes to rest during heating.
        if (swept || !state.HasSweptPlane)
        { state.Plane = plane; state.HasPlane = true; state.HasSweptPlane |= swept; }
        bool sameSurface = state.Surface == surface;
        state.Surface = surface; state.LocalHit = localHit; state.LocalNormal = localNormal;
        if (now - state.LastScarTime < .04) return;
        // Small burns remain visible during stationary contact. Bound individual ribbons so an
        // abrupt blade/target jump cannot draw a long glowing line across disconnected surfaces.
        var tangent = double3.Cross(localNormal, Math.Abs(localNormal.X) < .8 ? double3.UnitX : double3.UnitY).NormalizeOrZero();
        double3 start = sameSurface && state.HasPreviousScar && (state.PreviousScar - localHit).Length() < .15
            ? state.PreviousScar : localHit - tangent * .007;
        double3 end = (start - localHit).LengthSquared() < 1e-8 ? localHit + tangent * .007 : localHit;
        Scar?.Invoke(surface, start, end, localNormal, now);
        state.PreviousScar = localHit; state.HasPreviousScar = true; state.LastScarTime = now;
    }

    private bool TrySever(Part full, HeatState contact, double now)
    {
        var owner = full.Tree?.OwningVehicle;
        if (owner == null || owner.IsDisposed || owner is KittenEva) return false;
        if (!owner.HasPhysicsBubble) { Status = "Contact detected; cutting requires a physics bubble"; return false; }
        if (!ReferenceEquals(owner.Parent, _wielder?.Parent))
        { Status = "Contact detected; cutting requires the same parent body"; return false; }
        if (HasAssignedCrew(owner)) { Status = "Crew aboard: cutting refused"; return false; }
        if (owner.BubbleVehicleCount >= PartFailure.MaximumVehiclesPerBubble)
        { Status = "Debris budget reached"; return false; }
        if (full.IsAttachedInternal) { Status = "Internal part: surface scorched"; return false; }
        if (ArbitrarySlicing)
        {
            if (SliceHandler == null || !contact.HasPlane)
            { Status = "Arbitrary slicing is unavailable"; return false; }
            SliceAttempt result = SliceHandler(full, contact.Plane);
            Status = result.Message;
            if (result.Success) SlicedParts++;
            return result.Success;
        }
        if (FragmentStruckPart) return TryFragment(owner, full, contact, now);
        var root = full.TreeParent != null ? full : ChooseChildBoundary(full, contact);
        if (root == null) { Status = "No safe full-part boundary; surface scorched"; return false; }
        var subtree = new HashSet<Part> { root };
        AddChildren(root);
        Part.Connection? crossing = null;
        int crossings = 0;
        foreach (var node in subtree)
        {
            CountConnections(node);
            foreach (var sub in node.SubParts) CountConnections(sub);
        }
        if (crossings != 1 || crossing == null)
        { Status = "Reinforced connection graph: surface scorched"; return false; }
        var endpointPart0 = crossing.Connectors[0].ConnectionPart;
        var endpointPart1 = crossing.Connectors[1].ConnectionPart;
        if (!((endpointPart0 == root && endpointPart1 == root.TreeParent)
            || (endpointPart1 == root && endpointPart0 == root.TreeParent)))
        { Status = "Subpart connection requires native fragmentation; surface scorched"; return false; }
        var endpoint = crossing.Connectors[0];
        var piece = owner.Split(crossing, endpoint, 0, out _);
        if (piece == null) { Status = "Native separation refused"; return false; }
        // Preserve any functional or crewed fragment as an ordinary craft.
        if (!piece.IsControllable && !HasAssignedCrew(piece)) piece.MarkAsDebris();
        SeveredConnections++; Status = $"Severed {full.DisplayName} at its part boundary";
        return true;

        void AddChildren(Part node) { foreach (var child in node.TreeChildren) if (subtree.Add(child)) AddChildren(child); }
        void CountConnections(Part node)
        {
            foreach (var connection in node.Connections)
                if (!subtree.Contains(connection.OtherPart(node).FullPart)) { crossings++; crossing = connection; }
        }
    }

    private static Part? ChooseChildBoundary(Part root, HeatState contact)
    {
        Part? best = null; double distance = double.PositiveInfinity;
        double3 hit = contact.Surface == null ? root.PositionVehicleAsmb : contact.LocalHit.Transform(contact.Surface.MatrixAsmb2VehicleAsmb);
        foreach (var child in root.TreeChildren)
        {
            if (child.IsAttachedInternal) continue;
            double d = (child.PositionVehicleAsmb - hit).LengthSquared();
            if (best == null || d < distance || (d == distance && child.InstanceId < best.InstanceId)) { best = child; distance = d; }
        }
        return best;
    }

    private bool TryFragment(Vehicle owner, Part full, HeatState contact, double now)
    {
        var system = Universe.CurrentSystem;
        if (system == null) return false;
        if (NativeFragment == null) { Status = "Native subpart fragmentation unavailable in this game build"; return false; }
        if (HasAssignedCrew(owner)) { Status = "Crew aboard: fragmentation refused"; return false; }
        PartFailure.CollectSubPartDebris(full, _nativeSubparts);
        foreach (var subpart in _nativeSubparts)
        {
            var scale = subpart.ScaleTotal;
            if (!double.IsFinite(scale.X + scale.Y + scale.Z)
                || Math.Abs(scale.X - 1) > 1e-6 || Math.Abs(scale.Y - 1) > 1e-6 || Math.Abs(scale.Z - 1) > 1e-6)
            { Status = "Scaled subpart debris unsupported by native spawning"; return false; }
        }
        // Native IsolateAndDestroy operates on full-part Connections. Do not feed it custom
        // external subpart links, which its current isolation loop does not traverse.
        foreach (var node in owner.Parts.Parts)
            foreach (var sub in node.SubParts)
                if (sub.Connections.Count > 0)
                { Status = "Custom subpart connection graph: fragmentation refused"; return false; }
        var checkedConnections = new HashSet<Part.Connection>();
        var checkedEdges = new HashSet<(uint, uint)>();
        foreach (var node in owner.Parts.Parts)
            foreach (var connection in node.Connections)
            {
                if (!checkedConnections.Add(connection)) continue;
                var first = connection.Connectors[0].ConnectionPart;
                var second = connection.Connectors[1].ConnectionPart;
                var edge = first.InstanceId < second.InstanceId ? (first.InstanceId, second.InstanceId) : (second.InstanceId, first.InstanceId);
                if (first.IsSubPart || second.IsSubPart || first.Tree != owner.Parts || second.Tree != owner.Parts
                    || !(first.TreeParent == second || second.TreeParent == first) || !checkedEdges.Add(edge))
                { Status = "Reinforced connection graph: fragmentation refused"; return false; }
            }
        int connections = PartFailure.SeverableConnectionCount(full);
        if (owner.BubbleVehicleCount + Math.Max(0, connections - 1) + PartFailure.MaximumSubPartDebris > PartFailure.MaximumVehiclesPerBubble)
        { Status = "Insufficient debris slots for native part fragmentation"; return false; }
        _beforeFragmentation.Clear();
        foreach (var item in system.All.AsSpan()) if (item is Vehicle v) _beforeFragmentation.Add(v);
        _fragments.Clear();
        NativeFragment(owner, full, _fragments);
        // Native shedding clones templates and assigns fresh InstanceIds. Re-emit this contact on
        // the matching clone; retained full-part splits keep the original Part object unchanged.
        if (contact.Surface != null)
            foreach (var item in system.All.AsSpan())
                if (item is Vehicle { IsDisposed: false } v && !_beforeFragmentation.Contains(v)
                    && v.IsDebris && v.Parts.Root?.Template == contact.Surface.Template)
                {
                    var n = contact.LocalNormal;
                    var tangent = double3.Cross(n, Math.Abs(n.X) < .8 ? double3.UnitX : double3.UnitY).NormalizeOrZero();
                    Scar?.Invoke(v.Parts.Root, contact.LocalHit - tangent * .02, contact.LocalHit + tangent * .02, n, now);
                }
        bool destroyed = full.Tree?.OwningVehicle is not { IsDisposed: false };
        if (destroyed) { FragmentedParts++; Status = "Struck part fragmented with native subpart debris"; }
        else Status = "Native isolation refused; retained surface scorches";
        return destroyed;
    }

    private static bool HasAssignedCrew(Vehicle vehicle)
    { foreach (var seat in vehicle.Crew) if (seat.AssignedKittenHash != KeyHash.Zero) return true; return false; }
    private static CutPoint ToPoint(double3 value) => new(value.X, value.Y, value.Z);
    private static double3 ToGame(CutPoint value) => new(value.X, value.Y, value.Z);
    private static CutPoint Transform(CutPoint value, double4x4 matrix) => ToPoint(ToGame(value).Transform(matrix));
}
