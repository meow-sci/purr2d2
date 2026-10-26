using KSA;
using Purr2D2.Persistence;

namespace Purr2D2.Runtime;

internal sealed partial class SaberRuntime
{
    internal readonly KittenSaveSession SceneSaves = new();
    private readonly Dictionary<KittenEva, (KittenIdentity Identity, KittenProfile Profile)> _profileBindings
        = new(ReferenceEqualityComparer.Instance);
    private KittenIdentityIndex? _profileIndex;
    private bool _applyingSavedChoices;
    private bool _discardUnavailableKittenRecords;

    private void ApplyKittenProfileRecovery()
    {
        if (!_discardUnavailableKittenRecords) return;
        _discardUnavailableKittenRecords = false;
        if (SceneSaves.Suspended) return;
        SceneSaves.Profiles.DiscardUnavailable();
        var index = new KittenIdentityIndex();
        foreach (var pair in _kittenSabers)
        {
            if (_profileBindings.ContainsKey(pair.Key) || index.Resolve(pair.Key) is not { } identity) continue;
            var saber = pair.Value;
            var profile = SceneSaves.Profiles.GetOrCreate(identity, new()
            {
                Red = saber.Color.X, Green = saber.Color.Y, Blue = saber.Color.Z, Type = saber.Type,
                CapeEnabled = saber.CapeEnabled, SaberEnabled = SaberEnabledFor(pair.Key),
                BladeLengthOverride = saber.BladeLengthOverride
            }, SceneSaves.Warn);
            if (profile is not null) _profileBindings.Add(pair.Key, (identity, profile));
        }
        SceneSaves.Warn("Unavailable kitten records discarded. Future game saves use current choices.");
    }

    partial void BeginKittenProfileRefresh() => _profileIndex = null;

    partial void RestoreKittenChoices(KittenEva kitten, KittenSaber saber)
    {
        if (SceneSaves.Suspended) return;
        var identity = (_profileIndex ??= new()).Resolve(kitten);
        if (identity is null)
        {
            SceneSaves.Warn($"Kitten '{kitten.Id}' has unresolved identity; its choices cannot be rebound or saved.");
            return;
        }
        var profile = SceneSaves.Profiles.GetOrCreate(identity, new()
        {
            Red = saber.Color.X, Green = saber.Color.Y, Blue = saber.Color.Z,
            Type = saber.Type, CapeEnabled = saber.CapeEnabled,
            BladeLengthOverride = saber.BladeLengthOverride
        }, SceneSaves.Warn);
        if (profile is null) return;
        _profileBindings.Add(kitten, (identity, profile));
        _applyingSavedChoices = true;
        try
        {
            saber.Color = new(profile.Red, profile.Green, profile.Blue);
            saber.Type = profile.Type;
            saber.BladeLengthOverride = profile.BladeLengthOverride;
            SetKittenCapeEnabled(kitten, profile.CapeEnabled);
            // Discovery runs at the settled handoff. Restore directly through the same
            // equip stage as user requests; a large save must not overflow the UI queue.
            if (profile.SaberEnabled && _enabled)
            {
                saber.Blade.Equip(true);
                saber.Pose.Bind(kitten);
                saber.TogglePhase = SaberTogglePhase.Equipping;
            }
        }
        finally { _applyingSavedChoices = false; }
    }

    partial void RememberKittenChoices(KittenEva kitten, KittenSaber saber, bool revalidate, bool? enabled)
    {
        if (_applyingSavedChoices || SceneSaves.Suspended || !_profileBindings.TryGetValue(kitten, out var binding)
            || binding.Identity.Id != kitten.Id || binding.Identity.Character != kitten.Character?.Id) return;
        if (revalidate && new KittenIdentityIndex().Resolve(kitten) != binding.Identity)
        {
            _profileBindings.Remove(kitten);
            SceneSaves.Warn($"Identity changed for '{binding.Identity.Id}'; saved choices were preserved.");
            return;
        }
        var profile = binding.Profile;
        profile.Red = saber.Color.X; profile.Green = saber.Color.Y; profile.Blue = saber.Color.Z;
        profile.Type = saber.Type; profile.CapeEnabled = saber.CapeEnabled;
        profile.BladeLengthOverride = saber.BladeLengthOverride;
        // Intent survives missing poses and inactive resources. Only an explicit
        // equipment action changes it; cleanup/capture must not infer "off".
        if (enabled is { } requested) profile.SaberEnabled = requested;
    }

    partial void ForgetKittenBinding(KittenEva kitten)
    {
        if (_kittenSabers.TryGetValue(kitten, out var saber)) RememberKittenChoices(kitten, saber);
        _profileBindings.Remove(kitten);
    }

    partial void ReleaseProfileBindings()
    {
        foreach (var pair in _kittenSabers) RememberKittenChoices(pair.Key, pair.Value);
        _profileBindings.Clear(); _profileIndex = null;
        _discardUnavailableKittenRecords = false;
    }

    // Save captures can occur before the next settled frame. Include pending user intent,
    // but revalidate identities so a foreign rename cannot retarget a saved person.
    internal void CaptureKittenChoices()
    {
        if (_profileBindings.Count == 0 || SceneSaves.Suspended) return;
        var index = new KittenIdentityIndex();
        foreach (var pair in _kittenSabers)
            if (_profileBindings.TryGetValue(pair.Key, out var binding))
            {
                if (index.Resolve(pair.Key) == binding.Identity)
                    RememberKittenChoices(pair.Key, pair.Value, enabled: PendingSaberEnabledIntent(pair.Key));
                else
                {
                    _profileBindings.Remove(pair.Key);
                    SceneSaves.Warn($"Identity changed for '{binding.Identity.Id}'; saved choices were preserved.");
                }
            }
    }
}
