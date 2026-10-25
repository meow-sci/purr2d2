using HarmonyLib;
using KSA;
using Purr2D2.Persistence;

namespace Purr2D2.Runtime;

/// <summary>Passive observers. Unscience may defer a request; only executed originals own transactions.</summary>
internal static class KittenSaveHooks
{
    private static SaberRuntime? Runtime => GameHooks.Runtime;

    internal static void Install(Harmony harmony)
    {
        Patch(typeof(GameSave), nameof(GameSave.Populate), [], nameof(InvalidateCapture), nameof(Captured));
        Patch(typeof(UncompressedSave), nameof(UncompressedSave.Write), [], postfix: nameof(Written), finalizer: nameof(WriteFinished));
        Patch(typeof(UncompressedSave), nameof(UncompressedSave.Load), [], nameof(Loading), finalizer: nameof(LoadFinished));
        Patch(typeof(Universe), nameof(Universe.DeserializeSave), [typeof(UniverseData)], nameof(Reconstructing), finalizer: nameof(Reconstructed));
        Patch(typeof(Universe), nameof(Universe.LoadSystem), [typeof(string)], nameof(ChangingSystem), finalizer: nameof(SystemChanged));

        void Patch(Type type, string name, Type[] arguments, string? prefix = null, string? postfix = null, string? finalizer = null)
        {
            var target = AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);
            HarmonyMethod? Method(string? callback) => callback is null ? null : new(typeof(KittenSaveHooks), callback)
            {
                priority = Priority.Last - 100,
                after = ["MeowSci.Unscience"]
            };
            harmony.Patch(target, prefix: Method(prefix), postfix: Method(postfix), finalizer: Method(finalizer));
        }
    }

    private static void InvalidateCapture(GameSave __instance) => Runtime?.SceneSaves.InvalidateCapture(__instance);
    private static void Captured(GameSave __instance, bool __runOriginal)
    {
        if (!__runOriginal || Runtime is not { } runtime) return;
        Observe(() =>
        {
            runtime.CaptureKittenChoices();
            runtime.SceneSaves.CaptureSave(__instance, __instance.UniverseData);
        });
    }

    private static void Written(UncompressedSave __instance, bool __result, bool __runOriginal)
    {
        if (__runOriginal) Observe(() => Runtime?.SceneSaves.WriteSave(__instance, __instance.UniverseData,
            __instance.Directory.FullName, __result), "KSA saved, but kitten settings could not be written");
        else Runtime?.SceneSaves.InvalidateCapture(__instance);
    }

    private static Exception? WriteFinished(UncompressedSave __instance, Exception? __exception)
    {
        if (__exception is not null)
        {
            Runtime?.SceneSaves.InvalidateCapture(__instance);
            Runtime?.SceneSaves.Warn("Native save failed; kitten settings were not written: " + __exception.Message);
        }
        return __exception;
    }

    private static void Loading(UncompressedSave __instance, bool __runOriginal, out KittenSaveSession.FileLoad? __state)
    {
        __state = null;
        if (__runOriginal && Runtime is { } runtime)
            Observe(() => runtime.SceneSaves.BeginFileLoad(__instance, () => __instance.UniverseData, __instance.Directory.FullName), out __state);
    }

    private static Exception? LoadFinished(KittenSaveSession.FileLoad? __state, Exception? __exception)
    {
        Runtime?.SceneSaves.EndFileLoad(__state);
        if (__exception is not null) Runtime?.SceneSaves.Warn("Game load reported an error: " + __exception.Message);
        return __exception;
    }

    private static void Reconstructing(UniverseData universeData, bool __runOriginal, out KittenSaveSession.Reconstruction? __state)
    {
        __state = null;
        if (!__runOriginal || Runtime is not { } runtime || Universe.CurrentSystem is null) return;
        // GameHooks' earlier prefix joins native jobs and releases old bindings first.
        __state = runtime.SceneSaves.BeginReconstruction(universeData, Universe.CurrentSystem, fileAllowed: universeData is not null);
    }

    private static Exception? Reconstructed(KittenSaveSession.Reconstruction? __state, Exception? __exception, bool __runOriginal)
    {
        Observe(() => Runtime?.SceneSaves.FinishReconstruction(__state, Universe.CurrentSystem,
            __runOriginal ? __exception : new InvalidOperationException("Native reconstruction was skipped."), systemChange: !__runOriginal));
        return __exception;
    }

    private static void ChangingSystem(string id, bool __runOriginal, out KittenSaveSession.Reconstruction? __state)
    {
        __state = null;
        if (!__runOriginal || Runtime is not { } runtime || SystemLibrary.Find(id) is null) return;
        JobSystems.WaitAll();
        runtime.ResetWorld();
        __state = runtime.SceneSaves.BeginReconstruction(null, Universe.CurrentSystem, fileAllowed: false);
    }

    private static Exception? SystemChanged(KittenSaveSession.Reconstruction? __state, Exception? __exception, bool __runOriginal)
    {
        Observe(() => Runtime?.SceneSaves.FinishReconstruction(__state, Universe.CurrentSystem,
            __runOriginal ? __exception : new InvalidOperationException("Native system change was skipped."), systemChange: true));
        return __exception;
    }

    private static void Observe(Action action, string failure = "Kitten save/restore failed")
    {
        try { action(); }
        catch (Exception error) { Runtime?.SceneSaves.Warn(failure + ": " + error.Message); }
    }

    private static void Observe<T>(Func<T> action, out T? result) where T : class
    {
        result = null;
        try { result = action(); }
        catch (Exception error) { Runtime?.SceneSaves.Warn("Kitten save/restore failed: " + error.Message); }
    }
}
