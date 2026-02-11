using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
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
    private int selectedPoseIndex = -1;  // The pose index the user selected

    // Vanilla preset mode
    private bool isVanillaPreset = false;

    // Mod options editing
    private Dictionary<string, List<string>> editModOptions = new();
    private IReadOnlyDictionary<string, (string[] options, int groupType)>? availableModSettings;

    // Emote mods list
    private List<EmoteModInfo> emoteMods = new();
    private int selectedModIndex = -1;
    private string modSearchFilter = "";
    private List<EmoteModInfo> filteredMods = new();
    private bool needsRefresh = true;
    private bool isLoading = false;
    private string loadingStatus = "";

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
        ImGui.Text("Icon (optional):");
        ImGui.SameLine();
        if (editIconId.HasValue)
        {
            var iconTexture = GetGameIcon(editIconId.Value);
            if (iconTexture != null)
            {
                ImGui.Image(iconTexture.Handle, new Vector2(24 * UIStyles.Scale, 24 * UIStyles.Scale));
                ImGui.SameLine();
            }
        }
        if (ImGui.Button(editIconId.HasValue ? "Change" : "Choose"))
        {
            if (iconPickerWindow != null)
            {
                iconPickerWindow.Reset(editIconId);
                iconPickerWindow.IsOpen = true;
            }
        }
        if (editIconId.HasValue)
        {
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
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
            UIStyles.SectionHeader("Select Dance Mod");

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

                    // Check if it's a pose mod
                    if (mod.RequiresRedraw)
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
            UIStyles.SectionHeader("Emote to Use");
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
            var isPoseMod = detectedAnimationType != EmoteDetectionService.AnimationType.Emote &&
                           detectedAnimationType != EmoteDetectionService.AnimationType.None;

            if (isPoseMod)
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
                if (selectedPoseIndex >= 0)
                {
                    ImGui.TextDisabled($"Use /cpose to cycle to pose #{selectedPoseIndex}");
                }
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

                // Auto-detect pose commands: if user selected /sit, /groundsit, or /doze,
                // show the pose picker so they can choose which pose number
                var selectedCommandAnimType = GetAnimationTypeForCommand(editEmoteCommand);
                if (selectedCommandAnimType != EmoteDetectionService.AnimationType.Emote &&
                    detectedPoseIndices.Count > 0)
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

                    if (detectedPoseIndices.Count > 1)
                    {
                        ImGui.Text($"{poseTypeName} pose to use:");
                        ImGui.SetNextItemWidth(200);
                        var poseOptions = detectedPoseIndices.Select(i => $"Pose #{i}").ToArray();
                        var currentIdx = detectedPoseIndices.IndexOf(selectedPoseIndex);
                        if (currentIdx < 0) currentIdx = 0;
                        if (ImGui.Combo("##poseSelect", ref currentIdx, poseOptions, poseOptions.Length))
                        {
                            selectedPoseIndex = detectedPoseIndices[currentIdx];
                        }
                    }
                    else if (detectedPoseIndices.Count == 1)
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"{poseTypeName} pose #{detectedPoseIndices[0]}");
                        selectedPoseIndex = detectedPoseIndices[0];
                    }
                }
            }

        }

        // Mod Options section (only for non-vanilla presets with a mod selected that has settings)
        if (!isVanillaPreset && !string.IsNullOrEmpty(editModDirectory) && availableModSettings != null && availableModSettings.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.TreeNodeEx("Mod Settings", ImGuiTreeNodeFlags.DefaultOpen))
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

                ImGui.TreePop();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Buttons
        DrawButtons(windowSize);
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

        CurrentPreset.IsVanilla = isVanillaPreset;

        // Save mod options (deep copy)
        CurrentPreset.ModOptions = new Dictionary<string, List<string>>();
        foreach (var (group, opts) in editModOptions)
        {
            CurrentPreset.ModOptions[group] = new List<string>(opts);
        }
    }

    private static EmoteDetectionService.AnimationType GetAnimationTypeForCommand(string? command)
    {
        return command?.ToLowerInvariant() switch
        {
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
