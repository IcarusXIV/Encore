using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Encore.Styles;

namespace Encore.Windows;

public class SettingsWindow : Window
{
    //  Palette 
    private static readonly Vector4 Accent       = new(0.49f, 0.65f, 0.85f, 1f);
    private static readonly Vector4 AccentBright = new(0.65f, 0.77f, 0.92f, 1f);
    private static readonly Vector4 AccentDeep   = new(0.40f, 0.53f, 0.72f, 1f);
    private static readonly Vector4 AccentDark   = new(0.05f, 0.08f, 0.13f, 1f);
    private static readonly Vector4 Text         = new(0.86f, 0.87f, 0.89f, 1f);
    private static readonly Vector4 TextDim      = new(0.56f, 0.58f, 0.63f, 1f);
    private static readonly Vector4 TextFaint    = new(0.36f, 0.38f, 0.45f, 1f);
    private static readonly Vector4 Border       = new(0.18f, 0.21f, 0.26f, 1f);
    private static readonly Vector4 BorderSoft   = new(0.12f, 0.13f, 0.19f, 1f);
    private static readonly Vector4 Surface3     = new(0.125f, 0.140f, 0.180f, 1f);

    private static readonly Vector4 WindowBg  = new(0.020f, 0.024f, 0.035f, 1f);
    private static readonly Vector4 ContentBg = new(0.047f, 0.055f, 0.075f, 1f);

    // Per-section accents - drawn from the chapter rainbow palette.
    private static readonly Vector4 SecEmotes        = new(0.45f, 0.92f, 0.55f, 1f);  // spring green
    private static readonly Vector4 SecNotifications = new(0.49f, 0.65f, 0.85f, 1f);  // signal blue (accent)
    private static readonly Vector4 SecDisplay       = new(0.72f, 0.52f, 1.00f, 1f);  // violet
    private static readonly Vector4 Warning          = new(1.00f, 0.72f, 0.30f, 1f);  // amber

    // 8-step rainbow palette - same one used by the patch-notes and
    // main-window waveforms. Lets the footer EQ share a single family.
    private static readonly Vector4[] RainbowPalette =
    {
        new(0.38f, 0.72f, 1.00f, 1f),
        new(0.72f, 0.52f, 1.00f, 1f),
        new(1.00f, 0.42f, 0.70f, 1f),
        new(0.28f, 0.88f, 0.92f, 1f),
        new(0.45f, 0.92f, 0.55f, 1f),
        new(1.00f, 0.82f, 0.30f, 1f),
        new(1.00f, 0.62f, 0.25f, 1f),
        new(1.00f, 0.50f, 0.45f, 1f),
    };

    // Smoothed switch-knob positions (0 = OFF, 1 = ON). Each id
    // springs toward its target with moderate damping -> small
    // overshoot ("bounce") when toggled, settling in ~400ms.
    private readonly Dictionary<string, float> switchAnim = new();
    // Companion velocity field for the spring simulation.
    private readonly Dictionary<string, float> switchVel = new();

    private readonly Dictionary<string, double> switchFlareStartAt = new();
    private const double SwitchFlareSec = 0.55;

    public SettingsWindow() : base("Encore - Settings###EncoreSettings")
    {
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking
              | ImGuiWindowFlags.NoScrollbar;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.WindowScale;
        Size = new Vector2(540f * scale, 620f * scale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540f * scale, 480f * scale),
            MaximumSize = new Vector2(540f * scale, 1200f * scale),
        };
        UIStyles.PushEncoreWindow();
        ImGui.PushStyleColor(ImGuiCol.WindowBg, WindowBg);
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
            DrawConsoleBanner();

            var footerH = 48f * scale;
            var contentH = ImGui.GetContentRegionAvail().Y - footerH;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, ContentBg);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, 5f * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f * scale, 16f * scale));
            if (ImGui.BeginChild("##settingsContent",
                    new Vector2(ImGui.GetContentRegionAvail().X, contentH), false))
            {
                DrawContent();
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

    // ================================================================
    // RIBBON - 30px metadata bar shared with PatchNotes / Help.
    // ================================================================
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

        // Top hairline: accent solid at edges, fade to center.
        uint aSolid = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f));
        uint aClear = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            start, new Vector2(start.X + availW * 0.42f, start.Y + 1f),
            aSolid, aClear, aClear, aSolid);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.58f, start.Y),
            new Vector2(end.X, start.Y + 1f),
            aClear, aSolid, aSolid, aClear);

        // Bottom hairline: solid in middle, fade to edges.
        uint aSoft = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.30f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, end.Y - 1f),
            new Vector2(start.X + availW * 0.5f, end.Y),
            aClear, aSoft, aSoft, aClear);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.5f, end.Y - 1f),
            end,
            aSoft, aClear, aClear, aSoft);

        // 4-bar level-meter pip - settings' variation on the
        // patch-notes 3-bar pip. Bars step up like a VU meter.
        float padX = 14f * scale;
        float pipBoxH = 14f * scale;
        float pipX = start.X + padX;
        float pipBottomY = start.Y + (ribbonH + pipBoxH) * 0.5f;
        float t = (float)ImGui.GetTime();
        float barW = 2f * scale;
        float barGap = 2f * scale;
        float[] baseH = { 0.30f, 0.55f, 0.80f, 0.95f };
        for (int i = 0; i < 4; i++)
        {
            float phase = i * 0.10f;
            float pulse = 0.30f + 0.70f * (0.5f + 0.5f * MathF.Sin((t + phase) * MathF.Tau / 1.6f));
            float h = baseH[i] * pipBoxH * pulse;
            float x = pipX + i * (barW + barGap);
            dl.AddRectFilled(
                new Vector2(x, pipBottomY - h),
                new Vector2(x + barW, pipBottomY),
                ImGui.ColorConvertFloat4ToU32(Accent));
        }

        // Meta label + signal counter.
        var textH = ImGui.GetTextLineHeight();
        float textY = start.Y + (ribbonH - textH) * 0.5f;
        float metaX = pipX + 4 * (barW + barGap) + 12f * scale;
        string label = "THE BOARD";
        string sep = "  -  ";

        var config = Plugin.Instance?.Configuration;
        int armed = ArmedSwitchCount(config);
        // "SIGNAL " + armed count (accent if > 0) + " / 5 ARMED"
        string sigPre  = "SIGNAL  ";
        string sigNum  = $"{armed}";
        string sigPost = "  /  5  ARMED";

        var labelSz = ImGui.CalcTextSize(label);
        var sepSz   = ImGui.CalcTextSize(sep);
        var sigPreSz = ImGui.CalcTextSize(sigPre);
        var sigNumSz = ImGui.CalcTextSize(sigNum);
        dl.AddText(new Vector2(metaX, textY), ImGui.ColorConvertFloat4ToU32(Text), label);
        dl.AddText(new Vector2(metaX + labelSz.X, textY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), sep);
        float sigX = metaX + labelSz.X + sepSz.X;
        dl.AddText(new Vector2(sigX, textY),
            ImGui.ColorConvertFloat4ToU32(TextDim), sigPre);
        dl.AddText(new Vector2(sigX + sigPreSz.X, textY),
            ImGui.ColorConvertFloat4ToU32(armed > 0 ? Accent : TextDim), sigNum);
        dl.AddText(new Vector2(sigX + sigPreSz.X + sigNumSz.X, textY),
            ImGui.ColorConvertFloat4ToU32(TextDim), sigPost);

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

        ImGui.Dummy(new Vector2(1, ribbonH));
    }

    private static int ArmedSwitchCount(Configuration? config)
    {
        if (config == null) return 0;
        int n = 0;
        if (config.AllowSitDozeAnywhere)     n++;
        if (config.AllowUnlockedEmotes)      n++;
        if (config.ShowPatchNotesOnStartup)  n++;
        if (config.ShowUpdateNotification)   n++;
        if (config.IgnoreGlobalScale)        n++;
        return n;
    }

    private void DrawConsoleBanner()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var bannerH = 94f * scale;
        var end = new Vector2(start.X + availW, start.Y + bannerH);
        float t = (float)ImGui.GetTime();

        // Background - subtle radial from bottom-center.
        uint bgTop = ImGui.ColorConvertFloat4ToU32(new Vector4(0.071f, 0.086f, 0.126f, 1f));
        uint bgBot = ImGui.ColorConvertFloat4ToU32(new Vector4(0.039f, 0.051f, 0.078f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        // Soft drifting spotlight - shared idiom with patch-notes banner.
        void Spotlight(float period, float phase, float xFracBase, float xFracRange,
                       float yFrac, float maxR, float peakAlpha)
        {
            float st = 0.5f + 0.5f * MathF.Sin((t + phase) * MathF.Tau / period);
            float cx = start.X + availW * (xFracBase + xFracRange * st);
            float cy = start.Y + bannerH * yFrac;
            const int layers = 14;
            for (int l = layers - 1; l >= 0; l--)
            {
                float u = (float)l / (layers - 1);
                float r = maxR * (0.12f + 0.88f * u);
                float fall = (1f - u) * (1f - u);
                float a = peakAlpha * fall;
                dl.AddCircleFilled(
                    new Vector2(cx, cy), r,
                    ImGui.ColorConvertFloat4ToU32(
                        new Vector4(AccentBright.X, AccentBright.Y, AccentBright.Z, a)),
                    40);
            }
        }
        Spotlight(period: 18f, phase: 0f,  xFracBase: 0.20f, xFracRange: 0.50f,
                  yFrac: 0.35f, maxR: 100f * scale, peakAlpha: 0.020f);
        Spotlight(period: 22f, phase: 7f,  xFracBase: 0.40f, xFracRange: 0.40f,
                  yFrac: 0.65f, maxR: 80f  * scale, peakAlpha: 0.014f);

        // Bottom hairline - accent solid in middle, fade to edges.
        float ruleH = 1f;
        uint hSolid = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.75f));
        uint hClear = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, end.Y - ruleH),
            new Vector2(start.X + availW * 0.5f, end.Y),
            hClear, hSolid, hSolid, hClear);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.5f, end.Y - ruleH),
            end,
            hSolid, hClear, hClear, hSolid);

        // Corner brackets (top-left, top-right).
        float bracketSize = 14f * scale;
        float bracketInset = 8f * scale;
        uint bracketCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.45f));
        dl.AddLine(
            new Vector2(start.X + bracketInset, start.Y + bracketInset),
            new Vector2(start.X + bracketInset + bracketSize, start.Y + bracketInset),
            bracketCol, 1f);
        dl.AddLine(
            new Vector2(start.X + bracketInset, start.Y + bracketInset),
            new Vector2(start.X + bracketInset, start.Y + bracketInset + bracketSize),
            bracketCol, 1f);
        dl.AddLine(
            new Vector2(end.X - bracketInset - bracketSize, start.Y + bracketInset),
            new Vector2(end.X - bracketInset, start.Y + bracketInset),
            bracketCol, 1f);
        dl.AddLine(
            new Vector2(end.X - bracketInset, start.Y + bracketInset),
            new Vector2(end.X - bracketInset, start.Y + bracketInset + bracketSize),
            bracketCol, 1f);

        float padX = 20f * scale;
        float padY = 12f * scale;
        float leftX = start.X + padX;
        float idTop = start.Y + padY;

        string kicker = "CONTROL  SURFACE";
        float tickLen = 10f * scale;
        var kickerLineH = ImGui.GetTextLineHeight();
        uint tickCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f));
        dl.AddLine(
            new Vector2(leftX, idTop + kickerLineH * 0.5f),
            new Vector2(leftX + tickLen, idTop + kickerLineH * 0.5f),
            tickCol, 1f);
        float kickerX = leftX + tickLen + 8f * scale;
        DrawTrackedText(dl, new Vector2(kickerX, idTop), kicker,
            ImGui.ColorConvertFloat4ToU32(TextFaint), 1.5f * scale);

        // 30px Unbounded-Bold; 3px manual kerning
        string wordmark = "BOARD";
        var titleFont = Plugin.Instance?.TitleFont;
        var bannerFont = Plugin.Instance?.BannerFont;
        var headerFont = Plugin.Instance?.HeaderFont;
        IFontHandle? wmFont = null;
        if (titleFont is { Available: true }) wmFont = titleFont;
        else if (bannerFont is { Available: true }) wmFont = bannerFont;
        else if (headerFont is { Available: true }) wmFont = headerFont;
        float wmX = leftX;
        // Stack tightly - kicker and wordmark just 2px apart so
        // they read as a unified ID block, not three spread rows.
        float wmY = idTop + kickerLineH + 2f * scale;

        // Wider kerning - at 7px the 5-letter BOARD stretches out
        // to roughly where "ur" lands in SURFACE on the kicker
        // line above, visually rhyming the two rows' widths.
        float wmTrack = 7f * scale;
        float wmHeight;
        if (wmFont != null)
        {
            using (wmFont.Push())
            {
                wmHeight = ImGui.CalcTextSize(wordmark).Y;
                DrawTrackedText(dl, new Vector2(wmX, wmY), wordmark,
                    ImGui.ColorConvertFloat4ToU32(Text), wmTrack);
            }
        }
        else
        {
            wmHeight = ImGui.CalcTextSize(wordmark).Y;
            DrawTrackedText(dl, new Vector2(wmX, wmY), wordmark,
                ImGui.ColorConvertFloat4ToU32(Text), wmTrack);
        }

        // Sub-line - caption treatment, tight kerning (0.5px) so
        // it reads as smaller/quieter than the kicker. Sits 2px
        // below BOARD to keep the three rows visually clustered.
        string sub = "HOUSE  LIGHTS  -  MONITOR  MIX";
        float subY = wmY + wmHeight + 2f * scale;
        DrawTrackedText(dl, new Vector2(wmX, subY), sub,
            ImGui.ColorConvertFloat4ToU32(TextFaint), 0.5f * scale);

        // 8 mini-faders. rackLeft past the sub-line's end so it doesn't clip
        float rackLeft = start.X + availW * 0.52f;
        float rackRight = end.X - padX;
        float rackTop = start.Y + 10f * scale;
        float rackBot = end.Y - 8f * scale;
        float rackH = rackBot - rackTop;

        int faderN = 8;
        float faderGap = 6f * scale;
        float rackW = rackRight - rackLeft;
        float faderW = (rackW - faderGap * (faderN - 1)) / faderN;

        // first 5 reflect config state; last 3 wobble for ambience
        float[] vals = new float[faderN];
        var config = Plugin.Instance?.Configuration;
        if (config != null)
        {
            vals[0] = config.AllowSitDozeAnywhere    ? 0.88f : 0.18f;
            vals[1] = config.AllowUnlockedEmotes     ? 0.82f : 0.18f;
            vals[2] = config.ShowPatchNotesOnStartup ? 0.76f : 0.18f;
            vals[3] = config.ShowUpdateNotification  ? 0.70f : 0.18f;
            vals[4] = config.IgnoreGlobalScale       ? 0.74f : 0.18f;
        }
        vals[5] = 0.50f + 0.06f * MathF.Sin(t * 0.40f);
        vals[6] = 0.62f + 0.06f * MathF.Sin(t * 0.35f + 1.2f);
        vals[7] = 0.82f + 0.04f * MathF.Sin(t * 0.25f + 2.4f);

        Vector4[] faderColors =
        {
            SecEmotes, SecEmotes,
            SecNotifications, SecNotifications,
            SecDisplay,
            Accent, AccentBright, AccentDeep,
        };
        string[] faderLabels = { "EMT", "EMT", "NTF", "NTF", "DSP", "MIX", "FX", "MST" };
        // Tooltip for each fader - first 5 describe the real setting
        // they control; last 3 are decorative filler.
        string[] faderTips =
        {
            "Allow Sit / Doze Anywhere",
            "Allow All Emotes",
            "Show patch notes on update",
            "Notify of updates in chat",
            "Ignore global font scale",
            "decorative",
            "decorative",
            "decorative",
        };

        float labelH = 12f * scale;
        float labelGap = 4f * scale;
        float rackContentBot = rackBot - labelH - labelGap;

        // 0 dB reference line - sits low in the rack (~62% down),
        // matching the HTML's `bottom: 38%` placement so the rack
        // reads with more headroom above than below.
        uint refLine = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f));
        float refY = rackTop + (rackContentBot - rackTop) * 0.62f;
        dl.AddLine(
            new Vector2(rackLeft, refY),
            new Vector2(rackRight, refY),
            refLine, 1f);

        for (int i = 0; i < faderN; i++)
        {
            float fx = rackLeft + i * (faderW + faderGap);
            float fMid = fx + faderW * 0.5f;
            float fTop = rackTop;
            float fBot = rackContentBot;
            bool isInteractive = i < 5;

            // Hit-test column for clicks + tooltip (only the first 5
            // are real toggles; last 3 still show their tooltip).
            ImGui.SetCursorScreenPos(new Vector2(fx, fTop));
            string btnId = $"##fader_{i}";
            bool clicked = ImGui.InvisibleButton(btnId,
                new Vector2(faderW, fBot - fTop + labelH + labelGap));
            bool hovered = ImGui.IsItemHovered();

            if (hovered)
            {
                DrawFaderTooltip(faderTips[i], faderColors[i], isInteractive);
            }

            if (clicked && isInteractive && config != null)
            {
                switch (i)
                {
                    case 0: config.AllowSitDozeAnywhere    = !config.AllowSitDozeAnywhere;    break;
                    case 1: config.AllowUnlockedEmotes     = !config.AllowUnlockedEmotes;     break;
                    case 2: config.ShowPatchNotesOnStartup = !config.ShowPatchNotesOnStartup; break;
                    case 3: config.ShowUpdateNotification  = !config.ShowUpdateNotification;  break;
                    case 4: config.IgnoreGlobalScale       = !config.IgnoreGlobalScale;       break;
                }
                config.Save();
            }

            // Smooth the knob position across frames so clicks on
            // interactive faders glide rather than snap. Decorative
            // faders track their sine directly (no need to ease).
            float target = Math.Clamp(vals[i], 0f, 1f);
            if (isInteractive)
            {
                string animKey = $"rackFader_{i}";
                if (!switchAnim.TryGetValue(animKey, out var anim)) anim = target;
                float step = ImGui.GetIO().DeltaTime * 5.5f;
                if (anim < target) anim = MathF.Min(target, anim + step);
                else if (anim > target) anim = MathF.Max(target, anim - step);
                switchAnim[animKey] = anim;
                target = anim;
            }

            // Track - thin vertical line, slightly wider/brighter
            // for the interactive columns so they invite interaction.
            float trackW = isInteractive ? 2f : 1.5f;
            uint trackCol = ImGui.ColorConvertFloat4ToU32(
                new Vector4(Accent.X, Accent.Y, Accent.Z, isInteractive ? 0.12f : 0.08f));
            dl.AddRectFilled(
                new Vector2(fMid - trackW * 0.5f, fTop),
                new Vector2(fMid + trackW * 0.5f, fBot),
                trackCol);

            // Knob position.
            float ky = fBot - target * (fBot - fTop);
            var c = faderColors[i];
            float hoverBoost = hovered && isInteractive ? 1.15f : 1.0f;

            // Soft halo - three concentric rects with decreasing
            // alpha give a CSS-blur feel without a real blur.
            for (int r = 3; r >= 1; r--)
            {
                float pad = r * 2.2f * scale;
                float a = (0.12f / r) * hoverBoost;
                uint halo = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, a));
                dl.AddRectFilled(
                    new Vector2(fMid - 7f * scale - pad, ky - 2.4f * scale - pad),
                    new Vector2(fMid + 7f * scale + pad, ky + 2.4f * scale + pad),
                    halo);
            }
            // Core knob.
            float kw = 7f * scale;
            float kh = 2.4f * scale;
            var knobCol = new Vector4(
                MathF.Min(1f, c.X * hoverBoost),
                MathF.Min(1f, c.Y * hoverBoost),
                MathF.Min(1f, c.Z * hoverBoost), 1f);
            dl.AddRectFilled(
                new Vector2(fMid - kw, ky - kh),
                new Vector2(fMid + kw, ky + kh),
                ImGui.ColorConvertFloat4ToU32(knobCol));
            // Bright top edge on the knob for a specular highlight.
            dl.AddLine(
                new Vector2(fMid - kw, ky - kh),
                new Vector2(fMid + kw, ky - kh),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.35f * hoverBoost)),
                1f);

            // Label under the fader - slightly brighter for the
            // interactive columns.
            string lbl = faderLabels[i];
            var lblSz = ImGui.CalcTextSize(lbl);
            var lblCol = isInteractive ? TextDim : TextFaint;
            if (hovered && isInteractive) lblCol = Accent;
            dl.AddText(
                new Vector2(fMid - lblSz.X * 0.5f, fBot + labelGap),
                ImGui.ColorConvertFloat4ToU32(lblCol), lbl);
        }

        // restore cursor before reserving banner footprint (fader-loop InvisibleButtons left it elsewhere)
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(1, bannerH));
    }

    // letter-spaced text (ImGui has no native letter-spacing)
    private static void DrawTrackedText(
        ImDrawListPtr dl, Vector2 pos, string text, uint color, float track)
    {
        float cx = pos.X;
        for (int i = 0; i < text.Length; i++)
        {
            var s = text.Substring(i, 1);
            dl.AddText(new Vector2(cx, pos.Y), color, s);
            cx += ImGui.CalcTextSize(s).X + track;
        }
    }

    // mini-fader tooltip styled like the console
    private void DrawFaderTooltip(string name, Vector4 accent, bool isInteractive)
    {
        var scale = UIStyles.Scale;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 10f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 4f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);
        // Tooltips render as ImGui windows under the hood, so
        // WindowRounding is what actually kills the rounded corners.
        // Push WindowBorderSize too for the accent border.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.035f, 0.042f, 0.063f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, 0.55f));

        ImGui.BeginTooltip();

        // Small accent left-bar + tracked-caps kicker row.
        var dl = ImGui.GetWindowDrawList();
        string kicker = isInteractive ? "SETTING" : "DECORATIVE";
        var ksPos = ImGui.GetCursorScreenPos();
        float barH = ImGui.GetTextLineHeight();
        dl.AddRectFilled(
            new Vector2(ksPos.X, ksPos.Y + 1f * scale),
            new Vector2(ksPos.X + 2f * scale, ksPos.Y + barH - 1f * scale),
            ImGui.ColorConvertFloat4ToU32(accent));
        ImGui.SetCursorScreenPos(new Vector2(ksPos.X + 8f * scale, ksPos.Y));
        DrawTrackedText(dl, new Vector2(ksPos.X + 8f * scale, ksPos.Y), kicker,
            ImGui.ColorConvertFloat4ToU32(isInteractive ? accent : TextFaint),
            1.5f * scale);
        float kickerW = MeasureTrackedWidth(kicker, 1.5f * scale) + 8f * scale;
        ImGui.Dummy(new Vector2(kickerW, barH));

        // interactive faders show name; decorative show dim explanation
        if (isInteractive)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Text);
            ImGui.TextUnformatted(name);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
            ImGui.TextUnformatted("Purely cosmetic - not wired to anything.");
            ImGui.PopStyleColor();
        }

        ImGui.EndTooltip();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(6);
    }

    // Width of a tracked string - needed by layout code that
    // positions something after tracked text.
    private static float MeasureTrackedWidth(string text, float track)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float w = 0f;
        for (int i = 0; i < text.Length; i++)
            w += ImGui.CalcTextSize(text.Substring(i, 1)).X;
        return w + track * MathF.Max(0, text.Length - 1);
    }

    // ================================================================
    // CONTENT - three channels
    // ================================================================
    private void DrawContent()
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null) return;

        //  CH 01 - EMOTES 
        DrawEmotesChannel(config);
        //  CH 02 - NOTIFICATIONS 
        DrawNotificationsChannel(config);
        //  CH 03 - DISPLAY 
        DrawDisplayChannel(config);
    }

    private void DrawEmotesChannel(Configuration config)
    {
        int armed = 0;
        if (config.AllowSitDozeAnywhere) armed++;
        if (config.AllowUnlockedEmotes)  armed++;

        BeginChannel("01", "EMOTES", SecEmotes, armed, 2);

        bool sitDoze = config.AllowSitDozeAnywhere;
        if (DrawSwitchRow("sitDoze", SecEmotes, ref sitDoze,
                "Allow Sit / Doze Anywhere",
                "Sit or doze without requiring nearby furniture. Uses the game's internal emote functions."))
        {
            config.AllowSitDozeAnywhere = sitDoze;
            config.Save();
        }
        if (sitDoze)
        {
            DrawCallout("HEADS UP",
                "This uses the same technique as DozeAnywhere. It sends position data to Square Enix's servers and may be detectable. Use at your own risk.",
                Warning);
        }

        DrawRowSeparator();

        bool allowUnlocked = config.AllowUnlockedEmotes;
        if (DrawSwitchRow("allEmotes", SecEmotes, ref allowUnlocked,
                "Allow All Emotes",
                "Play your modded animations regardless of whether you have the base emote unlocked. Works with a preset checkbox or /vanilla <emote>. Like other mods, animations may not show to other players the first time they're played."))
        {
            config.AllowUnlockedEmotes = allowUnlocked;
            config.Save();
        }

        EndChannel();
    }

    private void DrawNotificationsChannel(Configuration config)
    {
        int armed = 0;
        if (config.ShowPatchNotesOnStartup) armed++;
        if (config.ShowUpdateNotification)  armed++;

        BeginChannel("02", "NOTIFICATIONS", SecNotifications, armed, 2);

        bool showPatchNotes = config.ShowPatchNotesOnStartup;
        if (DrawSwitchRow("patchNotes", SecNotifications, ref showPatchNotes,
                "Show patch notes on update",
                "Opens the What's New window automatically after Encore updates."))
        {
            config.ShowPatchNotesOnStartup = showPatchNotes;
            config.Save();
        }

        DrawRowSeparator();

        bool showUpdateNotif = config.ShowUpdateNotification;
        if (DrawSwitchRow("updateNotif", SecNotifications, ref showUpdateNotif,
                "Notify of updates in chat",
                "Shows a chat message when a new version of Encore is available on the plugin repo."))
        {
            config.ShowUpdateNotification = showUpdateNotif;
            config.Save();
        }

        EndChannel();
    }

    private void DrawDisplayChannel(Configuration config)
    {
        int armed = config.IgnoreGlobalScale ? 1 : 0;

        BeginChannel("03", "DISPLAY", SecDisplay, armed, 1);

        bool ignoreScale = config.IgnoreGlobalScale;
        if (DrawSwitchRow("ignoreScale", SecDisplay, ref ignoreScale,
                "Ignore global font scale for window sizes",
                "For high-DPI users: keeps Encore windows at their base size even when Dalamud's global font scale is large. Fonts still scale normally - this only affects window dimensions so they fit smaller screens."))
        {
            config.IgnoreGlobalScale = ignoreScale;
            config.Save();
        }

        EndChannel();
    }

    // ================================================================
    // CHANNEL (section) - header + body frame
    // ================================================================
    private void BeginChannel(string num, string name, Vector4 accent,
                              int armedCount, int total)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        // right gutter symmetric to the left-edge WindowPadding gap
        float rightGutter = 10f * scale;
        var availW = ImGui.GetContentRegionAvail().X - rightGutter;
        float headerH = 36f * scale;
        var headerEnd = new Vector2(start.X + availW, start.Y + headerH);
        bool armed = armedCount > 0;

        // Header gradient fill (accent-tinted, fades to right).
        uint bgLeft = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.10f));
        uint bgRight = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.02f));
        dl.AddRectFilledMultiColor(start, headerEnd, bgLeft, bgRight, bgRight, bgLeft);

        // top/right/header-body hairlines (left is the 3px accent bar; EndChannel draws the rest)
        uint frameBorder = ImGui.ColorConvertFloat4ToU32(BorderSoft);
        dl.AddLine(start, new Vector2(headerEnd.X, start.Y), frameBorder, 1f);
        dl.AddLine(
            new Vector2(headerEnd.X, start.Y),
            new Vector2(headerEnd.X, headerEnd.Y),
            frameBorder, 1f);
        dl.AddLine(
            new Vector2(start.X, headerEnd.Y),
            new Vector2(headerEnd.X, headerEnd.Y),
            frameBorder, 1f);

        // Left accent bar (3px).
        float accentW = 3f * scale;
        dl.AddRectFilled(
            start, new Vector2(start.X + accentW, headerEnd.Y),
            ImGui.ColorConvertFloat4ToU32(accent));

        // Subtle mid-band behind the label (patch-notes parity).
        uint midBand = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.14f));
        uint midBandClr = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        float midY = start.Y + headerH * 0.5f;
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.18f, midY),
            new Vector2(start.X + availW * 0.50f, midY + 1f),
            midBandClr, midBand, midBand, midBandClr);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.50f, midY),
            new Vector2(start.X + availW * 0.82f, midY + 1f),
            midBand, midBandClr, midBandClr, midBand);

        // LED - ringed when disarmed, lit + breathing when armed.
        float ledSize = 8f * scale;
        float ledX = start.X + accentW + 10f * scale;
        float ledY = start.Y + (headerH - ledSize) * 0.5f;
        var ledMin = new Vector2(ledX, ledY);
        var ledMax = new Vector2(ledX + ledSize, ledY + ledSize);
        if (armed)
        {
            float breath = 0.78f + 0.22f * (0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * MathF.Tau / 2.4f));
            uint haloFar = ImGui.ColorConvertFloat4ToU32(
                new Vector4(accent.X, accent.Y, accent.Z, 0.08f * breath));
            uint haloNear = ImGui.ColorConvertFloat4ToU32(
                new Vector4(accent.X, accent.Y, accent.Z, 0.22f * breath));
            dl.AddRectFilled(
                new Vector2(ledX - 6f * UIStyles.Scale, ledY - 6f * UIStyles.Scale),
                new Vector2(ledX + ledSize + 6f * UIStyles.Scale, ledY + ledSize + 6f * UIStyles.Scale),
                haloFar);
            dl.AddRectFilled(
                new Vector2(ledX - 3f * UIStyles.Scale, ledY - 3f * UIStyles.Scale),
                new Vector2(ledX + ledSize + 3f * UIStyles.Scale, ledY + ledSize + 3f * UIStyles.Scale),
                haloNear);
            dl.AddRectFilled(ledMin, ledMax, ImGui.ColorConvertFloat4ToU32(accent));
        }
        else
        {
            dl.AddRectFilled(ledMin, ledMax,
                ImGui.ColorConvertFloat4ToU32(new Vector4(accent.X, accent.Y, accent.Z, 0.05f)));
            dl.AddRect(ledMin, ledMax, ImGui.ColorConvertFloat4ToU32(accent), 0f, 0, 1f);
        }

        var textH = ImGui.GetTextLineHeight();
        float labelY = start.Y + (headerH - textH) * 0.5f;

        // CH 0N numeral - drawn with tight manual tracking so the
        // mono feel reads cleanly without doubling the width.
        string chNum = "CH " + num;
        float chX = ledX + ledSize + 12f * scale;
        DrawTrackedText(dl, new Vector2(chX, labelY), chNum,
            ImGui.ColorConvertFloat4ToU32(accent), 1.2f * scale);
        float chW = MeasureTrackedWidth(chNum, 1.2f * scale);

        // Channel name - wider manual tracking for display feel.
        float nameX = chX + chW + 16f * scale;
        DrawTrackedText(dl, new Vector2(nameX, labelY), name.ToUpperInvariant(),
            ImGui.ColorConvertFloat4ToU32(Text), 2.8f * scale);

        // Right-side meter - "N / M  ARMED", armed count colored.
        string mArmed = $"{armedCount}";
        string mTail  = $" / {total}  ARMED";
        var mArmedSz = ImGui.CalcTextSize(mArmed);
        var mTailSz  = ImGui.CalcTextSize(mTail);
        float meterX = headerEnd.X - 12f * scale - (mArmedSz.X + mTailSz.X);
        dl.AddText(new Vector2(meterX, labelY),
            ImGui.ColorConvertFloat4ToU32(armed ? accent : TextFaint), mArmed);
        dl.AddText(new Vector2(meterX + mArmedSz.X, labelY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), mTail);

        // Advance past the header.
        ImGui.SetCursorScreenPos(new Vector2(start.X, headerEnd.Y));
        ImGui.Dummy(new Vector2(1, 0));

        // ch 1 = content, ch 0 = body bg/frame; merged so content sits on top
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        channelBodyStarts.Push((start.X, start.X + availW, ImGui.GetCursorScreenPos().Y));

        ImGui.Indent(16f * scale);
        ImGui.Dummy(new Vector2(1, 4f * scale));
    }

    private readonly Stack<(float leftX, float rightX, float bodyTopY)> channelBodyStarts = new();

    // channel right edge (or child's right if not inside a channel)
    private float CurrentChannelRight()
    {
        if (channelBodyStarts.Count > 0) return channelBodyStarts.Peek().rightX;
        return ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
    }

    private void EndChannel()
    {
        var scale = UIStyles.Scale;
        ImGui.Unindent(16f * scale);
        ImGui.Dummy(new Vector2(1, 10f * scale));

        var (leftX, rightX, bodyTopY) = channelBodyStarts.Pop();
        float bottomY = ImGui.GetCursorScreenPos().Y;

        var dl = ImGui.GetWindowDrawList();

        // Switch to background channel and paint the body fill +
        // frame lines BEHIND the content we just rendered.
        dl.ChannelsSetCurrent(0);

        uint bodyFill = ImGui.ColorConvertFloat4ToU32(new Vector4(0.078f, 0.086f, 0.110f, 1f));
        dl.AddRectFilled(
            new Vector2(leftX, bodyTopY),
            new Vector2(rightX, bottomY),
            bodyFill);

        uint frameBorder = ImGui.ColorConvertFloat4ToU32(BorderSoft);
        dl.AddLine(new Vector2(leftX,  bodyTopY), new Vector2(leftX,  bottomY), frameBorder, 1f);
        dl.AddLine(new Vector2(rightX, bodyTopY), new Vector2(rightX, bottomY), frameBorder, 1f);
        dl.AddLine(new Vector2(leftX,  bottomY), new Vector2(rightX, bottomY),  frameBorder, 1f);

        dl.ChannelsMerge();

        ImGui.Dummy(new Vector2(1, 10f * scale));
    }

    private void DrawRowSeparator()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(1, 2f * scale));
        var start = ImGui.GetCursorScreenPos();
        // Clamp the separator's right edge to the channel's right
        // border (minus a small inner gutter) so dashes don't run
        // past the body frame line.
        float rightEdge = CurrentChannelRight() - 14f * scale;
        float availW = MathF.Max(0f, rightEdge - start.X);
        uint col = ImGui.ColorConvertFloat4ToU32(
            new Vector4(0.30f, 0.33f, 0.40f, 0.85f));
        for (float dx = 0; dx < availW; dx += 7f * scale)
        {
            float segW = MathF.Min(4f * scale, availW - dx);
            dl.AddLine(
                new Vector2(start.X + dx, start.Y),
                new Vector2(start.X + dx + segW, start.Y),
                col, 1f);
        }
        ImGui.Dummy(new Vector2(1, 6f * scale));
    }

    // ================================================================
    // ROW (switch + label + desc)
    // ================================================================
    private bool DrawSwitchRow(string id, Vector4 color, ref bool value,
                                string label, string desc)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var rowStart = ImGui.GetCursorScreenPos();

        // Tighter, smaller switch - 44x18 reads clearly without
        // dominating the row. The knob+halo carry the ON/OFF state
        // so there's no need for a parallel "ON"/"OFF" text pill.
        float switchW = 44f * scale;
        float switchH = 18f * scale;
        float gap = 14f * scale;

        bool toggled = DrawSwitch($"##sw_{id}", color, ref value,
            new Vector2(rowStart.X, rowStart.Y + 2f * scale),
            new Vector2(switchW, switchH));

        float textX = rowStart.X + switchW + gap;
        // Width clamped by the channel's right border so wrapped
        // desc text stops well inside the body frame rather than
        // running past it into the gutter.
        float textRight = CurrentChannelRight() - 14f * scale;
        float availTextW = MathF.Max(80f * scale, textRight - textX);

        var labelCol = ImGui.ColorConvertFloat4ToU32(Text);
        dl.AddText(new Vector2(textX, rowStart.Y), labelCol, label);
        var labelSz = ImGui.CalcTextSize(label);

        ImGui.SetCursorScreenPos(new Vector2(textX, rowStart.Y + labelSz.Y + 4f * scale));
        float wrapLocalX = ImGui.GetCursorPosX() + availTextW;
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.TextWrapped(desc);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        float textBottom = ImGui.GetCursorScreenPos().Y;
        float switchBottom = rowStart.Y + switchH + 4f * scale;
        float rowEnd = MathF.Max(textBottom, switchBottom);
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X, rowEnd));
        ImGui.Dummy(new Vector2(1, 4f * scale));

        return toggled;
    }

    // Fader-style pill switch. Returns true on toggle.
    private bool DrawSwitch(string id, Vector4 color, ref bool value,
                             Vector2 pos, Vector2 size)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var io = ImGui.GetIO();

        // Invisible button for interaction.
        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();
        if (clicked)
        {
            value = !value;
            // Record a flare start so the ripple + kick-scale pick
            // up on this frame.
            switchFlareStartAt[id] = ImGui.GetTime();
        }

        // Damped-spring toward target - underdamped (zeta ~= 0.5) so the knob
        // overshoots by ~15% and settles, giving toggles a bouncy snap
        // instead of a linear glide.
        float target = value ? 1f : 0f;
        if (!switchAnim.TryGetValue(id, out var anim)) anim = target;
        if (!switchVel.TryGetValue(id, out var vel)) vel = 0f;
        float dt = io.DeltaTime > 0.05f ? 0.05f : io.DeltaTime;
        const float stiffness = 320f;
        const float damping = 18f;
        float dx = target - anim;
        vel += (dx * stiffness - vel * damping) * dt;
        anim += vel * dt;
        switchAnim[id] = anim;
        switchVel[id] = vel;

        // Halo bloom underneath when on - layered concentric rects
        // with decreasing alpha. Breathes subtly (+/-15%) so an armed
        // switch feels alive rather than statically lit.
        if (anim > 0.01f)
        {
            float breath = 0.85f + 0.15f * (0.5f + 0.5f * MathF.Sin(
                (float)ImGui.GetTime() * MathF.Tau / 2.6f));
            for (int r = 4; r >= 1; r--)
            {
                float pad = r * 2.5f * scale;
                float a = (0.18f / (r + 1)) * anim * breath;
                uint halo = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(color.X, color.Y, color.Z, a));
                dl.AddRectFilled(
                    new Vector2(pos.X - pad, pos.Y - pad),
                    new Vector2(pos.X + size.X + pad, pos.Y + size.Y + pad),
                    halo);
            }
        }

        // Track background - blends from neutral dark to accent-tint.
        var trackOff = new Vector4(Surface3.X, Surface3.Y, Surface3.Z, 1f);
        var trackOn  = new Vector4(
            color.X * 0.28f + Surface3.X * 0.72f,
            color.Y * 0.28f + Surface3.Y * 0.72f,
            color.Z * 0.28f + Surface3.Z * 0.72f, 1f);
        var trackOnRight = new Vector4(
            color.X * 0.14f + Surface3.X * 0.86f,
            color.Y * 0.14f + Surface3.Y * 0.86f,
            color.Z * 0.14f + Surface3.Z * 0.86f, 1f);
        uint trackL = ImGui.ColorConvertFloat4ToU32(
            Lerp(trackOff, trackOn, anim));
        uint trackR = ImGui.ColorConvertFloat4ToU32(
            Lerp(trackOff, trackOnRight, anim));
        dl.AddRectFilledMultiColor(
            pos, new Vector2(pos.X + size.X, pos.Y + size.Y),
            trackL, trackR, trackR, trackL);

        // Track border - neutral off, accent on.
        var borderCol = Lerp(Border, color, anim);
        if (hovered && !held) borderCol = Lerp(borderCol, AccentBright, 0.15f);
        dl.AddRect(pos, new Vector2(pos.X + size.X, pos.Y + size.Y),
            ImGui.ColorConvertFloat4ToU32(borderCol), 0f, 0, 1f);

        // no ON/OFF text: 18px track clips default font, no smaller alternative

        // knob: eased between off (left) and on (right)
        float knobW = 18f * scale;
        float knobH = size.Y - 4f * scale;
        float knobTravel = size.X - knobW - 4f * scale;
        float kx = pos.X + 2f * scale + knobTravel * anim;
        float ky = pos.Y + 2f * scale;
        var knobMin = new Vector2(kx, ky);
        var knobMax = new Vector2(kx + knobW, ky + knobH);

        // Knob fill - dark off, accent gradient on.
        var knobTop = Lerp(new Vector4(0.16f, 0.18f, 0.23f, 1f),
                           new Vector4(color.X * 1.10f, color.Y * 1.10f, color.Z * 1.10f, 1f), anim);
        var knobBot = Lerp(new Vector4(0.10f, 0.11f, 0.15f, 1f), color, anim);
        uint kTop = ImGui.ColorConvertFloat4ToU32(knobTop);
        uint kBot = ImGui.ColorConvertFloat4ToU32(knobBot);
        dl.AddRectFilledMultiColor(knobMin, knobMax, kTop, kTop, kBot, kBot);

        // Knob glow when on.
        if (anim > 0.01f)
        {
            float ga = anim * 0.55f;
            uint kGlow = ImGui.ColorConvertFloat4ToU32(
                new Vector4(color.X, color.Y, color.Z, ga));
            float gpad = 2f * scale;
            dl.AddRect(
                new Vector2(knobMin.X - gpad, knobMin.Y - gpad),
                new Vector2(knobMax.X + gpad, knobMax.Y + gpad),
                kGlow, 0f, 0, 1f);
        }

        // Knob border.
        var knobBorder = Lerp(new Vector4(0.23f, 0.25f, 0.31f, 1f),
                              new Vector4(color.X * 0.70f, color.Y * 0.70f, color.Z * 0.70f, 1f),
                              anim);
        dl.AddRect(knobMin, knobMax,
            ImGui.ColorConvertFloat4ToU32(knobBorder), 0f, 0, 1f);

        // Knob grip lines (three vertical hairlines in the middle).
        uint gripCol = ImGui.ColorConvertFloat4ToU32(
            anim > 0.5f
                ? new Vector4(AccentDark.X, AccentDark.Y, AccentDark.Z, 0.55f)
                : new Vector4(Accent.X, Accent.Y, Accent.Z, 0.25f));
        float gMidY0 = ky + knobH * 0.30f;
        float gMidY1 = ky + knobH * 0.70f;
        float gCx = kx + knobW * 0.5f;
        float gGap = 2f * scale;
        for (int i = -1; i <= 1; i++)
        {
            float gx = gCx + i * gGap;
            dl.AddLine(new Vector2(gx, gMidY0), new Vector2(gx, gMidY1), gripCol, 1f);
        }

        // expanding ring, easeOutCubic, fades as it grows
        if (switchFlareStartAt.TryGetValue(id, out var flareStart))
        {
            float elapsed = (float)(ImGui.GetTime() - flareStart);
            float tRaw = MathF.Min(1f, elapsed / (float)SwitchFlareSec);
            if (tRaw >= 1f)
            {
                switchFlareStartAt.Remove(id);
            }
            else
            {
                float te = 1f - MathF.Pow(1f - tRaw, 3f);  // easeOutCubic
                var ctr = new Vector2(pos.X + size.X * 0.5f, pos.Y + size.Y * 0.5f);
                float baseR = size.Y * 0.55f;
                float rippleR = baseR + te * size.X * 1.35f;
                float rippleA = (1f - te) * 0.55f;
                if (rippleA > 0.01f)
                {
                    dl.AddCircle(ctr, rippleR,
                        ImGui.ColorConvertFloat4ToU32(
                            new Vector4(color.X, color.Y, color.Z, rippleA)),
                        28, 1.5f);
                    // faint inner ring for depth (switch is wider than the guide's dot)
                    float innerR = baseR + te * size.X * 0.85f;
                    float innerA = (1f - te) * 0.30f;
                    dl.AddCircle(ctr, innerR,
                        ImGui.ColorConvertFloat4ToU32(
                            new Vector4(color.X, color.Y, color.Z, innerA)),
                        28, 1.0f);
                }
            }
        }

        return clicked;
    }

    // ================================================================
    // CALLOUT - amber HEADS-UP block
    // ================================================================
    private void DrawCallout(string kicker, string body, Vector4 color)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        ImGui.Dummy(new Vector2(1, 3f * scale));

        var start = ImGui.GetCursorScreenPos();
        // Callout stops at the channel's right border - same gutter
        // as the row separator so everything lines up inside the
        // body frame.
        float calloutRight = CurrentChannelRight() - 14f * scale;
        float availW = MathF.Max(120f * scale, calloutRight - start.X);

        // Very tight - inline "kicker: body" instead of a stacked
        // kicker row + body. Reads as a one-or-two-line footnote
        // rather than a multi-paragraph warning block.
        float iconSize = 12f * scale;
        float innerPadX = 6f * scale;
        float innerPadY = 4f * scale;
        float leftBarW = 2f * scale;

        float iconX = start.X + leftBarW + innerPadX;
        float textY = start.Y + innerPadY;
        // tile centered on kicker midline; text line-height (18px) > tile (12px)
        float textLineH = ImGui.GetTextLineHeight();
        float iconY = textY + MathF.Max(0f, (textLineH - iconSize) * 0.5f);
        var iconMin = new Vector2(iconX, iconY);
        var iconMax = new Vector2(iconX + iconSize, iconY + iconSize);

        float textX = iconMax.X + 7f * scale;
        float textMaxX = start.X + availW - innerPadX;
        float textMaxW = textMaxX - textX;

        // "HEADS UP - body" inline; tile shifts not text
        ImGui.SetCursorScreenPos(new Vector2(textX, textY));
        float wrapLocalX = ImGui.GetCursorPosX() + textMaxW;

        // Zero ItemSpacing.Y so wrapped lines pack tight.
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f));
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextUnformatted(kicker);
        ImGui.PopStyleColor();
        ImGui.SameLine(0, 6f * scale);
        ImGui.PushStyleColor(ImGuiCol.Text, Text);
        ImGui.TextWrapped(body);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
        ImGui.PopStyleVar();

        float bodyBottom = ImGui.GetCursorScreenPos().Y;
        float calloutH = MathF.Max(bodyBottom - start.Y, iconMax.Y - start.Y) + innerPadY;

        var calloutMin = start;
        var calloutMax = new Vector2(start.X + availW, start.Y + calloutH);

        uint fill = ImGui.ColorConvertFloat4ToU32(
            new Vector4(color.X, color.Y, color.Z, 0.08f));
        uint border = ImGui.ColorConvertFloat4ToU32(
            new Vector4(color.X, color.Y, color.Z, 0.34f));
        dl.AddRectFilled(calloutMin, calloutMax, fill);
        dl.AddRect(calloutMin, calloutMax, border, 0f, 0, 1f);
        dl.AddRectFilled(
            calloutMin, new Vector2(calloutMin.X + leftBarW, calloutMax.Y),
            ImGui.ColorConvertFloat4ToU32(color));

        dl.AddRectFilled(iconMin, iconMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.14f)));
        dl.AddRect(iconMin, iconMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 0.55f)),
            0f, 0, 1f);
        string ex = "!";
        var exSz = ImGui.CalcTextSize(ex);
        dl.AddText(
            new Vector2(iconX + (iconSize - exSz.X) * 0.5f,
                        iconY + (iconSize - exSz.Y) * 0.5f),
            ImGui.ColorConvertFloat4ToU32(color), ex);

        ImGui.SetCursorScreenPos(new Vector2(start.X, calloutMax.Y));
        ImGui.Dummy(new Vector2(1, 4f * scale));
    }

    // ================================================================
    // FOOTER - status line + Done button
    // ================================================================
    private void DrawFooter()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        float footerH = 48f * scale;
        var end = new Vector2(start.X + availW, start.Y + footerH);

        // Background.
        uint bgCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(0.031f, 0.039f, 0.055f, 1f));
        dl.AddRectFilled(start, end, bgCol);

        // Top divider line.
        dl.AddLine(start, new Vector2(end.X, start.Y),
            ImGui.ColorConvertFloat4ToU32(Border), 1f);

        float padX = 18f * scale;
        float contentY = start.Y + footerH * 0.5f;
        var textH = ImGui.GetTextLineHeight();
        float textY = contentY - textH * 0.5f;

        // status anchored left; rainbow EQ rises above divider at same anchor
        var config = Plugin.Instance?.Configuration;
        int armed = ArmedSwitchCount(config);

        string lbl = "STATUS";
        var lblSz = ImGui.CalcTextSize(lbl);
        float statusLeft = start.X + padX;
        dl.AddText(new Vector2(statusLeft, textY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), lbl);

        // "  -  " separator then the count split into accent "XX"
        // and dim " - 05 CHANNELS ARMED" so the armed number reads
        // as the foregrounded datum (matching the ribbon counter).
        string valPre  = "  -  ";
        string valNum  = $"{armed}";
        string valPost = $"  -  5  CHANNELS  ARMED";
        var valPreSz = ImGui.CalcTextSize(valPre);
        var valNumSz = ImGui.CalcTextSize(valNum);
        float vX = statusLeft + lblSz.X;
        dl.AddText(new Vector2(vX, textY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), valPre);
        dl.AddText(new Vector2(vX + valPreSz.X, textY),
            ImGui.ColorConvertFloat4ToU32(armed > 0 ? Accent : TextDim), valNum);
        dl.AddText(new Vector2(vX + valPreSz.X + valNumSz.X, textY),
            ImGui.ColorConvertFloat4ToU32(TextDim), valPost);

        // drawn after status (so glow isn't obscured) but positioned above the text row
        float t = (float)ImGui.GetTime();
        float eqAnchorY = start.Y;
        float[] baseH = { 0.40f, 0.60f, 0.75f, 0.85f, 0.70f, 0.55f, 0.42f, 0.30f };
        float[] phases = { 0f, 0.12f, 0.24f, 0.36f, 0.48f, 0.36f, 0.24f, 0.12f };
        float eqBarW = 2f * scale;
        float eqGap = 3f * scale;
        float eqX = start.X + padX;
        float eqMaxH = 10f * scale;
        for (int i = 0; i < 8; i++)
        {
            float pulse = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin((t + phases[i]) * MathF.Tau / 2.8f));
            float h = baseH[i] * eqMaxH * pulse;
            float bx = eqX + i * (eqBarW + eqGap);
            var c = RainbowPalette[i];
            uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, 0.60f));
            dl.AddRectFilled(
                new Vector2(bx, eqAnchorY - h),
                new Vector2(bx + eqBarW, eqAnchorY),
                col);
        }

        // Done button - snug padding around the "DONE" label
        // rather than a wide bar. Text size unchanged.
        float btnW = 76f * scale;
        float btnH = 26f * scale;
        float btnX = end.X - padX - btnW;
        float btnY = contentY - btnH * 0.5f;

        var btnMin = new Vector2(btnX, btnY);
        var btnMax = new Vector2(btnX + btnW, btnY + btnH);
        UIStyles.DrawPlayButtonBloom(dl, btnMin, btnMax, scale, Accent);

        ImGui.SetCursorScreenPos(btnMin);
        if (UIStyles.DrawPlayButton("##settingsDone",
                new Vector2(btnW, btnH), 1f, scale,
                label: "DONE",
                restCol: Accent,
                hoverCol: AccentBright,
                heldCol: AccentDeep,
                borderCol: AccentDeep,
                textColor: AccentDark))
        {
            IsOpen = false;
        }
    }

    // ================================================================
    // WINDOW CORNER BRACKETS - bottom-left and bottom-right.
    // ================================================================
    private void DrawWindowCornerBrackets()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        float armLen = 14f * scale;
        float inset = 6f * scale;
        uint col = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.50f));
        float left = winPos.X + inset;
        float right = winPos.X + winSize.X - inset;
        float bottom = winPos.Y + winSize.Y - inset;
        dl.AddLine(new Vector2(left, bottom - armLen), new Vector2(left, bottom), col, 1f);
        dl.AddLine(new Vector2(left, bottom), new Vector2(left + armLen, bottom), col, 1f);
        dl.AddLine(new Vector2(right - armLen, bottom), new Vector2(right, bottom), col, 1f);
        dl.AddLine(new Vector2(right, bottom - armLen), new Vector2(right, bottom), col, 1f);
    }

    // ================================================================
    // UTILITIES
    // ================================================================
    private static Vector4 Lerp(Vector4 a, Vector4 b, float t) =>
        new(a.X + (b.X - a.X) * t,
            a.Y + (b.Y - a.Y) * t,
            a.Z + (b.Z - a.Z) * t,
            a.W + (b.W - a.W) * t);

    // ImGui has no letter-spacing control, so we approximate tracked
    // uppercase by interleaving spaces between characters. Used for
    // the CHANNEL name labels ("E M O T E S" etc.).
    private static string SpaceOut(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var chars = text.ToUpperInvariant().ToCharArray();
        return string.Join(" ", chars);
    }
}
