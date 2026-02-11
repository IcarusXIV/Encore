using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using Encore.Styles;

namespace Encore.Windows;

public class MainWindow : Window, IDisposable
{
    private PresetEditorWindow? editorWindow;
    private HelpWindow? helpWindow;
    private int? presetToDelete = null;
    private int selectedPresetIndex = -1;

    // Sorting options
    private enum SortMode { Custom, Name, Command, Favorites, Newest, Oldest }
    private SortMode currentSort = SortMode.Custom;

    // Search filter
    private string presetSearchFilter = "";

    // Settings
    private int editPriorityBoost = -1; // -1 means not yet loaded

    // Drag & drop state
    private int dragSourceIndex = -1;
    private string? dragSourcePresetId = null;

    // Folder rename state
    private string? renamingFolderId = null;
    private string renamingFolderName = "";

    // New folder dialog state
    private bool showNewFolderDialog = false;
    private string newFolderName = "New Folder";
    private Vector3 newFolderColor = new Vector3(0.45f, 0.55f, 0.75f);

    // Default folder color
    private static readonly Vector3 DefaultFolderColor = new Vector3(0.45f, 0.55f, 0.75f);

    // Preset folder colours
    private static readonly (string Name, Vector3 Color)[] PresetFolderColors = new[]
    {
        ("Red", new Vector3(0.8f, 0.2f, 0.2f)),
        ("Green", new Vector3(0.3f, 0.8f, 0.3f)),
        ("Blue", new Vector3(0.3f, 0.5f, 0.9f)),
        ("Yellow", new Vector3(0.9f, 0.8f, 0.2f)),
        ("Purple", new Vector3(0.7f, 0.3f, 0.9f)),
        ("Orange", new Vector3(1.0f, 0.6f, 0.2f)),
        ("Pink", new Vector3(0.9f, 0.4f, 0.7f)),
        ("Cyan", new Vector3(0.3f, 0.8f, 0.8f)),
    };

    // Folder delete confirmation
    private string? folderToDelete = null;

    // Base sizes (before scaling)
    private const float BaseWidth = 500f;
    private const float BaseHeight = 600f;
    private const float BaseMaxWidth = 800f;
    private const float BaseMaxHeight = 900f;

    // Drag state: isDragging = active drag, anyCardHovered = previous frame had a card hovered
    // We set NoMove when either is true so the window doesn't steal the drag from cards
    private bool isDragging = false;
    private bool anyCardHovered = false;

    public MainWindow() : base("Encore###EncoreMain")
    {
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.None;
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

        // Prevent window movement when hovering over cards in Custom sort mode or during drag.
        // anyCardHovered uses previous frame's state — user hovers a card, then on the next frame
        // NoMove is set so the click+drag activates BeginDragDropSource instead of moving the window.
        if (isDragging || (anyCardHovered && currentSort == SortMode.Custom))
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
    }

    public void SetEditorWindow(PresetEditorWindow editor)
    {
        editorWindow = editor;
    }

    public void SetHelpWindow(HelpWindow help)
    {
        helpWindow = help;
    }

    public override void Draw()
    {
        HandleEditorCompletion();

        // Reset per-frame hover tracking (read by PreDraw on NEXT frame)
        anyCardHovered = false;

        UIStyles.PushMainWindowStyle();

        try
        {
            DrawHeader();
            ImGui.Spacing();
            DrawPresetList();
            ImGui.Spacing();
            DrawBottomBar();

            // Delete confirmation popups
            DrawDeleteConfirmation();
            DrawFolderDeleteConfirmation();
        }
        finally
        {
            UIStyles.PopMainWindowStyle();
        }
    }

    private void DrawHeader()
    {
        // Title with preset count
        var presetCount = Plugin.Instance?.Configuration.Presets.Count ?? 0;
        ImGui.Text($"Dance Mod Presets ({presetCount})");

        // Penumbra status (right edge)
        var statusText = Plugin.Instance?.PenumbraService?.IsAvailable == true ? "Penumbra: Connected" : "Penumbra: Not Found";
        var statusColor = Plugin.Instance?.PenumbraService?.IsAvailable == true
            ? new Vector4(0.4f, 0.8f, 0.4f, 1f)
            : new Vector4(1f, 0.4f, 0.4f, 1f);

        var statusWidth = ImGui.CalcTextSize(statusText).X;
        ImGui.SameLine(ImGui.GetWindowWidth() - statusWidth - 15);
        ImGui.TextColored(statusColor, statusText);

        ImGui.Separator();
    }

    private void DrawPresetList()
    {
        var config = Plugin.Instance?.Configuration;
        var presets = config?.Presets;
        if (presets == null || config == null)
            return;

        // Sort controls
        DrawSortControls();

        // Calculate list height
        var listHeight = ImGui.GetContentRegionAvail().Y - 80;

        if (ImGui.BeginChild("PresetList", new Vector2(-1, listHeight), true))
        {
            if (presets.Count == 0)
            {
                ImGui.Spacing();
                UIStyles.TextCentered("No presets yet!");
                ImGui.Spacing();
                UIStyles.TextCentered("Click 'New Preset' to create one.");
            }
            else if (currentSort == SortMode.Custom)
            {
                // Folder-aware rendering in Custom mode
                DrawCustomSortPresets(presets, config);
            }
            else
            {
                // Flat sorted list for all other modes (folders ignored)
                var sortedIndices = GetSortedPresetIndices(presets);
                foreach (var i in sortedIndices)
                {
                    DrawPresetCard(presets[i], i);
                }
            }

            // Handle drop to unfiled area (drop on empty space below all content)
            if (currentSort == SortMode.Custom && dragSourcePresetId != null &&
                ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsWindowHovered())
            {
                // Dropped on empty area of the list — unfile the preset
                var draggedPreset = presets.FirstOrDefault(p => p.Id == dragSourcePresetId);
                if (draggedPreset != null)
                {
                    draggedPreset.FolderId = null;
                    config.Save();
                }
                dragSourceIndex = -1;
                dragSourcePresetId = null;
                isDragging = false;
            }
        }
        ImGui.EndChild();

        // Clear drag state if mouse released outside
        if (dragSourcePresetId != null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            dragSourceIndex = -1;
            dragSourcePresetId = null;
            isDragging = false;
        }

        // Also clear isDragging if no drag source is active
        if (dragSourcePresetId == null)
        {
            isDragging = false;
        }
    }

    private void DrawCustomSortPresets(List<DancePreset> presets, Configuration config)
    {
        var sortedIndices = GetSortedPresetIndices(presets);

        // Apply search filter to indices
        var searchFiltered = new HashSet<int>(sortedIndices);

        // 1. Draw unfiled presets first (FolderId == null)
        var unfiledIndices = sortedIndices.Where(i => presets[i].FolderId == null).ToList();
        foreach (var i in unfiledIndices)
        {
            DrawPresetCard(presets[i], i);
        }

        // 2. Draw each folder and its presets
        var folderOrder = config.FolderOrder ?? new List<string>();
        var folders = config.Folders ?? new List<PresetFolder>();

        foreach (var folderId in folderOrder)
        {
            var folder = folders.FirstOrDefault(f => f.Id == folderId);
            if (folder == null) continue;

            var folderIndices = sortedIndices.Where(i => presets[i].FolderId == folder.Id).ToList();
            var isExpandedWithContent = !folder.IsCollapsed && folderIndices.Count > 0;
            DrawFolderHeader(folder, folderIndices.Count, config, isExpandedWithContent);

            if (isExpandedWithContent)
            {
                var scale = UIStyles.Scale;
                var folderColor = folder.Color ?? DefaultFolderColor;
                var drawList = ImGui.GetWindowDrawList();
                var accentWidth = 4f * scale;
                var indent = accentWidth + 2f * scale;
                var paddingTop = 6f * scale;
                var paddingBottom = 22f * scale;

                // Estimate content area height
                var itemSpacing = ImGui.GetStyle().ItemSpacing.Y;
                var borderSize = ImGui.GetStyle().ChildBorderSize;
                var cardTotalHeight = 80f * scale + borderSize * 2 + itemSpacing; // card + border + spacing between
                var totalHeight = folderIndices.Count * cardTotalHeight + paddingTop + paddingBottom - itemSpacing; // padding top/bottom, remove trailing spacing

                var startPos = ImGui.GetCursorScreenPos();
                var contentWidth = ImGui.GetContentRegionAvail().X;

                // Content area background (connects to header above — flat top corners)
                var contentBg = new Vector4(
                    0.10f + folderColor.X * 0.05f,
                    0.10f + folderColor.Y * 0.05f,
                    0.10f + folderColor.Z * 0.05f,
                    0.85f);
                drawList.AddRectFilled(
                    startPos,
                    new Vector2(startPos.X + contentWidth, startPos.Y + totalHeight),
                    ImGui.ColorConvertFloat4ToU32(contentBg),
                    4f * scale, ImDrawFlags.RoundCornersBottom);

                // Continuous left accent bar (connects to header's accent)
                drawList.AddRectFilled(
                    startPos,
                    new Vector2(startPos.X + accentWidth, startPos.Y + totalHeight),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f)),
                    4f * scale, ImDrawFlags.RoundCornersBottomLeft);

                // Subtle bottom border line in folder color
                drawList.AddLine(
                    new Vector2(startPos.X + accentWidth, startPos.Y + totalHeight - 1),
                    new Vector2(startPos.X + contentWidth, startPos.Y + totalHeight - 1),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 0.3f)),
                    1f * scale);

                // Top padding
                ImGui.SetCursorScreenPos(new Vector2(startPos.X, startPos.Y + paddingTop));

                // Draw presets indented past the accent bar
                ImGui.Indent(indent);
                foreach (var i in folderIndices)
                {
                    DrawPresetCard(presets[i], i, folderColor);
                }
                ImGui.Unindent(indent);

                // Ensure cursor is past the content area
                var endY = startPos.Y + totalHeight + itemSpacing;
                if (ImGui.GetCursorScreenPos().Y < endY)
                    ImGui.SetCursorScreenPos(new Vector2(startPos.X, endY));

                ImGui.Spacing();
            }
        }

        // Also draw presets in folders that aren't in FolderOrder (orphaned folder refs)
        var knownFolderIds = new HashSet<string>(folderOrder);
        var orphanFolderIds = sortedIndices
            .Where(i => presets[i].FolderId != null && !knownFolderIds.Contains(presets[i].FolderId!))
            .Select(i => presets[i].FolderId!)
            .Distinct()
            .ToList();
        foreach (var orphanId in orphanFolderIds)
        {
            // Preset references a deleted folder — unfile it
            foreach (var i in sortedIndices.Where(i => presets[i].FolderId == orphanId))
            {
                presets[i].FolderId = null;
                DrawPresetCard(presets[i], i);
            }
            config.Save();
        }
    }

    private void DrawFolderHeader(PresetFolder folder, int presetCount, Configuration config, bool hasExpandedContent = false)
    {
        var scale = UIStyles.Scale;
        var headerHeight = 32f * scale;
        var drawList = ImGui.GetWindowDrawList();
        var folderColor = folder.Color ?? DefaultFolderColor;

        ImGui.PushID($"folder_{folder.Id}");

        // Background bar with subtle colour tint
        var cursorPos = ImGui.GetCursorScreenPos();
        var headerMin = cursorPos;
        var headerMax = new Vector2(cursorPos.X + ImGui.GetContentRegionAvail().X, cursorPos.Y + headerHeight);

        // Base background with ~10% tint from folder color
        var bgColor = new Vector4(
            0.16f + folderColor.X * 0.04f,
            0.16f + folderColor.Y * 0.04f,
            0.19f + folderColor.Z * 0.04f,
            0.95f);
        // When expanded with content, flat bottom corners to connect to content area
        var headerRounding = hasExpandedContent ? ImDrawFlags.RoundCornersTop : ImDrawFlags.RoundCornersAll;
        drawList.AddRectFilled(headerMin, headerMax,
            ImGui.ColorConvertFloat4ToU32(bgColor),
            4f * scale, headerRounding);

        // Left accent bar (4px wide)
        var accentWidth = 4f * scale;
        var accentRounding = hasExpandedContent ? ImDrawFlags.RoundCornersTopLeft : ImDrawFlags.RoundCornersLeft;
        drawList.AddRectFilled(
            headerMin,
            new Vector2(headerMin.X + accentWidth, headerMax.Y),
            ImGui.ColorConvertFloat4ToU32(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f)),
            4f * scale, accentRounding);

        // Folder icon (open/closed)
        var iconStr = folder.IsCollapsed
            ? FontAwesomeIcon.Folder.ToIconString()
            : FontAwesomeIcon.FolderOpen.ToIconString();

        ImGui.PushFont(UiBuilder.IconFont);
        var iconSize = ImGui.CalcTextSize(iconStr);
        var iconPos = new Vector2(headerMin.X + accentWidth + 8 * scale, headerMin.Y + (headerHeight - iconSize.Y) / 2);
        drawList.AddText(iconPos, ImGui.ColorConvertFloat4ToU32(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f)), iconStr);
        ImGui.PopFont();

        // Folder name or rename input
        var textStartX = iconPos.X + iconSize.X + 8 * scale;
        if (renamingFolderId == folder.Id)
        {
            ImGui.SetCursorScreenPos(new Vector2(textStartX, headerMin.Y + 3 * scale));
            ImGui.SetNextItemWidth(200 * scale);
            ImGui.SetKeyboardFocusHere();
            var flags = ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll;
            if (ImGui.InputText("##folderRename", ref renamingFolderName, 100, flags))
            {
                folder.Name = renamingFolderName;
                config.Save();
                renamingFolderId = null;
            }
            else if (!ImGui.IsItemActive() && renamingFolderId == folder.Id && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                folder.Name = renamingFolderName;
                config.Save();
                renamingFolderId = null;
            }
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                renamingFolderId = null;
            }
        }
        else
        {
            // Folder name (brighter)
            var namePos = new Vector2(textStartX, headerMin.Y + (headerHeight - ImGui.CalcTextSize(folder.Name).Y) / 2);
            drawList.AddText(namePos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.85f, 0.85f, 0.9f, 1f)), folder.Name);

            // Preset count (dimmed, after name)
            var countText = $"({presetCount})";
            var countPos = new Vector2(namePos.X + ImGui.CalcTextSize(folder.Name).X + 6 * scale, namePos.Y);
            drawList.AddText(countPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 0.8f)), countText);

            // Invisible button for click-to-toggle + right-click
            ImGui.SetCursorScreenPos(headerMin);
            if (ImGui.InvisibleButton($"folderBtn_{folder.Id}", new Vector2(headerMax.X - headerMin.X, headerHeight)))
            {
                folder.IsCollapsed = !folder.IsCollapsed;
                config.Save();
            }

            // Right-click context menu
            if (ImGui.BeginPopupContextItem($"folderCtx_{folder.Id}"))
            {
                if (ImGui.MenuItem("Rename"))
                {
                    renamingFolderId = folder.Id;
                    renamingFolderName = folder.Name;
                }

                // Folder Colour submenu
                if (ImGui.BeginMenu("Folder Color"))
                {
                    if (ImGui.MenuItem("Default", "", folder.Color == null))
                    {
                        folder.Color = null;
                        config.Save();
                    }

                    ImGui.Separator();

                    foreach (var (colorName, color) in PresetFolderColors)
                    {
                        var isSelected = folder.Color.HasValue &&
                            Vector3.Distance(folder.Color.Value, color) < 0.1f;

                        if (ImGui.MenuItem(colorName, "", isSelected))
                        {
                            folder.Color = color;
                            config.Save();
                        }
                    }

                    ImGui.Separator();

                    ImGui.Text("Custom:");
                    var tempColor = folder.Color ?? DefaultFolderColor;
                    if (ImGui.ColorEdit3("##folderColor", ref tempColor, ImGuiColorEditFlags.NoInputs))
                    {
                        folder.Color = tempColor;
                        config.Save();
                    }

                    ImGui.EndMenu();
                }

                ImGui.Separator();

                var folderOrder = config.FolderOrder;
                var folderIdx = folderOrder.IndexOf(folder.Id);

                if (folderIdx <= 0) ImGui.BeginDisabled();
                if (ImGui.MenuItem("Move Up"))
                {
                    folderOrder.RemoveAt(folderIdx);
                    folderOrder.Insert(folderIdx - 1, folder.Id);
                    config.Save();
                }
                if (folderIdx <= 0) ImGui.EndDisabled();

                if (folderIdx < 0 || folderIdx >= folderOrder.Count - 1) ImGui.BeginDisabled();
                if (ImGui.MenuItem("Move Down"))
                {
                    folderOrder.RemoveAt(folderIdx);
                    folderOrder.Insert(folderIdx + 1, folder.Id);
                    config.Save();
                }
                if (folderIdx < 0 || folderIdx >= folderOrder.Count - 1) ImGui.EndDisabled();

                ImGui.Separator();

                UIStyles.PushDangerButtonStyle();
                if (ImGui.MenuItem("Delete Folder"))
                {
                    folderToDelete = folder.Id;
                }
                UIStyles.PopDangerButtonStyle();

                ImGui.EndPopup();
            }
        }

        // Drop target: accept preset drag onto folder header
        if (dragSourcePresetId != null)
        {
            var mousePos = ImGui.GetMousePos();
            var isHovering = mousePos.X >= headerMin.X && mousePos.X <= headerMax.X &&
                             mousePos.Y >= headerMin.Y && mousePos.Y <= headerMax.Y;

            if (isHovering)
            {
                // Highlight with folder color
                drawList.AddRect(headerMin, headerMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f)),
                    4f * scale, ImDrawFlags.None, 2f * scale);

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    var presets = config.Presets;
                    var draggedPreset = presets.FirstOrDefault(p => p.Id == dragSourcePresetId);
                    if (draggedPreset != null)
                    {
                        draggedPreset.FolderId = folder.Id;
                        config.Save();
                    }
                    dragSourceIndex = -1;
                    dragSourcePresetId = null;
                    isDragging = false;
                }
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(headerMin.X, headerMax.Y));
        if (!hasExpandedContent)
            ImGui.Spacing();

        ImGui.PopID();
    }

    private void DrawSortControls()
    {
        // Search bar
        ImGui.SetNextItemWidth(200);
        ImGui.InputTextWithHint("##presetSearch", "Search...", ref presetSearchFilter, 100);

        ImGui.SameLine();

        // Sort combo
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Sort:");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(100);
        var sortOptions = new[] { "Custom", "Name", "Command", "Favorites", "Newest", "Oldest" };
        var currentSortIndex = (int)currentSort;
        if (ImGui.Combo("##sort", ref currentSortIndex, sortOptions, sortOptions.Length))
        {
            currentSort = (SortMode)currentSortIndex;
        }

        // Priority boost on the right
        ImGui.SameLine(ImGui.GetWindowWidth() - 125);
        ImGui.Text("Priority +");
        ImGui.SameLine(0, 4);
        ImGui.SetNextItemWidth(50);

        // Load current value if not yet loaded
        if (editPriorityBoost == -1)
        {
            editPriorityBoost = Plugin.Instance?.Configuration.DefaultPriorityBoost ?? 20;
        }

        if (ImGui.InputInt("##priorityBoost", ref editPriorityBoost, 0, 0))
        {
            Plugin.Instance!.Configuration.DefaultPriorityBoost = editPriorityBoost;
            Plugin.Instance.Configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text("How much to increase mod priority when activated.");
            ImGui.TextDisabled("Default: 20");
            ImGui.EndTooltip();
        }

        ImGui.Spacing();
    }

    private List<int> GetSortedPresetIndices(List<DancePreset> presets)
    {
        var indices = Enumerable.Range(0, presets.Count).ToList();
        var favorites = Plugin.Instance?.Configuration.FavoritePresetIds ?? new HashSet<string>();

        // Apply search filter
        if (!string.IsNullOrWhiteSpace(presetSearchFilter))
        {
            var filter = presetSearchFilter.ToLowerInvariant();
            indices = indices.Where(i =>
                presets[i].Name.ToLowerInvariant().Contains(filter) ||
                presets[i].ChatCommand.ToLowerInvariant().Contains(filter) ||
                presets[i].ModName.ToLowerInvariant().Contains(filter) ||
                presets[i].EmoteCommand.ToLowerInvariant().Contains(filter))
                .ToList();
        }

        switch (currentSort)
        {
            case SortMode.Name:
                indices.Sort((a, b) => string.Compare(presets[a].Name, presets[b].Name, StringComparison.OrdinalIgnoreCase));
                break;
            case SortMode.Command:
                indices.Sort((a, b) => string.Compare(presets[a].ChatCommand, presets[b].ChatCommand, StringComparison.OrdinalIgnoreCase));
                break;
            case SortMode.Favorites:
                // Favourites first, then by name
                indices.Sort((a, b) =>
                {
                    var aFav = favorites.Contains(presets[a].Id);
                    var bFav = favorites.Contains(presets[b].Id);
                    if (aFav != bFav) return bFav.CompareTo(aFav); // Favourites first
                    return string.Compare(presets[a].Name, presets[b].Name, StringComparison.OrdinalIgnoreCase);
                });
                break;
            case SortMode.Newest:
                indices.Sort((a, b) => presets[b].CreatedAt.CompareTo(presets[a].CreatedAt));
                break;
            case SortMode.Oldest:
                indices.Sort((a, b) => presets[a].CreatedAt.CompareTo(presets[b].CreatedAt));
                break;
            case SortMode.Custom:
            default:
                // Keep original order
                break;
        }

        return indices;
    }

    private void DrawPresetCard(DancePreset preset, int index, Vector3? folderColor = null)
    {
        ImGui.PushID($"preset_{preset.Id}");

        var scale = UIStyles.Scale;
        var isSelected = selectedPresetIndex == index;
        var isFavorite = Plugin.Instance?.Configuration.FavoritePresetIds?.Contains(preset.Id) ?? false;
        var cardHeight = 80f * scale;

        // Card background
        var cardColor = isSelected
            ? new Vector4(0.15f, 0.2f, 0.25f, 0.95f)
            : new Vector4(0.1f, 0.1f, 0.1f, 0.9f);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, cardColor);

        // Track card position for drag & drop
        var cardScreenPos = ImGui.GetCursorScreenPos();

        if (ImGui.BeginChild($"card_{index}", new Vector2(-1, cardHeight), true))
        {
            // Track hover for NoMove on next frame
            if (ImGui.IsWindowHovered())
                anyCardHovered = true;

            // Accent bars on left edge when inside a folder
            if (folderColor.HasValue)
            {
                var fc = folderColor.Value;
                var dl = ImGui.GetWindowDrawList();
                var winPos = ImGui.GetWindowPos();
                var winSize = ImGui.GetWindowSize();
                var accentW = 3f * scale;
                dl.AddRectFilled(
                    winPos,
                    new Vector2(winPos.X + accentW, winPos.Y + winSize.Y),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(fc.X, fc.Y, fc.Z, 0.8f)),
                    4f * scale, ImDrawFlags.RoundCornersLeft);
            }

            // Drag source (Custom sort only)
            if (currentSort == SortMode.Custom)
            {
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
                {
                    isDragging = true;
                    dragSourceIndex = index;
                    dragSourcePresetId = preset.Id;
                    ImGui.SetDragDropPayload("PRESET_REORDER", ReadOnlySpan<byte>.Empty, ImGuiCond.None);
                    ImGui.Text(preset.Name);
                    ImGui.EndDragDropSource();
                }
            }

            // Icon
            var iconSize = 48f * scale;
            if (preset.IconId.HasValue)
            {
                var iconTexture = GetGameIcon(preset.IconId.Value);
                if (iconTexture != null)
                {
                    ImGui.Image(iconTexture.Handle, new Vector2(iconSize, iconSize));
                }
                else
                {
                    DrawPlaceholderIcon(iconSize, scale);
                }
            }
            else
            {
                DrawPlaceholderIcon(iconSize, scale);
            }

            ImGui.SameLine();

            // Info section
            ImGui.BeginGroup();

            // Name with favourite star
            if (!preset.Enabled)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f));
            }

            if (isFavorite)
            {
                ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "*");
                ImGui.SameLine(0, 2);
            }
            ImGui.Text(preset.Name);

            if (!preset.Enabled)
            {
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.TextDisabled("(Disabled)");
            }

            // Command
            if (!string.IsNullOrEmpty(preset.ChatCommand))
            {
                ImGui.TextColored(new Vector4(0.6f, 0.8f, 1f, 1f), $"/{preset.ChatCommand}");
            }
            else
            {
                ImGui.TextDisabled("No command assigned");
            }

            // Mod name
            ImGui.TextDisabled(string.IsNullOrEmpty(preset.ModName) ? "No mod selected" : preset.ModName);

            ImGui.EndGroup();

            // Action buttons (right side - closer to edge)
            var buttonWidth = 50f * scale;
            var menuWidth = 28f * scale;
            var buttonsX = ImGui.GetWindowWidth() - buttonWidth - menuWidth - 12 * scale;

            ImGui.SameLine(buttonsX);
            ImGui.BeginGroup();

            // Play button
            UIStyles.PushSuccessButtonStyle();
            if (ImGui.Button("Play", new Vector2(buttonWidth, 0)))
            {
                Plugin.Instance?.ExecutePreset(preset);
            }
            UIStyles.PopSuccessButtonStyle();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Tip: Stop dancing before switching between\nmods that share the same base emote.");
            }

            ImGui.SameLine();

            // Menu button
            if (ImGui.Button("...", new Vector2(menuWidth, 0)))
            {
                ImGui.OpenPopup($"presetMenu_{index}");
            }

            // Context menu
            if (ImGui.BeginPopup($"presetMenu_{index}"))
            {
                if (ImGui.MenuItem("Edit"))
                {
                    editorWindow?.OpenEdit(preset);
                    selectedPresetIndex = index;
                }

                if (ImGui.MenuItem(isFavorite ? "Unfavorite" : "Favorite"))
                {
                    ToggleFavorite(preset.Id);
                }

                if (ImGui.MenuItem(preset.Enabled ? "Disable" : "Enable"))
                {
                    preset.Enabled = !preset.Enabled;
                    Plugin.Instance?.UpdatePresetCommands();
                    Plugin.Instance?.Configuration.Save();
                }

                if (ImGui.MenuItem("Duplicate"))
                {
                    var clone = preset.Clone();
                    Plugin.Instance?.Configuration.Presets.Add(clone);
                    Plugin.Instance?.Configuration.Save();
                }

                ImGui.Separator();

                // Move to Folder submenu
                var config = Plugin.Instance?.Configuration;
                if (config != null && config.Folders.Count > 0 && ImGui.BeginMenu("Move to Folder"))
                {
                    if (ImGui.MenuItem("(None)"))
                    {
                        preset.FolderId = null;
                        config.Save();
                    }
                    foreach (var folder in config.Folders)
                    {
                        var isInFolder = preset.FolderId == folder.Id;
                        if (ImGui.MenuItem(folder.Name, "", isInFolder))
                        {
                            preset.FolderId = folder.Id;
                            config.Save();
                        }
                    }
                    ImGui.EndMenu();
                }

                ImGui.Separator();

                UIStyles.PushDangerButtonStyle();
                if (ImGui.MenuItem("Delete"))
                {
                    presetToDelete = index;
                }
                UIStyles.PopDangerButtonStyle();

                ImGui.EndPopup();
            }

            ImGui.EndGroup();

            // Make the whole card clickable for selection
            if (ImGui.IsWindowHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                selectedPresetIndex = index;
            }

            // Double-click to edit
            if (ImGui.IsWindowHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                editorWindow?.OpenEdit(preset);
            }

            // Base emote (bottom right corner) - drawn via DrawList to not affect layout
            if (!string.IsNullOrEmpty(preset.EmoteCommand))
            {
                var drawList = ImGui.GetWindowDrawList();
                var emoteText = preset.EmoteCommand;

                // For pose mods, show the pose type and index (e.g., "cpose 1", "/groundsit 2")
                if (preset.AnimationType >= 2 && preset.AnimationType <= 5)
                {
                    var poseStr = preset.PoseIndex >= 0 ? $" {preset.PoseIndex}" : "";
                    emoteText = preset.AnimationType switch
                    {
                        2 => $"cpose{poseStr}",           // StandingIdle
                        3 => $"/sit cpose{poseStr}",      // ChairSitting
                        4 => $"/groundsit{poseStr}",      // GroundSitting
                        5 => $"/doze{poseStr}",           // LyingDozing
                        _ => emoteText
                    };
                }
                var emoteSize = ImGui.CalcTextSize(emoteText);
                var windowPos = ImGui.GetWindowPos();
                var windowSize = ImGui.GetWindowSize();
                var textPos = new Vector2(
                    windowPos.X + windowSize.X - emoteSize.X - 10 * scale,
                    windowPos.Y + windowSize.Y - emoteSize.Y - 8 * scale);
                drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.45f, 0.45f, 0.45f, 1f)), emoteText);
            }
        }
        ImGui.EndChild();

        // Drop target on the card (Custom sort only) — reorder preset position
        if (currentSort == SortMode.Custom && dragSourcePresetId != null && dragSourcePresetId != preset.Id)
        {
            var cardMin = cardScreenPos;
            var cardMax = new Vector2(cardScreenPos.X + ImGui.GetContentRegionAvail().X, cardScreenPos.Y + cardHeight);
            var mousePos = ImGui.GetMousePos();
            var isHovering = mousePos.X >= cardMin.X && mousePos.X <= cardMax.X &&
                             mousePos.Y >= cardMin.Y && mousePos.Y <= cardMax.Y;

            if (isHovering)
            {
                // Blue insertion indicator
                var dl = ImGui.GetWindowDrawList();
                uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(0.27f, 0.53f, 0.90f, 1f));
                dl.AddRect(cardMin, cardMax, col, 0, ImDrawFlags.None, 2 * scale);

                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    var presets = Plugin.Instance?.Configuration.Presets;
                    if (presets != null && dragSourceIndex >= 0 && dragSourceIndex < presets.Count)
                    {
                        // Move the dragged preset to the target's position
                        var draggedPreset = presets[dragSourceIndex];
                        presets.RemoveAt(dragSourceIndex);
                        // Recalculate target index after removal
                        var targetIdx = presets.IndexOf(preset);
                        if (targetIdx >= 0)
                        {
                            presets.Insert(targetIdx, draggedPreset);
                            // Adopt the target's folder
                            draggedPreset.FolderId = preset.FolderId;
                        }
                        else
                        {
                            presets.Add(draggedPreset);
                        }
                        Plugin.Instance?.Configuration.Save();
                    }
                    dragSourceIndex = -1;
                    dragSourcePresetId = null;
                    isDragging = false;
                }
            }
        }

        ImGui.PopStyleColor();
        ImGui.PopID();

        ImGui.Spacing();
    }

    private void ToggleFavorite(string presetId)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return;

        if (config.FavoritePresetIds == null)
            config.FavoritePresetIds = new HashSet<string>();

        if (config.FavoritePresetIds.Contains(presetId))
            config.FavoritePresetIds.Remove(presetId);
        else
            config.FavoritePresetIds.Add(presetId);

        config.Save();
    }

    private void MovePreset(int fromIndex, int toIndex)
    {
        var presets = Plugin.Instance?.Configuration.Presets;
        if (presets == null || fromIndex < 0 || toIndex < 0 || fromIndex >= presets.Count || toIndex >= presets.Count)
            return;

        var preset = presets[fromIndex];
        presets.RemoveAt(fromIndex);
        presets.Insert(toIndex, preset);

        selectedPresetIndex = toIndex;
        Plugin.Instance?.Configuration.Save();
    }

    private void DrawPlaceholderIcon(float size, float scale)
    {
        var drawList = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();

        drawList.AddRectFilled(
            pos,
            pos + new Vector2(size, size),
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 0.8f)),
            4f * scale
        );

        // Music note placeholder
        var textSize = ImGui.CalcTextSize("?");
        var textPos = pos + new Vector2((size - textSize.X) / 2, (size - textSize.Y) / 2);
        drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "?");

        ImGui.Dummy(new Vector2(size, size));
    }

    private void DrawBottomBar()
    {
        var scale = UIStyles.Scale;

        ImGui.Separator();
        ImGui.Spacing();

        // New preset button
        UIStyles.PushAccentButtonStyle();
        if (ImGui.Button("New Preset", new Vector2(120 * scale, 30 * scale)))
        {
            editorWindow?.OpenNew();
        }
        UIStyles.PopAccentButtonStyle();

        ImGui.SameLine();

        // Pinned mods button (pin icon, gold when mods are pinned)
        var pinnedCount = Plugin.Instance?.Configuration.PinnedModDirectories.Count ?? 0;
        var hasPinned = pinnedCount > 0;

        // Gold colour when pinned
        if (hasPinned)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.55f, 0.1f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.65f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.6f, 0.45f, 0.05f, 1f));
        }

        ImGui.PushFont(UiBuilder.IconFont);
        var pinClicked = ImGui.Button(FontAwesomeIcon.Thumbtack.ToIconString(), new Vector2(35 * scale, 30 * scale));
        var pinHovered = ImGui.IsItemHovered();
        ImGui.PopFont();

        if (hasPinned)
        {
            ImGui.PopStyleColor(3);
        }

        if (pinClicked)
        {
            ImGui.OpenPopup("Pinned Mods###pinnedMods");
        }

        if (pinHovered)
        {
            ImGui.BeginTooltip();
            ImGui.Text("Pinned mods won't be disabled when applying presets");
            ImGui.EndTooltip();
        }

        ImGui.SameLine();

        // New Folder button
        ImGui.PushFont(UiBuilder.IconFont);
        var folderClicked = ImGui.Button(FontAwesomeIcon.FolderPlus.ToIconString(), new Vector2(35 * scale, 30 * scale));
        var folderHovered = ImGui.IsItemHovered();
        ImGui.PopFont();

        if (folderClicked)
        {
            showNewFolderDialog = true;
            newFolderName = "New Folder";
            newFolderColor = DefaultFolderColor;
            ImGui.OpenPopup("New Folder###newFolderDialog");
        }

        if (folderHovered)
        {
            ImGui.SetTooltip("New Folder");
        }

        // New folder popup (must be in same ID scope as OpenPopup call)
        DrawNewFolderDialog();

        ImGui.SameLine();

        // Align to target button (dynamic colour based on state)
        var alignState = Plugin.Instance?.GetAlignState() ?? (false, "", 0f, false, true, false);
        var isWalking = alignState.isWalking;
        var alignBlocked = alignState.hasTarget && !alignState.isStanding && !isWalking;

        if (isWalking)
        {
            // Cyan pulse - actively walking to target
            var pulse = (float)(Math.Sin(ImGui.GetTime() * 4.0) * 0.15 + 0.85);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.1f * pulse, 0.5f * pulse, 0.6f * pulse, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f * pulse, 0.6f * pulse, 0.7f * pulse, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.08f * pulse, 0.4f * pulse, 0.5f * pulse, 1f));
        }
        else if (alignBlocked)
        {
            // Orange - sitting/dozing, blocked
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.4f, 0.1f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.7f, 0.5f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.5f, 0.35f, 0.05f, 1f));
        }
        else if (alignState.inRange)
        {
            // Green - in range
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.55f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.65f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.1f, 0.45f, 0.1f, 1f));
        }
        else if (alignState.hasTarget)
        {
            // Dim red - has target but too far
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.15f, 0.15f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.65f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.45f, 0.1f, 0.1f, 1f));
        }

        ImGui.PushFont(UiBuilder.IconFont);
        var alignClicked = ImGui.Button(FontAwesomeIcon.LocationCrosshairs.ToIconString(), new Vector2(35 * scale, 30 * scale));
        ImGui.PopFont();

        if (isWalking || alignBlocked || alignState.inRange || alignState.hasTarget)
        {
            ImGui.PopStyleColor(3);
        }

        if (alignClicked)
        {
            if (isWalking)
                Plugin.Instance?.MovementService?.Cancel();
            else
                Plugin.Framework.RunOnFrameworkThread(() => Plugin.Instance?.AlignToTarget());
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            if (isWalking)
            {
                ImGui.TextColored(new Vector4(0.3f, 0.9f, 0.9f, 1f), "Walking to target...");
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Click to cancel.");
            }
            else if (!alignState.hasTarget)
            {
                ImGui.Text("Align to Target");
                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Select a target first.");
            }
            else if (alignBlocked)
            {
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "Stand up first.");
                ImGui.Text($"Target: {alignState.targetName}");
            }
            else if (alignState.inRange)
            {
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Ready to align!");
                ImGui.Text($"Target: {alignState.targetName}");
            }
            else
            {
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.5f, 1f), "Move closer to your target.");
                ImGui.Text($"Target: {alignState.targetName}");
            }
            ImGui.EndTooltip();
        }

        ImGui.SameLine();

        // Reset button (requires Ctrl+Shift to prevent accidents)
        var hasStoredState = Plugin.Instance?.Configuration.OriginalPriorities.Count > 0 ||
                             Plugin.Instance?.Configuration.ModsWeEnabled.Count > 0 ||
                             Plugin.Instance?.Configuration.ModsWeDisabled.Count > 0;

        if (!hasStoredState) ImGui.BeginDisabled();

        if (ImGui.Button("Reset", new Vector2(80 * scale, 30 * scale)))
        {
            // Only reset if Ctrl+Shift are held
            if (ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift)
            {
                Plugin.Instance?.ResetAllPriorities();
            }
        }

        if (!hasStoredState) ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.BeginTooltip();
            if (hasStoredState)
            {
                ImGui.Text("Restore emote mods to their original state");
                ImGui.TextDisabled("Ctrl+Shift + Click to reset");
            }
            else
            {
                ImGui.Text("No changes to restore");
            }
            ImGui.EndTooltip();
        }

        // Help button (right edge, with room for gear button)
        ImGui.SameLine(ImGui.GetWindowWidth() - 75 * scale);
        if (ImGui.Button("?", new Vector2(30 * scale, 30 * scale)))
        {
            helpWindow?.Toggle();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Help & Guide");
        }

        ImGui.SameLine();

        // Settings gear button (far right)
        ImGui.PushFont(UiBuilder.IconFont);
        var gearClicked = ImGui.Button(FontAwesomeIcon.Cog.ToIconString(), new Vector2(30 * scale, 30 * scale));
        ImGui.PopFont();

        if (gearClicked)
        {
            ImGui.OpenPopup("###settingsPopup");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Settings");
        }

        // Draw popups
        DrawSettingsPopup();
        DrawPinnedModsPopup();
    }

    private string pinnedModsFilter = "";

    private void DrawPinnedModsPopup()
    {
        var scale = UIStyles.Scale;
        var popupSize = new Vector2(400 * scale, 350 * scale);

        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Always);

        if (ImGui.BeginPopup("Pinned Mods###pinnedMods"))
        {
            var currentPinnedCount = Plugin.Instance?.Configuration.PinnedModDirectories.Count ?? 0;
            ImGui.Text($"Pinned Mods ({currentPinnedCount})");
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Pinned mods won't be disabled by conflict detection.");
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Search filter
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##pinnedFilter", "Search mods...", ref pinnedModsFilter, 256);
            ImGui.Spacing();

            // Get all emote mods
            var emoteMods = Plugin.Instance?.EmoteDetectionService?.GetEmoteMods();
            var pinnedMods = Plugin.Instance?.Configuration.PinnedModDirectories ?? new HashSet<string>();

            if (emoteMods == null || emoteMods.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "No emote mods found in cache.");
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Create some presets first.");
            }
            else
            {
                // Filter mods
                var filteredMods = emoteMods
                    .Where(m => string.IsNullOrEmpty(pinnedModsFilter) ||
                                m.ModName.Contains(pinnedModsFilter, StringComparison.OrdinalIgnoreCase) ||
                                m.ModDirectory.Contains(pinnedModsFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => pinnedMods.Contains(m.ModDirectory))
                    .ThenBy(m => m.ModName)
                    .ToList();

                if (ImGui.BeginChild("PinnedModsList", new Vector2(-1, 220 * scale), true))
                {
                    foreach (var mod in filteredMods)
                    {
                        var isPinned = pinnedMods.Contains(mod.ModDirectory);
                        if (ImGui.Checkbox($"##pin_{mod.ModDirectory}", ref isPinned))
                        {
                            if (isPinned)
                            {
                                Plugin.Instance?.Configuration.PinnedModDirectories.Add(mod.ModDirectory);
                            }
                            else
                            {
                                Plugin.Instance?.Configuration.PinnedModDirectories.Remove(mod.ModDirectory);
                            }
                            Plugin.Instance?.Configuration.Save();
                        }

                        ImGui.SameLine();
                        ImGui.Text(mod.ModName);

                        // Show emotes on hover
                        if (ImGui.IsItemHovered() && mod.AffectedEmotes.Count > 0)
                        {
                            ImGui.BeginTooltip();
                            ImGui.Text($"Emotes: {string.Join(", ", mod.AffectedEmotes.Take(5))}");
                            if (mod.AffectedEmotes.Count > 5)
                                ImGui.Text($"...and {mod.AffectedEmotes.Count - 5} more");
                            ImGui.EndTooltip();
                        }
                    }
                }
                ImGui.EndChild();

                // Show total count
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    $"{emoteMods.Count} mods available");
            }

            ImGui.EndPopup();
        }
    }

    private void DrawSettingsPopup()
    {
        var scale = UIStyles.Scale;
        ImGui.SetNextWindowSize(new Vector2(300 * scale, 0));

        if (ImGui.BeginPopup("###settingsPopup"))
        {
            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                ImGui.Text("Settings");
                ImGui.Separator();
                ImGui.Spacing();

                // Sit/Doze Anywhere toggle
                var allowAnywhere = config.AllowSitDozeAnywhere;
                if (ImGui.Checkbox("Allow Sit/Doze Anywhere", ref allowAnywhere))
                {
                    config.AllowSitDozeAnywhere = allowAnywhere;
                    config.Save();
                }
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                    "Sit/doze without nearby furniture.");
                if (allowAnywhere)
                {
                    ImGui.Spacing();
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                        "This uses the same technique as DozeAnywhere.");
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                        "It sends position data to SE's servers and");
                    ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f),
                        "may be detectable. Use at your own risk.");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f),
                        "When off, /sit and /doze require furniture.");
                }

            }

            ImGui.EndPopup();
        }
    }

    private bool focusNewFolderName = false;

    private void DrawNewFolderDialog()
    {
        if (showNewFolderDialog)
        {
            ImGui.OpenPopup("###newFolderPopup");
            showNewFolderDialog = false;
            focusNewFolderName = true;
        }

        var scale = UIStyles.Scale;
        ImGui.SetNextWindowSize(new Vector2(280 * scale, 0));

        if (ImGui.BeginPopup("###newFolderPopup"))
        {
            ImGui.Text("Folder Name:");
            ImGui.SetNextItemWidth(-1);
            if (focusNewFolderName)
            {
                ImGui.SetKeyboardFocusHere();
                focusNewFolderName = false;
            }
            var enterPressed = ImGui.InputText("##newFolderName", ref newFolderName, 100, ImGuiInputTextFlags.EnterReturnsTrue);

            ImGui.Spacing();
            ImGui.Text("Color:");
            ImGui.Spacing();

            // Preset colour swatches
            var swatchSize = 22f * scale;
            for (var i = 0; i < PresetFolderColors.Length; i++)
            {
                var (colorName, color) = PresetFolderColors[i];
                var isSelected = Vector3.Distance(newFolderColor, color) < 0.1f;

                if (i > 0) ImGui.SameLine();

                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(color.X, color.Y, color.Z, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(
                    Math.Min(color.X * 1.2f, 1f),
                    Math.Min(color.Y * 1.2f, 1f),
                    Math.Min(color.Z * 1.2f, 1f), 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(color.X * 0.8f, color.Y * 0.8f, color.Z * 0.8f, 1f));

                if (ImGui.Button($"##swatch_{i}", new Vector2(swatchSize, swatchSize)))
                {
                    newFolderColor = color;
                }

                ImGui.PopStyleColor(3);

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(colorName);
                }

                // Draw selection border
                if (isSelected)
                {
                    var min = ImGui.GetItemRectMin();
                    var max = ImGui.GetItemRectMax();
                    ImGui.GetWindowDrawList().AddRect(min - new Vector2(2, 2), max + new Vector2(2, 2),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f)), 2f, ImDrawFlags.None, 2f);
                }
            }

            ImGui.SameLine();
            ImGui.ColorEdit3("##customFolderColor", ref newFolderColor, ImGuiColorEditFlags.NoInputs);

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Create / Cancel buttons
            var canCreate = !string.IsNullOrWhiteSpace(newFolderName);
            if (!canCreate) ImGui.BeginDisabled();

            UIStyles.PushAccentButtonStyle();
            if (ImGui.Button("Create", new Vector2(100 * scale, 0)) || (enterPressed && canCreate))
            {
                var config = Plugin.Instance?.Configuration;
                if (config != null)
                {
                    var folder = new PresetFolder
                    {
                        Name = newFolderName.Trim(),
                        Color = Vector3.Distance(newFolderColor, DefaultFolderColor) < 0.1f ? null : newFolderColor,
                    };
                    config.Folders.Add(folder);
                    config.FolderOrder.Add(folder.Id);
                    config.Save();
                }
                ImGui.CloseCurrentPopup();
            }
            UIStyles.PopAccentButtonStyle();

            if (!canCreate) ImGui.EndDisabled();

            ImGui.SameLine();

            if (ImGui.Button("Cancel", new Vector2(100 * scale, 0)))
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawDeleteConfirmation()
    {
        // Open the popup at window level when presetToDelete is set
        if (presetToDelete.HasValue)
        {
            ImGui.OpenPopup("Delete Preset?###deleteConfirm");
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (ImGui.BeginPopupModal("Delete Preset?###deleteConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (presetToDelete.HasValue && presetToDelete.Value < Plugin.Instance?.Configuration.Presets.Count)
            {
                var preset = Plugin.Instance!.Configuration.Presets[presetToDelete.Value];
                ImGui.Text($"Are you sure you want to delete '{preset.Name}'?");
                ImGui.TextDisabled("This action cannot be undone.");
                ImGui.Spacing();

                var scale = UIStyles.Scale;
                UIStyles.PushDangerButtonStyle();
                if (ImGui.Button("Delete", new Vector2(100 * scale, 0)))
                {
                    Plugin.Instance.Configuration.Presets.RemoveAt(presetToDelete.Value);
                    Plugin.Instance.UpdatePresetCommands();
                    Plugin.Instance.Configuration.Save();
                    presetToDelete = null;
                    selectedPresetIndex = -1;
                    ImGui.CloseCurrentPopup();
                }
                UIStyles.PopDangerButtonStyle();

                ImGui.SameLine();

                if (ImGui.Button("Cancel", new Vector2(100 * scale, 0)))
                {
                    presetToDelete = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            else
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void DrawFolderDeleteConfirmation()
    {
        if (folderToDelete != null)
        {
            ImGui.OpenPopup("Delete Folder?###deleteFolderConfirm");
        }

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (ImGui.BeginPopupModal("Delete Folder?###deleteFolderConfirm", ImGuiWindowFlags.AlwaysAutoResize))
        {
            var config = Plugin.Instance?.Configuration;
            var folder = config?.Folders.FirstOrDefault(f => f.Id == folderToDelete);

            if (folder != null && config != null)
            {
                var presetsInFolder = config.Presets.Count(p => p.FolderId == folder.Id);
                ImGui.Text($"Delete folder '{folder.Name}'?");
                if (presetsInFolder > 0)
                    ImGui.TextDisabled($"{presetsInFolder} preset(s) will be moved out of this folder.");
                else
                    ImGui.TextDisabled("This folder is empty.");
                ImGui.Spacing();

                var scale = UIStyles.Scale;
                UIStyles.PushDangerButtonStyle();
                if (ImGui.Button("Delete", new Vector2(100 * scale, 0)))
                {
                    // Unfile all presets in this folder
                    foreach (var p in config.Presets.Where(p => p.FolderId == folder.Id))
                        p.FolderId = null;

                    config.Folders.Remove(folder);
                    config.FolderOrder.Remove(folder.Id);
                    config.Save();
                    folderToDelete = null;
                    ImGui.CloseCurrentPopup();
                }
                UIStyles.PopDangerButtonStyle();

                ImGui.SameLine();

                if (ImGui.Button("Cancel", new Vector2(100 * scale, 0)))
                {
                    folderToDelete = null;
                    ImGui.CloseCurrentPopup();
                }
            }
            else
            {
                folderToDelete = null;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
    }

    private void HandleEditorCompletion()
    {
        if (editorWindow == null || editorWindow.IsOpen || !editorWindow.Confirmed)
            return;

        var preset = editorWindow.CurrentPreset;
        if (preset == null)
            return;

        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return;

        if (editorWindow.IsNewPreset)
        {
            // Add new preset
            config.Presets.Add(preset);
        }
        else
        {
            // Update existing preset (it's already in the list, just save)
        }

        Plugin.Instance?.UpdatePresetCommands();
        config.Save();

        editorWindow.Confirmed = false;
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

    public void Dispose()
    {
        // Nothing to dispose
    }
}
