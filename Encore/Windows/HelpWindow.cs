using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Encore.Styles;

namespace Encore.Windows;

public class HelpWindow : Window
{
    private int currentPage = 0;
    private const int TotalPages = 4;

    public HelpWindow() : base("Encore Guide###EncoreHelp")
    {
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoDocking;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.Scale;
        Size = new Vector2(520f * scale, 420f * scale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(520f * scale, 420f * scale),
            MaximumSize = new Vector2(520f * scale, 420f * scale)
        };
    }

    public override void Draw()
    {
        UIStyles.PushMainWindowStyle();

        try
        {
            var scale = UIStyles.Scale;

            // Content area
            if (ImGui.BeginChild("Content", new Vector2(-1, -45 * scale), false))
            {
                switch (currentPage)
                {
                    case 0: DrawWelcomePage(); break;
                    case 1: DrawCreatePresetPage(); break;
                    case 2: DrawUsingPresetsPage(); break;
                    case 3: DrawTipsPage(); break;
                }
            }
            ImGui.EndChild();

            // Navigation bar
            ImGui.Separator();
            ImGui.Spacing();
            DrawNavigation();
        }
        finally
        {
            UIStyles.PopMainWindowStyle();
        }
    }

    private void DrawWelcomePage()
    {
        var scale = UIStyles.Scale;

        ImGui.Spacing();
        ImGui.Spacing();

        // Icon and title
        CenteredIcon(FontAwesomeIcon.Music, new Vector4(0.4f, 0.7f, 1f, 1f));
        ImGui.Spacing();
        CenteredText("Welcome to Encore!", new Vector4(0.4f, 0.7f, 1f, 1f), true);
        CenteredText("Dance Mod Preset Manager", new Vector4(0.5f, 0.5f, 0.5f, 1f), false);

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Spacing();

        // What it does
        ImGui.TextWrapped("Encore lets you create presets for your dance mods. Switch between different dances with a single click or chat command - no more manually changing Penumbra priorities!");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Features list
        DrawIconBullet(FontAwesomeIcon.Play, "One-click switching between dance mods");
        ImGui.Spacing();
        DrawIconBullet(FontAwesomeIcon.Terminal, "Custom chat commands (e.g., /mydance)");
        ImGui.Spacing();
        DrawIconBullet(FontAwesomeIcon.Cog, "Automatic Penumbra priority management");
        ImGui.Spacing();
        DrawIconBullet(FontAwesomeIcon.User, "Pose presets auto-switch to the correct idle, sit, groundsit, or doze pose number");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Spacing();

        CenteredText("Click 'Next' to learn how to set up your first preset.", new Vector4(0.6f, 0.6f, 0.6f, 1f), false);
    }

    private void DrawCreatePresetPage()
    {
        UIStyles.SectionHeader("Creating a Preset");

        DrawStep("1", "Click 'New Preset'", "The button is at the bottom of the main window.");
        DrawStep("2", "Name your preset", "Something memorable like 'Victory Dance'.");
        DrawStep("3", "Set a chat command", "A short command like 'vdance' - you'll use /vdance to trigger it.");
        DrawStep("4", "Choose an icon", "(Optional) Pick a game icon to represent this preset.");
        DrawStep("5", "Select your mod", "Browse the list and click on the dance mod you want.");
        DrawStep("6", "Pick the emote", "If the mod replaces multiple emotes, choose which one to use.");
        DrawStep("7", "Configure mod settings (optional)", "If the mod has option groups, you can pick which settings to apply when the preset activates. These are restored when you switch away.");
        DrawStep("8", "Add modifiers (optional)", "Create named variants that override specific options or switch emotes. e.g., /mydance slow. More on the next page!");

        ImGui.Spacing();

        CenteredIcon(FontAwesomeIcon.Check, new Vector4(0.4f, 0.8f, 0.4f, 1f));
        CenteredText("Click 'Save' and you're done!", new Vector4(0.4f, 0.8f, 0.4f, 1f), false);
    }

    private void DrawUsingPresetsPage()
    {
        UIStyles.SectionHeader("Using Your Presets");

        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "Two ways to activate a preset:");

        ImGui.Spacing();
        ImGui.Spacing();

        // Method 1
        DrawIconBullet(FontAwesomeIcon.MousePointer, "Click the 'Play' button on any preset card");

        ImGui.Spacing();

        // Method 2
        DrawIconBullet(FontAwesomeIcon.Terminal, "Type your command in chat (e.g., /mydance)");

        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), "What happens when you activate:");

        ImGui.Spacing();
        ImGui.Spacing();

        ImGui.Bullet(); ImGui.SameLine();
        ImGui.TextWrapped("Your mod's priority gets boosted in Penumbra");

        ImGui.Bullet(); ImGui.SameLine();
        ImGui.TextWrapped("Conflicting emote mods are temporarily disabled");

        ImGui.Bullet(); ImGui.SameLine();
        ImGui.TextWrapped("Your character performs the dance!");

        ImGui.Bullet(); ImGui.SameLine();
        ImGui.TextWrapped("For pose presets, your character switches to the correct pose number");

        ImGui.Spacing();
        ImGui.Spacing();

        var warningColor = new Vector4(1f, 0.85f, 0.4f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, warningColor);
        DrawIconBullet(FontAwesomeIcon.ExclamationTriangle, "Sit & Doze Presets", warningColor);
        ImGui.PopStyleColor();
        ImGui.TextWrapped("By default, chair-sit and doze presets require nearby furniture. To sit/doze anywhere, enable 'Allow Sit/Doze Anywhere' in the settings (gear icon). This sends position data to the server and is not standard game behaviour.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var modifierColor = new Vector4(0.95f, 0.6f, 0.2f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, modifierColor);
        DrawIconBullet(FontAwesomeIcon.LayerGroup, "Modifiers - Multiple Variants, One Preset", modifierColor);
        ImGui.PopStyleColor();
        ImGui.TextWrapped("Instead of creating separate presets for each variation, add modifiers to a single preset. Each modifier can override the mod's options or switch to a different emote/pose.");

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Example:");
        ImGui.Indent(12 * UIStyles.Scale);
        ImGui.TextWrapped("You have a jumping jacks mod with speed options. Create one preset '/jj' and add modifiers 'slow' and 'fast' that each select different speed settings.");
        ImGui.Unindent(12 * UIStyles.Scale);

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "How to use:");
        ImGui.Indent(12 * UIStyles.Scale);
        ImGui.Bullet(); ImGui.SameLine();
        ImGui.TextWrapped("Chat command: /jj slow  or  /jj fast");
        ImGui.Bullet(); ImGui.SameLine();
        ImGui.TextWrapped("Context menu: right-click the '...' button for 'Play: slow', 'Play: fast'");
        ImGui.Unindent(12 * UIStyles.Scale);

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Setup:");
        ImGui.Indent(12 * UIStyles.Scale);
        ImGui.TextWrapped("Edit a preset and expand the 'Modifiers' section at the bottom. Click '+' to add a modifier, configure which options to override, then save.");
        ImGui.Unindent(12 * UIStyles.Scale);

        ImGui.Spacing();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "Everything is restored when you use a different preset or reset.");
    }

    private void DrawTipsPage()
    {
        UIStyles.SectionHeader("Tips & Good to Know");

        // Important warning
        var warningColor = new Vector4(1f, 0.85f, 0.4f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Text, warningColor);
        DrawIconBullet(FontAwesomeIcon.ExclamationTriangle, "Same base emote?", warningColor);
        ImGui.PopStyleColor();
        ImGui.TextWrapped("If switching between mods that replace the same emote (like two /dance mods), stop dancing first or the new one won't play.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawIconBullet(FontAwesomeIcon.Edit, "Double-click a preset card to edit it.");
        ImGui.Spacing();

        DrawIconBullet(FontAwesomeIcon.Thumbtack, "Pin button - mark mods that should never be disabled.");
        ImGui.Spacing();

        DrawIconBullet(FontAwesomeIcon.Sync, "Shift+Refresh rescans all your mods.");
        ImGui.Spacing();

        DrawIconBullet(FontAwesomeIcon.Undo, "Reset button - Ctrl+Shift+Click to restore all mods.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawIconBullet(FontAwesomeIcon.Ban, "Vanilla preset - use the original game animation.");
        ImGui.TextWrapped("Check 'Use vanilla animation' when creating a preset. Enter the emote command (e.g., /dance). This disables conflicting mods without enabling any, letting you use the vanilla animation.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.PushStyleColor(ImGuiCol.Text, warningColor);
        DrawIconBullet(FontAwesomeIcon.LocationCrosshairs, "Align to Target - duo emote alignment", warningColor);
        ImGui.PopStyleColor();
        ImGui.TextWrapped("For duo/group emotes, use the align button or /align to walk to your target's position. Stand right next to them first -- the button turns green when you're close enough. Your character will physically walk to them and match their rotation. Avoid clicking your mouse during the walk for best results.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1f), "Commands:");
        ImGui.Spacing();
        ImGui.Text("/encore");
        ImGui.SameLine(140 * UIStyles.Scale);
        ImGui.Text("Opens the main window");
        ImGui.Text("/encorereset");
        ImGui.SameLine(140 * UIStyles.Scale);
        ImGui.Text("Reset all mods");
        ImGui.Text("/align");
        ImGui.SameLine(140 * UIStyles.Scale);
        ImGui.Text("Walk to target for alignment");
        ImGui.Text("/loop <emote>");
        ImGui.SameLine(140 * UIStyles.Scale);
        ImGui.Text("Loop an emote (move to stop)");
    }

    private void DrawNavigation()
    {
        var scale = UIStyles.Scale;
        var buttonWidth = 80f * scale;

        // Back button
        if (currentPage == 0) ImGui.BeginDisabled();
        if (ImGui.Button("Back", new Vector2(buttonWidth, 0)))
        {
            currentPage--;
        }
        if (currentPage == 0) ImGui.EndDisabled();

        // Page indicator
        ImGui.SameLine();
        var pageText = $"Page {currentPage + 1} of {TotalPages}";
        var pageTextWidth = ImGui.CalcTextSize(pageText).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - pageTextWidth) / 2);
        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), pageText);

        // Next/Done button
        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetWindowWidth() - buttonWidth - 15 * scale);

        if (currentPage == TotalPages - 1)
        {
            UIStyles.PushAccentButtonStyle();
            if (ImGui.Button("Done!", new Vector2(buttonWidth, 0)))
            {
                IsOpen = false;
                currentPage = 0;
            }
            UIStyles.PopAccentButtonStyle();
        }
        else
        {
            UIStyles.PushAccentButtonStyle();
            if (ImGui.Button("Next", new Vector2(buttonWidth, 0)))
            {
                currentPage++;
            }
            UIStyles.PopAccentButtonStyle();
        }
    }

    private void DrawStep(string number, string title, string description)
    {
        var scale = UIStyles.Scale;

        ImGui.TextColored(new Vector4(0.4f, 0.7f, 1f, 1f), number);
        ImGui.SameLine();
        ImGui.Text(title);
        ImGui.Indent(24 * scale);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));
        ImGui.TextWrapped(description);
        ImGui.PopStyleColor();
        ImGui.Unindent(24 * scale);
        ImGui.Spacing();
    }

    private void DrawIconBullet(FontAwesomeIcon icon, string text, Vector4? iconColor = null)
    {
        var color = iconColor ?? new Vector4(0.5f, 0.7f, 0.9f, 1f);
        ImGui.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(color, icon.ToIconString());
        ImGui.PopFont();
        ImGui.SameLine();
        ImGui.TextWrapped(text);
    }

    private void CenteredIcon(FontAwesomeIcon icon, Vector4 color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var iconText = icon.ToIconString();
        var iconWidth = ImGui.CalcTextSize(iconText).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - iconWidth) / 2);
        ImGui.TextColored(color, iconText);
        ImGui.PopFont();
    }

    private void CenteredText(string text, Vector4 color, bool large)
    {
        var textWidth = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - textWidth) / 2);
        ImGui.TextColored(color, text);
    }
}
