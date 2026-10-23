using Brutal.Numerics;

namespace Purr2D2.Core;

/// <summary>A temporary, bounded pose offset. It never owns or changes saved preferences.</summary>
public sealed class SaberOverlayDrag
{
    public const double ReturnDuration = .24;
    private float2 _press, _target, _offset, _returnFrom;
    private float _radius;
    private double _returnTime;

    public bool Active { get; private set; }
    public bool Held { get; private set; }
    public float2 Offset => _offset;
    public float3 OriginalGrip { get; private set; }
    public float3 OriginalAim { get; private set; }

    public bool Begin(float2 pointer, float radius, float3 grip, float3 aim)
    {
        if (!Finite(pointer) || !float.IsFinite(radius) || radius <= 0 || !Finite(grip)
            || !Finite(aim) || aim.LengthSquared() < 1e-8f) return false;
        if (Active)
        {
            // Re-grabbing during the return continues this gesture without promoting an offset
            // pose to the baseline. The cursor's new origin preserves the current displayed offset.
            _press = pointer - _offset * radius;
            _radius = radius;
            _target = _offset;
            _returnTime = 0;
            Held = true;
            return true;
        }
        _press = pointer;
        _radius = radius;
        _target = _offset = _returnFrom = float2.Zero;
        _returnTime = 0;
        OriginalGrip = grip;
        OriginalAim = aim;
        Active = Held = true;
        return true;
    }

    public void Move(float2 pointer)
    {
        if (Held && Finite(pointer)) _target = ClampDisk((pointer - _press) / _radius);
    }

    public void Release()
    {
        if (!Held) return;
        Held = false;
        _returnFrom = _offset;
        _returnTime = 0;
    }

    /// <summary>Returns true for the final exact restoration frame as well as active frames.</summary>
    public bool Advance(double dt, out float3 grip, out float3 aim)
    {
        grip = OriginalGrip;
        aim = OriginalAim;
        if (!Active) return false;
        dt = double.IsFinite(dt) ? Math.Clamp(dt, 0, .1) : 0;
        if (Held)
        {
            // A short response time filters uneven mouse event rates without adding a long lag.
            float blend = (float)(1 - Math.Exp(-dt / .035));
            _offset += (_target - _offset) * blend;
        }
        else
        {
            _returnTime = Math.Min(ReturnDuration, _returnTime + dt);
            float t = (float)(_returnTime / ReturnDuration);
            float ease = t * t * (3 - 2 * t);
            _offset = _returnFrom * (1 - ease);
            if (_returnTime >= ReturnDuration)
            {
                _offset = float2.Zero;
                Active = false;
                return true;
            }
        }
        if (_offset.LengthSquared() <= 1e-12f) return true;

        // Model +X is the kitten's left. Screen right therefore moves the paw toward -X.
        // Keep reach changes small; the existing measured-bone IK does the final reach clamp.
        grip = new float3(
            Math.Clamp(OriginalGrip.X - _offset.X * 9, -40, 40),
            Math.Clamp(OriginalGrip.Y - _offset.Y * 10, -40, 40),
            OriginalGrip.Z);
        aim = float3.Transform(OriginalAim, floatQuat.CreateFromAxisAngle(float3.UnitY, -_offset.X * .9f));
        aim = float3.Transform(aim, floatQuat.CreateFromAxisAngle(float3.UnitX, _offset.Y * .75f));
        return true;
    }

    public void Reset()
    {
        Active = Held = false;
        _target = _offset = _returnFrom = float2.Zero;
        _returnTime = 0;
    }

    public static bool ContainsCircle(float2 point, float2 center, float radius) =>
        Finite(point) && Finite(center) && float.IsFinite(radius) && radius > 0
        && (point - center).LengthSquared() <= radius * radius;

    public static float2 ClampDisk(float2 value)
    {
        if (!Finite(value)) return float2.Zero;
        float length = value.Length();
        return length > 1 ? value / length : value;
    }

    private static bool Finite(float2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(float3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>Only a button whose press belongs to the overlay may have its release swallowed.</summary>
public sealed class SaberOverlayPointerCapture
{
    private int _buttons, _imguiButtons;
    public bool AnyHeld => _buttons != 0;
    public ulong EventFrame { get; private set; } = ulong.MaxValue;
    public bool IsHeld(int button) => (uint)button < 32 && (_buttons & (1 << button)) != 0;
    public bool OwnsScroll(bool overOverlay) => overOverlay || AnyHeld;

    public bool Consume(int button, bool pressed, bool overOverlay, bool gameOwnsGesture, ulong frame)
    {
        if ((uint)button >= 32) return false;
        int bit = 1 << button;
        if (!pressed)
        {
            if ((_buttons & bit) == 0) return false;
            _buttons &= ~bit;
        }
        else
        {
            if (gameOwnsGesture || (!overOverlay && !AnyHeld)) return false;
            _buttons |= bit;
            // Program and the ImGui backend are independent event subscribers. A game-side
            // release must not erase this decision before the backend receives the same event.
            _imguiButtons |= bit;
        }
        EventFrame = frame;
        return true;
    }

    public bool SuppressImGuiButton(int button, bool pressed, ulong frame)
    {
        if ((uint)button >= 32) return false;
        int bit = 1 << button;
        if ((_imguiButtons & bit) == 0) return false;
        if (!pressed)
        {
            _imguiButtons &= ~bit;
            // A release from a detached platform window can reach the backend first.
            // Complete the same owned gesture without forwarding it to native consumers.
            _buttons &= ~bit;
        }
        EventFrame = frame;
        return true;
    }

    public void Reset()
    {
        _buttons = _imguiButtons = 0;
        EventFrame = ulong.MaxValue;
    }
}
