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
    private readonly record struct SurfaceGroup(int Solid, int Material, SliceFace[] Faces);
    internal static CpuFragmentAsset Build(CpuFragment fragment, IReadOnlyDictionary<int, SliceMaterialData> materials, CancellationToken cancel)
    {
        var solids = fragment.Solids;
        for (int solid = 0; solid < solids.Length; solid++)
            for (int i = 0; i < solids[solid].Length; i++)
            {
                var face = solids[solid][i];
                if (face.Material == -1) continue;
                if (!materials.TryGetValue(face.Material, out var material) || string.IsNullOrEmpty(material.Id))
                    throw new InvalidDataException("A source surface has no durable material ID.");
                if (material.Id == CapMaterial) solids[solid][i] = face with { Material = -1 };
            }
        var transformed = solids.SelectMany(s => s).ToArray();
        var surfaces = solids.SelectMany((solid, i) => solid.GroupBy(f => f.Material).OrderBy(g => g.Key)
            .Select(g => new SurfaceGroup(i, g.Key, g.ToArray()))).ToArray();
        if (surfaces.Length > 2046) throw new InvalidDataException("Fragment surface count exceeds the export budget.");
        cancel.ThrowIfCancellationRequested();
        var collider = SliceColliderGeometry.Build(transformed, cancel);
        var seedGroups = surfaces.Select(s => new FragmentGlbWriter.Group($"solid_{s.Solid}_surface_{s.Material}", s.Material, s.Faces)).ToArray();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(FragmentGlbWriter.Write(seedGroups));
        hash.AppendData(Encoding.UTF8.GetBytes(string.Join("|", surfaces.Select(s => s.Material == -1 ? CapMaterial : materials[s.Material].Id + ":glass=" + materials[s.Material].Glass))));
        var inertia = fragment.Frame.PrincipalSpecificInertia;
        // New collider/export layout gets new immutable IDs; old saved fragments remain untouched.
        hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"|faceted-pick-v2|{fragment.MassKg:R}|{inertia.X:R}|{inertia.Y:R}|{inertia.Z:R}")));
        string id = Prefix + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        var groups = surfaces.Select(s => new FragmentGlbWriter.Group($"{id}_s{s.Solid}" + (s.Material == -1 ? "_cap" : "_m" + s.Material), s.Material, s.Faces)).ToList();
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
        var xml = AssetXml(id, "Generated/Slices/" + id + ".glb", groups, surfaces, materials, fragment.MassKg, inertia);
        // Cancellation between expensive serialization stages bounds abandoned work.
        cancel.ThrowIfCancellationRequested();
        var glb = FragmentGlbWriter.Write(groups);
        return new(id, glb, Encoding.UTF8.GetBytes(xml.ToString()), uploads, groups.Select(g => g.Name).ToArray(),
            fragment.Frame, fragment.Extents, fragment.MassKg, fragment.Piece.PositiveSide, sparks.ToArray());
    }

    private static XDocument AssetXml(string id, string glbPath, IReadOnlyList<FragmentGlbWriter.Group> groups,
        IReadOnlyList<SurfaceGroup> surfaces, IReadOnlyDictionary<int, SliceMaterialData> materials, float mass, double3 inertia)
    {
        var part = new XElement("Part", new XAttribute("Id", id), new XAttribute("DisplayName", "Sliced metal fragment"));
        var subparts = new List<XElement>();
        foreach (int solid in surfaces.Select(s => s.Solid).Distinct())
        {
            string subpartId = id + "_solid" + solid;
            var subpart = new XElement("SubPart", new XAttribute("Id", subpartId));
            // Host part raycasts visit visual subparts. This identity-transform child
            // owns the complete picking hull so every visible solid is targetable.
            if (subparts.Count == 0)
                subpart.Add(new XElement("MeshView", new XAttribute("Id", id + "_pick"),
                    new XElement("Mesh", new XAttribute("Id", id + "_hull"))));
            for (int i = 0; i < surfaces.Count; i++)
            {
                if (surfaces[i].Solid != solid) continue;
                var group = groups[i];
                subpart.Add(new XElement(group.Material != -1 && materials[group.Material].Glass ? "PartModelGlass" : "PartModel", new XAttribute("Id", group.Name + "_model"),
                    new XElement("Mesh", new XAttribute("Id", group.Name)),
                    new XElement("Material", new XAttribute("Id", group.Material == -1 ? CapMaterial : materials[group.Material].Id)),
                    new XElement("ShadowCaster", true)));
            }
            subparts.Add(subpart);
            part.Add(new XElement("SubPart", new XAttribute("InstanceOf", subpartId)));
        }
        part.Add(new XElement("Collider", new XAttribute("Id", id + "_collider"),
            new XElement("ConvexHull", new XAttribute("Id", id + "_hullCollider"),
                new XElement("Mesh", new XAttribute("Id", id + "_collision")))));
        part.Add(new XElement("CustomMass", new XElement("Mass", new XAttribute("Kg", F(mass))),
            new XElement("MassSpecificInertia", new XAttribute("Ixx", F(inertia.X)),
                new XAttribute("Iyy", F(inertia.Y)), new XAttribute("Izz", F(inertia.Z)))));
        return new XDocument(new XElement("Assets", new XElement("MeshAtlas", new XAttribute("Id", id + "_atlas"),
            new XAttribute("Path", glbPath)), subparts, part));
    }

    private static string F(double number) => number.ToString("R", CultureInfo.InvariantCulture);
}
