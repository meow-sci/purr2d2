using KSA;
using Purr2D2.Persistence;

namespace Purr2D2.Runtime;

/// <summary>Built lazily once per binding batch/capture, never once per kitten per frame.</summary>
internal sealed class KittenIdentityIndex
{
    private readonly Dictionary<string, KittenRosterEntryData> _roster = new(StringComparer.Ordinal);
    private readonly Dictionary<KeyHash, int> _rosterHashes = new();
    private readonly HashSet<KeyHash> _occupiedSeats = new();
    private readonly Dictionary<KeyHash, int> _vehicleHashes = new();
    private readonly CelestialSystem? _system = Universe.CurrentSystem;

    internal KittenIdentityIndex()
    {
        if (Universe.KittenRoster is { } roster)
            foreach (var entry in roster.Kittens)
            {
                _roster.TryAdd(entry.Name, entry);
                Add(_rosterHashes, KeyHash.Make(entry.Name));
                if (entry.NameHash != KeyHash.Make(entry.Name)) Add(_rosterHashes, entry.NameHash);
            }
        if (_system is null) return;
        foreach (var item in _system.All.AsSpan())
            if (item is Vehicle { IsDisposed: false } vehicle)
            {
                Add(_vehicleHashes, KeyHash.Make(vehicle.Id));
                foreach (var seat in vehicle.Crew)
                    if (seat.AssignedKittenHash != KeyHash.Zero) _occupiedSeats.Add(seat.AssignedKittenHash);
            }
    }

    internal KittenIdentity? Resolve(KittenEva kitten)
    {
        string? character = kitten.Character?.Id;
        var hash = KeyHash.Make(kitten.Id);
        if (string.IsNullOrWhiteSpace(character) || character.Length > 256 || string.IsNullOrWhiteSpace(kitten.Id) || kitten.Id.Length > 256
            || hash == KeyHash.Zero || kitten.Hash != hash || _system is null
            || !ReferenceEquals(_system.All.Get(hash), kitten)
            || !_vehicleHashes.TryGetValue(hash, out int vehicles) || vehicles != 1 || _occupiedSeats.Contains(hash)) return null;
        if (_roster.TryGetValue(kitten.Id, out var entry))
        {
            if (_rosterHashes[hash] != 1 || entry.NameHash != hash || entry.Character != character
                || entry.Kia || entry.AssignedVehicleId != KittenRosterEntryData.EvaAssignmentId) return null;
            return new("roster", kitten.Id, character);
        }
        return _rosterHashes.ContainsKey(hash) ? null : new("standalone-eva", kitten.Id, character);
    }

    private static void Add(Dictionary<KeyHash, int> counts, KeyHash hash) =>
        counts[hash] = counts.GetValueOrDefault(hash) + 1;
}
