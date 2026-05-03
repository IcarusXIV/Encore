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

    public List<Routine> Routines { get; set; } = new();

    // Original priority
    public Dictionary<string, int> OriginalPriorities { get; set; } = new();

    // Group name -> selected options
    public Dictionary<string, Dictionary<string, List<string>>> OriginalModOptions { get; set; } = new();

    // Mods we turned on (were disabled before)
    public HashSet<string> ModsWeEnabled { get; set; } = new();

    // Mods we turned off (conflicted with preset)
    public HashSet<string> ModsWeDisabled { get; set; } = new();

    // Mods with active temporary Penumbra settings ("collectionId|modDirectory" keys)
    public HashSet<string> ModsWithTempSettings { get; set; } = new();

    // bypass carriers; persists across plugin reload (game's PAP cache survives reload, not restart)
    public HashSet<ushort> UsedBypassCarrierIds { get; set; } = new();

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
    // Opt-in: bypass client-side emote unlock checks when Encore executes emotes
    public bool AllowUnlockedEmotes { get; set; } = false;
    public List<string> PinnedFileBrowserPaths { get; set; } = new();
    public string? LastBrowserDirectory { get; set; }
    public string LastSeenPatchNotesVersion { get; set; } = "";
    public bool ShowPatchNotesOnStartup { get; set; } = true;
    public bool ShowUpdateNotification { get; set; } = true;
    public bool IsMainWindowOpen { get; set; } = false;
    // window chrome ignores GlobalScale; fonts still scale
    public bool IgnoreGlobalScale { get; set; } = false;

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
    public string? ParentFolderId { get; set; }

    public bool IsRoutineFolder { get; set; } = false;
}

[Serializable]
public class DancePreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Preset";

    // Without leading slash (e.g., "hips" for /hips)
    public string ChatCommand { get; set; } = "";

    public uint? IconId { get; set; }
    public string? CustomIconPath { get; set; }
    public float IconZoom { get; set; } = 1.0f;
    public float IconOffsetX { get; set; } = 0f;
    public float IconOffsetY { get; set; } = 0f;

    // Whether commands are registered for this preset
    public bool Enabled { get; set; } = true;

    // Target mod settings
    public string ModDirectory { get; set; } = "";
    public string ModName { get; set; } = "";
    public int PriorityBoost { get; set; } = 20;

    // [legacy] absolute priority, only used if PriorityBoost is 0
    public int TargetPriority { get; set; } = 0;

    // Group name -> selected options
    public Dictionary<string, List<string>> ModOptions { get; set; } = new();

    // Emote
    public string EmoteCommand { get; set; } = "/dance";
    public bool ExecuteEmote { get; set; } = true;

    // 1=Emote, 2=StandingIdle, 3=ChairSitting, 4=GroundSitting, 5=LyingDozing, 6=Movement
    public int AnimationType { get; set; } = 1;

    // Pose index (0-6) for pose mods, or -1 if not applicable
    public int PoseIndex { get; set; } = -1;

    // Poses and movement mods require a Penumbra redraw to take effect
    public bool RequiresRedraw => AnimationType switch
    {
        2 => true, // StandingIdle
        3 => true, // ChairSitting
        4 => true, // GroundSitting
        5 => true, // LyingDozing
        6 => true, // Movement
        _ => false
    };

    // Vanilla preset: only disables conflicts, doesn't enable any mod
    public bool IsVanilla { get; set; } = false;

    // Emote unlock bypass: use carrier emote to play this mod's animation for locked emotes
    public bool EmoteLocked { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? FolderId { get; set; }

    // Named option-override variants (e.g., "slow", "fast")
    public List<PresetModifier> Modifiers { get; set; } = new();

    // Emote/pose names (lowercased) that should NOT disable conflicting mods when this preset activates.
    // Entries are matched against the mod's AffectedEmotes / EmoteCommands (without leading slash).
    public List<string> ConflictExclusions { get; set; } = new();

    // Simple Heels override applied while this preset is active (requires SimpleHeels plugin)
    public HeelsOffset? HeelsOffset { get; set; }

    public DancePreset Clone()
    {
        var clone = new DancePreset
        {
            Id = Guid.NewGuid().ToString(),
            Name = Name + " (Copy)",
            ChatCommand = "",
            IconId = IconId,
            CustomIconPath = CustomIconPath,
            IconZoom = IconZoom,
            IconOffsetX = IconOffsetX,
            IconOffsetY = IconOffsetY,
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
            EmoteLocked = EmoteLocked,
            CreatedAt = DateTime.UtcNow,
            FolderId = FolderId,
            Modifiers = new List<PresetModifier>(),
            ConflictExclusions = new List<string>(ConflictExclusions),
            HeelsOffset = HeelsOffset?.Clone(),
        };
        foreach (var m in Modifiers)
            clone.Modifiers.Add(m.Clone());
        return clone;
    }
}

// Rotation/pitch/roll in radians. Applied via SimpleHeels.RegisterPlayer (TempOffset).
[Serializable]
public class HeelsOffset
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Rotation { get; set; }
    public float Pitch { get; set; }
    public float Roll { get; set; }

    public HeelsOffset Clone() => new()
    {
        X = X, Y = Y, Z = Z,
        Rotation = Rotation, Pitch = Pitch, Roll = Roll,
    };

    public bool IsZero() =>
        MathF.Abs(X) < 0.0001f && MathF.Abs(Y) < 0.0001f && MathF.Abs(Z) < 0.0001f &&
        MathF.Abs(Rotation) < 0.0001f && MathF.Abs(Pitch) < 0.0001f && MathF.Abs(Roll) < 0.0001f;
}

public enum RoutineStepDuration
{
    Fixed = 0,          // Wait DurationSeconds then advance
    UntilLoopEnds = 1,  // Wait for the current emote's loop to end, then advance
    Forever = 2,        // Stay on this step indefinitely (last step only)
}

[Serializable]
public class RoutineStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PresetId { get; set; } = "";

    // Macro step: free-form FFXIV-style commands with /wait support, instead of a preset reference
    public bool IsMacroStep { get; set; } = false;
    public string MacroText { get; set; } = "";

    public RoutineStepDuration DurationKind { get; set; } = RoutineStepDuration.Fixed;
    public float DurationSeconds { get; set; } = 5f;

    // Optional expression/emote to fire on top of the preset/macro
    public string LayeredEmote { get; set; } = "";
    public float LayerDelaySeconds { get; set; } = 0f;
    // re-fire every few seconds; expressions otherwise fade after 3-6s
    public bool HoldExpression { get; set; } = true;

    // null = inherit preset's HeelsOffset
    public HeelsOffset? HeelsOverride { get; set; }

    // null/empty = base preset; otherwise matched against DancePreset.Modifiers (case-insensitive)
    public string? ModifierName { get; set; }

    public RoutineStep Clone() => new()
    {
        Id = Guid.NewGuid().ToString(),
        PresetId = PresetId,
        IsMacroStep = IsMacroStep,
        MacroText = MacroText,
        DurationKind = DurationKind,
        DurationSeconds = DurationSeconds,
        LayeredEmote = LayeredEmote,
        LayerDelaySeconds = LayerDelaySeconds,
        HoldExpression = HoldExpression,
        HeelsOverride = HeelsOverride?.Clone(),
        ModifierName = ModifierName,
    };
}

[Serializable]
public class Routine
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "New Routine";
    public string ChatCommand { get; set; } = "";
    public uint? IconId { get; set; }
    public string? CustomIconPath { get; set; }
    public float IconZoom { get; set; } = 1.0f;
    public float IconOffsetX { get; set; } = 0f;
    public float IconOffsetY { get; set; } = 0f;
    public bool Enabled { get; set; } = true;
    public bool RepeatLoop { get; set; } = false;
    public List<RoutineStep> Steps { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public string? FolderId { get; set; }

    public Routine Clone()
    {
        var clone = new Routine
        {
            Id = Guid.NewGuid().ToString(),
            Name = Name + " (Copy)",
            ChatCommand = "",
            IconId = IconId,
            CustomIconPath = CustomIconPath,
            IconZoom = IconZoom,
            IconOffsetX = IconOffsetX,
            IconOffsetY = IconOffsetY,
            Enabled = false,
            RepeatLoop = RepeatLoop,
            Steps = new List<RoutineStep>(),
            CreatedAt = DateTime.UtcNow,
            FolderId = FolderId,
        };
        foreach (var s in Steps)
            clone.Steps.Add(s.Clone());
        return clone;
    }
}

[Serializable]
public class PresetModifier
{
    public string Name { get; set; } = "";
    public string? EmoteCommandOverride { get; set; }
    public int? PoseIndexOverride { get; set; }
    public Dictionary<string, List<string>> OptionOverrides { get; set; } = new();

    public PresetModifier Clone()
    {
        var clone = new PresetModifier
        {
            Name = Name,
            EmoteCommandOverride = EmoteCommandOverride,
            PoseIndexOverride = PoseIndexOverride,
            OptionOverrides = new()
        };
        foreach (var (k, v) in OptionOverrides)
            clone.OptionOverrides[k] = new List<string>(v);
        return clone;
    }
}
