namespace Purr2D2.Core;

/// <summary>Maps the literal GLFW arrow-key state to the selected somersault direction.</summary>
internal static class SomersaultKeyboardDirection
{
    internal static SomersaultDirection Resolve(bool up, bool down, bool left, bool right)
    {
        if (left && right) return SomersaultDirection.Forward;
        if (left) return SomersaultDirection.Left;
        if (right) return SomersaultDirection.Right;
        if (up) return SomersaultDirection.Forward;
        if (down) return SomersaultDirection.Backward;
        return SomersaultDirection.Forward;
    }
}
