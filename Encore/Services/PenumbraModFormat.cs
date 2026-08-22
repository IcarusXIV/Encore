using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Encore.Services;

// Penumbra v4 meta.json readers; callers fall back to the legacy group/default JSONs.
internal static class PenumbraModFormat
{
    public static JsonElement? TryReadV4Meta(string modRoot)
    {
        try
        {
            var metaPath = Path.Combine(modRoot, "meta.json");
            if (!File.Exists(metaPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("FileVersion", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() < 4) return null;
            return root.Clone();
        }
        catch
        {
            return null;
        }
    }

    public static IEnumerable<JsonElement> EnumerateGroups(JsonElement v4Meta)
    {
        if (!v4Meta.TryGetProperty("Groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind == JsonValueKind.Object)
                yield return group;
        }
    }

    public static void CollectFileGamePaths(JsonElement element, List<string> sink)
        => CollectFiles(element, sink, keys: true);

    public static void CollectFileDiskPaths(JsonElement element, List<string> sink)
        => CollectFiles(element, sink, keys: false);

    private static void CollectFiles(JsonElement element, List<string> sink, bool keys)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name == "Files" && property.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var file in property.Value.EnumerateObject())
                        {
                            var entry = keys ? file.Name : file.Value.GetString();
                            if (!string.IsNullOrEmpty(entry))
                                sink.Add(entry!);
                        }
                    }
                    else
                    {
                        CollectFiles(property.Value, sink, keys);
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectFiles(item, sink, keys);
                break;
        }
    }
}
