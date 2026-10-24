using KSA;
using Purr2D2.Core;

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
        private bool _ready = true, _disposed = false, _selectingSlice = false;
        private string? _fault = null;
        private KittenEva? _owner;
        private readonly TestSettings _settings = new();
        private sealed class TestSettings { internal float JumpComboIntervalSeconds = .75f; }
        private readonly FixturePose _pose = new();
        private readonly FixtureSlice _sliceSequence = new();
        private sealed class FixturePose { internal bool Swinging => false; }
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
            runtime.QueueSomersault(SomersaultDirection.Forward);
            runtime.UpdateSomersault();
            var first = new SimStep(new(0), new(.02), .02);
            Universe.PrepareVehicleWorkers(first); Universe.Apply(first);
            Check(motion.Active && motion.RotationRadians == 0, "applied first launch awaits the next settled observation");

            // PollEvents happens after ApplyVehicleSolvers but before the mod's
            // settled update. The queue must observe the first launch itself.
            Program.PlayerTime = 10.5;
            runtime.QueueSomersault(SomersaultDirection.Backward);
            Check(runtime._somersaultRequest is { PressTime: 10.5 },
                "real input boundary accepts the exact-window second press before settled launch acknowledgement");
            Program.PlayerTime = 10.9;
            runtime.UpdateSomersault();
            Check(motion.CommandId == 2 && motion.Direction == SomersaultDirection.Forward,
                "low-frame-rate settled processing retains press time and original direction");
            var second = new SimStep(new(.02), new(.12), .1);
            Universe.PrepareVehicleWorkers(second); Universe.Apply(second);
            Program.PlayerTime = 11;
            runtime.QueueSomersault(SomersaultDirection.Left);
            Check(runtime._somersaultRequest is { PressTime: 11 },
                "real input boundary also observes an applied second boost before accepting the third press");
            runtime.UpdateSomersault();
            Check(motion.CommandId == 3, "input-to-worker integration accepts three deliberate requests once each");
        }

        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Somersault input check failed: " + message);
            Console.WriteLine("PASS: " + message);
        }
    }
}
