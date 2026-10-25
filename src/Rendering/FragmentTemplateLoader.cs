using System.Xml.Serialization;
using KSA;

namespace Purr2D2.Rendering;

/// <summary>Private worker serializer with the same component mapping required by KSA's XML loader.</summary>
internal static class FragmentTemplateLoader
{
    // Overrides generate a dynamic serialization assembly: create it once, not once per cut.
    private static readonly Lazy<XmlSerializer> Serializer = new(BuildSerializer);
    internal static XmlSerializer CreateSerializer() => Serializer.Value;

    /// <summary>Keep generated roots and visual subparts out of both editor catalogs.</summary>
    internal static void HideFromEditor(AssetBundle bundle)
    {
        // Change only the resolved native tags. Durable XML is immutable at its
        // content ID, and older saved debris must receive the same visibility fix.
        foreach (var part in bundle.Assets.OfType<PartTemplate>())
            if (!part.IsHidden) part.EditorTags.Add(EditorTag.Hidden);
    }

    private static XmlSerializer BuildSerializer()
    {
        // PartTemplate.Components has no element attributes. A plain XmlSerializer silently
        // discards Collider, MeshView and PartModel elements. Never use the shared XmlHelper
        // dictionary from a worker; describe the exact modules our exporter writes instead.
        var components = new XmlAttributes();
        components.XmlElements.Add(new("PartModel", typeof(PartModelModule.Template)));
        components.XmlElements.Add(new("PartModelGlass", typeof(PartModelGlassModule.Template)));
        components.XmlElements.Add(new("MeshView", typeof(MeshViewModule.Template)));
        components.XmlElements.Add(new("Collider", typeof(ColliderModule.Template)));
        var overrides = new XmlAttributeOverrides();
        overrides.Add(typeof(PartTemplate), nameof(PartTemplate.Components), components);
        return new XmlSerializer(typeof(AssetBundle), overrides);
    }

    internal static AssetBundle Read(XmlSerializer serializer, byte[] xml, string id, int surfaceCount)
    {
        using var stream = new MemoryStream(xml, writable: false);
        var bundle = serializer.Deserialize(stream) as AssetBundle ?? throw new InvalidDataException("Fragment XML could not be decoded.");
        var parts = bundle.Assets.OfType<PartTemplate>().ToArray();
        var full = parts.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Fragment template is missing.");
        // Native hover raycasts visit visual subparts. Older durable templates put
        // this mesh on the root; their instances are repaired before tree creation.
        var children = full.SubPartInstances.Select(p => p.InstanceOf).ToHashSet(StringComparer.Ordinal);
        bool hasPicking = full.Components.OfType<MeshViewModule.Template>().Any()
            || parts.Any(p => children.Contains(p.Id) && p.Components.OfType<MeshViewModule.Template>().Any());
        if (!full.Components.OfType<ColliderModule.Template>().Any(c => c.Colliders.Count > 0)
            || !hasPicking
            || full.InertMasses.Count == 0
            || parts.Sum(p => p.Components.Count(c => c is PartModelModule.Template or PartModelGlassModule.Template)) != surfaceCount)
            throw new InvalidDataException("Fragment XML lost its render, collision or mass components.");
        HideFromEditor(bundle);
        bundle.Assets.RemoveAll(asset => asset is MeshAtlasFileReference);
        return bundle; // Registration and OnDataLoad stay on the game thread.
    }
}
