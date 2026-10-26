using System.Text.Json;
using HarmonyLib;
using KSA;
using Purr2D2.Core;
using Purr2D2.Persistence;
using Purr2D2.Runtime;

internal static class SaveChecks
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "purr2d2-save-checks-" + Guid.NewGuid().ToString("N"));
    private static int _passed, _failed, _assertions, _directory;
    private static SaberRuntime Runtime => GameHooks.Runtime!;
    private static KittenSaveSession Session => Runtime.SceneSaves;

    private static int Main()
    {
        Directory.CreateDirectory(Root);
        var own = new Harmony("Purr2D2.Persistence.Checks");
        var other = new Harmony("MeowSci.Unscience");
        try
        {
            Run("storage round-trip, filename and detached capture", StorageRoundTrip);
            Run("storage rejects foreign, duplicate, deep, oversized and future files", StorageRejection);
            Run("unsupported and invalid kitten records survive alongside editable kittens", PreservedRecords);
            Run("known profile extension data survives edits", ProfileExtensionData);
            Run("old profiles inherit the default length and invalid overrides remain preserved", LengthProfileCompatibility);
            Run("changed character records remain recoverable through explicit discard", CharacterMismatchRecovery);
            Run("invalid colors, enums and excessive entry counts are rejected", InvalidSettings);
            Run("session generations reject stale completion and recognize known system", SessionGenerations);
            foreach (bool otherFirst in new[] { false, true })
            {
                Console.WriteLine("Hook installation order: " + (otherFirst ? "other mod, Purr2D2" : "Purr2D2, other mod"));
                if (otherFirst) OtherSaveHooks.Install(other);
                GameHooks.InstallBaseline(own);
                KittenSaveHooks.Install(own);
                if (!otherFirst) OtherSaveHooks.Install(other);
                try { HookChecks(); }
                finally { other.UnpatchAll(other.Id); own.UnpatchAll(own.Id); }
            }
        }
        finally
        {
            other.UnpatchAll(other.Id);
            own.UnpatchAll(own.Id);
            GameHooks.Runtime = null;
            Directory.Delete(Root, recursive: true);
        }
        Console.WriteLine($"Persistence: {_passed} scenarios, {_assertions} assertions passed; {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void HookChecks()
    {
        Run("captures pair with exact save object and snapshot", CapturePairing);
        Run("false and throwing writes consume their captures", FailedWrites);
        Run("sidecar publication failure does not turn native success into failure", SidecarFailure);
        Run("skipped writes cannot publish or retain a capture", SkippedWrite);
        Run("failed and skipped Populate invalidate earlier capture", FailedPopulate);
        Run("choice-capture failure cannot reuse older state", FailedChoiceCapture);
        Run("successful load replaces native snapshot before profile binding", NormalLoad);
        Run("deferred and skipped loads do not stage destructive work", DeferredLoad);
        Run("unreadable, editor-refused and earlier-prefix failures retain old scene", EarlyLoadFailure);
        Run("failed reconstruction quarantines incoming settings", ReconstructionFailure);
        Run("trailing OnGameLoaded failure keeps reconstructed scene profiles", TrailingLoadFailure);
        Run("nested file load wins over its outer load", NestedFileLoad);
        Run("nested unreadable file does not replace a completed outer load", NestedUnreadableLoad);
        Run("direct operation inside file load cannot borrow its sidecar", NestedDirectOperation);
        Run("deferred and skipped direct deserialize preserve old choices", DeferredDirectLoad);
        Run("successful direct deserialize clears profiles without OnGameLoaded", DirectLoad);
        Run("new, skipped, invalid and failing systems have independent lifecycles", SystemChanges);
        Run("worker join failure preserves old scene before reset", JoinFailure);
        Run("a later prefix veto cannot commit a skipped reconstruction", LateVeto);
        Run("a skipped transaction cannot resume an already quarantined scene", QuarantineVeto);
        Run("vanilla, corrupt and future sidecars clear old scene on native success", VanillaLoad);
    }

    private static void Run(string name, Action test)
    {
        OtherSaveHooks.Reset(); HostTrace.Events.Clear();
        KSA.Program.IsEditorOpen = KSA.Program.ThrowOnGameLoaded = false;
        KSA.Program.DuringGameLoaded = null;
        SystemLibrary.FailInitialization = JobSystems.FailWait = false;
        Universe.CurrentSystem = new(); Universe.CurrentData = null;
        GameHooks.Runtime = new();
        Session.ObserveSystem(Universe.CurrentSystem);
        try { test(); ++_passed; Console.WriteLine("PASS " + name); }
        catch (Exception error) { ++_failed; Console.WriteLine($"FAIL {name}: {error}"); }
    }

    private static string DirectoryFor(string label)
    {
        string directory = Path.Combine(Root, ++_directory + "-" + label);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "universe.xml"), "native-" + label);
        return directory;
    }

    private static KittenIdentity Identity(string id) => new("roster", id, "stock-cat");
    private static KittenProfile Profile(float red = .25f) => new()
    { Red = red, Green = .5f, Blue = .75f, Type = SaberType.Dark, CapeEnabled = true, SaberEnabled = true };
    private static KittenSaveDocument Document(string id, float red = .25f) => new()
    {
        Kittens = [new() { Identity = Identity(id), State = JsonSerializer.SerializeToElement(Profile(red), KittenSaveStorage.Options) }]
    };
    private static KittenProfile Seed(string id = "old", float red = .25f) =>
        Session.Profiles.GetOrCreate(Identity(id), Profile(red), Session.Warn)!;
    private static string[] Names(KittenSaveSession? session = null) => (session ?? Session).Profiles.Capture()
        .Kittens.Select(record => record.Identity.Id).Order(StringComparer.Ordinal).ToArray();
    private static void ExpectNames(params string[] names) => Check(Names().SequenceEqual(names.Order(StringComparer.Ordinal)),
        "expected profiles [" + string.Join(", ", names) + "], found [" + string.Join(", ", Names()) + "]");
    private static KittenProfile SavedProfile(string directory, string id) => KittenSaveStorage.Read(directory)!.Kittens
        .Single(record => record.Identity.Id == id).State.Deserialize<KittenProfile>(KittenSaveStorage.Options)!;
    private static UncompressedSave FileSave(string id, bool sidecar = true)
    {
        string directory = DirectoryFor(id);
        if (sidecar) KittenSaveStorage.Write(directory, Document(id));
        return new(directory) { ReadData = () => new UniverseData("loaded-" + id) };
    }
    private static bool HasSidecar(UncompressedSave save) => File.Exists(Path.Combine(save.Directory.FullName, KittenSaveStorage.FileName));

    private static void StorageRoundTrip()
    {
        string directory = DirectoryFor("round-trip");
        Check(KittenSaveStorage.Read(directory) is null, "missing sidecar is native-only");
        var document = Document("one");
        var profileWithOverride = Profile(); profileWithOverride.BladeLengthOverride = 2.75f;
        document.Kittens[0].State = JsonSerializer.SerializeToElement(profileWithOverride, KittenSaveStorage.Options);
        KittenSaveStorage.Write(directory, document);
        var restored = KittenSaveStorage.Read(directory)!;
        Check(restored.SchemaVersion == 1 && restored.UniverseSha256.Length == 64, "schema and native fingerprint persisted");
        var profile = SavedProfile(directory, "one");
        Check(profile.Red == .25f && profile.Green == .5f && profile.Blue == .75f
            && profile.Type == SaberType.Dark && profile.CapeEnabled && profile.SaberEnabled
            && profile.BladeLengthOverride == 2.75f, "all user choices and the optional blade length round-trip");
        Check(File.Exists(Path.Combine(directory, "purr2d2.json")) && !File.Exists(Path.Combine(directory, "unscience.json")), "Purr2D2 owns only its sidecar");
        Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "atomic write leaves no temporary file");
    }

    private static void StorageRejection()
    {
        string directory = DirectoryFor("rejection");
        string path = Path.Combine(directory, KittenSaveStorage.FileName);
        KittenSaveStorage.Write(directory, Document("one"));
        File.AppendAllText(Path.Combine(directory, "universe.xml"), "different universe");
        Throws<InvalidDataException>(() => KittenSaveStorage.Read(directory), "foreign native XML rejected");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"SchemaVersion\":1}");
        Throws<InvalidDataException>(() => KittenSaveStorage.Read(directory), "duplicate root property rejected");
        File.WriteAllText(path, "{\"extra\":{\"x\":1,\"x\":2}}");
        Throws<InvalidDataException>(() => KittenSaveStorage.Read(directory), "nested duplicate property rejected");
        File.WriteAllText(path, new string('[', 40) + "0" + new string(']', 40));
        Throws<JsonException>(() => KittenSaveStorage.Read(directory), "excessive JSON depth rejected");
        File.WriteAllBytes(path, new byte[KittenSaveStorage.MaximumBytes + 1]);
        Throws<InvalidDataException>(() => KittenSaveStorage.Read(directory), "oversized sidecar rejected before parsing");
        var future = Document("one"); future.SchemaVersion = 2;
        File.WriteAllText(path, JsonSerializer.Serialize(future, KittenSaveStorage.Options));
        Throws<InvalidDataException>(() => KittenSaveStorage.Read(directory), "future document schema rejected");
        var duplicate = Document("one"); duplicate.Kittens.Add(duplicate.Kittens[0]);
        Throws<InvalidDataException>(() => KittenSaveStorage.Write(directory, duplicate), "duplicate kitten identity rejected");
        var unknown = Document("one"); unknown.Kittens[0].Identity = new("guess", "one", "stock-cat");
        Throws<InvalidDataException>(() => KittenSaveStorage.Write(directory, unknown), "unknown locator kind rejected");
    }

    private static void PreservedRecords()
    {
        var document = Document("future");
        document.Kittens[0].Version = 9;
        document.Kittens[0].State = JsonSerializer.SerializeToElement(new { FutureCape = "iridescent", Nested = new[] { 1, 2 } });
        string original = document.Kittens[0].State.GetRawText();
        document.Kittens.Add(new() { Identity = Identity("invalid"), State = JsonDocument.Parse("{\"Red\":2}").RootElement.Clone() });
        Session.Profiles.Import(document, Session.Warn);
        Check(Session.Profiles.PreservedCount == 2, "future and invalid records remain preserved");
        Check(Session.Profiles.GetOrCreate(Identity("future"), Profile(), Session.Warn) is null, "unsupported record cannot be silently replaced");
        Seed("editable", .6f).Red = .8f;
        var captured = Session.Profiles.Capture();
        Check(captured.Kittens.Single(record => record.Identity.Id == "future").State.GetRawText() == original, "opaque record preserved unchanged");
        string directory = DirectoryFor("preserved"); KittenSaveStorage.Write(directory, captured);
        Check(SavedProfile(directory, "editable").Red == .8f, "one unresolved kitten does not freeze another kitten's edits");
        var reloaded = new KittenProfiles(); reloaded.Import(KittenSaveStorage.Read(directory), Session.Warn);
        Check(reloaded.PreservedCount == 2, "opaque records survive a disk round-trip");
    }

    private static void ProfileExtensionData()
    {
        var document = Document("one");
        var profile = Profile(); profile.Extra = new() { ["FutureSetting"] = JsonSerializer.SerializeToElement(new { Width = 3 }) };
        document.Kittens[0].State = JsonSerializer.SerializeToElement(profile, KittenSaveStorage.Options);
        Session.Profiles.Import(document, Session.Warn);
        Session.Profiles.GetOrCreate(Identity("one"), Profile(), Session.Warn)!.Red = .9f;
        var state = Session.Profiles.Capture().Kittens[0].State;
        Check(state.GetProperty("FutureSetting").GetProperty("Width").GetInt32() == 3 && state.GetProperty("Red").GetSingle() == .9f,
            "recognized settings can change without discarding unknown fields");
    }

    private static void LengthProfileCompatibility()
    {
        var oldProfile = Document("old-format");
        using (var state = JsonDocument.Parse("{\"Red\":0.25,\"Green\":0.5,\"Blue\":0.75,\"Type\":\"Dark\",\"CapeEnabled\":true,\"SaberEnabled\":true}"))
            oldProfile.Kittens[0].State = state.RootElement.Clone();
        Session.Profiles.Import(oldProfile, Session.Warn);
        Check(Session.Profiles.GetOrCreate(Identity("old-format"), Profile(), Session.Warn)?.BladeLengthOverride is null,
            "version-one profiles without the optional override remain valid and inherit the global default");

        var invalid = Document("invalid-length");
        using (var state = JsonDocument.Parse("{\"Red\":0.25,\"Green\":0.5,\"Blue\":0.75,\"Type\":\"Dark\",\"CapeEnabled\":true,\"SaberEnabled\":true,\"BladeLengthOverride\":12.01}"))
            invalid.Kittens[0].State = state.RootElement.Clone();
        string original = invalid.Kittens[0].State.GetRawText();
        Session.Profiles.Import(invalid, Session.Warn);
        Check(Session.Profiles.PreservedCount == 1
            && Session.Profiles.GetOrCreate(Identity("invalid-length"), Profile(), Session.Warn) is null
            && Session.Profiles.Capture().Kittens[0].State.GetRawText() == original,
            "out-of-range overrides are rejected while their original profile JSON remains untouched");
    }

    private static void CharacterMismatchRecovery()
    {
        var original = Seed("changed-character", .6f);
        var changed = new KittenIdentity("roster", "changed-character", "different-cat");
        Check(Session.Profiles.GetOrCreate(changed, Profile(), Session.Warn) is null,
            "character guard rejects a mismatched live identity");
        Check(Session.Profiles.PreservedCount == 1, "mismatched record is visible to unavailable-record recovery");
        original.Red = .9f;
        var preserved = Session.Profiles.Capture().Kittens.Single();
        Check(preserved.Identity.Character == "stock-cat" && preserved.State.GetProperty("Red").GetSingle() == .6f,
            "unavailable record retains detached original identity and latest valid choices");
        Session.Profiles.DiscardUnavailable();
        Check(Session.Profiles.PreservedCount == 0 && Names().Length == 0, "explicit discard removes the blocked record");
        Check(Session.Profiles.GetOrCreate(changed, Profile(.8f), Session.Warn)?.Red == .8f,
            "after explicit recovery the current character can obtain fresh choices");
    }

    private static void CapturePairing()
    {
        var profile = Seed();
        var first = FileSave("capture-first", sidecar: false);
        first.Populate(); profile.Red = .6f;
        var second = FileSave("capture-second", sidecar: false);
        second.Populate(); profile.Red = .9f;
        Check(second.Write() && first.Write(), "native writes succeed in reversed capture order");
        Check(SavedProfile(first.Directory.FullName, "old").Red == .25f
            && SavedProfile(second.Directory.FullName, "old").Red == .6f, "each detached capture belongs to the exact save object");
        var changed = FileSave("capture-changed", sidecar: false); changed.Populate(); changed.UniverseData = new("replacement");
        Check(changed.Write() && !HasSidecar(changed), "changed native snapshot cannot publish an earlier profile capture");
        var missing = FileSave("capture-missing", sidecar: false);
        Check(missing.Write() && !HasSidecar(missing), "write without capture does not substitute current scene choices");
    }

    private static void InvalidSettings()
    {
        string valid = JsonSerializer.Serialize(Profile(), KittenSaveStorage.Options);
        foreach (string state in new[]
        {
            valid.Replace("0.25", "-0.5", StringComparison.Ordinal),
            valid.Replace("0.25", "1e999", StringComparison.Ordinal),
            valid.Replace("\"Dark\"", "\"Unknown\"", StringComparison.Ordinal),
            valid.Replace("\"Dark\"", "42", StringComparison.Ordinal)
        })
        {
            var document = Document("invalid-settings");
            using var parsed = JsonDocument.Parse(state);
            document.Kittens[0].State = parsed.RootElement.Clone();
            Session.Profiles.Import(document, Session.Warn);
            Check(Session.Profiles.PreservedCount == 1
                && Session.Profiles.GetOrCreate(Identity("invalid-settings"), Profile(), Session.Warn) is null,
                "invalid numeric or enum value is retained without applying");
        }
        var excessive = Document("too-many");
        excessive.Kittens = Enumerable.Repeat(excessive.Kittens[0], KittenSaveStorage.MaximumKittens + 1).ToList();
        Throws<InvalidDataException>(() => KittenSaveStorage.Validate(excessive), "excessive entry count rejected");
    }

    private static void FailedWrites()
    {
        Seed(); var save = FileSave("write-failed", sidecar: false); save.Populate(); save.FailWrite = true;
        Check(!save.Write() && !HasSidecar(save), "false native write publishes nothing");
        save.FailWrite = false; Check(save.Write() && !HasSidecar(save), "false write consumed capture");
        save.Populate(); save.ThrowWrite = true;
        Throws<HostFailure>(() => save.Write(), "native write exception remains visible");
        save.ThrowWrite = false; Check(save.Write() && !HasSidecar(save), "throwing write consumed capture");
        save.Populate(); Check(save.Write() && HasSidecar(save), "fresh capture recovers after failed writes");
        var survivor = FileSave("intact-previous-save");
        string previous = File.ReadAllText(Path.Combine(survivor.Directory.FullName, KittenSaveStorage.FileName));
        survivor.Populate(); survivor.FailWrite = true;
        Check(!survivor.Write() && File.ReadAllText(Path.Combine(survivor.Directory.FullName, KittenSaveStorage.FileName)) == previous,
            "failed native replacement leaves surviving old sidecar untouched");
    }

    private static void SidecarFailure()
    {
        Seed(); var save = FileSave("sidecar-failure", sidecar: false); save.Populate(); save.BlockSidecarPublication = true;
        Check(save.Write(), "a sidecar failure cannot change native true return value");
        Check(File.Exists(Path.Combine(save.Directory.FullName, "universe.xml")) && !HasSidecar(save)
            && Session.LastWarning is { Length: > 0 }, "native output survives and sidecar failure is reported");
        Check(Directory.GetFiles(save.Directory.FullName, "*.tmp").Length == 0, "failed publication removes temporary sidecar");
        save.Load(); ExpectNames();
        Check(!Session.Suspended, "native-only save remains loadable after failed sidecar publication");
    }

    private static void SkippedWrite()
    {
        Seed(); var save = FileSave("write-skipped", sidecar: false); save.Populate(); OtherSaveHooks.SkipWrite = true;
        Check(save.Write() && !HasSidecar(save), "skipped original with true result does not publish sidecar");
        OtherSaveHooks.SkipWrite = false;
        Check(save.Write() && !HasSidecar(save), "skipped write consumed prior capture");
    }

    private static void FailedPopulate()
    {
        Seed(); var save = FileSave("populate-failed", sidecar: false); save.Populate(); save.FailPopulate = true;
        Throws<HostFailure>(save.Populate, "native Populate exception remains visible");
        save.FailPopulate = false; Check(save.Write() && !HasSidecar(save), "failed Populate invalidates earlier capture");
        save.Populate(); OtherSaveHooks.SkipPopulate = true; save.Populate(); OtherSaveHooks.SkipPopulate = false;
        Check(save.Write() && !HasSidecar(save), "skipped Populate invalidates earlier capture");
    }

    private static void FailedChoiceCapture()
    {
        Seed(); var save = FileSave("choice-capture", sidecar: false); save.Populate();
        Runtime.FailCapture = true; save.Populate();
        Check(save.Write() && !HasSidecar(save), "failed profile capture cannot use an earlier capture");
        Check(Session.Status.Contains("matching", StringComparison.OrdinalIgnoreCase), "missing capture reported");
    }

    private static void NormalLoad()
    {
        Seed(); var save = FileSave("loaded"); var oldData = save.UniverseData;
        save.Load(); ExpectNames("loaded");
        Check(!ReferenceEquals(oldData, save.UniverseData) && ReferenceEquals(Universe.CurrentData, save.UniverseData), "load pairs against newly parsed UniverseData");
        Check(!Session.Suspended && Runtime.ResetCount == 2, "reconstruction and OnGameLoaded cleanup leave durable profiles available");
        Session.ObserveSystem(Universe.CurrentSystem); ExpectNames("loaded");
        save.Load(); ExpectNames("loaded");
        Check(Runtime.ResetCount == 4, "repeated load cleans bindings without duplicating or deleting logical profiles");
    }

    private static void DeferredLoad()
    {
        Seed(); var superseded = FileSave("superseded"); var latest = FileSave("latest");
        OtherSaveHooks.DeferLoad = true; superseded.Load(); latest.Load();
        ExpectNames("old"); Check(Runtime.ResetCount == 0 && superseded.NativeLoads == 0 && latest.NativeLoads == 0, "held request neither reads nor cleans scene");
        OtherSaveHooks.Replay(); ExpectNames("latest");
        Check(superseded.NativeLoads == 0 && latest.NativeLoads == 1 && !Session.Suspended, "only executed latest request commits");
        OtherSaveHooks.DeferLoad = false; OtherSaveHooks.SkipLoad = true; superseded.Load(); ExpectNames("latest");
    }

    private static void EarlyLoadFailure()
    {
        Seed(); var save = FileSave("unreadable"); save.FailRead = true; save.Load();
        ExpectNames("old"); Check(Runtime.BindingsAlive && !Session.Suspended && Runtime.ResetCount == 0, "unreadable file preserves current effects");
        save.FailRead = false; KSA.Program.IsEditorOpen = true; save.Load(); KSA.Program.IsEditorOpen = false;
        ExpectNames("old"); Check(Runtime.ResetCount == 0, "editor refusal does not reset scene");
        OtherSaveHooks.ThrowBeforeLoad = true;
        Throws<HostFailure>(save.Load, "earlier prefix failure survives default observer state");
        OtherSaveHooks.ThrowBeforeLoad = false;
        Universe.DeserializeSave(new("direct-after-failures")); ExpectNames();
        Check(!Session.Suspended, "failed file calls leave no stale context for later direct deserialization");
    }

    private static void ReconstructionFailure()
    {
        Seed(); var save = FileSave("partial"); save.ReadData = () => new("partial") { FailReconstruction = true };
        Throws<HostFailure>(save.Load, "native reconstruction exception survives observers");
        ExpectNames("partial"); Check(Session.Suspended && !Runtime.BindingsAlive, "incoming settings quarantined while old native world is gone");
        Session.ObserveSystem(Universe.CurrentSystem); Check(Session.Suspended, "same-system observation cannot resume partial reconstruction");
        FileSave("recovered").Load(); ExpectNames("recovered"); Check(!Session.Suspended, "a complete load resumes profiles");
    }

    private static void TrailingLoadFailure()
    {
        Seed(); var save = FileSave("completed"); KSA.Program.ThrowOnGameLoaded = true;
        Throws<HostFailure>(save.Load, "trailing native failure survives");
        ExpectNames("completed"); Check(!Session.Suspended, "completed reconstruction owns profiles despite trailing error");
    }

    private static void NestedFileLoad()
    {
        Seed(); var outer = FileSave("outer"); var inner = FileSave("inner");
        KSA.Program.DuringGameLoaded = () => { KSA.Program.DuringGameLoaded = null; inner.Load(); };
        outer.Load(); ExpectNames("inner");
        Check(Universe.CurrentData?.Label == "loaded-inner", "nested load owns native world as well as profiles");
        Universe.DeserializeSave(new("unrelated")); ExpectNames();
    }

    private static void NestedUnreadableLoad()
    {
        Seed(); var outer = FileSave("outer-good"); var inner = FileSave("inner-bad"); inner.FailRead = true;
        KSA.Program.DuringGameLoaded = () => { KSA.Program.DuringGameLoaded = null; inner.Load(); };
        outer.Load(); ExpectNames("outer-good");
        Check(!Session.Suspended && Universe.CurrentData?.Label == "loaded-outer-good", "failed nested read does not invalidate completed outer scene");
    }

    private static void NestedDirectOperation()
    {
        Seed(); var outer = FileSave("outer-direct");
        outer.ReadData = () => new("outer-direct") { DuringReconstruction = () => Universe.DeserializeSave(new("nested-direct")) };
        outer.Load(); ExpectNames();
        Check(Universe.CurrentData?.Label == "nested-direct", "nested direct reconstruction wins and cannot borrow outer sidecar");
        var second = FileSave("outer-system");
        KSA.Program.DuringGameLoaded = () => { KSA.Program.DuringGameLoaded = null; Universe.LoadSystem("other"); };
        second.Load(); ExpectNames();
        Check(Universe.CurrentSystem?.Id == "other", "nested new system invalidates outer file profiles");
    }

    private static void DeferredDirectLoad()
    {
        Seed(); OtherSaveHooks.DeferDeserialize = true; Universe.DeserializeSave(new("held-direct"));
        ExpectNames("old"); Check(Runtime.ResetCount == 0, "deferred direct call leaves old bindings alive");
        OtherSaveHooks.Replay(); ExpectNames(); Check(Runtime.ResetCount == 1, "actual direct replay cleans exactly once");
        Seed("kept"); OtherSaveHooks.DeferDeserialize = false; OtherSaveHooks.SkipDeserialize = true;
        Universe.DeserializeSave(new("skipped-direct")); ExpectNames("kept");
    }

    private static void DirectLoad()
    {
        Seed(); Universe.DeserializeSave(new("direct")); ExpectNames();
        Check(!Session.Suspended && !HostTrace.Events.Contains("game loaded"), "direct reconstruction does not require file-load notification");
        Session.ObserveSystem(Universe.CurrentSystem); ExpectNames();
    }

    private static void SystemChanges()
    {
        Seed(); Throws<HostFailure>(() => Universe.LoadSystem("invalid"), "invalid system preserves native error"); ExpectNames("old");
        Check(Runtime.ResetCount == 0, "unknown system does not release bindings");
        OtherSaveHooks.SkipSystem = true; Universe.LoadSystem("valid"); OtherSaveHooks.SkipSystem = false; ExpectNames("old");
        Universe.LoadSystem("valid"); ExpectNames();
        Check(!Session.Suspended && !HostTrace.Events.Contains("game loaded"), "new system commits empty choices without OnGameLoaded");
        Seed("new-scene"); SystemLibrary.FailInitialization = true;
        Throws<HostFailure>(() => Universe.LoadSystem("other"), "partial system initialization error survives");
        ExpectNames(); Check(Session.Suspended && Universe.CurrentSystem?.Id == "other", "changed native system never inherits previous scene profiles");
    }

    private static void JoinFailure()
    {
        Seed(); var save = FileSave("join-failed"); JobSystems.FailWait = true;
        Throws<HostFailure>(save.Load, "worker join error survives");
        ExpectNames("old"); Check(Runtime.ResetCount == 0 && !Session.Suspended, "worker failure aborts before cleanup and profile transition");
        JobSystems.FailWait = false; save.Load(); ExpectNames("join-failed");
    }

    private static void LateVeto()
    {
        Seed(); var save = FileSave("late-veto");
        var oldSystem = Universe.CurrentSystem;
        var oldData = Universe.CurrentData = new("unchanged-world");
        var veto = new Harmony("Purr2D2.Persistence.LateVeto.Checks");
        var prefix = new HarmonyMethod(typeof(SaveChecks), nameof(Veto)) { priority = Priority.Last - 200 };
        veto.Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DeserializeSave)), prefix: prefix);
        veto.Patch(AccessTools.Method(typeof(Universe), nameof(Universe.LoadSystem)), prefix: prefix);
        try
        {
            save.Load(); ExpectNames("old");
            Check(ReferenceEquals(Universe.CurrentData, oldData) && !Session.Suspended,
                "late-skipped original cannot commit its file candidate or quarantine the unchanged world");
            Universe.LoadSystem("valid"); ExpectNames("old");
            Check(ReferenceEquals(Universe.CurrentSystem, oldSystem) && !Session.Suspended,
                "late-skipped system change preserves previous scene profiles");
        }
        finally { veto.UnpatchAll(veto.Id); }
        save.Load(); ExpectNames("late-veto");
    }

    private static bool Veto() => false;

    private static void QuarantineVeto()
    {
        var save = FileSave("quarantined");
        save.ReadData = () => new("quarantined") { FailReconstruction = true };
        Throws<HostFailure>(save.Load, "fixture leaves a partially reconstructed scene");
        Check(Session.Suspended, "partially reconstructed scene starts quarantined");
        var veto = new Harmony("Purr2D2.Persistence.QuarantineVeto.Checks");
        var prefix = new HarmonyMethod(typeof(SaveChecks), nameof(Veto)) { priority = Priority.Last - 200 };
        veto.Patch(AccessTools.Method(typeof(Universe), nameof(Universe.DeserializeSave)), prefix: prefix);
        veto.Patch(AccessTools.Method(typeof(Universe), nameof(Universe.LoadSystem)), prefix: prefix);
        try
        {
            Universe.DeserializeSave(new("skipped-while-quarantined"));
            ExpectNames("quarantined");
            Check(Session.Suspended, "nondestructive skipped deserialize preserves existing quarantine");
            Universe.LoadSystem("valid");
            Check(Session.Suspended, "nondestructive skipped system change preserves existing quarantine");
        }
        finally { veto.UnpatchAll(veto.Id); }
        FileSave("complete-after-quarantine").Load();
        ExpectNames("complete-after-quarantine"); Check(!Session.Suspended, "complete reconstruction releases quarantine");
    }

    private static void SessionGenerations()
    {
        var session = new KittenSaveSession(); object system = new(); session.ObserveSystem(system);
        object firstData = new(), secondData = new();
        string firstDirectory = DirectoryFor("first-generation"), secondDirectory = DirectoryFor("second-generation");
        KittenSaveStorage.Write(firstDirectory, Document("first-generation"));
        KittenSaveStorage.Write(secondDirectory, Document("second-generation"));
        var firstLoad = session.BeginFileLoad(new(), () => firstData, firstDirectory);
        var first = session.BeginReconstruction(firstData, system, fileAllowed: true);
        var secondLoad = session.BeginFileLoad(new(), () => secondData, secondDirectory);
        var second = session.BeginReconstruction(secondData, system, fileAllowed: true);
        session.FinishReconstruction(second, system, null, systemChange: false); session.EndFileLoad(secondLoad);
        session.FinishReconstruction(first, system, null, systemChange: false); session.EndFileLoad(firstLoad);
        Check(Names(session).SequenceEqual(["second-generation"]), "stale outer generation cannot overwrite latest completion");
        session.ObserveSystem(system); Check(Names(session).Length == 1, "first settled observation recognizes completed known scene");
        session.ObserveSystem(new object()); Check(Names(session).Length == 0, "spontaneous untracked system replacement clears old profiles");
        session.Clear(); Check(!session.Suspended && Names(session).Length == 0, "session disposal clears detached state");
    }

    private static void VanillaLoad()
    {
        Seed(); FileSave("vanilla", sidecar: false).Load(); ExpectNames();
        Seed(); var corrupt = FileSave("corrupt"); File.WriteAllText(Path.Combine(corrupt.Directory.FullName, KittenSaveStorage.FileName), "broken");
        corrupt.Load(); ExpectNames(); Check(!Session.Suspended, "bad sidecar does not block valid native load");
        Seed(); var future = FileSave("future-schema"); var document = Document("future-schema"); document.SchemaVersion = 99;
        File.WriteAllText(Path.Combine(future.Directory.FullName, KittenSaveStorage.FileName), JsonSerializer.Serialize(document, KittenSaveStorage.Options));
        future.Load(); ExpectNames();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        ++_assertions;
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { ++_assertions; return; }
        throw new InvalidOperationException(message + " (expected " + typeof(T).Name + ")");
    }
}
