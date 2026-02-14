using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Encore.Services;

// Cache data structure for emote mods - persisted to disk
public class EmoteModCacheData
{
    public const int CurrentVersion = 11;

    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("lastUpdated")]
    public DateTime LastUpdated { get; set; }

    [JsonPropertyName("mods")]
    public Dictionary<string, EmoteModCacheEntry> Mods { get; set; } = new();
}

// Individual cache entry for an emote mod
public class EmoteModCacheEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("isEmoteMod")]
    public bool IsEmoteMod { get; set; }

    [JsonPropertyName("affectedEmotes")]
    public List<string> AffectedEmotes { get; set; } = new();

    [JsonPropertyName("emoteCommands")]
    public List<string> EmoteCommands { get; set; } = new();

    [JsonPropertyName("primaryEmote")]
    public string PrimaryEmote { get; set; } = "";

    [JsonPropertyName("primaryCommand")]
    public string PrimaryCommand { get; set; } = "";

    [JsonPropertyName("animationType")]
    public int AnimationType { get; set; } = 1; // Default to Emote (1)

    [JsonPropertyName("poseAnimationType")]
    public int PoseAnimationType { get; set; } = 0; // For mixed mods: original pose type before reclassification

    [JsonPropertyName("poseIndex")]
    public int PoseIndex { get; set; } = -1; // -1 = not applicable/unknown

    [JsonPropertyName("affectedPoseIndices")]
    public List<int> AffectedPoseIndices { get; set; } = new();

    [JsonPropertyName("poseTypeIndices")]
    public Dictionary<int, List<int>> PoseTypeIndices { get; set; } = new();

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; set; }
}

/// <summary>
/// Manages caching of emote mod detection results.
/// Uses the same caching pattern as Character Select+.
/// </summary>
public class EmoteModCache
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly string cacheFilePath;

    // In-memory cache
    private Dictionary<string, EmoteModCacheEntry>? memoryCache;
    private bool isInitialized;
    private readonly object cacheLock = new object();
    private bool isSaving = false;

    public bool IsInitialized => isInitialized;

    public int GetCachedModCount() => memoryCache?.Count ?? 0;

    public EmoteModCache(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.cacheFilePath = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "emote_mod_cache.json");
    }

    public EmoteModCacheEntry? GetCachedEntry(string modDirectory)
    {
        if (memoryCache == null)
            return null;

        return memoryCache.TryGetValue(modDirectory, out var entry) ? entry : null;
    }

    public bool HasCachedEntry(string modDirectory)
    {
        return memoryCache?.ContainsKey(modDirectory) ?? false;
    }

    public List<EmoteModInfo> GetCachedEmoteMods()
    {
        var result = new List<EmoteModInfo>();

        if (memoryCache == null)
            return result;

        foreach (var (modDir, entry) in memoryCache)
        {
            if (entry.IsEmoteMod)
            {
                // Clone lists to prevent callers from mutating cache data
                result.Add(new EmoteModInfo
                {
                    ModDirectory = modDir,
                    ModName = entry.Name,
                    AffectedEmotes = new List<string>(entry.AffectedEmotes),
                    EmoteCommands = new List<string>(entry.EmoteCommands),
                    PrimaryEmote = entry.PrimaryEmote,
                    EmoteCommand = entry.PrimaryCommand,
                    AnimationType = (EmoteDetectionService.AnimationType)entry.AnimationType,
                    PoseAnimationType = (EmoteDetectionService.AnimationType)entry.PoseAnimationType,
                    PoseIndex = entry.PoseIndex,
                    AffectedPoseIndices = new List<int>(entry.AffectedPoseIndices ?? new List<int>()),
                    PoseTypeIndices = entry.PoseTypeIndices != null
                        ? entry.PoseTypeIndices.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value))
                        : new Dictionary<int, List<int>>()
                });
            }
        }

        return result;
    }

    // Thread-safe
    public void UpdateEntry(string modDirectory, string modName, EmoteModInfo? emoteInfo)
    {
        lock (cacheLock)
        {
            memoryCache ??= new Dictionary<string, EmoteModCacheEntry>();

            var entry = new EmoteModCacheEntry
            {
                Name = modName,
                IsEmoteMod = emoteInfo != null,
                AffectedEmotes = emoteInfo?.AffectedEmotes ?? new List<string>(),
                EmoteCommands = emoteInfo?.EmoteCommands ?? new List<string>(),
                PrimaryEmote = emoteInfo?.PrimaryEmote ?? "",
                PrimaryCommand = emoteInfo?.EmoteCommand ?? "",
                AnimationType = (int)(emoteInfo?.AnimationType ?? EmoteDetectionService.AnimationType.Emote),
                PoseAnimationType = (int)(emoteInfo?.PoseAnimationType ?? EmoteDetectionService.AnimationType.None),
                PoseIndex = emoteInfo?.PoseIndex ?? -1,
                AffectedPoseIndices = emoteInfo?.AffectedPoseIndices != null ? new List<int>(emoteInfo.AffectedPoseIndices) : new List<int>(),
                PoseTypeIndices = emoteInfo?.PoseTypeIndices != null
                    ? emoteInfo.PoseTypeIndices.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value))
                    : new Dictionary<int, List<int>>(),
                LastSeen = DateTime.UtcNow
            };

            memoryCache[modDirectory] = entry;
        }
    }

    public void RemoveEntry(string modDirectory)
    {
        memoryCache?.Remove(modDirectory);
    }

    public EmoteModCacheData? LoadFromDisk()
    {
        try
        {
            if (!File.Exists(cacheFilePath))
                return null;

            var json = File.ReadAllText(cacheFilePath);
            var cacheData = JsonSerializer.Deserialize<EmoteModCacheData>(json);

            if (cacheData != null)
            {
                // Reject outdated cache versions (forces rescan with updated emote dictionary)
                if (cacheData.Version < EmoteModCacheData.CurrentVersion)
                {
                    log.Information($"[EmoteCache] Cache version {cacheData.Version} is outdated (current: {EmoteModCacheData.CurrentVersion}), will rescan");
                    return null;
                }
                log.Information($"[EmoteCache] Loaded cache from disk (version {cacheData.Version}, {cacheData.Mods.Count} mods)");
            }

            return cacheData;
        }
        catch (Exception ex)
        {
            log.Warning($"[EmoteCache] Failed to load cache from disk: {ex.Message}");
            return null;
        }
    }

    // Uses locking and retry for safe concurrent access
    public void SaveToDisk()
    {
        if (memoryCache == null)
            return;

        // Prevent concurrent saves
        lock (cacheLock)
        {
            if (isSaving)
                return;
            isSaving = true;
        }

        try
        {
            // Copy data under lock to prevent modification during serialization
            Dictionary<string, EmoteModCacheEntry> cacheCopy;
            lock (cacheLock)
            {
                cacheCopy = new Dictionary<string, EmoteModCacheEntry>(memoryCache);
            }

            var cacheData = new EmoteModCacheData
            {
                Version = EmoteModCacheData.CurrentVersion,
                LastUpdated = DateTime.UtcNow,
                Mods = cacheCopy
            };

            var json = JsonSerializer.Serialize(cacheData, new JsonSerializerOptions { WriteIndented = true });

            // Retry logic for file write
            int retries = 3;
            while (retries > 0)
            {
                try
                {
                    File.WriteAllText(cacheFilePath, json);
                    log.Information($"[EmoteCache] Saved cache to disk ({cacheCopy.Count} mods)");
                    break;
                }
                catch (IOException) when (retries > 1)
                {
                    retries--;
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            log.Error($"[EmoteCache] Failed to save cache to disk: {ex.Message}");
        }
        finally
        {
            lock (cacheLock)
            {
                isSaving = false;
            }
        }
    }

    public void InitializeFromDisk(EmoteModCacheData? diskCache, bool markInitialized = true)
    {
        memoryCache = new Dictionary<string, EmoteModCacheEntry>();

        if (diskCache?.Mods != null)
        {
            foreach (var (modDir, entry) in diskCache.Mods)
            {
                memoryCache[modDir] = entry;
            }
        }

        if (markInitialized)
        {
            isInitialized = true;
        }
    }

    public void SetInitialized()
    {
        isInitialized = true;
    }

    // Clears both memory and disk
    public void ClearCache()
    {
        memoryCache?.Clear();
        isInitialized = false;

        try
        {
            if (File.Exists(cacheFilePath))
            {
                File.Delete(cacheFilePath);
                log.Information("[EmoteCache] Cleared cache file from disk");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[EmoteCache] Failed to delete cache file: {ex.Message}");
        }
    }
}
