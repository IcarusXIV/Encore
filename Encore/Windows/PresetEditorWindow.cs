using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
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

    // Emote unlock bypass for locked emotes
    private bool editEmoteLocked = false;

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
    private bool modSelectOpen = true;
    private bool emoteOpen = true;
    private bool modSettingsOpen = true;
    private bool modifiersOpen = true;
    private bool conflictExclusionsOpen = false;
    private bool heelsOpen = false;

    // rotation/pitch/roll radians (UI shows degrees); HeelsGizmoTarget shared with world gizmo
    private bool editHeelsEnabled = false;
    private readonly HeelsGizmoTarget editHeels = new();

    // Conflict handling: emotes/poses (by AffectedEmotes name) to NOT disable other mods for
    private HashSet<string> editConflictExclusions = new(StringComparer.OrdinalIgnoreCase);

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
    private float editIconZoom = 1.0f;
    private float editIconOffsetX = 0f;
    private float editIconOffsetY = 0f;

    // Icon picker reference
    private IconPickerWindow? iconPickerWindow;

    // Validation error messages
    private string? nameError = null;
    private string? commandError = null;  // Hard block (plugin conflicts, duplicate presets)
    private string? commandWarning = null;  // Currently unused, kept for future soft warnings
    private bool gameCommandWarningAcknowledged = false;  // Currently unused, kept for future soft warnings

    private readonly bool[] tickerPrevArmed = new bool[7];
    private readonly double[] tickerPulseStart = { -1, -1, -1, -1, -1, -1, -1 };

    // Preview card kick: last edit snapshot + timestamp.
    private string previewLastName = "";
    private string previewLastCommand = "";
    private string previewLastMod = "";
    private uint? previewLastIcon;
    private string previewLastCustomIcon = "";
    private double previewKickStart = -1;

    private readonly Dictionary<string, double> cardRippleStart = new();

    // 0 = closed (>), pi/2 = open (v); lerps toward target each frame
    private readonly Dictionary<string, float> cardChevronAngle = new();

    // Mod list lock-in: last-selected dir + transition time.
    private string modListLastSelected = "";
    private double modListLockInStart = -1;

    // Icon tile hover reveal: eased 0..1 amount.
    private float iconHoverAnim;

    // Command prefix `/` chip: focus + keystroke flash times.
    private bool commandWasFocused;
    private double commandFocusFlashStart = -1;
    private string commandLastText = "";
    private double commandKeystrokeFlashStart = -1;

    private string modifierLastActiveKey = "";
    private double modifierTabSwitchStart = -1;

    // pendingClose lets the save kick animation play out before window closes
    private double saveClickTime = -1;
    private bool savePendingClose;
    private double savePendingCloseAt;

    private bool pendingScrollToTop;


    private const float BaseWidth = 500f;
    private const float BaseHeight = 570f;
    private const float BaseMaxWidth = 800f;
    private const float BaseMaxHeight = 800f;

    public PresetEditorWindow() : base("Create Dance Preset###EncorePresetEditor")
    {
        SizeCondition = ImGuiCond.FirstUseEver;
        // themed content child owns the scrollbar
        Flags = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.WindowScale;

        Size = new Vector2(BaseWidth * scale, BaseHeight * scale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(BaseWidth * scale, BaseHeight * scale),
            MaximumSize = new Vector2(BaseMaxWidth * scale, BaseMaxHeight * scale)
        };
        UIStyles.PushEncoreWindow();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.020f, 0.024f, 0.035f, 1f));
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor();
        UIStyles.PopEncoreWindow();
        base.PostDraw();
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
        // every section collapsed on fresh open (Setup ignores via alwaysOpen)
        modSelectOpen = false;
        emoteOpen = false;
        modSettingsOpen = false;
        modifiersOpen = false;
        heelsOpen = false;
        conflictExclusionsOpen = false;
        pendingScrollToTop = true;
    }

    public override void OnClose()
    {
        base.OnClose();
        DeactivateHeelsGizmo();
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
        editIconZoom = 1.0f;
        editIconOffsetX = 0f;
        editIconOffsetY = 0f;
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
        editEmoteLocked = false;
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
        conflictExclusionsOpen = false;
        heelsOpen = false;
        editHeelsEnabled = false;
        editHeels.X = editHeels.Y = editHeels.Z = 0f;
        editHeels.Rotation = editHeels.Pitch = editHeels.Roll = 0f;
        editConflictExclusions = new(StringComparer.OrdinalIgnoreCase);

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
        editIconZoom = preset.IconZoom;
        editIconOffsetX = preset.IconOffsetX;
        editIconOffsetY = preset.IconOffsetY;
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
        editEmoteLocked = preset.EmoteLocked;

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
        conflictExclusionsOpen = preset.ConflictExclusions.Count > 0;
        heelsOpen = preset.HeelsOffset != null;
        editHeelsEnabled = preset.HeelsOffset != null;
        editHeels.X = preset.HeelsOffset?.X ?? 0f;
        editHeels.Y = preset.HeelsOffset?.Y ?? 0f;
        editHeels.Z = preset.HeelsOffset?.Z ?? 0f;
        editHeels.Rotation = preset.HeelsOffset?.Rotation ?? 0f;
        editHeels.Pitch = preset.HeelsOffset?.Pitch ?? 0f;
        editHeels.Roll = preset.HeelsOffset?.Roll ?? 0f;
        editConflictExclusions = new HashSet<string>(preset.ConflictExclusions, StringComparer.OrdinalIgnoreCase);

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
        HandleIconPickerCompletion();

        UIStyles.PushMainWindowStyle();
        UIStyles.PushEncoreContent();

        // ribbon/marquee/footer butt edge-to-edge; content child pushes its own padding
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

        try
        {
            var scale = UIStyles.Scale;

            DrawRibbon();
            DrawMarquee();
            DrawTicker();

            float footerH = 48f * scale;
            float contentH = ImGui.GetContentRegionAvail().Y - footerH;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.047f, 0.055f, 0.075f, 1f));
            // 14px top/bottom; horizontal padding via Indent so per-card right gutter can match
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 14f * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, 5f * scale));

            if (ImGui.BeginChild("##presetEditorContent",
                    new Vector2(ImGui.GetContentRegionAvail().X, contentH), false,
                    ImGuiWindowFlags.AlwaysVerticalScrollbar))
            {
                if (pendingScrollToTop)
                {
                    ImGui.SetScrollY(0f);
                    pendingScrollToTop = false;
                }
                ImGui.Indent(EditorHorizPad * scale);
                DrawContent();
                ImGui.Unindent(EditorHorizPad * scale);
            }
            ImGui.EndChild();

            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();

            DrawFooter();
            DrawWindowCornerBrackets();
        }
        finally
        {
            ImGui.PopStyleVar(2);
            UIStyles.PopEncoreContent();
            UIStyles.PopMainWindowStyle();
        }
    }

    private static void PushSmallChipButtonStyle(bool isDestructive = false)
    {
        var accent = isDestructive
            ? new Vector4(0.93f, 0.48f, 0.48f, 1f)
            : new Vector4(0.49f, 0.65f, 0.85f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.09f, 0.10f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(accent.X * 0.20f + 0.09f, accent.Y * 0.20f + 0.10f, accent.Z * 0.20f + 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(accent.X * 0.30f + 0.09f, accent.Y * 0.30f + 0.10f, accent.Z * 0.30f + 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,        new Vector4(accent.X * 0.55f, accent.Y * 0.55f, accent.Z * 0.55f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,          accent);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f * UIStyles.Scale, 2f * UIStyles.Scale));
    }
    private static void PopSmallChipButtonStyle()
    {
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
    }

    private static void PushEncoreInputStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.125f, 0.140f, 0.180f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.150f, 0.168f, 0.210f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0.170f, 0.190f, 0.236f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,         new Vector4(0.184f, 0.208f, 0.259f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,           new Vector4(0.855f, 0.867f, 0.894f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TextSelectedBg, new Vector4(0.49f, 0.65f, 0.85f, 0.35f));
        ImGui.PushStyleColor(ImGuiCol.CheckMark,      new Vector4(0.49f, 0.65f, 0.85f, 1f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrab,     new Vector4(0.49f, 0.65f, 0.85f, 1f));
        ImGui.PushStyleColor(ImGuiCol.SliderGrabActive, new Vector4(0.65f, 0.77f, 0.92f, 1f));

        // Combo/dropdown chrome
        ImGui.PushStyleColor(ImGuiCol.PopupBg,        new Vector4(0.086f, 0.098f, 0.133f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header,         new Vector4(0.49f, 0.65f, 0.85f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,  new Vector4(0.49f, 0.65f, 0.85f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,   new Vector4(0.49f, 0.65f, 0.85f, 0.35f));

        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 0f);
        // Inputs ~24px tall with tight 2px vertical item spacing.
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
            new Vector2(8f * UIStyles.Scale, 3f * UIStyles.Scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,
            new Vector2(6f * UIStyles.Scale, 2f * UIStyles.Scale));
    }
    private static void PopEncoreInputStyle()
    {
        ImGui.PopStyleVar(7);
        ImGui.PopStyleColor(13);
    }

    private static readonly Vector4 PE_LabelColor = new(0.56f, 0.58f, 0.63f, 1f); // uppercase eyebrow dim
    // Accent color for input focus rings - matches the main window's MW_Accent.
    private static readonly Vector4 FieldFocusAccent = new(0.49f, 0.65f, 0.85f, 1f);
    private static void FocusRing() => UIStyles.DrawFocusRingOnLastItem(FieldFocusAccent);

    private void DrawContent()
    {
        PushEncoreInputStyle();
        try
        {
            DrawContentInner();
        }
        finally
        {
            PopEncoreInputStyle();
        }
    }

    private void DrawContentInner()
    {
        bool _setupDummy = true;
        BeginCard("01", "SETUP", Sec_Setup, "", Chr_TextFaint, ref _setupDummy, alwaysOpen: true);
        {
            var scale = UIStyles.Scale;
            var dl = ImGui.GetWindowDrawList();
            float iconColW = 84f * scale;
            float iconTileSize = 64f * scale;
            bool hasCustomIcon = !string.IsNullOrEmpty(editCustomIconPath) && File.Exists(editCustomIconPath);
            bool hasGameIcon = editIconId.HasValue;
            bool hasIcon = hasCustomIcon || hasGameIcon;

            //  LEFT COLUMN : icon picker 
            ImGui.BeginGroup();
            {
                // Center the 64px tile within the 84px column.
                float tileIndent = (iconColW - iconTileSize) * 0.5f;
                ImGui.Dummy(new Vector2(tileIndent, 1));
                ImGui.SameLine(0, 0);

                var tileStart = ImGui.GetCursorScreenPos();
                ImGui.InvisibleButton("##iconTile", new Vector2(iconTileSize, iconTileSize));
                bool tileHovered = ImGui.IsItemHovered();
                bool tileClicked = ImGui.IsItemClicked();

                var tileMin = tileStart;
                var tileMax = new Vector2(tileMin.X + iconTileSize, tileMin.Y + iconTileSize);

                // Filled bg.
                uint tileBg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.125f, 0.140f, 0.180f, 1f));
                dl.AddRectFilled(tileMin, tileMax, tileBg);

                if (!hasIcon)
                {
                    float target = tileHovered ? 1f : 0f;
                    iconHoverAnim += (target - iconHoverAnim)
                        * MathF.Min(1f, ImGui.GetIO().DeltaTime * 8f);
                    float expand = iconHoverAnim * 2.5f * scale;
                    var hoverBorderCol = new Vector4(
                        Chr_TextGhost.X + (Chr_TextDim.X - Chr_TextGhost.X) * iconHoverAnim,
                        Chr_TextGhost.Y + (Chr_TextDim.Y - Chr_TextGhost.Y) * iconHoverAnim,
                        Chr_TextGhost.Z + (Chr_TextDim.Z - Chr_TextGhost.Z) * iconHoverAnim,
                        1f);
                    DrawDashedRect(dl,
                        new Vector2(tileMin.X - expand, tileMin.Y - expand),
                        new Vector2(tileMax.X + expand, tileMax.Y + expand),
                        ImGui.ColorConvertFloat4ToU32(hoverBorderCol),
                        4f * scale, 3f * scale, 1f);
                    string q = "?";
                    var tf = Plugin.Instance?.TitleFont;
                    if (tf is { Available: true })
                    {
                        using (tf.Push())
                        {
                            var qSz = ImGui.CalcTextSize(q);
                            dl.AddText(
                                new Vector2(tileMin.X + (iconTileSize - qSz.X) * 0.5f,
                                            tileMin.Y + (iconTileSize - qSz.Y) * 0.5f),
                                ImGui.ColorConvertFloat4ToU32(Chr_TextGhost), q);
                        }
                    }
                }
                else
                {
                    // Draw the image + solid 1px accent border.
                    if (hasCustomIcon)
                    {
                        var tex = GetCustomIcon(editCustomIconPath!);
                        if (tex != null)
                        {
                            var (uv0, uv1) = MainWindow.CalcIconUV(tex.Width, tex.Height,
                                editIconZoom, editIconOffsetX, editIconOffsetY);
                            dl.AddImage(tex.Handle, tileMin, tileMax, uv0, uv1);
                        }
                    }
                    else if (hasGameIcon)
                    {
                        var tex = GetGameIcon(editIconId!.Value);
                        if (tex != null) dl.AddImage(tex.Handle, tileMin, tileMax);
                    }
                    dl.AddRect(tileMin, tileMax,
                        ImGui.ColorConvertFloat4ToU32(Chr_Accent), 0f, 0, 1f);
                }

                // Bottom-right corner notch (HTML: .preview-icon::after).
                uint notchCol = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.55f));
                float notchLen = 8f * scale;
                dl.AddLine(
                    new Vector2(tileMax.X, tileMax.Y - notchLen),
                    new Vector2(tileMax.X, tileMax.Y), notchCol, 1f);
                dl.AddLine(
                    new Vector2(tileMax.X - notchLen, tileMax.Y),
                    new Vector2(tileMax.X, tileMax.Y), notchCol, 1f);

                if (tileHovered)
                {
                    dl.AddRectFilled(tileMin, tileMax,
                        ImGui.ColorConvertFloat4ToU32(
                            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.07f)));
                }
                if (tileClicked && iconPickerWindow != null)
                {
                    iconPickerWindow.Reset(editIconId);
                    iconPickerWindow.IsOpen = true;
                }

                ImGui.Dummy(new Vector2(1, 6f * scale));
                PushSmallChipButtonStyle();
                if (ImGui.Button("Choose##icChoose", new Vector2(iconColW, 0)))
                {
                    if (iconPickerWindow != null)
                    {
                        iconPickerWindow.Reset(editIconId);
                        iconPickerWindow.IsOpen = true;
                    }
                }
                PopSmallChipButtonStyle();
                PushSmallChipButtonStyle();
                if (ImGui.Button("Upload##icUpload", new Vector2(iconColW, 0)))
                {
                    OpenCustomIconDialog();
                }
                PopSmallChipButtonStyle();
                if (hasIcon)
                {
                    PushSmallChipButtonStyle(isDestructive: true);
                    if (ImGui.Button("Clear##icClear", new Vector2(iconColW, 0)))
                    {
                        editCustomIconPath = null;
                        editIconId = null;
                    }
                    PopSmallChipButtonStyle();
                }
            }
            ImGui.EndGroup();

            ImGui.SameLine(0, 14f * scale);

            //  RIGHT COLUMN : fields 
            ImGui.BeginGroup();
            {
                // Preset Name
                DrawFieldLabel("PRESET NAME");
                ImGui.Dummy(new Vector2(1, 1f * scale));
                ImGui.SetNextItemWidth(CardInnerWidth());
                if (ImGui.InputText("##name", ref editName, 100)) ValidateUniqueness();
                FocusRing();
                if (nameError != null)
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), nameError);
                }

                ImGui.Dummy(new Vector2(1, 2f * scale));

                // Chat Command with prefix chip.
                DrawFieldLabel("CHAT COMMAND");
                ImGui.Dummy(new Vector2(1, 1f * scale));
                {
                    var rowStart = ImGui.GetCursorScreenPos();
                    float rowInnerW = CardInnerWidth();
                    float frameH = ImGui.GetFrameHeight();
                    float prefixW = 26f * scale;

                    var prefixMin = rowStart;
                    var prefixMax = new Vector2(rowStart.X + prefixW, rowStart.Y + frameH);

                    float focusBgBoost = 0f;
                    if (commandFocusFlashStart >= 0)
                    {
                        float fE = (float)(ImGui.GetTime() - commandFocusFlashStart);
                        const float fDur = 0.35f;
                        if (fE >= fDur) commandFocusFlashStart = -1;
                        else
                        {
                            float ft = fE / fDur;
                            focusBgBoost = ft < 0.4f
                                ? 0.14f * (ft / 0.4f)
                                : 0.14f * (1f - (ft - 0.4f) / 0.6f);
                        }
                    }
                    float keystrokeBorderBoost = 0f;
                    if (commandKeystrokeFlashStart >= 0)
                    {
                        float kE = (float)(ImGui.GetTime() - commandKeystrokeFlashStart);
                        const float kDur = 0.22f;
                        if (kE >= kDur) commandKeystrokeFlashStart = -1;
                        else keystrokeBorderBoost = (1f - kE / kDur) * 0.40f;
                    }

                    float chipBgA = 0.08f + focusBgBoost;
                    float chipBrA = 0.55f + keystrokeBorderBoost;
                    dl.AddRectFilled(prefixMin, prefixMax,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, chipBgA)));
                    dl.AddRect(prefixMin, prefixMax,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, MathF.Min(1f, chipBrA))),
                        0f, 0, 1f);
                    string slash = "/";
                    var slashSz = ImGui.CalcTextSize(slash);
                    dl.AddText(
                        new Vector2(prefixMin.X + (prefixW - slashSz.X) * 0.5f,
                                    prefixMin.Y + (frameH - slashSz.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(Chr_Accent), slash);

                    ImGui.SetCursorScreenPos(new Vector2(rowStart.X + prefixW, rowStart.Y));
                    ImGui.SetNextItemWidth(rowInnerW - prefixW);
                    if (ImGui.InputText("##command", ref editCommand, 50))
                    {
                        editCommand = editCommand.TrimStart('/').Replace(" ", "").ToLowerInvariant();
                        gameCommandWarningAcknowledged = false;
                        ValidateUniqueness();
                    }
                    // Focus transition -> stamp focus flash.
                    bool nowFocused = ImGui.IsItemFocused();
                    if (nowFocused && !commandWasFocused)
                        commandFocusFlashStart = ImGui.GetTime();
                    commandWasFocused = nowFocused;
                    // Text changed -> stamp keystroke flash.
                    if (editCommand != commandLastText)
                    {
                        commandKeystrokeFlashStart = ImGui.GetTime();
                        commandLastText = editCommand;
                    }
                    FocusRing();
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

                ImGui.Dummy(new Vector2(1, 2f * scale));

                // Vanilla toggle.
                if (ImGui.Checkbox("Use vanilla animation (disable mods only)", ref isVanillaPreset))
                {
                    if (isVanillaPreset)
                    {
                        editModDirectory = "";
                        editModName = "";
                        selectedModIndex = -1;
                        detectedEmotes.Clear();
                        detectedEmoteCommands.Clear();
                    }
                }
                if (ImGui.IsItemHovered())
                    UIStyles.EncoreTooltip("Create a preset that disables conflicting dance mods\nwithout enabling any mod (uses vanilla animation)");
            }
            ImGui.EndGroup();

            //  Full-width row : zoom/offset sliders (custom icon only) 
            if (hasCustomIcon)
            {
                ImGui.Dummy(new Vector2(1, 4f * scale));
                float third = (CardInnerWidth() - 12f * scale) / 3f;
                ImGui.SetNextItemWidth(third);
                ImGui.SliderFloat("##iconZoom", ref editIconZoom, 1.0f, 4.0f, "Zoom %.1fx");
                FocusRing();
                ImGui.SameLine(0, 6f * scale);
                ImGui.SetNextItemWidth(third);
                ImGui.SliderFloat("##iconX", ref editIconOffsetX, -0.5f, 0.5f, "X %.2f");
                FocusRing();
                ImGui.SameLine(0, 6f * scale);
                ImGui.SetNextItemWidth(third);
                ImGui.SliderFloat("##iconY", ref editIconOffsetY, -0.5f, 0.5f, "Y %.2f");
                FocusRing();
                if (editIconZoom != 1.0f || editIconOffsetX != 0f || editIconOffsetY != 0f)
                {
                    ImGui.Dummy(new Vector2(1, 2f * scale));
                    PushSmallChipButtonStyle();
                    if (ImGui.Button("Reset##icReset", new Vector2(64f * scale, 0)))
                    {
                        editIconZoom = 1.0f;
                        editIconOffsetX = 0f;
                        editIconOffsetY = 0f;
                    }
                    PopSmallChipButtonStyle();
                }
            }
        }
        EndCard();

        //  Card 02 - SELECT MOD 
        string modSummary;
        Vector4 modSummaryCol;
        if (isVanillaPreset) { modSummary = "VANILLA"; modSummaryCol = Sec_Mod; }
        else if (!string.IsNullOrWhiteSpace(editModName)) { modSummary = editModName.ToUpperInvariant(); modSummaryCol = Sec_Mod; }
        else { modSummary = "REQUIRED"; modSummaryCol = Chr_Warning; }

        if (BeginCard("02", "SELECT MOD", Sec_Mod, modSummary, modSummaryCol, ref modSelectOpen))
        {
        if (isVanillaPreset)
        {
            ImGui.TextWrapped("Vanilla preset - no mod will be enabled. Encore will only temporarily disable conflicting mods so the vanilla animation plays.");
        }
        else
        {
        // Search - with  magnifier prefix glyph inside the input,
        // matching HTML `.search::before` pseudo-element.
        var isSearchDisabled = isLoading;
        if (isSearchDisabled) ImGui.BeginDisabled();
        {
            var scale = UIStyles.Scale;
            var dl = ImGui.GetWindowDrawList();
            var rowStart = ImGui.GetCursorScreenPos();
            float rowAvail = CardInnerWidth();
            float frameH = ImGui.GetFrameHeight();
            float refreshBtnW = 72f * scale;
            float searchW = rowAvail - refreshBtnW - 6f * scale;
            float glyphW = 22f * scale;

            uint glyphCol = ImGui.ColorConvertFloat4ToU32(Chr_TextFaint);
            ImGui.PushFont(UiBuilder.IconFont);
            var mag = FontAwesomeIcon.Search.ToIconString();
            var magSz = ImGui.CalcTextSize(mag);
            ImGui.PopFont();

            // Shift the input's text right of the glyph.
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
                new Vector2(glyphW, 4f * scale));
            ImGui.SetNextItemWidth(searchW);
            if (ImGui.InputTextWithHint("##modSearch", "Search mods...", ref modSearchFilter, 100))
            {
                ApplyModFilter();
            }
            var inputMin = ImGui.GetItemRectMin();
            ImGui.PopStyleVar();
            FocusRing();

            // Draw the glyph over the input's padded-left area.
            ImGui.PushFont(UiBuilder.IconFont);
            dl.AddText(
                new Vector2(inputMin.X + (glyphW - magSz.X) * 0.5f,
                            inputMin.Y + (frameH - magSz.Y) * 0.5f),
                glyphCol, mag);
            ImGui.PopFont();

            ImGui.SameLine(0, 6f * scale);
        }

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
            UIStyles.PushTooltipStyle();
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
            UIStyles.PopTooltipStyle();
        }

        if (isSearchDisabled) ImGui.EndDisabled();

        ImGui.Spacing();

        var listHeight = 170f * UIStyles.Scale;
        ImGui.PushStyleColor(ImGuiCol.ChildBg,       new Vector4(0.06f, 0.07f, 0.10f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,        new Vector4(0.20f, 0.22f, 0.27f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Header,        new Vector4(0.49f, 0.65f, 0.85f, 0.28f));  // selected row
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.49f, 0.65f, 0.85f, 0.18f));  // hover row
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  new Vector4(0.49f, 0.65f, 0.85f, 0.35f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 1f * UIStyles.Scale));
        if (ImGui.BeginChild("ModList", new Vector2(CardInnerWidth(), listHeight), true))
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
                // [24px icon tile] [mod name] [right-aligned tag]; click = select
                var presets = Plugin.Instance?.Configuration.Presets;
                var scale = UIStyles.Scale;
                var dl2 = ImGui.GetWindowDrawList();
                float rowH = 32f * scale;
                float iconTileSize = 22f * scale;

                for (int i = 0; i < filteredMods.Count; i++)
                {
                    var mod = filteredMods[i];
                    var isSelected = mod.ModDirectory == editModDirectory;

                    var usedByPresets = presets?.Where(p => p.ModDirectory == mod.ModDirectory).ToList();
                    var isUsed = usedByPresets != null && usedByPresets.Count > 0;

                    // Build the right-side tag from detected type.
                    string tag;
                    string glyph;
                    if (mod.AnimationType == EmoteDetectionService.AnimationType.Movement)
                    {
                        tag = "[MOVE]";
                        glyph = "->";
                    }
                    else if (mod.RequiresRedraw)
                    {
                        var poseTypeName = mod.AnimationType switch
                        {
                            EmoteDetectionService.AnimationType.StandingIdle => "IDLE",
                            EmoteDetectionService.AnimationType.ChairSitting => "SIT",
                            EmoteDetectionService.AnimationType.GroundSitting => "GSIT",
                            EmoteDetectionService.AnimationType.LyingDozing => "DOZE",
                            _ => "POSE"
                        };
                        if (mod.AffectedPoseIndices.Count > 1)
                            tag = $"[{poseTypeName} {string.Join(",", mod.AffectedPoseIndices.Select(i => $"#{i}"))}]";
                        else if (mod.PoseIndex >= 0)
                            tag = $"[{poseTypeName} #{mod.PoseIndex}]";
                        else
                            tag = $"[{poseTypeName}]";
                        glyph = "●";
                    }
                    else if (mod.EmoteCommands.Count > 0)
                    {
                        tag = $"[{mod.EmoteCommands[0].TrimStart('/')}]";
                        glyph = "♪";
                    }
                    else
                    {
                        tag = "[?]";
                        glyph = "?";
                    }

                    var rowStart = ImGui.GetCursorScreenPos();
                    float rowW = ImGui.GetContentRegionAvail().X;
                    var rowMin = rowStart;
                    var rowMax = new Vector2(rowStart.X + rowW, rowStart.Y + rowH);

                    // Interaction first so visuals layer on top.
                    ImGui.InvisibleButton($"##mod_{i}", new Vector2(rowW, rowH));
                    bool hovered = ImGui.IsItemHovered();
                    bool clicked = ImGui.IsItemClicked();

                    if (hovered)
                    {
                        UIStyles.PushTooltipStyle();
                        ImGui.BeginTooltip();
                        if (mod.EmoteCommands.Count > 0)
                            ImGui.Text($"Detected emote(s): {string.Join(", ", mod.EmoteCommands)}");
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
                        if (isUsed && usedByPresets != null)
                        {
                            ImGui.Separator();
                            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f),
                                $"Used by: {string.Join(", ", usedByPresets.Select(p => p.Name))}");
                        }
                        ImGui.EndTooltip();
                        UIStyles.PopTooltipStyle();
                    }

                    // Row backgrounds.
                    if (isSelected)
                    {
                        uint bgL = ImGui.ColorConvertFloat4ToU32(new Vector4(Sec_Mod.X, Sec_Mod.Y, Sec_Mod.Z, 0.14f));
                        uint bgR = ImGui.ColorConvertFloat4ToU32(new Vector4(Sec_Mod.X, Sec_Mod.Y, Sec_Mod.Z, 0f));
                        dl2.AddRectFilledMultiColor(rowMin, rowMax, bgL, bgR, bgR, bgL);

                        // lock-in flair: bar height kick over 250ms + sheen sweep over 400ms
                        float barTopY = rowMin.Y;
                        float barBotY = rowMax.Y;
                        bool isLockIn = editModDirectory == modListLastSelected
                                     && modListLockInStart >= 0;
                        if (isLockIn)
                        {
                            float lE = (float)(ImGui.GetTime() - modListLockInStart);
                            const float barDur = 0.25f;
                            if (lE < barDur)
                            {
                                float bt = lE / barDur;
                                float scaleY = bt < 0.5f
                                    ? 0.3f + (1.1f - 0.3f) * (bt / 0.5f)
                                    : 1.1f - (1.1f - 1.0f) * ((bt - 0.5f) / 0.5f);
                                float fullH = rowMax.Y - rowMin.Y;
                                float kickedH = fullH * scaleY;
                                float midY = (rowMin.Y + rowMax.Y) * 0.5f;
                                barTopY = midY - kickedH * 0.5f;
                                barBotY = midY + kickedH * 0.5f;
                            }
                            const float sheenDur = 0.40f;
                            if (lE < sheenDur)
                            {
                                float st = lE / sheenDur;
                                float sheenX = rowMin.X + rowW * st;
                                float sheenW = 60f * scale;
                                float sheenAlpha = (1f - st) * 0.22f;
                                uint sheenCol = ImGui.ColorConvertFloat4ToU32(
                                    new Vector4(Sec_Mod.X, Sec_Mod.Y, Sec_Mod.Z, sheenAlpha));
                                uint sheenClear = ImGui.ColorConvertFloat4ToU32(
                                    new Vector4(Sec_Mod.X, Sec_Mod.Y, Sec_Mod.Z, 0f));
                                dl2.AddRectFilledMultiColor(
                                    new Vector2(sheenX - sheenW * 0.5f, rowMin.Y),
                                    new Vector2(sheenX + sheenW * 0.5f, rowMax.Y),
                                    sheenClear, sheenCol, sheenCol, sheenClear);
                            }
                            if (lE >= MathF.Max(barDur, sheenDur))
                            {
                                modListLockInStart = -1;
                            }
                        }

                        // Left accent bar (height may be kicked).
                        dl2.AddRectFilled(
                            new Vector2(rowMin.X, barTopY),
                            new Vector2(rowMin.X + 2f * scale, barBotY),
                            ImGui.ColorConvertFloat4ToU32(Sec_Mod));
                    }
                    else if (hovered)
                    {
                        dl2.AddRectFilled(rowMin, rowMax,
                            ImGui.ColorConvertFloat4ToU32(new Vector4(Sec_Mod.X, Sec_Mod.Y, Sec_Mod.Z, 0.06f)));
                    }

                    // Icon tile.
                    float leftPad = isSelected ? 10f * scale : 8f * scale;
                    float tileX = rowMin.X + leftPad;
                    float tileY = rowMin.Y + (rowH - iconTileSize) * 0.5f;
                    var tileMin = new Vector2(tileX, tileY);
                    var tileMax = new Vector2(tileX + iconTileSize, tileY + iconTileSize);
                    var tileFillCol = isSelected
                        ? new Vector4(Sec_Mod.X, Sec_Mod.Y, Sec_Mod.Z, 0.14f)
                        : new Vector4(0.105f, 0.113f, 0.146f, 1f);
                    var tileBorderCol = isSelected ? Sec_Mod : Chr_Border;
                    var tileGlyphCol = isSelected ? Sec_Mod : Chr_TextFaint;
                    dl2.AddRectFilled(tileMin, tileMax, ImGui.ColorConvertFloat4ToU32(tileFillCol));
                    dl2.AddRect(tileMin, tileMax, ImGui.ColorConvertFloat4ToU32(tileBorderCol), 0f, 0, 1f);
                    var gSz = ImGui.CalcTextSize(glyph);
                    dl2.AddText(
                        new Vector2(tileX + (iconTileSize - gSz.X) * 0.5f,
                                    tileY + (iconTileSize - gSz.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(tileGlyphCol), glyph);

                    // Name + tag. Name truncates to leave room for tag.
                    float nameX = tileMax.X + 10f * scale;
                    var tagSz = ImGui.CalcTextSize(tag);
                    float tagRight = rowMax.X - 10f * scale;
                    float tagLeft = tagRight - tagSz.X;
                    float nameMaxW = tagLeft - nameX - 8f * scale;
                    float textH = ImGui.GetTextLineHeight();
                    float textY = rowMin.Y + (rowH - textH) * 0.5f;

                    string nameDisplay = mod.ModName;
                    if (isUsed) nameDisplay = "✓ " + nameDisplay;
                    string truncName = TruncateToFit(nameDisplay, nameMaxW);
                    var nameCol = isSelected ? Chr_Text : new Vector4(0.80f, 0.82f, 0.85f, 1f);
                    dl2.AddText(new Vector2(nameX, textY),
                        ImGui.ColorConvertFloat4ToU32(nameCol), truncName);

                    var tagCol = isSelected ? Sec_Mod : Chr_TextFaint;
                    dl2.AddText(new Vector2(tagLeft, textY),
                        ImGui.ColorConvertFloat4ToU32(tagCol), tag);

                    if (clicked)
                    {
                        editModDirectory = mod.ModDirectory;
                        editModName = mod.ModName;
                        selectedModIndex = i;
                        modListLastSelected = mod.ModDirectory;
                        modListLockInStart = ImGui.GetTime();

                        if (string.IsNullOrEmpty(editName))
                            editName = mod.ModName;
                        if (string.IsNullOrEmpty(editCommand))
                            editCommand = SanitizeCommand(mod.ModName);

                        availableModSettings = Plugin.Instance?.PenumbraService?.GetAvailableModSettings(mod.ModDirectory, mod.ModName);
                        editModOptions = new Dictionary<string, List<string>>();
                        UpdateDetectedInfo();
                    }
                }
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);     // FrameBorderSize + ItemSpacing
        ImGui.PopStyleColor(5);   // ChildBg, Border, Header, HeaderHovered, HeaderActive
        } // end mod-selection else branch
        }
        EndCard();

        //  Card 03 - EMOTE TO USE 
        string emoteSummary;
        Vector4 emoteSummaryCol;
        if (!isVanillaPreset && string.IsNullOrEmpty(editModDirectory))
        {
            emoteSummary = "- PICK A MOD FIRST";
            emoteSummaryCol = Chr_TextFaint;
        }
        else if (!string.IsNullOrWhiteSpace(editEmoteCommand))
        {
            emoteSummary = editEmoteCommand.ToUpperInvariant();
            emoteSummaryCol = Sec_Emote;
        }
        else if (isVanillaPreset)
        {
            emoteSummary = "REQUIRED";
            emoteSummaryCol = Chr_Warning;
        }
        else
        {
            emoteSummary = "-";
            emoteSummaryCol = Chr_TextFaint;
        }

        if (BeginCard("03", "EMOTE TO USE", Sec_Emote, emoteSummary, emoteSummaryCol, ref emoteOpen))
        {
        if (isVanillaPreset)
        {
            DrawFieldLabel("EMOTE COMMAND");
            ImGui.Dummy(new Vector2(1, 2f * UIStyles.Scale));
            // Accent / prefix chip, same input-group pattern as the
            // Setup card's command field.
            {
                var scale = UIStyles.Scale;
                var dl = ImGui.GetWindowDrawList();
                var rowStart = ImGui.GetCursorScreenPos();
                float rowInnerW = CardInnerWidth();
                float frameH = ImGui.GetFrameHeight();
                float prefixW = 26f * scale;
                var prefixMin = rowStart;
                var prefixMax = new Vector2(rowStart.X + prefixW, rowStart.Y + frameH);
                dl.AddRectFilled(prefixMin, prefixMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(Sec_Emote.X, Sec_Emote.Y, Sec_Emote.Z, 0.10f)));
                dl.AddRect(prefixMin, prefixMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(Sec_Emote.X, Sec_Emote.Y, Sec_Emote.Z, 0.55f)),
                    0f, 0, 1f);
                string slash = "/";
                var slashSz = ImGui.CalcTextSize(slash);
                dl.AddText(
                    new Vector2(prefixMin.X + (prefixW - slashSz.X) * 0.5f,
                                prefixMin.Y + (frameH - slashSz.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(Sec_Emote), slash);
                ImGui.SetCursorScreenPos(new Vector2(rowStart.X + prefixW, rowStart.Y));
                ImGui.SetNextItemWidth(rowInnerW - prefixW);
                if (ImGui.InputText("##vanillaEmote", ref editEmoteCommand, 50))
                {
                    editEmoteCommand = "/" + editEmoteCommand.TrimStart('/').ToLowerInvariant();
                }
                FocusRing();
            }
            ImGui.Dummy(new Vector2(1, 4f * UIStyles.Scale));
            ImGui.PushStyleColor(ImGuiCol.Text, Chr_TextDim);
            ImGui.TextWrapped("Plays the vanilla animation. Conflicting dance mods are temporarily disabled while this preset is active.");
            ImGui.PopStyleColor();
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
                // Movement mod - the mod's walk/movement animations apply on activation via redraw.
                ImGui.TextColored(new Vector4(0.6f, 0.9f, 0.6f, 1f), "Type: Movement (Walk / Sprint / Jog)");
                ImGui.TextDisabled("This mod changes your walking and movement animations.");

                // If the mod also bundles emotes/poses, let the user optionally pick one to execute
                if (detectedEmoteCommands.Count > 0)
                {
                    ImGui.Spacing();
                    ImGui.Text("Optional emote to execute on activation:");

                    if (!useCustomEmoteCommand)
                    {
                        ImGui.SetNextItemWidth(200);
                        // Prepend a "(none)" option so the user can keep it movement-only
                        var emoteOptions = new List<string> { "(none)" };
                        emoteOptions.AddRange(detectedEmoteCommands);
                        var idx = string.IsNullOrEmpty(editEmoteCommand)
                            ? 0
                            : detectedEmoteCommands.FindIndex(c => string.Equals(c, editEmoteCommand, StringComparison.OrdinalIgnoreCase)) + 1;
                        if (idx < 0) idx = 0;
                        var arr = emoteOptions.ToArray();
                        UIStyles.PushEncoreComboStyle();
                        if (ImGui.Combo("##movementEmoteSelect", ref idx, arr, arr.Length))
                        {
                            editEmoteCommand = idx == 0 ? "" : detectedEmoteCommands[idx - 1];
                            selectedEmoteIndex = Math.Max(0, idx - 1);
                        }
                        UIStyles.PopEncoreComboStyle();
                        FocusRing();
                    }
                    ImGui.TextDisabled("Leave as (none) for movement-only, or pick an emote to play when activated.");
                }
                else
                {
                    ImGui.TextDisabled("No emote or pose selection needed - just enable and go.");
                }
            }
            else if (isPoseMod)
            {
                // Pure pose mod - show type, pose index, and explain redraw behaviour
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
                    UIStyles.PushEncoreComboStyle();
                    if (ImGui.Combo("##poseSelect", ref currentIdx, poseOptions, poseOptions.Length))
                    {
                        selectedPoseIndex = detectedPoseIndices[currentIdx];
                    }
                    UIStyles.PopEncoreComboStyle();
                    FocusRing();
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
                // Emote mod (or mixed mod) - show emote selection.
                DrawFieldLabel("DETECTED COMMANDS");
                ImGui.Dummy(new Vector2(1, 2f * UIStyles.Scale));

                if (detectedEmoteCommands.Count > 0 && !useCustomEmoteCommand)
                {
                    float comboW = CardInnerWidth() - 80f * UIStyles.Scale;
                    ImGui.SetNextItemWidth(comboW);
                    var emoteOptions = detectedEmoteCommands.ToArray();
                    if (selectedEmoteIndex >= emoteOptions.Length)
                        selectedEmoteIndex = 0;

                    UIStyles.PushEncoreComboStyle();
                    if (ImGui.Combo("##emoteSelect", ref selectedEmoteIndex, emoteOptions, emoteOptions.Length))
                    {
                        editEmoteCommand = detectedEmoteCommands[selectedEmoteIndex];
                    }
                    UIStyles.PopEncoreComboStyle();
                    FocusRing();

                    ImGui.SameLine(0, 6f * UIStyles.Scale);
                    PushSmallChipButtonStyle();
                    if (ImGui.Button("Custom##em", new Vector2(70f * UIStyles.Scale, 0)))
                    {
                        useCustomEmoteCommand = true;
                        if (string.IsNullOrEmpty(editEmoteCommand) && detectedEmoteCommands.Count > 0)
                        {
                            editEmoteCommand = detectedEmoteCommands[0];
                        }
                    }
                    PopSmallChipButtonStyle();
                    if (ImGui.IsItemHovered())
                    {
                        UIStyles.EncoreTooltip("Enter a custom emote command");
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
                    FocusRing();

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

                // pose commands trigger pose-picker; per-type indices filter when available
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
                            UIStyles.PushEncoreComboStyle();
                            if (ImGui.Combo("##poseSelect", ref currentIdx, poseOptions, poseOptions.Length))
                            {
                                selectedPoseIndex = relevantIndices[currentIdx];
                            }
                            UIStyles.PopEncoreComboStyle();
                            FocusRing();
                        }
                        else if (relevantIndices.Count == 1)
                        {
                            ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"{poseTypeName} pose #{relevantIndices[0]}");
                            selectedPoseIndex = relevantIndices[0];
                        }
                    }
                }

                // Emote bypass checkbox - for emotes not on the character (not pose, not movement)
                if (!isVanillaPreset)
                {
                    ImGui.Spacing();
                    ImGui.Checkbox("I don't have this emote", ref editEmoteLocked);
                    if (ImGui.IsItemHovered())
                        UIStyles.EncoreTooltip("Your mod's animation will play regardless.\nRequires 'Allow All Emotes' in settings.");
                    if (editEmoteLocked)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), "(bypass)");
                    }
                }
            }

        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped("Pick a mod above and the emote dropdown will populate with the commands it was detected to affect.");
            ImGui.PopStyleColor();
        }
        }
        EndCard();

        //  Card 04 - MOD SETTINGS 
        bool hasAvailableOptions = availableModSettings != null && availableModSettings.Count > 0;
        bool modChosenForOptions = !isVanillaPreset && !string.IsNullOrEmpty(editModDirectory);
        string settingsSummary;
        Vector4 settingsSummaryCol;
        if (!modChosenForOptions) { settingsSummary = "-"; settingsSummaryCol = Chr_TextFaint; }
        else if (!hasAvailableOptions) { settingsSummary = "- NO OPTIONS"; settingsSummaryCol = Chr_TextFaint; }
        else if (editModOptions.Count == 0) { settingsSummary = "DEFAULTS"; settingsSummaryCol = Chr_TextDim; }
        else { settingsSummary = $"{editModOptions.Count} SET"; settingsSummaryCol = Sec_ModOptions; }

        if (BeginCard("04", "MOD SETTINGS", Sec_ModOptions, settingsSummary, settingsSummaryCol, ref modSettingsOpen))
        {
            if (!modChosenForOptions)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped("Select a mod in card 02 to see its adjustable option groups here.");
                ImGui.PopStyleColor();
            }
            else if (!hasAvailableOptions)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped("This mod has no adjustable option groups - it will play with its defaults.");
                ImGui.PopStyleColor();
            }
            else
            {
                float labelColW = CardInnerWidth() * 0.34f;
                float gridGap = 16f * UIStyles.Scale;
                float labelTrack = 1.0f * UIStyles.Scale;
                float labelMaxW = labelColW - 14f * UIStyles.Scale;
                foreach (var (groupName, (options, groupType)) in availableModSettings)
                {
                    if (options.Length == 0)
                        continue;

                    var rowTop = ImGui.GetCursorScreenPos();
                    ImGui.AlignTextToFramePadding();
                    string fullLabel = groupName.ToUpperInvariant();
                    string shownLabel = TruncateTrackedToFit(fullLabel, labelMaxW, labelTrack);
                    bool labelTruncated = shownLabel != fullLabel;
                    var dl = ImGui.GetWindowDrawList();
                    var labelPos = ImGui.GetCursorScreenPos();
                    UIStyles.DrawTrackedText(dl, labelPos, shownLabel,
                        ImGui.ColorConvertFloat4ToU32(Chr_TextGhost), labelTrack);
                    ImGui.Dummy(new Vector2(labelColW, ImGui.GetTextLineHeight()));
                    if (labelTruncated && ImGui.IsItemHovered())
                        UIStyles.EncoreTooltip(groupName);
                    ImGui.SameLine(labelColW + gridGap, 0);
                    float controlW = CardInnerWidth();

                    editModOptions.TryGetValue(groupName, out var currentSelection);

                    if (groupType == 0)
                    {
                        // Single-select combo, filling the right column.
                        var selectedOption = currentSelection?.Count > 0 ? currentSelection[0] : "";
                        var previewLabel = string.IsNullOrEmpty(selectedOption) ? "(default)" : selectedOption;

                        ImGui.SetNextItemWidth(controlW);
                        UIStyles.PushEncoreComboStyle();
                        if (ImGui.BeginCombo($"##{groupName}", previewLabel))
                        {
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
                        UIStyles.PopEncoreComboStyle();
                        FocusRing();
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
        }
        EndCard();

        //  Card 05 - MODIFIERS 
        bool modifiersRelevant = !isVanillaPreset
                              && !string.IsNullOrEmpty(editModDirectory)
                              && HasModifierRelevantContent();
        string modifiersSummary;
        Vector4 modifiersSummaryCol;
        if (!modifiersRelevant) { modifiersSummary = "-"; modifiersSummaryCol = Chr_TextFaint; }
        else if (editModifiers.Count == 0) { modifiersSummary = "NONE"; modifiersSummaryCol = Chr_TextDim; }
        else { modifiersSummary = $"{editModifiers.Count} VARIANT{(editModifiers.Count == 1 ? "" : "S")}"; modifiersSummaryCol = Sec_Modifiers; }

        if (BeginCard("05", "MODIFIERS", Sec_Modifiers, modifiersSummary, modifiersSummaryCol, ref modifiersOpen))
        {
            if (!modifiersRelevant)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped("Modifiers aren't applicable - this preset has nothing to vary. Pick a mod with multiple options, emotes, or poses to unlock variants.");
                ImGui.PopStyleColor();
            }
            else
            {
                DrawModifiersSection();
            }
        }
        EndCard();

        //  Card 06 - HEELS 
        // Gizmo lifecycle runs every frame regardless of card state.
        UpdateHeelsGizmoLifecycle(heelsOpen);
        string heelsSummary = editHeelsEnabled ? "CUSTOM OFFSET" : "OFF";
        Vector4 heelsSummaryCol = editHeelsEnabled ? Sec_Heels : Chr_TextDim;
        if (BeginCard("06", "HEELS", Sec_Heels, heelsSummary, heelsSummaryCol, ref heelsOpen))
        {
            DrawHeelsSectionBody();
        }
        EndCard();

        //  Card 07 - CONFLICT HANDLING 
        bool conflictsRelevant = !isVanillaPreset
                              && !string.IsNullOrEmpty(editModDirectory)
                              && detectedEmoteCommands.Count > 1;
        string conflictSummary;
        Vector4 conflictSummaryCol;
        if (!conflictsRelevant) { conflictSummary = "-"; conflictSummaryCol = Chr_TextFaint; }
        else if (editConflictExclusions.Count == 0) { conflictSummary = "DISABLE ALL"; conflictSummaryCol = Chr_TextDim; }
        else { conflictSummary = $"{editConflictExclusions.Count} KEPT ON"; conflictSummaryCol = Sec_Conflict; }

        if (BeginCard("07", "CONFLICT HANDLING", Sec_Conflict, conflictSummary, conflictSummaryCol, ref conflictExclusionsOpen))
        {
            if (!conflictsRelevant)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped("Nothing to opt out of - this preset only affects one emote/pose so there are no conflicts to choose between.");
                ImGui.PopStyleColor();
            }
            else
            {
                DrawConflictExclusionsSection();
            }
        }
        EndCard();

        // Save/Cancel live in the window footer (pinned, not scrolled)
    }

    private static readonly Vector4 HeelsAccentColor = new Vector4(0.95f, 0.75f, 0.45f, 1f);

    // runs every frame; tracks open flag independently of body-render
    private void UpdateHeelsGizmoLifecycle(bool sectionOpen)
    {
        var gizmoActive = sectionOpen && editHeelsEnabled;
        if (gizmoActive)
        {
            HeelsGizmoOverlay.Target = editHeels;
            HeelsGizmoOverlay.Label = string.IsNullOrWhiteSpace(editName) ? "Heels" : $"Heels - {editName}";
            var sh = Plugin.Instance?.SimpleHeelsService;
            if (sh != null && sh.IsAvailable)
                sh.ApplyOffset(editHeels.X, editHeels.Y, editHeels.Z, editHeels.Rotation, editHeels.Pitch, editHeels.Roll);
        }
        else if (HeelsGizmoOverlay.Target == editHeels)
        {
            HeelsGizmoOverlay.Target = null;
            HeelsGizmoOverlay.Label = null;
            Plugin.Instance?.RefreshActivePresetHeels();
        }
    }

    // caller owns card chrome + UpdateHeelsGizmoLifecycle
    private void DrawHeelsSectionBody()
    {
        var heelsAvailable = Plugin.Instance?.SimpleHeelsService?.IsAvailable ?? false;

        if (!heelsAvailable)
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Simple Heels plugin not detected.");
            ImGui.TextWrapped("Install the Simple Heels plugin to use per-preset heel offsets. Values saved here will apply once Simple Heels is available.");
        }
        else
        {
            ImGui.TextWrapped("Overrides Simple Heels while this preset is active. Drag the world-space arrows/rotation ring in game, or use the numeric fields below. Clears when a different preset or /encorereset runs.");
        }

        ImGui.Spacing();
        ImGui.Checkbox("Enable heel override for this preset", ref editHeelsEnabled);

        if (!editHeelsEnabled)
        {
            ImGui.TextDisabled("(disabled - Simple Heels will use your normal character config)");
            return;
        }

        ImGui.Spacing();
        DrawHeelsControls(editHeels);
    }

    internal void DeactivateHeelsGizmo()
    {
        if (HeelsGizmoOverlay.Target == editHeels)
        {
            HeelsGizmoOverlay.Target = null;
            HeelsGizmoOverlay.Label = null;
        }
        Plugin.Instance?.RefreshActivePresetHeels();
    }

    // shared by routine step editor; mutates target in place (gizmo edits same instance)
    internal static void DrawHeelsControls(HeelsGizmoTarget t)
    {
        var scale = UIStyles.Scale;

        ImGui.TextColored(new Vector4(0.75f, 0.85f, 0.95f, 1f),
            "Drag the arrows/ring in-world, or click-drag values below. Double-click to type.");
        ImGui.Spacing();

        const float TransStep = 0.005f;
        const float TransDragSpeed = 0.002f;
        const float TransMin = -3f;
        const float TransMax = 3f;
        ScrubFloat("Y (height)", ref t.Y, TransMin, TransMax, TransStep, TransDragSpeed, "%.4f");
        ScrubFloat("Forward (Z)", ref t.Z, TransMin, TransMax, TransStep, TransDragSpeed, "%.4f");
        ScrubFloat("Side (X)", ref t.X, TransMin, TransMax, TransStep, TransDragSpeed, "%.4f");

        // stored radians; edited as degrees
        var rotDeg = t.Rotation * (180f / MathF.PI);
        if (ScrubFloat("Rotation", ref rotDeg, -180f, 180f, 1f, 0.5f, "%.1fdeg"))
            t.Rotation = rotDeg * (MathF.PI / 180f);

        if (ImGui.TreeNode("Pitch / Roll (advanced)"))
        {
            var pitchDeg = t.Pitch * (180f / MathF.PI);
            if (ScrubFloat("Pitch", ref pitchDeg, -180f, 180f, 1f, 0.5f, "%.1fdeg"))
                t.Pitch = pitchDeg * (MathF.PI / 180f);

            var rollDeg = t.Roll * (180f / MathF.PI);
            if (ScrubFloat("Roll", ref rollDeg, -180f, 180f, 1f, 0.5f, "%.1fdeg"))
                t.Roll = rollDeg * (MathF.PI / 180f);

            ImGui.TreePop();
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset all##heelsResetAll"))
        {
            t.X = t.Y = t.Z = 0f;
            t.Rotation = t.Pitch = t.Roll = 0f;
        }
    }

    // State for auto-repeat on held +/-buttons. One pair of stopwatches is enough - only one
    // button can be held at a time, and we reset when the mouse releases.
    private static readonly System.Diagnostics.Stopwatch _scrubHoldSince = new();
    private static readonly System.Diagnostics.Stopwatch _scrubHoldThrottle = new();

    // Port of SimpleHeels' FloatEditor - [-] [DragFloat] [+] [(reset) reset] [label].
    // Returns true if the value changed this frame.
    private static bool ScrubFloat(string label, ref float value,
        float min, float max, float stepPerClick, float dragSpeed, string format)
    {
        if (_scrubHoldSince.IsRunning && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            _scrubHoldSince.Reset();
            _scrubHoldThrottle.Reset();
        }

        var scale = UIStyles.Scale;
        var changed = false;

        // Fixed-width row so all axes line up regardless of label.
        var rowW = 280f * scale;
        var dragW = rowW - (ImGui.GetFrameHeight() * 3) - (ImGui.GetStyle().ItemSpacing.X * 3);

        // [-]
        if (ImGuiComponents.IconButton($"##{label}_minus", FontAwesomeIcon.Minus) ||
            HeldRepeat(label + "-"))
        {
            value = Math.Clamp(value - stepPerClick, min, max);
            changed = true;
        }
        ImGui.SameLine();

        // DragFloat (click-drag to scrub, double-click to type)
        ImGui.SetNextItemWidth(dragW);
        if (ImGui.DragFloat($"##{label}_drag", ref value, dragSpeed, min, max, format))
            changed = true;
        ImGui.SameLine();

        // [+]
        if (ImGuiComponents.IconButton($"##{label}_plus", FontAwesomeIcon.Plus) ||
            HeldRepeat(label + "+"))
        {
            value = Math.Clamp(value + stepPerClick, min, max);
            changed = true;
        }
        ImGui.SameLine();

        // [(reset)] reset-to-zero
        if (ImGuiComponents.IconButton($"##{label}_reset", FontAwesomeIcon.Undo))
        {
            value = 0f;
            changed = true;
        }
        if (ImGui.IsItemHovered()) UIStyles.EncoreTooltip("Reset to 0");
        ImGui.SameLine();

        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        return changed;
    }

    // Poll for click-hold auto-repeat on the previous IconButton. Returns true once per throttle
    // interval while the button is held. Call immediately after the button to target it.
    private static bool HeldRepeat(string key)
    {
        if (!ImGui.IsItemHovered() || !ImGui.IsMouseDown(ImGuiMouseButton.Left)) return false;
        if (!_scrubHoldSince.IsRunning) _scrubHoldSince.Restart();
        if (_scrubHoldSince.ElapsedMilliseconds < 400) return false;
        if (_scrubHoldThrottle.IsRunning && _scrubHoldThrottle.ElapsedMilliseconds < 50) return false;
        _scrubHoldThrottle.Restart();
        return true;
    }

    // Draft modifier being configured before committing
    private PresetModifier? draftModifier = null;
    private string draftModifierName = "";

    // Amber / heels tone from the HTML palette - warm and distinct from the blue + green
    // already anchoring the rest of the Encore chrome.
    private static readonly Vector4 ModifierAccentColor = new Vector4(0.867f, 0.627f, 0.416f, 1f);

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

    private static readonly Vector4 ConflictAccentColor = new Vector4(0.95f, 0.55f, 0.45f, 1f);

    private static string FormatCommandLabel(string command)
    {
        var cmd = command?.ToLowerInvariant() ?? "";
        return cmd switch
        {
            "/cpose" => "Idle poses",
            "/sit" => "Sit poses",
            "/groundsit" => "Ground-sit poses",
            "/doze" => "Doze poses",
            _ => command ?? ""
        };
    }

    private void DrawConflictExclusionsSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped("When this preset activates, Encore will NOT disable other mods that conflict on the checked items.");
        ImGui.PopStyleColor();
        ImGui.Dummy(new Vector2(1, 6f * UIStyles.Scale));

        // [name left] [right-aligned "KEPT ON" / "DISABLED" tag]; row click toggles
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        float rowH = 30f * scale;
        for (int i = 0; i < detectedEmoteCommands.Count; i++)
        {
            var cmd = detectedEmoteCommands[i];
            var label = FormatCommandLabel(cmd);
            var excluded = editConflictExclusions.Contains(cmd);

            var rowStart = ImGui.GetCursorScreenPos();
            float rowW = ImGui.GetContentRegionAvail().X;
            var rowMin = rowStart;
            var rowMax = new Vector2(rowStart.X + rowW, rowStart.Y + rowH);

            ImGui.InvisibleButton($"##excl_{i}", new Vector2(rowW, rowH));
            bool hovered = ImGui.IsItemHovered();
            bool clicked = ImGui.IsItemClicked();
            if (clicked)
            {
                if (excluded) editConflictExclusions.Remove(cmd);
                else          editConflictExclusions.Add(cmd);
                excluded = !excluded;
            }

            // Hover tint.
            if (hovered)
            {
                dl.AddRectFilled(rowMin, rowMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(Sec_Conflict.X, Sec_Conflict.Y, Sec_Conflict.Z, 0.06f)));
            }

            // Dashed bottom separator between rows.
            if (i < detectedEmoteCommands.Count - 1)
            {
                uint sepCol = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(Chr_BorderSoft.X, Chr_BorderSoft.Y, Chr_BorderSoft.Z, 0.8f));
                for (float dx = 0; dx < rowW - 12f * scale; dx += 6f * scale)
                {
                    float segW = MathF.Min(3f * scale, rowW - 12f * scale - dx);
                    dl.AddLine(
                        new Vector2(rowMin.X + 4f * scale + dx, rowMax.Y),
                        new Vector2(rowMin.X + 4f * scale + dx + segW, rowMax.Y),
                        sepCol, 1f);
                }
            }

            // Name left.
            float nameX = rowMin.X + 8f * scale;
            float textH = ImGui.GetTextLineHeight();
            float textY = rowMin.Y + (rowH - textH) * 0.5f;

            // Tag right - "KEPT ON" in cyan when excluded, "DISABLED"
            // dim when not.
            string tagText = excluded ? "KEPT ON" : "DISABLED";
            var tagCol = excluded ? Sec_Conflict : Chr_TextFaint;
            float tagTrack = 1.0f * scale;
            float tagW = UIStyles.MeasureTrackedWidth(tagText, tagTrack);
            float tagPadX = 7f * scale;
            float tagPadY = 2f * scale;
            var tagSz = ImGui.CalcTextSize(tagText);
            float tagRight = rowMax.X - 10f * scale;
            float tagLeft = tagRight - tagW - tagPadX * 2;
            float tagTop = textY - tagPadY;
            float tagBot = textY + tagSz.Y + tagPadY;
            dl.AddRectFilled(
                new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
                ImGui.ColorConvertFloat4ToU32(new Vector4(tagCol.X, tagCol.Y, tagCol.Z, 0.08f)));
            dl.AddRect(
                new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
                ImGui.ColorConvertFloat4ToU32(new Vector4(tagCol.X, tagCol.Y, tagCol.Z, 0.45f)),
                0f, 0, 1f);
            UIStyles.DrawTrackedText(dl,
                new Vector2(tagLeft + tagPadX, textY),
                tagText, ImGui.ColorConvertFloat4ToU32(tagCol), tagTrack);

            // Name - truncate so it doesn't overlap tag.
            float nameMaxW = tagLeft - nameX - 10f * scale;
            string truncName = TruncateToFit(label, nameMaxW);
            var nameCol = excluded ? Chr_Text : Chr_TextDim;
            dl.AddText(new Vector2(nameX, textY),
                ImGui.ColorConvertFloat4ToU32(nameCol), truncName);
        }
    }

    private void DrawModifiersSection()
    {
        var scale = UIStyles.Scale;

        // Description (always visible) - dim wrapped text
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + CardInnerWidth());
        ImGui.TextColored(new Vector4(0.54f, 0.55f, 0.60f, 1f),
            "Switch between different mod options without creating separate presets.");
        ImGui.PopTextWrapPos();

        // Live usage examples - mono, lavender-tinted hint so they feel like code snippets
        if (editModifiers.Count > 0 && !string.IsNullOrWhiteSpace(editCommand))
        {
            var examples = string.Join("  ", editModifiers.Select(m => $"/{editCommand} {m.Name}"));
            var exampleCol = new Vector4(
                ModifierAccentColor.X * 0.70f + 0.20f,
                ModifierAccentColor.Y * 0.70f + 0.20f,
                ModifierAccentColor.Z * 0.70f + 0.20f, 1f);
            ImGui.TextColored(exampleCol, examples);
        }

        ImGui.Dummy(new Vector2(1, 4f * scale));

        // no nested ChannelsSplit (BeginCard already split this drawlist; nested splits break rendering)
        var variantSoft = new Vector4(ModifierAccentColor.X, ModifierAccentColor.Y, ModifierAccentColor.Z, 0.10f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 0f);
        ImGui.PushStyleColor(ImGuiCol.Tab,              new Vector4(0.09f, 0.10f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered,       variantSoft);
        ImGui.PushStyleColor(ImGuiCol.TabActive,        variantSoft);
        ImGui.PushStyleColor(ImGuiCol.TabUnfocused,     new Vector4(0.09f, 0.10f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.TabUnfocusedActive, variantSoft);

        ImGui.PushItemWidth(CardInnerWidth());
        ImGui.BeginGroup();

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
                    string tabKey = $"m:{i}";
                    if (modifierLastActiveKey != tabKey)
                    {
                        modifierTabSwitchStart = ImGui.GetTime();
                        modifierLastActiveKey = tabKey;
                    }
                    float alpha = 1f;
                    if (modifierTabSwitchStart >= 0)
                    {
                        float tE = (float)(ImGui.GetTime() - modifierTabSwitchStart);
                        const float tDur = 0.15f;
                        if (tE >= tDur) modifierTabSwitchStart = -1;
                        else alpha = 0.35f + 0.65f * (tE / tDur);
                    }
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);

                    // Inline rename field
                    if (renamingModifierIndex == i)
                    {
                        ImGui.Text("Name:");
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(150 * scale);
                        var renameEnter = ImGui.InputText($"##renameMod_{i}", ref renamingModifierName, 30,
                            ImGuiInputTextFlags.EnterReturnsTrue);
                        FocusRing();

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
                    ImGui.PopStyleVar();  // Alpha from tab-switch fade
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
                    // Tab-switch fade for the draft tab too.
                    if (modifierLastActiveKey != "draft")
                    {
                        modifierTabSwitchStart = ImGui.GetTime();
                        modifierLastActiveKey = "draft";
                    }
                    float draftAlpha = 1f;
                    if (modifierTabSwitchStart >= 0)
                    {
                        float tE = (float)(ImGui.GetTime() - modifierTabSwitchStart);
                        const float tDur = 0.15f;
                        if (tE >= tDur) modifierTabSwitchStart = -1;
                        else draftAlpha = 0.35f + 0.65f * (tE / tDur);
                    }
                    ImGui.PushStyleVar(ImGuiStyleVar.Alpha, draftAlpha);

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
                        FocusRing();

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

                    ImGui.PopStyleVar();  // draft-tab fade alpha
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
                    UIStyles.EncoreTooltip("Add modifier");
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

            // Centered chip-style button - transparent slate with lavender border + text
            var btnLabel = "+ add modifier";
            var btnPadding = 24f * scale;
            var btnWidth = ImGui.CalcTextSize(btnLabel).X + btnPadding * 2;
            var btnHeight = 28f * scale;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (regionWidth - btnWidth) * 0.5f);

            var variantSoftBg = new Vector4(
                ModifierAccentColor.X * 0.14f + 0.09f * 0.86f,
                ModifierAccentColor.Y * 0.14f + 0.10f * 0.86f,
                ModifierAccentColor.Z * 0.14f + 0.14f * 0.86f, 1f);
            var variantHover = new Vector4(
                ModifierAccentColor.X * 0.22f + 0.09f * 0.78f,
                ModifierAccentColor.Y * 0.22f + 0.10f * 0.78f,
                ModifierAccentColor.Z * 0.22f + 0.14f * 0.78f, 1f);
            var variantBorder = new Vector4(
                ModifierAccentColor.X * 0.55f,
                ModifierAccentColor.Y * 0.55f,
                ModifierAccentColor.Z * 0.55f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button,        variantSoftBg);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, variantHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  variantHover);
            ImGui.PushStyleColor(ImGuiCol.Border,        variantBorder);
            ImGui.PushStyleColor(ImGuiCol.Text,          ModifierAccentColor);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            if (ImGui.Button(btnLabel, new Vector2(btnWidth, btnHeight)))
            {
                draftModifier = new PresetModifier();
                draftModifierName = "";
                expandedModifierIndex = -2;
            }
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(5);

            ImGui.Spacing();
        }

        ImGui.EndGroup();
        ImGui.PopItemWidth();

        ImGui.PopStyleColor(5); // Tab / TabHovered / TabActive / TabUnfocused / TabUnfocusedActive
        ImGui.PopStyleVar();    // TabRounding
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
            UIStyles.PushEncoreComboStyle();
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
            UIStyles.PopEncoreComboStyle();
            FocusRing();

            ImGui.Spacing();
        }

        // Pose index override - works with the effective command (override or base)
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
                    UIStyles.PushEncoreComboStyle();
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
                    UIStyles.PopEncoreComboStyle();
                    FocusRing();

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
                UIStyles.PushEncoreComboStyle();
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
                UIStyles.PopEncoreComboStyle();
                FocusRing();
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

    private void HandleIconPickerCompletion()
    {
        if (iconPickerWindow == null || iconPickerWindow.IsOpen || !iconPickerWindow.Confirmed)
            return;

        editIconId = iconPickerWindow.SelectedIconId;
        editCustomIconPath = null;  // Game icon replaces custom icon
        editIconZoom = 1.0f;
        editIconOffsetX = 0f;
        editIconOffsetY = 0f;
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
        CurrentPreset.IconZoom = editIconZoom;
        CurrentPreset.IconOffsetX = editIconOffsetX;
        CurrentPreset.IconOffsetY = editIconOffsetY;
        CurrentPreset.ModDirectory = editModDirectory;
        CurrentPreset.ModName = editModName;

        // Movement mods: preserve an optional emote if the user picked one; otherwise leave empty
        if (detectedAnimationType == EmoteDetectionService.AnimationType.Movement)
        {
            CurrentPreset.ExecuteEmote = true;
            if (!string.IsNullOrWhiteSpace(editEmoteCommand))
            {
                CurrentPreset.EmoteCommand = editEmoteCommand.StartsWith("/")
                    ? editEmoteCommand
                    : "/" + editEmoteCommand;
            }
            else
            {
                CurrentPreset.EmoteCommand = "";
            }
            CurrentPreset.AnimationType = (int)EmoteDetectionService.AnimationType.Movement;
            CurrentPreset.PoseIndex = -1;
        }
        else
        {
            CurrentPreset.ExecuteEmote = true;

            // Use the edited emote command (with fallback to /dance)
            if (!string.IsNullOrWhiteSpace(editEmoteCommand))
            {
                CurrentPreset.EmoteCommand = editEmoteCommand.StartsWith("/")
                    ? editEmoteCommand
                    : "/" + editEmoteCommand;
            }
            else
            {
                CurrentPreset.EmoteCommand = "/dance";
            }

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
        CurrentPreset.EmoteLocked = editEmoteLocked;

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

        // Save conflict exclusions (only those still present in detected command list)
        CurrentPreset.ConflictExclusions = editConflictExclusions
            .Where(e => detectedEmoteCommands.Any(c => string.Equals(c, e, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Save heels offset. Disabled or all-zero -> null (so no unnecessary IPC traffic on activation).
        if (editHeelsEnabled &&
            (MathF.Abs(editHeels.X) > 0.0001f || MathF.Abs(editHeels.Y) > 0.0001f || MathF.Abs(editHeels.Z) > 0.0001f ||
             MathF.Abs(editHeels.Rotation) > 0.0001f || MathF.Abs(editHeels.Pitch) > 0.0001f || MathF.Abs(editHeels.Roll) > 0.0001f))
        {
            CurrentPreset.HeelsOffset = new HeelsOffset
            {
                X = editHeels.X, Y = editHeels.Y, Z = editHeels.Z,
                Rotation = editHeels.Rotation, Pitch = editHeels.Pitch, Roll = editHeels.Roll,
            };
        }
        else
        {
            CurrentPreset.HeelsOffset = null;
        }
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
                var destPath = Path.Combine(Plugin.IconsDirectory, $"{Guid.NewGuid()}.png");

                try
                {
                    ResizeAndSaveIcon(sourcePath, destPath);
                }
                catch
                {
                    // ImageSharp unavailable or image load failed - copy source file as-is
                    File.Copy(sourcePath, destPath, true);
                }

                editCustomIconPath = destPath;
                editIconId = null;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error($"Failed to import icon: {ex.Message}");
            }
        };
        browser.Open();
    }

    // isolated so ImageSharp JIT resolution only triggers here (callers can catch + fall back)
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ResizeAndSaveIcon(string sourcePath, string destPath)
    {
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

            // 4. Check for known game commands (hard block - can't intercept native game commands)
            if (EmoteDetectionService.IsKnownGameCommand(cleanCommand))
            {
                commandError = $"/{cleanCommand} is a game command and can't be used as a preset command";
                commandWarning = null;
                return;
            }
        }
        else
        {
            commandWarning = null;
        }
    }

    private const float EditorHorizPad = 14f;

    private static readonly Vector4 Chr_Accent       = new(0.49f, 0.65f, 0.85f, 1f);
    private static readonly Vector4 Chr_AccentBright = new(0.65f, 0.77f, 0.92f, 1f);
    private static readonly Vector4 Chr_AccentDeep   = new(0.40f, 0.53f, 0.72f, 1f);
    private static readonly Vector4 Chr_AccentDark   = new(0.05f, 0.08f, 0.13f, 1f);
    private static readonly Vector4 Chr_Text         = new(0.86f, 0.87f, 0.89f, 1f);
    private static readonly Vector4 Chr_TextDim      = new(0.56f, 0.58f, 0.63f, 1f);
    private static readonly Vector4 Chr_TextFaint    = new(0.36f, 0.38f, 0.45f, 1f);
    private static readonly Vector4 Chr_TextGhost    = new(0.26f, 0.28f, 0.35f, 1f);
    private static readonly Vector4 Chr_Border       = new(0.18f, 0.21f, 0.26f, 1f);
    private static readonly Vector4 Chr_BorderSoft   = new(0.12f, 0.13f, 0.19f, 1f);
    private static readonly Vector4 Chr_Warning      = new(1.00f, 0.72f, 0.30f, 1f);

    // Per-section accents - one rainbow color per card.
    private static readonly Vector4 Sec_Setup      = new(0.49f, 0.65f, 0.85f, 1f); // setup blue
    private static readonly Vector4 Sec_Mod        = new(0.38f, 0.72f, 1.00f, 1f); // sky blue
    private static readonly Vector4 Sec_Emote      = new(0.45f, 0.92f, 0.55f, 1f); // spring green
    private static readonly Vector4 Sec_ModOptions = new(0.72f, 0.52f, 1.00f, 1f); // violet
    private static readonly Vector4 Sec_Modifiers  = new(1.00f, 0.82f, 0.30f, 1f); // amber
    private static readonly Vector4 Sec_Heels      = new(1.00f, 0.42f, 0.70f, 1f); // rose
    private static readonly Vector4 Sec_Conflict   = new(0.28f, 0.88f, 0.92f, 1f); // cyan

    // BeginCard always pushes (balanced with EndCard); `active` tells EndCard if body rendered
    private readonly Stack<(float leftX, float rightX, float bodyTopY, bool active)> cardBodyStarts = new();

    private bool BeginCard(
        string num, string name, Vector4 accent,
        string summary, Vector4 summaryCol,
        ref bool open, bool alwaysOpen = false)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        // symmetric 14/14 card margins (Draw indents left; subtract here for right)
        var availW = ImGui.GetContentRegionAvail().X - EditorHorizPad * UIStyles.Scale;
        // Compact header - 26px (was 30). HTML is 36px relative to
        // its own font size; ours is scaled accordingly.
        float headerH = 26f * scale;
        var headerEnd = new Vector2(start.X + availW, start.Y + headerH);

        bool isOpen = alwaysOpen || open;
        bool hovered = false;

        if (!alwaysOpen)
        {
            ImGui.SetCursorScreenPos(start);
            bool clicked = ImGui.InvisibleButton($"##card_{num}", new Vector2(availW, headerH));
            hovered = ImGui.IsItemHovered();
            if (clicked)
            {
                open = !open;
                isOpen = open;
                cardRippleStart[num] = ImGui.GetTime();
            }
        }

        float bgLeftA  = isOpen ? (hovered ? 0.22f : 0.12f) : (hovered ? 0.10f : 0.05f);
        float bgRightA = isOpen ? 0.03f : 0.01f;
        uint bgL = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, bgLeftA));
        uint bgR = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, bgRightA));
        dl.AddRectFilledMultiColor(start, headerEnd, bgL, bgR, bgR, bgL);

        uint frameBorder = ImGui.ColorConvertFloat4ToU32(Chr_BorderSoft);
        dl.AddLine(start, new Vector2(headerEnd.X, start.Y), frameBorder, 1f);
        dl.AddLine(
            new Vector2(headerEnd.X, start.Y),
            new Vector2(headerEnd.X, headerEnd.Y),
            frameBorder, 1f);
        dl.AddLine(
            new Vector2(start.X, headerEnd.Y),
            new Vector2(headerEnd.X, headerEnd.Y),
            frameBorder, 1f);

        // 3px accent bar; ~500ms brightness pulse on click
        float accentW = 3f * scale;
        Vector4 accentBarCol = accent;
        if (cardRippleStart.TryGetValue(num, out var flashStart))
        {
            float fE = (float)(ImGui.GetTime() - flashStart);
            const float fDur = 0.50f;
            if (fE >= fDur)
            {
                cardRippleStart.Remove(num);
            }
            else
            {
                float tRaw = fE / fDur;
                float te = 1f - MathF.Pow(1f - tRaw, 3f);
                float boost = (1f - te) * 0.55f;
                accentBarCol = new Vector4(
                    MathF.Min(1f, accent.X + boost * (1f - accent.X)),
                    MathF.Min(1f, accent.Y + boost * (1f - accent.Y)),
                    MathF.Min(1f, accent.Z + boost * (1f - accent.Z)),
                    1f);
            }
        }
        dl.AddRectFilled(
            start, new Vector2(start.X + accentW, headerEnd.Y),
            ImGui.ColorConvertFloat4ToU32(accentBarCol));

        uint midBand = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, isOpen ? 0.18f : 0.10f));
        uint midBandClr = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0f));
        float midBandY = start.Y + headerH * 0.5f;
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.18f, midBandY),
            new Vector2(start.X + availW * 0.50f, midBandY + 1f),
            midBandClr, midBand, midBand, midBandClr);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.50f, midBandY),
            new Vector2(start.X + availW * 0.82f, midBandY + 1f),
            midBand, midBandClr, midBandClr, midBand);

        var textH = ImGui.GetTextLineHeight();
        float labelY = start.Y + (headerH - textH) * 0.5f;
        float cursorX = start.X + accentW + 12f * scale;

        // Chevron - rotates smoothly between > (closed, 0deg) and
        // v (open, 90deg CW) instead of snapping. Per-card angle
        // eases toward the target each frame for a tactile arc.
        {
            float chevSize = 4f * scale;
            float chevCenterX = cursorX + chevSize;
            float chevCenterY = start.Y + headerH * 0.5f;

            float targetAngle = isOpen ? MathF.PI * 0.5f : 0f;
            if (!cardChevronAngle.TryGetValue(num, out var curAngle))
                curAngle = targetAngle;  // no animation on first frame
            // Ease toward target (8 rad/sec ~= ~200ms for 90deg).
            float delta = targetAngle - curAngle;
            float step = ImGui.GetIO().DeltaTime * 8f;
            if (MathF.Abs(delta) <= step) curAngle = targetAngle;
            else curAngle += MathF.Sign(delta) * step;
            cardChevronAngle[num] = curAngle;

            // chevron points rotate around center: 0deg = >, 90deg = v
            float cos = MathF.Cos(curAngle);
            float sin = MathF.Sin(curAngle);
            Vector2 Rot(float x, float y) =>
                new(chevCenterX + (x * cos - y * sin),
                    chevCenterY + (x * sin + y * cos));
            var pTop    = Rot(-chevSize * 0.5f, -chevSize);
            var pCorner = Rot( chevSize * 0.55f,  0f);
            var pBot    = Rot(-chevSize * 0.5f,  chevSize);

            // Color lerps from TextFaint (closed) to accent (open)
            // based on how far through the arc we are.
            float t01 = curAngle / (MathF.PI * 0.5f);
            t01 = MathF.Max(0f, MathF.Min(1f, t01));
            var chevCol = new Vector4(
                Chr_TextFaint.X + (accent.X - Chr_TextFaint.X) * t01,
                Chr_TextFaint.Y + (accent.Y - Chr_TextFaint.Y) * t01,
                Chr_TextFaint.Z + (accent.Z - Chr_TextFaint.Z) * t01,
                1f);
            uint chevColU = ImGui.ColorConvertFloat4ToU32(chevCol);
            dl.AddLine(pTop, pCorner, chevColU, 1.3f);
            dl.AddLine(pCorner, pBot, chevColU, 1.3f);
            cursorX += chevSize * 2 + 10f * scale;
        }

        // Card number - tighter 0.8px tracking, less chunk.
        UIStyles.DrawTrackedText(dl, new Vector2(cursorX, labelY), num,
            ImGui.ColorConvertFloat4ToU32(accent), 0.8f * scale);
        float numW = UIStyles.MeasureTrackedWidth(num, 0.8f * scale);
        cursorX += numW + 12f * scale;

        // Card name - tighter 1.5px tracking. Still tracked-caps
        // but doesn't dominate the header at Dalamud's native font.
        float nameTrack = 1.5f * scale;
        string nameUpper = name.ToUpperInvariant();
        float nameW = UIStyles.MeasureTrackedWidth(nameUpper, nameTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursorX, labelY), nameUpper,
            ImGui.ColorConvertFloat4ToU32(isOpen ? Chr_Text : Chr_TextDim), nameTrack);

        if (!string.IsNullOrEmpty(summary))
        {
            float chipTrack = 0.8f * scale;
            float chipPadX = 5f * scale;
            float chipPadY = 1f * scale;
            float chipRight = headerEnd.X - 8f * scale;
            var chipGlyphSz = ImGui.CalcTextSize("A");

            // Reserve space for the full card name + a 12px gap
            // after it. Chip is never allowed past this left boundary.
            float minChipLeft = cursorX + nameW + 12f * scale;
            // Hard upper bound on chip width so even long names cap
            // out at a readable-pill size instead of ballooning.
            float maxChipTextW = 110f * scale;

            float chipTextW = MathF.Min(
                UIStyles.MeasureTrackedWidth(summary, chipTrack),
                maxChipTextW);
            float chipLeft = chipRight - chipTextW - chipPadX * 2;
            if (chipLeft < minChipLeft)
            {
                float availChipTextW = (chipRight - minChipLeft) - chipPadX * 2;
                if (availChipTextW < 20f * scale) availChipTextW = 20f * scale;
                chipTextW = MathF.Min(availChipTextW, maxChipTextW);
                chipLeft = chipRight - chipTextW - chipPadX * 2;
            }

            string chipDisplay = TruncateTrackedToFit(summary, chipTextW, chipTrack);
            float chipTop = start.Y + (headerH - chipGlyphSz.Y - chipPadY * 2) * 0.5f;
            float chipBot = chipTop + chipGlyphSz.Y + chipPadY * 2;
            dl.AddRectFilled(
                new Vector2(chipLeft, chipTop),
                new Vector2(chipRight, chipBot),
                ImGui.ColorConvertFloat4ToU32(new Vector4(summaryCol.X, summaryCol.Y, summaryCol.Z, 0.08f)));
            dl.AddRect(
                new Vector2(chipLeft, chipTop),
                new Vector2(chipRight, chipBot),
                ImGui.ColorConvertFloat4ToU32(new Vector4(summaryCol.X, summaryCol.Y, summaryCol.Z, 0.40f)),
                0f, 0, 1f);
            UIStyles.DrawTrackedText(dl,
                new Vector2(chipLeft + chipPadX, chipTop + chipPadY),
                chipDisplay, ImGui.ColorConvertFloat4ToU32(summaryCol), chipTrack);
        }

        // Advance cursor past the header.
        ImGui.SetCursorScreenPos(new Vector2(start.X, headerEnd.Y));
        ImGui.Dummy(new Vector2(1, 0));

        if (isOpen)
        {
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);
            cardBodyStarts.Push((start.X, start.X + availW, ImGui.GetCursorScreenPos().Y, true));
            // Tighter inner indent (was 16) so bodies stay compact.
            ImGui.Indent(10f * scale);
            ImGui.Dummy(new Vector2(1, 4f * scale));
            return true;
        }
        else
        {
            cardBodyStarts.Push((start.X, start.X + availW, start.Y, false));
            ImGui.Dummy(new Vector2(1, 6f * scale));
            return false;
        }
    }

    private void EndCard()
    {
        var scale = UIStyles.Scale;
        var (leftX, rightX, bodyTopY, active) = cardBodyStarts.Pop();
        if (!active) return;

        ImGui.Unindent(10f * scale);
        ImGui.Dummy(new Vector2(1, 6f * scale));
        float bottomY = ImGui.GetCursorScreenPos().Y;

        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSetCurrent(0);

        uint bodyFill = ImGui.ColorConvertFloat4ToU32(
            new Vector4(0.078f, 0.086f, 0.110f, 1f));
        dl.AddRectFilled(
            new Vector2(leftX, bodyTopY),
            new Vector2(rightX, bottomY),
            bodyFill);

        uint frameBorder = ImGui.ColorConvertFloat4ToU32(Chr_BorderSoft);
        dl.AddLine(new Vector2(leftX,  bodyTopY), new Vector2(leftX,  bottomY), frameBorder, 1f);
        dl.AddLine(new Vector2(rightX, bodyTopY), new Vector2(rightX, bottomY), frameBorder, 1f);
        dl.AddLine(new Vector2(leftX,  bottomY), new Vector2(rightX, bottomY),  frameBorder, 1f);

        dl.ChannelsMerge();
        ImGui.Dummy(new Vector2(1, 10f * scale));
    }

    private float CurrentCardRight()
    {
        if (cardBodyStarts.Count > 0) return cardBodyStarts.Peek().rightX;
        return ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
    }

    // GetContentRegionAvail() overshoots into the scrollbar gutter; use this for SetNextItemWidth
    private float CardInnerWidth()
    {
        float cursorX = ImGui.GetCursorScreenPos().X;
        return CurrentCardRight() - cursorX - 10f * UIStyles.Scale;
    }

    private void DrawRibbon()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var ribbonH = 30f * scale;
        var end = new Vector2(start.X + availW, start.Y + ribbonH);

        uint bgTop = ImGui.ColorConvertFloat4ToU32(new Vector4(0.047f, 0.055f, 0.071f, 1f));
        uint bgBot = ImGui.ColorConvertFloat4ToU32(new Vector4(0.024f, 0.031f, 0.043f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        uint aSolid = ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.55f));
        uint aClear = ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            start, new Vector2(start.X + availW * 0.42f, start.Y + 1f),
            aSolid, aClear, aClear, aSolid);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.58f, start.Y),
            new Vector2(end.X, start.Y + 1f),
            aClear, aSolid, aSolid, aClear);

        uint aSoft = ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.30f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, end.Y - 1f),
            new Vector2(start.X + availW * 0.5f, end.Y),
            aClear, aSoft, aSoft, aClear);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.5f, end.Y - 1f),
            end,
            aSoft, aClear, aClear, aSoft);

        float padX = 14f * scale;
        var textH = ImGui.GetTextLineHeight();
        float textY = start.Y + (ribbonH - textH) * 0.5f;
        float pipCenterY = start.Y + ribbonH * 0.5f;
        float pipX = start.X + padX;

        // Breathing glow behind the icon (matches HTML pip-mark
        // ::after pulse).
        {
            float glowT = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * MathF.Tau / 2.2f);
            float glowBase = 0.20f + 0.10f * glowT;
            float gcx = pipX + 7f * scale;
            float gcy = pipCenterY;
            for (int r = 4; r >= 1; r--)
            {
                float pad = r * 2.2f * scale;
                float a = glowBase * (0.20f / r);
                uint halo = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, a));
                dl.AddRectFilled(
                    new Vector2(gcx - pad, gcy - pad),
                    new Vector2(gcx + pad, gcy + pad),
                    halo);
            }
        }

        // The icon itself.
        ImGui.PushFont(UiBuilder.IconFont);
        var pipGlyph = FontAwesomeIcon.Edit.ToIconString();
        var pipSz = ImGui.CalcTextSize(pipGlyph);
        dl.AddText(
            new Vector2(pipX, pipCenterY - pipSz.Y * 0.5f),
            ImGui.ColorConvertFloat4ToU32(Chr_Accent),
            pipGlyph);
        float pipAdvance = pipSz.X;
        ImGui.PopFont();

        float metaX = pipX + pipAdvance + 10f * scale;
        string label = "STAGE PLOT";
        string sep   = "  -  ";
        string mode  = IsNewPreset ? "NEW" : "EDIT";
        string nameRaw = string.IsNullOrWhiteSpace(editName)
            ? (IsNewPreset ? "UNTITLED" : "-")
            : editName.ToUpperInvariant();

        // Ribbon meta text - tracked via per-glyph spacing.
        float metaTrack = 1.5f * scale;
        float metaCursor = metaX;
        float labelW = UIStyles.MeasureTrackedWidth(label, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(metaCursor, textY), label,
            ImGui.ColorConvertFloat4ToU32(Chr_TextDim), metaTrack);
        metaCursor += labelW;
        var sepSz = ImGui.CalcTextSize(sep);
        dl.AddText(new Vector2(metaCursor, textY),
            ImGui.ColorConvertFloat4ToU32(Chr_TextFaint), sep);
        metaCursor += sepSz.X;
        float modeW = UIStyles.MeasureTrackedWidth(mode, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(metaCursor, textY), mode,
            ImGui.ColorConvertFloat4ToU32(Chr_Accent), metaTrack);
        metaCursor += modeW;
        string sepBeforeName = "  -  ";
        float sepBeforeNameW = UIStyles.MeasureTrackedWidth(sepBeforeName, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(metaCursor, textY), sepBeforeName,
            ImGui.ColorConvertFloat4ToU32(Chr_TextDim), metaTrack);
        metaCursor += sepBeforeNameW;
        // reserve max tag width (MODIFIED + pad + dot) on the right
        float tagReserve = 96f * scale;
        float nameBudget = (end.X - padX - tagReserve) - metaCursor;
        string nameShown = TruncateTrackedToFit(nameRaw, MathF.Max(20f * scale, nameBudget), metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(metaCursor, textY), nameShown,
            ImGui.ColorConvertFloat4ToU32(Chr_TextDim), metaTrack);

        // Dirty/clean tag - compact, pilled. Tracked-caps text
        // sized down via tight tracking, smaller dot, tighter pad.
        bool dirty = !string.IsNullOrWhiteSpace(editName)
                  || !string.IsNullOrWhiteSpace(editCommand)
                  || !string.IsNullOrWhiteSpace(editModDirectory);
        string tagText = dirty ? "MODIFIED" : "CLEAN";
        float tagTrack = 1.0f * scale;
        float tagTextW = UIStyles.MeasureTrackedWidth(tagText, tagTrack);
        float tagPadX = 6f * scale;
        float tagPadY = 2f * scale;
        float dotR = 2f * scale;
        float dotGap = 4f * scale;
        float tagInnerW = dotR * 2 + dotGap + tagTextW;
        float tagRight = end.X - padX;
        float tagLeft = tagRight - tagInnerW - tagPadX * 2;
        float tagTop = textY - tagPadY;
        float tagBot = textY + textH + tagPadY;
        var tagCol = dirty ? Chr_Accent : Chr_TextFaint;
        dl.AddRectFilled(
            new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
            ImGui.ColorConvertFloat4ToU32(new Vector4(tagCol.X, tagCol.Y, tagCol.Z, 0.05f)));
        dl.AddRect(
            new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
            ImGui.ColorConvertFloat4ToU32(tagCol), 0f, 0, 1f);
        dl.AddCircleFilled(
            new Vector2(tagLeft + tagPadX + dotR, textY + textH * 0.5f),
            dotR, ImGui.ColorConvertFloat4ToU32(tagCol));
        UIStyles.DrawTrackedText(dl,
            new Vector2(tagLeft + tagPadX + dotR * 2 + dotGap, textY),
            tagText, ImGui.ColorConvertFloat4ToU32(tagCol), tagTrack);

        ImGui.Dummy(new Vector2(1, ribbonH));
    }

    private void DrawMarquee()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var marqueeH = 104f * scale;
        var end = new Vector2(start.X + availW, start.Y + marqueeH);
        float t = (float)ImGui.GetTime();

        uint bgTop = ImGui.ColorConvertFloat4ToU32(new Vector4(0.075f, 0.090f, 0.134f, 1f));
        uint bgBot = ImGui.ColorConvertFloat4ToU32(new Vector4(0.039f, 0.051f, 0.078f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        // Slow spotlight drifting across the top - marquee vibe.
        {
            float st = 0.5f + 0.5f * MathF.Sin(t * MathF.Tau / 13f);
            float cx = start.X + availW * (0.30f + 0.40f * st);
            float cy = start.Y + marqueeH * 0.20f;
            const int layers = 14;
            for (int l = layers - 1; l >= 0; l--)
            {
                float u = (float)l / (layers - 1);
                float r = 180f * scale * (0.12f + 0.88f * u);
                float fall = (1f - u) * (1f - u);
                float a = 0.028f * fall;
                dl.AddCircleFilled(
                    new Vector2(cx, cy), r,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(Chr_AccentBright.X, Chr_AccentBright.Y, Chr_AccentBright.Z, a)),
                    40);
            }
        }

        uint hSolid = ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.70f));
        uint hClear = ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0f));
        float hairLeft = start.X + EditorHorizPad * scale;
        float hairRight = end.X - EditorHorizPad * scale;
        float hairMid = (hairLeft + hairRight) * 0.5f;
        dl.AddRectFilledMultiColor(
            new Vector2(hairLeft, end.Y - 1f),
            new Vector2(hairMid, end.Y),
            hClear, hSolid, hSolid, hClear);
        dl.AddRectFilledMultiColor(
            new Vector2(hairMid, end.Y - 1f),
            new Vector2(hairRight, end.Y),
            hSolid, hClear, hClear, hSolid);

        // Corner brackets - top-left / top-right.
        float bSize = 14f * scale;
        float bInset = 8f * scale;
        uint bCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.45f));
        dl.AddLine(
            new Vector2(start.X + bInset, start.Y + bInset),
            new Vector2(start.X + bInset + bSize, start.Y + bInset),
            bCol, 1f);
        dl.AddLine(
            new Vector2(start.X + bInset, start.Y + bInset),
            new Vector2(start.X + bInset, start.Y + bInset + bSize),
            bCol, 1f);
        dl.AddLine(
            new Vector2(end.X - bInset - bSize, start.Y + bInset),
            new Vector2(end.X - bInset, start.Y + bInset),
            bCol, 1f);
        dl.AddLine(
            new Vector2(end.X - bInset, start.Y + bInset),
            new Vector2(end.X - bInset, start.Y + bInset + bSize),
            bCol, 1f);

        // "LIVE PREVIEW" kicker, centered with a dashed tick on each side.
        string kicker = "LIVE  PREVIEW";
        var kickerSz = ImGui.CalcTextSize(kicker);
        float kickerY = start.Y + 10f * scale;
        float tickW = 26f * scale;
        uint tickCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.55f));
        float totalW = kickerSz.X + 2 * (tickW + 8f * scale);
        float kickerCenterX = start.X + availW * 0.5f;
        float tickY = kickerY + kickerSz.Y * 0.5f;
        dl.AddLine(
            new Vector2(kickerCenterX - totalW * 0.5f, tickY),
            new Vector2(kickerCenterX - totalW * 0.5f + tickW, tickY),
            tickCol, 1f);
        dl.AddText(
            new Vector2(kickerCenterX - kickerSz.X * 0.5f, kickerY),
            ImGui.ColorConvertFloat4ToU32(Chr_TextFaint), kicker);
        dl.AddLine(
            new Vector2(kickerCenterX + kickerSz.X * 0.5f + 8f * scale, tickY),
            new Vector2(kickerCenterX + kickerSz.X * 0.5f + 8f * scale + tickW, tickY),
            tickCol, 1f);

        // must match EditorHorizPad so accent bars align vertically
        float cardPadX = EditorHorizPad * scale;
        float cardTop = kickerY + kickerSz.Y + 8f * scale;
        float cardH = end.Y - cardTop - 12f * scale;
        float cardLeft = start.X + cardPadX;
        float cardRight = end.X - cardPadX;
        var cardMin = new Vector2(cardLeft, cardTop);
        var cardMax = new Vector2(cardRight, cardTop + cardH);

        bool hasName = !string.IsNullOrWhiteSpace(editName);
        bool hasMod = !string.IsNullOrWhiteSpace(editModName);
        bool cardPopulated = hasName || hasMod || editIconId.HasValue
                          || !string.IsNullOrWhiteSpace(editCustomIconPath);

        // Card background - accent tint when populated, dim otherwise.
        uint cardBgL = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, cardPopulated ? 0.08f : 0.03f));
        uint cardBgR = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, cardPopulated ? 0.02f : 0.01f));
        dl.AddRectFilledMultiColor(cardMin, cardMax, cardBgL, cardBgR, cardBgR, cardBgL);
        uint cardBorder = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, cardPopulated ? 0.30f : 0.12f));
        dl.AddRect(cardMin, cardMax, cardBorder, 0f, 0, 1f);
        // Left accent bar - static accent when populated, faint
        // when empty.
        float cbarW = 3f * scale;
        dl.AddRectFilled(
            cardMin, new Vector2(cardLeft + cbarW, cardMax.Y),
            ImGui.ColorConvertFloat4ToU32(cardPopulated
                ? Chr_Accent
                : new Vector4(Chr_TextFaint.X, Chr_TextFaint.Y, Chr_TextFaint.Z, 1f)));

        // Icon tile on the left of the card.
        float iconSize = cardH - 12f * scale;
        float iconX = cardLeft + cbarW + 8f * scale;
        float iconY = cardTop + 6f * scale;
        var iconMin = new Vector2(iconX, iconY);
        var iconMax = new Vector2(iconX + iconSize, iconY + iconSize);

        bool drewIcon = false;
        if (!string.IsNullOrEmpty(editCustomIconPath) && File.Exists(editCustomIconPath))
        {
            var tex = GetCustomIcon(editCustomIconPath);
            if (tex != null)
            {
                var (uv0, uv1) = MainWindow.CalcIconUV(
                    tex.Width, tex.Height,
                    editIconZoom, editIconOffsetX, editIconOffsetY);
                dl.AddImage(tex.Handle, iconMin, iconMax, uv0, uv1);
                drewIcon = true;
            }
        }
        else if (editIconId.HasValue)
        {
            var tex = GetGameIcon(editIconId.Value);
            if (tex != null)
            {
                dl.AddImage(tex.Handle, iconMin, iconMax);
                drewIcon = true;
            }
        }
        if (!drewIcon)
        {
            // Empty-icon placeholder - dashed border in TextGhost
            // with an oversized "?" glyph at the center, matching
            // the HTML mockup's `.icon-tile.placeholder`.
            uint placeholderBg = ImGui.ColorConvertFloat4ToU32(
                new Vector4(0.125f, 0.140f, 0.180f, 1f));
            uint placeholderBorder = ImGui.ColorConvertFloat4ToU32(Chr_TextGhost);
            dl.AddRectFilled(iconMin, iconMax, placeholderBg);
            DrawDashedRect(dl, iconMin, iconMax, placeholderBorder, 4f * scale, 3f * scale, 1f);

            string q = "?";
            // 30px TitleFont fills the 52px tile like the HTML's 22px does its 64px tile
            var placeholderFont = Plugin.Instance?.TitleFont;
            if (placeholderFont is { Available: true })
            {
                using (placeholderFont.Push())
                {
                    var qSz = ImGui.CalcTextSize(q);
                    dl.AddText(
                        new Vector2(iconX + (iconSize - qSz.X) * 0.5f,
                                    iconY + (iconSize - qSz.Y) * 0.5f),
                        placeholderBorder, q);
                }
            }
            else
            {
                var qSz = ImGui.CalcTextSize(q);
                dl.AddText(
                    new Vector2(iconX + (iconSize - qSz.X) * 0.5f,
                                iconY + (iconSize - qSz.Y) * 0.5f),
                    placeholderBorder, q);
            }
        }
        else
        {
            // Real icon - draw a 1px hairline border over the image
            // (same cardBorder color). Skip for the placeholder so
            // its dashed border isn't double-framed.
            dl.AddRect(iconMin, iconMax, cardBorder, 0f, 0, 1f);
        }

        // Text block to the right of the icon.
        float textX = iconMax.X + 12f * scale;
        float textAvailRight = cardRight - 10f * scale;
        float textAvail = textAvailRight - textX;
        float nameY = cardTop + 6f * scale;

        string displayName = hasName ? editName.ToUpperInvariant() : "Untitled preset...";
        var nameCol = hasName ? Chr_Text : Chr_TextFaint;
        float nameHeight = ImGui.GetTextLineHeight();
        if (hasName)
        {
            float nameTrack = 1.4f * scale;
            string truncName = TruncateTrackedToFit(displayName, textAvail, nameTrack);
            UIStyles.DrawTrackedText(dl, new Vector2(textX, nameY), truncName,
                ImGui.ColorConvertFloat4ToU32(nameCol), nameTrack);
        }
        else
        {
            dl.AddText(new Vector2(textX, nameY),
                ImGui.ColorConvertFloat4ToU32(nameCol),
                TruncateToFit(displayName, textAvail));
        }

        // tracked mono-caps "/COMMAND - MOD NAME" at 1.2px per-char
        float metaY = nameY + nameHeight + 6f * scale;
        float metaTrack = 1.2f * scale;
        string cmdStr = string.IsNullOrWhiteSpace(editCommand)
            ? "NO COMMAND"
            : ("/" + editCommand).ToUpperInvariant();
        string modStr = hasMod
            ? editModName.ToUpperInvariant()
            : (isVanillaPreset ? "VANILLA" : "NO MOD SELECTED");

        var cmdCol = string.IsNullOrWhiteSpace(editCommand) ? Chr_TextFaint : Chr_Accent;
        float cmdW = UIStyles.MeasureTrackedWidth(cmdStr, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(textX, metaY), cmdStr,
            ImGui.ColorConvertFloat4ToU32(cmdCol), metaTrack);
        float sepX = textX + cmdW + 8f * scale;
        // Dot separator.
        dl.AddCircleFilled(
            new Vector2(sepX + 2f * scale, metaY + ImGui.GetTextLineHeight() * 0.5f),
            1.6f * scale,
            ImGui.ColorConvertFloat4ToU32(new Vector4(Chr_TextFaint.X, Chr_TextFaint.Y, Chr_TextFaint.Z, 0.5f)));
        float modX = sepX + 8f * scale;
        float modAvail = textAvailRight - modX;
        string truncMod = TruncateTrackedToFit(modStr, modAvail, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(modX, metaY), truncMod,
            ImGui.ColorConvertFloat4ToU32(Chr_TextDim), metaTrack);

        ImGui.Dummy(new Vector2(1, marqueeH));
    }

    // Draws a dashed rectangle - 4 sides each segmented into
    // `dashLen` pixel strokes separated by `gapLen` gaps. ImGui has
    // no native dashed rect, so we walk each edge as line segments.
    private static void DrawDashedRect(
        ImDrawListPtr dl, Vector2 min, Vector2 max, uint color,
        float dashLen, float gapLen, float thickness)
    {
        float step = dashLen + gapLen;
        // Top + bottom.
        for (float x = min.X; x < max.X; x += step)
        {
            float xe = MathF.Min(x + dashLen, max.X);
            dl.AddLine(new Vector2(x, min.Y), new Vector2(xe, min.Y), color, thickness);
            dl.AddLine(new Vector2(x, max.Y), new Vector2(xe, max.Y), color, thickness);
        }
        // Left + right.
        for (float y = min.Y; y < max.Y; y += step)
        {
            float ye = MathF.Min(y + dashLen, max.Y);
            dl.AddLine(new Vector2(min.X, y), new Vector2(min.X, ye), color, thickness);
            dl.AddLine(new Vector2(max.X, y), new Vector2(max.X, ye), color, thickness);
        }
    }

    // 7 dots, one per card; lights when "armed" with content
    private void DrawTicker()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        float tickerH = 42f * scale;
        var end = new Vector2(start.X + availW, start.Y + tickerH);

        uint bg = ImGui.ColorConvertFloat4ToU32(new Vector4(0.031f, 0.043f, 0.070f, 1f));
        dl.AddRectFilled(start, end, bg);

        // 2-rect bright-center fade
        uint lineSolid = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.40f));
        uint lineClear = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0f));
        float lineLeft = start.X + EditorHorizPad * scale;
        float lineRight = end.X - EditorHorizPad * scale;
        float lineMid = (lineLeft + lineRight) * 0.5f;
        dl.AddRectFilledMultiColor(
            new Vector2(lineLeft, end.Y - 1f),
            new Vector2(lineMid, end.Y),
            lineClear, lineSolid, lineSolid, lineClear);
        dl.AddRectFilledMultiColor(
            new Vector2(lineMid, end.Y - 1f),
            new Vector2(lineRight, end.Y),
            lineSolid, lineClear, lineClear, lineSolid);

        // Tick definitions - color + 3-char label + tooltip + armed flag.
        var ticks = new (Vector4 color, string label, string tooltip, bool armed)[]
        {
            (Sec_Setup,      "SET", "Setup - name + command + icon",
                !string.IsNullOrWhiteSpace(editName) || !string.IsNullOrWhiteSpace(editCommand)),
            (Sec_Mod,        "MOD", "Mod selection",
                isVanillaPreset || !string.IsNullOrEmpty(editModDirectory)),
            (Sec_Emote,      "EMO", "Emote to use",
                !string.IsNullOrWhiteSpace(editEmoteCommand)),
            (Sec_ModOptions, "OPT", "Mod settings", editModOptions.Count > 0),
            (Sec_Modifiers,  "MDF", "Modifiers",    editModifiers.Count > 0),
            (Sec_Heels,      "HEL", "Heels offset", editHeelsEnabled),
            (Sec_Conflict,   "CON", "Conflict handling", editConflictExclusions.Count > 0),
        };

        float padX = 14f * scale;
        float rowW = availW - padX * 2;
        float tickW = rowW / ticks.Length;
        float dotSize = 8f * scale;
        // Top padding so dots sit well inside the ticker, not
        // jammed against the marquee divider.
        float dotY = start.Y + 8f * scale;
        float labelTrack = 0.5f * scale;
        var labelH = ImGui.GetTextLineHeight();
        float labelY = dotY + dotSize + 4f * scale;

        for (int i = 0; i < ticks.Length; i++)
        {
            var (col, label, tooltip, armed) = ticks[i];
            float cx = start.X + padX + tickW * (i + 0.5f);
            float dotX = cx - dotSize * 0.5f;
            var dMin = new Vector2(dotX, dotY);
            var dMax = new Vector2(dotX + dotSize, dotY + dotSize);

            ImGui.SetCursorScreenPos(new Vector2(start.X + padX + tickW * i, start.Y));
            ImGui.InvisibleButton($"##tick_{i}", new Vector2(tickW, tickerH));
            bool hovered = ImGui.IsItemHovered();
            if (hovered)
            {
                UIStyles.EncoreTooltip(tooltip);
            }

            // unarmed -> armed transition fires one-shot ring pulse
            if (armed && !tickerPrevArmed[i])
            {
                tickerPulseStart[i] = ImGui.GetTime();
            }
            tickerPrevArmed[i] = armed;

            if (armed)
            {
                // Phase-offset breathing so the row reads as a
                // running signal meter rather than synced blinking.
                float breathT = (float)(ImGui.GetTime() * MathF.Tau / 2.4f) + i * 0.32f;
                float breath = 0.82f + 0.18f * (0.5f + 0.5f * MathF.Sin(breathT));
                for (int h = 3; h >= 1; h--)
                {
                    float pad = h * 2f * scale;
                    float a = (0.18f / h) * breath;
                    uint halo = ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, a));
                    dl.AddRectFilled(
                        new Vector2(dMin.X - pad, dMin.Y - pad),
                        new Vector2(dMax.X + pad, dMax.Y + pad),
                        halo);
                }
                dl.AddRectFilled(dMin, dMax, ImGui.ColorConvertFloat4ToU32(col));
            }
            else
            {
                dl.AddRectFilled(dMin, dMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, 0.05f)));
                dl.AddRect(dMin, dMax,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(col.X, col.Y, col.Z, hovered ? 0.70f : 0.45f)),
                    0f, 0, 1f);
            }

            // Arm-up ring pulse - expands outward + fades over
            // 500ms, easeOutCubic. Drawn AFTER the dot so it radiates
            // from the dot rather than being covered by it.
            if (tickerPulseStart[i] >= 0)
            {
                float pElapsed = (float)(ImGui.GetTime() - tickerPulseStart[i]);
                const float pDur = 0.5f;
                if (pElapsed >= pDur)
                {
                    tickerPulseStart[i] = -1;
                }
                else
                {
                    float tRaw = pElapsed / pDur;
                    float te = 1f - MathF.Pow(1f - tRaw, 3f);
                    var ctr = new Vector2((dMin.X + dMax.X) * 0.5f, (dMin.Y + dMax.Y) * 0.5f);
                    float r = dotSize * 0.5f + te * 14f * scale;
                    float a = (1f - te) * 0.70f;
                    dl.AddCircle(ctr, r,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(col.X, col.Y, col.Z, a)),
                        24, 1.5f);
                }
            }

            // 3-char label below the dot, tracked-caps via tight
            // per-glyph spacing so it reads compact. TextFaint
            // normally, section color when armed.
            var labelCol = armed ? col : Chr_TextFaint;
            float labelW = UIStyles.MeasureTrackedWidth(label, labelTrack);
            UIStyles.DrawTrackedText(dl,
                new Vector2(cx - labelW * 0.5f, labelY), label,
                ImGui.ColorConvertFloat4ToU32(labelCol), labelTrack);
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(1, tickerH));
    }

    // Uniform field label - tracked caps, TextGhost color, tight
    // tracking so it reads as a small quiet eyebrow above the input.
    private void DrawFieldLabel(string text, Vector4? color = null)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float track = 1.0f * scale;
        UIStyles.DrawTrackedText(dl, pos, text.ToUpperInvariant(),
            ImGui.ColorConvertFloat4ToU32(color ?? Chr_TextGhost),
            track);
        ImGui.Dummy(new Vector2(
            UIStyles.MeasureTrackedWidth(text.ToUpperInvariant(), track),
            ImGui.GetTextLineHeight()));
    }

    // Truncate with ellipsis if a string would overflow a given pixel width.
    // Uses three literal dots "..." rather than the Unicode U+2026 glyph,
    // which renders mid-height in Dalamud's default font.
    private static string TruncateToFit(string s, float maxW)
    {
        if (maxW <= 0) return "";
        if (ImGui.CalcTextSize(s).X <= maxW) return s;
        const string ell = "...";
        float ellW = ImGui.CalcTextSize(ell).X;
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float w = ImGui.CalcTextSize(s.Substring(0, mid)).X + ellW;
            if (w <= maxW) lo = mid;
            else hi = mid - 1;
        }
        return lo <= 0 ? ell : s.Substring(0, lo) + ell;
    }

    // Same as TruncateToFit but measures via tracked width (matches
    // what DrawTrackedText will actually render). Needed so tracked
    // strings truncate accurately.
    private static string TruncateTrackedToFit(string s, float maxW, float track)
    {
        if (maxW <= 0) return "";
        if (UIStyles.MeasureTrackedWidth(s, track) <= maxW) return s;
        const string ell = "...";
        float ellW = UIStyles.MeasureTrackedWidth(ell, track);
        int lo = 0, hi = s.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float w = UIStyles.MeasureTrackedWidth(s.Substring(0, mid), track) + ellW;
            if (w <= maxW) lo = mid;
            else hi = mid - 1;
        }
        return lo <= 0 ? ell : s.Substring(0, lo) + ell;
    }

    // 
    //  FOOTER - Save + Cancel
    // 
    private void DrawFooter()
    {
        ValidateUniqueness();
        bool isValid = IsFormValid();

        // Delayed close after Save click - lets the kick-scale
        // animation finish before the window actually disappears.
        if (savePendingClose && ImGui.GetTime() >= savePendingCloseAt)
        {
            SavePreset();
            Confirmed = true;
            IsOpen = false;
            savePendingClose = false;
        }

        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        float footerH = 48f * scale;
        var end = new Vector2(start.X + availW, start.Y + footerH);

        uint bgCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(0.031f, 0.039f, 0.055f, 1f));
        dl.AddRectFilled(start, end, bgCol);
        dl.AddLine(start, new Vector2(end.X, start.Y),
            ImGui.ColorConvertFloat4ToU32(Chr_Border), 1f);

        float padX = 18f * scale;
        float contentY = start.Y + footerH * 0.5f;
        var textH = ImGui.GetTextLineHeight();
        float textY = contentY - textH * 0.5f;

        // Status line - just a colored state dot + text, no
        // redundant "READY" label. Dot color signals the state
        // class at a glance (red=error, amber=required, accent=go).
        string statusText;
        Vector4 statusCol;
        if (nameError != null) { statusText = nameError; statusCol = new Vector4(1f, 0.45f, 0.45f, 1f); }
        else if (commandError != null) { statusText = commandError; statusCol = new Vector4(1f, 0.45f, 0.45f, 1f); }
        else if (string.IsNullOrWhiteSpace(editName)) { statusText = "Preset name required"; statusCol = new Vector4(1f, 0.72f, 0.30f, 1f); }
        else if (!isValid) { statusText = "Select a mod (or mark as vanilla)"; statusCol = new Vector4(1f, 0.72f, 0.30f, 1f); }
        else { statusText = "Ready to save"; statusCol = Chr_Accent; }

        // Soft-pulsing status dot - breathes on the valid/"ready"
        // state so the footer feels alive; static otherwise.
        float dotR = 3.5f * scale;
        float dotX = start.X + padX + dotR;
        float dotY = contentY;
        float dotAlpha = 1f;
        if (isValid && nameError == null && commandError == null)
        {
            float t = (float)(ImGui.GetTime() * MathF.Tau / 2.2f);
            dotAlpha = 0.75f + 0.25f * (0.5f + 0.5f * MathF.Sin(t));
        }
        // Soft halo behind dot.
        for (int r = 3; r >= 1; r--)
        {
            float pad = r * 1.5f * scale;
            float a = (0.18f / r) * dotAlpha;
            dl.AddCircleFilled(new Vector2(dotX, dotY), dotR + pad,
                ImGui.ColorConvertFloat4ToU32(new Vector4(statusCol.X, statusCol.Y, statusCol.Z, a)));
        }
        dl.AddCircleFilled(new Vector2(dotX, dotY), dotR,
            ImGui.ColorConvertFloat4ToU32(new Vector4(statusCol.X, statusCol.Y, statusCol.Z, dotAlpha)));
        dl.AddText(new Vector2(dotX + dotR + 8f * scale, textY),
            ImGui.ColorConvertFloat4ToU32(statusCol), statusText);

        // Buttons on the right: Cancel (ghost) then Save (accent halo).
        float btnH = 28f * scale;
        float saveBtnW = 96f * scale;
        float cancelBtnW = 72f * scale;
        float btnGap = 10f * scale;

        float saveX = end.X - padX - saveBtnW;
        float saveY = contentY - btnH * 0.5f;
        float cancelX = saveX - btnGap - cancelBtnW;

        // Cancel - ghost outline. Hover glows the border toward
        // accent + softens the bg. Click has a brief press-down
        // alpha dip via ImGui's native ButtonActive state.
        var cancelMin = new Vector2(cancelX, saveY);
        var cancelMax = new Vector2(cancelX + cancelBtnW, saveY + btnH);
        bool cancelHovered = ImGui.IsMouseHoveringRect(cancelMin, cancelMax);
        // Subtle accent-glow halo around Cancel on hover.
        if (cancelHovered)
        {
            for (int r = 3; r >= 1; r--)
            {
                float pad = r * 1.5f * scale;
                float a = 0.10f / r;
                uint halo = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(Chr_TextDim.X, Chr_TextDim.Y, Chr_TextDim.Z, a));
                dl.AddRectFilled(
                    new Vector2(cancelMin.X - pad, cancelMin.Y - pad),
                    new Vector2(cancelMax.X + pad, cancelMax.Y + pad),
                    halo);
            }
        }
        ImGui.SetCursorScreenPos(cancelMin);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.20f, 0.25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.24f, 0.26f, 0.32f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,
            cancelHovered
                ? new Vector4(Chr_TextDim.X, Chr_TextDim.Y, Chr_TextDim.Z, 1f)
                : new Vector4(0.24f, 0.26f, 0.32f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, cancelHovered ? Chr_Text : Chr_TextDim);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        if (ImGui.Button("CANCEL", new Vector2(cancelBtnW, btnH)))
        {
            Confirmed = false;
            IsOpen = false;
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);

        // Save - primary with bloom halo + play-button treatment.
        // Kick scale drives a compress/bounce on click; delayed-close
        // lets it play through before the window vanishes.
        var saveMin = new Vector2(saveX, saveY);
        var saveMax = new Vector2(saveX + saveBtnW, saveY + btnH);
        if (isValid)
        {
            UIStyles.DrawPlayButtonBloom(dl, saveMin, saveMax, scale, Chr_Accent);
        }

        ImGui.SetCursorScreenPos(saveMin);
        if (!isValid)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.24f, 0.26f, 0.32f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, Chr_TextFaint);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.Button("SAVE", new Vector2(saveBtnW, btnH));
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
        }
        else
        {
            float kick = 1f;
            if (saveClickTime >= 0)
                kick = UIStyles.PlayKickScale((float)(ImGui.GetTime() - saveClickTime));
            if (UIStyles.DrawPlayButton("##editorSave",
                    new Vector2(saveBtnW, btnH), kick, scale,
                    label: "SAVE",
                    restCol:  Chr_Accent,
                    hoverCol: Chr_AccentBright,
                    heldCol:  Chr_AccentDeep,
                    borderCol: Chr_AccentDeep,
                    textColor: Chr_AccentDark)
                && !savePendingClose)
            {
                saveClickTime = ImGui.GetTime();
                savePendingClose = true;
                savePendingCloseAt = ImGui.GetTime() + 0.5;
            }
        }
    }

    // 
    //  WINDOW-CORNER BRACKETS
    // 
    private void DrawWindowCornerBrackets()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        float armLen = 14f * scale;
        float inset = 6f * scale;
        uint col = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Chr_Accent.X, Chr_Accent.Y, Chr_Accent.Z, 0.50f));
        float left = winPos.X + inset;
        float right = winPos.X + winSize.X - inset;
        float bottom = winPos.Y + winSize.Y - inset;
        dl.AddLine(new Vector2(left, bottom - armLen), new Vector2(left, bottom), col, 1f);
        dl.AddLine(new Vector2(left, bottom), new Vector2(left + armLen, bottom), col, 1f);
        dl.AddLine(new Vector2(right - armLen, bottom), new Vector2(right, bottom), col, 1f);
        dl.AddLine(new Vector2(right, bottom - armLen), new Vector2(right, bottom), col, 1f);
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
