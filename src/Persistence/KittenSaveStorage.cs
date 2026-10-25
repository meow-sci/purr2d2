using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Purr2D2.Persistence;

internal static class KittenSaveStorage
{
    internal const string FileName = "purr2d2.json";
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumKittens = 4096;
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, MaxDepth = 32,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static KittenSaveDocument? Read(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Kitten save exceeds 4 MiB.");
        using var json = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = Options.MaxDepth });
        ValidateProperties(json.RootElement);
        var document = json.RootElement.Deserialize<KittenSaveDocument>(Options)
            ?? throw new InvalidDataException("Missing kitten save document.");
        Validate(document);
        if (!string.Equals(document.UniverseSha256, HashUniverse(directory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Kitten settings belong to a different universe.xml.");
        return document;
    }

    internal static void Write(string directory, KittenSaveDocument document)
    {
        document.UniverseSha256 = HashUniverse(directory);
        Validate(document);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, Options);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Kitten save exceeds 4 MiB.");
        string path = Path.Combine(directory, FileName);
        string temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void Validate(KittenSaveDocument document)
    {
        if (document.SchemaVersion != 1 || document.Kittens is null || document.Kittens.Count > MaximumKittens)
            throw new InvalidDataException("Unsupported or invalid kitten save document.");
        var keys = new HashSet<(string, string)>();
        foreach (var record in document.Kittens)
        {
            if (record?.Identity is not { } identity || identity.Kind is not ("roster" or "standalone-eva")
                || !ValidId(identity.Id) || !ValidId(identity.Character) || record.Version < 1
                || record.State.ValueKind != JsonValueKind.Object || !keys.Add((identity.Kind, identity.Id)))
                throw new InvalidDataException("Invalid or duplicate kitten save identity.");
        }
    }

    private static bool ValidId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static string HashUniverse(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, "universe.xml"));
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Duplicate property '{property.Name}'.");
                ValidateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateProperties(child);
    }
}
