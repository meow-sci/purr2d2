using System.Text.RegularExpressions;

namespace Purr2D2.Rendering;

/// <summary>Flatten only our portable includes; native includes resolve beside the real native water shader.</summary>
internal static class SaberWaterShaderSource
{
    private static readonly HashSet<string> NativeIncludes = ["../Common/Shared.glsl", "../Common/Camera.glsl", "OceanFunctions.glsl"];
    private static readonly Regex Include = new("^[ \\t]*#include[ \\t]+\"([^\"\\r\\n]+)\"[ \\t]*\\r?$",RegexOptions.Multiline);

    internal static string Expand(string path, string shaderRoot)
    {
        string root = Path.GetFullPath(shaderRoot) + Path.DirectorySeparatorChar;
        int files = 0;
        return Read(Path.GetFullPath(path),new HashSet<string>(StringComparer.Ordinal));

        string Read(string file, HashSet<string> active)
        {
            if (!file.StartsWith(root,StringComparison.Ordinal) || active.Count >= 32 || ++files > 128 || !active.Add(file))
                throw new InvalidDataException("Water shader include escaped its packaged tree or is cyclic.");
            try
            {
                if (new FileInfo(file).Length > 2 * 1024 * 1024) throw new InvalidDataException("Water shader source exceeds its size limit.");
                string expanded = Include.Replace(File.ReadAllText(file),match =>
                {
                    string include=match.Groups[1].Value;
                    if (NativeIncludes.Contains(include)) return match.Value;
                    if (include.Contains('\\') || Path.IsPathRooted(include)) throw new InvalidDataException("Invalid water shader include.");
                    return Read(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!,include)),active);
                });
                if (expanded.Length > 2 * 1024 * 1024) throw new InvalidDataException("Expanded water shader exceeds its size limit.");
                return expanded;
            }
            finally { active.Remove(file); }
        }
    }
}
