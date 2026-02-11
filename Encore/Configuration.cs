using Dalamud.Configuration;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Encore;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public List<DancePreset> Presets { get; set; } = new();

    // Original priority
    public Dictionary<string, int> OriginalPriorities { get; set; } = new();

    // Group name -> selected options
    public Dictionary<string, Dictionary<string, List<string>>> OriginalModOptions { get; set; } = new();

    // Mods we turned on (were disabled before)
    public HashSet<string> ModsWeEnabled { get; set; } = new();

    // Mods we turned off (conflicted with preset)
    public HashSet<string> ModsWeDisabled { get; set; } = new();

    // Mod directory names that should never be disabled by conflict detection
    public HashSet<string> PinnedModDirectories { get; set; } = new();

    public string? ActivePresetId { get; set; }
    public string? ActivePresetCollectionId { get; set; }
    public int DefaultPriorityBoost { get; set; } = 20;
    public List<uint> FavoriteIconIds { get; set; } = new();
    public HashSet<string> FavoritePresetIds { get; set; } = new();
    public bool ShowChatMessages { get; set; } = false;

    // Delay in ms between priority changes and emote execution
    public int EmoteDelayMs { get; set; } = 100;

    public bool HasSeenHelp { get; set; } = false;
    public List<PresetFolder> Folders { get; set; } = new();
    public List<string> FolderOrder { get; set; } = new();
    // Opt-in: use game functions for sit/doze anywhere (sends position data to server)
    public bool AllowSitDozeAnywhere { get; set; } = false;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}

[Serializable]
public class PresetFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Folder";
    public bool IsCollapsed { get; set; } = false;
    public Vector3? Color { get; set; }
}

[Serializable]
public class DancePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Preset";

    // Without leading slash (e.g., "hips" for /hips)
    public string ChatCommand { get; set; } = "";

    public uint? IconId { get; set; }

    // Whether commands are registered for this preset
    public bool Enabled { get; set; } = true;

    // Target mod settings
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public int PriorityBoost { get; set; } = 20;

    // [Legacy] Absolute priority — only used if PriorityBoost is 0
    public int TargetPriority { get; set; } = 0;

    // Group name -> selected options
    public Dictionary<string, List<string>> ModOptions { get; set; } = new();

    // Emote
    public string EmoteCommand { get; set; } = "/dance";
    public bool ExecuteEmote { get; set; } = true;

    // 1=Emote, 2=StandingIdle, 3=ChairSitting, 4=GroundSitting, 5=LyingDozing
    public int AnimationType { get; set; } = 1;

    // Pose index (0-6) for pose mods, or -1 if not applicable
    public int PoseIndex { get; set; } = -1;

    // Poses require a Penumbra redraw to take effect
    public bool RequiresRedraw => AnimationType switch
    {
        2 => true, // StandingIdle
        3 => true, // ChairSitting
        4 => true, // GroundSitting
        5 => true, // LyingDozing
        _ => false
    };

    // Vanilla preset: only disables conflicts, doesn't enable any mod
    public bool IsVanilla { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? FolderId { get; set; }

    public DancePreset Clone()
    {
        return new DancePreset
        {
            Id = Guid.NewGuid().ToString(),
            Name = Name + " (Copy)",
            ChatCommand = "",
            IconId = IconId,
            Enabled = false,
            ModDirectory = ModDirectory,
            ModName = ModName,
            PriorityBoost = PriorityBoost,
            TargetPriority = TargetPriority,
            ModOptions = new Dictionary<string, List<string>>(ModOptions),
            EmoteCommand = EmoteCommand,
            ExecuteEmote = ExecuteEmote,
            AnimationType = AnimationType,
            PoseIndex = PoseIndex,
            IsVanilla = IsVanilla,
            CreatedAt = DateTime.UtcNow,
            FolderId = FolderId,
        };
    }
}
