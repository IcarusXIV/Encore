using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Encore.Styles;

namespace Encore.Windows;

public class PatchNotesWindow : Window
{
    private readonly Plugin plugin;
    private bool hasScrolledToEnd = false;
    private bool wasOpen = false;

    // Collapsible section states (latest version starts open, older ones collapsed)
    private bool v1005Open = true;
    private bool v100Open = false;

    public PatchNotesWindow(Plugin plugin) : base("Encore - What's New###EncorePatchNotes")
    {
        this.plugin = plugin;
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.Scale;
        Size = new Vector2(520f * scale, 560f * scale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520f * scale, 560f * scale),
            MaximumSize = new Vector2(520f * scale, 560f * scale)
        };
    }

    public override void Draw()
    {
        UIStyles.PushMainWindowStyle();

        try
        {
            var scale = UIStyles.Scale;

            // Track open/close transitions to reset scroll state
            if (IsOpen && !wasOpen)
            {
                hasScrolledToEnd = false;
            }
            wasOpen = IsOpen;

            // Header
            DrawHeader();

            // Scrollable content
            var bottomBarHeight = 45 * scale;
            var contentHeight = ImGui.GetContentRegionAvail().Y - bottomBarHeight;

            if (ImGui.BeginChild("PatchNotesScroll", new Vector2(-1, contentHeight), false))
            {
                DrawVersion1005Notes();
                DrawVersion100Notes();

                // Track scroll position
                var scrollY = ImGui.GetScrollY();
                var maxScrollY = ImGui.GetScrollMaxY();
                if (maxScrollY > 0 && scrollY >= maxScrollY * 0.85f)
                {
                    hasScrolledToEnd = true;
                }
                // If content fits without scrolling, consider it "scrolled"
                if (maxScrollY <= 0)
                {
                    hasScrolledToEnd = true;
                }
            }
            ImGui.EndChild();

            // Bottom bar
            ImGui.Separator();
            DrawBottomBar();
        }
        finally
        {
            UIStyles.PopMainWindowStyle();
        }
    }

    private void DrawHeader()
    {
        var scale = UIStyles.Scale;
        var accentColor = new Vector4(0.4f, 0.7f, 1f, 1f);

        ImGui.Spacing();
        ImGui.Spacing();

        // Centered music icon
        ImGui.PushFont(UiBuilder.IconFont);
        var iconText = FontAwesomeIcon.Music.ToIconString();
        var iconWidth = ImGui.CalcTextSize(iconText).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - iconWidth) / 2);
        ImGui.TextColored(accentColor, iconText);
        ImGui.PopFont();

        ImGui.Spacing();

        // Title
        var title = $"What's New in v{Plugin.PatchNotesVersion}";
        var titleWidth = ImGui.CalcTextSize(title).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - titleWidth) / 2);
        ImGui.TextColored(accentColor, title);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private void DrawBottomBar()
    {
        var scale = UIStyles.Scale;

        ImGui.Spacing();

        var buttonWidth = 120 * scale;
        var buttonHeight = 28 * scale;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - buttonWidth) / 2);

        if (!hasScrolledToEnd)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            ImGui.Button("Got it!", new Vector2(buttonWidth, buttonHeight));
            ImGui.PopStyleVar();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Scroll through the new features first!");
            }
        }
        else
        {
            UIStyles.PushAccentButtonStyle();
            if (ImGui.Button("Got it!", new Vector2(buttonWidth, buttonHeight)))
            {
                plugin.Configuration.LastSeenPatchNotesVersion = Plugin.PatchNotesVersion;
                plugin.Configuration.Save();
                IsOpen = false;

                // Open main window if it isn't already
                if (Plugin.Instance != null)
                {
                    var mainWindow = Plugin.Instance.WindowSystem.Windows
                        .OfType<MainWindow>().FirstOrDefault();
                    if (mainWindow != null && !mainWindow.IsOpen)
                        mainWindow.IsOpen = true;
                }
            }
            UIStyles.PopAccentButtonStyle();
        }
    }

    private void DrawVersion1005Notes()
    {
        var scale = UIStyles.Scale;
        var sectionColor = new Vector4(0.5f, 0.8f, 0.5f, 1f);

        if (UIStyles.CollapsibleAccentSectionHeader("Version 1.0.0.5 - Fixes & Improvements", sectionColor, ref v1005Open))
        {
            ImGui.Indent(8 * scale);

            DrawFeatureItem(FontAwesomeIcon.Star, "Don't Have the Emote? No Problem.",
                "With 'Allow All Emotes' enabled in settings (gear icon), your dance and emote mod presets work regardless of whether you have the base emote or not. Just check 'I don't have this emote' when creating a preset. You can also use /vanilla <emote> in chat for a quick one-off. This automatically creates a Penumbra mod called _EncoreEmoteSwap in your mod directory -- this is normal and required for the bypass to work. Like other mods through sync plugins, the animation may not be visible to others on the first play -- just give it a second to load, then do the emote again!",
                new Vector4(1f, 0.8f, 0.3f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Music, "Vanilla Emotes",
                "Use /vanilla <emote> to play the original unmodded animation. Temporarily disables conflicting mods for that emote. You can also create a vanilla preset to have it as a one-click option.",
                new Vector4(0.6f, 0.6f, 0.8f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Image, "Custom Icon Fix",
                "Fixed custom icon uploads failing for some users.",
                new Vector4(0.5f, 0.7f, 0.9f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Search, "Icon Search by Name",
                "Icon picker now lets you search by emote, mount, or minion name -- not just icon ID.",
                new Vector4(0.6f, 0.8f, 0.5f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Cogs, "Better Mod Detection",
                "Improved detection for movement mods, hyphenated emotes (Push-ups), multi-word emotes (Paint It Black, Flower Shower), and multi-pose-type mods.",
                new Vector4(0.9f, 0.6f, 0.3f, 1f));

            ImGui.Unindent(8 * scale);
        }
    }

    private void DrawVersion100Notes()
    {
        var scale = UIStyles.Scale;
        var sectionColor = new Vector4(0.4f, 0.7f, 1f, 1f);

        if (UIStyles.CollapsibleAccentSectionHeader("Version 1.0 - Initial Release", sectionColor, ref v100Open))
        {
            ImGui.Indent(8 * scale);

            DrawFeatureItem(FontAwesomeIcon.Play, "One-Click Preset Switching",
                "Create presets for your dance and emote mods. Switch between them with a single click on the preset card -- Encore handles all the Penumbra priority management automatically.",
                new Vector4(0.4f, 0.8f, 0.4f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Terminal, "Chat Commands",
                "Assign custom chat commands to your presets (e.g., /mydance). Use /encore to open the main window, /encorereset to restore all mods.",
                new Vector4(0.5f, 0.7f, 0.9f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Shield, "Mod Conflict Resolution",
                "When you activate a preset, conflicting emote mods are automatically disabled. Everything is restored when you switch or reset.",
                new Vector4(0.9f, 0.6f, 0.3f, 1f));

            DrawFeatureItem(FontAwesomeIcon.User, "Pose Presets",
                "Supports idle, sit, groundsit, and doze pose mods. Encore writes the correct pose index and cycles /cpose for you -- no manual counting needed.",
                new Vector4(0.7f, 0.5f, 0.9f, 1f));

            DrawFeatureItem(FontAwesomeIcon.LayerGroup, "Modifiers",
                "Add named variants to a single preset instead of duplicating it. e.g., /mydance slow or /mydance fast -- each overrides specific mod options.",
                new Vector4(0.95f, 0.6f, 0.2f, 1f));

            DrawFeatureItem(FontAwesomeIcon.FolderOpen, "Folders & Organization",
                "Group presets into color-coded folders. Drag and drop to reorder. Sort by name, command, favorites, or newest.",
                new Vector4(0.5f, 0.8f, 0.7f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Redo, "Emote Looping",
                "Use /loop <emote> to continuously repeat any non-looping emote. Move to stop.",
                new Vector4(0.4f, 0.75f, 0.75f, 1f));

            DrawFeatureItem(FontAwesomeIcon.LocationCrosshairs, "Align to Target",
                "For duo emotes, use /align or the button in the bottom bar. Your character walks to your target and matches their rotation.",
                new Vector4(0.8f, 0.5f, 0.5f, 1f));

            DrawFeatureItem(FontAwesomeIcon.Walking, "Movement Mods",
                "Walk, sprint, and jog animation mods are detected and supported as presets.",
                new Vector4(0.6f, 0.8f, 0.5f, 1f));

            ImGui.Unindent(8 * scale);
        }
    }

    private void DrawFeatureItem(FontAwesomeIcon icon, string title, string description, Vector4 color)
    {
        var scale = UIStyles.Scale;

        // Icon
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();

        // Colored title
        ImGui.TextColored(color, title);

        // Description indented below
        ImGui.Indent(24 * scale);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.65f, 1f));
        ImGui.TextWrapped(description);
        ImGui.PopStyleColor();
        ImGui.Unindent(24 * scale);
        ImGui.Spacing();
    }
}
