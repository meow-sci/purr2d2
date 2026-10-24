using Brutal.Numerics;
using KSA;
using Purr2D2.Core;
using Purr2D2.Rendering;

namespace Purr2D2.Force;

/// <summary>Preflight all authored surfaces, then replace a held orphan with inert native vehicles.
/// No failure events, probabilistic shedding, functional template clones or explosion calls.</summary>
internal sealed class ForceVehicleBreakup : IDisposable
{
    private sealed record Piece(AuthoredDebrisShape Shape, double3 Position, doubleQuat Rotation, double Weight);
    private readonly Vehicle _source;
    private readonly Part[] _parts;
    private readonly List<Piece> _pieces = new();
    private readonly List<Part> _roots = new();
    internal int Count => _pieces.Count;

    internal static bool NeedsBreakup(Vehicle source)
    {
        if (source is KittenEva) return false;
        // A generated debris root and its picking/render child are one physical object.
        if (source.Parts.Count == 1 && source.Parts.Root.Template.Id.StartsWith(AuthoredDebrisAssets.Prefix, StringComparison.Ordinal)) return false;
        if (source.Parts.Count > 1) return true;
        int visuals = 0;
        foreach (var full in source.Parts.Parts)
        {
            if (HasVisual(full)) visuals++;
            foreach (var sub in full.SubParts) if (HasVisual(sub)) visuals++;
        }
        return visuals > 1;
    }

    internal ForceVehicleBreakup(Vehicle source)
    {
        _source = source;
        _parts = source.Parts.Parts.ToArray();
        if (_parts.Length > 4096) throw new InvalidDataException("Vehicle exceeds the 4096-part breakup budget.");
        double dryMass = 0;
        foreach (var full in _parts)
        {
            // Bake only geometry that can actually be represented by the authored mesh.
            foreach (var node in new[] { full }.Concat(full.SubParts.ToArray()))
            {
                foreach (var deformation in node.Modules.Get<FxDeformation>())
                    if (FxDeformation.TryGetFrom(source.Parts.States, out var states) && states.GetState(deformation).Count > 0)
                        throw new InvalidDataException("Dented geometry cannot be broken up until its deformed surface can be baked.");
                foreach (var chute in node.Modules.Get<Parachute>())
                    if (chute.State is ChuteState.LineStretch or ChuteState.Reefed or ChuteState.Disreefing or ChuteState.Full)
                        throw new InvalidDataException("Retract or cut away deployed parachutes before breaking up their vehicle.");
            }
            int first = _pieces.Count;
            Add(full);
            foreach (var sub in full.SubParts) Add(sub);
            double partMass = full.ComputeSubtreeInertMass();
            if (!double.IsFinite(partMass) || partMass < 0) throw new InvalidDataException("Vehicle dry mass is invalid.");
            dryMass += partMass;
            double volume = _pieces.Skip(first).Sum(p => p.Weight);
            for (int i = first; i < _pieces.Count; i++)
                _pieces[i] = _pieces[i] with { Weight = partMass * _pieces[i].Weight / volume };
        }
        if (Count < 2) throw new InvalidDataException("The vehicle has fewer than two visible authored pieces.");
        double weights = _pieces.Sum(p => p.Weight);
        const double minimumMass = .0002;
        if (!double.IsFinite(dryMass) || dryMass <= Count * minimumMass || weights <= 0)
            throw new InvalidDataException("The vehicle has insufficient dry mass for separate debris.");
        for (int i = 0; i < Count; i++)
            _pieces[i] = _pieces[i] with { Shape = _pieces[i].Shape with
                { MassKg = (float)(minimumMass + (dryMass - Count * minimumMass) * _pieces[i].Weight / weights) } };
    }

    private void Add(Part node)
    {
        var models = Models(node).ToArray();
        if (models.Length == 0) return; // Invisible module/mass containers contribute their dry mass above.
        if (Count >= ForceCaptureLimits.Maximum) throw new InvalidDataException("Vehicle exceeds the 512-piece breakup budget.");
        var surfaces = new List<AuthoredDebrisSurface>();
        var min = new double3(double.PositiveInfinity); var max = new double3(double.NegativeInfinity);
        int vertices = 0;
        foreach (var (meshRef, materialRef, glass) in models)
        {
            var mesh = meshRef?.Get(); var material = materialRef?.Get();
            if (mesh?.PositionsCompare is not { } primitives || !mesh.IsReferenceable || string.IsNullOrEmpty(mesh.Id)
                || material == null || !material.IsReferenceable || string.IsNullOrEmpty(material.Id))
                throw new InvalidDataException("An authored surface has no durable mesh/material or accessible geometry.");
            surfaces.Add(new(mesh.Id, material.Id, glass));
            foreach (var primitive in primitives)
                foreach (var point in primitive)
                {
                    if (++vertices > 360_000 || !ForceMath.Finite(point)) throw new InvalidDataException("Authored debris geometry exceeds finite bounds or the vertex budget.");
                    min = double3.Min(min, point); max = double3.Max(max, point);
                }
        }
        if (vertices == 0 || !double4x4.Decompose(node.MatrixAsmb2VehicleAsmb, out var scale, out var rotation, out _)
            || !ForceMath.Finite(scale) || scale.X <= 0 || Math.Abs(scale.Y - scale.X) > 1e-6 || Math.Abs(scale.Z - scale.X) > 1e-6)
            throw new InvalidDataException("Authored debris requires a finite positive uniform mesh scale.");
        var center = min + (max - min) * .5;
        // Authored sheets can be planar. Give only the collider a thin finite thickness.
        var extents = double3.Max((max - min) * scale.X, new double3(.002));
        var position = center.Transform(node.MatrixAsmb2VehicleAsmb);
        if (!ForceMath.Finite(position) || !ForceMath.Finite(extents) || rotation.NormalizedOrZero() == default)
            throw new InvalidDataException("Authored debris has an invalid rigid frame.");
        var shape = new AuthoredDebrisShape(surfaces.ToArray(), center, extents, scale.X, 1);
        _pieces.Add(new(shape, position, rotation.NormalizedOrZero(), extents.X * extents.Y * extents.Z));
    }

    internal void Prepare()
    {
        foreach (var piece in _pieces)
        {
            var template = AuthoredDebrisAssets.Prepare(piece.Shape);
            var root = new Part(template.Id, template);
            _roots.Add(root);
            foreach (var collider in root.Modules.Get<ColliderModule>())
                if (!collider.Handle.Exists) throw new InvalidOperationException("A debris collider could not be cooked.");
            root.Tree = PartTree.CreateFromNewPartTree(root);
        }
    }

    internal Vehicle[] Commit()
    {
        if (_roots.Count != Count || _source.IsDisposed || _source.HasPhysicsBubble
            || !_source.Parts.Parts.SequenceEqual(_parts)) throw new InvalidOperationException("The held source changed before breakup.");
        var spawned = new List<Vehicle>();
        var creatingIds = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var rotation = _source.GetBody2Cci();
            var position = _source.GetPositionCci(); var velocity = _source.GetVelocityCci();
            var time = Universe.GetLastSimStep().NextTime;
            for (int i = 0; i < Count; i++)
            {
                var piece = _pieces[i];
                var offset = piece.Position - _source.CenterOfMassAsmb;
                var body2Cci = doubleQuat.Concatenate(piece.Rotation, rotation).NormalizedOrZero();
                var rates = _source.BodyRates.Transform(rotation).Transform(body2Cci.Inverse());
                var orbit = Orbit.CreateFromStateCci(_source.Parent, time, position + offset.Transform(rotation),
                    velocity + double3.Cross(_source.BodyRates, offset).Transform(rotation), _source.Orbit.OrbitLineColor);
                string id = "Purr2D2_Force_" + Guid.NewGuid().ToString("N");
                creatingIds.Add(id);
                var vehicle = Vehicle.CreateVehicle(_source.System, doubleQuat.Concatenate(body2Cci, _source.Parent.GetCci2Cce()),
                    rates, _source.Parent, id, _roots[i], orbit);
                spawned.Add(vehicle);
                _source.Parent.Children.Add(vehicle); // The same registration as native separated-vehicle adoption.
                vehicle.MarkAsDebris();
                vehicle.UpdatePerFrameData();
            }
            // Native overlap protection retires after clearance on release. While held,
            // these orphan workers do not run; terrain and unrelated craft are untouched.
            for (int i = 0; i < spawned.Count; i++)
                for (int j = i + 1; j < spawned.Count; j++)
                    CollisionAvoidancePair.Link(ForcePhysicsPatches.Worker(spawned[i]), ForcePhysicsPatches.Worker(spawned[j])).SuppressAllContacts = true;
            // Every replacement exists before the first irreversible source operation.
            // No solver can tick the source between draining its stores and deletion.
            _source.DepleteConsumables();
            _source.StopAudio(immediate: true);
            Universe.DestroyVehicle(_source, CrewDisposition.Kill);
            return spawned.ToArray();
        }
        catch (Exception error)
        {
            if (_source.IsDisposed && spawned.Count == Count)
            {
                Console.WriteLine($"[purr2d2] Force source teardown reported after replacement: {error.Message}");
                return spawned.ToArray();
            }
            // Native construction registers before its final initialization. Recover
            // even a constructor that threw before returning its new vehicle.
            foreach (var item in _source.System.All.AsSpan())
                if (item is Vehicle vehicle && creatingIds.Contains(vehicle.Id) && !spawned.Contains(vehicle)) spawned.Add(vehicle);
            foreach (var vehicle in spawned)
                try { RollBack(vehicle); }
                catch (Exception cleanup) { Console.WriteLine($"[purr2d2] Force debris cleanup failed: {cleanup}"); }
            throw;
        }
    }

    private static void RollBack(Vehicle vehicle)
    {
        if (ForcePhysicsPatches.Worker(vehicle) != null) { Universe.DestroyVehicle(vehicle); return; }
        try { vehicle.Dispose(endMission: false); }
        catch (Exception error) { Console.WriteLine($"[purr2d2] Incomplete Force debris teardown: {error.Message}"); }
        finally
        {
            vehicle.System.All.Deregister(vehicle);
            if (vehicle.Parts is { IsDisposed: false } tree) tree.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var root in _roots)
            if (root.Tree is { } tree) { if (!tree.IsDisposed && tree.OwningVehicle == null) tree.Dispose(); }
            else root.Dispose();
        _roots.Clear();
    }

    private static bool HasVisual(Part node) => !node.Modules.Get<PartModelModule>().IsEmpty
        || !node.Modules.Get<PartModelDynamicModule>().IsEmpty || !node.Modules.Get<PartModelGlassModule>().IsEmpty;
    private static IEnumerable<(MeshReference? Mesh, PbrMaterialReference? Material, bool Glass)> Models(Part node)
    {
        // Span-backed native module collections cannot cross iterator suspension.
        foreach (var model in node.Modules.Get<PartModelModule>().ToArray()) yield return (model.PartModel.Template.Mesh, model.PartModel.Template.Material, false);
        foreach (var model in node.Modules.Get<PartModelDynamicModule>().ToArray()) yield return (model.PartModelDynamic.Template.Mesh, model.PartModelDynamic.Template.Material, false);
        foreach (var model in node.Modules.Get<PartModelGlassModule>().ToArray()) yield return (model.PartModelGlass.Template.Mesh, model.PartModelGlass.Template.Material, true);
    }
}
