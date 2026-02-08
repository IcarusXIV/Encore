using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Encore.Styles;

// UI styling helpers adapted from Character Select+
public static class UIStyles
{
    private static int colorStackCount = 0;
    private static int styleStackCount = 0;

    public static float Scale => ImGuiHelpers.GlobalScale;

    public static void PushMainWindowStyle()
    {
        float scale = Scale;

        // Matte black styling
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.06f, 0.06f, 0.06f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.08f, 0.08f, 0.08f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.06f, 0.06f, 0.06f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.12f, 0.12f, 0.12f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.18f, 0.18f, 0.18f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.22f, 0.22f, 0.22f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.04f, 0.04f, 0.04f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.06f, 0.06f, 0.06f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, new Vector4(0.06f, 0.06f, 0.06f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0.04f, 0.04f, 0.04f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(0.2f, 0.2f, 0.2f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.3f, 0.3f, 0.3f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, new Vector4(0.4f, 0.4f, 0.4f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.25f, 0.25f, 0.25f, 0.6f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, new Vector4(0.35f, 0.35f, 0.35f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, new Vector4(0.45f, 0.45f, 0.45f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.92f, 0.92f, 0.92f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(0.5f, 0.5f, 0.5f, 0.8f));

        // Button styling
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.16f, 0.16f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.22f, 0.22f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.28f, 0.28f, 0.28f, 0.9f));

        // Header styling
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.15f, 0.15f, 0.15f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.22f, 0.22f, 0.22f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.28f, 0.28f, 0.28f, 0.9f));

        // Tab styling
        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.12f, 0.12f, 0.12f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.22f, 0.22f, 0.22f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.18f, 0.18f, 0.18f, 0.9f));

        colorStackCount = 27;

        // Style variables
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(8 * scale, 4 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8 * scale, 6 * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10.0f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 8.0f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 6.0f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 8.0f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 6.0f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1.0f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 0.5f * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0.5f * scale);

        styleStackCount = 10;
    }

    public static void PopMainWindowStyle()
    {
        ImGui.PopStyleVar(styleStackCount);
        ImGui.PopStyleColor(colorStackCount);
        styleStackCount = 0;
        colorStackCount = 0;
    }

    public static void PushAccentButtonStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.4f, 0.6f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.5f, 0.7f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.3f, 0.55f, 0.75f, 0.9f));
    }

    public static void PopAccentButtonStyle()
    {
        ImGui.PopStyleColor(3);
    }

    public static void PushDangerButtonStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.5f, 0.15f, 0.15f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.6f, 0.2f, 0.2f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.7f, 0.25f, 0.25f, 0.9f));
    }

    public static void PopDangerButtonStyle()
    {
        ImGui.PopStyleColor(3);
    }

    public static void PushSuccessButtonStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.15f, 0.4f, 0.15f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.2f, 0.5f, 0.2f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.25f, 0.6f, 0.25f, 0.9f));
    }

    public static void PopSuccessButtonStyle()
    {
        ImGui.PopStyleColor(3);
    }

    public static bool BeginCard(string id, Vector2 size, bool border = true)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.1f, 0.1f, 0.1f, 0.9f));
        var result = ImGui.BeginChild(id, size, border);
        return result;
    }

    public static void EndCard()
    {
        ImGui.EndChild();
        ImGui.PopStyleColor();
    }

    public static void TextCentered(string text)
    {
        var windowWidth = ImGui.GetWindowSize().X;
        var textWidth = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX((windowWidth - textWidth) * 0.5f);
        ImGui.Text(text);
    }

    public static void SectionHeader(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.7f, 0.9f, 1f, 1f), text);
        ImGui.Separator();
        ImGui.Spacing();
    }

    public static void HelpTooltip(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(300);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
