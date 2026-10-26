using KSA;
using Brutal.Numerics;
using Brutal.GlfwApi;
using Purr2D2.Core;
using RenderCore.Input;

// This fixture links the actual runtime input partial and motion controller.
// Only pose/slice boundaries are replaced; native worker preparation/apply is
// the shared motion fixture, including its real Harmony handoff.
namespace Purr2D2.Pose
{
    internal sealed class SomersaultPoseController
    {
        internal bool Eligible { get; private set; }
        internal string Status => "Fixture pose.";
        internal void Bind(KittenEva owner) => Eligible = true;
        internal void Unbind() => Eligible = false;
        internal void SetMotion(SomersaultDirection direction, float angle, float tuck, bool active) { }
    }
}

namespace Purr2D2.Runtime
{
    internal sealed partial class SaberRuntime
    {
        private bool _ready = true, _disposed = false, _selectingSlice = false, _enabled = true;
        private string? _fault = null;
        private KittenEva? _owner;
        private readonly TestSettings _settings = new();
        private sealed class TestSettings
        {
            internal float JumpComboIntervalSeconds = .75f;
            internal bool SpaceSomersaultEnabled = true;
            internal float Volume = .6f;
        }
        private readonly FixtureBlade _blade = new();
        private readonly FixtureAudio _audio = new();
        private float Length => 1;
        private sealed class FixtureBlade { internal bool Equipped = true, TargetOn = true; internal float Deployment = 1; }
        private sealed class FixtureAudio
        {
            internal int Swooshes;
            internal void PlaySwoosh(KittenEva owner, double3 emitter, float volume) => Swooshes++;
        }
        private readonly FixturePose _pose = new();
        private readonly FixtureSlice _sliceSequence = new();
        private sealed class FixturePose
        {
            internal bool Swinging => false;
            internal bool TryGetBladeSegment(double length, out double3 emitter, out double3 tip)
            { emitter = double3.Zero; tip = double3.UnitY; return true; }
        }
        private sealed class FixtureSlice { internal bool Active => false; internal bool EndPresented => false; }
        private void CancelAssistedSlice(string reason) => _selectingSlice = false;
        private SaberRuntime(SomersaultMotionController motion) => _somersaultMotion = motion;

        internal static void RunSomersaultInputChecks(SomersaultMotionController motion, KittenEva owner)
        {
            var runtime = new SaberRuntime(motion) { _owner = owner };
            runtime.UpdateSomersault();
            runtime._settings.JumpComboIntervalSeconds = .1f;
            runtime.UpdateSomersault();
            Check(Math.Abs(motion.ComboIntervalSeconds - .1) < 1e-6,
                "the production settled runtime propagates a changed minimum combo interval to the controller");
            runtime._settings.JumpComboIntervalSeconds = .75f;
            runtime.UpdateSomersault();
            Program.PlayerTime = 10;
            var window = Program.GetWindow();
            window.SetKey(GlfwKey.Up, GlfwKeyAction.Press);
            Check(runtime.FilterSpaceSomersaultKey(owner,
                    new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
                && runtime._somersaultRequest is { Direction: SomersaultDirection.Forward, PressTime: 10, FromSpace: true },
                "the actual Space press captures the first grounded launch for the real worker controller");
            runtime.UpdateSomersault();
            Check(runtime._audio.Swooshes == 0 && motion.AppliedCommandId == 0,
                "accepting a somersault intent cannot play a swoosh before the native launch acknowledgement");
            var first = new SimStep(new(0), new(.02), .02);
            Universe.PrepareVehicleWorkers(first); Universe.Apply(first);
            Check(motion.Active && motion.RotationRadians == 0, "applied first launch awaits the next settled observation");

            // PollEvents happens after ApplyVehicleSolvers but before the mod's
            // settled update. The queue must observe the first launch itself.
            Program.PlayerTime = 10.5;
            window.SetKey(GlfwKey.Up, GlfwKeyAction.Release);
            window.SetKey(GlfwKey.Left, GlfwKeyAction.Press);
            Check(runtime.FilterSpaceSomersaultKey(owner,
                    new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
                && runtime._somersaultRequest is { PressTime: 10.5, Direction: SomersaultDirection.Left },
                "real input boundary accepts the exact-window second press before settled launch acknowledgement");
            Program.PlayerTime = 10.9;
            runtime.UpdateSomersault();
            Check(motion.CommandId == 2 && motion.Direction == SomersaultDirection.Forward,
                "low-frame-rate settled processing retains press time and original direction");
            Check(runtime._audio.Swooshes == 1 && motion.AppliedCommandId == 1,
                "the first acknowledged launch plays once even when input polling consumed its acknowledgement");
            runtime.UpdateSomersault();
            Check(runtime._audio.Swooshes == 1, "repeated settled observations cannot replay a somersault cue");
            var second = new SimStep(new(.02), new(.12), .1);
            Universe.PrepareVehicleWorkers(second); Universe.Apply(second);
            Program.PlayerTime = 11;
            window.SetKey(GlfwKey.Left, GlfwKeyAction.Release);
            window.SetKey(GlfwKey.Right, GlfwKeyAction.Press);
            Check(runtime.FilterSpaceSomersaultKey(owner,
                    new GlfwKeyEvent(window, GlfwKeyAction.Press, GlfwKey.Space))
                && runtime._somersaultRequest is { PressTime: 11, Direction: SomersaultDirection.Right },
                "real input boundary also observes an applied second boost before accepting the third press");
            runtime.UpdateSomersault();
            Check(motion.CommandId == 3, "input-to-worker integration accepts three deliberate requests once each");
            Check(runtime._audio.Swooshes == 2 && motion.AppliedCommandId == 2,
                "an acknowledged combo boost receives exactly one swoosh");
            var third = new SimStep(new(.12), new(.22), .1);
            Universe.PrepareVehicleWorkers(third); Universe.Apply(third);
            runtime._blade.TargetOn = false;
            runtime.UpdateSomersault();
            Check(motion.AppliedCommandId == 3 && runtime._audio.Swooshes == 2,
                "a completed somersault with an unlit saber is silent");
            runtime._blade.TargetOn = true;
            runtime.UpdateSomersault();
            Check(runtime._audio.Swooshes == 2, "igniting later cannot replay a silent jump's cue");
        }

        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Somersault input check failed: " + message);
            Console.WriteLine("PASS: " + message);
        }
    }
}
