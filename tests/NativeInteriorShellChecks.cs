using KSA;
using Purr2D2.Cutting;

// Real KSA template/reference types; no game content, renderer or physics is created.
internal static class NativeInteriorShellChecks
{
    internal static void Run()
    {
        if (typeof(MeshReference).GetField(nameof(MeshReference.Inverted))?.FieldType != typeof(bool)
            || typeof(PartModelModule.Template).GetField(nameof(PartModelModule.Template.Internal))?.FieldType != typeof(bool))
            throw new Exception("Native inside-out mesh or internal part-model flag changed.");
        static PartModelModule.Template Model(bool internalView, bool? inverted) => new()
        {
            Internal = internalView,
            Mesh = inverted is { } value ? new DataReference<MeshReference> { Value = new MeshReference { Inverted = value } } : null,
        };
        Check(NativeInteriorShell.ExcludedFromCapture(Model(true, true)),
            "an internal inside-out hull copy is excluded from slice capture");
        Check(!NativeInteriorShell.ExcludedFromCapture(Model(false, true)),
            "an exterior inverted mesh keeps its existing capture behaviour");
        Check(!NativeInteriorShell.ExcludedFromCapture(Model(true, false)),
            "ordinary internal IVA geometry keeps its existing capture behaviour");
        Check(!NativeInteriorShell.ExcludedFromCapture(Model(false, false)) && !NativeInteriorShell.ExcludedFromCapture(Model(true, null)),
            "ordinary and unresolved meshes are not silently dropped");
        Console.WriteLine("PASS: only native internal inside-out hull copies (5901 MeshAtlas Inverted) are excluded from slice capture");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Native interior shell check failed: " + message);
    }
}
