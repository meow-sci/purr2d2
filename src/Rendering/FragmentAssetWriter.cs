using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Brutal.Numerics;
using Purr2D2.Core;
using Purr2D2.Cutting;
using Purr2D2.Slicing;

namespace Purr2D2.Rendering;

internal sealed record CpuMeshUpload(string Name, int VertexBytes, int IndexBytes, byte[] Bytes);
internal readonly record struct SliceSpark(double3 Point, double3 Normal);
internal sealed record CpuFragmentAsset(string Id, byte[] Glb, byte[] Xml, CpuMeshUpload[] Uploads,
    string[] MeshNames, PrincipalFragment Frame, double3 Extents, float MassKg, bool PositiveSide, SliceSpark[] Sparks);

/// <summary>Pure worker-side export. No game objects, native registry, renderer or shared shape pool.</summary>
internal static class FragmentAssetWriter
{
    internal const string Prefix = "Purr2D2_Fragment_", CapMaterial = "Purr2D2_CutSurface";
    private static readonly SliceMaterialData CutSurface = new(CapMaterial, false);
    private readonly record struct SurfaceGroup(SliceMaterialData Material, SliceFace[] Faces, FragmentSolidRun[] SolidRuns)
    {
        internal bool Cap => Material.Id == CapMaterial;
        internal bool Foliage => Material.Foliage;
        internal SliceFace[] RenderFaces => !Foliage || !Material.DoubleSided ? Faces
            : Faces.Concat(Faces.Select(Backface)).ToArray();
        private SliceFace Backface(SliceFace face) => new(Back(face.A), Back(face.C), Back(face.B), face.Material);
        private SliceVertex Back(SliceVertex vertex) => Material.KeepBackfaceNormals ? vertex
            : vertex with { Normal = vertex.Normal * -1 };
    }

    internal static int CountRenderSurfaces(IEnumerable<SliceFace[]> solids, IReadOnlyDictionary<int, SliceMaterialData> materials,
        IEnumerable<SliceFace>? foliage = null)
        => solids.SelectMany(s => s).Concat(foliage ?? []).Select(f => ResolveMaterial(f.Material, materials)).Distinct().Count();

    private static SliceMaterialData ResolveMaterial(int index, IReadOnlyDictionary<int, SliceMaterialData> materials)
    {
        if (index == -1) return CutSurface;
        if (!materials.TryGetValue(index, out var material) || string.IsNullOrEmpty(material.Id))
            throw new InvalidDataException("A source surface has no durable material ID.");
        return material;
    }

    internal static CpuFragmentAsset Build(CpuFragment fragment, IReadOnlyDictionary<int, SliceMaterialData> materials, CancellationToken cancel,
        SliceGeometryBudget? geometry = null)
    {
        geometry ??= SliceGeometryBudget.Current;
        long triangles = fragment.Solids.Sum(s => (long)s.Length) + fragment.Foliage.Length;
        if (!geometry.AllowsFragment(fragment.Solids.Length, triangles))
            throw new InvalidDataException($"Fragment exceeds the export geometry budget ({geometry.FragmentUsage(fragment.Solids.Length, triangles)}). {SliceGeometryBudget.SettingsHint}");
        // Closed-solid boundaries belong to the geometry/recutting pipeline, not the
        // renderer. Concatenate faces without welding or changing their attributes.
        // Durable identity merges aliases while keeping opaque and glass passes apart.
        var transformed = fragment.Solids.SelectMany(s => s).ToArray();
        var surfaces = fragment.Solids.SelectMany((solid, i) => solid.Select(face => (Solid: i, Face: face)))
            .Concat(fragment.Foliage.Select(face => (Solid: -1, Face: face)))
            .GroupBy(f => ResolveMaterial(f.Face.Material, materials))
            .OrderBy(g => g.Key.Id, StringComparer.Ordinal).ThenBy(g => g.Key.Glass)
            .Select(g => new SurfaceGroup(g.Key, g.Select(f => f.Face).ToArray(),
                g.Key.Foliage ? [] : CountSolidRuns(g.Select(f => f.Solid)))).ToArray();
        if (surfaces.Any(s => s.Foliage && (s.Cap || s.Material.Glass))
            || fragment.Foliage.Any(f => !ResolveMaterial(f.Material, materials).Foliage)
            || fragment.Solids.SelectMany(s => s).Any(f => ResolveMaterial(f.Material, materials).Foliage))
            throw new InvalidDataException("Fragment closed solids and visual foliage have mixed surface provenance.");
        if (surfaces.Length > SliceResourceLimits.MaximumRenderSurfaces)
            throw new InvalidDataException($"Fragment surface count exceeds the export budget ({surfaces.Length:N0} / {SliceResourceLimits.MaximumRenderSurfaces:N0}).");
        long renderedTriangles = surfaces.Sum(s => (long)s.Faces.Length * (s.Foliage && s.Material.DoubleSided ? 2 : 1));
        if (!geometry.AllowsFragment(fragment.Solids.Length, renderedTriangles))
            throw new InvalidDataException($"Fragment render geometry exceeds the export budget ({geometry.FragmentUsage(fragment.Solids.Length, renderedTriangles)}). {SliceGeometryBudget.SettingsHint}");
        cancel.ThrowIfCancellationRequested();
        var collider = SliceColliderGeometry.Build(transformed, cancel);
        var picker = fragment.Foliage.Length == 0 ? transformed : transformed.Concat(fragment.Foliage).ToArray();
        var inertia = fragment.Frame.PrincipalSpecificInertia;
        string id;
        using (var hash = new IdentityHash(cancel, fragment.Foliage.Length != 0))
        {
            hash.Int32(surfaces.Length);
            foreach (var surface in surfaces)
            {
                hash.Text(surface.Material.Id);
                hash.Int32(surface.Material.Glass ? 1 : 0);
                hash.Int32(unchecked((int)surface.Material.ClutterColor));
                hash.Int32(unchecked((int)surface.Material.ClutterLuminosity));
                if (fragment.Foliage.Length != 0)
                {
                    hash.Int32(surface.Foliage ? 1 : 0);
                    hash.Int32(surface.Material.DoubleSided ? 1 : 0);
                    hash.Int32(surface.Material.KeepBackfaceNormals ? 1 : 0);
                }
                hash.Geometry(surface.Faces);
                // Batched triangles can represent independent touching solids or
                // one shell. Persisted recutting provenance is part of identity.
                hash.Int32(surface.SolidRuns.Length);
                foreach (var run in surface.SolidRuns) { hash.Int32(run.Solid); hash.Int32(run.Triangles); }
            }
            // Picker order also determines its compact indices and first attributes
            // at shared positions. Include both exported non-render meshes.
            hash.Geometry(picker);
            hash.Geometry(collider);
            id = Prefix + hash.Complete(fragment.MassKg, inertia);
        }
        // _s now indexes render surfaces. Retain this marker and _cap suffix so old
        // saved fragments and every cap group share the existing heat-owner lookup.
        var groups = surfaces.Select((s, i) => new FragmentGlbWriter.Group($"{id}_s{i}"
            + (s.Cap ? "_cap" : (s.Foliage ? "_fol" : "")
                + ClutterSurfaceTint.MeshSuffix(s.Material.ClutterColor, s.Material.ClutterLuminosity) + "_m" + i),
                s.Cap ? -1 : i, s.RenderFaces)).ToList();
        // Full visual picking mesh, plus a compact collider with exactly the same enclosing hull.
        groups.Add(new(id + "_hull", int.MinValue, picker));
        groups.Add(new(id + "_collision", int.MinValue, collider));
        var uploads = new CpuMeshUpload[surfaces.Length];
        for (int i = 0; i < uploads.Length; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var faces = groups[i].Faces;
            int vertexBytes = checked(faces.Length * 3 * 32), indexBytes = checked(faces.Length * 3 * 4);
            var bytes = new byte[vertexBytes + indexBytes];
            int offset = 0, index = 0;
            foreach (var face in faces) { Vertex(face.A); Vertex(face.B); Vertex(face.C); }
            uploads[i] = new(groups[i].Name, vertexBytes, indexBytes, bytes);
            void Vertex(SliceVertex vertex)
            {
                Scalar(vertex.Position.X); Scalar(vertex.Position.Y); Scalar(vertex.Position.Z);
                Scalar(vertex.Normal.X); Scalar(vertex.Normal.Y); Scalar(vertex.Normal.Z); Scalar(vertex.U); Scalar(vertex.V);
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(vertexBytes + index * 4), index++);
            }
            void Scalar(double value)
            { BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), (float)value); offset += 4; }
        }
        var sparks = new List<SliceSpark>();
        var caps = fragment.Piece.Faces.Where(f => f.Material == -1).ToArray();
        int stride = Math.Max(1, caps.Length / 24);
        for (int i = 0; i < caps.Length && sparks.Count < 24; i += stride)
        {
            var f = caps[i]; var center = (f.A.Position + f.B.Position + f.C.Position) / 3;
            var point = double3.Transform(new(center.X, center.Y, center.Z), fragment.Frame.SourceToFragment);
            var normal = double3.TransformNormal(new(f.A.Normal.X, f.A.Normal.Y, f.A.Normal.Z), fragment.Frame.SourceToFragment);
            sparks.Add(new(point, normal));
        }
        var xml = AssetXml(id, "Generated/Slices/" + id + ".glb", groups, surfaces, fragment.MassKg, inertia);
        // Cancellation between expensive serialization stages bounds abandoned work.
        cancel.ThrowIfCancellationRequested();
        var glb = FragmentGlbWriter.Write(groups, geometry);
        return new(id, glb, Encoding.UTF8.GetBytes(xml.ToString()), uploads, groups.Select(g => g.Name).ToArray(),
            fragment.Frame, fragment.Extents, fragment.MassKg, fragment.Piece.PositiveSide, sparks.ToArray());
    }

    private static XDocument AssetXml(string id, string glbPath, IReadOnlyList<FragmentGlbWriter.Group> groups,
        IReadOnlyList<SurfaceGroup> surfaces, float mass, double3 inertia)
    {
        var part = new XElement("Part", new XAttribute("Id", id), new XAttribute("DisplayName", "Sliced fragment"));
        string subpartId = id + "_visual";
        var subpart = new XElement("SubPart", new XAttribute("Id", subpartId),
            // Native part raycasts visit visual subparts. The single identity child
            // owns the complete picker, including every disconnected closed solid.
            new XElement("MeshView", new XAttribute("Id", id + "_pick"),
                new XElement("Mesh", new XAttribute("Id", id + "_hull"))));
        for (int i = 0; i < surfaces.Count; i++)
        {
            var group = groups[i];
            var material = surfaces[i].Material;
            var model = new XElement(material.Glass ? "PartModelGlass" : "PartModel", new XAttribute("Id", group.Name + "_model"),
                new XElement("Mesh", new XAttribute("Id", group.Name)),
                new XElement("Material", new XAttribute("Id", material.Id)),
                // Native part shadows are vertex-only and cannot alpha-test leaf
                // cards. Keep wood/cap shadows without casting opaque rectangles.
                new XElement("ShadowCaster", !surfaces[i].Foliage));
            // Native RT part geometry is ForceOpaque and ignores ShadowCaster.
            // Cards stay in the ordinary alpha-tested raster path in RT views too.
            if (surfaces[i].Foliage) model.Add(new XElement("RayTracing", "Disabled"));
            subpart.Add(model);
        }
        part.Add(new XElement("SubPart", new XAttribute("InstanceOf", subpartId)));
        part.Add(new XElement("Collider", new XAttribute("Id", id + "_collider"),
            new XElement("ConvexHull", new XAttribute("Id", id + "_hullCollider"),
                new XElement("Mesh", new XAttribute("Id", id + "_collision")))));
        part.Add(new XElement("CustomMass", new XElement("Mass", new XAttribute("Kg", F(mass))),
            new XElement("MassSpecificInertia", new XAttribute("Ixx", F(inertia.X)),
                new XAttribute("Iyy", F(inertia.Y)), new XAttribute("Izz", F(inertia.Z)))));
        var topology = new FragmentTopology(surfaces.SelectMany(s => s.SolidRuns).Max(r => r.Solid) + 1,
            surfaces.Select((s, i) => (Name: groups[i].Name, s.SolidRuns, s.Foliage)).Where(s => !s.Foliage)
                .ToDictionary(s => s.Name, s => s.SolidRuns, StringComparer.Ordinal),
            surfaces.Select((s, i) => (Name: groups[i].Name, Surface: s)).Where(s => s.Surface.Foliage)
                .ToDictionary(s => s.Name, s => new FragmentFoliageRun(s.Surface.Faces.Length,
                    s.Surface.Material.DoubleSided, s.Surface.Material.KeepBackfaceNormals), StringComparer.Ordinal));
        return new XDocument(new XElement("Assets", new XElement("MeshAtlas", new XAttribute("Id", id + "_atlas"),
            new XAttribute("Path", glbPath)), subpart, part, topology.ToXml()));
    }

    private static string F(double number) => number.ToString("R", CultureInfo.InvariantCulture);

    private static FragmentSolidRun[] CountSolidRuns(IEnumerable<int> solids)
    {
        var runs = new List<FragmentSolidRun>();
        foreach (int solid in solids)
        {
            if (runs.Count > 0 && runs[^1].Solid == solid)
                runs[^1] = runs[^1] with { Triangles = runs[^1].Triangles + 1 };
            else runs.Add(new(solid, 1));
        }
        return runs.ToArray();
    }

    /// <summary>Versioned, length-delimited little-endian content stream, without a discarded GLB.</summary>
    private sealed class IdentityHash : IDisposable
    {
        // A new identity never overwrites a v3 saved asset; startup still reads its
        // original XML/GLB and names. Bump this when durable export semantics change.
        private const string Version = "Purr2D2|merged-surfaces-pick-v5|durable-clutter-tint|canonical-le";
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private readonly IncrementalHash _hash;
        private readonly byte[] _buffer = new byte[16 * 1024];
        private readonly CancellationToken _cancel;
        private int _used;

        internal IdentityHash(CancellationToken cancel, bool foliage = false)
        {
            _cancel = cancel;
            cancel.ThrowIfCancellationRequested();
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Text(foliage ? "Purr2D2|wood-solids-foliage-v6|durable-clutter-tint|canonical-le" : Version);
        }

        internal void Int32(int value)
        {
            Ensure(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_used), value);
            _used += sizeof(int);
        }

        internal void Text(string value)
        {
            Int32(Utf8.GetByteCount(value));
            var encoder = Utf8.GetEncoder();
            ReadOnlySpan<char> remaining = value.AsSpan();
            bool complete;
            do
            {
                Ensure(4); // Space for a complete UTF-8 scalar, even at a chunk boundary.
                encoder.Convert(remaining, _buffer.AsSpan(_used), true, out int characters, out int bytes, out complete);
                _used += bytes;
                remaining = remaining[characters..];
            } while (!complete);
        }

        internal void Geometry(SliceFace[] faces)
        {
            _cancel.ThrowIfCancellationRequested();
            Int32(faces.Length);
            foreach (var face in faces) { Vertex(face.A); Vertex(face.B); Vertex(face.C); }
        }

        private void Vertex(SliceVertex vertex)
        {
            Single(vertex.Position.X, position: true); Single(vertex.Position.Y, position: true); Single(vertex.Position.Z, position: true);
            Single(vertex.Normal.X); Single(vertex.Normal.Y); Single(vertex.Normal.Z); Single(vertex.U); Single(vertex.V);
        }

        private void Single(double value, bool position = false)
        {
            float exported = (float)value;
            if (!float.IsFinite(exported)) throw new InvalidDataException("Fragment vertices contain non-finite float attributes.");
            // SliceMeshPrecision prepares positions at float32 before export. This
            // invariant makes exact CutPoint deduplication in the picker a function
            // of the same values hashed here, including its first-vertex ordering.
            if (position && value != exported)
                throw new InvalidDataException("Fragment positions must be prepared at float32 precision.");
            Ensure(sizeof(float));
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_used), exported);
            _used += sizeof(float);
        }

        internal string Complete(float mass, double3 inertia)
        {
            if (!float.IsFinite(mass) || !double.IsFinite(inertia.X) || !double.IsFinite(inertia.Y) || !double.IsFinite(inertia.Z))
                throw new InvalidDataException("Fragment mass and inertia must be finite.");
            Single(mass);
            Double(inertia.X); Double(inertia.Y); Double(inertia.Z);
            Flush();
            return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        private void Double(double value)
        {
            Ensure(sizeof(double));
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_used), value);
            _used += sizeof(double);
        }

        private void Ensure(int bytes)
        {
            if (_buffer.Length - _used < bytes) Flush();
        }

        private void Flush()
        {
            _cancel.ThrowIfCancellationRequested();
            _hash.AppendData(_buffer.AsSpan(0, _used));
            _used = 0;
        }

        public void Dispose() => _hash.Dispose();
    }
}
