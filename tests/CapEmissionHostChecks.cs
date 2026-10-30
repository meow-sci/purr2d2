using System.Reflection;
using HarmonyLib;
using KSA;
using KSA.Rendering;

// Cut-cap glow rewrites PartModel instance lists in a WriteInstancesToGpu prefix, because
// batched PartTreeRenderData.Compose fills them without calling AddInstance. Prove on host
// IL that every static part model is uploaded there, from the list the prefix rewrites.
internal static class CapEmissionHostChecks
{
    internal static void Run()
    {
        var upload = AccessTools.Method(typeof(PartModel), nameof(PartModel.WriteInstancesToGpu), [typeof(IViewport), typeof(int)]);
        Require(upload != null && !upload.IsStatic && upload.ReturnType == typeof(void)
            && upload.GetParameters().Select(p => p.Name).SequenceEqual(["viewport", "frameIndex"]),
            "PartModel.WriteInstancesToGpu(IViewport viewport, int frameIndex) changed; Harmony binds viewport by name.");
        var il = PatchProcessor.GetOriginalInstructions(upload!);
        var get = AccessTools.Method(typeof(PartModel.ViewportData), nameof(PartModel.ViewportData.Get), [typeof(PartModel), typeof(IViewport)]);
        var list = AccessTools.Field(typeof(PartModel.ViewportData), nameof(PartModel.ViewportData.InstanceList));
        int read = il.FindIndex(c => get != null && c.Calls(get));
        int uploaded = il.FindLastIndex(c => c.operand is MethodInfo m && m.DeclaringType == typeof(DeviceVector) && m.Name == "Add");
        Require(read >= 0 && uploaded > read && il.Skip(read).Take(uploaded - read).Any(c => c.LoadsField(list)),
            "WriteInstancesToGpu must upload the per-viewport PartModel.ViewportData.InstanceList that the cap prefix rewrites.");
        var update = AccessTools.Method(typeof(PartModelRenderer), nameof(PartModelRenderer.UpdateRenderData), [typeof(IViewport), typeof(int)]);
        Require(update != null && PatchProcessor.GetOriginalInstructions(update).Count(c => c.Calls(upload!)) == 1,
            "PartModelRenderer.UpdateRenderData must upload every static PartModel through WriteInstancesToGpu.");
        Console.WriteLine("PASS: actual KSA uploads each static part-model view from ViewportData.InstanceList in WriteInstancesToGpu, the cut-cap emission prefix target");
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
