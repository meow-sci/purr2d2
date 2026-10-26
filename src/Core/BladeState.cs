namespace Purr2D2.Core;

/// <summary>Continuous reversible deployment. All time passed here is simulation time.</summary>
public sealed class BladeState
{
    public bool Equipped { get; private set; }
    public bool TargetOn { get; private set; }
    public float Deployment { get; private set; }
    public bool ManualDeployment { get; private set; }
    public float ManualTargetDeployment { get; private set; }
    public string Phase => !Equipped ? "Unequipped" : ManualDeployment
        ? Deployment < ManualTargetDeployment ? "Extending"
            : Deployment > ManualTargetDeployment ? "Retracting"
            : Deployment == 0 ? "Retracted" : Deployment == 1 ? "Lit" : "Partial"
        : TargetOn
            ? Deployment == 1 ? "Lit" : "Extending"
            : Deployment == 0 ? "Retracted" : "Retracting";

    public void Equip(bool equipped)
    {
        Equipped = equipped;
        if (!equipped) Reset();
    }

    public void SetIgnited(bool on)
    {
        ManualDeployment = false;
        TargetOn = Equipped && on;
    }

    public bool HoldDeployment(float deployment)
    {
        if (!SetManualDeploymentTarget(deployment)) return false;
        Deployment = ManualTargetDeployment;
        return true;
    }

    public bool SetManualDeploymentTarget(float deployment)
    {
        if (!Equipped || !float.IsFinite(deployment)) return false;
        ManualTargetDeployment = Math.Clamp(deployment, 0, 1);
        ManualDeployment = true;
        TargetOn = ManualTargetDeployment > 0;
        return true;
    }

    public void Advance(double dt, float extensionSeconds = .3f, float retractionSeconds = .22f)
    {
        if (!Equipped || !double.IsFinite(dt) || dt <= 0) return;
        if (ManualDeployment)
        {
            if (Deployment == ManualTargetDeployment) return;
            const double manualDurationSeconds = .15;
            double manualStep = Math.Min(dt, .1) / manualDurationSeconds;
            if (Deployment < ManualTargetDeployment)
                Deployment = (float)Math.Min(ManualTargetDeployment, Deployment + manualStep);
            else if (Deployment > ManualTargetDeployment)
                Deployment = (float)Math.Max(ManualTargetDeployment, Deployment - manualStep);
            return;
        }
        float seconds = TargetOn ? extensionSeconds : retractionSeconds;
        if (!float.IsFinite(seconds) || seconds <= 0) return;
        // A stalled frame never jumps through the entire contact/deployment history.
        float step = (float)Math.Min(dt, .1) / seconds;
        Deployment = Math.Clamp(Deployment + (TargetOn ? step : -step), 0, 1);
    }

    public void Reset()
    {
        Equipped = false;
        TargetOn = false;
        Deployment = 0;
        ManualDeployment = false;
        ManualTargetDeployment = 0;
    }
}
