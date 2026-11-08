using System.Reflection;
using System.Text;
using System.Xml.Linq;
using HarmonyLib;
using KSA;
using Purr2D2.Rendering;

/// <summary>Real 5801 collections, native source loader and production runtime registration; no GPU/FMOD/Bepu calls.</summary>
internal static class FragmentContentHostChecks
{
    private static void Main() => Run();

    internal static void Run()
    {
        CheckShippedSource();
        CheckPersistedSource();
        CheckRuntimeRegistration();
        CheckLifecycleHooks();
        ModLibrary.TagTable.ClearCollections();
    }

    private static void CheckShippedSource()
    {
        ModLibrary.TagTable.ClearCollections();
        var mod = new KSA.Mod { Id = "purr2d2", DirectoryPath = Directory.GetCurrentDirectory() };
        var source = Source(mod);
        Add(source, mod, "fixture-core.xml", """
            <Assets><Texture Id="EmptyNormal" Path="unused.png"/><Texture Id="EmptyWhite" Path="unused.png"/>
            <EditorTagDef Id="Hidden"/><EditorTagDef Id="Structural"/><ChannelGroup Id="SfxChannel"/></Assets>
            """);
        string[] files = Directory.GetFiles("Assets", "*.xml").Order(StringComparer.Ordinal).ToArray();
        foreach (string file in files) Add(source, mod, file, File.ReadAllText(file));
        // ShaderReference.OnDataLoad touches ShaderModuleUtils' native shaderc
        // initializer even with ReadPayloads=false. Isolate only that known native
        // boundary; every row still uses the real serializer/registry/resolver.
        var shaderBoundary = new Harmony("purr2d2.tests.content-shader-boundary");
        shaderBoundary.Patch(AccessTools.Method(typeof(ShaderReference), nameof(ShaderReference.OnDataLoad)),
            prefix: new HarmonyMethod(typeof(FragmentContentHostChecks), nameof(SkipNativeShaderInitialization)));
        try { Load(source); }
        finally { shaderBoundary.UnpatchAll(shaderBoundary.Id); }
        foreach (var row in source.Rows.Where(r => r.Name == "Shader"))
        {
            var shader = ModLibrary.Get<ShaderReference>((string)row.Attribute("Id")!);
            Check(shader.LocalPath == (string?)row.Attribute("Path") && File.Exists(mod.GetPath(shader.LocalPath)),
                "shipped native shader rows retain their actual IDs and source paths");
        }
        var hilt = ModLibrary.Get<PartTemplate>("Purr2D2_DroppedHilt");
        Check(hilt.IsHidden && hilt.Components.OfType<ColliderModule.Template>().Single().Colliders.Count == 1
            && hilt.InertMasses.Count == 1, "shipped hilt retains hidden tag, collider and mass");
        foreach (string id in new[] { "Purr2D2_PracticeBillet", "Purr2D2_PracticeTube" })
        {
            var part = ModLibrary.Get<PartTemplate>(id);
            Check(part.SubPartInstances.Count == 1 && part.SubPartInstances[0].Value != null
                && part.SubPartInstances[0].Key == id + "_Placed" && part.Connectors.Count == 2
                && part.Components.OfType<ColliderModule.Template>().Single().Colliders.Count == 1,
                "shipped practice part retains resolved placement, stable placement key, connectors and collider");
        }
        foreach (var row in source.Rows.Where(r => r.Name == "Sound"))
        {
            var sound = ModLibrary.Get<SoundBehavior>((string)row.Attribute("Id")!) as SoundReference;
            Check(sound != null && sound.ChannelGroupRef?.Value?.Id == "SfxChannel"
                && sound.SpatialSoundData?.Value != null && sound.SoundFiles.Count > 0
                && sound.SoundFiles.All(f => f.Value != null), "shipped sounds resolve hoisted files, channel and spatial data");
        }
        foreach (var row in source.Rows.Where(r => r.Name == "PbrMaterial"))
        {
            var material = ModLibrary.Get<PbrMaterialReference>((string)row.Attribute("Id")!);
            Check(material.DiffuseReference?.Value != null && material.NormalReference?.Value != null
                && material.PBRMap?.Value != null, "all shipped PBR maps resolve");
        }
        var cape = ModLibrary.Get<Gltf2Reference>("Purr2D2_Cape_Glb");
        Check(cape.Source?.Value != null && cape.MaterialReferences?.Count == 1
            && cape.MaterialReferences[0].AssetId == "Purr2D2_Cloth_Light", "cape source and material mapping survive native content loading");
        Console.WriteLine($"PASS: all {files.Length} shipped XML manifests decode/register/resolve through actual KSA ContentLoader, with names-only atlases; only native shader initialization is isolated");
    }

    private static void CheckPersistedSource()
    {
        ModLibrary.TagTable.ClearCollections();
        string directory = Path.GetFullPath(Path.Combine("artifacts/checks", "fragment-content-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(directory, "Generated", "Slices"));
        try
        {
            var mod = new KSA.Mod { Id = "FixtureGenerated", DirectoryPath = directory };
            var source = Source(mod);
            Add(source, mod, "fixture-core.xml", """
                <Assets><EditorTagDef Id="Hidden"/><PbrMaterial Id="FixtureMetal"/><PbrMaterial Id="FixtureCopper"/>
                <PbrMaterial Id="Purr2D2_CutSurface"/><Mesh Id="FixtureAuthoredMesh"/></Assets>
                """);
            var frozen = File.ReadAllBytes("tests/fixtures/fragment-v3.xml");
            var oldDocument = XDocument.Parse(Encoding.UTF8.GetString(frozen));
            string oldId = (string)oldDocument.Root!.Element("Part")!.Attribute("Id")!;
            Write(oldId, frozen, File.ReadAllBytes("tests/fixtures/fragment-v3.glb"));
            var (fixture, materials) = FragmentAssetChecks.Fixture();
            var current = FragmentAssetWriter.Build(fixture, materials, default);
            Write(current.Id, current.Xml, current.Glb);
            var (authoredId, authoredXml) = AuthoredDebrisAssets.Build(new(
                [new("FixtureAuthoredMesh", "FixtureMetal", false)], default, new(1, 2, 3), 1, 12));
            Write(authoredId, authoredXml);
            string oldPath = Path.Combine(directory, "Generated", "Slices", oldId + ".xml");
            string[] ids = FragmentContentSource.AppendPersistedAssets(source, mod);
            Check(ids.Length == 6 && source.Rows.All(r => r.Name != "Purr2D2SliceTopology"),
                "old, current and authored generated parts join the source without mod-only topology rows");
            Load(source);
            foreach (string id in ids)
                Check(ModLibrary.Get<PartTemplate>(id) is { LoadFailed: false, IsHidden: true }, "all immutable generated roots and subparts load hidden");
            var old = ModLibrary.Get<PartTemplate>(oldId);
            var latest = ModLibrary.Get<PartTemplate>(current.Id);
            Check(old.Id != latest.Id && old.SubPartInstances[0].Key == oldId + "_visual"
                && old.SubPartInstances[0].Value?.Id == oldId + "_visual"
                && latest.SubPartInstances[0].Value?.Components.OfType<PartModelGlassModule.Template>().Count() == 1,
                "historical saved placement identities and current glass geometry coexist after native resolution");
            Check(old.InertMasses.Single() is CustomMassTemplate { Mass.Kg: 3 }
                && old.Components.OfType<ColliderModule.Template>().Single().Colliders.OfType<ConvexHullColliderTemplate>().Single().Mesh?.Value != null,
                "historical custom mass and collider mesh resolve without cooking native shapes");
            Check(File.ReadAllBytes(oldPath).SequenceEqual(frozen) && oldDocument.ToString() == XDocument.Parse(Encoding.UTF8.GetString(frozen)).ToString(),
                "normalization never rewrites durable XML or original document");
            var missing = XDocument.Parse(Encoding.UTF8.GetString(frozen));
            missing.Root!.Element("MeshAtlas")!.SetAttributeValue("Path", "Generated/Slices/missing.glb");
            File.WriteAllText(oldPath, missing.ToString());
            Invalid(() => FragmentContentSource.AppendPersistedAssets(Source(mod), mod), "missing persisted GLB must fail explicitly");
            Check(File.ReadAllText(oldPath) == missing.ToString(), "missing-GLB refusal preserves original durable bytes");
            Console.WriteLine("PASS: native source loads frozen v3, current export and authored debris together, preserves IDs/placement keys, and refuses missing GLBs without rewriting assets");

            void Write(string id, byte[] xml, byte[]? glb = null)
            {
                File.WriteAllBytes(Path.Combine(directory, "Generated", "Slices", id + ".xml"), xml);
                if (glb != null) File.WriteAllBytes(Path.Combine(directory, "Generated", "Slices", id + ".glb"), glb);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void CheckRuntimeRegistration()
    {
        ModLibrary.TagTable.ClearCollections();
        var mod = new KSA.Mod { Id = "FixtureRuntime" };
        var (fixture, materials) = FragmentAssetChecks.Fixture();
        var current = FragmentAssetWriter.Build(fixture, materials, default);
        var bundle = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), current.Xml, current.Id, current.Uploads.Length);
        foreach (string mesh in current.MeshNames) Register(new MeshReference { Id = mesh });
        foreach (string material in materials.Values.Select(m => m.Id).Append(FragmentAssetWriter.CapMaterial).Distinct())
            Register(new PbrMaterialReference { Id = material });
        var registration = new FragmentAssetRegistration(bundle, mod);
        var root = bundle.Assets.OfType<PartTemplate>().Single(p => p.Id == current.Id);
        var mass = (CustomMassTemplate)root.InertMasses.Single();
        // All identities publish first, the complete reference pass follows, then
        // native initialization. No OnDataLoad is allowed to observe empty Values.
        for (int i = 0; i < bundle.Assets.Count; i++)
        {
            registration.AdvanceOne();
            Check(double.IsNaN(mass.Mass) && root.Mod == null
                && !FragmentAssetRegistration.TryGetAsset<PartTemplate>(root.Id, out _),
                "registration must not initialize native template fields or expose incomplete templates for reuse");
        }
        registration.AdvanceOne();
        Check(double.IsNaN(mass.Mass) && root.SubPartInstances[0].Value != null,
            "all placement references resolve before any native template initialization");
        var visual = root.SubPartInstances[0].Value!;
        Check(visual.Components.OfType<MeshViewModule.Template>().Single().MeshView.Value != null
            && visual.Components.OfType<PartModelModule.Template>().All(m => m.Mesh?.Value != null && m.Material?.Value != null),
            "render/picking references resolve before OnDataLoad");
        while (!registration.Done) registration.AdvanceOne();
        Check(FragmentAssetRegistration.TryGetAsset<SubPartTemplate>(visual.Id.ToLowerInvariant(), out var foundVisual)
            && ReferenceEquals(foundVisual, visual)
            && !FragmentAssetRegistration.TryGetAsset<MeshReference>(visual.Id, out _)
            && !FragmentAssetRegistration.TryGetAsset<PartTemplate>("missing", out _),
            "bound-collection lookup preserves case-insensitive ID semantics and rejects missing/wrong-type assets");
        typeof(SerializedId).GetProperty(nameof(SerializedId.LoadFailed))!.SetValue(visual, true);
        Check(!FragmentAssetRegistration.TryGetAsset<PartTemplate>(visual.Id, out _), "failed native templates are never reused");
        typeof(SerializedId).GetProperty(nameof(SerializedId.LoadFailed))!.SetValue(visual, false);
        Check(!double.IsNaN(mass.Mass) && root.Mod == mod && root.IsHidden && visual.IsHidden,
            "bounded native initialization completes only after reference resolution");
        ModLibrary.TagTable.ClearCollections();
        var unresolved = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), current.Xml, current.Id, current.Uploads.Length);
        var failure = new FragmentAssetRegistration(unresolved, mod);
        Invalid(() => { while (!failure.Done) failure.AdvanceOne(); }, "unavailable render meshes must prevent initialization");
        Check(unresolved.Assets.OfType<PartTemplate>().All(p => p.Mod == null)
            && !FragmentAssetRegistration.TryGetAsset<PartTemplate>(current.Id, out _),
            "unresolved bundle never calls OnDataLoad and failed published identities cannot be reused");
        var normalized = FragmentTemplateLoader.NormalizeDocument(XDocument.Parse(Encoding.UTF8.GetString(current.Xml)));
        var decodedNative = FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), Encoding.UTF8.GetBytes(normalized.ToString()), current.Id, current.Uploads.Length);
        Check(decodedNative.Assets.Count == bundle.Assets.Count, "current native schema is accepted without a second transformation");
        normalized.Root!.Element("Part")!.Add(new XElement("UnexpectedGeneratedComponent"));
        Invalid(() => FragmentTemplateLoader.Read(FragmentTemplateLoader.CreateSerializer(), Encoding.UTF8.GetBytes(normalized.ToString()), current.Id, current.Uploads.Length),
            "unknown generated data must never be silently lost");
        Console.WriteLine("PASS: production runtime registration publishes identities, resolves all references before OnDataLoad, initializes incrementally, and rejects missing dependencies/unknown XML");

        void Register(SerializedId asset) { asset.OnDataLoad(mod); FragmentAssetRegistration.Register(asset); }
    }


    private static bool SkipNativeShaderInitialization() => false;

    private static void CheckLifecycleHooks()
    {
        ModLibrary.TagTable.ClearCollections();
        var harmony = new Harmony("purr2d2.tests.content-lifecycle");
        int initialized = 0, completed = 0;
        string directory = Path.GetFullPath(Path.Combine("artifacts/checks", "fragment-lifecycle-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(directory, "purr2d2"));
        var bootSource = typeof(ModLibrary).GetField("_bootSource", BindingFlags.NonPublic | BindingFlags.Static)!;
        var read = typeof(ContentLoader).GetMethod(nameof(ContentLoader.ReadSource),
            [typeof(string), typeof(string), typeof(ModManifest), typeof(ContentErrors), typeof(IContentLoadProgress)])!;
        var load = typeof(ModLibrary).GetMethod(nameof(ModLibrary.LoadAll), [typeof(IContentLoadProgress)])!;
        try
        {
            File.WriteAllText(Path.Combine(directory, "purr2d2", "mod.toml"), "name = \"Fixture\"\nassets = []\n");
            FragmentContentSource.InstallSourceHook(harmony, _ => initialized++, () => completed++);
            FragmentContentSource.InstallSourceHook(harmony, _ => initialized += 100, () => completed += 100);
            Check(Harmony.GetPatchInfo(read)!.Postfixes.Count(p => p.owner == harmony.Id) == 1
                && Harmony.GetPatchInfo(load)!.Postfixes.Count(p => p.owner == harmony.Id) == 1,
                "duplicate startup installation must keep one source and one load-completion patch");
            var manifest = new ModManifest(); manifest.Mods.Add(new ModEntry { Id = "purr2d2" });
            var errors = new ContentErrors();
            var source = ContentLoader.ReadSource(directory, directory, manifest, errors, null);
            Check(errors.Count == 0 && source.Mods.Count == 1 && initialized == 1 && completed == 0,
                "real native ReadSource invokes startup initialization exactly once before loading");
            // A second source read is explicitly refused instead of reserving again.
            InvalidOperation(() => ContentLoader.ReadSource(directory, directory, manifest, new ContentErrors(), null),
                "re-reading content must fail rather than reserve a second arena");
            Check(initialized == 1, "repeated ReadSource cannot repeat startup initialization");
            bootSource.SetValue(null, source);
            harmony.Patch(load, prefix: new HarmonyMethod(typeof(FragmentContentHostChecks), nameof(SkipNativeLoad)));
            ModLibrary.LoadAll(null);
            Check(completed == 0 && ReferenceEquals(bootSource.GetValue(null), source),
                "a skipped LoadAll must not initialize runtime or consume native content");
            harmony.Unpatch(load, HarmonyPatchType.Prefix, harmony.Id);
            // Empty content needs no renderer or physics world; this runs the real
            // native full loader and its completion postfix, with no native payloads.
            ModLibrary.LoadAll(null);
            ModLibrary.LoadAll(null);
            Check(completed == 1 && bootSource.GetValue(null) == null,
                "real LoadAll completion initializes once and duplicate empty loads cannot repeat it");
            FragmentContentSource.VerifyStartupRestore();
            Console.WriteLine("PASS: real startup ReadSource/LoadAll hooks install once, initialize once, reject duplicate source reads and ignore skipped loads");
        }
        finally
        {
            bootSource.SetValue(null, null);
            harmony.UnpatchAll(harmony.Id);
            FragmentContentSource.RemoveSourceHook();
            Directory.Delete(directory, recursive: true);
        }
        InvalidOperation(() => FragmentContentSource.InstallSourceHook(harmony, _ => initialized++, () => completed++),
            "startup hooks installed after the source was consumed must fail explicitly");
    }

    private static bool SkipNativeLoad() => false;

    private static void InvalidOperation(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }

    private static ContentSource Source(KSA.Mod mod)
    {
        var source = new ContentSource(); source.Mods.Add(mod); return source;
    }

    private static void Add(ContentSource source, KSA.Mod mod, string path, string xml)
    {
        int fileIndex = source.Rows.Count, rowIndex = 0;
        foreach (var row in XDocument.Parse(xml).Root!.Elements())
        {
            ContentStamps.Stamp(row, mod.Id, 0, path, fileIndex, rowIndex++);
            source.Rows.Add(row);
        }
    }

    private static void Load(ContentSource source)
    {
        var errors = new ContentErrors();
        ContentLoader.Load(source, ModLibrary.TagTable,
            new ContentLoadOptions { Serial = true, ReadPayloads = false, AtlasNamesOnly = true }, null, errors);
        if (errors.Count > 0)
            throw new Exception("Actual native content loading failed:\n" + string.Join("\n", Enumerable.Range(0, errors.Count).Select(i => ContentErrorFormatter.Format(errors[i]))));
    }

    private static void Invalid(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Fragment content host: " + message);
    }
}
