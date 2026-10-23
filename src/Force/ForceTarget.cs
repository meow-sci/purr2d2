using KSA;

namespace Purr2D2.Force;

/// <summary>A settled-query result. A group can contain both ordinary vehicles and local physical clutter.</summary>
internal sealed record ForceTarget(Vehicle? Vehicle, ClutterForceTarget? Clutter)
{
    internal ForceTarget(Vehicle vehicle) : this(vehicle, null) { }
    internal ForceTarget(ClutterForceTarget clutter) : this(null, clutter) { }
}
