using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Encore.Styles;

namespace Encore.Windows;

public class HelpWindow : Window
{
    private int currentChapter = 0;          // 0..4
    private const int TOTAL = 5;

    // -1 sentinel = no transition in flight
    private int displayedChapter = -1;
    private int fromChapterForTransition = -1;
    private double transitionStartAt = -1;
    private const double TransitionSec = 0.34;

    // Per chapter accent color. Drives the chapter chip, numeral, and
    // section accents on that page.
    private static readonly Vector4[] ChapAccents =
    {
        new(0.38f, 0.72f, 1.00f, 1f),  // 01 sky
        new(0.72f, 0.52f, 1.00f, 1f),  // 02 violet
        new(1.00f, 0.42f, 0.70f, 1f),  // 03 rose
        new(0.45f, 0.92f, 0.55f, 1f),  // 04 green
        new(1.00f, 0.82f, 0.30f, 1f),  // 05 amber
    };

    private static readonly string[] ChapLabels =
    {
        "WELCOME", "CREATE", "PLAY", "ROUTINES", "TIPS"
    };

    private static readonly string[] ChapKickers =
    {
        "CHAPTER ONE  -  INTRODUCTION",
        "CHAPTER TWO  -  SETUP",
        "CHAPTER THREE  -  PERFORMANCE",
        "CHAPTER FOUR  -  CHOREOGRAPHY",
        "CHAPTER FIVE  -  REFERENCE",
    };

    // Chapter titles, first line / second line. Uppercased to match the
    // mockup's `.chap-title { text-transform: uppercase }`.
    private static readonly (string l1, string l2)[] ChapTitles =
    {
        ("WELCOME TO",     "ENCORE"),
        ("CREATING",       "A PRESET"),
        ("USING YOUR",     "PRESETS"),
        ("ROUTINES",       ""),
        ("TIPS &",         "COMMANDS"),
    };

    // Shared palette, matches the patch notes window.
    private static readonly Vector4 Accent       = new(0.49f, 0.65f, 0.85f, 1f);
    private static readonly Vector4 AccentBright = new(0.65f, 0.77f, 0.92f, 1f);
    private static readonly Vector4 AccentDeep   = new(0.40f, 0.53f, 0.72f, 1f);
    private static readonly Vector4 AccentDark   = new(0.05f, 0.08f, 0.13f, 1f);
    private static readonly Vector4 Text         = new(0.90f, 0.91f, 0.93f, 1f);
    private static readonly Vector4 TextDim      = new(0.66f, 0.68f, 0.73f, 1f);
    private static readonly Vector4 TextFaint    = new(0.46f, 0.48f, 0.55f, 1f);
    private static readonly Vector4 TextGhost    = new(0.30f, 0.32f, 0.38f, 1f);
    private static readonly Vector4 Border       = new(0.18f, 0.21f, 0.26f, 1f);
    private static readonly Vector4 Surface2     = new(0.075f, 0.090f, 0.120f, 1f);
    private static readonly Vector4 Surface3     = new(0.095f, 0.110f, 0.145f, 1f);
    private static readonly Vector4 WindowBgDeep = new(0.020f, 0.024f, 0.035f, 1f);
    private static readonly Vector4 Success      = new(0.45f, 0.92f, 0.55f, 1f);
    private static readonly Vector4 Warning      = new(1.00f, 0.72f, 0.30f, 1f);

    public HelpWindow() : base("Encore Guide###EncoreHelp")
    {
        SizeCondition = ImGuiCond.Always;
        // NoScrollWithMouse delegates wheel to the content child
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
              | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar
              | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.WindowScale;
        Size = new Vector2(580f * scale, 520f * scale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(580f * scale, 520f * scale),
            MaximumSize = new Vector2(580f * scale, 520f * scale),
        };
        UIStyles.PushEncoreWindow();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBgDeep);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor();
        UIStyles.PopEncoreWindow();
        base.PostDraw();
    }

    public override void Draw()
    {
        UIStyles.PushMainWindowStyle();
        UIStyles.PushEncoreContent();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

        try
        {
            var scale = UIStyles.Scale;

            DrawRibbon();
            DrawChapterNav();

            float footerH = 44f * scale;
            float contentH = ImGui.GetContentRegionAvail().Y - footerH;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, Surface2);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 6f * scale));
            if (ImGui.BeginChild("##guideContent",
                    new Vector2(ImGui.GetContentRegionAvail().X, contentH), false))
            {
                DrawRuledBackground();
                // Match the mockup's content area: 22px horizontal + 18px top
                // padding. Left pad handled by Indent; right pad is enforced
                // per draw helper via BodyAvailW() which caps the width.
                float bodyIndent = 22f * scale;
                ImGui.Indent(bodyIndent);
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - bodyIndent);

                // chapter nav's 1px bottom border + ItemSpacing.Y=6 already gives breathing room
                DrawChapterHero(currentChapter);
                switch (currentChapter)
                {
                    case 0: DrawWelcomePage(); break;
                    case 1: DrawCreatePage(); break;
                    case 2: DrawPlayPage(); break;
                    case 3: DrawRoutinesPage(); break;
                    case 4: DrawTipsPage(); break;
                }

                // Match the mockup's 20px bottom padding - without it the
                // last element on pages 2-5 sits flush against the scrollable
                // region's bottom edge.
                ImGui.Dummy(new Vector2(1, 20f * scale));

                ImGui.PopTextWrapPos();
                ImGui.Unindent(bodyIndent);
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
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

    // ==================================================================
    // RIBBON
    // ==================================================================
    private void DrawRibbon()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        float h = 30f * scale;
        var end = new Vector2(start.X + availW, start.Y + h);

        uint bgTop = ImGui.ColorConvertFloat4ToU32(new Vector4(0.047f, 0.055f, 0.071f, 1f));
        uint bgBot = ImGui.ColorConvertFloat4ToU32(new Vector4(0.024f, 0.031f, 0.043f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        // Top and bottom accent hairlines, matches the patch notes ribbon.
        uint aSolid = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f));
        uint aClear = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            start, new Vector2(start.X + availW * 0.42f, start.Y + 1f),
            aSolid, aClear, aClear, aSolid);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.58f, start.Y),
            new Vector2(end.X, start.Y + 1f),
            aClear, aSolid, aSolid, aClear);
        uint aSoft = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.28f));
        uint aSoftClr = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, end.Y - 1f),
            new Vector2(start.X + availW * 0.5f, end.Y),
            aSoftClr, aSoft, aSoft, aSoftClr);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.5f, end.Y - 1f),
            end,
            aSoft, aSoftClr, aSoftClr, aSoft);

        // Pip, three stacked dots cycling. Different from the patch notes
        // mini EQ pip. Signals "a different book from the same set."
        float padX = 14f * scale;
        float pipX = start.X + padX;
        float pipY = start.Y + (h - 13f * scale) * 0.5f;
        float t = (float)ImGui.GetTime();
        float pipDotW = 5f * scale;
        float pipDotH = 3f * scale;
        float pipDotGap = 2f * scale;
        for (int i = 0; i < 3; i++)
        {
            float phase = (t * 0.36f + i * 0.33f) % 1f;
            float alpha = phase < 0.33f ? 1f : 0.22f;
            uint dotCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(Accent.X, Accent.Y, Accent.Z, alpha));
            float dy = pipY + (2 - i) * (pipDotH + pipDotGap);
            dl.AddRectFilled(
                new Vector2(pipX, dy),
                new Vector2(pipX + pipDotW, dy + pipDotH),
                dotCol);
        }

        // Meta label: "THE PLAYBOOK  -  CHAPTER XX OF 05"
        var textH = ImGui.GetTextLineHeight();
        float textY = start.Y + (h - textH) * 0.5f;
        float metaX = pipX + pipDotW + 12f * scale;
        string label = "THE PLAYBOOK";
        string sep = "  -  ";
        string progressA = "CHAPTER ";
        string progressN = $"{currentChapter + 1:D2}";
        string progressB = " OF 05";
        var labelSz = ImGui.CalcTextSize(label);
        var sepSz = ImGui.CalcTextSize(sep);
        var progASz = ImGui.CalcTextSize(progressA);
        var progNSz = ImGui.CalcTextSize(progressN);
        float cursor = metaX;
        dl.AddText(new Vector2(cursor, textY),
            ImGui.ColorConvertFloat4ToU32(Text), label);
        cursor += labelSz.X;
        dl.AddText(new Vector2(cursor, textY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), sep);
        cursor += sepSz.X;
        dl.AddText(new Vector2(cursor, textY),
            ImGui.ColorConvertFloat4ToU32(TextDim), progressA);
        cursor += progASz.X;
        dl.AddText(new Vector2(cursor, textY),
            ImGui.ColorConvertFloat4ToU32(ChapAccents[currentChapter]), progressN);
        cursor += progNSz.X;
        dl.AddText(new Vector2(cursor, textY),
            ImGui.ColorConvertFloat4ToU32(TextDim), progressB);

        // Version tag on the right.
        string ver = $"V{Plugin.PatchNotesVersion}";
        var verSz = ImGui.CalcTextSize(ver);
        float tagPadX = 8f * scale;
        float tagPadY = 3f * scale;
        float tagRight = end.X - padX;
        float tagLeft = tagRight - verSz.X - tagPadX * 2;
        float tagTop = textY - tagPadY;
        float tagBot = textY + textH + tagPadY;
        dl.AddRectFilled(
            new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
            ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.05f)));
        dl.AddRect(
            new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
            ImGui.ColorConvertFloat4ToU32(Accent), 0f, 0, 1f);
        dl.AddText(
            new Vector2(tagLeft + tagPadX, textY),
            ImGui.ColorConvertFloat4ToU32(Accent), ver);

        ImGui.Dummy(new Vector2(1, h));
    }

    // ==================================================================
    // CHAPTER NAV. Row of 5 chips, click to jump.
    // ==================================================================
    private void DrawChapterNav()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        float navPadX = 14f * scale;
        float navPadY = 10f * scale;
        float chipGap = 6f * scale;
        float rowH = 40f * scale + navPadY * 2;
        var end = new Vector2(start.X + availW, start.Y + rowH);

        // Background fill plus bottom hairline with accent fade.
        dl.AddRectFilled(start, end,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.031f, 0.039f, 0.055f, 1f)));

        // Notebook thread. Fading horizontal line at the bottom.
        uint threadC = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.40f));
        uint threadClr = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.10f, end.Y - 1f),
            new Vector2(start.X + availW * 0.50f, end.Y),
            threadClr, threadC, threadC, threadClr);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.50f, end.Y - 1f),
            new Vector2(start.X + availW * 0.90f, end.Y),
            threadC, threadClr, threadClr, threadC);

        float chipAreaW = availW - navPadX * 2 - chipGap * 4;
        float chipW = chipAreaW / 5f;
        float chipH = 40f * scale;
        for (int i = 0; i < 5; i++)
        {
            float cx = start.X + navPadX + i * (chipW + chipGap);
            float cy = start.Y + navPadY;
            var chipMin = new Vector2(cx, cy);
            var chipMax = new Vector2(cx + chipW, cy + chipH);

            ImGui.SetCursorScreenPos(chipMin);
            if (ImGui.InvisibleButton($"##chap_{i}", new Vector2(chipW, chipH)))
                currentChapter = i;
            bool hovered = ImGui.IsItemHovered();
            bool active = i == currentChapter;
            var accent = ChapAccents[i];

            if (active)
            {
                uint bgTop = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(accent.X, accent.Y, accent.Z, 0.16f));
                uint bgBot = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(accent.X, accent.Y, accent.Z, 0.06f));
                dl.AddRectFilledMultiColor(chipMin, chipMax, bgTop, bgTop, bgBot, bgBot);
                dl.AddRect(chipMin, chipMax,
                    ImGui.ColorConvertFloat4ToU32(accent), 0f, 0, 1f);
                dl.AddRectFilled(
                    chipMin, new Vector2(chipMax.X, chipMin.Y + 2f * scale),
                    ImGui.ColorConvertFloat4ToU32(accent));
            }
            else if (hovered)
            {
                dl.AddRectFilled(chipMin, chipMax,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(accent.X, accent.Y, accent.Z, 0.06f)));
                dl.AddRect(chipMin, chipMax,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(accent.X, accent.Y, accent.Z, 0.32f)),
                    0f, 0, 1f);
            }
            else
            {
                dl.AddRect(chipMin, chipMax,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(Accent.X, Accent.Y, Accent.Z, 0.12f)),
                    0f, 0, 1f);
            }

            string numStr = $"{i + 1:D2}";
            var numSz = ImGui.CalcTextSize(numStr);
            var numCol = active ? accent : (hovered ? TextDim : TextFaint);
            dl.AddText(
                new Vector2(cx + (chipW - numSz.X) * 0.5f, cy + 6f * scale),
                ImGui.ColorConvertFloat4ToU32(numCol), numStr);

            string lbl = ChapLabels[i];
            var lblSz = ImGui.CalcTextSize(lbl);
            var lblCol = active ? Text : (hovered ? TextDim : TextGhost);
            dl.AddText(
                new Vector2(cx + (chipW - lblSz.X) * 0.5f, cy + chipH - lblSz.Y - 6f * scale),
                ImGui.ColorConvertFloat4ToU32(lblCol), lbl);
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(1, 0));
    }

    // ==================================================================
    // CONTENT. Ruled background, chapter hero, per page bodies.
    // ==================================================================
    private void DrawRuledBackground()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        float lineSpacing = 28f * scale;
        uint lineCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.020f));
        for (float y = winPos.Y; y <= winPos.Y + winSize.Y; y += lineSpacing)
        {
            dl.AddLine(
                new Vector2(winPos.X, y),
                new Vector2(winPos.X + winSize.X, y),
                lineCol, 1f);
        }
    }

    // numeral left, 18px gap, kicker + 2-line title; dashed hairline + 16px margin
    private void DrawChapterHero(int idx)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var accent = ChapAccents[idx];
        var (line1, line2) = ChapTitles[idx];
        var heroStart = ImGui.GetCursorScreenPos();

        string num = $"{idx + 1:D2}";
        var numFont = Plugin.Instance?.NumeralFont;
        var bannerFont = Plugin.Instance?.BannerFont;
        IFontHandle? useNum =
            (numFont is { Available: true }) ? numFont :
            (bannerFont is { Available: true }) ? bannerFont : null;

        Vector2 numSz;
        if (useNum != null) { using (useNum.Push()) numSz = ImGui.CalcTextSize(num); }
        else numSz = ImGui.CalcTextSize(num);

        // TitleFont (22px Unbounded-Bold) -> HeaderFont -> Dalamud default
        var titleFont = Plugin.Instance?.TitleFont;
        var headerFont = Plugin.Instance?.HeaderFont;
        IFontHandle? useTitle =
            (titleFont is { Available: true }) ? titleFont :
            (headerFont is { Available: true }) ? headerFont : null;
        string kicker = ChapKickers[idx];
        var kickerSz = ImGui.CalcTextSize(kicker);
        float headerLineH;
        if (useTitle != null) { using (useTitle.Push()) headerLineH = ImGui.GetTextLineHeight(); }
        else headerLineH = ImGui.GetTextLineHeight();
        bool hasLine2 = !string.IsNullOrEmpty(line2);

        float textPadTop = 8f * scale;
        float kickerToTitle = 2f * scale;
        float titleLineStride = headerLineH * 0.82f;
        float kickerTracking = 3.5f * scale;
        float titleTracking = 3.8f * scale;
        float titleTopY = textPadTop + kickerSz.Y + kickerToTitle;
        float titleBottomY = titleTopY + (hasLine2 ? titleLineStride : 0f) + headerLineH;
        float heroH = MathF.Max(numSz.Y, titleBottomY);

        // Numeral, top-left, flush with body indent.
        if (useNum != null)
        {
            using (useNum.Push())
                dl.AddText(new Vector2(heroStart.X, heroStart.Y),
                    ImGui.ColorConvertFloat4ToU32(accent), num);
        }
        else
        {
            dl.AddText(new Vector2(heroStart.X, heroStart.Y),
                ImGui.ColorConvertFloat4ToU32(accent), num);
        }

        // text block 18px right of numeral, 8px below hero top. Kicker faint, title text-color
        float textX = heroStart.X + numSz.X + 18f * scale;
        float textY = heroStart.Y + textPadTop;
        DrawTrackedText(dl, new Vector2(textX, textY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), kicker, kickerTracking);

        float titleY = textY + kickerSz.Y + kickerToTitle;
        uint titleCol = ImGui.ColorConvertFloat4ToU32(Text);
        if (useTitle != null)
        {
            using (useTitle.Push())
            {
                DrawTrackedText(dl, new Vector2(textX, titleY),
                    titleCol, line1, titleTracking);
                if (hasLine2)
                    DrawTrackedText(dl, new Vector2(textX, titleY + titleLineStride),
                        titleCol, line2, titleTracking);
            }
        }
        else
        {
            DrawTrackedText(dl, new Vector2(textX, titleY),
                titleCol, line1, titleTracking);
            if (hasLine2)
                DrawTrackedText(dl, new Vector2(textX, titleY + titleLineStride),
                    titleCol, line2, titleTracking);
        }

        // Dashed hairline below the hero. Mockup: `border-bottom: 1px dashed`
        // with 14px padding above it.
        float dividerPadTop = 12f * scale;
        float divY = heroStart.Y + heroH + dividerPadTop;
        uint dashCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f));
        float availW = BodyAvailW();
        for (float dx = 0; dx < availW; dx += 6f * scale)
        {
            float segW = MathF.Min(3f * scale, availW - dx);
            dl.AddLine(
                new Vector2(heroStart.X + dx, divY),
                new Vector2(heroStart.X + dx + segW, divY),
                dashCol, 1f);
        }

        // Advance the cursor past the hero with a small gap (ItemSpacing
        // adds more - together this matches the mockup's 16px margin).
        ImGui.SetCursorScreenPos(new Vector2(heroStart.X, divY));
        ImGui.Dummy(new Vector2(1, 10f * scale));
    }

    // ==================================================================
    // PAGES
    // ==================================================================
    private void DrawWelcomePage()
    {
        var scale = UIStyles.Scale;
        var accent = ChapAccents[0];

        WrappedText(
            "Encore manages your dance mod presets. Swap between modded animations " +
            "with one click or a chat command. No more fiddling with Penumbra " +
            "priorities mid-party.", TextDim);
        ImGui.Dummy(new Vector2(1, 10f * scale));

        float availW = BodyAvailW();
        float gap = 10f * scale;
        float cardW = (availW - gap) * 0.5f;

        DrawFeatureCard(accent, FontAwesomeIcon.Play, "One-click switching",
            "Between dance mods", cardW);
        ImGui.SameLine(0, gap);
        DrawFeatureCard(accent, FontAwesomeIcon.Terminal, "Custom chat commands",
            "/mydance, /vibe, anything", cardW);

        DrawFeatureCard(accent, FontAwesomeIcon.Cog, "Penumbra priority handling",
            "Automatic conflict resolution", cardW);
        ImGui.SameLine(0, gap);
        DrawFeatureCard(accent, FontAwesomeIcon.Chair, "Pose presets",
            "Idle, sit, groundsit, doze", cardW);

        // Small spacer then callout. Previous gap felt orphaned.
        ImGui.Dummy(new Vector2(1, 4f * scale));
        DrawCallout(accent, FontAwesomeIcon.ArrowRight, "Next up",
            ": how to set up your first preset. Tap Chapter 02 above, or use the arrow below.");
    }

    private void DrawCreatePage()
    {
        var scale = UIStyles.Scale;
        var accent = ChapAccents[1];

        // Intro with inline accent-colored "New Preset". Plain ImGui Text +
        // SameLine(0,0) chain; the sentence is short enough that a forced
        // wrap inside the inline segment isn't a concern at this width.
        float wrapAt = ImGui.GetCursorPosX() + BodyAvailW();
        ImGui.PushTextWrapPos(wrapAt);
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.TextUnformatted("Nine steps. Most are optional. Hit ");
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 0);
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        ImGui.TextUnformatted("New Preset");
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 0);
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.TextWrapped(" in the main window to get started.");
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        ImGui.Dummy(new Vector2(1, 10f * scale));

        DrawStep(accent, 1, "Name your preset",
            "Something memorable. \"Victory Dance,\" \"Shimmy,\" you pick.", false);
        DrawStep(accent, 2, "Set a chat command",
            "Short handle like /vdance. Can't collide with a native game command.", false);
        DrawStep(accent, 3, "Choose an icon",
            "Pick a game icon to represent the preset on its card.", true);
        DrawStep(accent, 4, "Select your mod",
            "Browse the Penumbra mod list and click the dance mod you want.", false);
        DrawStep(accent, 5, "Pick the emote",
            "If the mod replaces multiple emotes, choose the one this preset should fire.", false);
        DrawStep(accent, 6, "Don't own the emote?",
            "Tick \"I don't have this emote\" and Encore routes through a carrier bypass.", true);
        DrawStep(accent, 7, "Mod settings",
            "Pre-select option group values. Restored when you switch away.", true);
        DrawStep(accent, 8, "Modifiers",
            "Named variants that override options or swap the emote. See Chapter 03.", true);
        DrawStep(accent, 9, "Save",
            "Commit the preset. It drops into your library and the chat command fires.", false);

        ImGui.Dummy(new Vector2(1, 2f * scale));
        DrawCallout(Success, FontAwesomeIcon.Check, "Ready to roll",
            ": your preset lives on the main window now. Chapter 03 covers how to actually play it.");
    }

    private void DrawPlayPage()
    {
        var scale = UIStyles.Scale;
        var accent = ChapAccents[2];

        float availW = BodyAvailW();
        float gap = 10f * scale;
        float cardW = (availW - gap) * 0.5f;
        DrawMethodCard(accent, FontAwesomeIcon.MousePointer, "Click It",
            "Tap the Play button on any preset card in the main window.", cardW);
        ImGui.SameLine(0, gap);
        DrawMethodCard(accent, FontAwesomeIcon.Terminal, "Type It",
            "Use the preset's chat command, like /vdance or /mydance.", cardW);
        ImGui.Dummy(new Vector2(1, 14f * scale));

        DrawSubHead(accent, "What Happens on Activate");
        DrawNumberedBullet(accent, 1, "Your mod's priority is boosted in Penumbra");
        DrawNumberedBullet(accent, 2, "Conflicting emote mods are temp-disabled");
        DrawNumberedBullet(accent, 3, "The dance fires. Pose presets switch to the right index");
        DrawNumberedBullet(accent, 4, "Everything restores when you switch or reset");

        ImGui.Dummy(new Vector2(1, 10f * scale));
        DrawCallout(Warning, FontAwesomeIcon.ExclamationTriangle, "Sit & Doze",
            ": by default these need furniture nearby. Flip Allow Sit/Doze Anywhere in " +
            "settings to skip that check. Sends position data to the server.");

        ImGui.Dummy(new Vector2(1, 12f * scale));
        DrawSubHead(accent, "Modifiers  -  One Preset, Many Moods");
        WrappedText(
            "Instead of cloning a preset for each variation, add modifiers. Each one can " +
            "override mod options or swap the emote.", Text);
        ImGui.Dummy(new Vector2(1, 6f * scale));
        WrappedText(
            "Example: a jumping-jacks mod with speed options. One preset /jj with " +
            "modifiers 'slow' and 'fast'. Fire them with /jj slow or /jj fast.", TextDim);
    }

    private void DrawRoutinesPage()
    {
        var scale = UIStyles.Scale;
        var accent = ChapAccents[3];

        WrappedText(
            "A routine is a timeline of presets. Drag presets from the library, " +
            "set a duration per step, add expressions or macros, hit play. " +
            "That's a show.",
            TextDim);
        ImGui.Dummy(new Vector2(1, 10f * scale));

        // Schematic frame - draw content on channel 1 first, then background
        // on channel 0 using the measured height. Same channel-splitting
        // pattern we use for folder content containers in MainWindow.
        var dl = ImGui.GetWindowDrawList();
        var frameStart = ImGui.GetCursorScreenPos();
        float frameW = BodyAvailW();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.Dummy(new Vector2(1, 8f * scale));
        DrawStepSpine(accent);
        ImGui.Dummy(new Vector2(1, 4f * scale));

        DrawScriptRow(accent, "Duration", "per step", new[]
        {
            new InlineSeg("Two modes. ", null),
            new InlineSeg("Fixed", accent),
            new InlineSeg(" advances after a set time - good for verses and looped dances (", null),
            new InlineSeg("1:30", accent),
            new InlineSeg("). ", null),
            new InlineSeg("Until emote ends", accent),
            new InlineSeg(" works for one-shots like ", null),
            new InlineSeg("/bow", accent),
            new InlineSeg(" and ", null),
            new InlineSeg("/cheer", accent),
            new InlineSeg(". Looping dances never end - use Fixed.", null),
        }, isFirst: true);

        DrawScriptRow(accent, "Expression", "overlay", new[]
        {
            new InlineSeg("Each step can layer a face on top - ", null),
            new InlineSeg("/smile", accent),
            new InlineSeg(", ", null),
            new InlineSeg("/serious", accent),
            new InlineSeg(", ", null),
            new InlineSeg("/blush", accent),
            new InlineSeg(". Keep ", null),
            new InlineSeg("Hold", accent),
            new InlineSeg(" on; Encore re-fires every few seconds so the expression sticks the whole step.", null),
        });

        DrawScriptRow(accent, "Macro", "raw cmds", new[]
        {
            new InlineSeg("Drop in a macro step for raw FFXIV commands. ", null),
            new InlineSeg("/wait N", accent),
            new InlineSeg(" for spacing - great for VFX shows or job-skill flourishes. Defaults to ", null),
            new InlineSeg("Until macro ends", accent),
            new InlineSeg(" with an optional trailing buffer.", null),
        });

        ImGui.Dummy(new Vector2(1, 10f * scale));
        var frameEnd = ImGui.GetCursorScreenPos();
        float frameH = frameEnd.Y - frameStart.Y;

        dl.ChannelsSetCurrent(0);
        DrawSchematicFrame(frameStart, new Vector2(frameStart.X + frameW, frameStart.Y + frameH), accent);
        dl.ChannelsMerge();

        ImGui.Dummy(new Vector2(1, 8f * scale));

        // Trigger footer: mono label on the left, inline flow on the right.
        DrawTriggerRow(accent, new[]
        {
            new InlineSeg("click ", null),
            new InlineSeg("Play", accent),
            new InlineSeg("   ", null),
            new InlineSeg("›", new Vector4(accent.X, accent.Y, accent.Z, 0.55f)),
            new InlineSeg("  or chat ", null),
            new InlineSeg("/showtime", accent),
            new InlineSeg("   ", null),
            new InlineSeg("›", new Vector4(accent.X, accent.Y, accent.Z, 0.55f)),
            new InlineSeg("  ", null),
            new InlineSeg("Stop", accent),
            new InlineSeg(" / move to cancel", null),
        });
    }

    // A run of inline text with an optional color override. null color means
    // use the defaultColor passed to DrawInlineWrapped (typically body text).
    private readonly record struct InlineSeg(string Text, Vector4? Color);

    // returns the bottom Y so callers can advance the cursor precisely
    private float DrawInlineWrapped(
        IReadOnlyList<InlineSeg> segments, Vector4 defaultColor, float wrapWidth)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        float lineH = ImGui.GetTextLineHeight();
        // CSS uses line-height 1.55-1.6 for the body copy; ImGui's line height
        // is the font size, so add ~0.4x as a line gap to match the mockup.
        float lineGap = MathF.Max(3f * scale, lineH * 0.4f);
        float x = origin.X;
        float y = origin.Y;
        float wrapRight = origin.X + wrapWidth;

        foreach (var seg in segments)
        {
            var col = seg.Color ?? defaultColor;
            uint colU32 = ImGui.ColorConvertFloat4ToU32(col);
            string text = seg.Text;
            int i = 0;
            while (i < text.Length)
            {
                int wsStart = i;
                while (i < text.Length && text[i] == ' ') i++;
                string ws = text.Substring(wsStart, i - wsStart);

                int wordStart = i;
                while (i < text.Length && text[i] != ' ') i++;
                string word = text.Substring(wordStart, i - wordStart);

                if (ws.Length > 0 && x > origin.X)
                {
                    float wsW = ImGui.CalcTextSize(ws).X;
                    dl.AddText(new Vector2(x, y), colU32, ws);
                    x += wsW;
                }

                if (word.Length > 0)
                {
                    float wordW = ImGui.CalcTextSize(word).X;
                    if (x + wordW > wrapRight && x > origin.X)
                    {
                        x = origin.X;
                        y += lineH + lineGap;
                    }
                    dl.AddText(new Vector2(x, y), colU32, word);
                    x += wordW;
                }
            }
        }

        return y + lineH;
    }

    // Dotted horizontal rail used for the STEP spine. Mockup uses
    // `repeating-linear-gradient` with 4px dashes and 4px gaps.
    private static void DrawDottedRail(
        ImDrawListPtr dl, Vector2 from, Vector2 to, Vector4 color, float alpha)
    {
        var scale = UIStyles.Scale;
        var c = new Vector4(color.X, color.Y, color.Z, alpha);
        uint u32 = ImGui.ColorConvertFloat4ToU32(c);
        float dashLen = 4f * scale;
        float gapLen = 4f * scale;
        float x = from.X;
        while (x < to.X)
        {
            float endX = MathF.Min(x + dashLen, to.X);
            dl.AddLine(new Vector2(x, from.Y), new Vector2(endX, from.Y), u32, 1f);
            x += dashLen + gapLen;
        }
    }

    // The schematic spine: dotted rail - [ STEP ] - dotted rail. Anchors the
    // three facet rows as "parts of a step" within the frame.
    private void DrawStepSpine(Vector4 accent)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();

        float padX = 14f * scale;
        float rowH = 20f * scale;
        float availW = BodyAvailW() - padX * 2;
        float startX = start.X + padX;
        float centerY = start.Y + rowH * 0.5f;

        string label = "STEP";
        float tracking = 2f * scale;
        float textW = MeasureTrackedText(label, tracking);
        var textSz = ImGui.CalcTextSize(label);
        float blockPadX = 14f * scale;
        float blockPadY = 3f * scale;
        float blockW = textW + blockPadX * 2;
        float blockH = textSz.Y + blockPadY * 2;

        float blockX = startX + (availW - blockW) * 0.5f;
        float blockY = centerY - blockH * 0.5f;

        DrawDottedRail(dl,
            new Vector2(startX, centerY),
            new Vector2(blockX - 6f * scale, centerY),
            accent, 0.55f);

        var blockMin = new Vector2(blockX, blockY);
        var blockMax = new Vector2(blockX + blockW, blockY + blockH);
        dl.AddRectFilled(blockMin, blockMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.10f)));
        dl.AddRect(blockMin, blockMax,
            ImGui.ColorConvertFloat4ToU32(accent), 0f, 0, 1f);
        DrawTrackedText(dl,
            new Vector2(blockX + blockPadX, blockY + blockPadY),
            ImGui.ColorConvertFloat4ToU32(accent), label, tracking);

        DrawDottedRail(dl,
            new Vector2(blockX + blockW + 6f * scale, centerY),
            new Vector2(startX + availW, centerY),
            accent, 0.55f);

        ImGui.Dummy(new Vector2(1, rowH));
    }

    // One facet row: uppercase accent label on the left (with a dim sub-label
    // below), inline-wrapped body prose on the right. Draws a dashed top
    // separator unless it's the first row in the frame.
    private void DrawScriptRow(
        Vector4 accent, string label, string sub,
        IReadOnlyList<InlineSeg> body, bool isFirst = false)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        float padX = 14f * scale;
        float labelColW = 92f * scale;
        float gap = 18f * scale;

        if (!isFirst)
        {
            var sepStart = ImGui.GetCursorScreenPos();
            float sepLeft = sepStart.X + padX;
            float sepRight = sepStart.X + BodyAvailW() - padX;
            uint sepCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(accent.X, accent.Y, accent.Z, 0.18f));
            for (float dx = sepLeft; dx < sepRight; dx += 6f * scale)
            {
                float segW = MathF.Min(3f * scale, sepRight - dx);
                dl.AddLine(
                    new Vector2(dx, sepStart.Y),
                    new Vector2(dx + segW, sepStart.Y),
                    sepCol, 1f);
            }
            ImGui.Dummy(new Vector2(1, 8f * scale));
        }

        var rowStart = ImGui.GetCursorScreenPos();
        float labelX = rowStart.X + padX;
        float bodyX = labelX + labelColW + gap;
        float bodyW = BodyAvailW() - padX * 2 - labelColW - gap;

        // Main label (uppercase, accent, tracked).
        string mainUpper = label.ToUpperInvariant();
        float labelTracking = 2.2f * scale;
        DrawTrackedText(dl,
            new Vector2(labelX, rowStart.Y + 2f * scale),
            ImGui.ColorConvertFloat4ToU32(accent), mainUpper, labelTracking);

        // Sub-label - smaller, text-faint, tracked lightly. Position it a
        // short gap below the main label's baseline.
        float subY = rowStart.Y + 2f * scale + ImGui.GetTextLineHeight() + 3f * scale;
        float subTracking = 1.4f * scale;
        DrawTrackedText(dl,
            new Vector2(labelX, subY),
            ImGui.ColorConvertFloat4ToU32(TextFaint),
            sub.ToUpperInvariant(), subTracking);
        float subBottom = subY + ImGui.GetTextLineHeight();

        // Body prose, wrapped inline.
        ImGui.SetCursorScreenPos(new Vector2(bodyX, rowStart.Y));
        float bodyBottom = DrawInlineWrapped(body, Text, bodyW);

        float rowBottom = MathF.Max(bodyBottom, subBottom);
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowBottom));
        ImGui.Dummy(new Vector2(1, 4f * scale));
    }

    // Trigger footer - same two-column shape as a script row but without the
    // schematic frame and with a single inline flow (no sub-label).
    private void DrawTriggerRow(Vector4 accent, IReadOnlyList<InlineSeg> segments)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var rowStart = ImGui.GetCursorScreenPos();
        float labelColW = 92f * scale;
        float gap = 18f * scale;
        float labelX = rowStart.X;
        float bodyX = labelX + labelColW + gap;
        float bodyW = BodyAvailW() - labelColW - gap;

        string label = "TO TRIGGER";
        float labelTracking = 2.2f * scale;
        DrawTrackedText(dl,
            new Vector2(labelX, rowStart.Y + 3f * scale),
            ImGui.ColorConvertFloat4ToU32(accent), label, labelTracking);

        ImGui.SetCursorScreenPos(new Vector2(bodyX, rowStart.Y + 2f * scale));
        float bodyBottom = DrawInlineWrapped(segments, Text, bodyW);

        ImGui.SetCursorScreenPos(new Vector2(rowStart.X, bodyBottom));
        ImGui.Dummy(new Vector2(1, 2f * scale));
    }

    // Tinted surface + border + accent corner brackets (top-left, bottom-right).
    private void DrawSchematicFrame(Vector2 min, Vector2 max, Vector4 accent)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();

        dl.AddRectFilled(min, max,
            ImGui.ColorConvertFloat4ToU32(
                new Vector4(accent.X, accent.Y, accent.Z, 0.02f)));
        dl.AddRect(min, max,
            ImGui.ColorConvertFloat4ToU32(
                new Vector4(accent.X, accent.Y, accent.Z, 0.22f)),
            0f, 0, 1f);

        float bLen = 10f * scale;
        uint bCol = ImGui.ColorConvertFloat4ToU32(accent);
        dl.AddLine(min, new Vector2(min.X + bLen, min.Y), bCol, 1f);
        dl.AddLine(min, new Vector2(min.X, min.Y + bLen), bCol, 1f);
        dl.AddLine(max, new Vector2(max.X - bLen, max.Y), bCol, 1f);
        dl.AddLine(max, new Vector2(max.X, max.Y - bLen), bCol, 1f);
    }

    private void DrawTipsPage()
    {
        var scale = UIStyles.Scale;
        var accent = ChapAccents[4];

        DrawStreamRule(accent, 0.26f);

        // Tips: uppercase tracked labels in the left gutter.
        DrawCcRow(accent, "Align", isKbd: false, new[]
        {
            new InlineSeg("/align", accent),
            new InlineSeg(" walks you to your target's position for duo emotes.", null),
        }, isFirst: true);

        DrawCcRow(accent, "Loop", isKbd: false, new[]
        {
            new InlineSeg("/loop <emote>", accent),
            new InlineSeg(" repeats a non-looping emote until you move.", null),
        });

        DrawCcRow(accent, "Random", isKbd: false, new[]
        {
            new InlineSeg("/encore random", accent),
            new InlineSeg(" rolls a preset for you.", null),
        });

        DrawCcDivider(accent, "Commands");

        // Commands: mono chat strings as labels, descriptions in the body.
        DrawCcRow(accent, "/encore", isKbd: true, new[]
        {
            new InlineSeg("Open the main window.", null),
        }, isFirst: true);

        DrawCcRow(accent, "/encorereset", isKbd: true, new[]
        {
            new InlineSeg("Restore all mods to their original state.", null),
        });

        DrawCcRow(accent, "/align", isKbd: true, new[]
        {
            new InlineSeg("Walk to your target's position and match rotation.", null),
        });

        DrawCcRow(accent, "/loop <emote>", isKbd: true, new[]
        {
            new InlineSeg("Continuously repeat a non-looping emote.", null),
        });

        DrawCcRow(accent, "/vanilla <emote>", isKbd: true, new[]
        {
            new InlineSeg("Play the unmodded animation for one emote.", null),
        });

        DrawStreamRule(accent, 0.26f);

        ImGui.Dummy(new Vector2(1, 10f * scale));
        DrawCallout(Accent, FontAwesomeIcon.Music, "That's the tour",
            ": hit Done to close this book and start writing your own setlist.");
    }

    // Solid 1px horizontal rule spanning the body width - used to bracket
    // the telegraph stream top and bottom.
    private void DrawStreamRule(Vector4 accent, float alpha)
    {
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float w = BodyAvailW();
        uint col = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, alpha));
        dl.AddLine(
            new Vector2(start.X, start.Y),
            new Vector2(start.X + w, start.Y),
            col, 1f);
        ImGui.Dummy(new Vector2(1, 1f));
    }

    // isFirst skips the dashed top separator
    private void DrawCcRow(
        Vector4 accent, string label, bool isKbd,
        IReadOnlyList<InlineSeg> body, bool isFirst = false)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        float labelColW = 128f * scale;
        float gap = 14f * scale;
        float leftPad = 2f * scale;

        if (!isFirst)
        {
            var sepStart = ImGui.GetCursorScreenPos();
            float sepRight = sepStart.X + BodyAvailW();
            uint sepCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(accent.X, accent.Y, accent.Z, 0.12f));
            for (float dx = sepStart.X; dx < sepRight; dx += 6f * scale)
            {
                float segW = MathF.Min(3f * scale, sepRight - dx);
                dl.AddLine(
                    new Vector2(dx, sepStart.Y),
                    new Vector2(dx + segW, sepStart.Y),
                    sepCol, 1f);
            }
            ImGui.Dummy(new Vector2(1, 1f));
        }

        ImGui.Dummy(new Vector2(1, 5f * scale));
        var rowStart = ImGui.GetCursorScreenPos();
        float labelX = rowStart.X + leftPad;
        float bodyX = labelX + labelColW + gap;
        float bodyW = BodyAvailW() - labelColW - gap - leftPad;

        if (isKbd)
        {
            // Mono chat strings, minimal tracking, keep case as-is.
            DrawTrackedText(dl,
                new Vector2(labelX, rowStart.Y),
                ImGui.ColorConvertFloat4ToU32(accent), label, 0.2f * scale);
        }
        else
        {
            // Uppercase tracked label for tips.
            DrawTrackedText(dl,
                new Vector2(labelX, rowStart.Y),
                ImGui.ColorConvertFloat4ToU32(accent),
                label.ToUpperInvariant(), 2.2f * scale);
        }

        ImGui.SetCursorScreenPos(new Vector2(bodyX, rowStart.Y));
        float bodyBottom = DrawInlineWrapped(body, Text, bodyW);

        float labelBottom = rowStart.Y + ImGui.GetTextLineHeight();
        float rowBottom = MathF.Max(bodyBottom, labelBottom);
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowBottom));
        ImGui.Dummy(new Vector2(1, 5f * scale));
    }

    // dashed top rule + accent square + tracked label
    private void DrawCcDivider(Vector4 accent, string label)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();

        // Dashed top rule at stronger alpha than row separators so the
        // divider reads as a distinct break, not another row seam.
        var sepStart = ImGui.GetCursorScreenPos();
        float sepRight = sepStart.X + BodyAvailW();
        uint sepCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(accent.X, accent.Y, accent.Z, 0.22f));
        for (float dx = sepStart.X; dx < sepRight; dx += 6f * scale)
        {
            float segW = MathF.Min(3f * scale, sepRight - dx);
            dl.AddLine(
                new Vector2(dx, sepStart.Y),
                new Vector2(dx + segW, sepStart.Y),
                sepCol, 1f);
        }

        ImGui.Dummy(new Vector2(1, 8f * scale));
        var rowStart = ImGui.GetCursorScreenPos();
        float textH = ImGui.GetTextLineHeight();

        // Accent marker square - 4px, vertically centered with the text.
        float markerSize = 4f * scale;
        float markerX = rowStart.X + 2f * scale;
        float markerY = rowStart.Y + (textH - markerSize) * 0.5f;
        dl.AddRectFilled(
            new Vector2(markerX, markerY),
            new Vector2(markerX + markerSize, markerY + markerSize),
            ImGui.ColorConvertFloat4ToU32(accent));

        // Label in text-faint, heavily tracked to read as a quiet header.
        DrawTrackedText(dl,
            new Vector2(markerX + markerSize + 10f * scale, rowStart.Y),
            ImGui.ColorConvertFloat4ToU32(TextFaint),
            label.ToUpperInvariant(), 2.8f * scale);

        ImGui.Dummy(new Vector2(1, textH + 4f * scale));
    }

    // body width after symmetric padding (content child stays full width for bg fill)
    private static float BodyAvailW()
        => ImGui.GetContentRegionAvail().X - 22f * UIStyles.Scale;

    // ==================================================================
    // CONTENT HELPERS
    // ==================================================================
    private static void WrappedText(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        // Wrap at the body right edge (BodyAvailW), not the child's edge -
        // otherwise paragraph intros run past the 22px right pad.
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + BodyAvailW());
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    // Feature card. Drawlist only, no inner widgets, so SameLine works.
    private void DrawFeatureCard(Vector4 accent, FontAwesomeIcon icon,
                                  string title, string subtitle, float width)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float padX = 10f * scale;
        float padY = 10f * scale;
        float iconSize = 26f * scale;

        var textH = ImGui.GetTextLineHeight();
        float cardH = padY * 2 + textH * 2 + 4f * scale;
        var end = new Vector2(start.X + width, start.Y + cardH);

        // Card surface.
        dl.AddRectFilled(start, end,
            ImGui.ColorConvertFloat4ToU32(Surface3));
        dl.AddRect(start, end,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.22f)),
            0f, 0, 1f);
        dl.AddRectFilled(
            start, new Vector2(start.X + 2f * scale, end.Y),
            ImGui.ColorConvertFloat4ToU32(accent));

        // Icon tile, accent tinted.
        float ix = start.X + padX;
        float iy = start.Y + (cardH - iconSize) * 0.5f;
        dl.AddRectFilled(
            new Vector2(ix, iy), new Vector2(ix + iconSize, iy + iconSize),
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.10f)));
        dl.AddRect(
            new Vector2(ix, iy), new Vector2(ix + iconSize, iy + iconSize),
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.42f)),
            0f, 0, 1f);
        DrawFaIconCentered(dl, icon, new Vector2(ix, iy),
            new Vector2(iconSize, iconSize), accent);

        // Title and subtitle.
        float textX = ix + iconSize + 12f * scale;
        float titleY = start.Y + padY + 1f * scale;
        dl.AddText(new Vector2(textX, titleY),
            ImGui.ColorConvertFloat4ToU32(Text), title);
        dl.AddText(new Vector2(textX, titleY + textH + 2f * scale),
            ImGui.ColorConvertFloat4ToU32(TextDim), subtitle);

        // Emit a single Dummy matching the card footprint so SameLine works.
        ImGui.Dummy(new Vector2(width, cardH));
    }

    // letter-spaced text (ImGui has no native letter-spacing)
    private static void DrawTrackedText(
        ImDrawListPtr dl, Vector2 pos, uint color, string text, float spacing)
    {
        if (string.IsNullOrEmpty(text)) return;
        float x = pos.X;
        for (int i = 0; i < text.Length; i++)
        {
            string ch = text[i].ToString();
            dl.AddText(new Vector2(x, pos.Y), color, ch);
            x += ImGui.CalcTextSize(ch).X + spacing;
        }
    }

    // Mirror of the draw routine - useful if a caller ever needs the total
    // width of a tracked string for centering / right-aligning.
    private static float MeasureTrackedText(string text, float spacing)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
        {
            w += ImGui.CalcTextSize(text[i].ToString()).X;
            if (i < text.Length - 1) w += spacing;
        }
        return w;
    }

    // Centers a FontAwesome glyph in the given rect using its visible
    // bounds (X0/X1 from the font glyph), like UIStyles.IconButton.
    private static unsafe void DrawFaIconCentered(
        ImDrawListPtr dl, FontAwesomeIcon icon, Vector2 pos,
        Vector2 size, Vector4 color)
    {
        var iconStr = icon.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        try
        {
            var glyph = ImGui.GetFont().FindGlyph((ushort)iconStr[0]);
            var glyphSize = ImGui.CalcTextSize(iconStr);
            float visibleW = glyph->X1 - glyph->X0;
            float targetLeft = pos.X + (size.X - visibleW) * 0.5f;
            var drawPos = new Vector2(
                targetLeft - glyph->X0,
                pos.Y + (size.Y - glyphSize.Y) * 0.5f);
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
                drawPos, ImGui.ColorConvertFloat4ToU32(color), iconStr);
        }
        finally { ImGui.PopFont(); }
    }

    private void DrawStep(Vector4 accent, int num, string title,
                           string desc, bool optional)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float numBoxSize = 22f * scale;

        var numMin = new Vector2(start.X, start.Y + 2f * scale);
        var numMax = new Vector2(start.X + numBoxSize, start.Y + 2f * scale + numBoxSize);
        dl.AddRectFilled(numMin, numMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.08f)));
        dl.AddRect(numMin, numMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.42f)),
            0f, 0, 1f);
        string numStr = $"{num:D2}";
        var numSz = ImGui.CalcTextSize(numStr);
        dl.AddText(
            new Vector2(numMin.X + (numBoxSize - numSz.X) * 0.5f,
                        numMin.Y + (numBoxSize - numSz.Y) * 0.5f),
            ImGui.ColorConvertFloat4ToU32(accent), numStr);

        float textX = start.X + numBoxSize + 12f * scale;
        var textH = ImGui.GetTextLineHeight();
        dl.AddText(new Vector2(textX, start.Y),
            ImGui.ColorConvertFloat4ToU32(Text), title);
        if (optional)
        {
            var titleSz = ImGui.CalcTextSize(title);
            string opt = "OPTIONAL";
            var optSz = ImGui.CalcTextSize(opt);
            float tagPadX = 6f * scale;
            float tagPadY = 2f * scale;
            float tagX = textX + titleSz.X + 8f * scale;
            float tagY = start.Y + (textH - optSz.Y - tagPadY * 2) * 0.5f;
            var tagMin = new Vector2(tagX, tagY);
            var tagMax = new Vector2(tagX + optSz.X + tagPadX * 2, tagY + optSz.Y + tagPadY * 2);
            // Filled accent pill, not a muted outline.
            dl.AddRectFilled(tagMin, tagMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.18f)));
            dl.AddRect(tagMin, tagMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.55f)),
                0f, 0, 1f);
            dl.AddText(new Vector2(tagX + tagPadX, tagY + tagPadY),
                ImGui.ColorConvertFloat4ToU32(accent), opt);
        }

        float availW = BodyAvailW();
        float descX = textX;
        float descMaxW = availW - (descX - start.X) - 4f * scale;

        ImGui.SetCursorScreenPos(new Vector2(descX, start.Y + textH + 3f * scale));
        float wrapLocalX = ImGui.GetCursorPosX() + descMaxW;
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.TextWrapped(desc);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        float rowBot = ImGui.GetCursorScreenPos().Y;
        float numBot = numMax.Y;
        float endY = MathF.Max(rowBot, numBot);
        ImGui.SetCursorScreenPos(new Vector2(start.X, endY));
        ImGui.Dummy(new Vector2(1, 4f * scale));
        var sepStart = ImGui.GetCursorScreenPos();
        uint sepCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f));
        for (float dx = 0; dx < availW; dx += 6f * scale)
        {
            float segW = MathF.Min(3f * scale, availW - dx);
            dl.AddLine(
                new Vector2(sepStart.X + dx, sepStart.Y),
                new Vector2(sepStart.X + dx + segW, sepStart.Y),
                sepCol, 1f);
        }
        ImGui.Dummy(new Vector2(1, 4f * scale));
    }

    // TextWrapped perturbs the cursor; reset to start before the final Dummy for SameLine
    private void DrawMethodCard(Vector4 accent, FontAwesomeIcon icon,
                                 string title, string desc, float width)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float padX = 14f * scale;
        float padY = 14f * scale;
        var textH = ImGui.GetTextLineHeight();
        float iconSize = 22f * scale;

        float cardH = padY * 2 + iconSize + textH * 3 + 10f * scale;
        var end = new Vector2(start.X + width, start.Y + cardH);

        dl.AddRectFilled(start, end,
            ImGui.ColorConvertFloat4ToU32(Surface3));
        dl.AddRect(start, end,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.22f)),
            0f, 0, 1f);

        // Corner bracket decorations, top-left and bottom-right.
        float bLen = 12f * scale;
        uint bCol = ImGui.ColorConvertFloat4ToU32(accent);
        dl.AddLine(start, new Vector2(start.X + bLen, start.Y), bCol, 1f);
        dl.AddLine(start, new Vector2(start.X, start.Y + bLen), bCol, 1f);
        dl.AddLine(end, new Vector2(end.X - bLen, end.Y), bCol, 1f);
        dl.AddLine(end, new Vector2(end.X, end.Y - bLen), bCol, 1f);

        // Icon.
        DrawFaIconCentered(dl, icon,
            new Vector2(start.X + padX, start.Y + padY),
            new Vector2(iconSize, iconSize), accent);
        // Title.
        dl.AddText(
            new Vector2(start.X + padX, start.Y + padY + iconSize + 6f * scale),
            ImGui.ColorConvertFloat4ToU32(Text), title);
        // Description, wrapped via ImGui widget flow.
        ImGui.SetCursorScreenPos(
            new Vector2(start.X + padX, start.Y + padY + iconSize + textH + 8f * scale));
        float wrapLocalX = ImGui.GetCursorPosX() + width - padX * 2;
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.TextWrapped(desc);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        // Reset cursor to the card origin so the Dummy below lands at the
        // card's top-left, giving SameLine a correct "last item" rect.
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(width, cardH));
    }

    private void DrawSubHead(Vector4 accent, string text)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var textH = ImGui.GetTextLineHeight();

        // Short solid dash then label.
        float dashW = 16f * scale;
        float dashY = start.Y + textH * 0.5f;
        dl.AddRectFilled(
            new Vector2(start.X, dashY - 1.5f * scale),
            new Vector2(start.X + dashW, dashY + 1.5f * scale),
            ImGui.ColorConvertFloat4ToU32(accent));

        string upper = text.ToUpperInvariant();
        dl.AddText(
            new Vector2(start.X + dashW + 10f * scale, start.Y),
            ImGui.ColorConvertFloat4ToU32(accent), upper);

        ImGui.Dummy(new Vector2(1, textH + 8f * scale));
    }

    private void DrawBullet(Vector4 accent, string text)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var textH = ImGui.GetTextLineHeight();
        float dashW = 8f * scale;
        float dashY = start.Y + textH * 0.5f;
        dl.AddRectFilled(
            new Vector2(start.X + 4f * scale, dashY - 1f * scale),
            new Vector2(start.X + 4f * scale + dashW, dashY + 1f * scale),
            ImGui.ColorConvertFloat4ToU32(accent));

        float textX = start.X + dashW + 12f * scale;
        float availW = BodyAvailW();
        float maxW = availW - (textX - start.X) - 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y));
        float wrapLocalX = ImGui.GetCursorPosX() + maxW;
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    // Bullet with an accent-colored leading label, mirrors `<li><b>x</b> ...</li>`
    // in the mockup. The label renders inline with the body via SameLine(0,0);
    // PushTextWrapPos handles wrapping of the concatenated text.
    private void DrawBulletBold(Vector4 accent, string boldPrefix, string rest)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var textH = ImGui.GetTextLineHeight();
        float dashW = 8f * scale;
        float dashY = start.Y + textH * 0.5f;
        dl.AddRectFilled(
            new Vector2(start.X + 4f * scale, dashY - 1f * scale),
            new Vector2(start.X + 4f * scale + dashW, dashY + 1f * scale),
            ImGui.ColorConvertFloat4ToU32(accent));

        float textX = start.X + dashW + 12f * scale;
        float availW = BodyAvailW();
        float maxW = availW - (textX - start.X) - 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y));
        float wrapLocalX = ImGui.GetCursorPosX() + maxW;
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        ImGui.TextUnformatted(boldPrefix);
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 0);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.TextWrapped(rest);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
    }

    private void DrawNumberedBullet(Vector4 accent, int num, string text)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        string numStr = $"{num:D2}";
        var numSz = ImGui.CalcTextSize(numStr);
        dl.AddText(
            new Vector2(start.X + 2f * scale, start.Y),
            ImGui.ColorConvertFloat4ToU32(accent), numStr);

        float textX = start.X + numSz.X + 14f * scale;
        float availW = BodyAvailW();
        float maxW = availW - (textX - start.X) - 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(textX, start.Y));
        float wrapLocalX = ImGui.GetCursorPosX() + maxW;
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.TextWrapped(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    // Callout. Icon column on the left, bold accent prefix inline with the
    // rest of the body. Mirrors the mockup's `.callout` blocks where the
    // first phrase reads like a glossary term and the rest wraps after it.
    private void DrawCallout(Vector4 accent, FontAwesomeIcon icon,
                              string boldPrefix, string rest)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float padX = 14f * scale;
        float padY = 12f * scale;
        float iconW = 20f * scale;
        float iconGap = 12f * scale;
        float availW = BodyAvailW();
        float textAreaW = availW - padX * 2 - iconW - iconGap;

        // Measure combined text so the box fits both the prefix and wrapped
        // body together. CalcTextSize with a wrap width returns the total
        // wrapped rect for the full string.
        string combined = boldPrefix + rest;
        var bodySz = ImGui.CalcTextSize(combined, false, textAreaW);
        var textH = ImGui.GetTextLineHeight();
        float contentH = MathF.Max(bodySz.Y, textH);
        float totalH = padY * 2 + contentH;
        var end = new Vector2(start.X + availW, start.Y + totalH);

        // Tinted surface + border + left accent bar.
        dl.AddRectFilled(start, end,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.08f)));
        dl.AddRect(start, end,
            ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.32f)),
            0f, 0, 1f);
        dl.AddRectFilled(
            start, new Vector2(start.X + 3f * scale, end.Y),
            ImGui.ColorConvertFloat4ToU32(accent));

        // Icon, vertically centered in its column.
        float iconX = start.X + padX;
        float iconY = start.Y + (totalH - iconW) * 0.5f;
        DrawFaIconCentered(dl, icon,
            new Vector2(iconX, iconY), new Vector2(iconW, iconW), accent);

        // Inline bold prefix in accent color, then the rest continues on
        // the same line and wraps within textAreaW.
        float textStartX = start.X + padX + iconW + iconGap;
        float textStartY = start.Y + padY;
        ImGui.SetCursorScreenPos(new Vector2(textStartX, textStartY));
        float wrapLocalX = ImGui.GetCursorPosX() + textAreaW;
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.PushStyleColor(ImGuiCol.Text, accent);
        ImGui.TextUnformatted(boldPrefix);
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 0);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.TextWrapped(rest);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        // Reset cursor to origin then emit a footprint Dummy so downstream
        // layout (spacing, SameLine) works from the callout's start.
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(availW, totalH));
    }

    private void DrawCommandRow(Vector4 accent, string cmd, string desc)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var textH = ImGui.GetTextLineHeight();

        dl.AddText(new Vector2(start.X, start.Y),
            ImGui.ColorConvertFloat4ToU32(accent), cmd);

        float availW = BodyAvailW();
        float descX = start.X + 160f * scale;
        float maxW = availW - (descX - start.X) - 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(descX, start.Y));
        float wrapLocalX = ImGui.GetCursorPosX() + maxW;
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.TextWrapped(desc);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        float rowBot = ImGui.GetCursorScreenPos().Y;
        ImGui.SetCursorScreenPos(new Vector2(start.X, rowBot + 2f * scale));
        var sepStart = ImGui.GetCursorScreenPos();
        uint sepCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f));
        for (float dx = 0; dx < availW; dx += 6f * scale)
        {
            float segW = MathF.Min(3f * scale, availW - dx);
            dl.AddLine(
                new Vector2(sepStart.X + dx, sepStart.Y),
                new Vector2(sepStart.X + dx + segW, sepStart.Y),
                sepCol, 1f);
        }
        ImGui.Dummy(new Vector2(1, 4f * scale));
    }

    // ==================================================================
    // FOOTER. Prev/next arrows, chapter dots, Done button.
    // ==================================================================
    private void DrawFooter()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float availW = ImGui.GetContentRegionAvail().X;
        float h = 44f * scale;
        var end = new Vector2(start.X + availW, start.Y + h);

        dl.AddRectFilled(start, end,
            ImGui.ColorConvertFloat4ToU32(new Vector4(0.027f, 0.039f, 0.070f, 1f)));
        dl.AddLine(start, new Vector2(end.X, start.Y),
            ImGui.ColorConvertFloat4ToU32(Border), 1f);

        // Top hairline fading from center, binder stitch.
        uint stitchC = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.28f));
        uint stitchClr = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.25f, start.Y - 1f),
            new Vector2(start.X + availW * 0.50f, start.Y),
            stitchClr, stitchC, stitchC, stitchClr);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.50f, start.Y - 1f),
            new Vector2(start.X + availW * 0.75f, start.Y),
            stitchC, stitchClr, stitchClr, stitchC);

        float padX = 14f * scale;
        float centerY = start.Y + h * 0.5f;

        // Prev arrow.
        float arrSize = 26f * scale;
        float prevX = start.X + padX;
        float prevY = centerY - arrSize * 0.5f;
        bool prevDisabled = currentChapter == 0;
        DrawNavArrow("##prevArr", new Vector2(prevX, prevY),
            new Vector2(arrSize, arrSize), "<", prevDisabled,
            onClick: () => { if (currentChapter > 0) currentChapter--; });

        // Next arrow.
        float nextX = end.X - padX - arrSize - 100f * scale - 10f * scale;
        float nextY = prevY;
        bool nextDisabled = currentChapter == TOTAL - 1;
        DrawNavArrow("##nextArr", new Vector2(nextX, nextY),
            new Vector2(arrSize, arrSize), ">", nextDisabled,
            onClick: () => { if (currentChapter < TOTAL - 1) currentChapter++; });

        // Dots between the arrows - animated transitions.
        float dotsLeft = prevX + arrSize + 10f * scale;
        float dotsRight = nextX - 10f * scale;
        float dotsCenter = (dotsLeft + dotsRight) * 0.5f;
        float dotSize = 6f * scale;
        float activeDotW = 14f * scale;
        float dotGap = 6f * scale;

        // Detect chapter change -> kick off a transition. First render
        // (displayedChapter == -1) adopts the current chapter silently
        // so the window doesn't animate on open.
        if (displayedChapter != currentChapter)
        {
            if (displayedChapter >= 0)
            {
                fromChapterForTransition = displayedChapter;
                transitionStartAt = ImGui.GetTime();
            }
            displayedChapter = currentChapter;
        }

        bool isTransitioning = transitionStartAt >= 0;
        float t = 1f;
        if (isTransitioning)
        {
            float tRaw = MathF.Min(1f,
                (float)((ImGui.GetTime() - transitionStartAt) / TransitionSec));
            t = 1f - MathF.Pow(1f - tRaw, 3f); // easeOutCubic
            if (tRaw >= 1f)
            {
                transitionStartAt = -1;
                fromChapterForTransition = -1;
                isTransitioning = false;
            }
        }
        int fromCh = isTransitioning ? fromChapterForTransition : currentChapter;

        float totalDotsW = (TOTAL - 1) * dotSize + activeDotW + (TOTAL - 1) * dotGap;
        float xStart = dotsCenter - totalDotsW * 0.5f;
        float dotY = centerY - dotSize * 0.5f;

        for (int i = 0; i < TOTAL; i++)
        {
            // Walk the row twice: once with fromCh active to get xFrom,
            // once with currentChapter active to get xCur. O(n^2) with n=5
            // is negligible.
            float xFrom = xStart;
            float xCur = xStart;
            for (int k = 0; k < i; k++)
            {
                xFrom += (k == fromCh ? activeDotW : dotSize) + dotGap;
                xCur  += (k == currentChapter ? activeDotW : dotSize) + dotGap;
            }
            float xi = xFrom + (xCur - xFrom) * t;

            float wFrom = (i == fromCh) ? activeDotW : dotSize;
            float wCur  = (i == currentChapter) ? activeDotW : dotSize;
            float wi = wFrom + (wCur - wFrom) * t;

            var dotMin = new Vector2(xi, dotY);
            var dotMax = new Vector2(xi + wi, dotY + dotSize);

            ImGui.SetCursorScreenPos(dotMin);
            if (ImGui.InvisibleButton($"##dot_{i}", new Vector2(wi, dotSize)))
                currentChapter = i;
            bool hovered = ImGui.IsItemHovered();

            // Compute state (rgb, fillA, borderA) at fromCh and currentChapter,
            // then lerp. done/upcoming use the shared Accent hue; the active
            // dot uses its chapter's own accent color.
            Vector3 rgbFrom; float fillAFrom; float borderAFrom;
            if (i == fromCh)
            {
                rgbFrom = new Vector3(ChapAccents[fromCh].X, ChapAccents[fromCh].Y, ChapAccents[fromCh].Z);
                fillAFrom = 1f; borderAFrom = 0f;
            }
            else if (i < fromCh)
            {
                rgbFrom = new Vector3(Accent.X, Accent.Y, Accent.Z);
                fillAFrom = 0.35f; borderAFrom = 0f;
            }
            else
            {
                rgbFrom = new Vector3(Accent.X, Accent.Y, Accent.Z);
                fillAFrom = 0f; borderAFrom = 0.35f;
            }

            Vector3 rgbCur; float fillACur; float borderACur;
            if (i == currentChapter)
            {
                rgbCur = new Vector3(ChapAccents[currentChapter].X, ChapAccents[currentChapter].Y, ChapAccents[currentChapter].Z);
                fillACur = 1f; borderACur = 0f;
            }
            else if (i < currentChapter)
            {
                rgbCur = new Vector3(Accent.X, Accent.Y, Accent.Z);
                fillACur = 0.35f; borderACur = 0f;
            }
            else
            {
                rgbCur = new Vector3(Accent.X, Accent.Y, Accent.Z);
                fillACur = 0f; borderACur = 0.35f;
            }

            var rgb = new Vector3(
                rgbFrom.X + (rgbCur.X - rgbFrom.X) * t,
                rgbFrom.Y + (rgbCur.Y - rgbFrom.Y) * t,
                rgbFrom.Z + (rgbCur.Z - rgbFrom.Z) * t);
            float fillA = fillAFrom + (fillACur - fillAFrom) * t;
            float borderA = borderAFrom + (borderACur - borderAFrom) * t;

            // Hover lift for non-active, non-transitioning dots.
            if (hovered && i != currentChapter && !isTransitioning)
            {
                fillA = MathF.Max(fillA, 0.25f);
                borderA = MathF.Max(borderA, 0.60f);
            }

            // Breathing halo behind the stable active pill. Slow sine at
            // 0.5 Hz - subtle, not distracting.
            if (i == currentChapter && !isTransitioning)
            {
                float breath = 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * MathF.PI);
                float halo = (0.10f + 0.10f * breath) * 0.4f;
                float pad = 3f * scale;
                dl.AddRectFilled(
                    new Vector2(dotMin.X - pad, dotMin.Y - pad),
                    new Vector2(dotMax.X + pad, dotMax.Y + pad),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, halo)));
            }

            if (fillA > 0.01f)
            {
                dl.AddRectFilled(dotMin, dotMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(rgb.X, rgb.Y, rgb.Z, fillA)));
            }
            if (borderA > 0.01f)
            {
                dl.AddRect(dotMin, dotMax,
                    ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, borderA)),
                    0f, 0, 1f);
            }

            // Bloom ring emanating from the newly-active dot during a
            // transition - expands as t goes 0->1 and fades out.
            if (isTransitioning && i == currentChapter)
            {
                float rippleR = wi * 0.5f + t * dotSize * 2.5f;
                float rippleA = (1f - t) * 0.50f;
                if (rippleA > 0.01f)
                {
                    var ctr = new Vector2(
                        (dotMin.X + dotMax.X) * 0.5f,
                        (dotMin.Y + dotMax.Y) * 0.5f);
                    dl.AddCircle(ctr, rippleR,
                        ImGui.ColorConvertFloat4ToU32(
                            new Vector4(rgb.X, rgb.Y, rgb.Z, rippleA)),
                        24, 1.5f);
                }
            }
        }

        // Done button, right side.
        float btnW = 100f * scale;
        float btnH = 28f * scale;
        float btnX = end.X - padX - btnW;
        float btnY = centerY - btnH * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(btnX, btnY));
        ImGui.PushStyleColor(ImGuiCol.Button, Accent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentBright);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentDeep);
        ImGui.PushStyleColor(ImGuiCol.Border, AccentDeep);
        ImGui.PushStyleColor(ImGuiCol.Text, AccentDark);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        if (ImGui.Button("DONE", new Vector2(btnW, btnH)))
        {
            if (Plugin.Instance != null)
            {
                Plugin.Instance.Configuration.HasSeenHelp = true;
                Plugin.Instance.Configuration.Save();
            }
            IsOpen = false;
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(1, 0));
    }

    private void DrawNavArrow(string id, Vector2 pos, Vector2 size,
                               string glyph, bool disabled, Action onClick)
    {
        var dl = ImGui.GetWindowDrawList();
        ImGui.SetCursorScreenPos(pos);
        if (!disabled && ImGui.InvisibleButton(id, size)) onClick();
        bool hovered = !disabled && ImGui.IsItemHovered();

        var min = pos;
        var max = new Vector2(pos.X + size.X, pos.Y + size.Y);
        Vector4 borderCol = disabled
            ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f)
            : (hovered ? Accent : new Vector4(Accent.X, Accent.Y, Accent.Z, 0.24f));
        dl.AddRect(min, max,
            ImGui.ColorConvertFloat4ToU32(borderCol), 0f, 0, 1f);
        if (hovered)
            dl.AddRectFilled(min, max,
                ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.08f)));
        var sz = ImGui.CalcTextSize(glyph);
        Vector4 textCol = disabled ? TextGhost : (hovered ? AccentBright : TextDim);
        dl.AddText(
            new Vector2(pos.X + (size.X - sz.X) * 0.5f,
                        pos.Y + (size.Y - sz.Y) * 0.5f),
            ImGui.ColorConvertFloat4ToU32(textCol), glyph);
    }

    private void DrawWindowCornerBrackets()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        float arm = 14f * scale;
        float inset = 6f * scale;
        uint col = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.45f));
        float left = winPos.X + inset;
        float right = winPos.X + winSize.X - inset;
        float bottom = winPos.Y + winSize.Y - inset;
        dl.AddLine(new Vector2(left, bottom - arm), new Vector2(left, bottom), col, 1f);
        dl.AddLine(new Vector2(left, bottom), new Vector2(left + arm, bottom), col, 1f);
        dl.AddLine(new Vector2(right - arm, bottom), new Vector2(right, bottom), col, 1f);
        dl.AddLine(new Vector2(right, bottom - arm), new Vector2(right, bottom), col, 1f);
    }
}
