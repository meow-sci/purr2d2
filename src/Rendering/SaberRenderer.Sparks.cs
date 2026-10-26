using System;
using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using KSA;
using Purr2D2.Core;

namespace Purr2D2.Rendering;

public sealed unsafe partial class SaberRenderer
{
    // Allocated once. No native emitters, physics objects, textures, buffers, or extra lights.
    private readonly SlagBurst[] _slag = new SlagBurst[DarkSaberSparkTuning.MaximumBursts];
    private readonly SlagSource[] _slagSources = new SlagSource[DarkSaberSparkTuning.MaximumSources];
    private int _slagCount;
    private uint _clashSparkSequence;
    private DarkSaberSparkTuning _slagTuning = DarkSaberSparkTuning.FromEffects(SaberEffectSettings.Create(SaberType.Dark));
    private static readonly DarkSaberSparkTuning ClashSparkTuning = new(8, 0, .8f, 2.4f, .24f, 1.45f);

    private struct SlagSource
    {
        internal KittenEva? Owner;
        internal double LastSeen;
        internal DarkSaberSparkClock Clock;
    }

    private readonly record struct SlagBurst(KittenEva Owner, DarkSaberSparkFrame Frame, float3 Color, double Born,
        float Seed, DarkSaberSparkTuning Tuning);

    private void SubmitSlag(KittenEva owner, IViewport viewport, float4x4 emitterToEgo,
        float length, float3 color)
    {
        if (!_slagTuning.Enabled || !GameSettings.Current.Graphics.Particles || owner.IsDisposed
            || !double.IsFinite(_now) || _now < 0 || !float.IsFinite(length) || length < .12f) return;
        var camera = viewport.GetCamera();
        var start = double3.Unpack(emitterToEgo.Translation);
        var axis = double3.Unpack(emitterToEgo.Z.XYZ).NormalizeOrZero();
        if (!Finite(start) || !Finite(axis) || axis.Length() < .5
            || DarkSaberSparkTuning.Visibility(start.Length()) <= 0
            || camera.IsSphereOutsideFrustum(start + axis * (length * .5), length * .5 + 3)) return;
        var clip = float4.Transform(new float4(float3.Pack(in start), 1), camera.MVP.viewProjection);
        if (clip.W <= .0001f || .065f * Math.Abs(camera.MVP.projection.M22)
            * viewport.OffscreenTarget.Extent.Height / (2 * clip.W) < 1) return;

        int sourceIndex = -1, freeIndex = -1;
        for (int i = 0; i < _slagSources.Length; i++)
        {
            if (ReferenceEquals(_slagSources[i].Owner, owner)) { sourceIndex = i; break; }
            if (_slagSources[i].Owner == null && freeIndex < 0) freeIndex = i;
        }
        if (sourceIndex < 0)
        {
            if (freeIndex < 0) return;
            sourceIndex = freeIndex;
            _slagSources[sourceIndex] = new SlagSource { Owner = owner };
        }
        ref var source = ref _slagSources[sourceIndex];
        source.LastSeen = _now;
        if (!source.Clock.TryEmit(_now, _slagTuning.Frequency, SaberVfx.Seed(owner.Id), out float seed)
            || _slagCount == _slag.Length) return; // Drop surplus; never catch up after a hitch.

        var emission = start + axis * (length * (.15 + .7 * ((seed * .61803398875) % 1)));
        // Capture only the small emission offset, using the same camera-relative owner position
        // as the saber. Following current owner translation works at any speed, even while
        // physics is paused or warped; integrating orbital velocity with player time does not.
        if (!DarkSaberSparkFrame.TryCreate(camera.GetPositionEgo(owner), emission,
                owner.GetPositionCce(), out var frame)) return;
        _slag[_slagCount++] = new SlagBurst(owner, frame, color, _now, seed, _slagTuning);
    }

    private void UpdateSlag(double now)
    {
        if (!double.IsFinite(now) || now < _now || now < 0) { ClearSlag(); return; }
        for (int i = _slagCount - 1; i >= 0; i--)
            if (_slag[i].Owner.IsDisposed || now - _slag[i].Born >= _slag[i].Tuning.Lifetime)
            {
                _slag[i] = _slag[--_slagCount];
                _slag[_slagCount] = default;
            }
        for (int i = 0; i < _slagSources.Length; i++)
            if (_slagSources[i].Owner is { } owner
                && (owner.IsDisposed || now - _slagSources[i].LastSeen > 2)) _slagSources[i] = default;
    }

    /// <summary>One bounded ballistic burst at a clash point, independent of Dark saber ambient sparkle settings.</summary>
    internal void AddClashSparks(KittenEva owner, double3 pointBody, double now)
    {
        if (_disposed || owner.IsDisposed || !GameSettings.Current.Graphics.Particles
            || !double.IsFinite(now) || now < 0 || !Finite(pointBody)) return;
        try
        {
            Camera camera = GameAudio.GetAudioCamera();
            double3 ownerEgo = camera.GetPositionEgo(owner);
            double3 pointEgo = ownerEgo + pointBody.Transform(owner.Body2Cce);
            double3 upEgo = double3.UnitY.Transform(owner.Body2Cce);
            if (!DarkSaberSparkFrame.TryCreate(ownerEgo, pointEgo, upEgo, out var frame)) return;
            uint sequence = _clashSparkSequence++ % 1024;
            float seed = SaberVfx.Seed(owner.Id) * 97 + sequence * 13;
            var burst = new SlagBurst(owner, frame, new float3(1f, .54f, .17f), now, seed, ClashSparkTuning);
            if (_slagCount < _slag.Length) _slag[_slagCount++] = burst;
            else
            {
                int oldest = 0;
                for (int i = 1; i < _slagCount; i++)
                    if (_slag[i].Born < _slag[oldest].Born) oldest = i;
                _slag[oldest] = burst;
            }
        }
        catch (Exception ex) { Report("clash spark submission", ex); }
    }

    private void ClearSlag()
    {
        Array.Clear(_slag);
        Array.Clear(_slagSources);
        _slagCount = 0;
        _clashSparkSequence = 0;
    }

    private void DrawSlag(Camera camera, CommandBuffer commands, float4x4 projection)
    {
        if (!GameSettings.Current.Graphics.Particles) return;
        for (int i = 0; i < _slagCount; i++)
        {
            var burst = _slag[i];
            float age = (float)(_now - burst.Born);
            if (burst.Owner.IsDisposed || age < 0 || age >= burst.Tuning.Lifetime) continue;
            var origin = burst.Frame.Origin(camera.GetPositionEgo(burst.Owner));
            float visibility = DarkSaberSparkTuning.Visibility(origin.Length());
            double travel = 2.8 * burst.Tuning.Speed * burst.Tuning.Lifetime
                + 3 * burst.Tuning.Lifetime * burst.Tuning.Lifetime + .2;
            if (visibility <= 0 || !Finite(origin) || camera.IsSphereOutsideFrustum(origin, travel)) continue;
            var right = burst.Frame.Right;
            var up = burst.Frame.Up;
            var forward = double3.Cross(right, up);
            var model = new double4x4(
                right.X, right.Y, right.Z, 0,
                up.X, up.Y, up.Z, 0,
                forward.X, forward.Y, forward.Z, 0,
                origin.X, origin.Y, origin.Z, 1);
            var cameraLocal = new float3((float)-double3.Dot(origin, right),
                (float)-double3.Dot(origin, up), (float)-double3.Dot(origin, forward));
            var push = new GlowPush
            {
                Mvp = float4x4.Pack(in model) * projection,
                Color = new float4(burst.Color, 8 * burst.Tuning.Brightness * visibility),
                // Mode -1: cheap instanced ballistic streaks, sharing the existing dark pipeline.
                Shape = new float4(0, .006f * burst.Tuning.Size, burst.Tuning.Speed, -1),
                Animation = new float4(age, burst.Seed, burst.Tuning.Lifetime, 6),
                Camera = new float4(cameraLocal, 0),
            };
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
                ByteSize.Zero, push);
            commands.Draw(4, burst.Tuning.Count, 0, 0);
        }
    }
}
