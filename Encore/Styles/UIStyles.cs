using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace Encore.Styles;

// UI styling helpers adapted from Character Select+
public static class UIStyles
{
    private static int colorStackCount = 0;
    private static int styleStackCount = 0;

    public static float Scale => ImGuiHelpers.GlobalScale;

    // letter-spaced text (ImGui has no native letter-spacing)
    public static void DrawTrackedText(
        ImDrawListPtr dl, Vector2 pos, string text, uint color, float track)
    {
        if (string.IsNullOrEmpty(text)) return;
        float cx = pos.X;
        for (int i = 0; i < text.Length; i++)
        {
            var s = text.Substring(i, 1);
            dl.AddText(new Vector2(cx, pos.Y), color, s);
            cx += ImGui.CalcTextSize(s).X + track;
        }
    }

    public static float MeasureTrackedWidth(string text, float track)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
            w += ImGui.CalcTextSize(text.Substring(i, 1)).X;
        return w + track * MathF.Max(0, text.Length - 1);
    }

    // window-chrome scale; honors Configuration.IgnoreGlobalScale
    public static float WindowScale =>
        (Plugin.Instance?.Configuration?.IgnoreGlobalScale ?? false) ? 1f : ImGuiHelpers.GlobalScale;

    public static void PushMainWindowStyle()
    {
        float scale = Scale;

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.047f, 0.055f, 0.075f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.047f, 0.055f, 0.075f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.047f, 0.055f, 0.075f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0.086f, 0.098f, 0.133f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.120f, 0.138f, 0.180f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, new Vector4(0.150f, 0.172f, 0.220f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.020f, 0.024f, 0.035f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.031f, 0.039f, 0.055f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, new Vector4(0.020f, 0.024f, 0.035f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, new Vector4(0.020f, 0.024f, 0.035f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(0.18f, 0.21f, 0.26f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabHovered, new Vector4(0.25f, 0.29f, 0.36f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrabActive, new Vector4(0.40f, 0.53f, 0.72f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Separator, new Vector4(0.18f, 0.21f, 0.26f, 0.6f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, new Vector4(0.28f, 0.33f, 0.40f, 0.8f));
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, new Vector4(0.49f, 0.65f, 0.85f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.86f, 0.87f, 0.89f, 1.0f));
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, new Vector4(0.56f, 0.58f, 0.63f, 0.85f));

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.120f, 0.138f, 0.180f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.170f, 0.196f, 0.246f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.210f, 0.243f, 0.306f, 0.95f));

        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.49f, 0.65f, 0.85f, 0.22f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.49f, 0.65f, 0.85f, 0.14f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.49f, 0.65f, 0.85f, 0.30f));

        ImGui.PushStyleColor(ImGuiCol.Tab, new Vector4(0.086f, 0.098f, 0.133f, 0.9f));
        ImGui.PushStyleColor(ImGuiCol.TabHovered, new Vector4(0.170f, 0.196f, 0.246f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.TabActive, new Vector4(0.150f, 0.172f, 0.220f, 0.95f));

        colorStackCount = 27;

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

    // Encore cue-sheet theme. PushEncoreWindow in PreDraw, PushEncoreContent in Draw.
    public static readonly Vector4 EncoreWindowBg = new(0.047f, 0.055f, 0.075f, 1f);
    public static readonly Vector4 EncoreBorder   = new(0.18f, 0.21f, 0.26f, 1f);
    public static readonly Vector4 EncoreContentBg = new(0.047f, 0.055f, 0.075f, 1f);
    // outer ribbon chrome (patch notes only)
    public static readonly Vector4 EncoreShellBg   = new(0.020f, 0.024f, 0.035f, 1f);

    public static void PushEncoreWindow()
    {
        ImGui.PushStyleColor(ImGuiCol.WindowBg, EncoreWindowBg);
    }

    public static void PopEncoreWindow()
    {
        ImGui.PopStyleColor();
    }

    public static void PushEncoreContent()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,     0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,     0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding,     0f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding,      0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding,       0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize,   1f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, EncoreContentBg);
    }

    public static void PopEncoreContent()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(7);
    }

    public static void PushTooltipStyle()
    {
        var scale = Scale;
        var accent = new Vector4(0.49f, 0.65f, 0.85f, 1f);

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    new Vector2(12f * scale, 8f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,      new Vector2(0f, 3f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,    0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize,  1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg,
            new Vector4(0.035f, 0.042f, 0.063f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border,
            new Vector4(accent.X, accent.Y, accent.Z, 0.45f));
    }

    public static void PopTooltipStyle()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(6);
    }

    public static void EncoreTooltip(string text)
    {
        var scale = Scale;
        PushTooltipStyle();

        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 320f * scale);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();

        PopTooltipStyle();
    }

    public static void EncoreEyebrow(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.56f, 0.62f, 1f));
        ImGui.Text(text.ToUpperInvariant());
        ImGui.PopStyleColor();
        ImGui.Spacing();
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

    public static void PushEncoreComboStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.PopupBg,       new Vector4(0.070f, 0.082f, 0.118f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border,        new Vector4(0.49f, 0.65f, 0.85f, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.Header,        new Vector4(0.49f, 0.65f, 0.85f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.49f, 0.65f, 0.85f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  new Vector4(0.49f, 0.65f, 0.85f, 0.35f));
        ImGui.PushStyleColor(ImGuiCol.Text,          new Vector4(0.86f, 0.87f, 0.89f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,    0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize,  1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
    }

    public static void PopEncoreComboStyle()
    {
        ImGui.PopStyleVar(4);
        ImGui.PopStyleColor(6);
    }

    /// <summary>
    /// CS+ style section header: colored accent bar + uppercase title + fading horizontal line.
    /// </summary>
    public static void AccentSectionHeader(string text, Vector4 color)
    {
        var scale = Scale;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.Dummy(new Vector2(1, 8 * scale));

        var headerPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;

        var dotSize = 8f * scale;
        var cx = headerPos.X + dotSize / 2f;
        var cy = headerPos.Y + 5 * scale + dotSize / 2f;
        for (int step = 3; step >= 1; step--)
        {
            var haloR = dotSize / 2f + step * 3f * scale;
            var haloAlpha = 0.12f - step * 0.03f;
            drawList.AddRectFilled(
                new Vector2(cx - haloR, cy - haloR),
                new Vector2(cx + haloR, cy + haloR),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, haloAlpha)));
        }
        drawList.AddRectFilled(
            new Vector2(cx - dotSize / 2f, cy - dotSize / 2f),
            new Vector2(cx + dotSize / 2f, cy + dotSize / 2f),
            ImGui.ColorConvertFloat4ToU32(color));

        var label = text.ToUpperInvariant();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + dotSize + 12 * scale);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.Text(label);
        ImGui.PopStyleColor();

        // Fading horizontal rule from end of label to right edge (16 segments, decreasing alpha)
        var titleWidth = ImGui.CalcTextSize(label).X;
        var lineStartX = headerPos.X + dotSize + 12 * scale + titleWidth + 10 * scale;
        var lineEndX = headerPos.X + availWidth;
        var lineY = headerPos.Y + 9 * scale;
        const int segments = 16;
        for (int i = 0; i < segments; i++)
        {
            var t0 = i / (float)segments;
            var t1 = (i + 1) / (float)segments;
            var x0 = lineStartX + (lineEndX - lineStartX) * t0;
            var x1 = lineStartX + (lineEndX - lineStartX) * t1;
            var alpha = (1f - t0) * 0.35f;
            drawList.AddLine(
                new Vector2(x0, lineY),
                new Vector2(x1, lineY),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, alpha)),
                1f);
        }

        ImGui.Dummy(new Vector2(1, 10 * scale));
    }

    public static bool CollapsibleAccentSectionHeader(string text, Vector4 color, ref bool isOpen)
    {
        var scale = Scale;
        var drawList = ImGui.GetWindowDrawList();

        ImGui.Dummy(new Vector2(1, 8 * scale));

        var headerPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;
        var headerHeight = 22 * scale;

        var dotSize = 8f * scale;
        var cx = headerPos.X + dotSize / 2f;
        var cy = headerPos.Y + 5 * scale + dotSize / 2f;
        for (int step = 3; step >= 1; step--)
        {
            var haloR = dotSize / 2f + step * 3f * scale;
            var haloAlpha = 0.12f - step * 0.03f;
            drawList.AddRectFilled(
                new Vector2(cx - haloR, cy - haloR),
                new Vector2(cx + haloR, cy + haloR),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, haloAlpha)));
        }
        drawList.AddRectFilled(
            new Vector2(cx - dotSize / 2f, cy - dotSize / 2f),
            new Vector2(cx + dotSize / 2f, cy + dotSize / 2f),
            ImGui.ColorConvertFloat4ToU32(color));

        var arrowText = isOpen ? "v" : ">";
        var arrowColor = new Vector4(color.X * 0.7f, color.Y * 0.7f, color.Z * 0.7f, 1f);
        var label = text.ToUpperInvariant();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + dotSize + 12 * scale);
        ImGui.PushStyleColor(ImGuiCol.Text, arrowColor);
        ImGui.Text(arrowText);
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 8 * scale);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.Text(label);
        ImGui.PopStyleColor();

        var arrowWidth = ImGui.CalcTextSize(arrowText).X;
        var titleWidth = ImGui.CalcTextSize(label).X;
        var lineStartX = headerPos.X + dotSize + 12 * scale + arrowWidth + 8 * scale + titleWidth + 10 * scale;
        var lineEndX = headerPos.X + availWidth;
        var lineY = headerPos.Y + 9 * scale;
        const int segments = 16;
        for (int i = 0; i < segments; i++)
        {
            var t0 = i / (float)segments;
            var t1 = (i + 1) / (float)segments;
            var x0 = lineStartX + (lineEndX - lineStartX) * t0;
            var x1 = lineStartX + (lineEndX - lineStartX) * t1;
            var alpha = (1f - t0) * 0.35f;
            drawList.AddLine(
                new Vector2(x0, lineY),
                new Vector2(x1, lineY),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, alpha)),
                1f);
        }

        ImGui.SetCursorScreenPos(headerPos);
        if (ImGui.InvisibleButton($"##collapse_{text}", new Vector2(availWidth, headerHeight)))
        {
            isOpen = !isOpen;
        }
        if (ImGui.IsItemHovered())
        {
            drawList.AddRectFilled(
                headerPos,
                new Vector2(headerPos.X + availWidth, headerPos.Y + headerHeight),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.05f)));
        }

        ImGui.Dummy(new Vector2(1, 10 * scale));
        return isOpen;
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

    // Centers on glyph's visible X0/X1 bounds; FA advance widths often exceed visible width.
    public static bool IconButton(FontAwesomeIcon icon, Vector2 size, string? tooltip = null,
                                   Vector4? iconColor = null, Vector4? bgOverride = null,
                                   Vector4? hoverOverride = null, Vector4? activeOverride = null)
    {
        var iconStr = icon.ToIconString();

        float visibleX0 = 0f;
        Vector2 iconSize;
        float visibleWidth;
        ImGui.PushFont(UiBuilder.IconFont);
        iconSize = ImGui.CalcTextSize(iconStr);
        visibleWidth = iconSize.X;
        if (!string.IsNullOrEmpty(iconStr))
        {
            try
            {
                unsafe
                {
                    var glyphPtr = ImGui.GetFont().FindGlyph(iconStr[0]);
                    if (glyphPtr != null)
                    {
                        visibleX0 = glyphPtr->X0;
                        visibleWidth = glyphPtr->X1 - glyphPtr->X0;
                    }
                }
            }
            catch { }
        }
        ImGui.PopFont();

        var buttonPos = ImGui.GetCursorScreenPos();
        var buttonId = $"##encoreicn_{iconStr}_{buttonPos.X}_{buttonPos.Y}";
        bool result = ImGui.InvisibleButton(buttonId, size);
        bool isHovered = ImGui.IsItemHovered();
        bool isActive = ImGui.IsItemActive();

        var drawList = ImGui.GetWindowDrawList();
        var buttonEnd = buttonPos + size;

        Vector4 bgColor;
        if (isActive)
            bgColor = activeOverride ?? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (isHovered)
            bgColor = hoverOverride ?? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else
            bgColor = bgOverride ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Button];

        drawList.AddRectFilled(buttonPos, buttonEnd,
            ImGui.ColorConvertFloat4ToU32(bgColor), ImGui.GetStyle().FrameRounding);

        float targetVisibleLeftX = buttonPos.X + (size.X - visibleWidth) * 0.5f;
        var iconPos = new Vector2(
            targetVisibleLeftX - visibleX0,
            buttonPos.Y + (size.Y - iconSize.Y) * 0.5f);
        var textColor = iconColor ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

        ImGui.PushFont(UiBuilder.IconFont);
        drawList.AddText(iconPos, ImGui.ColorConvertFloat4ToU32(textColor), iconStr);
        ImGui.PopFont();

        if (isHovered && !string.IsNullOrEmpty(tooltip))
        {
            ImGui.SetTooltip(tooltip);
        }

        return result;
    }

    // Card flair (drawlist-only, no ImGui state)
    public static void DrawCardAccentStripe(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, Vector4 accent,
        bool highlight, float scale)
    {
        var baseAccent = highlight
            ? new Vector4(
                MathF.Min(accent.X * 1.15f, 1f),
                MathF.Min(accent.Y * 1.15f, 1f),
                MathF.Min(accent.Z * 1.15f, 1f),
                1f)
            : accent;
        var topCol = new Vector4(
            MathF.Min(baseAccent.X * 0.5f + 0.5f, 1f),
            MathF.Min(baseAccent.Y * 0.5f + 0.5f, 1f),
            MathF.Min(baseAccent.Z * 0.5f + 0.5f, 1f),
            1f);
        var botCol = new Vector4(baseAccent.X * 0.82f, baseAccent.Y * 0.82f, baseAccent.Z * 0.82f, 1f);

        var accentW = 3f * scale;
        var midY = (cardMin.Y + cardMax.Y) * 0.5f;
        var rightX = cardMin.X + accentW;

        uint uTop = ImGui.ColorConvertFloat4ToU32(topCol);
        uint uMid = ImGui.ColorConvertFloat4ToU32(baseAccent);
        uint uBot = ImGui.ColorConvertFloat4ToU32(botCol);

        dl.AddRectFilledMultiColor(
            cardMin, new Vector2(rightX, midY),
            uTop, uTop, uMid, uMid);
        dl.AddRectFilledMultiColor(
            new Vector2(cardMin.X, midY), new Vector2(rightX, cardMax.Y),
            uMid, uMid, uBot, uBot);
    }

    public static void DrawCardTopHighlight(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, float scale)
    {
        var accentW = 3f * scale;
        var y = cardMin.Y + 1f;
        dl.AddRectFilled(
            new Vector2(cardMin.X + accentW + 1f, y),
            new Vector2(cardMax.X - 1f, y + 1f),
            ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.08f)));
    }

    public static void DrawCardIdleGlow(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, Vector4 accent, float scale, int seed)
    {
        float t = (float)ImGui.GetTime();
        float w = cardMax.X - cardMin.X;
        float h = cardMax.Y - cardMin.Y;
        // Per-card phase + period offsets from a simple hash of seed.
        uint hA = (uint)(seed * 2654435761u);
        uint hB = (uint)(seed * 40503u + 17u);
        float phaseA = ((hA >> 8) & 0xFFFF) / 65535f * MathF.Tau;
        float phaseB = ((hB >> 8) & 0xFFFF) / 65535f * MathF.Tau;
        float period = 11f + ((hA >> 16) & 0xFF) / 255f * 5f;            // 11..16s

        float cyc = t / period * MathF.Tau;
        // Drift within the card's interior: x spans 20-80%, y spans 30-70%.
        float fx = 0.20f + 0.60f * (0.5f + 0.5f * MathF.Cos(cyc + phaseA));
        float fy = 0.30f + 0.40f * (0.5f + 0.5f * MathF.Sin(cyc * 0.8f + phaseB));
        float cx = cardMin.X + w * fx;
        float cy = cardMin.Y + h * fy;

        float maxR = 46f * scale;
        const int layers = 10;
        float peakAlpha = 0.14f;
        for (int i = 0; i < layers; i++)
        {
            float t01 = (float)i / (layers - 1);
            float r = maxR * (1f - 0.88f * t01);
            float a = peakAlpha * (t01 * t01);
            if (a < 0.01f) continue;
            dl.AddCircleFilled(new Vector2(cx, cy), r,
                ImGui.ColorConvertFloat4ToU32(
                    new Vector4(accent.X, accent.Y, accent.Z, a)),
                24);
        }
    }

    public static void DrawAuroraSpots(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 min, Vector2 max, Vector4 tint,
        float intensityMul = 1f, bool showtime = false)
    {
        float t = (float)ImGui.GetTime();
        float ww = max.X - min.X;
        float wh = max.Y - min.Y;
        float baseR = MathF.Min(ww, wh) * 0.45f;

        float timeMul = showtime ? 1.45f : 1.0f;
        float showAlpha = showtime ? 1.80f : 1.0f;
        float tAdj = t * timeMul;

        Vector4 rose = new(0.85f, 0.55f, 0.70f, 1f);
        Vector4 blue = new(0.52f, 0.70f, 0.92f, 1f);
        Vector4 teal = new(0.55f, 0.85f, 0.90f, 1f);
        float desat = showtime ? 0.75f : 0f;
        Vector4 Mix(Vector4 c) => new(
            c.X + (1f - c.X) * desat,
            c.Y + (1f - c.Y) * desat,
            c.Z + (1f - c.Z) * desat,
            1f);
        rose = Mix(rose);
        blue = Mix(blue);
        teal = Mix(teal);

        void Spot(Vector4 color,
                  float periodX, float phaseX, float ampX, float cxBase,
                  float periodY, float phaseY, float ampY, float cyBase,
                  float breathePeriod, float breathePhase,
                  float maxR, float peakAlpha, float falloffExp)
        {
            float stX = 0.5f + 0.5f * MathF.Sin((tAdj + phaseX) * MathF.Tau / periodX);
            float stY = 0.5f + 0.5f * MathF.Cos((tAdj + phaseY) * MathF.Tau / periodY);
            float cx = min.X + ww * (cxBase + ampX * stX);
            float cy = min.Y + wh * (cyBase + ampY * stY);

            float breath = 0.5f + 0.5f * MathF.Sin((tAdj + breathePhase) * MathF.Tau / breathePeriod);
            float radMul = 0.80f + 0.40f * breath;
            float aMul   = 0.60f + 0.40f * breath;
            float effR = maxR * radMul;

            const int layers = 18;
            for (int l = layers - 1; l >= 0; l--)
            {
                float u = (float)l / (layers - 1);
                float r = effR * (0.12f + 0.88f * u);
                float fall = MathF.Pow(1f - u, falloffExp);
                float a = peakAlpha * fall * aMul * intensityMul * showAlpha;
                dl.AddCircleFilled(
                    new Vector2(cx, cy), r,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(color.X, color.Y, color.Z, a)),
                    48);
            }
        }

        Spot(rose,
             periodX: 22f, phaseX: 0f,  ampX: 0.50f, cxBase: 0.18f,
             periodY: 31f, phaseY: 3f,  ampY: 0.25f, cyBase: 0.28f,
             breathePeriod: 14f, breathePhase: 0f,
             maxR: baseR * 0.60f,
             peakAlpha: 0.028f,
             falloffExp: 2.6f);

        Spot(blue,
             periodX: 18f, phaseX: 7f,  ampX: 0.44f, cxBase: 0.30f,
             periodY: 25f, phaseY: 11f, ampY: 0.30f, cyBase: 0.55f,
             breathePeriod: 17f, breathePhase: 5f,
             maxR: baseR * 1.30f,
             peakAlpha: 0.013f,
             falloffExp: 1.35f);

        Spot(teal,
             periodX: 28f, phaseX: 14f, ampX: 0.30f, cxBase: 0.40f,
             periodY: 19f, phaseY: 17f, ampY: 0.22f, cyBase: 0.15f,
             breathePeriod: 13f, breathePhase: 9f,
             maxR: baseR * 0.90f,
             peakAlpha: 0.017f,
             falloffExp: 2.0f);
    }

    public static void DrawStageAmbient(Vector4 accent, Vector4 expression, Vector4 macro)
    {
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        float t = (float)ImGui.GetTime();
        float wl = winPos.X, wt = winPos.Y;
        float ww = winSize.X, wh = winSize.Y;

        void Ray(float phase, float xTopFrac, float xBotFrac, float freq,
                 Vector4 c, float peakAlpha)
        {
            const int bands = 64;
            float sway = MathF.Sin(t * 2f * MathF.PI / freq + phase) * 90f;
            float xTop = wl + ww * xTopFrac;
            float xBot = wl + ww * xBotFrac + sway;
            float topHalfW = 4f;
            float botHalfW = 95f;
            var clear = new Vector4(c.X, c.Y, c.Z, 0f);
            uint uClear = ImGui.ColorConvertFloat4ToU32(clear);
            for (int i = 0; i < bands; i++)
            {
                float fA = (float)i / bands;
                float fB = (float)(i + 1) / bands;
                float yA = wt + fA * wh;
                float yB = wt + fB * wh;
                float xCA = xTop + (xBot - xTop) * fA;
                float xCB = xTop + (xBot - xTop) * fB;
                float xC  = (xCA + xCB) * 0.5f;
                float hwA = topHalfW + (botHalfW - topHalfW) * fA;
                float hwB = topHalfW + (botHalfW - topHalfW) * fB;
                float hw  = (hwA + hwB) * 0.5f;
                float aA = peakAlpha * MathF.Pow(1f - fA, 1.6f);
                float aB = peakAlpha * MathF.Pow(1f - fB, 1.6f);
                // ramp in over first 15% so hotspot doesn't wash title text
                const float topSafe = 0.15f;
                float revealA = fA < topSafe ? MathF.Pow(fA / topSafe, 1.5f) : 1f;
                float revealB = fB < topSafe ? MathF.Pow(fB / topSafe, 1.5f) : 1f;
                aA *= revealA;
                aB *= revealB;
                var brA = new Vector4(c.X, c.Y, c.Z, aA);
                var brB = new Vector4(c.X, c.Y, c.Z, aB);
                uint uBrA = ImGui.ColorConvertFloat4ToU32(brA);
                uint uBrB = ImGui.ColorConvertFloat4ToU32(brB);
                dl.AddRectFilledMultiColor(
                    new Vector2(xC - hw, yA), new Vector2(xC,       yB),
                    uClear, uBrA, uBrB, uClear);
                dl.AddRectFilledMultiColor(
                    new Vector2(xC,      yA), new Vector2(xC + hw,  yB),
                    uBrA, uClear, uClear, uBrB);
            }
        }
        Ray(0f,   0.20f, 0.20f,  9f, accent,     0.30f);
        Ray(2.1f, 0.55f, 0.55f, 11f, expression, 0.24f);
        Ray(5.3f, 0.84f, 0.84f, 10f, macro,      0.20f);

        void Mote(float xFrac, float period, float phase, float wobbleAmp, float size, Vector4 c)
        {
            float life = (float)(((t + phase) / period) % 1.0);
            float y = wt + wh - life * (wh + 40f);
            float wobble = MathF.Sin(life * MathF.PI * 2f) * wobbleAmp;
            float x = wl + ww * xFrac + wobble;
            float a;
            if (life < 0.12f)      a = life / 0.12f * 0.7f;
            else if (life > 0.88f) a = (1f - life) / 0.12f * 0.7f;
            else                   a = 0.9f;
            if (a <= 0.01f) return;
            var col = new Vector4(c.X, c.Y, c.Z, a);
            uint uHalo = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, a * 0.35f));
            uint uCore = ImGui.ColorConvertFloat4ToU32(col);
            dl.AddCircleFilled(new Vector2(x, y), size + 2f, uHalo);
            dl.AddCircleFilled(new Vector2(x, y), size,      uCore);
        }
        Mote(0.04f, 22f, -2f,  10f, 1.8f, accent);
        Mote(0.11f, 28f, -9f,  12f, 2.2f, expression);
        Mote(0.18f, 24f, -15f, 10f, 2.0f, macro);
        Mote(0.26f, 31f, -4f,  14f, 1.6f, accent);
        Mote(0.34f, 26f, -18f, 11f, 2.6f, expression);
        Mote(0.42f, 23f, -11f, 12f,  1.8f, macro);
        Mote(0.49f, 29f, -6f,  10f, 1.6f, accent);
        Mote(0.57f, 25f, -14f, 13f, 2.2f, macro);
        Mote(0.65f, 30f, -20f, 11f, 1.8f, expression);
        Mote(0.72f, 27f, -3f,  12f, 1.6f, accent);
        Mote(0.80f, 32f, -16f, 10f, 2.2f, macro);
        Mote(0.88f, 24f, -8f,  13f, 1.8f, expression);
        Mote(0.94f, 34f, -10f, 11f, 2.4f, accent);
        Mote(0.48f, 36f, -14f, 12f, 1.8f, expression);
    }

    // playing-state card effects, time-driven via ImGui.GetTime()
    public static float BeatHz => Plugin.Instance?.CurrentBgmHz ?? 2f;

    public static bool IsBeatMatched
    {
        get
        {
            var p = Plugin.Instance;
            if (p == null) return false;
            bool performing = !string.IsNullOrEmpty(p.Configuration?.ActivePresetId)
                           || !string.IsNullOrEmpty(p.ActiveRoutineName);
            return performing && p.BgmTrackerService?.CurrentBpm != null;
        }
    }

    public static float BeatPhase01()
    {
        float t = (float)ImGui.GetTime();
        return 0.5f + 0.5f * MathF.Sin(t * 2f * MathF.PI * BeatHz);
    }
    public static float BeatKickScale(float amp = 0.06f)
    {
        float t = (float)ImGui.GetTime();
        float beatFrac = (float)((t * BeatHz) % 1.0);
        float k = beatFrac < 0.6f
            ? MathF.Pow(1f - beatFrac / 0.6f, 1.2f)
            : 0f;
        return 1f + amp * k;
    }

    // 3.5s breath, decoupled from BeatPhase01 (full-card 2 Hz reads as flashing)
    public static void DrawPlayingCardPulse(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, Vector4 accent)
    {
        float t = (float)ImGui.GetTime();
        float p = 0.5f - 0.5f * MathF.Cos(t * MathF.Tau / 3.5f);
        float alpha = 0.025f + p * 0.035f;
        dl.AddRectFilled(cardMin, cardMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, alpha)));
    }

    public static void DrawPlayingStripeGlow(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, Vector4 accent, float scale)
    {
        float p = BeatPhase01();
        float coreA = 0.22f + p * 0.22f;
        dl.AddRectFilled(
            cardMin, new Vector2(cardMin.X + 3f * scale, cardMax.Y),
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, coreA)));
        float glowA = 0.10f + p * 0.12f;
        uint uG     = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, glowA));
        uint uClear = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(cardMin.X + 3f * scale, cardMin.Y),
            new Vector2(cardMin.X + 14f * scale, cardMax.Y),
            uG, uClear, uClear, uG);
    }

    public static void DrawBeatRipple(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 center, Vector4 accent, float baseRadius)
    {
        float t = (float)ImGui.GetTime();
        for (int i = 0; i < 2; i++)
        {
            float cycleLen = 1.0f;
            float phase = i * 0.5f;
            float life = (float)(((t + phase) % cycleLen) / cycleLen);
            float r = baseRadius * (0.55f + 1.75f * life);
            float alpha = 0.75f * (1f - life);
            if (alpha <= 0.02f) continue;
            dl.AddCircle(center, r,
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, alpha)),
                40, 1.5f);
        }
    }

    public static void DrawCardConfetti(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, float scale)
    {
        const int count = 12;
        Span<(float xFrac, float dx, float delay, int palIdx)> parts = stackalloc (float, float, float, int)[count]
        {
            (0.06f,  10f, -0.10f, 0),
            (0.15f,  -8f, -1.20f, 1),
            (0.24f,  14f, -0.60f, 5),
            (0.34f,  -4f, -2.10f, 2),
            (0.42f,  12f, -1.50f, 4),
            (0.52f, -14f, -0.30f, 6),
            (0.60f,   6f, -2.60f, 3),
            (0.68f,  -6f, -1.80f, 0),
            (0.78f,  10f, -0.80f, 1),
            (0.86f, -10f, -2.30f, 5),
            (0.92f,   4f, -1.00f, 2),
            (0.72f,  -4f, -0.50f, 4),
        };
        Span<Vector4> palette = stackalloc Vector4[8];
        palette[0] = new Vector4(0.38f, 0.72f, 1.00f, 1f);
        palette[1] = new Vector4(0.72f, 0.52f, 1.00f, 1f);
        palette[2] = new Vector4(1.00f, 0.42f, 0.70f, 1f);
        palette[3] = new Vector4(1.00f, 0.62f, 0.25f, 1f);
        palette[4] = new Vector4(0.45f, 0.92f, 0.55f, 1f);
        palette[5] = new Vector4(0.28f, 0.88f, 0.92f, 1f);
        palette[6] = new Vector4(1.00f, 0.82f, 0.30f, 1f);
        palette[7] = new Vector4(1.00f, 0.50f, 0.46f, 1f);

        float cardW = cardMax.X - cardMin.X;
        float cardH = cardMax.Y - cardMin.Y;
        float startY = cardMin.Y + cardH * 0.62f;
        float travel = 46f * scale;
        float t = (float)ImGui.GetTime();
        const float dur = 3.2f;

        foreach (var p in parts)
        {
            float life = ((t + p.delay) % dur) / dur;
            if (life < 0f) life += 1f;
            float alpha;
            if (life < 0.15f) alpha = (life / 0.15f) * 0.95f;
            else              alpha = 0.95f * (1f - (life - 0.15f) / 0.85f);
            if (alpha < 0.04f) continue;

            float x = cardMin.X + cardW * p.xFrac + p.dx * scale * life;
            float y = startY - travel * life;
            if (x < cardMin.X - 2f || x > cardMax.X + 2f) continue;

            var c = palette[p.palIdx];
            var coreSize = 1.5f * scale;
            var haloSize = 3.0f * scale;
            dl.AddCircleFilled(new Vector2(x, y), haloSize,
                ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, alpha * 0.45f)),
                12);
            dl.AddCircleFilled(new Vector2(x, y), coreSize,
                ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, alpha)),
                12);
        }
    }

    public static void DrawStripeScan(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, Vector4 accent, float scale)
    {
        float stripeW = 3f * scale;
        float bleedW = 18f * scale;
        float cardH = cardMax.Y - cardMin.Y;
        float t = (float)ImGui.GetTime();
        const float dur = 2.4f;
        float phase = (t % dur) / dur;
        float bandH = cardH * 0.40f;
        float bandTop = cardMin.Y - bandH + (cardH + bandH) * phase;
        float bandBot = bandTop + bandH;
        float env = phase < 0.10f ? phase / 0.10f
                  : phase > 0.90f ? (1f - phase) / 0.10f
                  : 1f;
        env = Math.Clamp(env, 0f, 1f);
        if (env < 0.02f) return;

        float mid = (bandTop + bandBot) * 0.5f;

        uint clear  = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0f));
        uint core   = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.95f * env));
        dl.AddRectFilledMultiColor(
            new Vector2(cardMin.X, bandTop), new Vector2(cardMin.X + stripeW, mid),
            clear, clear, core, core);
        dl.AddRectFilledMultiColor(
            new Vector2(cardMin.X, mid), new Vector2(cardMin.X + stripeW, bandBot),
            core, core, clear, clear);

        uint bleedIn  = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0.55f * env));
        uint bleedOut = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(cardMin.X + stripeW, bandTop),
            new Vector2(cardMin.X + stripeW + bleedW, mid),
            clear, clear, bleedOut, bleedIn);
        dl.AddRectFilledMultiColor(
            new Vector2(cardMin.X + stripeW, mid),
            new Vector2(cardMin.X + stripeW + bleedW, bandBot),
            bleedIn, bleedOut, clear, clear);
    }

    public static void DrawIconHaloRings(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 iconCenter, float iconHalfSize, Vector4 accent, float scale)
    {
        float t = (float)ImGui.GetTime();
        float breath = 0.5f + 0.5f * MathF.Sin(t * MathF.Tau / 1.0f);
        for (int i = 0; i < 3; i++)
        {
            float outset = (1.5f + i * 2.5f) * scale;
            float r = iconHalfSize + outset;
            float alpha = (0.40f - i * 0.11f) * (0.55f + 0.45f * breath);
            if (alpha < 0.02f) continue;
            dl.AddCircle(iconCenter, r,
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, alpha)),
                32, 1f);
        }
    }

    public static void DrawConicRainbowRing(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, float scale)
    {
        Span<Vector4> palette = stackalloc Vector4[8];
        palette[0] = new Vector4(0.38f, 0.72f, 1.00f, 1f);
        palette[1] = new Vector4(0.72f, 0.52f, 1.00f, 1f);
        palette[2] = new Vector4(1.00f, 0.42f, 0.70f, 1f);
        palette[3] = new Vector4(1.00f, 0.62f, 0.25f, 1f);
        palette[4] = new Vector4(0.45f, 0.92f, 0.55f, 1f);
        palette[5] = new Vector4(0.28f, 0.88f, 0.92f, 1f);
        palette[6] = new Vector4(1.00f, 0.82f, 0.30f, 1f);
        palette[7] = new Vector4(1.00f, 0.50f, 0.46f, 1f);

        float t = (float)ImGui.GetTime();
        float rot = (t / 8f) % 1f;
        float w = cardMax.X - cardMin.X;
        float h = cardMax.Y - cardMin.Y;
        float perim = 2f * (w + h);

        // unrolled per side: a segment crossing a corner draws diagonally across the interior.
        // can't use a local fn: Span<Vector4> is a ref struct and can't be captured.
        {
            int n = Math.Max(6, (int)MathF.Ceiling(w / (8f * scale)));
            float dx = w / n;
            for (int i = 0; i < n; i++)
            {
                float dAlong = ((i + 0.5f) / n) * w;
                float paletteF = ((dAlong / perim + rot) % 1f) * 8f;
                int pi = (int)MathF.Floor(paletteF) % 8, pj = (pi + 1) % 8;
                float lerpT = paletteF - MathF.Floor(paletteF);
                var c = new Vector4(
                    palette[pi].X + (palette[pj].X - palette[pi].X) * lerpT,
                    palette[pi].Y + (palette[pj].Y - palette[pi].Y) * lerpT,
                    palette[pi].Z + (palette[pj].Z - palette[pi].Z) * lerpT, 0.55f);
                dl.AddLine(
                    new Vector2(cardMin.X + dx * i,       cardMin.Y),
                    new Vector2(cardMin.X + dx * (i + 1), cardMin.Y),
                    ImGui.ColorConvertFloat4ToU32(c), 1f);
            }
        }
        {
            int n = Math.Max(6, (int)MathF.Ceiling(h / (8f * scale)));
            float dy = h / n;
            for (int i = 0; i < n; i++)
            {
                float dAlong = w + ((i + 0.5f) / n) * h;
                float paletteF = ((dAlong / perim + rot) % 1f) * 8f;
                int pi = (int)MathF.Floor(paletteF) % 8, pj = (pi + 1) % 8;
                float lerpT = paletteF - MathF.Floor(paletteF);
                var c = new Vector4(
                    palette[pi].X + (palette[pj].X - palette[pi].X) * lerpT,
                    palette[pi].Y + (palette[pj].Y - palette[pi].Y) * lerpT,
                    palette[pi].Z + (palette[pj].Z - palette[pi].Z) * lerpT, 0.55f);
                dl.AddLine(
                    new Vector2(cardMax.X, cardMin.Y + dy * i),
                    new Vector2(cardMax.X, cardMin.Y + dy * (i + 1)),
                    ImGui.ColorConvertFloat4ToU32(c), 1f);
            }
        }
        {
            int n = Math.Max(6, (int)MathF.Ceiling(w / (8f * scale)));
            float dx = w / n;
            for (int i = 0; i < n; i++)
            {
                float dAlong = w + h + ((i + 0.5f) / n) * w;
                float paletteF = ((dAlong / perim + rot) % 1f) * 8f;
                int pi = (int)MathF.Floor(paletteF) % 8, pj = (pi + 1) % 8;
                float lerpT = paletteF - MathF.Floor(paletteF);
                var c = new Vector4(
                    palette[pi].X + (palette[pj].X - palette[pi].X) * lerpT,
                    palette[pi].Y + (palette[pj].Y - palette[pi].Y) * lerpT,
                    palette[pi].Z + (palette[pj].Z - palette[pi].Z) * lerpT, 0.55f);
                dl.AddLine(
                    new Vector2(cardMax.X - dx * i,       cardMax.Y),
                    new Vector2(cardMax.X - dx * (i + 1), cardMax.Y),
                    ImGui.ColorConvertFloat4ToU32(c), 1f);
            }
        }
        {
            int n = Math.Max(6, (int)MathF.Ceiling(h / (8f * scale)));
            float dy = h / n;
            for (int i = 0; i < n; i++)
            {
                float dAlong = 2f * w + h + ((i + 0.5f) / n) * h;
                float paletteF = ((dAlong / perim + rot) % 1f) * 8f;
                int pi = (int)MathF.Floor(paletteF) % 8, pj = (pi + 1) % 8;
                float lerpT = paletteF - MathF.Floor(paletteF);
                var c = new Vector4(
                    palette[pi].X + (palette[pj].X - palette[pi].X) * lerpT,
                    palette[pi].Y + (palette[pj].Y - palette[pi].Y) * lerpT,
                    palette[pi].Z + (palette[pj].Z - palette[pi].Z) * lerpT, 0.55f);
                dl.AddLine(
                    new Vector2(cardMin.X, cardMax.Y - dy * i),
                    new Vector2(cardMin.X, cardMax.Y - dy * (i + 1)),
                    ImGui.ColorConvertFloat4ToU32(c), 1f);
            }
        }
    }

    public static void DrawFolderEchoGlow(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 headerMin, Vector2 headerMax, Vector4 folderColor, float scale)
    {
        float t = (float)ImGui.GetTime();
        float breath = 0.5f - 0.5f * MathF.Cos(t * MathF.Tau / 3f);
        float accentBarW = 4f * scale;
        float bleedW = 48f * scale;
        var bright = new Vector4(folderColor.X, folderColor.Y, folderColor.Z,
            0.22f + 0.18f * breath);
        var clear  = new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 0f);
        uint uB = ImGui.ColorConvertFloat4ToU32(bright);
        uint uC = ImGui.ColorConvertFloat4ToU32(clear);
        dl.AddRectFilledMultiColor(
            new Vector2(headerMin.X + accentBarW,           headerMin.Y),
            new Vector2(headerMin.X + accentBarW + bleedW,  headerMax.Y),
            uB, uC, uC, uB);

        float leftBleed = 6f * scale;
        dl.AddRectFilledMultiColor(
            new Vector2(headerMin.X - leftBleed, headerMin.Y),
            new Vector2(headerMin.X,             headerMax.Y),
            uC, uB, uB, uC);
    }

    public static void DrawWindowEqEdges(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 winMin, Vector2 winMax, Vector4 accent, float scale)
    {
        float colThick = 3f * scale;
        float colGap = 3f * scale;
        float maxRowW = 19f * scale;
        float halo = 1.5f * scale;

        float t = (float)ImGui.GetTime();

        float h = winMax.Y - winMin.Y;
        int cols = (int)MathF.Floor((h + colGap) / (colThick + colGap));
        if (cols < 10) cols = 10;
        float totalH = cols * colThick + (cols - 1) * colGap;
        float startY = winMin.Y + (h - totalH) * 0.5f;

        for (int side = 0; side < 2; side++)
        {
            float edgeX = side == 0 ? winMin.X : winMax.X;
            float outwardSign = side == 0 ? -1f : 1f;
            int seedSalt = side == 0 ? 1000 : 2000;

            for (int i = 0; i < cols; i++)
            {
                uint hA = (uint)((i + seedSalt) * 2654435761u);
                uint hB = (uint)((i + seedSalt) * 40503u + 17u);
                float dur = 1.18f + ((hA >> 8) & 0xFFFF) / 65535f * 0.28f;
                float phaseOff = ((hB >> 8) & 0xFFFF) / 65535f * 1.1f;
                float cycle = ((t + phaseOff) % dur) / dur;
                float ease = 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
                float colScale = 0.22f + 0.78f * ease;

                float yFrac = cols <= 1 ? 0.5f : (float)i / (cols - 1);
                float envelope = MathF.Pow(MathF.Sin(yFrac * MathF.PI), 1.4f);
                if (envelope < 0.02f) continue;

                var c = accent;
                float y = startY + i * (colThick + colGap);

                float barLen = maxRowW * colScale * envelope;
                if (barLen < 2f) continue;
                float edgeAlpha = 1f;
                float innerX = edgeX;
                float outerX = edgeX + outwardSign * barLen;
                float x0 = MathF.Min(innerX, outerX);
                float x1 = MathF.Max(innerX, outerX);
                float y0 = y - colThick * 0.5f;
                float y1 = y + colThick * 0.5f;

                uint haloCol = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(c.X, c.Y, c.Z, edgeAlpha * 0.30f));
                uint coreCol = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(c.X, c.Y, c.Z, edgeAlpha * 0.85f));
                dl.AddRectFilled(
                    new Vector2(x0 - halo, y0 - halo),
                    new Vector2(x1 + halo, y1 + halo),
                    haloCol);
                dl.AddRectFilled(
                    new Vector2(x0, y0), new Vector2(x1, y1),
                    coreCol);
            }
        }
    }

    public static void DrawRainbowBars(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 areaMin, Vector2 areaMax, float scale,
        float opacity = 1f, float colW = 3f, float colGap = 2f,
        bool leftAlign = false, float peakFrac = 0.5f)
    {
        float cW = colW * scale;
        float cG = colGap * scale;
        float areaW = areaMax.X - areaMin.X;
        float areaH = areaMax.Y - areaMin.Y;
        if (areaW < 20f || areaH < 4f) return;
        int cols = (int)MathF.Floor((areaW + cG) / (cW + cG));
        if (cols < 6) cols = 6;
        float totalW = cols * cW + (cols - 1) * cG;
        float startX = leftAlign
            ? areaMin.X
            : areaMin.X + (areaW - totalW) * 0.5f;
        float bottomY = areaMax.Y;
        float maxH = areaH;

        Span<Vector4> palette = stackalloc Vector4[8];
        palette[0] = new Vector4(0.38f, 0.72f, 1.00f, 1f);
        palette[1] = new Vector4(0.72f, 0.52f, 1.00f, 1f);
        palette[2] = new Vector4(1.00f, 0.42f, 0.70f, 1f);
        palette[3] = new Vector4(0.28f, 0.88f, 0.92f, 1f);
        palette[4] = new Vector4(0.45f, 0.92f, 0.55f, 1f);
        palette[5] = new Vector4(1.00f, 0.82f, 0.30f, 1f);
        palette[6] = new Vector4(1.00f, 0.62f, 0.25f, 1f);
        palette[7] = new Vector4(1.00f, 0.50f, 0.45f, 1f);

        float t = (float)ImGui.GetTime();
        float paletteSlide = (t / 8f) % 1f;

        float peakClamp = MathF.Max(0.05f, MathF.Min(0.95f, peakFrac));
        for (int i = 0; i < cols; i++)
        {
            float envFrac = (i + 0.5f) / cols;
            float bell = envFrac < peakClamp
                ? MathF.Sin(envFrac / peakClamp * 0.5f * MathF.PI)
                : MathF.Sin((1f - envFrac) / (1f - peakClamp) * 0.5f * MathF.PI);
            float peak = MathF.Pow(bell, 3f);

            uint hA = (uint)(i * 2654435761u);
            uint hB = (uint)(i * 40503u + 17u);
            // beat-matched: tie period to beat + add kick. idle: original 1.60-2.00s.
            float dur;
            float kickAmp;
            if (IsBeatMatched)
            {
                float beatPeriod = 1f / MathF.Max(0.5f, BeatHz);
                dur = beatPeriod * (1.0f + ((hA >> 8) & 0xFFFF) / 65535f * 1.0f);
                kickAmp = 0.15f;
            }
            else
            {
                dur = 1.60f + ((hA >> 8) & 0xFFFF) / 65535f * 0.40f;
                kickAmp = 0f;
            }
            float phaseOff = ((hB >> 8) & 0xFFFF) / 65535f * 2.0f;
            float cycle = ((t + phaseOff) % dur) / dur;
            float ease = 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
            float bounce = 0.30f + 0.70f * ease;
            float beatKick = 1f + kickAmp * (BeatKickScale(1f) - 1f);
            float h = maxH * peak * bounce * beatKick;
            if (h < 2f) continue;

            float palIdx = ((i / (float)cols + paletteSlide) % 1f) * 8f;
            int p0 = (int)MathF.Floor(palIdx) % 8;
            int p1 = (p0 + 1) % 8;
            float lerp = palIdx - MathF.Floor(palIdx);
            var a0 = palette[p0];
            var a1 = palette[p1];
            var c = new Vector4(
                a0.X + (a1.X - a0.X) * lerp,
                a0.Y + (a1.Y - a0.Y) * lerp,
                a0.Z + (a1.Z - a0.Z) * lerp,
                1f);

            float x = startX + i * (cW + cG);
            uint haloCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(c.X, c.Y, c.Z, 0.18f * opacity));
            uint coreCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(c.X, c.Y, c.Z, 0.55f * opacity));
            dl.AddRectFilled(
                new Vector2(x - 1f, bottomY - h - 1f),
                new Vector2(x + cW + 1f, bottomY + 1f),
                haloCol);
            dl.AddRectFilled(
                new Vector2(x, bottomY - h),
                new Vector2(x + cW, bottomY),
                coreCol);
        }
    }

    public static void DrawCursorGlow(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 cardMin, Vector2 cardMax, Vector4 accent, float scale,
        float alphaMul = 1f, Vector2? centerOverride = null)
    {
        if (alphaMul <= 0.01f) return;
        Vector2 center;
        if (centerOverride.HasValue)
        {
            center = centerOverride.Value;
        }
        else
        {
            center = ImGui.GetMousePos();
            if (center.X < cardMin.X || center.X > cardMax.X) return;
            if (center.Y < cardMin.Y || center.Y > cardMax.Y) return;
        }

        const int layers = 14;
        float maxR = 160f * scale;
        float peakAlpha = 0.085f * alphaMul;

        for (int i = 0; i < layers; i++)
        {
            float t = (float)i / (layers - 1);
            float r = maxR * (1f - 0.88f * t);
            float a = peakAlpha * t * t;
            if (a < 0.003f) continue;
            dl.AddCircleFilled(center, r,
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, a)),
                48);
        }
    }

    public static Vector2 IdleGlowCenter(Vector2 min, Vector2 max, float seed = 0f)
    {
        float t = (float)ImGui.GetTime() + seed;
        var cc = new Vector2((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f);
        float ampX = (max.X - min.X) * 0.32f;
        float ampY = (max.Y - min.Y) * 0.30f;
        return new Vector2(
            cc.X + ampX * MathF.Sin(t * 0.31f),
            cc.Y + ampY * MathF.Cos(t * 0.44f));
    }

    public static void DrawNameUnderline(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        float startX, float endX, float y, Vector4 accent, float scale,
        float alphaMul = 1f)
    {
        if (alphaMul <= 0.02f) return;
        if (endX - startX < 2f) return;
        uint core = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0.85f * alphaMul));
        uint halo = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0.30f * alphaMul));
        dl.AddRectFilled(
            new Vector2(startX, y + 1f),
            new Vector2(endX,   y + 3f),
            halo);
        dl.AddLine(new Vector2(startX, y), new Vector2(endX, y), core, 1f);
    }

    public static void DrawPlayButtonBloom(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 btnMin, Vector2 btnMax, float scale,
        Vector4? tintOverride = null)
    {
        float t = (float)ImGui.GetTime();
        float breath = 0.5f - 0.5f * MathF.Cos(t * MathF.Tau / 3.2f);
        float alpha = 0.28f * breath;
        if (alpha < 0.02f) return;
        var baseTint = tintOverride ?? new Vector4(0.45f, 0.92f, 0.55f, 1f);
        var core = new Vector4(baseTint.X, baseTint.Y, baseTint.Z, alpha);
        var soft = new Vector4(baseTint.X, baseTint.Y, baseTint.Z, alpha * 0.45f);
        uint cCore = ImGui.ColorConvertFloat4ToU32(core);
        uint cSoft = ImGui.ColorConvertFloat4ToU32(soft);
        float o1 = 2f * scale;
        float o2 = 6f * scale;
        dl.AddRect(
            new Vector2(btnMin.X - o2, btnMin.Y - o2),
            new Vector2(btnMax.X + o2, btnMax.Y + o2),
            cSoft, 0f, 0, 1f);
        dl.AddRect(
            new Vector2(btnMin.X - o1, btnMin.Y - o1),
            new Vector2(btnMax.X + o1, btnMax.Y + o1),
            cCore, 0f, 0, 1f);
    }

    public static float PlayKickScale(float elapsedSec)
    {
        const float dur = 0.9f;
        if (elapsedSec < 0f || elapsedSec > dur) return 1f;
        float c = elapsedSec / dur;
        static float Smoothstep(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * (3f - 2f * t); }
        if (c < 0.22f) return 1.00f + (0.84f - 1.00f) * Smoothstep(c / 0.22f);
        if (c < 0.55f) return 0.84f + (1.18f - 0.84f) * Smoothstep((c - 0.22f) / 0.33f);
        if (c < 0.85f) return 1.18f + (1.00f - 1.18f) * Smoothstep((c - 0.55f) / 0.30f);
        return 1.00f;
    }

    public static bool DrawPlayButton(
        string id, Vector2 size, float kickScale, float scale,
        string label = "PLAY",
        Vector4? restCol = null, Vector4? hoverCol = null,
        Vector4? heldCol = null, Vector4? borderCol = null,
        Vector4? textColor = null)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();

        var center = new Vector2(pos.X + size.X * 0.5f, pos.Y + size.Y * 0.5f);
        float hw = size.X * 0.5f * kickScale;
        float hh = size.Y * 0.5f * kickScale;
        var rectMin = new Vector2(center.X - hw, center.Y - hh);
        var rectMax = new Vector2(center.X + hw, center.Y + hh);

        var defRest  = new Vector4(0.45f, 0.92f, 0.55f, 1f);
        var defHover = new Vector4(0.55f, 1.00f, 0.65f, 1f);
        var defHeld  = new Vector4(0.32f, 0.76f, 0.42f, 1f);
        var rest    = restCol  ?? defRest;
        var hoverC  = hoverCol ?? defHover;
        var heldC   = heldCol  ?? defHeld;
        Vector4 fill = held ? heldC : (hovered ? hoverC : rest);
        var border = borderCol ?? heldC;
        var textCol = textColor ?? new Vector4(0.04f, 0.08f, 0.05f, 1f);

        var top = new Vector4(
            MathF.Min(1f, fill.X * 1.10f + 0.04f),
            MathF.Min(1f, fill.Y * 1.10f + 0.04f),
            MathF.Min(1f, fill.Z * 1.10f + 0.04f), 1f);
        var bot = new Vector4(fill.X * 0.82f, fill.Y * 0.82f, fill.Z * 0.82f, 1f);
        uint uTop = ImGui.ColorConvertFloat4ToU32(top);
        uint uBot = ImGui.ColorConvertFloat4ToU32(bot);
        dl.AddRectFilledMultiColor(rectMin, rectMax, uTop, uTop, uBot, uBot);
        dl.AddRect(rectMin, rectMax, ImGui.ColorConvertFloat4ToU32(border), 0f, 0, 1f);

        if (hovered)
        {
            float t = (float)ImGui.GetTime();
            const float sheenDur = 2.0f;
            float cyc = (t % sheenDur) / sheenDur;
            float rectW = rectMax.X - rectMin.X;
            float bandW = rectW * 0.70f;
            float sweepL = rectMin.X - bandW + (rectW + bandW) * cyc;
            float sweepR = sweepL + bandW;
            float midX = sweepL + bandW * 0.5f;
            dl.PushClipRect(rectMin, rectMax, true);
            uint clear  = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0f));
            uint bright = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.55f));
            dl.AddRectFilledMultiColor(
                new Vector2(sweepL, rectMin.Y), new Vector2(midX, rectMax.Y),
                clear, bright, bright, clear);
            dl.AddRectFilledMultiColor(
                new Vector2(midX, rectMin.Y), new Vector2(sweepR, rectMax.Y),
                bright, clear, clear, bright);
            dl.PopClipRect();
        }

        var lblSz = ImGui.CalcTextSize(label);
        var lblPos = new Vector2(center.X - lblSz.X * 0.5f,
                                 center.Y - lblSz.Y * 0.5f + (held ? 1f : 0f));
        dl.AddText(lblPos, ImGui.ColorConvertFloat4ToU32(textCol), label);

        return clicked;
    }

    public static bool DrawStopButton(
        string id, Vector2 size, float kickScale, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();

        var center = new Vector2(pos.X + size.X * 0.5f, pos.Y + size.Y * 0.5f);
        float hw = size.X * 0.5f * kickScale;
        float hh = size.Y * 0.5f * kickScale;
        var rectMin = new Vector2(center.X - hw, center.Y - hh);
        var rectMax = new Vector2(center.X + hw, center.Y + hh);

        var coral = new Vector4(0.83f, 0.53f, 0.47f, 1f);
        if (held)
            dl.AddRectFilled(rectMin, rectMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(coral.X, coral.Y, coral.Z, 0.22f)));
        else if (hovered)
            dl.AddRectFilled(rectMin, rectMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(coral.X, coral.Y, coral.Z, 0.12f)));

        float t = (float)ImGui.GetTime();
        float breath = 0.5f + 0.5f * MathF.Sin(t * MathF.Tau / 2.4f);
        float borderA = 0.25f + breath * 0.60f;
        dl.AddRect(rectMin, rectMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(coral.X, coral.Y, coral.Z, borderA)),
            0f, 0, 1.5f);

        float haloCycle = (t % 1.6f) / 1.6f;
        float haloScale = 1f + haloCycle * 0.35f;
        float haloAlpha = 0.55f * (1f - haloCycle);
        if (haloAlpha > 0.02f)
        {
            float cx = (rectMin.X + rectMax.X) * 0.5f;
            float cy = (rectMin.Y + rectMax.Y) * 0.5f;
            float haloHW = (rectMax.X - rectMin.X) * 0.5f * haloScale;
            float haloHH = (rectMax.Y - rectMin.Y) * 0.5f * haloScale;
            dl.AddRect(
                new Vector2(cx - haloHW, cy - haloHH),
                new Vector2(cx + haloHW, cy + haloHH),
                ImGui.ColorConvertFloat4ToU32(
                    new Vector4(coral.X, coral.Y, coral.Z, haloAlpha)),
                0f, 0, 1f);
        }

        float dotPhase = 0.5f + 0.5f * MathF.Sin(t * MathF.Tau / 1.2f);
        float dotR = (2.0f + dotPhase * 1.1f) * scale;
        float dotA = 0.55f + dotPhase * 0.45f;
        var dotPos = new Vector2(rectMin.X + 9f * scale, center.Y);
        dl.AddCircleFilled(dotPos, dotR,
            ImGui.ColorConvertFloat4ToU32(new Vector4(coral.X, coral.Y, coral.Z, dotA)));

        if (hovered)
        {
            const float sheenDur = 2.0f;
            float cyc = (t % sheenDur) / sheenDur;
            float rectW = rectMax.X - rectMin.X;
            float bandW = rectW * 0.70f;
            float sweepL = rectMin.X - bandW + (rectW + bandW) * cyc;
            float sweepR = sweepL + bandW;
            float midX = sweepL + bandW * 0.5f;
            dl.PushClipRect(rectMin, rectMax, true);
            uint clear  = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0f));
            uint bright = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.40f));
            dl.AddRectFilledMultiColor(
                new Vector2(sweepL, rectMin.Y), new Vector2(midX, rectMax.Y),
                clear, bright, bright, clear);
            dl.AddRectFilledMultiColor(
                new Vector2(midX, rectMin.Y), new Vector2(sweepR, rectMax.Y),
                bright, clear, clear, bright);
            dl.PopClipRect();
        }

        var lblSz = ImGui.CalcTextSize("STOP");
        var lblPos = new Vector2(
            center.X - lblSz.X * 0.5f + 5f * scale,
            center.Y - lblSz.Y * 0.5f + (held ? 1f : 0f));
        dl.AddText(lblPos, ImGui.ColorConvertFloat4ToU32(coral), "STOP");

        return clicked;
    }

    public static void DrawCardRipple(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 clickPos, float elapsedSec, Vector4 accent, float scale)
    {
        const float dur = 1.1f;
        if (elapsedSec < 0f || elapsedSec > dur) return;
        float life = elapsedSec / dur;

        float easeT = 1f - MathF.Pow(1f - life, 2.2f);
        float r = 6f * scale + (110f * scale) * easeT;

        float envelope;
        if (life < 0.18f) envelope = life / 0.18f;
        else              envelope = 1f - (life - 0.18f) / 0.82f;
        envelope = Math.Clamp(envelope, 0f, 1f);
        float peak = 0.25f * envelope;
        if (peak < 0.01f) return;

        Span<float> bellAlphas = stackalloc float[7]
        {
            0.12f, 0.28f, 0.60f, 1.00f, 0.60f, 0.28f, 0.12f,
        };
        float radialSpread = 8f * scale;
        float stepR = radialSpread / 3f;

        for (int i = 0; i < 7; i++)
        {
            float layerR = r + (i - 3) * stepR;
            if (layerR <= 1f) continue;
            float a = peak * bellAlphas[i];
            if (a < 0.01f) continue;
            uint col = ImGui.ColorConvertFloat4ToU32(
                new Vector4(accent.X, accent.Y, accent.Z, a));
            dl.AddCircle(clickPos, layerR, col, 96, 1f);
        }
    }

    private static readonly System.Collections.Generic.Dictionary<uint, float>
        focusRingAlphas = new();

    public static void DrawFocusRingOnLastItem(Vector4 accent, float? scaleOverride = null)
    {
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        if (max.X - min.X < 2f || max.Y - min.Y < 2f) return;
        float scale = scaleOverride ?? Scale;

        uint id = unchecked((uint)(
            (min.X.GetHashCode() * 397) ^
            (min.Y.GetHashCode() * 7919) ^
            (max.X - min.X).GetHashCode()));

        focusRingAlphas.TryGetValue(id, out float current);
        float target = ImGui.IsItemActive() ? 1f : 0f;
        float dt = ImGui.GetIO().DeltaTime;
        float step = MathF.Min(1f, dt * 8f);
        float next = current + (target - current) * step;
        focusRingAlphas[id] = next;
        if (next < 0.02f) return;

        var dl = ImGui.GetWindowDrawList();
        uint ringCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0.95f * next));
        dl.AddRect(min, max, ringCol, 0f, 0, 1f);
        for (int i = 1; i <= 2; i++)
        {
            float pad = i * 1.5f * scale;
            float a = (0.22f - i * 0.08f) * next;
            if (a < 0.02f) continue;
            dl.AddRect(
                new Vector2(min.X - pad, min.Y - pad),
                new Vector2(max.X + pad, max.Y + pad),
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, a)),
                0f, 0, 1f);
        }
    }

    public static void DrawHoverSheenOnLastItem(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl, bool hovered, float alphaPeak = 0.28f)
    {
        if (!hovered) return;
        var rectMin = ImGui.GetItemRectMin();
        var rectMax = ImGui.GetItemRectMax();
        if (rectMax.X - rectMin.X < 6f) return;
        float t = (float)ImGui.GetTime();
        const float sheenDur = 1.8f;
        float cyc = (t % sheenDur) / sheenDur;
        float rectW = rectMax.X - rectMin.X;
        float bandW = rectW * 0.60f;
        float sweepL = rectMin.X - bandW + (rectW + bandW) * cyc;
        float sweepR = sweepL + bandW;
        float midX = sweepL + bandW * 0.5f;
        dl.PushClipRect(rectMin, rectMax, true);
        uint clear  = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0f));
        uint bright = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, alphaPeak));
        dl.AddRectFilledMultiColor(
            new Vector2(sweepL, rectMin.Y), new Vector2(midX, rectMax.Y),
            clear, bright, bright, clear);
        dl.AddRectFilledMultiColor(
            new Vector2(midX, rectMin.Y), new Vector2(sweepR, rectMax.Y),
            bright, clear, clear, bright);
        dl.PopClipRect();
    }

    public static void DrawPlayActivation(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 btnCenter, float elapsedSec, float scale,
        Vector4? color = null)
    {
        const float dur = 0.7f;
        if (elapsedSec < 0f || elapsedSec > dur) return;
        float life = elapsedSec / dur;
        float fade = 1f - life;
        var green = color ?? new Vector4(0.45f, 0.92f, 0.55f, 1f);

        float baseR = 10f * scale;
        float ringR = baseR * (0.5f + 5.5f * life);
        float ringAlpha = 0.9f * fade;
        dl.AddCircle(btnCenter, ringR,
            ImGui.ColorConvertFloat4ToU32(new Vector4(green.X, green.Y, green.Z, ringAlpha)),
            48, 2f * scale);

        float travel = 40f * scale * life;
        float sparkSize = MathF.Max(1f, 4f * scale * (1f - life * 0.9f));
        float sparkAlpha = fade;
        uint sparkCore = ImGui.ColorConvertFloat4ToU32(
            new Vector4(green.X, green.Y, green.Z, sparkAlpha));
        uint sparkGlow = ImGui.ColorConvertFloat4ToU32(
            new Vector4(green.X, green.Y, green.Z, sparkAlpha * 0.45f));
        Span<Vector2> dirs = stackalloc Vector2[8]
        {
            new( 1f,  0f), new( 0.7071f, -0.7071f),
            new( 0f, -1f), new(-0.7071f, -0.7071f),
            new(-1f,  0f), new(-0.7071f,  0.7071f),
            new( 0f,  1f), new( 0.7071f,  0.7071f),
        };
        foreach (var d in dirs)
        {
            var p = new Vector2(btnCenter.X + d.X * travel, btnCenter.Y + d.Y * travel);
            dl.AddCircleFilled(p, sparkSize * 1.8f, sparkGlow, 16);
            dl.AddCircleFilled(p, sparkSize, sparkCore, 16);
        }
    }

    public static void DrawNameEq(Vector2 topLeft, Vector4 accent, float height, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        float t = (float)ImGui.GetTime();
        float barW = 2f * scale;
        float gap  = 2f * scale;

        float bpmScale = IsBeatMatched ? (2f / MathF.Max(0.5f, BeatHz)) : 1f;
        Span<float> durs   = stackalloc float[5] { 0.680f * bpmScale, 0.820f * bpmScale, 0.720f * bpmScale, 0.900f * bpmScale, 0.760f * bpmScale };
        Span<float> delays = stackalloc float[5] { 0.080f * bpmScale, 0.220f * bpmScale, 0.340f * bpmScale, 0.460f * bpmScale, 0.580f * bpmScale };

        uint bodyCol = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 1f));
        uint haloCol = ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.30f));

        for (int i = 0; i < 5; i++)
        {
            float cycle = ((t + delays[i]) % durs[i]) / durs[i];
            float h = height * EqBouncePiecewise(cycle);
            float x = topLeft.X + i * (barW + gap);
            float yTop = topLeft.Y + (height - h);
            float yBot = topLeft.Y + height;

            dl.AddRectFilled(new Vector2(x - 0.5f, yTop - 0.5f),
                             new Vector2(x + barW + 0.5f, yBot + 0.5f),
                             haloCol);
            dl.AddRectFilled(new Vector2(x, yTop), new Vector2(x + barW, yBot), bodyCol);
        }
    }

    private static float EqBouncePiecewise(float c)
    {
        c = c - MathF.Floor(c);
        float a, b, segT;
        if (c < 0.25f)      { a = 0.35f; b = 1.00f; segT = c / 0.25f; }
        else if (c < 0.50f) { a = 1.00f; b = 0.55f; segT = (c - 0.25f) / 0.25f; }
        else if (c < 0.75f) { a = 0.55f; b = 0.85f; segT = (c - 0.50f) / 0.25f; }
        else                { a = 0.85f; b = 0.35f; segT = (c - 0.75f) / 0.25f; }
        float s = segT * segT * (3f - 2f * segT);
        return a + (b - a) * s;
    }

    public static float NameEqWidth(float scale) => (5f * 2f + 4f * 2f) * scale;

    // marquee text: ticker-scrolls when text exceeds maxW; tooltip fallback on hover
    private const float _MarqueeSpeed = 38f;
    private const float _MarqueeGap   = 40f;

    public static void DrawMarqueeText(
        string text, float maxWidth, Vector4 color,
        bool disabled = false, bool animate = false)
    {
        var full = ImGui.CalcTextSize(text);
        if (full.X <= maxWidth || maxWidth <= 0)
        {
            if (disabled) ImGui.TextDisabled(text);
            else          ImGui.TextColored(color, text);
            return;
        }

        if (!animate)
        {
            var ellipsis = "...";
            var ellW = ImGui.CalcTextSize(ellipsis).X;
            var truncated = text;
            for (int i = text.Length - 1; i > 0; i--)
            {
                var t = text[..i];
                if (ImGui.CalcTextSize(t).X + ellW <= maxWidth)
                {
                    truncated = t + ellipsis;
                    break;
                }
            }
            if (disabled) ImGui.TextDisabled(truncated);
            else          ImGui.TextColored(color, truncated);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
            return;
        }

        var pos = ImGui.GetCursorScreenPos();
        float cycleWidth = full.X + _MarqueeGap;
        float t0 = (float)ImGui.GetTime();
        float offset = -((t0 * _MarqueeSpeed) % cycleWidth);
        var dl = ImGui.GetWindowDrawList();
        dl.PushClipRect(pos, new Vector2(pos.X + maxWidth, pos.Y + full.Y), true);
        var drawCol = disabled
            ? new Vector4(color.X, color.Y, color.Z, 0.55f)
            : color;
        uint uc = ImGui.ColorConvertFloat4ToU32(drawCol);
        dl.AddText(new Vector2(pos.X + offset,              pos.Y), uc, text);
        dl.AddText(new Vector2(pos.X + offset + cycleWidth, pos.Y), uc, text);
        dl.PopClipRect();
        ImGui.Dummy(new Vector2(maxWidth, full.Y));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }

    public static float DrawMarqueeTextAt(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl, Vector2 pos,
        string text, float maxWidth, Vector4 color)
    {
        var full = ImGui.CalcTextSize(text);
        if (full.X <= maxWidth || maxWidth <= 0)
        {
            dl.AddText(pos, ImGui.ColorConvertFloat4ToU32(color), text);
            return full.X;
        }
        float cycleWidth = full.X + _MarqueeGap;
        float t0 = (float)ImGui.GetTime();
        float offset = -((t0 * _MarqueeSpeed) % cycleWidth);
        dl.PushClipRect(pos, new Vector2(pos.X + maxWidth, pos.Y + full.Y), true);
        uint uc = ImGui.ColorConvertFloat4ToU32(color);
        dl.AddText(new Vector2(pos.X + offset,              pos.Y), uc, text);
        dl.AddText(new Vector2(pos.X + offset + cycleWidth, pos.Y), uc, text);
        dl.PopClipRect();
        return maxWidth;
    }

    public static void DrawStopButtonDecor(Vector4 coral, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        float t = (float)ImGui.GetTime();
        float breath = 0.5f + 0.5f * MathF.Sin(t * 2f * MathF.PI / 2.4f);
        float borderA = 0.25f + breath * 0.60f;
        dl.AddRect(
            min, max,
            ImGui.ColorConvertFloat4ToU32(new Vector4(coral.X, coral.Y, coral.Z, borderA)),
            0f, 0, 1.5f);
        float dotPhase = 0.5f + 0.5f * MathF.Sin(t * 2f * MathF.PI / 1.2f);
        float dotR = (2.0f + dotPhase * 1.1f) * scale;
        float dotA = 0.55f + dotPhase * 0.45f;
        var dotPos = new Vector2(min.X + 9f * scale, (min.Y + max.Y) * 0.5f);
        dl.AddCircleFilled(dotPos, dotR,
            ImGui.ColorConvertFloat4ToU32(new Vector4(coral.X, coral.Y, coral.Z, dotA)));
    }

    public static void DrawFolderWaveform(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 headerMin, Vector2 headerMax, Vector4 folderColor, float scale,
        bool hasPlaying = false, bool bottomAnchor = false)
    {
        const int cols = 100;
        float colW = 3f * scale;
        float gap  = 2f * scale;
        float totalW = cols * colW + (cols - 1) * gap;
        float clusterRight = headerMax.X - 8f * scale;
        float clusterLeft = clusterRight - totalW;
        if (clusterLeft < headerMin.X + 4f * scale)
            clusterLeft = headerMin.X + 4f * scale;

        float centerY = (headerMin.Y + headerMax.Y) * 0.5f;
        float maxBarH = MathF.Min(12f * scale, (headerMax.Y - headerMin.Y) - 8f * scale);
        if (maxBarH < 6f * scale) maxBarH = 6f * scale;

        float t = (float)ImGui.GetTime();
        Span<Vector4> palette = stackalloc Vector4[8];
        if (hasPlaying)
        {
            palette[0] = new Vector4(0.38f, 0.72f, 1.00f, 1f);
            palette[1] = new Vector4(0.72f, 0.52f, 1.00f, 1f);
            palette[2] = new Vector4(1.00f, 0.42f, 0.70f, 1f);
            palette[3] = new Vector4(0.28f, 0.88f, 0.92f, 1f);
            palette[4] = new Vector4(0.45f, 0.92f, 0.55f, 1f);
            palette[5] = new Vector4(1.00f, 0.82f, 0.30f, 1f);
            palette[6] = new Vector4(1.00f, 0.62f, 0.25f, 1f);
            palette[7] = new Vector4(1.00f, 0.50f, 0.45f, 1f);
        }
        float idleAlpha = 0.22f;
        float idleHaloA = 0.10f;
        float activeAlpha = 0.70f;
        float activeHaloA = 0.28f;

        for (int i = 0; i < cols; i++)
        {
            float envFrac = (float)(i + 0.5) / cols;
            float peak = MathF.Pow(MathF.Sin(envFrac * MathF.PI), 3f);

            uint hA = (uint)(i * 2654435761u);
            uint hB = (uint)(i * 40503u + 17u);
            float durBase = hasPlaying ? 1.40f : 2.10f;
            float durRange = hasPlaying ? 0.30f : 0.40f;
            float dur = durBase + ((hA >> 8) & 0xFFFF) / 65535f * durRange;
            float phaseOff = ((hB >> 8) & 0xFFFF) / 65535f * 2.0f;
            float cycle = ((t + phaseOff) % dur) / dur;
            float ease = 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
            float bounce = 0.30f + 0.70f * ease;
            float barH = maxBarH * peak * bounce;
            if (barH < 2f) continue;

            Vector4 baseC;
            if (hasPlaying)
            {
                float rot = (t / 6f) % 1f;
                float pf = ((i / (float)cols + rot) % 1f) * 8f;
                int pi = (int)MathF.Floor(pf) % 8, pj = (pi + 1) % 8;
                float lt = pf - MathF.Floor(pf);
                baseC = new Vector4(
                    palette[pi].X + (palette[pj].X - palette[pi].X) * lt,
                    palette[pi].Y + (palette[pj].Y - palette[pi].Y) * lt,
                    palette[pi].Z + (palette[pj].Z - palette[pi].Z) * lt,
                    1f);
            }
            else
            {
                baseC = folderColor;
            }

            float barA = hasPlaying ? activeAlpha : idleAlpha;
            float haloA = hasPlaying ? activeHaloA : idleHaloA;
            uint baseCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(baseC.X, baseC.Y, baseC.Z, barA));
            uint haloCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(baseC.X, baseC.Y, baseC.Z, haloA));

            float x = clusterLeft + i * (colW + gap);
            if (x + colW > headerMax.X) break;
            float yTop, yBot;
            if (bottomAnchor)
            {
                yBot = headerMax.Y;
                yTop = headerMax.Y - barH * 2f;
            }
            else
            {
                yTop = centerY - barH * 0.5f;
                yBot = centerY + barH * 0.5f;
            }
            dl.AddRectFilled(
                new Vector2(x - 1f, yTop - 1f),
                new Vector2(x + colW + 1f, yBot + 1f),
                haloCol);
            dl.AddRectFilled(new Vector2(x, yTop), new Vector2(x + colW, yBot), baseCol);
        }
    }

    public static void DrawTopBarWaveform(
        Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 areaMin, Vector2 areaMax, float scale, bool leftAlign = false, float opacity = 1f,
        float dimRectX0 = 0f, float dimRectX1 = 0f, float dimOpacity = 1f,
        bool topHalfOnly = false, float verticalScale = 1f, bool rightAlign = false)
    {
        float colW = 3f * scale;
        float gap  = 3f * scale;
        float areaW = areaMax.X - areaMin.X;
        float areaH = areaMax.Y - areaMin.Y;
        if (areaW < 60f * scale) return;

        int cols = (int)MathF.Floor((areaW * 0.8f + gap) / (colW + gap));
        if (cols < 18) cols = 18;
        if (cols > 36) cols = 36;
        float totalW = cols * colW + (cols - 1) * gap;
        if (totalW > areaW - 6f * scale) return;

        float startX = rightAlign
            ? areaMax.X - totalW
            : (leftAlign ? areaMin.X : areaMin.X + (areaW - totalW) * 0.5f);
        float centerY = (areaMin.Y + areaMax.Y) * 0.5f;

        float dotH = 3f * scale;
        float dotGap = 1f * scale * verticalScale;
        float maxStackH = MathF.Min(areaH - 4f * scale, (5f * 3f + 4f * 1f * verticalScale) * scale);

        Span<Vector4> palette = stackalloc Vector4[8];
        palette[0] = new Vector4(0.38f, 0.72f, 1.00f, 1f);
        palette[1] = new Vector4(0.72f, 0.52f, 1.00f, 1f);
        palette[2] = new Vector4(1.00f, 0.42f, 0.70f, 1f);
        palette[3] = new Vector4(0.28f, 0.88f, 0.92f, 1f);
        palette[4] = new Vector4(0.45f, 0.92f, 0.55f, 1f);
        palette[5] = new Vector4(1.00f, 0.82f, 0.30f, 1f);
        palette[6] = new Vector4(1.00f, 0.62f, 0.25f, 1f);
        palette[7] = new Vector4(1.00f, 0.50f, 0.45f, 1f);

        float t = (float)ImGui.GetTime();

        for (int i = 0; i < cols; i++)
        {
            uint hA = (uint)(i * 2654435761u);
            uint hB = (uint)(i * 40503u + 17u);
            float dur = 1.18f + ((hA >> 8) & 0xFFFF) / 65535f * 0.28f;
            float phaseOff = ((hB >> 8) & 0xFFFF) / 65535f * 1.1f;
            float cycle = ((t + phaseOff) % dur) / dur;
            float ease = 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
            float colScale = 0.22f + 0.78f * ease;

            float xFrac = cols <= 1 ? 0.5f : (float)i / (cols - 1);
            float edgeAlpha = 1f;
            if (!leftAlign && xFrac < 0.18f) edgeAlpha = xFrac / 0.18f;
            else if (!rightAlign && xFrac > 0.82f) edgeAlpha = (1f - xFrac) / 0.18f;
            edgeAlpha = Math.Clamp(edgeAlpha, 0f, 1f);
            if (edgeAlpha < 0.02f) continue;

            var c = palette[i % 8];
            float x = startX + i * (colW + gap);

            float colOpacity = opacity;
            if (dimRectX1 > dimRectX0 && x + colW > dimRectX0 && x < dimRectX1)
                colOpacity = opacity * dimOpacity;

            float scaledDotH = MathF.Max(1f, dotH * colScale);
            float scaledGap = dotGap * colScale;
            float pitch = scaledDotH + scaledGap;

            for (int j = -2; j <= 2; j++)
            {
                if (topHalfOnly && j > 0) continue;
                float y = centerY + j * pitch;

                int absJ = j < 0 ? -j : j;
                float dotAlpha = absJ == 0 ? 1.00f : (absJ == 1 ? 0.85f : 0.55f);
                dotAlpha *= edgeAlpha;
                dotAlpha *= colOpacity;

                float halo = 1.5f * scale;
                uint glowCol = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(c.X, c.Y, c.Z, dotAlpha * 0.35f));
                dl.AddRectFilled(
                    new Vector2(x - halo,         y - scaledDotH * 0.5f - halo),
                    new Vector2(x + colW + halo,  y + scaledDotH * 0.5f + halo),
                    glowCol);

                uint coreCol = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(c.X, c.Y, c.Z, dotAlpha));
                dl.AddRectFilled(
                    new Vector2(x,        y - scaledDotH * 0.5f),
                    new Vector2(x + colW, y + scaledDotH * 0.5f),
                    coreCol);
            }
        }
    }

    public static float DrawRoutineStepDots(
        Vector2 pos, int currentIndex, int totalSteps,
        Vector4 accent, float scale)
    {
        if (totalSteps <= 0) return pos.X;
        var dl = ImGui.GetWindowDrawList();
        float dotSize = 6f * scale;
        float gap = 4f * scale;
        float x = pos.X;
        float cy = pos.Y + dotSize * 0.5f;
        uint accentFilled = ImGui.ColorConvertFloat4ToU32(accent);
        uint accentDim = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0.25f));
        uint accentBorder = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0.55f));
        float t = (float)ImGui.GetTime();
        float pulse = 0.5f + 0.5f * MathF.Sin(t * 2f * MathF.PI);

        for (int i = 0; i < totalSteps; i++)
        {
            float left = x;
            float top = pos.Y;
            float right = left + dotSize;
            float bot = top + dotSize;

            if (i < currentIndex)
            {
                dl.AddRectFilled(new Vector2(left, top), new Vector2(right, bot), accentFilled);
            }
            else if (i == currentIndex)
            {
                float scaleK = 0.8f + pulse * 0.4f;
                float pad = dotSize * (1f - scaleK) * 0.5f;
                dl.AddRectFilled(new Vector2(left - 1, top - 1), new Vector2(right + 1, bot + 1),
                    accentDim);
                dl.AddRectFilled(
                    new Vector2(left + pad, top + pad),
                    new Vector2(right - pad, bot - pad),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.9f)));
            }
            else
            {
                dl.AddRectFilled(new Vector2(left, top), new Vector2(right, bot), accentDim);
                dl.AddRect(new Vector2(left, top), new Vector2(right, bot), accentBorder);
            }
            x += dotSize + gap;
        }
        return x;
    }

    public static void DrawActionbarScan(
        float scanY, float winLeft, float winRight, Vector4 accent, float period = 5f)
    {
        var dl = ImGui.GetWindowDrawList();
        float ww = winRight - winLeft;
        float t = (float)ImGui.GetTime();
        float progress = (float)((t / period) % 1.0);
        float streakW = ww * 0.38f;
        float cx = winLeft - streakW * 0.5f + progress * (ww + streakW);
        float xL = cx - streakW * 0.5f;
        float xR = cx + streakW * 0.5f;
        if (xR <= winLeft || xL >= winRight) return;
        float xLc = MathF.Max(xL, winLeft);
        float xRc = MathF.Min(xR, winRight);
        float midX = (xLc + xRc) * 0.5f;
        var scanClear  = new Vector4(accent.X, accent.Y, accent.Z, 0f);
        var scanBright = new Vector4(
            MathF.Min(accent.X * 1.25f, 1f),
            MathF.Min(accent.Y * 1.25f, 1f),
            MathF.Min(accent.Z * 1.25f, 1f),
            0.8f);
        uint uSC = ImGui.ColorConvertFloat4ToU32(scanClear);
        uint uSB = ImGui.ColorConvertFloat4ToU32(scanBright);
        dl.AddRectFilledMultiColor(
            new Vector2(xLc, scanY), new Vector2(midX, scanY + 1.5f),
            uSC, uSB, uSB, uSC);
        dl.AddRectFilledMultiColor(
            new Vector2(midX, scanY), new Vector2(xRc, scanY + 1.5f),
            uSB, uSC, uSC, uSB);
    }

    public static void DrawWindowCornerBrackets(Vector4 accent, float alpha = 0.45f)
    {
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        var scale = Scale;

        var armLen = 14f * scale;
        var inset = 6f * scale;
        uint col = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, alpha));

        float left   = winPos.X + inset;
        float top    = winPos.Y + inset;
        float right  = winPos.X + winSize.X - inset;
        float bottom = winPos.Y + winSize.Y - inset;

        // top corners hidden by Dalamud title bar; only bottom pair drawn
        dl.AddLine(new Vector2(left, bottom - armLen), new Vector2(left, bottom), col, 1f);
        dl.AddLine(new Vector2(left, bottom), new Vector2(left + armLen, bottom), col, 1f);
        dl.AddLine(new Vector2(right - armLen, bottom), new Vector2(right, bottom), col, 1f);
        dl.AddLine(new Vector2(right, bottom - armLen), new Vector2(right, bottom), col, 1f);
    }
}
