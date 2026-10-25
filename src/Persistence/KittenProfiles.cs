using System.Text.Json;
using System.Text.Json.Serialization;
using Purr2D2.Core;

namespace Purr2D2.Persistence;

internal sealed record KittenIdentity(string Kind, string Id, string Character);

internal sealed class KittenProfile
{
    [JsonRequired] public float Red { get; set; }
    [JsonRequired] public float Green { get; set; }
    [JsonRequired] public float Blue { get; set; }
    [JsonRequired] public SaberType Type { get; set; }
    [JsonRequired] public bool CapeEnabled { get; set; }
    [JsonRequired] public bool SaberEnabled { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    internal void Validate()
    {
        if (!ValidColor(Red) || !ValidColor(Green) || !ValidColor(Blue) || !Enum.IsDefined(Type))
            throw new InvalidDataException("Invalid saved kitten appearance.");
    }

    private static bool ValidColor(float value) => float.IsFinite(value) && value is >= 0 and <= 1;
}

internal sealed class KittenSaveRecord
{
    public KittenIdentity Identity { get; set; } = new("", "", "");
    public int Version { get; set; } = 1;
    public JsonElement State { get; set; }
}

internal sealed class KittenSaveDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string UniverseSha256 { get; set; } = "";
    public List<KittenSaveRecord> Kittens { get; set; } = new();
}

/// <summary>Scene recipes only. No game objects or resources survive in this registry.</summary>
internal sealed class KittenProfiles
{
    private sealed record Entry(KittenSaveRecord Original, KittenProfile? Profile);
    private readonly Dictionary<(string Kind, string Id), Entry> _entries = new();
    internal int PreservedCount => _entries.Values.Count(entry => entry.Profile is null);

    internal void DiscardUnavailable()
    {
        foreach (var key in _entries.Where(item => item.Value.Profile is null).Select(item => item.Key).ToArray())
            _entries.Remove(key);
    }

    internal void Import(KittenSaveDocument? document, Action<string> warn)
    {
        if (document is not null) KittenSaveStorage.Validate(document);
        _entries.Clear();
        if (document is null) return;
        foreach (var record in document.Kittens)
        {
            KittenProfile? profile = null;
            try
            {
                if (record.Version != 1) throw new InvalidDataException($"unsupported version {record.Version}");
                profile = record.State.Deserialize<KittenProfile>(KittenSaveStorage.Options)
                    ?? throw new InvalidDataException("missing settings");
                profile.Validate();
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or NotSupportedException)
            {
                profile = null;
                warn($"Settings for '{record.Identity.Id}' preserved without applying: {error.Message}");
            }
            _entries.Add((record.Identity.Kind, record.Identity.Id), new(record, profile));
        }
    }

    internal KittenProfile? GetOrCreate(KittenIdentity identity, KittenProfile defaults, Action<string> warn)
    {
        var key = (identity.Kind, identity.Id);
        if (_entries.TryGetValue(key, out var entry))
        {
            if (entry.Original.Identity.Character == identity.Character) return entry.Profile;
            if (entry.Profile is { } previous)
                _entries[key] = new(new()
                {
                    Identity = entry.Original.Identity, Version = entry.Original.Version,
                    State = JsonSerializer.SerializeToElement(previous, KittenSaveStorage.Options)
                }, null);
            warn($"Saved character identity changed for '{identity.Id}'; its original settings were preserved.");
            return null;
        }
        if (_entries.Count >= KittenSaveStorage.MaximumKittens)
        {
            warn("Kitten save limit reached; additional choices cannot be saved.");
            return null;
        }
        defaults.Validate();
        _entries.Add(key, new(new() { Identity = identity }, defaults));
        return defaults;
    }

    internal KittenSaveDocument Capture()
    {
        var document = new KittenSaveDocument();
        foreach (var entry in _entries.Values)
            document.Kittens.Add(new()
            {
                Identity = entry.Original.Identity,
                Version = entry.Original.Version,
                State = entry.Profile is { } profile
                    ? JsonSerializer.SerializeToElement(profile, KittenSaveStorage.Options) : entry.Original.State.Clone()
            });
        return document;
    }
}
