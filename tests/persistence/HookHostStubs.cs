using System.Runtime.CompilerServices;
using HarmonyLib;
using Purr2D2.Persistence;

namespace Purr2D2.Core
{
    // Keep this executable native-free; the production enum has these exact values.
    public enum SaberType { Light, Dark }
}

namespace KSA
{
    internal static class HostTrace
    {
        internal static readonly List<string> Events = [];
        internal static void Add(string value) => Events.Add(value);
    }

    public sealed class HostFailure(string message) : Exception(message);

    public sealed class UniverseData(string label = "initial")
    {
        public readonly string Label = label;
        public bool FailReconstruction;
        public Action? DuringReconstruction;
    }

    public class GameSave
    {
        private static int _nextSnapshot;
        public UniverseData UniverseData = new();
        public bool FailPopulate;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Populate()
        {
            HostTrace.Add("native populate");
            if (FailPopulate) throw new HostFailure("populate failed");
            UniverseData = new("snapshot-" + ++_nextSnapshot);
        }
    }

    public sealed class UncompressedSave(string directory) : GameSave
    {
        public readonly DirectoryInfo Directory = new(directory);
        public bool FailWrite, ThrowWrite, FailRead, BlockSidecarPublication;
        public Func<UniverseData>? ReadData;
        public Action? AfterGameLoaded;
        public int NativeLoads;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool Write()
        {
            HostTrace.Add("native write");
            if (ThrowWrite) throw new HostFailure("write failed");
            if (FailWrite) return false;
            Directory.Create();
            // Native overwrites remove the old sidecar along with the old save folder.
            File.Delete(Path.Combine(Directory.FullName, KittenSaveStorage.FileName));
            File.WriteAllText(Path.Combine(Directory.FullName, "universe.xml"), UniverseData.Label);
            if (BlockSidecarPublication)
                System.IO.Directory.CreateDirectory(Path.Combine(Directory.FullName, KittenSaveStorage.FileName));
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Load()
        {
            if (Program.IsEditorOpen) { HostTrace.Add("editor refusal"); return; }
            ++NativeLoads;
            HostTrace.Add("native read");
            if (FailRead) return; // KSA catches an unreadable XML file and returns.
            UniverseData = ReadData?.Invoke()
                ?? new UniverseData(File.ReadAllText(Path.Combine(Directory.FullName, "universe.xml")));
            Universe.DeserializeSave(UniverseData);
            Program.OnGameLoaded();
            AfterGameLoaded?.Invoke();
        }
    }

    public static class Program
    {
        public static bool IsEditorOpen, ThrowOnGameLoaded;
        public static Action? DuringGameLoaded;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void OnGameLoaded()
        {
            HostTrace.Add("game loaded");
            DuringGameLoaded?.Invoke();
            if (ThrowOnGameLoaded) throw new HostFailure("game-loaded failed");
        }
    }

    public sealed class CelestialSystem(string id = "initial")
    {
        public readonly string Id = id;
    }

    public static class Universe
    {
        public static CelestialSystem? CurrentSystem = new();
        public static UniverseData? CurrentData;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void DeserializeSave(UniverseData universeData)
        {
            if (CurrentSystem is null) throw new HostFailure("no system");
            JobSystems.WaitAll();
            HostTrace.Add("native destruction");
            CurrentData = null;
            if (universeData.FailReconstruction) throw new HostFailure("reconstruction failed");
            CurrentData = universeData;
            HostTrace.Add("native reconstructed " + universeData.Label);
            universeData.DuringReconstruction?.Invoke();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void LoadSystem(string id)
        {
            if (SystemLibrary.Find(id) is null) throw new HostFailure("unknown system");
            CurrentSystem = new(id);
            CurrentData = null;
            HostTrace.Add("native system " + id);
            if (SystemLibrary.FailInitialization) throw new HostFailure("system initialization failed");
        }
    }

    public static class SystemLibrary
    {
        public static bool FailInitialization;
        public static object? Find(string id) => id is "valid" or "other" ? id : null;
    }

    public static class JobSystems
    {
        public static bool FailWait;
        public static void WaitAll()
        {
            HostTrace.Add("join jobs");
            if (FailWait) throw new HostFailure("worker join failed");
        }
    }
}

namespace Purr2D2.Runtime
{
    internal sealed class SaberRuntime
    {
        internal readonly KittenSaveSession SceneSaves = new();
        internal bool BindingsAlive = true, FailCapture;
        internal int CaptureCount, ResetCount;

        internal void CaptureKittenChoices()
        {
            ++CaptureCount;
            if (FailCapture) throw new KSA.HostFailure("choice capture failed");
        }

        internal void ResetWorld()
        {
            ++ResetCount;
            BindingsAlive = false;
            KSA.HostTrace.Add("release bindings");
        }
    }

    internal static class GameHooks
    {
        internal static SaberRuntime? Runtime;

        internal static void InstallBaseline(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(KSA.Universe), nameof(KSA.Universe.DeserializeSave)),
                prefix: new HarmonyMethod(typeof(GameHooks), nameof(BeforeDeserialize)) { priority = Priority.Last });
            harmony.Patch(AccessTools.Method(typeof(KSA.Program), nameof(KSA.Program.OnGameLoaded)),
                postfix: new HarmonyMethod(typeof(GameHooks), nameof(AfterGameLoaded)));
        }

        private static void BeforeDeserialize(bool __runOriginal)
        {
            if (!__runOriginal) return;
            KSA.JobSystems.WaitAll();
            Runtime?.ResetWorld();
        }

        private static void AfterGameLoaded() => Runtime?.ResetWorld();
    }

    /// <summary>Models another mod skipping/holding native calls; replay is explicit in each test.</summary>
    internal static class OtherSaveHooks
    {
        internal static bool SkipPopulate, SkipWrite, SkipLoad, SkipDeserialize, SkipSystem;
        internal static bool DeferLoad, DeferDeserialize, IsReplaying, ThrowBeforeLoad;
        internal static Action? HeldRequest;

        internal static void Install(Harmony harmony)
        {
            Patch(typeof(KSA.GameSave), nameof(KSA.GameSave.Populate), nameof(Populate));
            Patch(typeof(KSA.UncompressedSave), nameof(KSA.UncompressedSave.Write), nameof(Write));
            Patch(typeof(KSA.UncompressedSave), nameof(KSA.UncompressedSave.Load), nameof(Load));
            Patch(typeof(KSA.Universe), nameof(KSA.Universe.DeserializeSave), nameof(Deserialize));
            Patch(typeof(KSA.Universe), nameof(KSA.Universe.LoadSystem), nameof(System));
            void Patch(Type type, string name, string method) => harmony.Patch(AccessTools.Method(type, name),
                prefix: new HarmonyMethod(typeof(OtherSaveHooks), method));
        }

        internal static void Reset()
        {
            SkipPopulate = SkipWrite = SkipLoad = SkipDeserialize = SkipSystem = false;
            DeferLoad = DeferDeserialize = IsReplaying = ThrowBeforeLoad = false;
            HeldRequest = null;
        }

        internal static void Replay()
        {
            Action? request = HeldRequest;
            HeldRequest = null;
            IsReplaying = true;
            try { request?.Invoke(); }
            finally { IsReplaying = false; }
        }

        private static bool Populate() => !SkipPopulate;
        private static bool Write(ref bool __result)
        {
            if (!SkipWrite) return true;
            __result = true; // A skipped method can still have a convincing return value.
            return false;
        }
        private static bool Load(KSA.UncompressedSave __instance)
        {
            if (ThrowBeforeLoad) throw new KSA.HostFailure("earlier load prefix failed");
            if (SkipLoad) return false;
            if (!DeferLoad || IsReplaying) return true;
            HeldRequest = __instance.Load;
            return false;
        }
        private static bool Deserialize(KSA.UniverseData universeData)
        {
            if (SkipDeserialize) return false;
            if (!DeferDeserialize || IsReplaying) return true;
            HeldRequest = () => KSA.Universe.DeserializeSave(universeData);
            return false;
        }
        private static bool System() => !SkipSystem;
    }
}
