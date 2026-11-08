using KSA;

namespace Purr2D2.Slicing;

/// <summary>Classify only participants in a completed slice separation, after bulk tree updates.</summary>
internal static class SliceDebrisClassification
{
    internal static void MarkSeparated(Vehicle vehicle)
    {
        // Match native cleanup's control/crew protections. Native Split copies
        // IsDebris from the source, so an inert offcut otherwise remains Vehicle.
        if (vehicle.IsDisposed || vehicle is KittenEva || vehicle.IsEditedVehicle
            || vehicle.Parts.IsDisposed || vehicle.HasControlModule || ReferenceEquals(vehicle, Program.ControlledVehicle)) return;
        foreach (var seat in vehicle.Crew)
            if (seat.AssignedKittenHash != KeyHash.Zero) return;
        vehicle.MarkAsDebris();
    }
}
