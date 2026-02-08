using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using Encore.Styles;

namespace Encore.Windows;

// Window for picking game icons (adapted from Character Select+)
public class IconPickerWindow : Window
{
    public uint? SelectedIconId { get; private set; }
    public bool Confirmed { get; set; }

    private readonly Dictionary<string, List<(uint start, uint end)>> iconCategories;
    private string selectedCategory = "Emotes";
    private List<uint> currentIcons = new();
    private double lastClickTime;
    private string searchFilter = "";
    private HashSet<uint> favoriteIcons;

    // Base sizes (before scaling)
    private const float BaseWidth = 800f;
    private const float BaseHeight = 600f;
    private const float BaseMaxWidth = 1200f;
    private const float BaseMaxHeight = 900f;

    public IconPickerWindow(uint? initialIcon = null) : base("Icon Picker###EncoreIconPicker")
    {
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoDocking;

        SelectedIconId = initialIcon;
        favoriteIcons = new HashSet<uint>(Plugin.Instance?.Configuration.FavoriteIconIds ?? new List<uint>());

        iconCategories = new Dictionary<string, List<(uint, uint)>>
        {
            {
                "Emotes", new List<(uint, uint)>
                {
                    (246001, 246004), (246101, 246133), (246201, 246280),
                    (246282, 246299), (246301, 246324), (246327, 246453),
                    (246456, 246457), (246459, 246459), (246463, 246470)
                }
            },
            {
                "General", new List<(uint, uint)>
                {
                    (0, 95), (101, 132), (651, 652), (654, 655), (695, 698),
                    (66001, 66001), (66021, 66023), (66031, 66033), (66041, 66043),
                    (66051, 66053), (66061, 66063), (66071, 66073), (66081, 66083),
                    (66101, 66105), (66121, 66125), (66141, 66145), (66161, 66171),
                    (66181, 66191), (66301, 66341), (66401, 66423), (66452, 66473),
                    (60001, 60048), (60071, 60074), (61471, 61489), (61501, 61548),
                    (61551, 61598), (61751, 61768), (61801, 61850), (61875, 61880)
                }
            },
            {
                "Jobs", new List<(uint, uint)>
                {
                    (62001, 62042), (62801, 62842), (62226, 62267),
                    (62101, 62142), (62301, 62320), (62401, 62422),
                    (82271, 82286)
                }
            },
            {
                "Minions", new List<(uint, uint)>
                {
                    (4401, 4521), (4523, 4611), (4613, 4939), (4941, 4962),
                    (4964, 4967), (4971, 4973), (4977, 4979),
                    (59401, 59521), (59523, 59611), (59613, 59939), (59941, 59962),
                    (59964, 59967), (59971, 59973), (59977, 59979)
                }
            },
            {
                "Mounts", new List<(uint, uint)>
                {
                    (4001, 4045), (4047, 4098), (4101, 4276), (4278, 4329),
                    (4331, 4332), (4334, 4335), (4339, 4339), (4343, 4343)
                }
            },
            {
                "Shapes & Symbols", new List<(uint, uint)>
                {
                    (82091, 82093), (90001, 90004), (90200, 90263), (90401, 90463),
                    (61901, 61918), (230131, 230143), (230201, 230215), (230301, 230317),
                    (230401, 230433), (230701, 230715), (230626, 230629), (230631, 230641),
                    (180021, 180028)
                }
            }
        };

        LoadIconsForCategory(selectedCategory);
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

    private void LoadIconsForCategory(string category)
    {
        currentIcons.Clear();

        if (category == "Favorites")
        {
            currentIcons.AddRange(favoriteIcons.OrderBy(i => i));
        }
        else if (iconCategories.TryGetValue(category, out var ranges))
        {
            foreach (var (start, end) in ranges)
            {
                for (uint i = start; i <= end; i++)
                {
                    currentIcons.Add(i);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(searchFilter))
        {
            var filterLower = searchFilter.ToLower();
            currentIcons = currentIcons.Where(id =>
                id.ToString().Contains(filterLower)).ToList();
        }
    }

    public override void Draw()
    {
        UIStyles.PushMainWindowStyle();

        try
        {
            var scale = UIStyles.Scale;
            var windowSize = ImGui.GetWindowSize();
            var buttonHeight = 30f * scale;
            var sidebarWidth = 150f * scale;
            var padding = 8f * scale;

            // Categories sidebar
            if (ImGui.BeginChild("Categories", new Vector2(sidebarWidth, -buttonHeight - padding * 2), true))
            {
                ImGui.Text("Categories");
                ImGui.Separator();

                // Favourites category
                {
                    var isSelected = "Favorites" == selectedCategory;
                    if (isSelected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));

                    var buttonText = $"Favorites ({favoriteIcons.Count})";
                    if (ImGui.Button(buttonText, new Vector2(-1, 0)))
                    {
                        selectedCategory = "Favorites";
                        LoadIconsForCategory("Favorites");
                    }

                    if (isSelected) ImGui.PopStyleColor();
                }

                ImGui.Separator();

                foreach (var category in iconCategories.Keys)
                {
                    var isSelected = category == selectedCategory;
                    if (isSelected) ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive));

                    if (ImGui.Button(category, new Vector2(-1, 0)))
                    {
                        selectedCategory = category;
                        LoadIconsForCategory(category);
                    }

                    if (isSelected) ImGui.PopStyleColor();
                }
            }
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginGroup();

            // Search
            ImGui.Text("Search by ID:");
            ImGui.SetNextItemWidth(-1);
            if (ImGui.InputText("##search", ref searchFilter, 50))
            {
                LoadIconsForCategory(selectedCategory);
            }

            ImGui.Separator();
            ImGui.Spacing();

            // Icon grid
            var remainingHeight = ImGui.GetContentRegionAvail().Y - buttonHeight - padding * 2;
            if (ImGui.BeginChild("IconGrid", new Vector2(-1, remainingHeight), false, ImGuiWindowFlags.HorizontalScrollbar))
            {
                DrawIconGrid();
            }
            ImGui.EndChild();

            ImGui.EndGroup();

            // Bottom bar
            ImGui.Separator();

            ImGui.Text("Selected:");
            ImGui.SameLine();
            if (SelectedIconId.HasValue)
            {
                var iconTexture = GetGameIcon(SelectedIconId.Value);
                if (iconTexture != null)
                {
                    ImGui.Image(iconTexture.Handle, new Vector2(24 * scale, 24 * scale));
                    ImGui.SameLine();
                }
                ImGui.Text($"Icon ID: {SelectedIconId.Value}");
            }
            else
            {
                ImGui.Text("None");
            }

            ImGui.SameLine(windowSize.X - 150 * scale);

            if (ImGui.Button("Cancel", new Vector2(70 * scale, 0)))
            {
                SelectedIconId = null;
                Confirmed = false;
                IsOpen = false;
            }

            ImGui.SameLine();

            UIStyles.PushAccentButtonStyle();
            if (ImGui.Button("Confirm", new Vector2(70 * scale, 0)))
            {
                Confirmed = true;
                IsOpen = false;
            }
            UIStyles.PopAccentButtonStyle();
        }
        finally
        {
            UIStyles.PopMainWindowStyle();
        }
    }

    private void DrawIconGrid()
    {
        if (currentIcons.Count == 0)
        {
            ImGui.Text("No icons found.");
            return;
        }

        var scale = UIStyles.Scale;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var iconSize = 48f * scale;
        var spacing = 8f * scale;
        var cellSize = iconSize + spacing;

        var iconsPerRow = Math.Max(1, (int)Math.Floor(availableWidth / cellSize));
        var actualCellWidth = availableWidth / iconsPerRow;

        var drawList = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();

        for (int i = 0; i < currentIcons.Count; i++)
        {
            int col = i % iconsPerRow;
            int row = i / iconsPerRow;

            var cellPos = startPos + new Vector2(
                col * actualCellWidth,
                row * (cellSize + spacing)
            );

            var cellMin = cellPos;
            var cellMax = cellPos + new Vector2(actualCellWidth - spacing, cellSize);

            var iconId = currentIcons[i];
            bool isHovered = ImGui.IsMouseHoveringRect(cellMin, cellMax);
            bool isSelected = SelectedIconId == iconId;
            bool isFavorite = favoriteIcons.Contains(iconId);
            bool clicked = isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
            bool rightClicked = isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);

            var bgColor = isSelected
                ? new Vector4(0.3f, 0.6f, 0.3f, 0.8f)
                : isHovered
                    ? new Vector4(0.4f, 0.4f, 0.4f, 0.6f)
                    : new Vector4(0.2f, 0.2f, 0.2f, 0.4f);

            drawList.AddRectFilled(cellMin, cellMax, ImGui.ColorConvertFloat4ToU32(bgColor), 4f * scale);

            if (isSelected || isHovered)
            {
                var borderColor = isSelected
                    ? new Vector4(0.4f, 0.8f, 0.4f, 1.0f)
                    : new Vector4(0.6f, 0.6f, 0.6f, 1.0f);
                drawList.AddRect(cellMin, cellMax, ImGui.ColorConvertFloat4ToU32(borderColor), 4f * scale, ImDrawFlags.RoundCornersAll, 1.5f * scale);
            }

            var iconTexture = GetGameIcon(iconId);
            if (iconTexture != null)
            {
                var iconPos = cellMin + new Vector2(((actualCellWidth - spacing) - iconSize) / 2, (cellSize - iconSize) / 2);
                drawList.AddImage(iconTexture.Handle, iconPos, iconPos + new Vector2(iconSize, iconSize));
            }
            else
            {
                var fallbackText = iconId.ToString();
                var textSize = ImGui.CalcTextSize(fallbackText);
                var textPos = cellMin + new Vector2(
                    ((actualCellWidth - spacing) - textSize.X) / 2,
                    (cellSize - textSize.Y) / 2
                );
                drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.6f, 0.6f, 1.0f)), fallbackText);
            }

            // Favourite star
            if (isFavorite)
            {
                var starPos = cellMin + new Vector2(actualCellWidth - spacing - 16f * scale, 2f * scale);
                var starColor = new Vector4(1.0f, 0.84f, 0.0f, 1.0f);
                drawList.AddText(starPos, ImGui.ColorConvertFloat4ToU32(starColor), "*");
            }

            if (clicked)
            {
                SelectedIconId = iconId;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // Double-click to confirm
                if (lastClickTime > 0 && now - lastClickTime < 250)
                {
                    Confirmed = true;
                    IsOpen = false;
                }

                lastClickTime = now;
            }

            // Right-click to favourite
            if (rightClicked)
            {
                if (favoriteIcons.Contains(iconId))
                    favoriteIcons.Remove(iconId);
                else
                    favoriteIcons.Add(iconId);

                if (Plugin.Instance?.Configuration != null)
                {
                    Plugin.Instance.Configuration.FavoriteIconIds = favoriteIcons.ToList();
                    Plugin.Instance.Configuration.Save();
                }

                if (selectedCategory == "Favorites")
                    LoadIconsForCategory("Favorites");
            }

            if (isHovered)
            {
                ImGui.BeginTooltip();
                ImGui.Text($"Icon ID: {iconId}");
                ImGui.TextDisabled(isFavorite ? "Right-click to unfavorite" : "Right-click to favorite");
                ImGui.EndTooltip();
            }
        }

        int totalRows = (currentIcons.Count + iconsPerRow - 1) / iconsPerRow;
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + totalRows * (cellSize + spacing));
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

    public void Reset(uint? initialIcon = null)
    {
        SelectedIconId = initialIcon;
        Confirmed = false;
        favoriteIcons = new HashSet<uint>(Plugin.Instance?.Configuration.FavoriteIconIds ?? new List<uint>());
        LoadIconsForCategory(selectedCategory);
    }
}
