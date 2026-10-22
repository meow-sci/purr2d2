using System.Text;
using System.Text.Json;
using Purr2D2.Cutting;

namespace Purr2D2.Rendering;

/// <summary>Self-contained KSA mesh atlas: one primitive per material, plus a complete collider mesh.</summary>
internal static class FragmentGlbWriter
{
    internal readonly record struct Group(string Name, int Material, SliceFace[] Faces);

    internal static byte[] Write(IReadOnlyList<Group> groups)
    {
        if (groups.Count == 0 || groups.Count > 2048) throw new InvalidDataException("Fragment material group count is outside 1..2048.");
        using var binary = new MemoryStream();
        using var writer = new BinaryWriter(binary, Encoding.UTF8, leaveOpen: true);
        var views = new List<object>();
        var accessors = new List<object>();
        var meshes = new List<object>();
        var nodes = new List<object>();
        foreach (var group in groups)
        {
            if (group.Faces.Length == 0 || group.Faces.Length > 200_000)
                throw new InvalidDataException("Fragment group has no triangles or exceeds the 200,000 triangle limit.");
            var vertices = new SliceVertex[checked(group.Faces.Length * 3)];
            for (var i = 0; i < group.Faces.Length; i++)
            {
                vertices[i * 3] = group.Faces[i].A;
                vertices[i * 3 + 1] = group.Faces[i].B;
                vertices[i * 3 + 2] = group.Faces[i].C;
            }
            uint[] triangleIndices = Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray();
            if (group.Material == int.MinValue)
            {
                // Hull construction consumes only positions. Share equal positions here so it
                // does not receive three copies per triangle from the flat render attributes.
                var unique = new Dictionary<CutPoint, uint>();
                var compact = new List<SliceVertex>();
                for (int i = 0; i < vertices.Length; i++)
                {
                    var vertex = vertices[i];
                    if (!unique.TryGetValue(vertex.Position, out uint vertexIndex))
                    { vertexIndex = (uint)compact.Count; unique.Add(vertex.Position, vertexIndex); compact.Add(vertex); }
                    triangleIndices[i] = vertexIndex;
                }
                vertices = compact.ToArray();
            }
            var minimum = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            var maximum = new[] { float.MinValue, float.MinValue, float.MinValue };
            var position = Attribute("VEC3", 3, v => [v.Position.X, v.Position.Y, v.Position.Z], bounds: true);
            var normal = Attribute("VEC3", 3, v => [v.Normal.X, v.Normal.Y, v.Normal.Z]);
            var uv = Attribute("VEC2", 2, v => [v.U, v.V]);
            var indexOffset = checked((int)binary.Position);
            foreach (uint value in triangleIndices) writer.Write(value);
            var indexView = views.Count;
            views.Add(new { buffer = 0, byteOffset = indexOffset, byteLength = checked(triangleIndices.Length * 4), target = 34963 });
            var index = accessors.Count;
            accessors.Add(new { bufferView = indexView, componentType = 5125, count = triangleIndices.Length, type = "SCALAR" });
            meshes.Add(new
            {
                name = group.Name,
                primitives = new[] { new { attributes = new { POSITION = position, NORMAL = normal, TEXCOORD_0 = uv }, indices = index, material = 0, mode = 4 } },
            });
            nodes.Add(new { name = group.Name, mesh = meshes.Count - 1 });

            int Attribute(string type, int width, Func<SliceVertex, double[]> values, bool bounds = false)
            {
                var offset = checked((int)binary.Position);
                foreach (var vertex in vertices)
                {
                    var tuple = values(vertex);
                    for (var c = 0; c < width; c++)
                    {
                        var value = (float)tuple[c];
                        if (!float.IsFinite(value)) throw new InvalidDataException("Fragment vertices contain non-finite float attributes.");
                        writer.Write(value);
                        if (bounds) { minimum[c] = Math.Min(minimum[c], value); maximum[c] = Math.Max(maximum[c], value); }
                    }
                }
                var view = views.Count;
                views.Add(new { buffer = 0, byteOffset = offset, byteLength = checked(vertices.Length * width * 4), target = 34962 });
                var accessor = accessors.Count;
                if (bounds) accessors.Add(new { bufferView = view, componentType = 5126, count = vertices.Length, type, min = minimum, max = maximum });
                else accessors.Add(new { bufferView = view, componentType = 5126, count = vertices.Length, type });
                return accessor;
            }
        }
        var payload = binary.ToArray();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            asset = new { version = "2.0", generator = "Purr2D2 exact plane slicer" },
            scene = 0,
            scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Count).ToArray() } },
            nodes,
            meshes,
            materials = new[] { new { name = "NativeXmlMaterial", pbrMetallicRoughness = new { baseColorFactor = new[] { 1, 1, 1, 1 }, metallicFactor = 0, roughnessFactor = 1 } } },
            buffers = new[] { new { byteLength = payload.Length } },
            bufferViews = views,
            accessors,
        });
        var jsonSize = (json.Length + 3) & ~3;
        var binarySize = (payload.Length + 3) & ~3;
        using var output = new MemoryStream();
        using var glb = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        glb.Write(0x46546c67u); glb.Write(2u); glb.Write(checked((uint)(28 + jsonSize + binarySize)));
        glb.Write(jsonSize); glb.Write(0x4e4f534au); glb.Write(json);
        while (output.Position % 4 != 0) glb.Write((byte)' ');
        glb.Write(binarySize); glb.Write(0x004e4942u); glb.Write(payload);
        while (output.Position % 4 != 0) glb.Write((byte)0);
        return output.ToArray();
    }
}
