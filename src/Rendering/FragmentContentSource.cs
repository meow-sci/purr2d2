using System.Reflection;
using System.Xml.Linq;
using HarmonyLib;
using KSA;

namespace Purr2D2.Rendering;

/// <summary>Join immutable generated rows to the native two-phase content pipeline.</summary>
internal static class FragmentContentSource
{
    private const string ModId = "purr2d2";
    private static Action<KSA.Mod>? _initialize;
    private static Action? _complete;
    private static string[] _restoredIds = [];
    private static bool _sourceRead, _completed;

    internal static void InstallSourceHook(Harmony harmony, Action<KSA.Mod> initialize, Action complete)
    {
        if (_initialize != null) return;
        // A late installation cannot recover the native source/reset/resolve order.
        var sourceField = typeof(ModLibrary).GetField("_bootSource", BindingFlags.Static | BindingFlags.NonPublic);
        if (sourceField?.FieldType != typeof(ContentSource))
            throw new MissingFieldException("The native startup content-source contract changed.");
        if (_sourceRead || sourceField.GetValue(null) != null || ModLibrary.Find(ModId) != null)
            throw new InvalidOperationException("Purr2d2 must load before KSA prepares its content source; restart with a loader that invokes BeforeMain before content preparation.");
        var read = typeof(ContentLoader).GetMethod(nameof(ContentLoader.ReadSource),
            [typeof(string), typeof(string), typeof(ModManifest), typeof(ContentErrors), typeof(IContentLoadProgress)])
            ?? throw new MissingMethodException("The native startup content-source seam changed.");
        var load = typeof(ModLibrary).GetMethod(nameof(ModLibrary.LoadAll), [typeof(IContentLoadProgress)])
            ?? throw new MissingMethodException("The native content-load completion seam changed.");
        _initialize = initialize;
        _complete = complete;
        try
        {
            harmony.Patch(read, postfix: new HarmonyMethod(typeof(FragmentContentSource), nameof(AfterReadSource)));
            harmony.Patch(load, postfix: new HarmonyMethod(typeof(FragmentContentSource), nameof(AfterLoadAll)));
        }
        catch
        {
            harmony.Unpatch(read, HarmonyPatchType.Postfix, harmony.Id);
            harmony.Unpatch(load, HarmonyPatchType.Postfix, harmony.Id);
            _initialize = null; _complete = null;
            throw;
        }
    }

    private static void AfterReadSource(ContentSource __result)
    {
        var mod = __result.Mods.SingleOrDefault(m => m.Id == ModId);
        if (mod == null) return;
        if (_sourceRead)
            throw new InvalidOperationException("Purr2d2's native content source was read twice. Restart before reloading generated assets.");
        _initialize!(mod); // Reserves mesh capacity before any payload loader can allocate meshes.
        _restoredIds = AppendPersistedAssets(__result, mod);
        _sourceRead = true;
    }

    private static void AfterLoadAll(bool __runOriginal)
    {
        // A skipped native load must not install runtime patches into unloaded content.
        if (!__runOriginal || !_sourceRead || _completed) return;
        _completed = true;
        _complete!();
    }

    /// <summary>File reads and adaptation only; native ContentLoader owns registration and resolution.</summary>
    internal static string[] AppendPersistedAssets(ContentSource source, KSA.Mod mod)
    {
        if (!source.Mods.Contains(mod)) throw new InvalidOperationException("Generated rows need their defining mod in the native source.");
        var directory = Path.Combine(mod.DirectoryPath, "Generated", "Slices");
        if (!Directory.Exists(directory)) return [];
        var ids = new List<string>();
        int fileIndex = source.Rows.Where(row => (string?)row.Attribute(ContentStamps.MOD) == mod.Id)
            .Select(row => int.TryParse((string?)row.Attribute(ContentStamps.FILE_INDEX), out int index) ? index : -1)
            .DefaultIfEmpty(-1).Max() + 1;
        foreach (var path in Directory.EnumerateFiles(directory, "*.xml").Order(StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(path);
            using var input = new MemoryStream(bytes, writable: false);
            var original = XDocument.Load(input);
            foreach (var atlas in original.Descendants("MeshAtlas"))
            {
                string assetPath = (string?)atlas.Attribute("Path") ?? "";
                if (assetPath.Length == 0 || !File.Exists(mod.GetPath(assetPath)))
                    throw new InvalidDataException($"Generated slice '{path}' is missing its GLB '{assetPath}'. Restore the complete Generated/Slices directory before loading its saves.");
            }
            var normalized = FragmentTemplateLoader.NormalizeDocument(original, addHiddenReference: true);
            var rootPart = normalized.Root!.Elements("Part").SingleOrDefault()
                ?? throw new InvalidDataException("Generated slice has no unique root part: " + path);
            string id = (string?)rootPart.Attribute("Id") ?? "";
            int surfaces = normalized.Descendants("Components").Elements()
                .Count(e => e.Name == "PartModel" || e.Name == "PartModelGlass");
            // Decode with the production private serializer before adding any rows;
            // unknown/missing components must not silently invalidate durable saves.
            FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), bytes, id, surfaces);
            FragmentTopology.Remember(original);
            string relative = Path.GetRelativePath(mod.DirectoryPath, path).Replace(Path.DirectorySeparatorChar, '/');
            int modIndex = source.Mods.IndexOf(mod), rowIndex = 0;
            foreach (var row in normalized.Root.Elements())
            {
                ContentStamps.Stamp(row, mod.Id, modIndex, relative, fileIndex, rowIndex++);
                source.Rows.Add(row);
                if (row.Name == "Part" || row.Name == "SubPart") ids.Add((string?)row.Attribute("Id") ?? "");
            }
            fileIndex++;
        }
        return ids.ToArray();
    }

    internal static int VerifyStartupRestore()
    {
        if (!_sourceRead)
            throw new InvalidOperationException("Purr2d2's startup source hook did not run. The loader must invoke BeforeMain before content preparation.");
        foreach (string id in _restoredIds)
            if (!FragmentAssetRegistration.TryGetAsset<PartTemplate>(id, out var part) || part == null || part.LoadFailed)
                throw new InvalidDataException($"Generated slice template '{id}' did not finish native content loading. Retain its immutable XML/GLB and restore all required source mods before loading a save.");
        return _restoredIds.Length;
    }

    internal static void RemoveSourceHook()
    {
        _initialize = null;
        _complete = null;
        // Read status is deliberately retained: late hot reload cannot reserve a new
        // mesh arena safely or reconstruct a source that the host has already consumed.
    }
}
