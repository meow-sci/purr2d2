using System.Xml.Linq;
using System.Xml.Serialization;
using KSA;

namespace Purr2D2.Rendering;

/// <summary>Private worker serializer and in-memory adapter for immutable generated XML.</summary>
internal static class FragmentTemplateLoader
{
    // Overrides generate a dynamic serialization assembly: create it once, not once per cut.
    private static readonly Lazy<XmlSerializer> Serializer = new(BuildSerializer);
    internal static XmlSerializer CreateSerializer() => Serializer.Value;

    /// <summary>Keep generated roots and visual subparts out of both editor catalogs.</summary>
    internal static void HideFromEditor(AssetBundle bundle)
    {
        foreach (var part in bundle.Assets.OfType<PartTemplate>()) HideFromEditor(part);
    }

    internal static void HideFromEditor(PartTemplate part)
    {
        // OnDataLoad also appends resolved tags. Keep this repair idempotent on
        // decoded, initialized, and historical templates without changing XML.
        bool found = false;
        for (int i = 0; i < part.EditorTags.Count; i++)
            if (part.EditorTags[i] == EditorTag.Hidden)
            {
                if (found) part.EditorTags.RemoveAt(i--);
                else found = true;
            }
        if (!found) part.EditorTags.Add(EditorTag.Hidden);
    }

    private static XmlSerializer BuildSerializer()
    {
        // Components is an XmlArray in 5801. The native override maps array items;
        // XmlElements would retain the old unwrapped schema and lose native rows.
        var components = new XmlAttributes { XmlArray = new("Components") };
        components.XmlArrayItems.Add(new("PartModel", typeof(PartModelModule.Template)));
        components.XmlArrayItems.Add(new("PartModelGlass", typeof(PartModelGlassModule.Template)));
        components.XmlArrayItems.Add(new("MeshView", typeof(MeshViewModule.Template)));
        components.XmlArrayItems.Add(new("Collider", typeof(ColliderModule.Template)));
        var overrides = new XmlAttributeOverrides();
        overrides.Add(typeof(PartTemplate), nameof(PartTemplate.Components), components);
        return new XmlSerializer(typeof(AssetBundle), overrides);
    }

    /// <summary>Adapt a copy only. Content-addressed XML/GLBs and their IDs never change on disk.</summary>
    internal static XDocument NormalizeDocument(XDocument original, bool addHiddenReference = false)
    {
        var document = new XDocument(original);
        var root = document.Root;
        if (root?.Name != "Assets") throw new InvalidDataException("Generated fragment XML must have an Assets root.");
        root.Elements("Purr2D2SliceTopology").Remove(); // Mod metadata is not a native content row.
        foreach (var row in root.Elements())
        {
            if (row.Name == "MeshAtlas") continue;
            if (row.Name != "Part" && row.Name != "SubPart")
                throw new InvalidDataException("Unsupported generated asset: " + row.Name);
            Wrap(row, "Components", "PartModel", "PartModelGlass", "MeshView", "Collider");
            Wrap(row, "SubParts", "SubPart");
            Wrap(row, "InertMasses", "CustomMass");
            foreach (var placement in row.Element("SubParts")?.Elements("SubPart") ?? [])
            {
                var oldTarget = placement.Attribute("InstanceOf");
                if (oldTarget != null)
                {
                    SetConvertedAttribute(placement, "Ref", oldTarget.Value);
                    oldTarget.Remove();
                }
                // Old Part.ResolveRuntimeId used Id when present and template Id
                // otherwise. The placement Key must preserve that saved identity.
                var oldId = placement.Attribute("Id");
                if (oldId != null) { SetConvertedAttribute(placement, "Key", oldId.Value); oldId.Remove(); }
                if (placement.Attribute("Key") == null)
                    placement.SetAttributeValue("Key", (string?)placement.Attribute("Ref") ?? "");
            }
            foreach (var component in row.Element("Components")?.Elements() ?? [])
            {
                ConvertId(component, "Key");
                if (component.Name == "Collider")
                {
                    Wrap(component, "Colliders", "Box", "Capsule", "Cylinder", "Sphere", "ConvexHull");
                    foreach (var collider in component.Element("Colliders")?.Elements() ?? []) ConvertId(collider, "Key");
                }
            }
            // The generated dialect only uses these nested asset references.
            foreach (var reference in row.Descendants().Where(e => e.Name == "Mesh" || e.Name == "Material" || e.Name == "EditorTag"))
                ConvertId(reference, "Ref");
            if (addHiddenReference)
            {
                var tags = row.Element("EditorTags");
                if (tags == null) row.Add(tags = new XElement("EditorTags"));
                if (!tags.Elements("EditorTag").Any(t => (string?)t.Attribute("Ref") == "Hidden"))
                    tags.Add(new XElement("EditorTag", new XAttribute("Ref", "Hidden")));
            }
        }
        return document;
    }

    private static void Wrap(XElement parent, string wrapper, params string[] names)
    {
        var children = parent.Elements().Where(e => names.Contains(e.Name.LocalName, StringComparer.Ordinal)).ToArray();
        if (children.Length == 0) return;
        var container = parent.Element(wrapper);
        if (container == null) parent.Add(container = new XElement(wrapper));
        foreach (var child in children) { child.Remove(); container.Add(child); }
    }

    private static void ConvertId(XElement element, string name)
    {
        var oldId = element.Attribute("Id");
        if (oldId == null) return;
        SetConvertedAttribute(element, name, oldId.Value);
        oldId.Remove();
    }

    private static void SetConvertedAttribute(XElement element, string name, string value)
    {
        if (element.Attribute(name) is { } existing && existing.Value != value)
            throw new InvalidDataException($"Conflicting generated {element.Name} {name} and legacy identity.");
        element.SetAttributeValue(name, value);
    }

    internal static AssetBundle Read(XmlSerializer serializer, byte[] xml, string id, int surfaceCount)
    {
        using var input = new MemoryStream(xml, writable: false);
        var normalized = NormalizeDocument(XDocument.Load(input));
        AssetBundle bundle;
        try
        {
            using var reader = normalized.CreateReader();
            bundle = serializer.Deserialize(reader, new XmlDeserializationEvents
            {
                OnUnknownAttribute = (_, args) => throw new InvalidDataException("Unknown generated XML attribute: " + args.Attr.Name),
                OnUnknownElement = (_, args) => throw new InvalidDataException("Unknown generated XML element: " + args.Element.Name),
            }) as AssetBundle ?? throw new InvalidDataException("Fragment XML could not be decoded.");
        }
        catch (InvalidOperationException error) { throw new InvalidDataException("Fragment XML could not be decoded without losing data.", error); }
        var parts = bundle.Assets.OfType<PartTemplate>().ToArray();
        var full = parts.SingleOrDefault(p => p.Id == id) ?? throw new InvalidDataException("Fragment template is missing.");
        // Native hover raycasts visit visual subparts. Older durable templates put
        // this mesh on the root; their instances are repaired before tree creation.
        var children = full.SubPartInstances.Select(p => p.Ref).ToHashSet(StringComparer.Ordinal);
        bool hasPicking = full.Components.OfType<MeshViewModule.Template>().Any()
            || parts.Any(p => children.Contains(p.Id) && p.Components.OfType<MeshViewModule.Template>().Any());
        if (!full.Components.OfType<ColliderModule.Template>().Any(c => c.Colliders.Count > 0)
            || !hasPicking
            || full.InertMasses.Count == 0
            || parts.Sum(p => p.Components.Count(c => c is PartModelModule.Template or PartModelGlassModule.Template)) != surfaceCount)
            throw new InvalidDataException("Fragment XML lost its render, collision or mass components.");
        HideFromEditor(bundle);
        bundle.Assets.RemoveAll(asset => asset is MeshAtlasFileReference);
        return bundle; // Registration, reference resolution and OnDataLoad stay on the game thread.
    }
}
