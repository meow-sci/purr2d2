using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;

namespace Purr2D2.Rendering;

public sealed unsafe partial class SaberRenderer
{
    private const int MaximumTreeFireSources = 32;
    private readonly List<TreeFireSource> _treeFire = new(MaximumTreeFireSources);
    private readonly record struct TreeFireSource(Part Part, double3 Point, double3 Normal, double Born, double Duration);

    internal void AddTreeCutFire(Part part, double3 pointLocal, double3 normalLocal, double now, double seconds)
    {
        if (_disposed || !Finite(pointLocal) || !Finite(normalLocal) || !double.IsFinite(now)
            || !double.IsFinite(seconds) || seconds <= 0 || part.Tree?.OwningVehicle is not { IsDisposed: false }) return;
        if (_treeFire.Count == MaximumTreeFireSources) _treeFire.RemoveAt(0);
        _treeFire.Add(new(part, pointLocal, normalLocal.NormalizeOrZero(), now, Math.Clamp(seconds, 0, 60)));
    }
    private void UpdateTreeFire(double now)
    {
        for (int i = _treeFire.Count - 1; i >= 0; i--)
            if (now < _treeFire[i].Born || now - _treeFire[i].Born >= _treeFire[i].Duration
                || _treeFire[i].Part.Tree?.OwningVehicle is not { IsDisposed: false } vehicle
                || !ReferenceEquals(vehicle.System, Universe.CurrentSystem)) _treeFire.RemoveAt(i);
    }
    internal void ClearTreeFire() => _treeFire.Clear();

    private void DrawTreeFire(CommandBuffer commands, float4x4 projection, Camera camera)
    {
        if (!GameSettings.Current.Graphics.Particles) return;
        foreach (var source in _treeFire)
        {
            double age = _now - source.Born;
            if (age < 0 || age >= source.Duration || source.Part.Tree?.OwningVehicle is not { IsDisposed: false } vehicle) continue;
            var vehicleMatrix = vehicle.GetMatrixAsmb2Ego(camera);
            var matrix = source.Part.MatrixAsmb2Ego(in vehicleMatrix);
            var origin = source.Point.Transform(matrix);
            if (!Finite(origin) || origin.LengthSquared() > 300 * 300 || camera.IsSphereOutsideFrustum(origin, 1.5)) continue;
            var up = camera.GetPositionEgo(vehicle).NormalizeOrZero();
            // Camera coordinates are relative; radial up comes from the body's
            // actual inertial radial direction transformed through its ego frame.
            var upBody = vehicle.GetPositionCci().NormalizeOrZero().Transform(vehicle.GetBody2Cci().Inverse());
            up = upBody.Transform(vehicle.Body2Ego).NormalizeOrZero();
            var tangent = double3.Cross(up, Math.Abs(up.X) < .8 ? double3.UnitX : double3.UnitY).NormalizeOrZero();
            var bitangent = double3.Cross(up, tangent);
            double fade = Math.Min(1, (source.Duration - age) / .7);
            for (int i = 0; i < 10; i++)
            {
                double cycle = age * 1.7 + i * .61803398875;
                double phase = cycle - Math.Floor(cycle);
                double angle = i * 2.399963 + Math.Floor(cycle) * 1.83;
                var lateral = tangent * Math.Cos(angle) + bitangent * Math.Sin(angle);
                var tail = origin + lateral * (.03 + .1 * phase) + up * (.6 * phase);
                var tip = tail + up * (.09 + .11 * (1 - phase)) + lateral * .02;
                float intensity = (float)(5 * (1 - phase) * fade);
                DrawRibbon(commands, projection, tail, tip, double3.Zero, (float)(.045 * (1 - phase) + .008), .006f,
                    new float4(1, (float)(.22 + .48 * (1 - phase)), .015f, intensity), isChar: false, billboard: true);
            }
        }
    }
}
