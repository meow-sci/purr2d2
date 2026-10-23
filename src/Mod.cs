using HarmonyLib;
using Purr2D2.Runtime;
using Purr2D2.Rendering;
using Purr2D2.Slicing;
using StarMap.API;

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
        // Reserve native mesh space and register persisted slice templates before game saves load.
        RuntimeFragmentAssets.Initialize(mod);
    }

    [StarMapAllModsLoaded]
    public void AllModsLoaded()
    {
        try
        {
            _harmony = new Harmony("meow-sci.purr2d2");
            _runtime = new SaberRuntime(_directory);
            _runtime.Initialize(_harmony);
            SliceJitWarmup.Start(); // Patches are installed; compile the cold native path off-thread.
            Console.WriteLine("[purr2d2] Ready. F7 controls; Ctrl+F7 equip; Shift+F7 ignite; Alt+F7 swing.");
        }
        catch (Exception ex)
        {
            SaberRuntime.Report("Initialization failed", ex);
            _runtime?.SetInitializationFault(ex.Message);
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
            GameHooks.Runtime = null;
            _harmony?.UnpatchAll(_harmony.Id);
            _runtime = null;
            _harmony = null;
        }
    }
}
