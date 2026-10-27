using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Rendering;

internal sealed record AuthoredDebrisSurface(string Mesh, string Material, bool Glass, bool Internal = false);
internal sealed record AuthoredDebrisShape(AuthoredDebrisSurface[] Surfaces, double3 Center, double3 Extents,
    double Scale, float MassKg)
{
    internal AuthoredDebrisNode[]? Nodes { get; init; }
}
internal sealed record AuthoredDebrisNode(int SourceIndex, AuthoredDebrisSurface[] Surfaces, double3 Position,
    doubleQuat Rotation, double3 Scale, double3 BoundsCenter, double3 BoundsExtents, double Volume,
    bool AttachedInternal = false);

/// <summary>Detached worker output. The bundle is not registered and has not received OnDataLoad.</summary>
internal sealed record PreparedAuthoredDebrisAsset(string Id, AssetBundle Bundle);

/// <summary>Incremental main-thread cursor for native asset loading and registration.</summary>
internal interface IAuthoredDebrisRegistration
{
    bool Done { get; }
    PartTemplate? Template { get; }
    void AdvanceOne();
}

/// <summary>Inert, durable templates that reuse authored meshes without GPU uploads or mesh cutting.</summary>
internal static class AuthoredDebrisAssets
{
    internal const string Prefix = "Purr2D2_ForceDebris_";
    // Installed by RuntimeFragmentAssets.Initialize. The context callback is called on
    // the main thread; PrepareOnWorker receives only detached DTOs, a path string and a token.
    internal static Func<string>? CaptureDirectory { get; set; }
    internal static Func<AuthoredDebrisShape[], string, CancellationToken, PreparedAuthoredDebrisAsset[]>? PrepareOnWorker { get; set; }
    internal static Func<PreparedAuthoredDebrisAsset, IAuthoredDebrisRegistration>? BeginRegistration { get; set; }

    internal static (string Id, byte[] Xml) Build(AuthoredDebrisShape shape, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        if (shape.Surfaces.Length == 0 || !float.IsFinite(shape.MassKg) || shape.MassKg <= .0001f
            || !double.IsFinite(shape.Scale) || shape.Scale <= 0 || !Finite(shape.Center) || !Finite(shape.Extents)
            || shape.Extents.X <= 0 || shape.Extents.Y <= 0 || shape.Extents.Z <= 0)
            throw new InvalidDataException("Authored debris has invalid geometry, material or mass.");
        foreach (var surface in shape.Surfaces)
        {
            cancel.ThrowIfCancellationRequested();
            if (surface == null || string.IsNullOrEmpty(surface.Mesh) || string.IsNullOrEmpty(surface.Material))
                throw new InvalidDataException("Authored debris has invalid geometry, material or mass.");
        }
        if (shape.Nodes is { } nodes)
        {
            if (nodes.Length == 0)
                throw new InvalidDataException("Authored debris has invalid compound geometry.");
            int compoundSurfaceCount = 0;
            foreach (var node in nodes)
            {
                cancel.ThrowIfCancellationRequested();
                if (node.Surfaces.Length == 0 || !Finite(node.Position) || !Finite(node.Scale)
                    || !Finite(node.Rotation) || !Finite(node.BoundsCenter) || !Finite(node.BoundsExtents)
                    || !double.IsFinite(node.Volume) || node.Volume <= 0
                    || node.Scale.X <= 0 || node.Scale.Y <= 0 || node.Scale.Z <= 0
                    || node.BoundsExtents.X <= 0 || node.BoundsExtents.Y <= 0 || node.BoundsExtents.Z <= 0)
                    throw new InvalidDataException("Authored debris has invalid compound geometry.");
                compoundSurfaceCount += node.Surfaces.Length;
                foreach (var surface in node.Surfaces)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (surface == null || string.IsNullOrEmpty(surface.Mesh) || string.IsNullOrEmpty(surface.Material))
                        throw new InvalidDataException("Authored debris has invalid compound geometry.");
                }
            }
            if (compoundSurfaceCount != shape.Surfaces.Length)
                throw new InvalidDataException("Authored debris has invalid compound geometry.");
        }
        string identity = Xml("identity").ToString(SaveOptions.DisableFormatting);
        string id = Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return (id, Encoding.UTF8.GetBytes(Xml(id).ToString(SaveOptions.DisableFormatting)));

        XDocument Xml(string name)
        {
            var assets = new XElement("Assets");
            var compoundNodes = shape.Nodes;
            if (compoundNodes is null)
            {
                var visual = new XElement("SubPart", new XAttribute("Id", name + "_surface"));
                for (int i = 0; i < shape.Surfaces.Length; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    AddSurface(visual, shape.Surfaces[i], name, i);
                }
                assets.Add(visual);
            }

            var size = shape.Extents;
            var inertia = new double3(size.Y * size.Y + size.Z * size.Z,
                size.X * size.X + size.Z * size.Z, size.X * size.X + size.Y * size.Y) / 12;
            var root = new XElement("Part", new XAttribute("Id", name), new XAttribute("DisplayName", "Furce debris"));
            if (compoundNodes is null)
            {
                root.Add(new XElement("SubPart", new XAttribute("InstanceOf", name + "_surface"),
                    new XElement("Transform", Vector("Position", -shape.Center * shape.Scale),
                        Vector("Scale", double3.One * shape.Scale))));
            }
            else
            {
                for (int nodeIndex = 0; nodeIndex < compoundNodes.Length; nodeIndex++)
                {
                    cancel.ThrowIfCancellationRequested();
                    var node = compoundNodes[nodeIndex];
                    string visualId = name + "_node" + nodeIndex;
                    var visual = new XElement("SubPart", new XAttribute("Id", visualId));
                    for (int surfaceIndex = 0; surfaceIndex < node.Surfaces.Length; surfaceIndex++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        AddSurface(visual, node.Surfaces[surfaceIndex], visualId, surfaceIndex);
                    }
                    assets.Add(visual);
                    root.Add(new XElement("SubPart", new XAttribute("InstanceOf", visualId),
                        new XElement("Transform", Vector("Position", node.Position - shape.Center),
                            Vector("Rotation", node.Rotation.ToXyzRadians()), Vector("Scale", node.Scale))));
                }
            }
            root.Add(
                new XElement("Collider", new XAttribute("Id", name + "_collider"),
                    new XElement("Box", new XAttribute("Id", name + "_box"),
                        new XElement("LengthX", new XAttribute("M", F(size.X))),
                        new XElement("LengthY", new XAttribute("M", F(size.Y))),
                        new XElement("LengthZ", new XAttribute("M", F(size.Z))))),
                new XElement("CustomMass", new XElement("Mass", new XAttribute("Kg", F(shape.MassKg))),
                    new XElement("MassSpecificInertia", new XAttribute("Ixx", F(inertia.X)),
                        new XAttribute("Iyy", F(inertia.Y)), new XAttribute("Izz", F(inertia.Z)))));
            assets.Add(root);
            return new XDocument(assets);
        }

        void AddSurface(XElement visual, AuthoredDebrisSurface surface, string prefix, int index)
        {
            // KSA caches PartModel/PartModelGlass globally by module template ID.
            // A local "model0" would make unrelated debris render the first mesh.
            var model = new XElement(surface.Glass ? "PartModelGlass" : "PartModel", new XAttribute("Id", prefix + "_model" + index),
                new XElement("Mesh", new XAttribute("Id", surface.Mesh)),
                new XElement("Material", new XAttribute("Id", surface.Material)), new XElement("ShadowCaster", true));
            if (surface.Internal && !surface.Glass) model.Add(new XElement("Internal", true));
            visual.Add(model);
            visual.Add(new XElement("MeshView", new XAttribute("Id", prefix + "_pick" + index),
                new XElement("Mesh", new XAttribute("Id", surface.Mesh))));
        }
    }

    private static XElement Vector(string name, double3 value) => new(name,
        new XAttribute("X", F(value.X)), new XAttribute("Y", F(value.Y)), new XAttribute("Z", F(value.Z)));
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
    private static bool Finite(doubleQuat value) => double.IsFinite(value.X) && double.IsFinite(value.Y)
        && double.IsFinite(value.Z) && double.IsFinite(value.W) && value.NormalizedOrZero() != default;
}
