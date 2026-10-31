using Brutal.Numerics;
using KSA;
using Purr2D2.Audio;
using Purr2D2.Core;

// Exercise real SaberAudio state transitions with only the game/mixer boundary replaced.
internal static class AudioChecks
{
    static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    static void Main()
    {
        using var audio = new SaberAudio();
        var owner = new KittenEva();
        void Tick(SaberType type = SaberType.Light, bool paused = false, float deployment = 1,
            float volume = 1, bool on = true) => audio.Update(owner, new double3(2, 3, 4),
                deployment, on, paused, volume, contact: 1, type: type);
        ChannelWrapper Latest(string suffix) => GameAudio.Channels.Last(c => c.Id == "Purr2D2Saber" + suffix);

        Tick();
        Check(!Latest("LightIgnite").Stopped, "Light-type BFL9K starts its own ignition recording");
        var light = Latest("LightStatic");
        var hum = Latest("Hum");
        var contact = Latest("Contact");
        Check(GameAudio.Channels.Count == 4 && !light.Stopped && !hum.Stopped && !contact.Stopped,
            "static layers alongside ignition, hum and contact");
        Check(light.VolumeMultiplier == .3f && hum.VolumeMultiplier == .42f
            && light.Spatial.Position == new double3(2, 3, 4),
            "Light ambient loops use the moving emitter and their unchanged configured gains");
        Tick();
        Check(GameAudio.Channels.Count == 4, "repeated updates retain the loop");
        Tick(SaberType.Dark);
        var dark = Latest("DarkStatic");
        Check(light.Stopped && !dark.Stopped && !hum.Stopped && !contact.Stopped
            && GameAudio.Channels.Count == 5
            && Math.Abs(dark.VolumeMultiplier / (.3f * .7f) - .7f) < 1e-6f
            && Math.Abs(hum.VolumeMultiplier / (.42f * .7f) - .7f) < 1e-6f
            && contact.VolumeMultiplier == .35f,
            "Dark hum and static each run at 70% of their previous gain while type changes preserve other loops");
        Tick(SaberType.Dark, paused: true);
        Check(dark.Paused && hum.Paused && GameAudio.Channels.Count == 5, "pause retains channels");
        Tick(paused: true);
        Check(dark.Stopped && GameAudio.Channels.Count == 5, "paused type change defers new playback");
        Tick(deployment: .5f, volume: .5f);
        light = Latest("LightStatic");
        Check(light.VolumeMultiplier == .075f && hum.VolumeMultiplier == .105f
            && contact.VolumeMultiplier == .0875f && !light.Paused,
            "Light hum, static and contact gains follow deployment and volume unchanged");
        audio.Suspend();
        Check(light.Paused, "stale pose suspends static");
        Tick();
        Check(!light.Paused, "fresh pose resumes static");
        Tick(volume: 0);
        Check(light.Stopped && hum.Stopped, "mute stops loops");
        Tick(SaberType.Dark);
        dark = Latest("DarkStatic");
        Tick(SaberType.Dark, deployment: 0, on: false);
        Check(dark.Stopped && !Latest("Retract").Stopped, "retraction ends static and keeps retract effect");
        Tick(SaberType.Dark);
        audio.Reset();
        Check(GameAudio.Channels.All(c => c.Stopped), "world reset stops every owned channel");
        int beforePausedIgnition = GameAudio.Channels.Count;
        Tick(SaberType.Dark, paused: true);
        Check(GameAudio.Channels.Count == beforePausedIgnition, "paused ignition starts no sounds");
        Tick(SaberType.Dark);
        Check(!Latest("DarkIgnite").Stopped, "Dark saber starts its own ignition recording on resume");
        Tick(on: false, deployment: 0);
        Check(Latest("DarkIgnite").Stopped && !Latest("Retract").Stopped,
            "reversal stops Dark ignition and plays the common close sound");
        Tick();
        Check(!Latest("LightIgnite").Stopped && Latest("Retract").Stopped,
            "reignition selects the current Light style and cuts the previous transition");
        audio.Shutdown();
        int count = GameAudio.Channels.Count;
        Tick();
        Check(GameAudio.Channels.All(c => c.Stopped) && GameAudio.Channels.Count == count,
            "shutdown stops playback permanently");

        ForceBreakupOneShots();
        ForceBreakupAudioGracefulFailures();
        SaberClashOneShot();
        SaberActionOneShots();
        ManualSwingOneShot();
        SaberSwooshAssetSelectionAndMissingCue();
        Console.WriteLine("PASS: audio layering, saber action one-shots, type switching, pause, mute and cleanup");
    }

    private static void SaberActionOneShots()
    {
        using var audio = new SaberAudio();
        var owner = new KittenEva();
        GameAudio.CameraPosition = new double3(10, 20, 30);
        audio.Update(owner, double3.Zero, 1, true, false, .8f, type: SaberType.Dark);
        var ignition = GameAudio.Channels.Last(c => c.Id == "Purr2D2SaberDarkIgnite");
        int before = GameAudio.Channels.Count;
        int lightIgnitionsBeforeSwoosh = GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberLightIgnite");
        audio.PlaySwoosh(owner, new double3(2, 3, 4), .8f);
        var swoosh = GameAudio.Channels[^1];
        audio.PlaySlice(owner, new double3(5, 6, 7), .8f);
        var slice = GameAudio.Channels[^1];
        Check(GameAudio.Channels.Count == before + 2 && swoosh.Id == "Purr2D2SomersaultSwoosh"
            && slice.Id == "Purr2D2ForceBreak" && !ignition.Stopped && !swoosh.Stopped
            && GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberLightIgnite") == lightIgnitionsBeforeSwoosh,
            "Dark saber uses the dedicated swoosh and Force breakup bang without starting Light ignition");
        Check(swoosh.VolumeMultiplier == .8f * .525f && slice.VolumeMultiplier == .8f
            && !swoosh.Paused && !slice.Paused && swoosh.Channel.DopplerLevel == 0 && slice.Channel.DopplerLevel == 0,
            "action sounds use their independent gains, global volume and disabled orbital Doppler");
        audio.Suspend();
        Check(swoosh.Paused && slice.Paused, "a stale pose suspends action sounds");
        GameAudio.CameraPosition = new double3(20, 30, 40);
        audio.Update(owner, double3.Zero, 1, true, true, .4f, type: SaberType.Dark);
        Check(GameAudio.Channels.Count == before + 2 && swoosh.Paused && slice.Paused
            && swoosh.Spatial.Position == new double3(22, 33, 44)
            && slice.Spatial.Position == new double3(25, 36, 47)
            && swoosh.VolumeMultiplier == .4f * .525f && slice.VolumeMultiplier == .4f,
            "paused updates follow each captured emitter and apply changed volume without replaying events");
        audio.Update(owner, double3.Zero, 1, true, false, .4f, type: SaberType.Dark);
        Check(!swoosh.Paused && !slice.Paused, "resume retains the original action channels");
        audio.PlaySwoosh(owner, double3.Zero, .4f);
        var nextSwoosh = GameAudio.Channels[^1];
        audio.PlaySlice(owner, double3.Zero, .4f);
        var nextSlice = GameAudio.Channels[^1];
        Check(swoosh.Stopped && slice.Stopped && !nextSwoosh.Stopped && !nextSlice.Stopped && !ignition.Stopped,
            "new combo jumps and manual cuts replace only their own still-playing clips");
        before = GameAudio.Channels.Count;
        audio.PlaySwoosh(owner, double3.Zero, 0);
        audio.PlaySlice(owner, double3.Zero, float.NaN);
        audio.PlaySwoosh(owner, new double3(double.NaN, 0, 0), 1);
        Check(GameAudio.Channels.Count == before, "muted or invalid action events create no channels");
        nextSlice.Finished = true;
        audio.Update(owner, double3.Zero, 1, true, false, .4f, type: SaberType.Dark);
        Check(nextSlice.Stopped && !nextSwoosh.Stopped, "completed slice clips release independently");
        audio.Update(new KittenEva(), double3.Zero, 0, false, false, 0);
        Check(nextSwoosh.Stopped && ignition.Stopped, "owner changes stop all old action channels");
        audio.PlaySwoosh(owner, double3.Zero, 1);
        audio.PlaySlice(owner, double3.Zero, 1);
        var shutdownChannels = GameAudio.Channels.TakeLast(2).ToArray();
        audio.Shutdown();
        before = GameAudio.Channels.Count;
        audio.PlaySwoosh(owner, double3.Zero, 1);
        audio.PlaySlice(owner, double3.Zero, 1);
        Check(shutdownChannels.All(c => c.Stopped) && GameAudio.Channels.Count == before,
            "shutdown stops action sounds and rejects later events");

        ModLibrary.MissingIds.Add("Purr2D2SaberHum");
        using var withoutHum = new SaberAudio();
        withoutHum.PlaySlice(owner, double3.Zero, 1);
        var standaloneSlice = GameAudio.Channels[^1];
        withoutHum.Update(owner, double3.Zero, 0, false, true, .5f);
        Check(standaloneSlice.Paused && standaloneSlice.VolumeMultiplier == .5f,
            "action channels still follow and pause when an unrelated ambient asset is unavailable");
        ModLibrary.MissingIds.Clear();
        GameAudio.CameraPosition = double3.Zero;
    }

    private static void SaberSwooshAssetSelectionAndMissingCue()
    {
        var owner = new KittenEva();
        foreach (SaberType type in new[] { SaberType.Light, SaberType.Dark })
        {
            using var audio = new SaberAudio();
            audio.Update(owner, double3.Zero, 0, targetOn: false, paused: false, volume: .8f, type: type);
            int before = GameAudio.Channels.Count;
            int lightIgnitionsBefore = GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberLightIgnite");
            audio.PlaySwoosh(owner, new double3(1, 2, 3), .8f);
            var swoosh = GameAudio.Channels[^1];
            Check(GameAudio.Channels.Count == before + 1 && swoosh.Id == "Purr2D2SomersaultSwoosh"
                && !swoosh.Stopped && swoosh.VolumeMultiplier == .8f * .525f
                && GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberLightIgnite") == lightIgnitionsBefore,
                $"{type} saber uses the dedicated swoosh without starting Light ignition");
        }

        ModLibrary.MissingIds.Add("Purr2D2SomersaultSwoosh");
        try
        {
            using var missingSwoosh = new SaberAudio();
            int before = GameAudio.Channels.Count;
            int lightIgnitionsBefore = GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberLightIgnite");
            missingSwoosh.PlaySwoosh(owner, double3.Zero, 1);
            Check(GameAudio.Channels.Count == before
                && GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberLightIgnite") == lightIgnitionsBefore,
                "a missing swoosh asset fails closed without falling back to Light ignition");

            missingSwoosh.Update(owner, double3.Zero, 0, targetOn: true, paused: false, volume: .5f,
                type: SaberType.Light);
            var ignition = GameAudio.Channels[^1];
            Check(GameAudio.Channels.Count == before + 1 && ignition.Id == "Purr2D2SaberLightIgnite"
                && !ignition.Stopped,
                "actual Light ignition remains available when the dedicated swoosh asset is missing");
            missingSwoosh.PlaySwoosh(owner, double3.Zero, 1);
            Check(GameAudio.Channels.Count == before + 1 && !ignition.Stopped,
                "missing swoosh playback neither adds a fallback channel nor cuts actual ignition");
        }
        finally { ModLibrary.MissingIds.Remove("Purr2D2SomersaultSwoosh"); }
    }

    private static void ManualSwingOneShot()
    {
        var owner = new KittenEva();
        GameAudio.CameraPosition = new double3(10, 20, 30);
        GameAudio.CameraVelocity = new double3(1, 2, 3);
        using (var audio = new SaberAudio())
        {
            audio.Update(owner, double3.Zero, 0, targetOn: true, paused: false, volume: .8f);
            var ignition = GameAudio.Channels[^1];
            audio.PlaySwoosh(owner, new double3(1, 1, 1), .8f);
            var somersault = GameAudio.Channels[^1];
            audio.PlayClash(owner, new double3(3, 4, 5), .8f);
            var clash = GameAudio.Channels[^1];
            audio.PlaySlice(owner, new double3(5, 6, 7), .8f);
            var slice = GameAudio.Channels[^1];
            int beforeSwing = GameAudio.Channels.Count;
            audio.PlayManualSwing(owner, new double3(2, 3, 4), .8f);
            var swing = GameAudio.Channels[^1];

            Check(GameAudio.Channels.Count == beforeSwing + 1 && swing.Id == "Purr2D2SaberSwing"
                && !swing.Stopped && !swing.Paused && swing.VolumeMultiplier == .8f * .525f
                && swing.Spatial.Position == new double3(12, 23, 34)
                && swing.Spatial.Velocity == GameAudio.CameraVelocity && swing.Channel.DopplerLevel == 0,
                "manual swing uses its own spatial one-shot at the captured point and 52.5 percent gain");
            Check(!ignition.Stopped && !somersault.Stopped && !clash.Stopped && !slice.Stopped,
                "manual swing leaves ignition, somersault, clash and slice channels independent");

            audio.PlayManualSwing(owner, new double3(6, 7, 8), .6f);
            var nextSwing = GameAudio.Channels[^1];
            Check(swing.Stopped && nextSwing.Id == "Purr2D2SaberSwing" && !nextSwing.Stopped
                && !ignition.Stopped && !somersault.Stopped && !clash.Stopped && !slice.Stopped,
                "a later manual swing replaces only the previous manual swing channel");

            audio.Suspend();
            Check(nextSwing.Paused, "suspending the saber pauses the manual swing");
            GameAudio.CameraPosition = new double3(20, 30, 40);
            audio.Update(owner, double3.Zero, 0, targetOn: true, paused: true, volume: .4f);
            Check(nextSwing.Paused && nextSwing.Spatial.Position == new double3(26, 37, 48)
                && nextSwing.VolumeMultiplier == .4f * .525f,
                "paused updates move the manual swing with its owner and apply current volume");
            audio.Update(owner, double3.Zero, 0, targetOn: true, paused: false, volume: .4f);
            Check(!nextSwing.Paused, "a fresh pose resumes the manual swing");

            int beforeInvalid = GameAudio.Channels.Count;
            audio.PlayManualSwing(owner, double3.Zero, 0);
            audio.PlayManualSwing(owner, new double3(double.NaN, 0, 0), 1);
            audio.PlayManualSwing(owner, double3.Zero, float.NaN);
            Check(GameAudio.Channels.Count == beforeInvalid && !nextSwing.Stopped,
                "muted or invalid manual swing input does not create or replace a channel");

            audio.Reset();
            Check(nextSwing.Stopped && ignition.Stopped && somersault.Stopped && clash.Stopped && slice.Stopped,
                "reset stops the manual swing and other owned saber channels");
            audio.PlayManualSwing(owner, double3.Zero, 1);
            var shutdownSwing = GameAudio.Channels[^1];
            audio.Shutdown();
            int afterShutdown = GameAudio.Channels.Count;
            audio.PlayManualSwing(owner, double3.Zero, 1);
            Check(shutdownSwing.Stopped && GameAudio.Channels.Count == afterShutdown,
                "shutdown stops manual swing and prevents later playback");
        }

        ModLibrary.MissingIds.Add("Purr2D2SaberSwing");
        try
        {
            using var missingSwing = new SaberAudio();
            int beforeMissing = GameAudio.Channels.Count;
            missingSwing.PlayManualSwing(owner, double3.Zero, 1);
            Check(GameAudio.Channels.Count == beforeMissing,
                "a missing manual swing cue fails closed without starting another sound");
            missingSwing.Update(owner, double3.Zero, 0, targetOn: true, paused: false, volume: .5f);
            var ignition = GameAudio.Channels[^1];
            missingSwing.PlayManualSwing(owner, double3.Zero, 1);
            Check(GameAudio.Channels.Count == beforeMissing + 1 && ignition.Id == "Purr2D2SaberLightIgnite"
                && !ignition.Stopped,
                "missing manual swing never falls back to ignition, which remains independently available");
        }
        finally { ModLibrary.MissingIds.Remove("Purr2D2SaberSwing"); }

        ModLibrary.MissingIds.Add("Purr2D2SaberHum");
        try
        {
            using var withoutHum = new SaberAudio();
            withoutHum.PlayManualSwing(owner, new double3(2, 3, 4), .8f);
            var swingWithoutHum = GameAudio.Channels[^1];
            GameAudio.CameraPosition = new double3(30, 40, 50);
            withoutHum.Update(owner, double3.Zero, 0, targetOn: false, paused: true, volume: .4f);
            Check(swingWithoutHum.Id == "Purr2D2SaberSwing" && swingWithoutHum.Paused
                && swingWithoutHum.Spatial.Position == new double3(32, 43, 54)
                && swingWithoutHum.VolumeMultiplier == .4f * .525f,
                "an active manual swing still follows, pauses and updates gain when the hum asset is missing");
        }
        finally { ModLibrary.MissingIds.Remove("Purr2D2SaberHum"); }
        GameAudio.CameraPosition = double3.Zero;
        GameAudio.CameraVelocity = double3.Zero;
    }

    private static void SaberClashOneShot()
    {
        int firstChannel = GameAudio.Channels.Count;
        int darkIgnitionsBefore = GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberDarkIgnite");
        var owner = new KittenEva();
        GameAudio.CameraPosition = new double3(10, 20, 30);
        using var audio = new SaberAudio();
        audio.PlayClash(owner, new double3(2, 3, 4), .5f);
        Check(GameAudio.Channels.Count == firstChannel + 1
            && GameAudio.Channels.Count(c => c.Id == "Purr2D2SaberDarkIgnite") == darkIgnitionsBefore,
            "a clash starts exactly one dedicated sound without starting Dark ignition");
        var first = GameAudio.Channels[^1];
        Check(first.Id == "Purr2D2SaberClash" && !first.Paused && first.VolumeMultiplier == .4f
            && first.Spatial.Position == new double3(12, 23, 34),
            "clash configures its contact point, gain and playback before unpausing");

        audio.Update(owner, double3.Zero, 0, targetOn: true, paused: false, volume: .25f, type: SaberType.Dark);
        var darkIgnition = GameAudio.Channels[^1];
        Check(darkIgnition.Id == "Purr2D2SaberDarkIgnite" && !darkIgnition.Stopped && !first.Stopped,
            "actual Dark ignition plays on its own channel while a clash continues");

        audio.Update(owner, double3.Zero, 0, targetOn: true, paused: true, volume: .25f, type: SaberType.Dark);
        Check(first.Paused && first.VolumeMultiplier == .2f
            && first.Spatial.Position == new double3(12, 23, 34),
            "the hit follows its contact offset and pauses independently of the blade emitter");

        audio.PlayClash(owner, new double3(3, 4, 5), .5f);
        var second = GameAudio.Channels[^1];
        Check(first.Stopped && second.Id == "Purr2D2SaberClash" && !second.Paused
            && second.Spatial.Position == new double3(13, 24, 35) && !darkIgnition.Stopped,
            "a new clash replaces only the previous clash and leaves actual Dark ignition alone");
        audio.Reset();
        Check(second.Stopped && darkIgnition.Stopped, "reset cleans up the separate clash and ignition channels");

        int beforeMissingAsset = GameAudio.Channels.Count;
        ModLibrary.MissingIds.Add("Purr2D2SaberClash");
        using (var missingClash = new SaberAudio())
        {
            missingClash.PlayClash(owner, double3.Zero, 1);
            Check(GameAudio.Channels.Count == beforeMissingAsset,
                "a missing clash asset fails closed instead of falling back to Dark ignition");
            missingClash.Update(owner, double3.Zero, 0, targetOn: true, paused: false, volume: .5f,
                type: SaberType.Light);
            Check(GameAudio.Channels.Count == beforeMissingAsset + 1
                && GameAudio.Channels[^1].Id == "Purr2D2SaberLightIgnite",
                "other saber audio remains available when the clash asset is missing");
        }
        ModLibrary.MissingIds.Clear();
    }

    private static void ForceBreakupOneShots()
    {
        int firstChannel = GameAudio.Channels.Count;
        var owner = new KittenEva();
        using var audio = new ForceBreakupAudio();
        GameAudio.CameraPosition = new double3(10, 20, 30);
        GameAudio.CameraVelocity = new double3(100, 200, 300);

        audio.Initialize();
        audio.PlayGroan(owner, new double3(2, 3, 4), .8f);
        Check(GameAudio.Channels.Count == firstChannel + 1, "stage one starts exactly one creak one-shot");
        Check(audio.HasActiveChannels, "active groan is exposed to the runtime poller");
        var groan = GameAudio.Channels[^1];
        Check(groan.Id == "Purr2D2ForceCreak" && !groan.Stopped && !groan.Paused,
            "stage one maps to the dedicated force creak sound");
        Check(groan.VolumeMultiplier == .8f * .7f && groan.Spatial.Position == new double3(12, 23, 34)
            && groan.Spatial.Velocity == GameAudio.CameraVelocity && groan.Channel.DopplerLevel == 0,
            "creak uses the body-frame emitter, 70 percent gain, global volume and zero Doppler");

        audio.Update(owner, new double3(3, 4, 5), .8f);
        audio.Update(owner, new double3(4, 5, 6), .8f);
        Check(GameAudio.Channels.Count == firstChannel + 1 && groan.Spatial.Position == new double3(14, 25, 36),
            "repeated updates only move the one-shot and never replay it");

        audio.PlayBreak(owner, new double3(5, 6, 7), .6f);
        Check(GameAudio.Channels.Count == firstChannel + 2, "break starts a separate one-shot channel");
        var breakup = GameAudio.Channels[^1];
        Check(breakup.Id == "Purr2D2ForceBreak" && !breakup.Stopped && !groan.Stopped,
            "stage two maps to the dedicated force bang and overlaps the creak");
        Check(breakup.VolumeMultiplier == .6f,
            "bang keeps its natural gain while respecting the global volume");
        Check(audio.HasActiveChannels, "overlapping one-shots keep the runtime poller active");

        audio.Update(owner, new double3(7, 8, 9), .4f, paused: true);
        Check(groan.Paused && breakup.Paused
            && groan.Spatial.Position == new double3(17, 28, 39)
            && breakup.Spatial.Position == new double3(17, 28, 39)
            && groan.VolumeMultiplier == .4f * .7f && breakup.VolumeMultiplier == .4f,
            "pause retains and spatially follows both channels with their separate stage gains");
        audio.Update(owner, new double3(8, 9, 10), 0, paused: false);
        Check(!groan.Paused && !breakup.Paused && groan.VolumeMultiplier == 0 && breakup.VolumeMultiplier == 0,
            "resume follows each stage's gain and the saved global volume, including mute");
        int beforeMutedPlay = GameAudio.Channels.Count;
        audio.PlayGroan(owner, new double3(0, 0, 0), 0);
        audio.PlayBreak(owner, new double3(0, 0, 0), 0);
        Check(GameAudio.Channels.Count == beforeMutedPlay, "muted events do not create new channels");
        breakup.Finished = true;
        audio.Update(owner, new double3(8, 9, 10), 0);
        Check(breakup.Stopped && !groan.Stopped && audio.HasActiveChannels,
            "a finished one-shot releases without cutting its overlapping channel");
        groan.Finished = true;
        audio.Update(owner, new double3(8, 9, 10), 0);
        Check(groan.Stopped && !audio.HasActiveChannels,
            "the runtime poller can stop after every one-shot has finished");

        audio.Reset();
        Check(groan.Stopped && breakup.Stopped && !audio.HasActiveChannels, "reset stops every owned one-shot");
        audio.PlayGroan(owner, new double3(1, 2, 3), 1);
        var staleOwnerChannel = GameAudio.Channels[^1];
        owner.IsDisposed = true;
        audio.Update(owner, new double3(1, 2, 3), 1);
        Check(staleOwnerChannel.Stopped, "disposed owner releases its one-shot");
        owner.IsDisposed = false;

        audio.PlayBreak(owner, new double3(1, 2, 3), 1);
        var shutdownChannel = GameAudio.Channels[^1];
        audio.Shutdown();
        int afterShutdown = GameAudio.Channels.Count;
        audio.PlayGroan(owner, new double3(1, 2, 3), 1);
        audio.Update(owner, new double3(1, 2, 3), 1);
        Check(shutdownChannel.Stopped && !audio.HasActiveChannels && GameAudio.Channels.Count == afterShutdown,
            "shutdown stops channels and prevents later playback");
    }

    private static void ForceBreakupAudioGracefulFailures()
    {
        int firstChannel = GameAudio.Channels.Count;
        var owner = new KittenEva();
        GameAudio.System.Null = true;
        using (var unavailable = new ForceBreakupAudio())
        {
            unavailable.Initialize();
            unavailable.PlayGroan(owner, double3.Zero, 1);
            unavailable.Update(owner, double3.Zero, 1);
            unavailable.Shutdown();
        }
        GameAudio.System.Null = false;
        Check(GameAudio.Channels.Count == firstChannel, "missing audio system fails closed without throwing");

        ModLibrary.MissingIds.Add("Purr2D2ForceCreak");
        using (var missingAsset = new ForceBreakupAudio())
        {
            missingAsset.Initialize();
            missingAsset.PlayGroan(owner, double3.Zero, 1);
            missingAsset.Update(owner, double3.Zero, 1);
            missingAsset.Shutdown();
        }
        ModLibrary.MissingIds.Clear();
        Check(GameAudio.Channels.Count == firstChannel, "missing sound asset fails closed without throwing");

        GameAudio.ReturnNullChannel = true;
        using (var missingChannel = new ForceBreakupAudio())
        {
            missingChannel.Initialize();
            missingChannel.PlayGroan(owner, double3.Zero, 1);
            missingChannel.Update(owner, double3.Zero, 1);
            missingChannel.Shutdown();
        }
        GameAudio.ReturnNullChannel = false;
        Check(GameAudio.Channels.Count == firstChannel, "unavailable mixer channel fails closed without throwing");
    }
}

namespace Brutal.FmodApi
{
    public sealed class TestChannel
    {
        public float DopplerLevel;
        public float Spread;
        public void TrySet3dDopplerLevel(float value) => DopplerLevel = value;
        public void TrySet3dSpread(float value) => Spread = value;
    }
}

namespace KSA
{
    public static class Double3Ex
    {
        public static double3 Transform(this double3 value, doubleQuat rotation) => double3.Transform(value, rotation);
    }
    public sealed class KittenEva
    {
        public bool IsDisposed { get; set; }
        public doubleQuat Body2Cce => doubleQuat.Identity;
    }
    public sealed class Camera
    {
        public double3 GetPositionEgo(KittenEva owner) => GameAudio.CameraPosition;
        public double3 GetVelocityEgo(KittenEva owner) => GameAudio.CameraVelocity;
    }
    public readonly record struct SpatialAudio(double3 Position, double3 Velocity, double Density);
    public sealed record SoundBehavior(string Id);
    public static class ModLibrary
    {
        public static readonly HashSet<string> MissingIds = new();
        public static bool TryGet<T>(string id, out T? sound) where T : class
        { sound = MissingIds.Contains(id) ? null : new SoundBehavior(id) as T; return sound is not null; }
    }
    public interface IChannel
    {
        float VolumeMultiplier { get; set; }
        float PitchMultiplier { get; set; }
        bool IsPlaying();
        bool IsVirtual();
        void SetPaused(bool paused);
        void SetSpatialAudio(SpatialAudio spatial);
        void UpdateVolume();
        void UpdatePitch();
        void Stop(bool stopImmediate);
    }
    public sealed class ChannelWrapper(string id) : IChannel
    {
        public readonly string Id = id;
        public readonly Brutal.FmodApi.TestChannel Channel = new();
        public bool Paused, Stopped, Finished;
        public SpatialAudio Spatial;
        public float VolumeMultiplier { get; set; }
        public float PitchMultiplier { get; set; }
        public bool IsPlaying() => !Stopped && !Finished;
        public bool IsVirtual() => false;
        public void SetPaused(bool paused) => Paused = paused;
        public void SetSpatialAudio(SpatialAudio spatial) => Spatial = spatial;
        public void UpdateVolume() { }
        public void UpdatePitch() { }
        public void Stop(bool stopImmediate) => Stopped = true;
    }
    public static class GameAudio
    {
        public sealed class TestSystem { public bool Null; public bool IsNull() => Null; }
        public static readonly TestSystem System = new();
        public static readonly List<ChannelWrapper> Channels = new();
        public static bool ReturnNullChannel;
        public static double3 CameraPosition;
        public static double3 CameraVelocity;
        public static Camera GetAudioCamera() => new();
        public static void PlaySound(SoundBehavior sound, SpatialAudio spatial, out IChannel? channel,
            float volume, bool startPaused)
        {
            if (ReturnNullChannel) { channel = null; return; }
            var created = new ChannelWrapper(sound.Id) { Spatial = spatial, VolumeMultiplier = volume, Paused = startPaused };
            Channels.Add(created);
            channel = created;
        }
    }
}
