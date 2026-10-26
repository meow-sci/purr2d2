using System.Collections.Concurrent;
using System.Xml.Linq;
using Purr2D2.Core;
using Purr2D2.Cutting;

namespace Purr2D2.Rendering;

internal readonly record struct FragmentSolidRun(int Solid, int Triangles);

/// <summary>Durable closed-solid provenance, independent of render batching and native subparts.</summary>
internal sealed class FragmentTopology(int solidCount, IReadOnlyDictionary<string, FragmentSolidRun[]> surfaces)
{
    private const string ElementName = "Purr2D2SliceTopology";
    private static readonly ConcurrentDictionary<string, FragmentTopology> Cache = new(StringComparer.Ordinal);

    internal static FragmentTopology? Find(string partId, bool required = false)
    {
        if (Cache.TryGetValue(partId, out var value)) return value;
        if (required) throw new InvalidDataException("Merged fragment has no restored closed-solid topology.");
        return null;
    }

    internal Capture BeginCapture() => new(solidCount, surfaces);

    internal XElement ToXml() => new(ElementName, new XAttribute("Version", 1), new XAttribute("Solids", solidCount),
        surfaces.Select(surface => new XElement("Surface", new XAttribute("Mesh", surface.Key),
            surface.Value.Select(run => new XElement("Run", new XAttribute("Solid", run.Solid), new XAttribute("Triangles", run.Triangles))))));

    /// <summary>Called with private worker XML or startup XML, before generated parts can be captured.</summary>
    internal static void Remember(XDocument document)
    {
        var topology = Read(document);
        if (topology == null) return; // Legacy generated parts retain their per-solid SubParts.
        string id = (string?)document.Root?.Element("Part")?.Attribute("Id")
            ?? throw new InvalidDataException("Fragment topology has no owning part.");
        Cache[id] = topology;
    }

    internal static FragmentTopology? Read(XDocument document, SliceGeometryBudget? geometry = null)
    {
        // These files can belong to older saves, or a cut whose settings have since
        // changed. Validate durable data against supported ceilings; admission of a
        // recut uses its captured preferences in SliceSourceCapture and the slicer.
        geometry ??= SliceGeometryBudget.Maximum;
        var element = document.Root?.Element(ElementName);
        if (element == null)
        {
            string? id = (string?)document.Root?.Element("Part")?.Attribute("Id");
            if (id != null && document.Root!.Elements("SubPart").Any(s => (string?)s.Attribute("Id") == id + "_visual"))
                throw new InvalidDataException("Merged fragment is missing its closed-solid topology.");
            return null;
        }
        if (document.Root!.Elements(ElementName).Skip(1).Any())
            throw new InvalidDataException("Fragment contains duplicate closed-solid topology.");
        if ((int?)element.Attribute("Version") != 1) throw new InvalidDataException("Unsupported fragment topology version.");
        int count = (int?)element.Attribute("Solids") ?? 0;
        if (count < 1 || count > geometry.Solids)
            throw new InvalidDataException($"Fragment topology solid count is outside the geometry budget ({count:N0} / {geometry.Solids:N0}).");
        var result = new Dictionary<string, FragmentSolidRun[]>(StringComparer.Ordinal);
        var seen = new HashSet<int>();
        int total = 0;
        foreach (var surface in element.Elements("Surface"))
        {
            string mesh = (string?)surface.Attribute("Mesh") ?? "";
            var runs = surface.Elements("Run").Select(run => new FragmentSolidRun(
                (int?)run.Attribute("Solid") ?? -1, (int?)run.Attribute("Triangles") ?? 0)).ToArray();
            if (mesh.Length == 0 || runs.Length == 0 || !result.TryAdd(mesh, runs))
                throw new InvalidDataException("Fragment topology contains missing or duplicate surface ranges.");
            foreach (var run in runs)
            {
                if (run.Solid < 0 || run.Solid >= count || run.Triangles < 1 || run.Triangles > geometry.FragmentTriangles
                    || total > geometry.FragmentTriangles - run.Triangles)
                    throw new InvalidDataException("Fragment topology range is outside the geometry budget.");
                total += run.Triangles;
                seen.Add(run.Solid);
            }
        }
        var models = document.Root!.Elements("SubPart").SelectMany(p => p.Elements())
            .Where(e => e.Name.LocalName is "PartModel" or "PartModelGlass")
            .Select(e => (string?)e.Element("Mesh")?.Attribute("Id") ?? "").ToHashSet(StringComparer.Ordinal);
        if (result.Count < 1 || result.Count > SliceResourceLimits.MaximumRenderSurfaces || seen.Count != count || !models.SetEquals(result.Keys))
            throw new InvalidDataException("Fragment topology does not cover every visual surface and closed solid.");
        return new(count, result);
    }

    /// <summary>Partitions the same bounded triangle chunks used by native source capture.</summary>
    internal sealed class Capture(int solidCount, IReadOnlyDictionary<string, FragmentSolidRun[]> surfaces)
    {
        private readonly Dictionary<int, List<SliceFace>> _solids = new();
        private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
        private FragmentSolidRun[] _runs = [];
        private string? _mesh;
        private int _run, _consumed;

        internal void BeginSurface(string meshId)
        {
            if (_mesh != null || _completed.Contains(meshId) || !surfaces.TryGetValue(meshId, out _runs!))
                throw new InvalidDataException("Generated fragment has an unexpected or duplicated visual mesh.");
            _mesh = meshId; _run = 0; _consumed = 0;
        }

        internal void AppendChunk(IReadOnlyList<SliceFace> faces)
        {
            if (_mesh == null) throw new InvalidDataException("Generated fragment has no active topology surface.");
            foreach (var face in faces)
            {
                if (_run >= _runs.Length) throw new InvalidDataException("Generated fragment mesh exceeds its closed-solid topology.");
                var run = _runs[_run];
                if (!_solids.TryGetValue(run.Solid, out var target)) _solids.Add(run.Solid, target = new());
                target.Add(face);
                if (++_consumed == run.Triangles) { _run++; _consumed = 0; }
            }
        }

        internal void EndSurface()
        {
            if (_mesh == null || _run != _runs.Length || _consumed != 0)
                throw new InvalidDataException("Generated fragment mesh no longer matches its closed-solid topology.");
            _completed.Add(_mesh); _mesh = null;
        }

        internal IEnumerable<SliceFace[]> Complete()
        {
            if (_mesh != null || _completed.Count != surfaces.Count || _solids.Count != solidCount)
                throw new InvalidDataException("Generated fragment capture did not cover every visual surface and closed solid.");
            foreach (var solid in _solids.OrderBy(s => s.Key)) yield return solid.Value.ToArray();
        }
    }
}
