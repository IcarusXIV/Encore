using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Encore.Styles;

namespace Encore.Windows;

public class PatchNotesWindow : Window
{
    private readonly Plugin plugin;
    private bool hasScrolledToEnd = false;
    private bool wasOpen = false;

    // Section collapse state (latest open, older collapsed).
    private bool v1006Open = true;
    private bool v1005Open = false;
    private bool v100Open = false;

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
    private static readonly Vector4 Success      = new(0.45f, 0.92f, 0.55f, 1f);

    // Deep backdrop - the window fills with this so content feels inset
    // against a darker stage.
    private static readonly Vector4 WindowBg     = new(0.020f, 0.024f, 0.035f, 1f);
    private static readonly Vector4 ContentBg    = new(0.047f, 0.055f, 0.075f, 1f);

    // 8-step rainbow - same palette used by MainWindow.DrawTopBarWaveform.
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

    // Play-activation burst - same effect the main window uses when a
    // preset is played. Tracks the click timestamp; draw code runs while
    // elapsed <= 0.7s then stops by itself.
    private double markAsReadClickTime = -1;
    private Vector2 markAsReadClickCenter;
    private bool pendingClose = false;
    private double pendingCloseAt = 0;

    // Scroll state cached from the content child so the footer can read it.
    private float scrollPercent = 0f;

    // Banner wordmark fade-in. Dalamud's custom fonts build async, so we
    // skip rendering the wordmark until the font is ready, then ease it
    // in over ~400ms instead of popping in at full alpha.
    private float bannerFadeAlpha = 0f;

    public PatchNotesWindow(Plugin plugin) : base("Encore - What's New###EncorePatchNotes")
    {
        this.plugin = plugin;
        SizeCondition = ImGuiCond.Always;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
              | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.WindowScale;
        Size = new Vector2(560f * scale, 640f * scale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560f * scale, 640f * scale),
            MaximumSize = new Vector2(560f * scale, 640f * scale),
        };
        UIStyles.PushEncoreWindow();
        // Push a darker window bg so chrome regions stand out against it.
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

            if (IsOpen && !wasOpen)
            {
                hasScrolledToEnd = false;
                scrollPercent = 0f;   // reset progress meter on fresh open
            }
            wasOpen = IsOpen;

            if (pendingClose && ImGui.GetTime() >= pendingCloseAt)
            {
                plugin.Configuration.LastSeenPatchNotesVersion = Plugin.PatchNotesVersion;
                plugin.Configuration.Save();
                IsOpen = false;
                pendingClose = false;
                if (Plugin.Instance != null)
                {
                    var main = Plugin.Instance.WindowSystem.Windows
                        .OfType<MainWindow>().FirstOrDefault();
                    if (main != null && !main.IsOpen) main.IsOpen = true;
                }
            }

            DrawRibbon();
            DrawBanner();

            var footerH = 58f * scale;
            var contentH = ImGui.GetContentRegionAvail().Y - footerH;

            // Content surface - explicit bg fill so it feels inset, darker
            // than the ribbon but lighter than true black.
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ContentBg);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, 5f * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(28f * scale, 16f * scale));
            if (ImGui.BeginChild("##patchContent",
                    new Vector2(ImGui.GetContentRegionAvail().X, contentH), false))
            {
                DrawVersion1006Notes();
                DrawVersion1005Notes();
                DrawVersion100Notes();

                var scrollY = ImGui.GetScrollY();
                var maxScrollY = ImGui.GetScrollMaxY();
                if (maxScrollY > 0)
                {
                    // Watermark - never decreases within an open session.
                    // Once the meter reaches a value it stays at least that
                    // high, even if the user scrolls back up.
                    float raw = MathF.Min(1f, scrollY / maxScrollY);
                    if (raw > scrollPercent) scrollPercent = raw;
                    if (scrollY >= maxScrollY * 0.85f)
                        hasScrolledToEnd = true;
                }
                else
                {
                    scrollPercent = 1f;
                    hasScrolledToEnd = true;
                }
            }
            ImGui.EndChild();
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor();

            DrawFooter();
            DrawWindowCornerBrackets();
            // Play-activation burst renders on the foreground drawlist so
            // it sits on top of the button + any surrounding chrome.
            if (markAsReadClickTime >= 0)
            {
                float elapsed = (float)(ImGui.GetTime() - markAsReadClickTime);
                if (elapsed > 0.7f) markAsReadClickTime = -1;
                else
                    UIStyles.DrawPlayActivation(
                        ImGui.GetForegroundDrawList(),
                        markAsReadClickCenter, elapsed, UIStyles.Scale,
                        color: Accent);
            }
        }
        finally
        {
            ImGui.PopStyleVar(2);
            UIStyles.PopEncoreContent();
            UIStyles.PopMainWindowStyle();
        }
    }

    // ================================================================
    // RIBBON
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

        float ruleH = 1f;
        uint aSolid = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.55f));
        uint aClear = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            start, new Vector2(start.X + availW * 0.42f, start.Y + ruleH),
            aSolid, aClear, aClear, aSolid);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.58f, start.Y),
            new Vector2(end.X, start.Y + ruleH),
            aClear, aSolid, aSolid, aClear);

        uint aSoft = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.30f));
        uint aSoftClr = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, end.Y - ruleH),
            new Vector2(start.X + availW * 0.5f, end.Y),
            aSoftClr, aSoft, aSoft, aSoftClr);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.5f, end.Y - ruleH),
            end,
            aSoft, aSoftClr, aSoftClr, aSoft);

        // 3-bar mini-EQ pip.
        float padX = 14f * scale;
        float pipBoxH = 14f * scale;
        float pipX = start.X + padX;
        float pipBottomY = start.Y + (ribbonH + pipBoxH) * 0.5f;
        float t = (float)ImGui.GetTime();
        float barW = 2f * scale;
        float barGap = 2f * scale;
        float[] baseH = { 0.50f, 0.85f, 0.65f };
        float[] phase = { 0f, 0.22f, 0.44f };
        for (int i = 0; i < 3; i++)
        {
            float pulse = 0.30f + 0.70f * (0.5f + 0.5f * MathF.Sin((t + phase[i]) * MathF.Tau / 1.1f));
            float h = baseH[i] * pipBoxH * pulse;
            float x = pipX + i * (barW + barGap);
            dl.AddRectFilled(
                new Vector2(x, pipBottomY - h),
                new Vector2(x + barW, pipBottomY),
                ImGui.ColorConvertFloat4ToU32(Accent));
        }

        // Meta label + date.
        var textH = ImGui.GetTextLineHeight();
        float textY = start.Y + (ribbonH - textH) * 0.5f;
        float metaX = pipX + 3 * (barW + barGap) + 12f * scale;
        string label = "PATCH NOTES";
        string sep = "  -  ";
        string date = DateTime.Today.ToString("dd MMM yyyy").ToUpperInvariant();
        var labelSz = ImGui.CalcTextSize(label);
        var sepSz = ImGui.CalcTextSize(sep);
        dl.AddText(new Vector2(metaX, textY), ImGui.ColorConvertFloat4ToU32(Text), label);
        dl.AddText(new Vector2(metaX + labelSz.X, textY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), sep);
        dl.AddText(new Vector2(metaX + labelSz.X + sepSz.X, textY),
            ImGui.ColorConvertFloat4ToU32(TextDim), date);

        // Version tag.
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

    // ================================================================
    // BANNER - big ENCORE wordmark + soft EQ + radial spotlight
    // ================================================================
    private void DrawBanner()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var bannerH = 148f * scale;
        var end = new Vector2(start.X + availW, start.Y + bannerH);
        float t = (float)ImGui.GetTime();

        // Background: darker, with radial vignette-ish feel (just a soft
        // gradient for the sweep to sit on).
        uint bgTop = ImGui.ColorConvertFloat4ToU32(new Vector4(0.055f, 0.067f, 0.094f, 1f));
        uint bgBot = ImGui.ColorConvertFloat4ToU32(new Vector4(0.020f, 0.027f, 0.039f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        // 2 slow drifting concentric-circle spotlights, 18-22s periods
        void Spotlight(float period, float phase, float xFracBase, float xFracRange,
                       float yFrac, float maxR, float peakAlpha)
        {
            float st = 0.5f + 0.5f * MathF.Sin((t + phase) * MathF.Tau / period);
            float cx = start.X + availW * (xFracBase + xFracRange * st);
            float cy = start.Y + bannerH * yFrac;
            const int layers = 18;
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
                    48);
            }
        }
        // Two lights - different speeds, positions, and max sizes so they
        // don't lock-step. Opposite horizontal directions for cross-sway.
        Spotlight(period: 22f, phase: 0f,   xFracBase: 0.18f, xFracRange: 0.50f,
                  yFrac: 0.28f, maxR: 130f * scale, peakAlpha: 0.022f);
        Spotlight(period: 18f, phase: 7f,   xFracBase: 0.30f, xFracRange: 0.44f,
                  yFrac: 0.55f, maxR: 110f * scale, peakAlpha: 0.016f);
        Spotlight(period: 28f, phase: 14f,  xFracBase: 0.40f, xFracRange: 0.30f,
                  yFrac: 0.15f, maxR: 90f * scale,  peakAlpha: 0.012f);

        // Bottom hairline - solid middle, fade edges.
        float ruleH = 1f;
        uint hSolid = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0.85f));
        uint hClear = ImGui.ColorConvertFloat4ToU32(new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, end.Y - ruleH),
            new Vector2(start.X + availW * 0.5f, end.Y),
            hClear, hSolid, hSolid, hClear);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.5f, end.Y - ruleH),
            end,
            hSolid, hClear, hClear, hSolid);
        // Soft accent glow just under the hairline to give it presence.
        uint glowTop = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.12f));
        uint glowBot = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.22f, end.Y),
            new Vector2(start.X + availW * 0.78f, end.Y + 3f * scale),
            glowTop, glowTop, glowBot, glowBot);

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

        // bottom-edge bars; column count from banner width for edge-to-edge span
        float eqColW = 3f * scale;
        float eqColGap = 5f * scale;
        float eqInset = 8f * scale;
        float eqFullW = availW - eqInset * 2;
        int eqCols = (int)MathF.Floor((eqFullW + eqColGap) / (eqColW + eqColGap));
        if (eqCols < 12) eqCols = 12;
        float eqAreaW = eqCols * eqColW + (eqCols - 1) * eqColGap;
        float eqStartX = start.X + (availW - eqAreaW) * 0.5f;
        float eqBottomY = end.Y - 2f * scale; // sit against the hairline
        float eqMaxH = 64f * scale;
        for (int i = 0; i < eqCols; i++)
        {
            // Envelope: bell curve across columns (peaks at middle).
            float envFrac = (float)(i + 0.5f) / eqCols;
            float envelope = MathF.Pow(MathF.Sin(envFrac * MathF.PI), 3f);

            // Per-col hashed phase/duration so adjacent bars don't sync.
            uint hA = (uint)(i * 2654435761u);
            uint hB = (uint)(i * 40503u + 17u);
            float dur = 1.60f + ((hA >> 8) & 0xFFFF) / 65535f * 0.40f;
            float phaseOff = ((hB >> 8) & 0xFFFF) / 65535f * 2.0f;
            float cycle = ((t + phaseOff) % dur) / dur;
            float ease = 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
            float bounce = 0.30f + 0.70f * ease;
            float h = eqMaxH * envelope * bounce;
            if (h < 2f) continue;

            // Palette cycles slowly across columns so it reads as a rainbow
            // wave rather than fixed bins.
            float paletteSlide = (t / 8f) % 1f;
            float paletteIdx = ((i / (float)eqCols + paletteSlide) % 1f) * 8f;
            int p0 = (int)MathF.Floor(paletteIdx) % 8;
            int p1 = (p0 + 1) % 8;
            float lerp = paletteIdx - MathF.Floor(paletteIdx);
            var a0 = RainbowPalette[p0];
            var a1 = RainbowPalette[p1];
            var c = new Vector4(
                a0.X + (a1.X - a0.X) * lerp,
                a0.Y + (a1.Y - a0.Y) * lerp,
                a0.Z + (a1.Z - a0.Z) * lerp,
                1f);

            float x = eqStartX + i * (eqColW + eqColGap);
            // Soft outward halo - two rings at decreasing alpha.
            for (int g = 2; g >= 1; g--)
            {
                float pad = g * 1.5f * scale;
                float a = 0.10f / g;
                dl.AddRectFilled(
                    new Vector2(x - pad, eqBottomY - h - pad),
                    new Vector2(x + eqColW + pad, eqBottomY + pad),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, a)));
            }
            // Core bar at soft transparency.
            dl.AddRectFilled(
                new Vector2(x, eqBottomY - h),
                new Vector2(x + eqColW, eqBottomY),
                ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, 0.32f)));
        }

        // skip until BannerFont/HeaderFont is ready (default-font render would pop on size snap)
        const string wordmark = "ENCORE";
        var bannerFont = Plugin.Instance?.BannerFont;
        var headerFont = Plugin.Instance?.HeaderFont;
        IFontHandle? font = null;
        if (bannerFont is { Available: true }) font = bannerFont;
        else if (headerFont is { Available: true }) font = headerFont;
        bool hasCustomFont = font != null;

        float trackSpacing = 10f * scale;
        var letterWidths = new float[wordmark.Length];
        float totalLetterW = 0f;
        float maxLetterH = 0f;
        void Measure()
        {
            totalLetterW = 0f;
            maxLetterH = 0f;
            for (int i = 0; i < wordmark.Length; i++)
            {
                var sz = ImGui.CalcTextSize(wordmark[i].ToString());
                letterWidths[i] = sz.X;
                totalLetterW += sz.X;
                if (sz.Y > maxLetterH) maxLetterH = sz.Y;
            }
        }
        if (hasCustomFont) { using (font!.Push()) Measure(); }
        float trackedW = totalLetterW + trackSpacing * (wordmark.Length - 1);

        // Subtitle sits above the wordmark as a kicker -"WHAT'S NEW IN
        // ENCORE" reads as one phrase. Measured up front so we know how
        // wide the tick row needs to be.
        string subtitle = "WHAT'S  NEW  IN";
        var subSz = ImGui.CalcTextSize(subtitle);
        float subY = start.Y + 20f * scale;

        // Wordmark - original centered position in the banner.
        float wmY = start.Y + (bannerH - maxLetterH) * 0.5f - 10f * scale;
        float wmX = start.X + (availW - trackedW) * 0.5f;

        void DrawWordmark()
        {
            // Advance the fade timer - reaches 1.0 over ~400ms once the
            // font is ready.
            bannerFadeAlpha = MathF.Min(1f,
                bannerFadeAlpha + ImGui.GetIO().DeltaTime / 0.40f);
            float fade = bannerFadeAlpha;

            // letters rendered many times at small offsets in 2 concentric rings (soft-blur fake)
            var glowColA = new Vector4(Accent.X, Accent.Y, Accent.Z, 0.08f * fade);
            var glowColB = new Vector4(Accent.X, Accent.Y, Accent.Z, 0.035f * fade);
            uint uA = ImGui.ColorConvertFloat4ToU32(glowColA);
            uint uB = ImGui.ColorConvertFloat4ToU32(glowColB);

            float cursor = wmX;
            for (int i = 0; i < wordmark.Length; i++)
            {
                var s = wordmark[i].ToString();

                // Inner ring - 10 samples at small radius, brighter.
                int innerCount = 10;
                float innerR = 2.2f * scale;
                for (int a = 0; a < innerCount; a++)
                {
                    float ang = (a / (float)innerCount) * MathF.Tau;
                    dl.AddText(
                        new Vector2(cursor + MathF.Cos(ang) * innerR,
                                    wmY + MathF.Sin(ang) * innerR),
                        uA, s);
                }
                // Outer ring - 16 samples at larger radius, dimmer.
                int outerCount = 16;
                float outerR = 5f * scale;
                for (int a = 0; a < outerCount; a++)
                {
                    float ang = (a / (float)outerCount) * MathF.Tau;
                    dl.AddText(
                        new Vector2(cursor + MathF.Cos(ang) * outerR,
                                    wmY + MathF.Sin(ang) * outerR),
                        uB, s);
                }

                cursor += letterWidths[i] + trackSpacing;
            }

            // Core letters on top - alpha fades in from 0 to 1.
            var coreCol = new Vector4(Text.X, Text.Y, Text.Z, Text.W * fade);
            uint coreU = ImGui.ColorConvertFloat4ToU32(coreCol);
            cursor = wmX;
            for (int i = 0; i < wordmark.Length; i++)
            {
                var s = wordmark[i].ToString();
                dl.AddText(new Vector2(cursor, wmY), coreU, s);
                cursor += letterWidths[i] + trackSpacing;
            }
        }
        // Draw the subtitle FIRST (above the wordmark), then the wordmark
        // on top - the wordmark's glow halos sit over the banner bg, not
        // over the subtitle, so the subtitle stays crisp.
        float tickLen = 22f * scale;
        float tickGap = 10f * scale;
        float subTotalW = tickLen + tickGap + subSz.X + tickGap + tickLen;
        float subX = start.X + (availW - subTotalW) * 0.5f;

        uint tickCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.70f));
        dl.AddLine(
            new Vector2(subX, subY + subSz.Y * 0.5f),
            new Vector2(subX + tickLen, subY + subSz.Y * 0.5f),
            tickCol, 1f);
        dl.AddText(
            new Vector2(subX + tickLen + tickGap, subY),
            ImGui.ColorConvertFloat4ToU32(TextDim), subtitle);
        dl.AddLine(
            new Vector2(subX + tickLen + tickGap + subSz.X + tickGap,
                        subY + subSz.Y * 0.5f),
            new Vector2(subX + subTotalW, subY + subSz.Y * 0.5f),
            tickCol, 1f);

        // Wordmark drawn on top of everything else in the banner.
        // Only rendered once the custom banner font is ready; otherwise
        // skipped for the brief loading window to prevent size-pop.
        if (hasCustomFont) { using (font!.Push()) DrawWordmark(); }

        ImGui.Dummy(new Vector2(1, bannerH));
    }

    // ================================================================
    // SECTION CARD
    // ================================================================
    private bool DrawPatchSection(string id, string version, string title,
                                   bool isLatest, ref bool open)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var headerH = 34f * scale;
        var end = new Vector2(start.X + availW, start.Y + headerH);

        ImGui.SetCursorScreenPos(start);
        if (ImGui.InvisibleButton($"##secBtn_{id}", new Vector2(availW, headerH)))
            open = !open;
        bool hovered = ImGui.IsItemHovered();

        float bgLeftA = open ? (hovered ? 0.22f : 0.14f) : (hovered ? 0.10f : 0.05f);
        float bgRightA = open ? 0.03f : 0f;
        uint bgLeft = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, bgLeftA));
        uint bgRight = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, bgRightA));
        dl.AddRectFilledMultiColor(start, end, bgLeft, bgRight, bgRight, bgLeft);

        var borderCol = open
            ? new Vector4(Accent.X, Accent.Y, Accent.Z, 0.40f)
            : new Vector4(Border.X, Border.Y, Border.Z, 1f);
        if (hovered && open)
            borderCol = new Vector4(Accent.X, Accent.Y, Accent.Z, 0.60f);
        dl.AddRect(start, end, ImGui.ColorConvertFloat4ToU32(borderCol), 0f, 0, 1f);

        // Left accent bar (3px).
        float accentBarW = 3f * scale;
        var accentCol = open ? Accent :
            (hovered ? AccentDeep : new Vector4(TextFaint.X, TextFaint.Y, TextFaint.Z, 1f));
        dl.AddRectFilled(
            start, new Vector2(start.X + accentBarW, end.Y),
            ImGui.ColorConvertFloat4ToU32(accentCol));

        // Subtle horizontal "::before" decoration - a thin mid-line band
        // behind the label, matching the HTML's material feel.
        uint midBand = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, open ? 0.18f : 0.08f));
        uint midBandClr = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0f));
        float midY = start.Y + headerH * 0.5f;
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.12f, midY),
            new Vector2(start.X + availW * 0.50f, midY + 1f),
            midBandClr, midBand, midBand, midBandClr);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.50f, midY),
            new Vector2(start.X + availW * 0.88f, midY + 1f),
            midBand, midBandClr, midBandClr, midBand);

        // Chevron.
        float chevX = start.X + accentBarW + 12f * scale;
        float chevY = start.Y + headerH * 0.5f;
        float chevSize = 5f * scale;
        var chevColV = open ? Accent : TextFaint;
        uint chevColU = ImGui.ColorConvertFloat4ToU32(chevColV);
        if (open)
        {
            dl.AddLine(
                new Vector2(chevX - chevSize, chevY - chevSize * 0.5f),
                new Vector2(chevX, chevY + chevSize * 0.6f),
                chevColU, 1.5f);
            dl.AddLine(
                new Vector2(chevX, chevY + chevSize * 0.6f),
                new Vector2(chevX + chevSize, chevY - chevSize * 0.5f),
                chevColU, 1.5f);
        }
        else
        {
            dl.AddLine(
                new Vector2(chevX - chevSize * 0.5f, chevY - chevSize),
                new Vector2(chevX + chevSize * 0.6f, chevY),
                chevColU, 1.5f);
            dl.AddLine(
                new Vector2(chevX + chevSize * 0.6f, chevY),
                new Vector2(chevX - chevSize * 0.5f, chevY + chevSize),
                chevColU, 1.5f);
        }

        var textH = ImGui.GetTextLineHeight();
        float labelY = start.Y + (headerH - textH) * 0.5f;

        // Version in accent mono feel.
        string verText = version;
        var verSz = ImGui.CalcTextSize(verText);
        float verX = chevX + chevSize + 10f * scale;
        var verCol = open ? Accent : TextDim;
        dl.AddText(new Vector2(verX, labelY),
            ImGui.ColorConvertFloat4ToU32(verCol), verText);

        // LATEST pill (right-aligned) - reserve its space before truncating the title.
        float rightReserve = 8f * scale;
        if (isLatest)
        {
            string pill = "LATEST";
            var pillSz = ImGui.CalcTextSize(pill);
            float pillPadX = 7f * scale;
            float pillPadY = 2f * scale;
            float pillW = pillSz.X + pillPadX * 2;
            float pillH = pillSz.Y + pillPadY * 2;
            float pillRight = end.X - 10f * scale;
            float pillLeft = pillRight - pillW;
            float pillTop = start.Y + (headerH - pillH) * 0.5f;
            float pillBot = pillTop + pillH;
            dl.AddRectFilled(
                new Vector2(pillLeft, pillTop), new Vector2(pillRight, pillBot),
                ImGui.ColorConvertFloat4ToU32(Accent));
            dl.AddText(
                new Vector2(pillLeft + pillPadX, pillTop + pillPadY),
                ImGui.ColorConvertFloat4ToU32(AccentDark), pill);
            rightReserve = (end.X - pillLeft) + 10f * scale;
        }

        // Title (uppercase, TRUNCATED with ellipsis if it would overflow).
        string titleUpper = title.ToUpperInvariant();
        float titleX = verX + verSz.X + 12f * scale;
        float titleMaxW = (end.X - rightReserve) - titleX;
        string shown = TruncateToWidth(titleUpper, titleMaxW);
        var titleCol = open ? Text : TextDim;
        dl.AddText(new Vector2(titleX, labelY),
            ImGui.ColorConvertFloat4ToU32(titleCol), shown);

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(1, 0));

        if (open)
        {
            // Indent the feature content inward from the section's left
            // edge so icons don't hug the edge. Matched by EndSectionBody.
            ImGui.Dummy(new Vector2(1, 4f * scale));
            ImGui.Indent(10f * scale);
        }
        return open;
    }

    // Simple ellipsis truncator - measures with the current font.
    // Uses "..." (three dots) instead of U+2026 since the Unicode
    // ellipsis renders mid-height in Dalamud's default font.
    private static string TruncateToWidth(string s, float maxW)
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

    private void EndSectionBody()
    {
        var scale = UIStyles.Scale;
        ImGui.Unindent(10f * scale);
        ImGui.Dummy(new Vector2(1, 8f * scale));
    }

    // ================================================================
    // FEATURE ITEM
    // ================================================================
    private void DrawFeatureItem(FontAwesomeIcon icon, string title, string description,
                                  string? tag = null)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();

        ImGui.Dummy(new Vector2(1, 2f * scale));
        var rowStart = ImGui.GetCursorScreenPos();
        float tileSize = 26f * scale;

        var tileMin = rowStart;
        var tileMax = new Vector2(rowStart.X + tileSize, rowStart.Y + tileSize);
        dl.AddRectFilled(tileMin, tileMax,
            ImGui.ColorConvertFloat4ToU32(
                new Vector4(Accent.X, Accent.Y, Accent.Z, 0.07f)));
        dl.AddRect(tileMin, tileMax,
            ImGui.ColorConvertFloat4ToU32(
                new Vector4(Accent.X, Accent.Y, Accent.Z, 0.32f)),
            0f, 0, 1f);

        uint notchCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(Accent.X, Accent.Y, Accent.Z, 0.7f));
        float notchLen = 3f * scale;
        dl.AddLine(tileMin, new Vector2(tileMin.X + notchLen, tileMin.Y), notchCol, 1f);
        dl.AddLine(tileMin, new Vector2(tileMin.X, tileMin.Y + notchLen), notchCol, 1f);
        dl.AddLine(tileMax, new Vector2(tileMax.X - notchLen, tileMax.Y), notchCol, 1f);
        dl.AddLine(tileMax, new Vector2(tileMax.X, tileMax.Y - notchLen), notchCol, 1f);

        ImGui.PushFont(UiBuilder.IconFont);
        var glyphStr = icon.ToIconString();
        var gSize = ImGui.CalcTextSize(glyphStr);
        float glyphX = tileMin.X + (tileSize - gSize.X) * 0.5f;
        float glyphY = tileMin.Y + (tileSize - gSize.Y) * 0.5f;
        unsafe
        {
            try
            {
                var glyphPtr = ImGui.GetFont().FindGlyph(glyphStr[0]);
                if (glyphPtr != null)
                {
                    float visW = glyphPtr->X1 - glyphPtr->X0;
                    glyphX = tileMin.X + (tileSize - visW) * 0.5f - glyphPtr->X0;
                }
            }
            catch { }
        }
        dl.AddText(new Vector2(glyphX, glyphY),
            ImGui.ColorConvertFloat4ToU32(Accent), glyphStr);
        ImGui.PopFont();

        float textX = tileMax.X + 11f * scale;
        float availWidth = ImGui.GetContentRegionAvail().X;
        // Extra right-side buffer so wrapped text never hugs the section's
        // right edge - gives the content room to breathe.
        float textMaxW = availWidth - (textX - rowStart.X) - 12f * scale;

        var textH = ImGui.GetTextLineHeight();
        dl.AddText(new Vector2(textX, rowStart.Y),
            ImGui.ColorConvertFloat4ToU32(Text), title);
        var titleSz = ImGui.CalcTextSize(title);
        if (tag != null)
        {
            var newSz = ImGui.CalcTextSize(tag);
            float tagPadX = 5f * scale;
            float tagPadY = 1f * scale;
            float tagX = textX + titleSz.X + 6f * scale;
            float tagY = rowStart.Y + (textH - newSz.Y - tagPadY * 2) * 0.5f;
            var newBorder = new Vector4(Success.X, Success.Y, Success.Z, 0.45f);
            var newBg = new Vector4(Success.X, Success.Y, Success.Z, 0.08f);
            dl.AddRectFilled(
                new Vector2(tagX, tagY),
                new Vector2(tagX + newSz.X + tagPadX * 2, tagY + newSz.Y + tagPadY * 2),
                ImGui.ColorConvertFloat4ToU32(newBg));
            dl.AddRect(
                new Vector2(tagX, tagY),
                new Vector2(tagX + newSz.X + tagPadX * 2, tagY + newSz.Y + tagPadY * 2),
                ImGui.ColorConvertFloat4ToU32(newBorder), 0f, 0, 1f);
            dl.AddText(new Vector2(tagX + tagPadX, tagY + tagPadY),
                ImGui.ColorConvertFloat4ToU32(Success), tag);
        }

        // PushTextWrapPos uses window-local X; pass cursor.X + textMaxW for icon-tile-aware wrap
        ImGui.SetCursorScreenPos(new Vector2(textX, rowStart.Y + textH + 2f * scale));
        float wrapLocalX = ImGui.GetCursorPosX() + textMaxW;
        ImGui.PushStyleColor(ImGuiCol.Text, TextDim);
        ImGui.PushTextWrapPos(wrapLocalX);
        ImGui.TextWrapped(description);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();

        float rowBottom = ImGui.GetCursorScreenPos().Y;
        float iconBottom = tileMax.Y;
        float endY = MathF.Max(rowBottom, iconBottom);
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X, endY));
        ImGui.Dummy(new Vector2(1, 3f * scale));

        // Dashed separator - short dashes every 6px.
        var sepStart = ImGui.GetCursorScreenPos();
        uint sepCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(BorderSoft.X, BorderSoft.Y, BorderSoft.Z, 0.7f));
        for (float dx = 0; dx < availWidth; dx += 6f * scale)
        {
            float segW = MathF.Min(3f * scale, availWidth - dx);
            dl.AddLine(
                new Vector2(sepStart.X + dx, sepStart.Y),
                new Vector2(sepStart.X + dx + segW, sepStart.Y),
                sepCol, 1f);
        }
        ImGui.Dummy(new Vector2(1, 4f * scale));
    }

    // ================================================================
    // VERSION SECTIONS
    // ================================================================
    private void DrawVersion1006Notes()
    {
        if (DrawPatchSection("v1006", $"V{Plugin.PatchNotesVersion}",
                "Routines & Choreography", isLatest: true, ref v1006Open))
        {
            DrawFeatureItem(FontAwesomeIcon.Palette, "UI Redesign",
                "Encore's had a full visual refresh. Hover over things, open some menus, switch between tabs, and you'll notice little touches tucked in all over the place. Everything's a bit easier on the eyes, too.",
                tag: "HEADLINER");

            DrawFeatureItem(FontAwesomeIcon.Stream, "Routines (Timeline)",
                "Choreograph multi-step performances. Drop presets onto a timeline, set per-step duration (fixed time, until the emote ends, or forever), and optionally loop the whole routine. Mix in macro steps to fire raw FFXIV commands (job skills, VFX, /em lines) with /wait support between steps. Great for dance shows, ceremony flows, or anything you'd otherwise run by hand.");

            DrawFeatureItem(FontAwesomeIcon.ShoePrints, "Simple Heels Integration",
                "Every preset (and per-step in routines) can now carry a Simple Heels offset: X/Y/Z translate, plus rotation, pitch, and roll. Drag the sliders or grab the in-world gizmo to dial in position visually while the preview updates live.");

            DrawFeatureItem(FontAwesomeIcon.Random, "Play a Random Preset",
                "Use /encore random to fire a random preset, or /encore random <folder> to roll within a specific folder. Also available as a shuffle button in the bottom bar.");

            DrawFeatureItem(FontAwesomeIcon.Filter, "Conflict Handling",
                "New Conflict Handling section in the preset editor lets you opt out of disabling specific conflicting mods per preset.");

            DrawFeatureItem(FontAwesomeIcon.Bolt, "Just Type the Emote",
                "With 'Allow All Emotes' enabled, just type the emote command directly (/consider, /beesknees, /runwaywalk) and it works automatically.");

            DrawFeatureItem(FontAwesomeIcon.FolderPlus, "Nested Folders",
                "Folders can now contain other folders. Drag them into each other, or use the context menu to move them around.");

            DrawFeatureItem(FontAwesomeIcon.Search, "Search Finds Everything",
                "Searching in Custom sort mode now auto-expands folders to reveal matching presets.");

            DrawFeatureItem(FontAwesomeIcon.CropAlt, "Icon Zoom & Pan",
                "Custom preset icons now have zoom (1-4x) and X/Y offset controls in the editor for precise framing.");

            DrawFeatureItem(FontAwesomeIcon.Wrench, "Bug Fixes",
                "Fixed reset button not clearing temporary mod settings. Various other stability fixes.");

            EndSectionBody();
        }
    }

    private void DrawVersion1005Notes()
    {
        if (DrawPatchSection("v1005", "V1.0.0.5",
                "Bypass Refinements", isLatest: false, ref v1005Open))
        {
            DrawFeatureItem(FontAwesomeIcon.Star, "Don't Have the Emote? No Problem.",
                "With 'Allow All Emotes' enabled, your dance and emote mod presets work regardless of whether you have the base emote.");

            DrawFeatureItem(FontAwesomeIcon.Music, "Vanilla Emotes",
                "Use /vanilla <emote> to play the original unmodded animation. Temporarily disables conflicting mods.");

            DrawFeatureItem(FontAwesomeIcon.Image, "Custom Icon Fix",
                "Fixed custom icon uploads failing for some users.");

            DrawFeatureItem(FontAwesomeIcon.Search, "Icon Search by Name",
                "Icon picker now lets you search by emote, mount, or minion name - not just icon ID.");

            DrawFeatureItem(FontAwesomeIcon.Cogs, "Better Mod Detection",
                "Improved detection for movement mods, hyphenated emotes, and multi-pose-type mods.");

            EndSectionBody();
        }
    }

    private void DrawVersion100Notes()
    {
        if (DrawPatchSection("v100", "V1.0.0.0",
                "Initial Release", isLatest: false, ref v100Open))
        {
            DrawFeatureItem(FontAwesomeIcon.Play, "One-Click Preset Switching",
                "Create presets for your dance and emote mods. Switch with a single click - Encore handles Penumbra priority automatically.");

            DrawFeatureItem(FontAwesomeIcon.Terminal, "Chat Commands",
                "Assign custom chat commands to your presets. Use /encore to open the main window, /encorereset to restore all mods.");

            DrawFeatureItem(FontAwesomeIcon.Shield, "Mod Conflict Resolution",
                "When you activate a preset, conflicting emote mods are automatically disabled. Everything is restored when you switch or reset.");

            DrawFeatureItem(FontAwesomeIcon.User, "Pose Presets",
                "Supports idle, sit, groundsit, and doze pose mods. Encore writes the correct pose index and cycles /cpose for you.");

            DrawFeatureItem(FontAwesomeIcon.LayerGroup, "Modifiers",
                "Add named variants to a single preset instead of duplicating it. e.g., /mydance slow or /mydance fast.");

            DrawFeatureItem(FontAwesomeIcon.FolderOpen, "Folders & Organization",
                "Group presets into color-coded folders. Drag and drop to reorder. Sort by name, command, favorites, or newest.");

            DrawFeatureItem(FontAwesomeIcon.Redo, "Emote Looping",
                "Use /loop <emote> to continuously repeat any non-looping emote. Move to stop.");

            DrawFeatureItem(FontAwesomeIcon.LocationCrosshairs, "Align to Target",
                "For duo emotes, use /align or the button in the bottom bar.");

            DrawFeatureItem(FontAwesomeIcon.Walking, "Movement Mods",
                "Walk, sprint, and jog animation mods are detected and supported as presets.");

            EndSectionBody();
        }
    }

    // ================================================================
    // FOOTER - half-EQ straddles divider, shorter progress bar, big CTA
    // ================================================================
    private void DrawFooter()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var footerH = 58f * scale;
        var end = new Vector2(start.X + availW, start.Y + footerH);

        // Footer background.
        uint bgCol = ImGui.ColorConvertFloat4ToU32(
            new Vector4(0.031f, 0.039f, 0.055f, 1f));
        dl.AddRectFilled(start, end, bgCol);

        // Divider line.
        dl.AddLine(start, new Vector2(end.X, start.Y),
            ImGui.ColorConvertFloat4ToU32(Border), 1f);

        // Footer-divider EQ removed - the progress EQ in the bar below
        // now carries the bar-motion role, and doubling up was busy.
        float t = (float)ImGui.GetTime();

        // Layout: [READ] [====] [42%]          [MARK AS READ]
        float padX = 14f * scale;
        float contentY = start.Y + footerH * 0.5f;
        var textH = ImGui.GetTextLineHeight();
        float textY = contentY - textH * 0.5f;

        // "READ" label.
        string readLabel = "READ";
        var readSz = ImGui.CalcTextSize(readLabel);
        dl.AddText(new Vector2(start.X + padX, textY),
            ImGui.ColorConvertFloat4ToU32(TextFaint), readLabel);

        // Button dimensions.
        float btnW = 148f * scale;
        float btnH = 32f * scale;
        float btnX = end.X - padX - btnW;
        float btnY = contentY - btnH * 0.5f;

        float barLeft = start.X + padX + readSz.X + 10f * scale;
        float barW = 150f * scale;
        float barRight = barLeft + barW;
        float pct = MathF.Max(0f, MathF.Min(1f, scrollPercent));
        float pbBarW = 2f * scale;
        float pbColGap = 2f * scale;
        int pbCols = (int)MathF.Floor((barW + pbColGap) / (pbBarW + pbColGap));
        if (pbCols < 8) pbCols = 8;
        float pbTotalW = pbCols * pbBarW + (pbCols - 1) * pbColGap;
        float pbStartX = barLeft + (barW - pbTotalW) * 0.5f;
        float pbBottomY = contentY + 1f * scale;   // baseline sits just below center
        float pbMaxH = 14f * scale;
        float pbMinH = 1.5f * scale;               // thin baseline at 0% scroll
        float palSlide = (t / 8f) % 1f;

        for (int i = 0; i < pbCols; i++)
        {
            float envFrac = (i + 0.5f) / pbCols;
            float envelope = MathF.Pow(MathF.Sin(envFrac * MathF.PI), 2f);

            // Per-col hashed phase so adjacent bars don't sync.
            uint hA = (uint)(i * 2654435761u);
            uint hB = (uint)(i * 40503u + 17u);
            float dur = 1.40f + ((hA >> 8) & 0xFFFF) / 65535f * 0.40f;
            float phaseOff = ((hB >> 8) & 0xFFFF) / 65535f * 2.0f;
            float cycle = ((t + phaseOff) % dur) / dur;
            float ease = 0.5f - 0.5f * MathF.Cos(cycle * MathF.Tau);
            float bounce = 0.55f + 0.45f * ease;

            // Target height at full scroll (envelope x per-col bounce).
            float fullH = pbMaxH * envelope * bounce;
            // Lerp from baseline thin line to full animated height.
            float h = pbMinH + (fullH - pbMinH) * pct;
            if (h < 1f) h = 1f;

            // Rainbow slide - same palette cycle as the banner EQ.
            float palIdx = ((i / (float)pbCols + palSlide) % 1f) * 8f;
            int p0 = (int)MathF.Floor(palIdx) % 8;
            int p1 = (p0 + 1) % 8;
            float lerp = palIdx - MathF.Floor(palIdx);
            var a0 = RainbowPalette[p0];
            var a1 = RainbowPalette[p1];
            var c = new Vector4(
                a0.X + (a1.X - a0.X) * lerp,
                a0.Y + (a1.Y - a0.Y) * lerp,
                a0.Z + (a1.Z - a0.Z) * lerp,
                1f);

            float x = pbStartX + i * (pbBarW + pbColGap);
            // Halo around bar.
            dl.AddRectFilled(
                new Vector2(x - 1f, pbBottomY - h - 1f),
                new Vector2(x + pbBarW + 1f, pbBottomY + 1f),
                ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, 0.20f)));
            // Core bar.
            dl.AddRectFilled(
                new Vector2(x, pbBottomY - h),
                new Vector2(x + pbBarW, pbBottomY),
                ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, 0.80f)));
        }

        string pctText = $"{(int)(pct * 100)}%";
        float pctX = barRight + 8f * scale;
        dl.AddText(new Vector2(pctX, textY),
            ImGui.ColorConvertFloat4ToU32(Accent), pctText);

        // disabled before scroll gate opens; stays reactive after pendingClose so kick animation finishes
        bool disabled = !hasScrolledToEnd;
        ImGui.SetCursorScreenPos(new Vector2(btnX, btnY));

        if (disabled)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.24f, 0.26f, 0.32f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text, TextFaint);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.Button("MARK AS READ", new Vector2(btnW, btnH));
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Scroll through the new features first!");
        }
        else
        {
            // Kick-scale - compress/overshoot bounce on click.
            float kick = 1f;
            if (markAsReadClickTime >= 0)
                kick = UIStyles.PlayKickScale((float)(ImGui.GetTime() - markAsReadClickTime));

            // Resting breath halo (same as DrawPlayButtonBloom, accent-tinted).
            var btnMin = new Vector2(btnX, btnY);
            var btnMax = new Vector2(btnX + btnW, btnY + btnH);
            UIStyles.DrawPlayButtonBloom(dl, btnMin, btnMax, scale, Accent);

            // Exact same button as the main window's PLAY, recolored accent.
            if (UIStyles.DrawPlayButton("##markAsRead",
                    new Vector2(btnW, btnH), kick, scale,
                    label: "MARK AS READ",
                    restCol:  Accent,
                    hoverCol: AccentBright,
                    heldCol:  AccentDeep,
                    borderCol: AccentDeep,
                    textColor: AccentDark)
                && !pendingClose)
            {
                // Only the FIRST click during a session registers - after
                // that pendingClose is set and we just let the animation
                // finish while ignoring further clicks.
                markAsReadClickCenter = new Vector2(
                    btnX + btnW * 0.5f, btnY + btnH * 0.5f);
                markAsReadClickTime = ImGui.GetTime();
                pendingClose = true;
                pendingCloseAt = ImGui.GetTime() + 0.9;
            }
        }
    }

    // ================================================================
    // WINDOW CORNER BRACKETS (bottom-left, bottom-right)
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

        // Bottom-left: vertical + horizontal arms from the corner.
        dl.AddLine(new Vector2(left, bottom - armLen), new Vector2(left, bottom), col, 1f);
        dl.AddLine(new Vector2(left, bottom), new Vector2(left + armLen, bottom), col, 1f);
        // Bottom-right.
        dl.AddLine(new Vector2(right - armLen, bottom), new Vector2(right, bottom), col, 1f);
        dl.AddLine(new Vector2(right, bottom - armLen), new Vector2(right, bottom), col, 1f);
    }

}
