using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Brutal.Numerics;
using KSA;

namespace Purr2D2.Rendering;

internal sealed record AuthoredDebrisSurface(string Mesh, string Material, bool Glass);
internal sealed record AuthoredDebrisShape(AuthoredDebrisSurface[] Surfaces, double3 Center, double3 Extents,
    double Scale, float MassKg);

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
        string identity = Xml("identity").ToString(SaveOptions.DisableFormatting);
        string id = Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return (id, Encoding.UTF8.GetBytes(Xml(id).ToString(SaveOptions.DisableFormatting)));

        XDocument Xml(string name)
        {
            var visual = new XElement("SubPart", new XAttribute("Id", name + "_surface"));
            for (int i = 0; i < shape.Surfaces.Length; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var surface = shape.Surfaces[i];
                // KSA caches PartModel/PartModelGlass globally by module template ID.
                // A local "model0" would make unrelated debris render the first mesh.
                visual.Add(new XElement(surface.Glass ? "PartModelGlass" : "PartModel", new XAttribute("Id", name + "_model" + i),
                    new XElement("Mesh", new XAttribute("Id", surface.Mesh)),
                    new XElement("Material", new XAttribute("Id", surface.Material)), new XElement("ShadowCaster", true)));
                visual.Add(new XElement("MeshView", new XAttribute("Id", name + "_pick" + i),
                    new XElement("Mesh", new XAttribute("Id", surface.Mesh))));
            }
            var size = shape.Extents;
            var inertia = new double3(size.Y * size.Y + size.Z * size.Z,
                size.X * size.X + size.Z * size.Z, size.X * size.X + size.Y * size.Y) / 12;
            var part = new XElement("Part", new XAttribute("Id", name), new XAttribute("DisplayName", "Force debris"),
                new XElement("SubPart", new XAttribute("InstanceOf", name + "_surface"),
                    new XElement("Transform", Vector("Position", -shape.Center * shape.Scale),
                        Vector("Scale", double3.One * shape.Scale))),
                new XElement("Collider", new XAttribute("Id", name + "_collider"),
                    new XElement("Box", new XAttribute("Id", name + "_box"),
                        new XElement("LengthX", new XAttribute("M", F(size.X))),
                        new XElement("LengthY", new XAttribute("M", F(size.Y))),
                        new XElement("LengthZ", new XAttribute("M", F(size.Z))))),
                new XElement("CustomMass", new XElement("Mass", new XAttribute("Kg", F(shape.MassKg))),
                    new XElement("MassSpecificInertia", new XAttribute("Ixx", F(inertia.X)),
                        new XAttribute("Iyy", F(inertia.Y)), new XAttribute("Izz", F(inertia.Z)))));
            return new XDocument(new XElement("Assets", visual, part));
        }
    }

    private static XElement Vector(string name, double3 value) => new(name,
        new XAttribute("X", F(value.X)), new XAttribute("Y", F(value.Y)), new XAttribute("Z", F(value.Z)));
    private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static bool Finite(double3 value) => double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);
}
