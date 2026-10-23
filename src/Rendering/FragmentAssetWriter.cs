using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Brutal.Numerics;
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
    }

    internal static int CountRenderSurfaces(IEnumerable<SliceFace[]> solids, IReadOnlyDictionary<int, SliceMaterialData> materials)
        => solids.SelectMany(s => s).Select(f => ResolveMaterial(f.Material, materials)).Distinct().Count();

    private static SliceMaterialData ResolveMaterial(int index, IReadOnlyDictionary<int, SliceMaterialData> materials)
    {
        if (index == -1) return CutSurface;
        if (!materials.TryGetValue(index, out var material) || string.IsNullOrEmpty(material.Id))
            throw new InvalidDataException("A source surface has no durable material ID.");
        return material;
    }

    internal static CpuFragmentAsset Build(CpuFragment fragment, IReadOnlyDictionary<int, SliceMaterialData> materials, CancellationToken cancel)
    {
        // Closed-solid boundaries belong to the geometry/recutting pipeline, not the
        // renderer. Concatenate faces without welding or changing their attributes.
        // Durable identity merges aliases while keeping opaque and glass passes apart.
        var transformed = fragment.Solids.SelectMany(s => s).ToArray();
        var surfaces = fragment.Solids.SelectMany((solid, i) => solid.Select(face => (Solid: i, Face: face)))
            .GroupBy(f => ResolveMaterial(f.Face.Material, materials))
            .OrderBy(g => g.Key.Id, StringComparer.Ordinal).ThenBy(g => g.Key.Glass)
            .Select(g => new SurfaceGroup(g.Key, g.Select(f => f.Face).ToArray(),
                CountSolidRuns(g.Select(f => f.Solid)))).ToArray();
        if (surfaces.Length > 2046) throw new InvalidDataException("Fragment surface count exceeds the export budget.");
        cancel.ThrowIfCancellationRequested();
        var collider = SliceColliderGeometry.Build(transformed, cancel);
        var seedGroups = surfaces.Select((s, i) => new FragmentGlbWriter.Group($"surface_{i}", s.Cap ? -1 : i, s.Faces)).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(FragmentGlbWriter.Write(seedGroups));
        hash.AppendData(Encoding.UTF8.GetBytes(string.Join("|", surfaces.Select(s => s.Material.Id + ":glass=" + s.Material.Glass))));
        // Identical batched triangles can describe touching independent solids or a
        // connected shell. Include provenance so immutable IDs cannot alias the two.
        hash.AppendData(Encoding.UTF8.GetBytes(string.Join("|", surfaces.Select(s => string.Join(",", s.SolidRuns.Select(r => $"{r.Solid}:{r.Triangles}"))))));
        var inertia = fragment.Frame.PrincipalSpecificInertia;
        // New collider/export layout gets new immutable IDs; old saved fragments remain untouched.
        hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"|merged-surfaces-pick-v3|{fragment.MassKg:R}|{inertia.X:R}|{inertia.Y:R}|{inertia.Z:R}")));
        string id = Prefix + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        // _s now indexes render surfaces. Retain this marker and _cap suffix so old
        // saved fragments and every cap group share the existing heat-owner lookup.
        var groups = surfaces.Select((s, i) => new FragmentGlbWriter.Group($"{id}_s{i}" + (s.Cap ? "_cap" : "_m" + i), s.Cap ? -1 : i, s.Faces)).ToList();
        // Full visual picking mesh, plus a compact collider with exactly the same enclosing hull.
        groups.Add(new(id + "_hull", int.MinValue, transformed));
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
        var glb = FragmentGlbWriter.Write(groups);
        return new(id, glb, Encoding.UTF8.GetBytes(xml.ToString()), uploads, groups.Select(g => g.Name).ToArray(),
            fragment.Frame, fragment.Extents, fragment.MassKg, fragment.Piece.PositiveSide, sparks.ToArray());
    }

    private static XDocument AssetXml(string id, string glbPath, IReadOnlyList<FragmentGlbWriter.Group> groups,
        IReadOnlyList<SurfaceGroup> surfaces, float mass, double3 inertia)
    {
        var part = new XElement("Part", new XAttribute("Id", id), new XAttribute("DisplayName", "Sliced metal fragment"));
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
            subpart.Add(new XElement(material.Glass ? "PartModelGlass" : "PartModel", new XAttribute("Id", group.Name + "_model"),
                new XElement("Mesh", new XAttribute("Id", group.Name)),
                new XElement("Material", new XAttribute("Id", material.Id)),
                new XElement("ShadowCaster", true)));
        }
        part.Add(new XElement("SubPart", new XAttribute("InstanceOf", subpartId)));
        part.Add(new XElement("Collider", new XAttribute("Id", id + "_collider"),
            new XElement("ConvexHull", new XAttribute("Id", id + "_hullCollider"),
                new XElement("Mesh", new XAttribute("Id", id + "_collision")))));
        part.Add(new XElement("CustomMass", new XElement("Mass", new XAttribute("Kg", F(mass))),
            new XElement("MassSpecificInertia", new XAttribute("Ixx", F(inertia.X)),
                new XAttribute("Iyy", F(inertia.Y)), new XAttribute("Izz", F(inertia.Z)))));
        var topology = new FragmentTopology(surfaces.SelectMany(s => s.SolidRuns).Max(r => r.Solid) + 1,
            surfaces.Select((s, i) => (Name: groups[i].Name, s.SolidRuns)).ToDictionary(s => s.Name, s => s.SolidRuns, StringComparer.Ordinal));
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
}
