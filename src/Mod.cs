using HarmonyLib;
using Purr2D2.Runtime;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using Purr2D2.Core;
using StarMap.API;
using MeowSci.ModRuntime;

namespace Purr2D2;

[StarMapMod]
public sealed class Mod
{
    private Harmony? _harmony;
    private SaberRuntime? _runtime;
    private string _directory = Path.GetDirectoryName(typeof(Mod).Assembly.Location)!;

    [StarMapImmediateLoad]
    public void ImmediateLoad(KSA.Mod mod)
    {
        _directory = mod.DirectoryPath;
        ClutterTintShader.Install();
        // Reserve native mesh space and register persisted slice templates before game saves load.
        SaberSettings settings;
        try { settings = SaberSettings.Load(Path.Combine(_directory, "settings.json")); }
        catch (Exception error)
        {
            settings = new();
            Console.WriteLine($"[purr2d2] Could not read slice resource preferences; using defaults: {error.Message}");
        }
        RuntimeFragmentAssets.Initialize(mod, settings.SliceVertexArenaMiB, settings.SliceIndexArenaMiB,
            settings.SliceAssetDiskMiB, settings.SliceAssetEntryLimit);
    }

    [StarMapAllModsLoaded]
    public void AllModsLoaded()
    {
        RuntimeBootstrap.Report("purr2d2");
        try
        {
            _harmony = new Harmony("meow-sci.purr2d2");
            _runtime = new SaberRuntime(_directory);
            _runtime.Initialize(_harmony);
            SliceJitWarmup.Start(); // Patches are installed; compile the cold native path off-thread.
            Console.WriteLine("[purr2d2] Ready. Enable from the Purr2D2 game menu to show the overlay.");
        }
        catch (Exception ex)
        {
            SaberRuntime.Report("Initialization failed", ex);
            _runtime?.SetInitializationFault(ex.Message);
            SliceWorkerHooks.Uninstall();
            _harmony?.UnpatchAll(_harmony.Id);
        }
    }

    [StarMapAfterGui]
    public void AfterGui(double dt) => _runtime?.DrawUi();

    [StarMapUnload]
    public void Unload()
    {
        SliceFrameDiagnostics.Reset();
        SliceJitWarmup.Cancel(); // Stop between methods; never join an in-progress compilation.
        try { _runtime?.Dispose(); }
        catch (Exception ex) { SaberRuntime.Report("Unload cleanup failed", ex); }
        finally
        {
            ClutterTintShader.Remove();
            GameHooks.Runtime = null;
            SliceWorkerHooks.Uninstall();
            _harmony?.UnpatchAll(_harmony.Id);
            _runtime = null;
            _harmony = null;
        }
    }
}
