// Run with .NET 10: dotnet run --file tools/inspect_host.cs -- <KSA.dll> <Purr2D2.dll>
// This reads PE metadata and IL; it never loads or executes either assembly.
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

return HostInspection.Run(args);

static class HostInspection
{
    private static int _failures;
    private sealed record Expected(string Type, string Name, string Result, bool Static,
        MethodAttributes Access, params string[] Parameters)
    {
        public string Key => $"{Type}.{Name}({string.Join(",", Parameters)}):{Result}";
    }
    private sealed record Call(int Offset, string Key);

    public static int Run(string[] arguments)
    {
        if (arguments.Length != 2)
        {
            Console.Error.WriteLine("Usage: dotnet run --file tools/inspect_host.cs -- <path/to/KSA.dll> <path/to/Purr2D2.dll>");
            return 2;
        }
        try
        {
            using var host = new Image(arguments[0]);
            using var mod = new Image(arguments[1]);
            Console.WriteLine($"Host: {host.Path} ({host.AssemblyName}, {host.AssemblyVersion}, {host.Pe.PEHeaders.CoffHeader.Machine})");
            Console.WriteLine($"Mod:  {mod.Path} ({mod.AssemblyName}, {mod.AssemblyVersion})");
            Check(host.AssemblyName == "KSA", "Host input is the KSA assembly", "Pass the game's actual KSA.dll as the first argument.");
            Check(mod.AssemblyName == "Purr2D2", "Mod input is the built Purr2D2 assembly", "Build Purr2D2.csproj and pass its output as the second argument.");

            Expected[] patches =
            [
                new("KSA.Program", "PrepareFrame", "KSA.Program+OnFrameResult", false, MethodAttributes.Private, "System.Double", "System.Double"),
                new("KSA.Program", "OnFrameEditor", "System.Void", false, MethodAttributes.Private, "System.Double"),
                new("KSA.Program", "OnFrameViewports", "System.Void", false, MethodAttributes.Private, "System.Double"),
                new("KSA.Program", "OnMouseButton", "System.Void", false, MethodAttributes.Public,
                    "Brutal.GlfwApi.GlfwWindow", "Brutal.GlfwApi.GlfwMouseButton", "Brutal.GlfwApi.GlfwButtonAction", "Brutal.GlfwApi.GlfwModifier"),
                new("KSA.Program", "OnGameLoaded", "System.Void", true, MethodAttributes.Public),
                new("KSA.Universe", "DeserializeSave", "System.Void", true, MethodAttributes.Public, "KSA.UniverseData"),
                new("KSA.Program", "Dispose", "System.Void", false, MethodAttributes.Public),
                new("KSA.Program", "UpdateRenderingResources", "System.Void", false, MethodAttributes.Public, "System.Int32"),
                new("KSA.SuperMeshRenderSystem", "ClearBuckets", "System.Void", false, MethodAttributes.Public, "KSA.IViewport"),
                new("KSA.SuperMeshRenderSystem", "RenderTranslucencyPass", "System.Void", false, MethodAttributes.Public,
                    "KSA.IViewport", "Brutal.VulkanApi.CommandBuffer", "System.Boolean"),
                new("KSA.PartFailure", "IsolateAndDestroy", "System.Void", true, MethodAttributes.Assembly,
                    "KSA.Vehicle", "KSA.Part", "System.Collections.Generic.List`1<KSA.Vehicle>"),
                new("KSA.PartModel", "AddInstance", "System.Void", false, MethodAttributes.Private,
                    "KSA.PartModel+PerInstanceData", "KSA.Deformation.PerInstanceDent", "KSA.IViewport", "System.Int32"),
                new("KSA.Vehicle", "DepleteConsumables", "System.Void", false, MethodAttributes.Public),
                new("KSA.Vehicle", "UpdateVehicleConfiguration", "System.Void", false, MethodAttributes.Public),
                new("KSA.Part", "get_StructuralPart", "KSA.Part", false, MethodAttributes.Public),
                new("KSA.Universe", "SetSimulationSpeed", "System.Void", true, MethodAttributes.Public, "System.Double", "System.Boolean"),
            ];
            MethodDefinitionHandle prepare = default;
            foreach (var expected in patches)
            {
                var handle = CheckMethod(host, expected);
                if (expected.Name == "PrepareFrame") prepare = handle;
            }
            // Harmony matches these render postfix arguments by name as well as by type.
            CheckParameterNames(host, "KSA.SuperMeshRenderSystem", "ClearBuckets", "viewport");
            CheckParameterNames(host, "KSA.SuperMeshRenderSystem", "RenderTranslucencyPass", "viewport", "commandBuffer", "useCustomRenderPass");
            CheckAvatarField(host);
            CheckManualInputField(host);
            CheckDeviceMeshFields(host);
            if (!prepare.IsNil) CheckFrameSeam(host, prepare);
            CheckRenderSeams(host);
            CheckEntry(mod);
            CheckReferences(host, mod);
            Console.WriteLine(_failures == 0
                ? "PASS: PE metadata/IL compatibility checks passed. Runtime Harmony patch acceptance and in-game behavior remain unverified."
                : $"FAIL: {_failures} compatibility check(s) failed. Do not assume this host is supported; inspect the changed signatures/frame order before running the mod.");
            return _failures == 0 ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL: Could not inspect the input PE files: {error.Message}");
            return 1;
        }
    }

    private static void Check(bool condition, string description, string failure)
    {
        if (condition) Console.WriteLine("PASS: " + description);
        else { _failures++; Console.Error.WriteLine("FAIL: " + description + ". " + failure); }
    }

    private static MethodDefinitionHandle CheckMethod(Image image, Expected expected)
    {
        var candidates = image.Methods(expected.Type, expected.Name).ToArray();
        var matches = candidates.Where(h => image.MethodKey(h) == expected.Key).ToArray();
        Check(matches.Length == 1, expected.Key, matches.Length == 0
            ? "Expected exactly one matching method. Found: " + string.Join("; ", candidates.Select(image.MethodKey).DefaultIfEmpty("no methods with that name"))
            : $"Found {matches.Length} matches; the patch target is ambiguous.");
        if (matches.Length != 1) return default;
        var definition = image.Reader.GetMethodDefinition(matches[0]);
        Check((definition.Attributes & MethodAttributes.Static) != 0 == expected.Static
              && (definition.Attributes & MethodAttributes.MemberAccessMask) == expected.Access
              && definition.GetGenericParameters().Count == 0,
            $"{expected.Type}.{expected.Name} visibility/static/non-generic flags",
            $"Expected {expected.Access}, {(expected.Static ? "static" : "instance")}; found {definition.Attributes}.");
        return matches[0];
    }

    private static void CheckParameterNames(Image image, string type, string name, params string[] expected)
    {
        var methods = image.Methods(type, name).Where(h => image.Reader.GetMethodDefinition(h)
            .DecodeSignature(image.Types, (object?)null).ParameterTypes.Length == expected.Length).ToArray();
        if (methods.Length != 1) return; // The exact signature check above reports a changed/ambiguous target.
        var names = image.Reader.GetMethodDefinition(methods[0]).GetParameters()
            .Select(image.Reader.GetParameter).Where(p => p.SequenceNumber > 0)
            .OrderBy(p => p.SequenceNumber).Select(p => image.Reader.GetString(p.Name)).ToArray();
        Check(names.SequenceEqual(expected), $"{type}.{name} Harmony argument names",
            $"Expected ({string.Join(", ", expected)}), found ({string.Join(", ", names)}). Update named Harmony arguments.");
    }

    private static void CheckAvatarField(Image image)
    {
        var type = image.FindType("KSA.KittenRenderable");
        var fields = type.IsNil ? [] : image.Reader.GetTypeDefinition(type).GetFields()
            .Where(h => image.Reader.GetString(image.Reader.GetFieldDefinition(h).Name) == "_characterAvatar").ToArray();
        var valid = fields.Length == 1;
        if (valid)
        {
            var field = image.Reader.GetFieldDefinition(fields[0]);
            valid = field.DecodeSignature(image.Types, (object?)null) == "KSA.CharacterAvatar"
                    && (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Private
                    && (field.Attributes & FieldAttributes.Static) == 0;
        }
        Check(valid, "Private instance KittenRenderable._characterAvatar : KSA.CharacterAvatar",
            "The pose reflection seam changed. Update SaberPoseController before enabling its pose overlay.");
    }

    private static void CheckManualInputField(Image image)
    {
        var type = image.FindType("KSA.Vehicle");
        var fields = type.IsNil ? [] : image.Reader.GetTypeDefinition(type).GetFields()
            .Where(h => image.Reader.GetString(image.Reader.GetFieldDefinition(h).Name) == "_manualControlInputs").ToArray();
        var valid = fields.Length == 1;
        if (valid)
        {
            var field = image.Reader.GetFieldDefinition(fields[0]);
            valid = field.DecodeSignature(image.Types, (object?)null) == "KSA.ManualControlInputs"
                    && (field.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Private
                    && (field.Attributes & FieldAttributes.Static) == 0;
        }
        Check(valid, "Private instance Vehicle._manualControlInputs : KSA.ManualControlInputs",
            "The EVA MMU pulse-input seam changed. Update EvaCuttingAutopilot before enabling close-proximity guidance.");
    }

    private static void CheckDeviceMeshFields(Image image)
    {
        var expected = new Dictionary<string, string>
        {
            ["HostMesh"] = "RenderCore.MeshAsset", ["HasNormal"] = "System.Boolean", ["HasUV"] = "System.Boolean",
            ["VertexStride"] = "Brutal.ByteSize", ["VerticesOffset"] = "Brutal.ByteSize", ["VerticesSize"] = "Brutal.ByteSize",
            ["IndexCount"] = "System.Int32", ["IndexType"] = "Brutal.VulkanApi.VkIndexType",
            ["IndicesStride"] = "Brutal.ByteSize", ["IndicesOffset"] = "Brutal.ByteSize", ["IndicesSize"] = "Brutal.ByteSize",
        };
        var handle = image.FindType("KSA.DeviceMeshInterleaved");
        var fields = handle.IsNil ? [] : image.Reader.GetTypeDefinition(handle).GetFields()
            .Select(image.Reader.GetFieldDefinition).Where(f => (f.Attributes & FieldAttributes.Static) == 0).ToArray();
        Check(fields.Length == expected.Count && fields.All(f =>
                (f.Attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public
                && expected.TryGetValue(image.Reader.GetString(f.Name), out var type)
                && f.DecodeSignature(image.Types, (object?)null) == type),
            "DeviceMeshInterleaved has the exact plain-public-field runtime arena layout",
            "RuntimeFragmentAssets initializes every field without invoking the counter-advancing native constructor; update that adapter before supporting this changed host.");
    }

    private static void CheckFrameSeam(Image image, MethodDefinitionHandle prepare)
    {
        Expected[] expected =
        [
            new("KSA.Universe", "ApplyOrbitSolvers", "System.Void", true, MethodAttributes.Public),
            new("KSA.Universe", "ApplyVehicleSolvers", "System.Void", true, MethodAttributes.Public),
            new("KSA.Universe", "ApplyClothSolvers", "System.Void", true, MethodAttributes.Public),
            new("KSA.InputEvents", "ApplyInputEvents", "System.Void", true, MethodAttributes.Public),
            new("KSA.Universe", "ExecuteNextClothSolvers", "System.Void", true, MethodAttributes.Public, "System.Double", "KSA.SimStep"),
            new("KSA.Universe", "ExecuteNextVehicleSolvers", "System.Void", true, MethodAttributes.Public, "System.Double", "KSA.SimStep"),
            new("KSA.Universe", "ExecuteNextOrbitSolvers", "System.Void", true, MethodAttributes.Public, "System.Double", "KSA.SimStep"),
        ];
        foreach (var method in expected) CheckMethod(image, method);
        var calls = image.Calls(prepare);
        var keys = expected.Select(e => e.Key).ToArray();
        var problem = OrderedUnique(calls, keys);
        Check(problem == null, "PrepareFrame has one ordered apply/input/dispatch seam",
            problem ?? "Inspect GameHooks.InsertSettledUpdate against the changed host IL.");
        if (problem == null)
        {
            foreach (var key in keys)
                Console.WriteLine($"  IL_{calls.Single(c => c.Key == key).Offset:X4} {key}");
        }
        // Negative controls exercise the exact validator, without modifying or executing host IL.
        var reduced = calls.Where(c => keys.Contains(c.Key)).ToList();
        var duplicate = reduced.Concat(reduced.Take(1)).ToList();
        var missing = reduced.Skip(1).ToList();
        var reordered = reduced.ToList();
        if (reordered.Count >= 2) (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        Check(OrderedUnique(duplicate, keys) != null && OrderedUnique(missing, keys) != null
              && OrderedUnique(reordered, keys) != null,
            "Seam validator rejects duplicated, missing and reordered call controls",
            "The inspector's seam validator is not correctly detecting changed frame layouts.");
    }

    private static string? OrderedUnique(IReadOnlyList<Call> calls, string[] expected)
    {
        var previous = -1;
        foreach (var key in expected)
        {
            var indices = Enumerable.Range(0, calls.Count).Where(i => calls[i].Key == key).ToArray();
            if (indices.Length != 1) return $"Expected one call to {key}, found {indices.Length}. Re-check the affected hook.";
            if (indices[0] <= previous) return $"{key} moved before its required predecessor. Re-check the affected hook.";
            previous = indices[0];
        }
        return null;
    }

    private static void CheckRenderSeams(Image image)
    {
        // Depthless glow must follow atmosphere composition, but precede bloom/final composition.
        // Inspect actual host IL: compiling against the API alone cannot detect an order change.
        (string Method, string[] Stages)[] paths =
        [
            ("RenderGame", ["KSA.SuperMeshRenderSystem.RenderMainPass", "KSA.PlanetTransparenciesRenderer.Render",
                "KSA.Rendering.Atmosphere.Rendering.TransparenciesMsaaResolve.ApplyTransparenciesMsaaIfEnabled",
                "KSA.SuperMeshRenderSystem.RenderTranslucencyPass", "KSA.OverallBloomRenderer.Render"]),
            ("RenderViewport", ["KSA.SuperMeshRenderSystem.RenderMainPass", "KSA.PlanetTransparenciesRenderer.RenderAtmosphereOnly",
                "KSA.SuperMeshRenderSystem.RenderTranslucencyPass", "KSA.Rendering.RenderTarget.ResolveAttachments"]),
            ("RenderEditor", ["KSA.SuperMeshRenderSystem.RenderMainPass", "KSA.SuperMeshRenderSystem.RenderTranslucencyPass",
                "KSA.Rendering.RenderTarget.ResolveAttachments", "KSA.OverallBloomRenderer.Render"]),
        ];
        foreach (var (method, stages) in paths)
        {
            var methods = image.Methods("KSA.Program", method).ToArray();
            Check(methods.Length == 1, $"One Program.{method} rendering entry", "Inspect the host's changed rendering path.");
            if (methods.Length != 1) continue;
            var calls = image.Calls(methods[0]).Select(c => new Call(c.Offset, c.Key.Split('(')[0])).ToArray();
            var problem = OrderedUnique(calls, stages);
            Check(problem == null, $"Program.{method} orders saber translucency after the scene and before post-processing",
                problem ?? "Review SaberRenderer's translucency hook against the current host.");
        }
    }

    private static void CheckEntry(Image image)
    {
        const string attribute = "StarMap.API.StarMapModAttribute";
        var entries = image.Reader.TypeDefinitions.Where(h => image.Attributes(
            image.Reader.GetTypeDefinition(h).GetCustomAttributes()).Contains(attribute)).ToArray();
        Check(entries.Length == 1, "Exactly one [StarMapMod] entry class", $"Found {entries.Length}; StarMap selects the first marked type.");
        if (entries.Length != 1) return;
        var type = image.Reader.GetTypeDefinition(entries[0]);
        var name = image.TypeName(entries[0]);
        Check(name == "Purr2D2.Mod" && (type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public
              && (type.Attributes & TypeAttributes.Abstract) == 0,
            "Entry is public, concrete Purr2D2.Mod", $"Found {name} with flags {type.Attributes}.");
        CheckMethod(image, new Expected(name, ".ctor", "System.Void", false, MethodAttributes.Public));
        (string Name, string Method, string[] Parameters)[] lifecycle =
        [
            ("StarMapImmediateLoadAttribute", "ImmediateLoad", ["KSA.Mod"]),
            ("StarMapAllModsLoadedAttribute", "AllModsLoaded", []),
            ("StarMapAfterGuiAttribute", "AfterGui", ["System.Double"]),
            ("StarMapUnloadAttribute", "Unload", []),
        ];
        foreach (var hook in lifecycle)
        {
            var fullName = "StarMap.API." + hook.Name;
            var annotated = type.GetMethods().Where(h => image.Attributes(
                image.Reader.GetMethodDefinition(h).GetCustomAttributes()).Contains(fullName)).ToArray();
            Check(annotated.Length == 1, $"Exactly one [{hook.Name}] method",
                $"Found {annotated.Length}. This lifecycle callback will be missing or duplicated.");
            var handle = CheckMethod(image, new Expected(name, hook.Method, "System.Void", false,
                MethodAttributes.Public, hook.Parameters));
            if (!handle.IsNil)
                Check(annotated.Length == 1 && annotated[0] == handle,
                    $"{hook.Method} carries its expected lifecycle attribute",
                    "The attribute moved to another method; check StarMap callback discovery.");
        }
    }

    private static void CheckReferences(Image host, Image mod)
    {
        var references = mod.Reader.AssemblyReferences.Select(mod.Reader.GetAssemblyReference).ToArray();
        var names = references.Select(r => mod.Reader.GetString(r.Name)).ToArray();
        foreach (var name in new[] { "KSA", "StarMap.API", "0Harmony" })
            Check(names.Contains(name), $"Mod metadata references {name}", "The staged output may not be the intended production assembly.");
        var ksa = references.Where(r => mod.Reader.GetString(r.Name) == "KSA").ToArray();
        Check(ksa.Length == 1 && ksa[0].Version == host.AssemblyVersion,
            "Compiled KSA assembly-reference version matches inspected host",
            "Rebuild the mod against this host snapshot. Matching versions alone do not prove API compatibility.");
        Console.WriteLine("Mod assembly references (metadata only): " + string.Join(", ", references
            .Select(r => $"{mod.Reader.GetString(r.Name)} {r.Version}").OrderBy(n => n)));
    }

    private sealed class Image : IDisposable
    {
        private readonly FileStream _stream;
        private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
            .ToDictionary(o => unchecked((ushort)o.Value));
        public string Path { get; }
        public PEReader Pe { get; }
        public MetadataReader Reader { get; }
        public TypeNames Types { get; }
        public string AssemblyName => Reader.GetString(Reader.GetAssemblyDefinition().Name);
        public Version AssemblyVersion => Reader.GetAssemblyDefinition().Version;

        public Image(string path)
        {
            Path = System.IO.Path.GetFullPath(path);
            _stream = File.OpenRead(Path);
            Pe = new PEReader(_stream);
            if (!Pe.HasMetadata) throw new BadImageFormatException($"{Path} has no CLI metadata");
            Reader = Pe.GetMetadataReader();
            Types = new TypeNames();
        }
        public string TypeName(TypeDefinitionHandle h) => Types.GetTypeFromDefinition(Reader, h, 0);
        public TypeDefinitionHandle FindType(string name) => Reader.TypeDefinitions.FirstOrDefault(h => TypeName(h) == name);
        public IEnumerable<MethodDefinitionHandle> Methods(string type, string method)
        {
            var h = FindType(type);
            return h.IsNil ? [] : Reader.GetTypeDefinition(h).GetMethods()
                .Where(m => Reader.GetString(Reader.GetMethodDefinition(m).Name) == method);
        }
        public string MethodKey(MethodDefinitionHandle h)
        {
            var method = Reader.GetMethodDefinition(h);
            return Key(TypeName(method.GetDeclaringType()), Reader.GetString(method.Name), method.DecodeSignature(Types, (object?)null));
        }
        private static string Key(string type, string name, MethodSignature<string> sig) =>
            $"{type}.{name}({string.Join(",", sig.ParameterTypes)}):{sig.ReturnType}";

        private string EntityType(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => TypeName((TypeDefinitionHandle)h),
            HandleKind.TypeReference => Types.GetTypeFromReference(Reader, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => Types.GetTypeFromSpecification(Reader, null, (TypeSpecificationHandle)h, 0),
            _ => $"<{h.Kind}>",
        };
        private string MethodKey(EntityHandle h) => h.Kind switch
        {
            HandleKind.MethodDefinition => MethodKey((MethodDefinitionHandle)h),
            HandleKind.MemberReference => MemberKey((MemberReferenceHandle)h),
            HandleKind.MethodSpecification => MethodKey(Reader.GetMethodSpecification((MethodSpecificationHandle)h).Method),
            _ => $"<{h.Kind}>",
        };
        private string MemberKey(MemberReferenceHandle h)
        {
            var member = Reader.GetMemberReference(h);
            return Key(EntityType(member.Parent), Reader.GetString(member.Name), member.DecodeMethodSignature(Types, (object?)null));
        }
        public IEnumerable<string> Attributes(CustomAttributeHandleCollection handles)
        {
            foreach (var handle in handles)
            {
                var constructor = Reader.GetCustomAttribute(handle).Constructor;
                if (constructor.Kind == HandleKind.MemberReference)
                    yield return EntityType(Reader.GetMemberReference((MemberReferenceHandle)constructor).Parent);
                else if (constructor.Kind == HandleKind.MethodDefinition)
                    yield return TypeName(Reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType());
            }
        }
        public List<Call> Calls(MethodDefinitionHandle h)
        {
            var method = Reader.GetMethodDefinition(h);
            if (method.RelativeVirtualAddress == 0) throw new BadImageFormatException("PrepareFrame has no IL body");
            var il = Pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()
                     ?? throw new BadImageFormatException("PrepareFrame has empty IL");
            var result = new List<Call>();
            for (var position = 0; position < il.Length;)
            {
                var offset = position;
                ushort code = il[position++];
                if (code == 0xFE) code = (ushort)(0xFE00 | il[position++]);
                if (!Opcodes.TryGetValue(code, out var opcode)) throw new BadImageFormatException($"Unknown IL opcode at IL_{offset:X4}");
                if (opcode == OpCodes.Call || opcode == OpCodes.Callvirt)
                    result.Add(new Call(offset, MethodKey(MetadataTokens.EntityHandle(BitConverter.ToInt32(il, position)))));
                var size = opcode.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => checked(4 + 4 * BitConverter.ToInt32(il, position)),
                    OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                        or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                        or OperandType.ShortInlineR => 4,
                    _ => throw new BadImageFormatException($"Unsupported operand at IL_{offset:X4}"),
                };
                position = checked(position + size);
                if (position > il.Length) throw new BadImageFormatException($"Truncated operand at IL_{offset:X4}");
            }
            return result;
        }
        public void Dispose() { Pe.Dispose(); _stream.Dispose(); }
    }

    private sealed class TypeNames : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string element, ArrayShape shape) => element + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string element) => element + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "methodptr(" + string.Join(",", signature.ParameterTypes) + ")";
        public string GetGenericInstantiation(string generic, ImmutableArray<string> arguments) => generic + "<" + string.Join(",", arguments) + ">";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodified, bool required) => unmodified;
        public string GetPinnedType(string element) => element;
        public string GetPointerType(string element) => element + "*";
        public string GetPrimitiveType(PrimitiveTypeCode code) => "System." + (code switch
        {
            PrimitiveTypeCode.Void => "Void", PrimitiveTypeCode.Boolean => "Boolean", PrimitiveTypeCode.Char => "Char",
            PrimitiveTypeCode.SByte => "SByte", PrimitiveTypeCode.Byte => "Byte", PrimitiveTypeCode.Int16 => "Int16",
            PrimitiveTypeCode.UInt16 => "UInt16", PrimitiveTypeCode.Int32 => "Int32", PrimitiveTypeCode.UInt32 => "UInt32",
            PrimitiveTypeCode.Int64 => "Int64", PrimitiveTypeCode.UInt64 => "UInt64", PrimitiveTypeCode.Single => "Single",
            PrimitiveTypeCode.Double => "Double", PrimitiveTypeCode.String => "String", PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr", PrimitiveTypeCode.Object => "Object", PrimitiveTypeCode.TypedReference => "TypedReference",
            _ => code.ToString(),
        });
        public string GetSZArrayType(string element) => element + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeDefinition(handle);
            var parent = type.GetDeclaringType();
            var prefix = parent.IsNil ? reader.GetString(type.Namespace) : GetTypeFromDefinition(reader, parent, 0);
            return prefix.Length == 0 ? reader.GetString(type.Name) : prefix + (parent.IsNil ? "." : "+") + reader.GetString(type.Name);
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var type = reader.GetTypeReference(handle);
            var nested = type.ResolutionScope.Kind == HandleKind.TypeReference;
            var prefix = nested ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, 0) : reader.GetString(type.Namespace);
            return prefix.Length == 0 ? reader.GetString(type.Name) : prefix + (nested ? "+" : ".") + reader.GetString(type.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    }
}
