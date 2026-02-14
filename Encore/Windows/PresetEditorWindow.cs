using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Encore.Styles;
using Encore.Services;

namespace Encore.Windows;

public class PresetEditorWindow : Window
{
    public DancePreset? CurrentPreset { get; private set; }
    public bool Confirmed { get; set; }
    public bool IsNewPreset { get; private set; }

    // Editing state - only the essentials
    private string editName = "";
    private string editCommand = "";
    private uint? editIconId;
    private string editModDirectory = "";
    private string editModName = "";
    private string editEmoteCommand = "";  // The emote command to execute (editable)

    // Auto-detected values
    private List<string> detectedEmotes = new();
    private List<string> detectedEmoteCommands = new();
    private List<string> detectedConflicts = new();
    private int selectedEmoteIndex = 0;  // Which emote to use when there are multiple
    private bool useCustomEmoteCommand = false;  // User wants to override detected emote
    private EmoteDetectionService.AnimationType detectedAnimationType = EmoteDetectionService.AnimationType.Emote;
    private EmoteDetectionService.AnimationType detectedPoseAnimationType = EmoteDetectionService.AnimationType.None;  // For mixed mods
    private List<int> detectedPoseIndices = new();  // All pose indices detected for this mod
    private Dictionary<int, List<int>> detectedPoseTypeIndices = new();  // Per-type pose indices (AnimationType -> indices)
    private int selectedPoseIndex = -1;  // The pose index the user selected

    // Vanilla preset mode
    private bool isVanillaPreset = false;

    // Mod options editing
    private Dictionary<string, List<string>> editModOptions = new();
    private IReadOnlyDictionary<string, (string[] options, int groupType)>? availableModSettings;

    // Modifier editing
    private List<PresetModifier> editModifiers = new();
    private int expandedModifierIndex = -1;
    private int dragModifierSourceIndex = -1;
    private int renamingModifierIndex = -1;
    private string renamingModifierName = "";

    // Section collapse state
    private bool modSettingsOpen = true;
    private bool modifiersOpen = true;

    // Emote mods list
    private List<EmoteModInfo> emoteMods = new();
    private int selectedModIndex = -1;
    private string modSearchFilter = "";
    private List<EmoteModInfo> filteredMods = new();
    private bool needsRefresh = true;
    private bool isLoading = false;
    private string loadingStatus = "";

    // Custom icon path (uploaded image)
    private string? editCustomIconPath;

    // Icon picker reference
    private IconPickerWindow? iconPickerWindow;

    // Validation error messages
    private string? nameError = null;
    private string? commandError = null;  // Hard block (plugin conflicts, duplicate presets)
    private string? commandWarning = null;  // Soft warning (game command conflicts)
    private bool gameCommandWarningAcknowledged = false;  // User acknowledged the warning

    // Base sizes (before scaling)
    private const float BaseWidth = 500f;
    private const float BaseHeight = 570f;
    private const float BaseMaxWidth = 800f;
    private const float BaseMaxHeight = 800f;

    public PresetEditorWindow() : base("Create Dance Preset###EncorePresetEditor")
    {
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoDocking;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.Scale;

        // Apply scaled size on first use
        Size = new Vector2(BaseWidth * scale, BaseHeight * scale);

        // Dynamic size constraints based on current scale
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(BaseWidth * scale, BaseHeight * scale),
            MaximumSize = new Vector2(BaseMaxWidth * scale, BaseMaxHeight * scale)
        };
    }

    public void SetIconPicker(IconPickerWindow iconPicker)
    {
        iconPickerWindow = iconPicker;
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (needsRefresh && !isLoading)
        {
            RefreshEmoteModsAsync();
        }
    }

    public void RefreshEmoteModsAsync(bool forceRescan = false)
    {
        var emoteService = Plugin.Instance?.EmoteDetectionService;
        if (emoteService == null)
            return;

        // Don't start another refresh if already loading
        if (isLoading)
            return;

        isLoading = true;
        needsRefresh = false;
        loadingStatus = forceRescan ? "Rescanning all mods..." : "Loading mods...";

        // If force rescan, clear cache first
        if (forceRescan)
        {
            emoteService.ClearCacheAndRescan();
        }

        // Run on background thread
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // If force rescan, wait for initialization to restart
                if (forceRescan)
                {
                    // Give it a moment to clear and restart
                    await System.Threading.Tasks.Task.Delay(300);
                }

                // Wait for scan to FULLY complete - don't show partial results
                int waitCount = 0;
                int maxWait = 6000; // 10 minutes max (scanning can take a while with many mods)

                // Wait while initializing OR if we're doing a rescan and not yet started
                while (emoteService.IsInitializing || (forceRescan && !emoteService.IsInitialized && waitCount < 10))
                {
                    // Just update status, don't show partial mod list
                    if (waitCount % 10 == 0) // Every second
                    {
                        var currentCount = emoteService.GetCachedModCount();
                        Plugin.Framework.RunOnFrameworkThread(() =>
                        {
                            loadingStatus = $"Scanning... ({currentCount} mods processed)";
                        });
                    }

                    await System.Threading.Tasks.Task.Delay(100);
                    waitCount++;

                    if (waitCount >= maxWait)
                        break;
                }

                // Only get mods AFTER scan is fully complete
                var mods = emoteService.GetEmoteMods();

                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    emoteMods = mods;
                    ApplyModFilter();
                    isLoading = false;
                    loadingStatus = "";

                    if (mods.Count == 0)
                    {
                        loadingStatus = "No emote mods found";
                    }
                });
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Error loading mods: {ex.Message}");
                Plugin.Framework.RunOnFrameworkThread(() =>
                {
                    isLoading = false;
                    loadingStatus = $"Error: {ex.Message}";
                });
            }
        });
    }

    private void ApplyModFilter()
    {
        if (string.IsNullOrWhiteSpace(modSearchFilter))
        {
            filteredMods = emoteMods.ToList();
        }
        else
        {
            var filter = modSearchFilter.ToLowerInvariant();
            filteredMods = emoteMods
                .Where(m => m.ModName.ToLowerInvariant().Contains(filter) ||
                           m.PrimaryEmote.ToLowerInvariant().Contains(filter) ||
                           m.EmoteCommands.Any(c => c.ToLowerInvariant().Contains(filter)))
                .ToList();
        }

        // Maintain selection
        if (!string.IsNullOrEmpty(editModDirectory))
        {
            selectedModIndex = filteredMods.FindIndex(m => m.ModDirectory == editModDirectory);
        }
    }

    public void OpenNew()
    {
        CurrentPreset = null;
        IsNewPreset = true;
        Confirmed = false;

        editName = "";
        editCommand = "";
        editIconId = null;
        editCustomIconPath = null;
        editModDirectory = "";
        editModName = "";
        editEmoteCommand = "";
        useCustomEmoteCommand = false;
        detectedEmotes.Clear();
        detectedEmoteCommands.Clear();
        detectedConflicts.Clear();
        selectedModIndex = -1;
        selectedEmoteIndex = 0;
        modSearchFilter = "";
        nameError = null;
        commandError = null;
        commandWarning = null;
        gameCommandWarningAcknowledged = false;
        isVanillaPreset = false;
        editModOptions = new();
        availableModSettings = null;
        editModifiers = new();
        expandedModifierIndex = -1;
        renamingModifierIndex = -1;
        renamingModifierName = "";
        draftModifier = null;
        draftModifierName = "";
        modSettingsOpen = false;
        modifiersOpen = false;

        needsRefresh = true;
        IsOpen = true;
    }

    public void OpenEdit(DancePreset preset)
    {
        CurrentPreset = preset;
        IsNewPreset = false;
        Confirmed = false;

        editName = preset.Name;
        editCommand = preset.ChatCommand;
        editIconId = preset.IconId;
        editCustomIconPath = preset.CustomIconPath;
        editModDirectory = preset.ModDirectory;
        editModName = preset.ModName;
        editEmoteCommand = preset.EmoteCommand ?? "";  // Load the preset's emote command
        useCustomEmoteCommand = false;
        modSearchFilter = "";
        nameError = null;
        commandError = null;
        commandWarning = null;
        gameCommandWarningAcknowledged = false;
        isVanillaPreset = preset.IsVanilla;

        // Load mod options from preset (deep copy)
        editModOptions = new Dictionary<string, List<string>>();
        foreach (var (group, opts) in preset.ModOptions)
        {
            editModOptions[group] = new List<string>(opts);
        }

        // Deep copy modifiers
        editModifiers = new List<PresetModifier>();
        foreach (var m in preset.Modifiers)
            editModifiers.Add(m.Clone());
        expandedModifierIndex = -1;
        renamingModifierIndex = -1;
        renamingModifierName = "";
        draftModifier = null;
        draftModifierName = "";
        modSettingsOpen = false;
        modifiersOpen = false;

        // Load available settings from Penumbra
        availableModSettings = null;
        if (!isVanillaPreset && !string.IsNullOrEmpty(preset.ModDirectory))
        {
            availableModSettings = Plugin.Instance?.PenumbraService?.GetAvailableModSettings(preset.ModDirectory, preset.ModName);
        }

        // Re-detect info for the mod (but preserve editEmoteCommand)
        if (!isVanillaPreset)
        {
            UpdateDetectedInfo(preserveEmoteCommand: true);
        }

        needsRefresh = true;
        IsOpen = true;
    }

    private void UpdateDetectedInfo(bool preserveEmoteCommand = false)
    {
        // Create new lists instead of .Clear() to avoid corrupting cache data
        // (detectedEmotes/detectedEmoteCommands may be references to cached lists)
        detectedEmotes = new List<string>();
        detectedEmoteCommands = new List<string>();
        detectedConflicts = new List<string>();
        selectedEmoteIndex = 0;  // Reset selection
        detectedAnimationType = EmoteDetectionService.AnimationType.Emote;  // Reset to default
        detectedPoseAnimationType = EmoteDetectionService.AnimationType.None;  // Reset mixed-mod pose type
        detectedPoseIndices = new List<int>();  // Reset pose indices
        detectedPoseTypeIndices = new Dictionary<int, List<int>>();  // Reset per-type indices
        selectedPoseIndex = -1;  // Reset selected pose

        if (string.IsNullOrEmpty(editModDirectory))
            return;

        var emoteService = Plugin.Instance?.EmoteDetectionService;
        if (emoteService == null)
            return;

        // Get emote info from cache (fast)
        var modInfo = emoteService.AnalyzeMod(editModDirectory, editModName);
        if (modInfo != null)
        {
            detectedEmotes = modInfo.AffectedEmotes;
            detectedEmoteCommands = modInfo.EmoteCommands;
            detectedAnimationType = modInfo.AnimationType;
            detectedPoseAnimationType = modInfo.PoseAnimationType;
            // Populate pose indices from the mod's detected list (fallback to single PoseIndex)
            if (modInfo.AffectedPoseIndices.Count > 0)
                detectedPoseIndices = new List<int>(modInfo.AffectedPoseIndices);
            else if (modInfo.PoseIndex >= 0)
                detectedPoseIndices = new List<int> { modInfo.PoseIndex };
            else
                detectedPoseIndices = new List<int>();

            // Populate per-type pose indices
            if (modInfo.PoseTypeIndices.Count > 0)
                detectedPoseTypeIndices = modInfo.PoseTypeIndices.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));
            else
                detectedPoseTypeIndices = new Dictionary<int, List<int>>();

            // Auto-select pose: if editing existing preset, re-select its PoseIndex;
            // if single pose, auto-select; if multiple, default to first
            if (preserveEmoteCommand && CurrentPreset != null && CurrentPreset.PoseIndex >= 0 &&
                detectedPoseIndices.Contains(CurrentPreset.PoseIndex))
                selectedPoseIndex = CurrentPreset.PoseIndex;
            else if (detectedPoseIndices.Count > 0)
                selectedPoseIndex = detectedPoseIndices[0];
            else
                selectedPoseIndex = -1;

            // For new presets or when not preserving, auto-select the detected emote
            if (!preserveEmoteCommand)
            {
                if (detectedEmoteCommands.Count > 0)
                {
                    editEmoteCommand = detectedEmoteCommands[0];
                    selectedEmoteIndex = 0;
                }
                else
                {
                    editEmoteCommand = "";
                }
                useCustomEmoteCommand = false;
            }
            else
            {
                // When preserving, try to find the existing command in detected list
                if (!string.IsNullOrEmpty(editEmoteCommand) && detectedEmoteCommands.Count > 0)
                {
                    var idx = detectedEmoteCommands.FindIndex(c =>
                        string.Equals(c, editEmoteCommand, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        selectedEmoteIndex = idx;
                        useCustomEmoteCommand = false;
                    }
                    else
                    {
                        // Emote command not in detected list - it's a custom override
                        useCustomEmoteCommand = true;
                    }
                }
            }
        }

        // NOTE: Conflict detection is done at save time, not on click
        // This avoids freezing when selecting a mod with 8000+ mods installed
    }

    public override void Draw()
    {
        // Handle icon picker completion
        HandleIconPickerCompletion();

        UIStyles.PushMainWindowStyle();

        try
        {
            DrawContent();
        }
        finally
        {
            UIStyles.PopMainWindowStyle();
        }
    }

    private void DrawContent()
    {
        var windowSize = ImGui.GetWindowSize();

        // Name
        ImGui.Text("Preset Name:");
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##name", ref editName, 100))
        {
            ValidateUniqueness();
        }
        if (nameError != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), nameError);
        }

        ImGui.Spacing();

        // Command - show with "/" prefix
        ImGui.Text("Chat Command:");
        ImGui.SetNextItemWidth(-1);
        ImGui.Text("/");
        ImGui.SameLine(0, 0);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##command", ref editCommand, 50))
        {
            // Clean up: remove slashes, spaces, make lowercase
            editCommand = editCommand.TrimStart('/').Replace(" ", "").ToLowerInvariant();
            // Reset warning acknowledgment when command changes
            gameCommandWarningAcknowledged = false;
            ValidateUniqueness();
        }
        if (commandError != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), commandError);
        }
        else if (commandWarning != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.4f, 1f), commandWarning);
            ImGui.Checkbox("I understand, use this command anyway", ref gameCommandWarningAcknowledged);
        }

        ImGui.Spacing();

        // Icon (optional)
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Icon (optional):");
        ImGui.SameLine();
        bool hasIcon = false;
        if (!string.IsNullOrEmpty(editCustomIconPath) && File.Exists(editCustomIconPath))
        {
            var tex = GetCustomIcon(editCustomIconPath);
            if (tex != null)
            {
                var iconDisplaySize = new Vector2(24 * UIStyles.Scale, 24 * UIStyles.Scale);
                Vector2 uv0 = Vector2.Zero, uv1 = Vector2.One;
                if (tex.Width > tex.Height) {
                    float excess = (tex.Width - tex.Height) / (2f * tex.Width);
                    uv0.X = excess; uv1.X = 1f - excess;
                } else if (tex.Height > tex.Width) {
                    float excess = (tex.Height - tex.Width) / (2f * tex.Height);
                    uv0.Y = excess; uv1.Y = 1f - excess;
                }
                ImGui.Image(tex.Handle, iconDisplaySize, uv0, uv1);
                ImGui.SameLine();
                hasIcon = true;
            }
        }
        else if (editIconId.HasValue)
        {
            var iconTexture = GetGameIcon(editIconId.Value);
            if (iconTexture != null)
            {
                ImGui.Image(iconTexture.Handle, new Vector2(24 * UIStyles.Scale, 24 * UIStyles.Scale));
                ImGui.SameLine();
                hasIcon = true;
            }
        }
        if (ImGui.Button("Choose"))
        {
            if (iconPickerWindow != null)
            {
                iconPickerWindow.Reset(editIconId);
                iconPickerWindow.IsOpen = true;
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Pick a game icon");
        }
        ImGui.SameLine();
        if (ImGui.Button("Upload"))
        {
            OpenCustomIconDialog();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Upload a custom image (PNG, JPG, GIF, WebP)");
        }
        if (hasIcon)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                editCustomIconPath = null;
                editIconId = null;
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Vanilla preset toggle
        if (ImGui.Checkbox("Use vanilla animation (disable mods only)", ref isVanillaPreset))
        {
            if (isVanillaPreset)
            {
                // Clear mod selection when switching to vanilla
                editModDirectory = "";
                editModName = "";
                selectedModIndex = -1;
                detectedEmotes.Clear();
                detectedEmoteCommands.Clear();
                // Keep editEmoteCommand - user needs to specify which emote
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Create a preset that disables conflicting dance mods\nwithout enabling any mod (uses vanilla animation)");
        }

        ImGui.Spacing();

        if (!isVanillaPreset)
        {
            // Mod Selection (only show when not vanilla)
            UIStyles.AccentSectionHeader("Select Dance Mod", new Vector4(0.4f, 0.7f, 1f, 1f));

        // Search
        var isSearchDisabled = isLoading;
        if (isSearchDisabled) ImGui.BeginDisabled();

        ImGui.SetNextItemWidth(-90);
        if (ImGui.InputText("##modSearch", ref modSearchFilter, 100))
        {
            ApplyModFilter();
        }
        ImGui.SameLine();

        // Single Refresh button - hold Shift for full rescan
        // Disable button while loading to prevent accidental double-clicks
        if (isLoading) ImGui.BeginDisabled();
        var buttonLabel = isLoading ? "Scanning..." : "Refresh";
        if (ImGui.Button(buttonLabel))
        {
            // Shift+Click = full rescan, normal click = just refresh from cache
            bool forceRescan = ImGui.GetIO().KeyShift;
            RefreshEmoteModsAsync(forceRescan);
        }
        if (isLoading) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (isLoading)
            {
                ImGui.Text("Scan in progress - please wait");
            }
            else
            {
                ImGui.Text("Click: Refresh mod list from cache");
                ImGui.Text("Shift+Click: Full rescan (clears cache)");
            }
            ImGui.EndTooltip();
        }

        if (isSearchDisabled) ImGui.EndDisabled();

        ImGui.Spacing();

        // Mod list
        var listHeight = 150f * UIStyles.Scale;
        if (ImGui.BeginChild("ModList", new Vector2(-1, listHeight), true))
        {
            if (isLoading)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 1f, 1f), loadingStatus);
                ImGui.TextDisabled("Please wait...");
            }
            else if (filteredMods.Count == 0)
            {
                if (emoteMods.Count == 0)
                {
                    ImGui.TextColored(new Vector4(1f, 0.6f, 0.4f, 1f), "No dance/emote mods found.");
                    ImGui.TextDisabled("Click Refresh (or Shift+Refresh to rescan)");
                }
                else
                {
                    ImGui.TextDisabled("No mods match the search.");
                }
            }
            else
            {
                // Get presets for "used" indicator
                var presets = Plugin.Instance?.Configuration.Presets;

                for (int i = 0; i < filteredMods.Count; i++)
                {
                    var mod = filteredMods[i];
                    var isSelected = mod.ModDirectory == editModDirectory;

                    // Check if this mod is used by any preset
                    var usedByPresets = presets?.Where(p => p.ModDirectory == mod.ModDirectory).ToList();
                    var isUsed = usedByPresets != null && usedByPresets.Count > 0;

                    // Show mod name with type info
                    var label = isUsed ? "\u2713 " : "";  // Checkmark prefix if used
                    label += mod.ModName;

                    // Check if it's a movement mod
                    if (mod.AnimationType == EmoteDetectionService.AnimationType.Movement)
                    {
                        label += " [Walk / Run]";
                    }
                    // Check if it's a pose mod
                    else if (mod.RequiresRedraw)
                    {
                        // Pose mod - show type and index
                        var poseTypeName = mod.AnimationType switch
                        {
                            EmoteDetectionService.AnimationType.StandingIdle => "Idle",
                            EmoteDetectionService.AnimationType.ChairSitting => "Sit",
                            EmoteDetectionService.AnimationType.GroundSitting => "GroundSit",
                            EmoteDetectionService.AnimationType.LyingDozing => "Doze",
                            _ => "Pose"
                        };
                        if (mod.AffectedPoseIndices.Count > 1)
                        {
                            label += $" [{poseTypeName} {string.Join(",", mod.AffectedPoseIndices.Select(i => $"#{i}"))}]";
                        }
                        else if (mod.PoseIndex >= 0)
                        {
                            label += $" [{poseTypeName} #{mod.PoseIndex}]";
                        }
                        else
                        {
                            label += $" [{poseTypeName}]";
                        }
                    }
                    else if (mod.EmoteCommands.Count > 0)
                    {
                        // Emote mod - show commands
                        label += $" [{string.Join(", ", mod.EmoteCommands)}]";
                    }
                    else
                    {
                        label += " [emote unknown]";
                    }

                    if (ImGui.Selectable(label, isSelected))
                    {
                        editModDirectory = mod.ModDirectory;
                        editModName = mod.ModName;
                        selectedModIndex = i;

                        // Auto-fill name if empty
                        if (string.IsNullOrEmpty(editName))
                        {
                            editName = mod.ModName;
                        }

                        // Auto-fill command if empty - use preset name as command
                        if (string.IsNullOrEmpty(editCommand))
                        {
                            // Use a sanitized version of the mod name
                            editCommand = SanitizeCommand(mod.ModName);
                        }

                        // Load available mod settings from Penumbra
                        availableModSettings = Plugin.Instance?.PenumbraService?.GetAvailableModSettings(mod.ModDirectory, mod.ModName);
                        // Reset mod options when selecting a new mod
                        editModOptions = new Dictionary<string, List<string>>();

                        UpdateDetectedInfo();
                    }

                    // Show tooltip with more details
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        if (mod.EmoteCommands.Count > 0)
                        {
                            ImGui.Text($"Detected emote(s): {string.Join(", ", mod.EmoteCommands)}");
                        }
                        else
                        {
                            ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "Could not auto-detect emote command.");
                            ImGui.Text("You'll need to set the emote manually after creating.");
                        }
                        if (mod.EmotePaths.Count > 0)
                        {
                            ImGui.Separator();
                            ImGui.TextDisabled($"Animation files: {mod.EmotePaths.Count}");
                        }
                        // Show which presets use this mod
                        if (isUsed && usedByPresets != null)
                        {
                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"Used by: {string.Join(", ", usedByPresets.Select(p => p.Name))}");
                        }
                        ImGui.EndTooltip();
                    }
                }
            }
        }
        ImGui.EndChild();
        } // End of !isVanillaPreset block for mod selection

        // Show detected info and emote selection
        if (isVanillaPreset)
        {
            // Vanilla preset - just show emote input (required)
            ImGui.Spacing();
            UIStyles.AccentSectionHeader("Emote to Use", new Vector4(0.4f, 0.9f, 0.5f, 1f));
            ImGui.Text("Emote command (required):");
            ImGui.SetNextItemWidth(200);
            ImGui.Text("/");
            ImGui.SameLine(0, 0);
            ImGui.SetNextItemWidth(180);
            if (ImGui.InputText("##vanillaEmote", ref editEmoteCommand, 50))
            {
                // Clean up: ensure it starts with /
                editEmoteCommand = "/" + editEmoteCommand.TrimStart('/').ToLowerInvariant();
            }
            ImGui.TextDisabled("This emote will play using the vanilla animation.");
            ImGui.TextDisabled("Conflicting dance mods will be temporarily disabled.");
        }
        else if (!string.IsNullOrEmpty(editModDirectory))
        {
            ImGui.Spacing();

            // Show different info based on animation type
            var isMovementMod = detectedAnimationType == EmoteDetectionService.AnimationType.Movement;
            var isPoseMod = !isMovementMod &&
                           detectedAnimationType != EmoteDetectionService.AnimationType.Emote &&
                           detectedAnimationType != EmoteDetectionService.AnimationType.None;

            if (isMovementMod)
            {
                // Movement mod — no emote/pose selection needed
                ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.6f, 1f), "Type: Movement (Walk / Sprint / Jog)");
                ImGui.TextDisabled("This mod changes your walking and movement animations.");
                ImGui.TextDisabled("No emote or pose selection needed — just enable and go.");
            }
            else if (isPoseMod)
            {
                // Pure pose mod — show type, pose index, and explain redraw behaviour
                var poseTypeName = detectedAnimationType switch
                {
                    EmoteDetectionService.AnimationType.StandingIdle => "Idle",
                    EmoteDetectionService.AnimationType.ChairSitting => "Sit",
                    EmoteDetectionService.AnimationType.GroundSitting => "Ground Sit",
                    EmoteDetectionService.AnimationType.LyingDozing => "Doze",
                    _ => "Pose"
                };

                if (detectedPoseIndices.Count > 1)
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"Type: {poseTypeName} (will redraw)");
                    ImGui.Text("Pose to use:");
                    ImGui.SetNextItemWidth(200);
                    var poseOptions = detectedPoseIndices.Select(i => $"Pose #{i}").ToArray();
                    var currentIdx = detectedPoseIndices.IndexOf(selectedPoseIndex);
                    if (currentIdx < 0) currentIdx = 0;
                    if (ImGui.Combo("##poseSelect", ref currentIdx, poseOptions, poseOptions.Length))
                    {
                        selectedPoseIndex = detectedPoseIndices[currentIdx];
                    }
                    ImGui.TextDisabled($"This mod affects poses: {string.Join(", ", detectedPoseIndices.Select(i => $"#{i}"))}");
                }
                else if (detectedPoseIndices.Count == 1)
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"Type: {poseTypeName} #{detectedPoseIndices[0]} (will redraw)");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"Type: {poseTypeName} (will redraw)");
                }
                ImGui.TextDisabled("Pose mods require a Penumbra redraw to take effect");
            }
            else
            {
                // Emote mod (or mixed mod) - show emote selection
                ImGui.Text("Emote to execute:");

                if (detectedEmoteCommands.Count > 0 && !useCustomEmoteCommand)
                {
                    ImGui.SetNextItemWidth(200);
                    var emoteOptions = detectedEmoteCommands.ToArray();
                    if (selectedEmoteIndex >= emoteOptions.Length)
                        selectedEmoteIndex = 0;

                    if (ImGui.Combo("##emoteSelect", ref selectedEmoteIndex, emoteOptions, emoteOptions.Length))
                    {
                        editEmoteCommand = detectedEmoteCommands[selectedEmoteIndex];
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Custom"))
                    {
                        useCustomEmoteCommand = true;
                        if (string.IsNullOrEmpty(editEmoteCommand) && detectedEmoteCommands.Count > 0)
                        {
                            editEmoteCommand = detectedEmoteCommands[0];
                        }
                    }
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("Enter a custom emote command");
                    }
                }
                else
                {
                    ImGui.SetNextItemWidth(200);
                    ImGui.Text("/");
                    ImGui.SameLine(0, 0);
                    ImGui.SetNextItemWidth(180);
                    if (ImGui.InputText("##customEmote", ref editEmoteCommand, 50))
                    {
                        editEmoteCommand = "/" + editEmoteCommand.TrimStart('/').ToLowerInvariant();
                    }

                    if (detectedEmoteCommands.Count > 0)
                    {
                        ImGui.SameLine();
                        if (ImGui.Button("Use Detected"))
                        {
                            useCustomEmoteCommand = false;
                            if (selectedEmoteIndex < detectedEmoteCommands.Count)
                            {
                                editEmoteCommand = detectedEmoteCommands[selectedEmoteIndex];
                            }
                        }
                    }
                    else
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "(auto-detect failed)");
                    }
                }

                // Auto-detect pose commands: if user selected /sit, /groundsit, /doze, or /cpose,
                // show the pose picker so they can choose which pose number.
                // Use per-type indices when available to filter to only relevant poses.
                var selectedCommandAnimType = GetAnimationTypeForCommand(editEmoteCommand);
                if (selectedCommandAnimType != EmoteDetectionService.AnimationType.Emote)
                {
                    // Get pose indices for this specific type, falling back to all indices
                    var typeKey = (int)selectedCommandAnimType;
                    var relevantIndices = (detectedPoseTypeIndices.Count > 0 && detectedPoseTypeIndices.TryGetValue(typeKey, out var typeIndices))
                        ? typeIndices
                        : detectedPoseIndices;

                    if (relevantIndices.Count > 0)
                    {
                        ImGui.Spacing();
                        var poseTypeName = selectedCommandAnimType switch
                        {
                            EmoteDetectionService.AnimationType.StandingIdle => "Idle",
                            EmoteDetectionService.AnimationType.ChairSitting => "Sit",
                            EmoteDetectionService.AnimationType.GroundSitting => "Ground Sit",
                            EmoteDetectionService.AnimationType.LyingDozing => "Doze",
                            _ => "Pose"
                        };

                        if (relevantIndices.Count > 1)
                        {
                            ImGui.Text($"{poseTypeName} pose to use:");
                            ImGui.SetNextItemWidth(200);
                            var poseOptions = relevantIndices.Select(i => $"Pose #{i}").ToArray();
                            var currentIdx = relevantIndices.IndexOf(selectedPoseIndex);
                            if (currentIdx < 0) { currentIdx = 0; selectedPoseIndex = relevantIndices[0]; }
                            if (ImGui.Combo("##poseSelect", ref currentIdx, poseOptions, poseOptions.Length))
                            {
                                selectedPoseIndex = relevantIndices[currentIdx];
                            }
                        }
                        else if (relevantIndices.Count == 1)
                        {
                            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"{poseTypeName} pose #{relevantIndices[0]}");
                            selectedPoseIndex = relevantIndices[0];
                        }
                    }
                }
            }

        }

        // Mod Options section (only for non-vanilla presets with a mod selected that has settings)
        if (!isVanillaPreset && !string.IsNullOrEmpty(editModDirectory) && availableModSettings != null && availableModSettings.Count > 0)
        {
            if (UIStyles.CollapsibleAccentSectionHeader("Mod Settings", new Vector4(0.7f, 0.5f, 1f, 1f), ref modSettingsOpen))
            {
                foreach (var (groupName, (options, groupType)) in availableModSettings)
                {
                    if (options.Length == 0)
                        continue;

                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.8f, 1f), groupName);

                    // Get current selection for this group
                    editModOptions.TryGetValue(groupName, out var currentSelection);

                    if (groupType == 0)
                    {
                        // Single-select (radio-button style via combo)
                        var selectedOption = currentSelection?.Count > 0 ? currentSelection[0] : "";
                        var previewLabel = string.IsNullOrEmpty(selectedOption) ? "(default)" : selectedOption;

                        ImGui.SetNextItemWidth(250 * UIStyles.Scale);
                        if (ImGui.BeginCombo($"##{groupName}", previewLabel))
                        {
                            // "(default)" option to clear selection
                            if (ImGui.Selectable("(default)", string.IsNullOrEmpty(selectedOption)))
                            {
                                editModOptions.Remove(groupName);
                            }

                            for (int i = 0; i < options.Length; i++)
                            {
                                var isOptionSelected = options[i] == selectedOption;
                                if (ImGui.Selectable(options[i], isOptionSelected))
                                {
                                    editModOptions[groupName] = new List<string> { options[i] };
                                }
                            }
                            ImGui.EndCombo();
                        }
                    }
                    else
                    {
                        // Multi-select (checkboxes)
                        var selectedSet = currentSelection != null
                            ? new HashSet<string>(currentSelection, StringComparer.OrdinalIgnoreCase)
                            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        bool changed = false;
                        for (int i = 0; i < options.Length; i++)
                        {
                            var isChecked = selectedSet.Contains(options[i]);
                            if (ImGui.Checkbox($"{options[i]}##{groupName}_{i}", ref isChecked))
                            {
                                if (isChecked)
                                    selectedSet.Add(options[i]);
                                else
                                    selectedSet.Remove(options[i]);
                                changed = true;
                            }
                        }

                        if (changed)
                        {
                            if (selectedSet.Count > 0)
                                editModOptions[groupName] = selectedSet.ToList();
                            else
                                editModOptions.Remove(groupName);
                        }
                    }

                    ImGui.Spacing();
                }
            }

            // Modifiers section (when mod has settings, multiple emotes, or multiple poses)
            DrawModifiersSection();
        }
        else if (!isVanillaPreset && !string.IsNullOrEmpty(editModDirectory))
        {
            // Show modifiers even without mod settings (for mods with multiple emotes/poses)
            DrawModifiersSection();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Buttons
        DrawButtons(windowSize);
    }

    // Draft modifier being configured before committing
    private PresetModifier? draftModifier = null;
    private string draftModifierName = "";

    private static readonly Vector4 ModifierAccentColor = new Vector4(0.9f, 0.65f, 0.2f, 1f);

    private bool HasModifierRelevantContent()
    {
        // Mod has option groups to override
        if (availableModSettings != null && availableModSettings.Count > 0)
            return true;
        // Mod has multiple emotes to choose from
        if (detectedEmoteCommands.Count > 1)
            return true;
        // Mod has multiple pose indices
        if (detectedPoseIndices.Count > 1)
            return true;
        return false;
    }

    private void DrawModifiersSection()
    {
        if (!HasModifierRelevantContent())
            return;

        if (!UIStyles.CollapsibleAccentSectionHeader("Modifiers", ModifierAccentColor, ref modifiersOpen))
            return;

        var scale = UIStyles.Scale;
        var drawList = ImGui.GetWindowDrawList();

        // Description (always visible)
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f),
            "Switch between different mod options without creating separate presets.");

        // Live usage examples
        if (editModifiers.Count > 0 && !string.IsNullOrWhiteSpace(editCommand))
        {
            var examples = string.Join("  ", editModifiers.Select(m => $"/{editCommand} {m.Name}"));
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), examples);
        }

        ImGui.Spacing();

        // Rounded tab styling — cool neutrals, no warm tints
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 6f * scale);
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.14f, 0.14f, 0.15f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.22f, 0.22f, 0.24f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.18f, 0.20f, 0.95f));

        // Use channel splitting to draw background behind content
        var boxStartPos = ImGui.GetCursorScreenPos();
        var boxPadding = 8f * scale;
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1); // Content first

        // Indent content for the box padding
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + boxPadding);
        ImGui.PushItemWidth(-boxPadding);
        ImGui.BeginGroup();

        ImGui.Spacing();

        // Tab bar with overflow handling and trailing + button
        var hasAnyTabs = editModifiers.Count > 0 || draftModifier != null;
        if (hasAnyTabs && ImGui.BeginTabBar("##modifierTabs", ImGuiTabBarFlags.FittingPolicyResizeDown | ImGuiTabBarFlags.NoCloseWithMiddleMouseButton))
        {
            int deleteIndex = -1;
            int dragTargetIdx = -1;

            for (int i = 0; i < editModifiers.Count; i++)
            {
                var modifier = editModifiers[i];
                var tabLabel = $"{modifier.Name}###modTab_{i}";

                var tabFlags = ImGuiTabItemFlags.None;
                if (expandedModifierIndex == i)
                {
                    tabFlags |= ImGuiTabItemFlags.SetSelected;
                    expandedModifierIndex = -1;
                }

                var isSelected = ImGui.BeginTabItem(tabLabel, tabFlags);

                // Right-click context menu on the tab
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup($"##tabCtx_{i}");
                if (ImGui.BeginPopup($"##tabCtx_{i}"))
                {
                    if (ImGui.MenuItem("Rename"))
                    {
                        renamingModifierIndex = i;
                        renamingModifierName = modifier.Name;
                    }
                    if (ImGui.MenuItem("Delete"))
                        deleteIndex = i;
                    ImGui.EndPopup();
                }

                // Drag and drop for tab reordering
                var tabMin = ImGui.GetItemRectMin();
                var tabMax = ImGui.GetItemRectMax();
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
                {
                    dragModifierSourceIndex = i;
                    ImGui.SetDragDropPayload("MODIFIER_TAB", ReadOnlySpan<byte>.Empty, ImGuiCond.None);
                    ImGui.Text(modifier.Name);
                    ImGui.EndDragDropSource();
                }
                if (dragModifierSourceIndex >= 0 && dragModifierSourceIndex != i &&
                    ImGui.IsMouseHoveringRect(tabMin, tabMax) && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    dragTargetIdx = i;
                }

                if (isSelected)
                {
                    // Inline rename field
                    if (renamingModifierIndex == i)
                    {
                        ImGui.Text("Name:");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(150 * scale);
                        var renameEnter = ImGui.InputText($"##renameMod_{i}", ref renamingModifierName, 30,
                            ImGuiInputTextFlags.EnterReturnsTrue);

                        var renameValid = !string.IsNullOrWhiteSpace(renamingModifierName) &&
                                          !editModifiers.Where((m, idx) => idx != i)
                                              .Any(m => string.Equals(m.Name, renamingModifierName, StringComparison.OrdinalIgnoreCase));

                        ImGui.SameLine();
                        if (!renameValid) ImGui.BeginDisabled();
                        if (ImGui.SmallButton("OK") || (renameEnter && renameValid))
                        {
                            modifier.Name = renamingModifierName.Trim();
                            renamingModifierIndex = -1;
                        }
                        if (!renameValid) ImGui.EndDisabled();

                        ImGui.SameLine();
                        if (ImGui.SmallButton("Cancel"))
                            renamingModifierIndex = -1;

                        ImGui.Spacing();
                    }

                    DrawModifierOptions(modifier);
                    ImGui.EndTabItem();
                }
            }

            // Draft tab (new modifier being configured)
            if (draftModifier != null)
            {
                var draftFlags = ImGuiTabItemFlags.None;
                if (expandedModifierIndex == -2)
                {
                    draftFlags |= ImGuiTabItemFlags.SetSelected;
                    expandedModifierIndex = -1;
                }

                var draftSelected = ImGui.BeginTabItem("New...###draftTab", draftFlags);

                // Right-click to cancel draft
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    ImGui.OpenPopup("##draftCtx");
                if (ImGui.BeginPopup("##draftCtx"))
                {
                    if (ImGui.MenuItem("Cancel"))
                    {
                        draftModifier = null;
                        draftModifierName = "";
                    }
                    ImGui.EndPopup();
                }

                if (draftSelected)
                {
                    if (draftModifier != null)
                    {
                        ImGui.Spacing();

                        // Options first
                        DrawModifierOptions(draftModifier);

                        ImGui.Spacing();
                        ImGui.Separator();
                        ImGui.Spacing();

                        // Name + confirm at the bottom
                        ImGui.Text("Modifier name:");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(150 * scale);
                        var enterPressed = ImGui.InputText("##draftModName", ref draftModifierName, 30,
                            ImGuiInputTextFlags.EnterReturnsTrue);

                        var canAdd = !string.IsNullOrWhiteSpace(draftModifierName) &&
                                     !editModifiers.Any(m => string.Equals(m.Name, draftModifierName, StringComparison.OrdinalIgnoreCase));

                        ImGui.SameLine();
                        if (!canAdd) ImGui.BeginDisabled();
                        if (ImGui.Button("Confirm") || (enterPressed && canAdd))
                        {
                            draftModifier.Name = draftModifierName.Trim();
                            editModifiers.Add(draftModifier);
                            expandedModifierIndex = editModifiers.Count - 1;
                            draftModifier = null;
                            draftModifierName = "";
                        }
                        if (!canAdd) ImGui.EndDisabled();

                        // Validation
                        if (!string.IsNullOrWhiteSpace(draftModifierName) &&
                                 editModifiers.Any(m => string.Equals(m.Name, draftModifierName, StringComparison.OrdinalIgnoreCase)))
                            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), "Name already used");
                    }

                    ImGui.EndTabItem();
                }
            }

            // Trailing + button to add new modifier (always visible in tab bar)
            if (draftModifier == null)
            {
                if (ImGui.TabItemButton("+", ImGuiTabItemFlags.Trailing))
                {
                    draftModifier = new PresetModifier();
                    draftModifierName = "";
                    expandedModifierIndex = -2;  // Signal to select draft tab
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Add modifier");
                }
            }

            ImGui.EndTabBar();

            if (deleteIndex >= 0)
                editModifiers.RemoveAt(deleteIndex);

            // Handle tab reorder from drag and drop
            if (dragTargetIdx >= 0 && dragModifierSourceIndex >= 0 && dragModifierSourceIndex != dragTargetIdx)
            {
                var item = editModifiers[dragModifierSourceIndex];
                editModifiers.RemoveAt(dragModifierSourceIndex);
                editModifiers.Insert(dragTargetIdx, item);
                expandedModifierIndex = dragTargetIdx;
                dragModifierSourceIndex = -1;
            }

            // Clear drag state on mouse release
            if (dragModifierSourceIndex >= 0 && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                dragModifierSourceIndex = -1;
        }

        // If no tabs yet, show centered empty state
        if (!hasAnyTabs)
        {
            ImGui.Spacing();
            ImGui.Spacing();

            var regionWidth = ImGui.GetContentRegionAvail().X;

            // Centered dimmed text
            var noModText = "No modifiers yet";
            var noModWidth = ImGui.CalcTextSize(noModText).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (regionWidth - noModWidth) * 0.5f);
            ImGui.TextDisabled(noModText);

            ImGui.Spacing();

            // Centered accent-styled button
            var btnLabel = "+ Add Modifier";
            var btnPadding = 24f * scale;
            var btnWidth = ImGui.CalcTextSize(btnLabel).X + btnPadding * 2;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (regionWidth - btnWidth) * 0.5f);

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(ModifierAccentColor.X * 0.15f, ModifierAccentColor.Y * 0.15f, ModifierAccentColor.Z * 0.15f, 0.8f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(ModifierAccentColor.X * 0.25f, ModifierAccentColor.Y * 0.25f, ModifierAccentColor.Z * 0.25f, 0.9f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(ModifierAccentColor.X * 0.3f, ModifierAccentColor.Y * 0.3f, ModifierAccentColor.Z * 0.3f, 0.9f));
            if (ImGui.Button(btnLabel, new Vector2(btnWidth, 0)))
            {
                draftModifier = new PresetModifier();
                draftModifierName = "";
                expandedModifierIndex = -2;
            }
            ImGui.PopStyleColor(3);

            ImGui.Spacing();
        }

        ImGui.Spacing();
        ImGui.EndGroup();
        ImGui.PopItemWidth();

        // Measure content bounds and draw background box behind it
        var boxEndPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var boxHeight = boxEndPos.Y - boxStartPos.Y;

        drawList.ChannelsSetCurrent(0); // Background
        var boxColor = new Vector4(ModifierAccentColor.X * 0.06f, ModifierAccentColor.Y * 0.06f, ModifierAccentColor.Z * 0.06f, 0.5f);
        var borderColor = new Vector4(ModifierAccentColor.X * 0.25f, ModifierAccentColor.Y * 0.25f, ModifierAccentColor.Z * 0.25f, 0.4f);
        var rounding = 6f * scale;
        drawList.AddRectFilled(
            boxStartPos,
            new Vector2(boxStartPos.X + availWidth, boxStartPos.Y + boxHeight),
            ImGui.ColorConvertFloat4ToU32(boxColor),
            rounding);
        drawList.AddRect(
            boxStartPos,
            new Vector2(boxStartPos.X + availWidth, boxStartPos.Y + boxHeight),
            ImGui.ColorConvertFloat4ToU32(borderColor),
            rounding,
            ImDrawFlags.None,
            1f);
        drawList.ChannelsMerge();

        ImGui.PopStyleColor(3); // Tab colors
        ImGui.PopStyleVar(1);   // TabRounding
    }

    private void DrawModifierOptions(PresetModifier modifier)
    {
        var modifierId = modifier.Name;
        if (string.IsNullOrEmpty(modifierId)) modifierId = "draft";

        // Emote override (only when mod has multiple emotes)
        if (detectedEmoteCommands.Count > 1)
        {
            var hasEmoteOverride = modifier.EmoteCommandOverride != null;
            var textColor = hasEmoteOverride
                ? new Vector4(0.5f, 0.9f, 0.9f, 1f)
                : new Vector4(0.5f, 0.5f, 0.5f, 1f);
            ImGui.TextColored(textColor, "Emote");

            var presetDefault = !string.IsNullOrEmpty(editEmoteCommand) ? editEmoteCommand : "(default)";
            var previewLabel = modifier.EmoteCommandOverride ?? $"(use preset default: {presetDefault})";

            ImGui.SetNextItemWidth(300 * UIStyles.Scale);
            if (ImGui.BeginCombo($"##emoteOverride_{modifierId}", previewLabel))
            {
                if (ImGui.Selectable($"(use preset default: {presetDefault})", modifier.EmoteCommandOverride == null))
                {
                    modifier.EmoteCommandOverride = null;
                    modifier.PoseIndexOverride = null;
                }

                for (int i = 0; i < detectedEmoteCommands.Count; i++)
                {
                    var cmd = detectedEmoteCommands[i];
                    var isSelected = cmd == modifier.EmoteCommandOverride;
                    if (ImGui.Selectable(cmd, isSelected))
                    {
                        modifier.EmoteCommandOverride = cmd;
                        modifier.PoseIndexOverride = null;
                    }
                }
                ImGui.EndCombo();
            }

            ImGui.Spacing();
        }

        // Pose index override — works with the effective command (override or base)
        // Shows whenever the mod has pose indices for the relevant animation type
        {
            var effectiveCmd = modifier.EmoteCommandOverride ?? editEmoteCommand;
            var effectiveAnimType = GetAnimationTypeForCommand(effectiveCmd);

            // Also check the base preset's detected type for pure pose mods (no emote dropdown)
            if (effectiveAnimType == EmoteDetectionService.AnimationType.Emote &&
                detectedAnimationType != EmoteDetectionService.AnimationType.Emote &&
                detectedAnimationType != EmoteDetectionService.AnimationType.None &&
                detectedAnimationType != EmoteDetectionService.AnimationType.Movement)
            {
                effectiveAnimType = detectedAnimationType;
            }

            if (effectiveAnimType != EmoteDetectionService.AnimationType.Emote)
            {
                var typeKey = (int)effectiveAnimType;
                var relevantIndices = (detectedPoseTypeIndices.Count > 0 && detectedPoseTypeIndices.TryGetValue(typeKey, out var typeIndices))
                    ? typeIndices
                    : detectedPoseIndices;

                if (relevantIndices.Count > 1)
                {
                    var poseTypeName = effectiveAnimType switch
                    {
                        EmoteDetectionService.AnimationType.StandingIdle => "Idle",
                        EmoteDetectionService.AnimationType.ChairSitting => "Sit",
                        EmoteDetectionService.AnimationType.GroundSitting => "Ground Sit",
                        EmoteDetectionService.AnimationType.LyingDozing => "Doze",
                        _ => "Pose"
                    };

                    var hasPoseOverride = modifier.PoseIndexOverride != null;
                    var poseTextColor = hasPoseOverride
                        ? new Vector4(0.5f, 0.9f, 0.9f, 1f)
                        : new Vector4(0.5f, 0.5f, 0.5f, 1f);
                    ImGui.TextColored(poseTextColor, $"{poseTypeName} Pose");

                    var baseDefault = $"Pose #{selectedPoseIndex}";
                    var currentPoseIdx = modifier.PoseIndexOverride ?? -1;
                    var previewPose = currentPoseIdx >= 0 ? $"Pose #{currentPoseIdx}" : $"(use preset default: {baseDefault})";

                    ImGui.SetNextItemWidth(300 * UIStyles.Scale);
                    if (ImGui.BeginCombo($"##modPose_{modifierId}", previewPose))
                    {
                        if (ImGui.Selectable($"(use preset default: {baseDefault})", modifier.PoseIndexOverride == null))
                        {
                            modifier.PoseIndexOverride = null;
                        }

                        for (int pi = 0; pi < relevantIndices.Count; pi++)
                        {
                            var idx = relevantIndices[pi];
                            var isPoseSelected = idx == currentPoseIdx;
                            if (ImGui.Selectable($"Pose #{idx}", isPoseSelected))
                            {
                                modifier.PoseIndexOverride = idx;
                            }
                        }
                        ImGui.EndCombo();
                    }

                    ImGui.Spacing();
                }
            }
        }

        // Mod option overrides
        if (availableModSettings == null) return;
        foreach (var (groupName, (options, groupType)) in availableModSettings)
        {
            if (options.Length == 0) continue;

            var hasOverride = modifier.OptionOverrides.ContainsKey(groupName);
            var textColor = hasOverride
                ? new Vector4(0.9f, 0.9f, 0.5f, 1f)
                : new Vector4(0.5f, 0.5f, 0.5f, 1f);
            ImGui.TextColored(textColor, groupName);

            modifier.OptionOverrides.TryGetValue(groupName, out var currentOverride);

            if (groupType == 0)
            {
                // Single-select combo
                var selectedOption = currentOverride?.Count > 0 ? currentOverride[0] : null;

                // Show what the preset default is for context
                string presetDefault = "(default)";
                if (editModOptions.TryGetValue(groupName, out var baseOpts) && baseOpts.Count > 0)
                    presetDefault = baseOpts[0];

                var previewLabel = selectedOption ?? $"(use preset default: {presetDefault})";

                ImGui.SetNextItemWidth(300 * UIStyles.Scale);
                if (ImGui.BeginCombo($"##{groupName}_mod", previewLabel))
                {
                    if (ImGui.Selectable($"(use preset default: {presetDefault})", selectedOption == null))
                    {
                        modifier.OptionOverrides.Remove(groupName);
                    }

                    for (int i = 0; i < options.Length; i++)
                    {
                        var isOptionSelected = options[i] == selectedOption;
                        if (ImGui.Selectable(options[i], isOptionSelected))
                        {
                            modifier.OptionOverrides[groupName] = new List<string> { options[i] };
                        }
                    }
                    ImGui.EndCombo();
                }
            }
            else
            {
                // Multi-select: inherit toggle then checkboxes
                var isInheriting = !hasOverride;
                if (ImGui.Checkbox($"Use preset default##{groupName}_inh", ref isInheriting))
                {
                    if (isInheriting)
                        modifier.OptionOverrides.Remove(groupName);
                    else
                        modifier.OptionOverrides[groupName] = new List<string>();
                }

                if (!isInheriting)
                {
                    var selectedSet = currentOverride != null
                        ? new HashSet<string>(currentOverride, StringComparer.OrdinalIgnoreCase)
                        : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    bool changed = false;
                    var contentMaxX = ImGui.GetWindowContentRegionMax().X;
                    for (int i = 0; i < options.Length; i++)
                    {
                        // Try to place on same line, wrap if it would overflow
                        var checkboxWidth = ImGui.CalcTextSize(options[i]).X + 30 * UIStyles.Scale;
                        if (i > 0)
                        {
                            ImGui.SameLine();
                            if (ImGui.GetCursorPosX() + checkboxWidth > contentMaxX)
                                ImGui.NewLine();
                        }
                        var isChecked = selectedSet.Contains(options[i]);
                        if (ImGui.Checkbox($"{options[i]}##{groupName}_mod_{i}", ref isChecked))
                        {
                            if (isChecked) selectedSet.Add(options[i]);
                            else selectedSet.Remove(options[i]);
                            changed = true;
                        }
                    }

                    if (changed)
                    {
                        modifier.OptionOverrides[groupName] = selectedSet.ToList();
                    }
                }
            }

            ImGui.Spacing();
        }
    }

    private void DrawButtons(Vector2 windowSize)
    {
        // Run validation
        ValidateUniqueness();
        bool isValid = IsFormValid();

        var scale = UIStyles.Scale;
        var buttonWidth = 80f * scale;
        ImGui.SetCursorPosX(windowSize.X - buttonWidth * 2 - 24 * scale);

        if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
        {
            Confirmed = false;
            IsOpen = false;
        }

        ImGui.SameLine();

        if (!isValid) ImGui.BeginDisabled();

        UIStyles.PushAccentButtonStyle();
        if (ImGui.Button("Save", new Vector2(buttonWidth, 0)))
        {
            SavePreset();
            Confirmed = true;
            IsOpen = false;
        }
        UIStyles.PopAccentButtonStyle();

        if (!isValid) ImGui.EndDisabled();
    }

    private void HandleIconPickerCompletion()
    {
        if (iconPickerWindow == null || iconPickerWindow.IsOpen || !iconPickerWindow.Confirmed)
            return;

        editIconId = iconPickerWindow.SelectedIconId;
        editCustomIconPath = null;  // Game icon replaces custom icon
        iconPickerWindow.Confirmed = false;
    }

    private void SavePreset()
    {
        if (CurrentPreset == null)
        {
            CurrentPreset = new DancePreset();
        }

        CurrentPreset.Name = editName;
        CurrentPreset.ChatCommand = editCommand.TrimStart('/');
        CurrentPreset.IconId = editIconId;
        CurrentPreset.CustomIconPath = editCustomIconPath;
        CurrentPreset.ModDirectory = editModDirectory;
        CurrentPreset.ModName = editModName;

        // Use the edited emote command (with fallback to /dance)
        if (!string.IsNullOrWhiteSpace(editEmoteCommand))
        {
            // Ensure it starts with /
            CurrentPreset.EmoteCommand = editEmoteCommand.StartsWith("/")
                ? editEmoteCommand
                : "/" + editEmoteCommand;
        }
        else
        {
            CurrentPreset.EmoteCommand = "/dance";
        }

        // Movement mods need ExecuteEmote=true so the redraw runs on activation
        if (detectedAnimationType == EmoteDetectionService.AnimationType.Movement)
        {
            CurrentPreset.ExecuteEmote = true;
            CurrentPreset.EmoteCommand = "";
            CurrentPreset.AnimationType = (int)EmoteDetectionService.AnimationType.Movement;
            CurrentPreset.PoseIndex = -1;
        }
        else
        {
            CurrentPreset.ExecuteEmote = true;

            // Determine animation type: if the selected emote is a pose command (/sit, /groundsit, /doze),
            // use the corresponding pose animation type so the preset executes as a pose preset
            var commandAnimType = GetAnimationTypeForCommand(CurrentPreset.EmoteCommand);
            if (commandAnimType != EmoteDetectionService.AnimationType.Emote &&
                detectedPoseIndices.Count > 0)
            {
                CurrentPreset.AnimationType = (int)commandAnimType;
                CurrentPreset.PoseIndex = selectedPoseIndex;
            }
            else
            {
                CurrentPreset.AnimationType = (int)detectedAnimationType;
                CurrentPreset.PoseIndex = selectedPoseIndex;
            }
        }

        CurrentPreset.IsVanilla = isVanillaPreset;

        // Save mod options (deep copy)
        CurrentPreset.ModOptions = new Dictionary<string, List<string>>();
        foreach (var (group, opts) in editModOptions)
        {
            CurrentPreset.ModOptions[group] = new List<string>(opts);
        }

        // Save modifiers (deep copy)
        CurrentPreset.Modifiers = new List<PresetModifier>();
        foreach (var m in editModifiers)
            CurrentPreset.Modifiers.Add(m.Clone());
    }

    private static EmoteDetectionService.AnimationType GetAnimationTypeForCommand(string? command)
    {
        return command?.ToLowerInvariant() switch
        {
            "/cpose" => EmoteDetectionService.AnimationType.StandingIdle,
            "/sit" => EmoteDetectionService.AnimationType.ChairSitting,
            "/groundsit" => EmoteDetectionService.AnimationType.GroundSitting,
            "/doze" => EmoteDetectionService.AnimationType.LyingDozing,
            _ => EmoteDetectionService.AnimationType.Emote
        };
    }

    private string SanitizeCommand(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        // Remove special characters, keep only letters and numbers
        var result = new System.Text.StringBuilder();
        foreach (var c in input.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                result.Append(c);
        }

        var cmd = result.ToString();

        // Limit length
        if (cmd.Length > 20)
            cmd = cmd.Substring(0, 20);

        return cmd;
    }

    private IDalamudTextureWrap? GetGameIcon(uint iconId)
    {
        try
        {
            var texture = Plugin.TextureProvider.GetFromGameIcon(iconId);
            return texture?.GetWrapOrEmpty();
        }
        catch
        {
            return null;
        }
    }

    private IDalamudTextureWrap? GetCustomIcon(string path)
    {
        try
        {
            var texture = Plugin.TextureProvider.GetFromFile(path);
            return texture?.GetWrapOrEmpty();
        }
        catch
        {
            return null;
        }
    }

    private void OpenCustomIconDialog()
    {
        var browser = Plugin.Instance?.FileBrowserWindow;
        if (browser == null) return;

        browser.OnFileSelected = (sourcePath) =>
        {
            try
            {
                var destName = $"{Guid.NewGuid()}.png";
                var destPath = Path.Combine(Plugin.IconsDirectory, destName);

                // Resize to max 128x128 for crisp icon display (avoids crunchy downscaling)
                using var image = Image.Load(sourcePath);
                const int maxSize = 128;
                if (image.Width > maxSize || image.Height > maxSize)
                {
                    image.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size(maxSize, maxSize),
                        Mode = ResizeMode.Max
                    }));
                }
                image.SaveAsPng(destPath);

                editCustomIconPath = destPath;
                editIconId = null;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to copy icon: {ex.Message}");
            }
        };
        browser.Open();
    }

    private void ValidateUniqueness()
    {
        nameError = null;
        commandError = null;

        var presets = Plugin.Instance?.Configuration.Presets;
        if (presets == null)
            return;

        var currentId = CurrentPreset?.Id;

        // Check for duplicate name
        if (!string.IsNullOrWhiteSpace(editName))
        {
            var duplicateName = presets.FirstOrDefault(p =>
                p.Id != currentId &&
                string.Equals(p.Name, editName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (duplicateName != null)
            {
                nameError = "A preset with this name already exists";
            }
        }

        // Check for command conflicts
        if (!string.IsNullOrWhiteSpace(editCommand))
        {
            var cleanCommand = editCommand.TrimStart('/').ToLowerInvariant();

            // 1. Check for duplicate within our own presets
            var duplicateCommand = presets.FirstOrDefault(p =>
                p.Id != currentId &&
                string.Equals(p.ChatCommand, cleanCommand, StringComparison.OrdinalIgnoreCase));

            if (duplicateCommand != null)
            {
                commandError = $"Command already used by preset '{duplicateCommand.Name}'";
                commandWarning = null;
                return;
            }

            // 2. Check for Encore's own reserved commands
            if (cleanCommand == "encore" || cleanCommand == "encorereset")
            {
                commandError = "This command is reserved for Encore";
                commandWarning = null;
                return;
            }

            // 3. Check for other plugin commands (hard block)
            // Skip this check if we're editing and the command matches the current preset's command
            var isOwnCommand = !IsNewPreset &&
                CurrentPreset != null &&
                string.Equals(CurrentPreset.ChatCommand, cleanCommand, StringComparison.OrdinalIgnoreCase);

            var commandKey = $"/{cleanCommand}";
            if (!isOwnCommand && Plugin.CommandManager.Commands.ContainsKey(commandKey))
            {
                commandError = "Command already registered by Dalamud";
                commandWarning = null;
                return;
            }

            // 4. Check for known game commands (soft warning)
            if (EmoteDetectionService.IsKnownGameCommand(cleanCommand))
            {
                commandWarning = $"This will override the game's /{cleanCommand} command while active";
                // Reset acknowledgment if command changed
                if (commandWarning != null && !commandWarning.Contains(cleanCommand))
                {
                    gameCommandWarningAcknowledged = false;
                }
            }
            else
            {
                commandWarning = null;
            }
        }
        else
        {
            commandWarning = null;
        }
    }

    private bool IsFormValid()
    {
        // Name is always required
        if (string.IsNullOrWhiteSpace(editName))
            return false;

        // Vanilla presets need emote command but not mod directory
        if (isVanillaPreset)
        {
            if (string.IsNullOrWhiteSpace(editEmoteCommand))
                return false;
        }
        else
        {
            // Non-vanilla presets need mod directory
            if (string.IsNullOrWhiteSpace(editModDirectory))
                return false;
        }

        // Hard errors block saving
        if (nameError != null || commandError != null)
            return false;

        // Warnings require acknowledgment
        if (commandWarning != null && !gameCommandWarningAcknowledged)
            return false;

        return true;
    }
}
