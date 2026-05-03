using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;

namespace Encore.Services;

// Resolves the active SCD for a Penumbra mod given its option selections.
// Reads group_NNN_*.json + default_mod.json; intersects with effectiveModOptions or DefaultSettings.
// First .scd in the resolved file set wins.
internal static class ModMusicFinder
{
    public static string? FindActiveScd(string modRoot,
        IReadOnlyDictionary<string, List<string>>? effectiveModOptions,
        IPluginLog? log = null)
    {
        if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
        {
            log?.Debug($"[ModMusic] modRoot missing: {modRoot}");
            return null;
        }

        var activeRelPaths = new List<string>();

        TryAddFiles(Path.Combine(modRoot, "default_mod.json"), activeRelPaths,
            optionPicker: null);

        var groupFiles = Directory.EnumerateFiles(modRoot, "group_*.json",
            SearchOption.TopDirectoryOnly);
        foreach (var groupFilePath in groupFiles)
        {
            try
            {
                var json = File.ReadAllText(groupFilePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("Name", out var nameProp)) continue;
                string groupName = nameProp.GetString() ?? "";
                string groupType = root.TryGetProperty("Type", out var t)
                    ? (t.GetString() ?? "Single") : "Single";

                if (!root.TryGetProperty("Options", out var optsProp)) continue;
                if (optsProp.ValueKind != JsonValueKind.Array) continue;

                List<int>? activeOptionIndices = null;
                string? matchedSource = null;
                if (effectiveModOptions != null
                    && effectiveModOptions.TryGetValue(groupName, out var selected)
                    && selected != null && selected.Count > 0)
                {
                    activeOptionIndices = new List<int>();
                    int idx = 0;
                    foreach (var opt in optsProp.EnumerateArray())
                    {
                        var optName = opt.TryGetProperty("Name", out var n)
                            ? n.GetString() : null;
                        if (optName != null && selected.Contains(optName))
                            activeOptionIndices.Add(idx);
                        idx++;
                    }
                    if (activeOptionIndices.Count == 0) activeOptionIndices = null;
                    else matchedSource = "preset/modifier";
                }

                if (activeOptionIndices == null)
                {
                    uint defaults = root.TryGetProperty("DefaultSettings", out var d)
                        ? d.GetUInt32() : 0u;
                    activeOptionIndices = new List<int>();
                    if (groupType == "Single")
                    {
                        activeOptionIndices.Add((int)defaults);
                    }
                    else // Multi
                    {
                        for (int i = 0; i < 32; i++)
                            if ((defaults & (1u << i)) != 0) activeOptionIndices.Add(i);
                    }
                    matchedSource = "defaults";
                }

                int oi = 0;
                var pickedOptionNames = new List<string>();
                foreach (var opt in optsProp.EnumerateArray())
                {
                    if (activeOptionIndices.Contains(oi))
                    {
                        var optName = opt.TryGetProperty("Name", out var n)
                            ? n.GetString() : "(unnamed)";
                        pickedOptionNames.Add(optName ?? "(null)");
                        if (opt.TryGetProperty("Files", out var filesProp)
                            && filesProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var f in filesProp.EnumerateObject())
                            {
                                var rel = f.Value.GetString();
                                if (!string.IsNullOrEmpty(rel)) activeRelPaths.Add(rel);
                            }
                        }
                    }
                    oi++;
                }
                log?.Debug($"[ModMusic]   group '{groupName}' ({groupType}, via {matchedSource}): [{string.Join(", ", pickedOptionNames)}]");
            }
            catch
            {
            }
        }

        var scdRel = activeRelPaths
            .FirstOrDefault(p => p.EndsWith(".scd", StringComparison.OrdinalIgnoreCase));
        if (scdRel == null) return null;

        // Penumbra JSON paths use backslashes
        var fullPath = Path.Combine(modRoot, scdRel.Replace('\\', Path.DirectorySeparatorChar));
        return File.Exists(fullPath) ? fullPath : null;
    }

    private static void TryAddFiles(string jsonPath, List<string> sink,
        Func<JsonElement, bool>? optionPicker)
    {
        try
        {
            if (!File.Exists(jsonPath)) return;
            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Files", out var filesProp)) return;
            if (filesProp.ValueKind != JsonValueKind.Object) return;
            foreach (var f in filesProp.EnumerateObject())
            {
                var rel = f.Value.GetString();
                if (!string.IsNullOrEmpty(rel)) sink.Add(rel);
            }
        }
        catch { }
    }
}
