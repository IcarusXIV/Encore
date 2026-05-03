using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Windowing;
using Encore.Styles;

namespace Encore.Windows;

// design source: F:/Encore/Encore/design/routine-editor-redesign-mockup.html
public class RoutineEditorWindow : Window
{
    public Routine? CurrentRoutine { get; private set; }
    public bool Confirmed { get; set; }
    public bool IsNewRoutine { get; private set; }

    //  Edit state 
    private string editName = "";
    private string editCommand = "";
    private bool editRepeatLoop = false;
    private uint? editIconId;
    private string? editCustomIconPath;
    private float editIconZoom = 1f;
    private float editIconOffsetX = 0f;
    private float editIconOffsetY = 0f;
    private List<RoutineStep> editSteps = new();
    private IconPickerWindow? iconPickerWindow;

    //  Transient UI state 
    private string presetSearch = "";
    private int dragSourceStepIndex = -1;
    private string? dragSourcePresetId = null;
    private int editingLayeredEmoteStep = -1;
    private int editingHeelsStep = -1;
    private bool pendingScrollToTop = false;

    // Save CTA flair - delayed close so the play-kick animation plays through.
    private double saveClickTime = -1;
    private bool savePendingClose = false;
    private double savePendingCloseAt = 0;

    // Loop switch easing state (Settings-compatible). -1 = uninitialized (seeds to target).
    private float loopSwitchAnim = -1f;
    private float loopSwitchVel = 0f;           // spring velocity for bounce
    private double loopSwitchFlareStartAt = -1;

    // New-step arrival flash - stamps creation time per step ID so the card
    // renders a 500ms accent overlay + ring when it first appears.
    private readonly Dictionary<string, double> stepCreatedAt = new();

    // Step removal animation - X click stamps the removal time here. The
    // card fades + collapses over ~400ms before the actual RemoveAt runs at
    // the cleanup pass at the top of the timeline panel draw.
    private readonly Dictionary<string, double> stepRemovalStartAt = new();
    private const float StepRemovalDurSec = 0.65f;

    // step reorder slide: ID -> initial Y offset, decays via ease-out cubic
    private readonly Dictionary<string, double> stepSlideStartAt = new();
    private readonly Dictionary<string, float> stepSlideStartOffset = new();
    private const float StepSlideDurSec = 0.24f;

    // +Macro Step button - click flash timestamp.
    private double macroButtonClickAt = -1;


    // Cached mm:ss text per step index (so we don't re-format while typing).
    private readonly Dictionary<int, string> editStepDurationText = new();

    // Shared heels target for the currently expanded per-step editor.
    private HeelsGizmoTarget? heelsPopupTarget;
    private int heelsPopupStepIndex = -1;

    private string? nameError;
    private string? commandError;

    //  Constants 
    private const float BaseWidth = 860f;
    private const float BaseHeight = 700f;
    private const float EditorHorizPad = 14f;

    // Common duration presets shown in the dropdown next to the text input.
    private static readonly (string Label, float Seconds)[] DurationPresets =
    {
        ("0:05", 5f),
        ("0:10", 10f),
        ("0:15", 15f),
        ("0:30", 30f),
        ("0:45", 45f),
        ("1:00", 60f),
        ("1:30", 90f),
        ("2:00", 120f),
        ("3:00", 180f),
        ("5:00", 300f),
    };

    //  Palette  Chrome (family-wide) 
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

    //  Palette  Per-step rainbow (mirrors --step-1..7 in the mockup) 
    private static readonly Vector4[] StepMarkerCycle =
    {
        new(0.38f, 0.72f, 1.00f, 1f),  // step-1 sky blue  #61b8ff
        new(0.72f, 0.52f, 1.00f, 1f),  // step-2 violet    #b885ff
        new(1.00f, 0.42f, 0.70f, 1f),  // step-3 rose      #ff6bb3
        new(0.45f, 0.92f, 0.55f, 1f),  // step-4 green     #73eb8c
        new(1.00f, 0.82f, 0.30f, 1f),  // step-5 yellow    #ffd14d
        new(0.28f, 0.88f, 0.92f, 1f),  // step-6 cyan      #47e1eb
        new(1.00f, 0.62f, 0.25f, 1f),  // step-7 orange    #ff9f40
    };

    // Footer EQ rainbow - 8 bars using p0..p7 (adds coral p7 beyond step cycle).
    private static readonly Vector4[] FooterEqColors =
    {
        new(0.38f, 0.72f, 1.00f, 1f),  // p0 sky blue
        new(0.72f, 0.52f, 1.00f, 1f),  // p1 violet
        new(1.00f, 0.42f, 0.70f, 1f),  // p2 rose
        new(0.28f, 0.88f, 0.92f, 1f),  // p3 cyan
        new(0.45f, 0.92f, 0.55f, 1f),  // p4 green
        new(1.00f, 0.82f, 0.30f, 1f),  // p5 yellow
        new(1.00f, 0.62f, 0.25f, 1f),  // p6 orange
        new(1.00f, 0.50f, 0.45f, 1f),  // p7 coral
    };

    // Special accents.
    private static readonly Vector4 Col_Macro    = new(0.72f, 0.52f, 1.00f, 1f);  // violet - macro steps
    private static readonly Vector4 Col_Loopback = new(1.00f, 0.42f, 0.70f, 1f);  // rose   - loop marker
    private static readonly Vector4 Col_Warning  = new(1.00f, 0.72f, 0.30f, 1f);
    private static readonly Vector4 Col_Error    = new(1.00f, 0.45f, 0.45f, 1f);

    // Badge accents (per-concern).
    private static readonly Vector4 Bdg_Variant    = new(0.72f, 0.52f, 1.00f, 1f);
    private static readonly Vector4 Bdg_Expression = new(1.00f, 0.42f, 0.70f, 1f);
    private static readonly Vector4 Bdg_Heels      = new(1.00f, 0.62f, 0.25f, 1f);
    private static readonly Vector4 Bdg_Info       = new(0.48f, 0.52f, 0.58f, 1f);

    //  Ctor / lifecycle 
    public RoutineEditorWindow() : base("Routine Editor###EncoreRoutineEditor")
    {
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags = ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public void SetIconPicker(IconPickerWindow picker) => iconPickerWindow = picker;

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.WindowScale;
        Size = new Vector2(BaseWidth * scale, BaseHeight * scale);
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(BaseWidth * scale, BaseHeight * scale),
            MaximumSize = new Vector2(BaseWidth * 2 * scale, BaseHeight * 2 * scale)
        };
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UIStyles.EncoreWindowBg);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor();
        base.PostDraw();
    }

    public void OpenNew()
    {
        CurrentRoutine = null;
        IsNewRoutine = true;
        Confirmed = false;
        editName = "";
        editCommand = "";
        editRepeatLoop = false;
        editIconId = null;
        editCustomIconPath = null;
        editIconZoom = 1f;
        editIconOffsetX = 0f;
        editIconOffsetY = 0f;
        editSteps = new();
        editStepDurationText.Clear();
        presetSearch = "";
        nameError = null;
        commandError = null;
        editingLayeredEmoteStep = -1;
        editingHeelsStep = -1;
        pendingScrollToTop = true;
        saveClickTime = -1;
        savePendingClose = false;
        loopSwitchAnim = -1f;
        loopSwitchVel = 0f;
        loopSwitchFlareStartAt = -1;
        stepCreatedAt.Clear();
        stepRemovalStartAt.Clear();
        stepSlideStartAt.Clear();
        stepSlideStartOffset.Clear();
        macroButtonClickAt = -1;
        IsOpen = true;
    }

    public void OpenEdit(Routine r)
    {
        CurrentRoutine = r;
        IsNewRoutine = false;
        Confirmed = false;
        editName = r.Name;
        editCommand = r.ChatCommand;
        editRepeatLoop = r.RepeatLoop;
        editIconId = r.IconId;
        editCustomIconPath = r.CustomIconPath;
        editIconZoom = r.IconZoom;
        editIconOffsetX = r.IconOffsetX;
        editIconOffsetY = r.IconOffsetY;
        editSteps = r.Steps.Select(s => s.Clone()).ToList();
        editStepDurationText.Clear();
        presetSearch = "";
        nameError = null;
        commandError = null;
        editingLayeredEmoteStep = -1;
        editingHeelsStep = -1;
        pendingScrollToTop = true;
        saveClickTime = -1;
        savePendingClose = false;
        loopSwitchAnim = -1f;
        loopSwitchVel = 0f;
        loopSwitchFlareStartAt = -1;
        stepCreatedAt.Clear();
        stepRemovalStartAt.Clear();
        stepSlideStartAt.Clear();
        stepSlideStartOffset.Clear();
        macroButtonClickAt = -1;
        IsOpen = true;
    }

    public override void OnClose()
    {
        base.OnClose();
        if (HeelsGizmoOverlay.Target == heelsPopupTarget && heelsPopupTarget != null)
        {
            HeelsGizmoOverlay.Target = null;
            HeelsGizmoOverlay.Label = null;
        }
        heelsPopupTarget = null;
        heelsPopupStepIndex = -1;
        editingHeelsStep = -1;
        Plugin.Instance?.RefreshActivePresetHeels();
    }

    //  Color helpers 
    private static uint ColAlpha(Vector4 c, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, a));
    private static uint ColU32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
    private static Vector4 GetStepMarkerColor(int index) =>
        StepMarkerCycle[index % StepMarkerCycle.Length];

    // 
    //   DRAW
    // 
    public override void Draw()
    {
        HandleIconPickerCompletion();

        UIStyles.PushMainWindowStyle();
        UIStyles.PushEncoreContent();

        // All chrome (ribbon + marquee + footer) renders edge-to-edge, no
        // item padding. Body child pushes its own padding + spacing inside.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.GrabRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.TabRounding, 0f);

        try
        {
            var scale = UIStyles.Scale;

            DrawRibbon();
            DrawMarquee();

            float footerH = 48f * scale;
            float bodyH = ImGui.GetContentRegionAvail().Y - footerH;

            ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.047f, 0.055f, 0.075f, 1f));
            if (ImGui.BeginChild("##routineBody",
                    new Vector2(ImGui.GetContentRegionAvail().X, bodyH), false,
                    ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawBody();
            }
            ImGui.EndChild();
            ImGui.PopStyleColor();

            DrawFooter();
            DrawWindowCornerBrackets();
        }
        finally
        {
            ImGui.PopStyleVar(8);
            UIStyles.PopEncoreContent();
            UIStyles.PopMainWindowStyle();
        }
    }

    // 
    //   RIBBON  -  5-dot setlist pip + "SETLIST - NEW/EDIT - NAME" + tag
    // 
    private void DrawRibbon()
    {
        // Shrink all ribbon text slightly - Dalamud's 18px default is too
        // chunky for a 30px chrome strip. CalcTextSize tracks the scale so
        // MeasureTrackedWidth + positioning math adjusts automatically.
        ImGui.SetWindowFontScale(0.85f);
        try { DrawRibbonInner(); }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    private void DrawRibbonInner()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        var ribbonH = 30f * scale;
        var end = new Vector2(start.X + availW, start.Y + ribbonH);
        float t = (float)ImGui.GetTime();

        // Gradient bg.
        uint bgTop = ColU32(new Vector4(0.047f, 0.055f, 0.071f, 1f));
        uint bgBot = ColU32(new Vector4(0.024f, 0.031f, 0.043f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        // Top hairline - fading in from edges toward center.
        uint aSolid = ColAlpha(Chr_Accent, 0.55f);
        uint aClear = ColAlpha(Chr_Accent, 0f);
        dl.AddRectFilledMultiColor(
            start, new Vector2(start.X + availW * 0.42f, start.Y + 1f),
            aSolid, aClear, aClear, aSolid);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.58f, start.Y),
            new Vector2(end.X, start.Y + 1f),
            aClear, aSolid, aSolid, aClear);
        // Bottom hairline - fading out toward edges.
        uint aSoft = ColAlpha(Chr_Accent, 0.30f);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, end.Y - 1f),
            new Vector2(start.X + availW * 0.5f, end.Y),
            aClear, aSoft, aSoft, aClear);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.5f, end.Y - 1f),
            end,
            aSoft, aClear, aClear, aSoft);

        float padX = 14f * scale;
        float textH = ImGui.GetTextLineHeight();
        float textY = start.Y + (ribbonH - textH) * 0.5f;
        float pipCenterY = start.Y + ribbonH * 0.5f;

        // Setlist pip - 5 dots with phase-offset breathing (new glyph in
        // the family: represents the 5 tracks on a setlist sheet).
        float dotX = start.X + padX;
        float dotSize = 4f * scale;
        float dotGap = 3f * scale;
        for (int i = 0; i < 5; i++)
        {
            float phase = t * MathF.Tau / 1.6f - i * 0.36f;
            float breath = 0.5f + 0.5f * MathF.Sin(phase);
            float eased = 0.30f + 0.70f * (breath * breath);
            var dMin = new Vector2(dotX + i * (dotSize + dotGap), pipCenterY - dotSize * 0.5f);
            var dMax = new Vector2(dMin.X + dotSize, dMin.Y + dotSize);
            dl.AddRectFilled(dMin, dMax, ColAlpha(Chr_Accent, eased));
        }
        float pipWidth = 5 * dotSize + 4 * dotGap;
        float metaX = dotX + pipWidth + 12f * scale;

        // Meta run - matches HTML:
        //   0 cues : "SETLIST - MODE - NO STEPS YET"
        //   N cues : "SETLIST - MODE - NAME - ~runtime"
        string labelTxt = "SETLIST";
        string sep = "  -  ";
        string mode = IsNewRoutine ? "NEW" : "EDIT";

        // Compute runtime tail.
        var cfgRibbon = Plugin.Instance?.Configuration;
        float totalSecsRibbon = ComputeRoutineTotalSeconds(cfgRibbon);
        bool hasDynamicRibbon = editSteps.Any(s =>
            s.DurationKind == RoutineStepDuration.UntilLoopEnds ||
            s.DurationKind == RoutineStepDuration.Forever);
        string tail = editSteps.Count == 0
            ? "NO STEPS YET"
            : ((hasDynamicRibbon ? "~" : "") + FormatDurationMmSs(totalSecsRibbon));
        bool showNameInTail = editSteps.Count > 0 && !string.IsNullOrWhiteSpace(editName);

        float metaTrack = 1.5f * scale;
        float cursor = metaX;
        float labelW = UIStyles.MeasureTrackedWidth(labelTxt, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), labelTxt,
            ColU32(Chr_TextDim), metaTrack);
        cursor += labelW;
        float sepW = UIStyles.MeasureTrackedWidth(sep, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), sep,
            ColU32(Chr_TextFaint), metaTrack);
        cursor += sepW;
        float modeW = UIStyles.MeasureTrackedWidth(mode, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), mode,
            ColU32(Chr_Accent), metaTrack);
        cursor += modeW;
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), sep,
            ColU32(Chr_TextFaint), metaTrack);
        cursor += sepW;

        float tagReserve = 110f * scale;
        float rightLimit = end.X - padX - tagReserve;

        if (showNameInTail)
        {
            // "{NAME}  -  {runtime}" - truncate name to fit, always show runtime.
            float tailW = UIStyles.MeasureTrackedWidth(tail, metaTrack);
            float nameMax = rightLimit - cursor - sepW - tailW;
            string nameShown = TruncateTrackedToFit(editName.ToUpperInvariant(),
                MathF.Max(20f * scale, nameMax), metaTrack);
            float nameShownW = UIStyles.MeasureTrackedWidth(nameShown, metaTrack);
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), nameShown,
                ColU32(Chr_Text), metaTrack);
            cursor += nameShownW;
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), sep,
                ColU32(Chr_TextFaint), metaTrack);
            cursor += sepW;
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), tail,
                ColU32(Chr_AccentBright), metaTrack);
        }
        else
        {
            // Just the tail (no name).
            string tailShown = TruncateTrackedToFit(tail,
                MathF.Max(20f * scale, rightLimit - cursor), metaTrack);
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), tailShown,
                ColU32(Chr_TextDim), metaTrack);
        }

        // Modified / Clean tag - compact pilled pill on the right.
        bool dirty = !string.IsNullOrWhiteSpace(editName)
                  || !string.IsNullOrWhiteSpace(editCommand)
                  || editSteps.Count > 0;
        string tagText = dirty ? "MODIFIED" : "CLEAN";
        float tagTrack = 1.0f * scale;
        float tagTextW = UIStyles.MeasureTrackedWidth(tagText, tagTrack);
        float tagPadX = 6f * scale;
        float tagPadY = 2f * scale;
        float dotR = 2f * scale;
        float dotGapTag = 4f * scale;
        float tagInnerW = dotR * 2 + dotGapTag + tagTextW;
        float tagRight = end.X - padX;
        float tagLeft = tagRight - tagInnerW - tagPadX * 2;
        float tagTop = textY - tagPadY;
        float tagBot = textY + textH + tagPadY;
        var tagCol = dirty ? Chr_Accent : Chr_TextFaint;
        dl.AddRectFilled(
            new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
            ColAlpha(tagCol, 0.05f));
        dl.AddRect(
            new Vector2(tagLeft, tagTop), new Vector2(tagRight, tagBot),
            ColU32(tagCol), 0f, 0, 1f);
        dl.AddCircleFilled(
            new Vector2(tagLeft + tagPadX + dotR, textY + textH * 0.5f),
            dotR, ColU32(tagCol));
        UIStyles.DrawTrackedText(dl,
            new Vector2(tagLeft + tagPadX + dotR * 2 + dotGapTag, textY),
            tagText, ColU32(tagCol), tagTrack);

        ImGui.Dummy(new Vector2(1, ribbonH));
    }

    // 
    //   MARQUEE  -  TONIGHT'S SET kicker - icon/name/cmd/loop row - step ribbon
    // 
    private void DrawMarquee()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        float marqueeH = 192f * scale;
        var end = new Vector2(start.X + availW, start.Y + marqueeH);
        float t = (float)ImGui.GetTime();
        float padX = EditorHorizPad * scale;

        // Background gradient.
        uint bgTop = ColU32(new Vector4(0.075f, 0.090f, 0.134f, 1f));
        uint bgBot = ColU32(new Vector4(0.039f, 0.051f, 0.078f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        // Spotlight drift - slow travelling wash behind everything else.
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
                dl.AddCircleFilled(new Vector2(cx, cy), r,
                    ColAlpha(Chr_AccentBright, a), 40);
            }
        }

        // Bottom hairline - accent-center, fade-edges.
        uint hSolid = ColAlpha(Chr_Accent, 0.70f);
        uint hClear = ColAlpha(Chr_Accent, 0f);
        float hairLeft = start.X + padX;
        float hairRight = end.X - padX;
        float hairMid = (hairLeft + hairRight) * 0.5f;
        dl.AddRectFilledMultiColor(
            new Vector2(hairLeft, end.Y - 1f),
            new Vector2(hairMid, end.Y),
            hClear, hSolid, hSolid, hClear);
        dl.AddRectFilledMultiColor(
            new Vector2(hairMid, end.Y - 1f),
            new Vector2(hairRight, end.Y),
            hSolid, hClear, hClear, hSolid);

        // Corner brackets - top-left and top-right.
        float bSize = 14f * scale;
        float bInset = 7f * scale;
        uint bCol = ColAlpha(Chr_Accent, 0.45f);
        dl.AddLine(new Vector2(start.X + bInset, start.Y + bInset),
                   new Vector2(start.X + bInset + bSize, start.Y + bInset), bCol, 1f);
        dl.AddLine(new Vector2(start.X + bInset, start.Y + bInset),
                   new Vector2(start.X + bInset, start.Y + bInset + bSize), bCol, 1f);
        dl.AddLine(new Vector2(end.X - bInset - bSize, start.Y + bInset),
                   new Vector2(end.X - bInset, start.Y + bInset), bCol, 1f);
        dl.AddLine(new Vector2(end.X - bInset, start.Y + bInset),
                   new Vector2(end.X - bInset, start.Y + bInset + bSize), bCol, 1f);

        //  "TONIGHT'S SET" kicker - centered, full-width fading gradient flairs on either side 
        string kicker = "TONIGHT'S  SET";
        var kSz = ImGui.CalcTextSize(kicker);
        float kickerTop = start.Y + 12f * scale;
        float kickerCX = start.X + availW * 0.5f;
        float kickerMidY = kickerTop + kSz.Y * 0.5f;
        dl.AddText(new Vector2(kickerCX - kSz.X * 0.5f, kickerTop),
            ColU32(Chr_TextFaint), kicker);

        uint fClear = ColAlpha(Chr_Accent, 0f);
        uint fSolid = ColAlpha(Chr_Accent, 0.30f);
        float flairGap = 14f * scale;
        float flairEdgePad = padX + 24f * scale;
        float lfLeft  = start.X + flairEdgePad;
        float lfRight = kickerCX - kSz.X * 0.5f - flairGap;
        float rfLeft  = kickerCX + kSz.X * 0.5f + flairGap;
        float rfRight = end.X - flairEdgePad;
        if (lfRight - lfLeft > 8f * scale)
        {
            dl.AddRectFilledMultiColor(
                new Vector2(lfLeft, kickerMidY),
                new Vector2(lfRight, kickerMidY + 1f),
                fClear, fSolid, fSolid, fClear);
        }
        if (rfRight - rfLeft > 8f * scale)
        {
            dl.AddRectFilledMultiColor(
                new Vector2(rfLeft, kickerMidY),
                new Vector2(rfRight, kickerMidY + 1f),
                fSolid, fClear, fClear, fSolid);
        }

        // 4 rack modules: [ ICN (76) | NAME (flex) | CMD (150) | LOOP (96) ]
        float consoleTop = kickerTop + kSz.Y + 14f * scale;
        float consoleH = 88f * scale;
        // Top strip reserved for module labels (above the content area).
        float labelStripH = 24f * scale;
        float consoleLeft = start.X + padX;
        float consoleRight = end.X - padX;
        float consoleW = consoleRight - consoleLeft;
        var consoleMin = new Vector2(consoleLeft, consoleTop);
        var consoleMax = new Vector2(consoleRight, consoleTop + consoleH);

        float iconModW = 76f * scale;
        float cmdModW  = 150f * scale;
        float loopModW = 96f * scale;
        float nameModW = consoleW - iconModW - cmdModW - loopModW;
        float iconModL = consoleLeft;
        float nameModL = iconModL + iconModW;
        float cmdModL  = nameModL + nameModW;
        float loopModL = cmdModL + cmdModW;

        // Console bg - vertical gradient giving a "milled metal" feel.
        uint cBgTop = ColU32(new Vector4(0.086f, 0.098f, 0.125f, 0.80f));
        uint cBgBot = ColU32(new Vector4(0.039f, 0.047f, 0.071f, 0.90f));
        dl.AddRectFilledMultiColor(consoleMin, consoleMax, cBgTop, cBgTop, cBgBot, cBgBot);
        dl.AddRect(consoleMin, consoleMax, ColAlpha(Chr_Border, 1f), 0f, 0, 1f);

        // Inset top highlight + bottom shadow (1px each).
        dl.AddLine(
            new Vector2(consoleLeft + 1f, consoleTop + 1f),
            new Vector2(consoleRight - 1f, consoleTop + 1f),
            ColU32(new Vector4(1f, 1f, 1f, 0.04f)), 1f);
        dl.AddLine(
            new Vector2(consoleLeft + 1f, consoleMax.Y - 2f),
            new Vector2(consoleRight - 1f, consoleMax.Y - 2f),
            ColU32(new Vector4(0f, 0f, 0f, 0.50f)), 1f);

        // Vertical hairline dividers between modules - fade top and bottom.
        {
            float divInset = 10f * scale;
            uint dClear = ColAlpha(Chr_Border, 0f);
            uint dSolid = ColAlpha(Chr_Border, 1f);
            float divMid = (consoleTop + divInset + consoleMax.Y - divInset) * 0.5f;
            foreach (float divX in new[] { nameModL, cmdModL, loopModL })
            {
                dl.AddRectFilledMultiColor(
                    new Vector2(divX, consoleTop + divInset),
                    new Vector2(divX + 1f, divMid),
                    dClear, dClear, dSolid, dSolid);
                dl.AddRectFilledMultiColor(
                    new Vector2(divX, divMid),
                    new Vector2(divX + 1f, consoleMax.Y - divInset),
                    dSolid, dSolid, dClear, dClear);
            }
        }

        float modLabelTrack = 1.1f * scale;
        float contentTop = consoleTop + labelStripH;
        float contentBot = consoleTop + consoleH;
        float contentCenterY = (contentTop + contentBot) * 0.5f;
        // module visual center (ignores label strip); NAME/CMD/LOOP align on this
        float moduleCenterY = consoleTop + consoleH * 0.5f;

        // ICN module: icon centered in full height (no eyebrow label)
        float iconSize = 56f * scale;
        float iconTileX = iconModL + (iconModW - iconSize) * 0.5f;
        float iconTileY = consoleTop + (consoleH - iconSize) * 0.5f;
        var iconMin = new Vector2(iconTileX, iconTileY);
        var iconMax = new Vector2(iconTileX + iconSize, iconTileY + iconSize);

        ImGui.SetCursorScreenPos(iconMin);
        ImGui.InvisibleButton("##marqueeIcon", new Vector2(iconSize, iconSize));
        bool iconHovered = ImGui.IsItemHovered();
        bool iconClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        bool iconRightClicked = ImGui.IsItemClicked(ImGuiMouseButton.Right);
        if (iconHovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (iconClicked && iconPickerWindow != null)
        {
            iconPickerWindow.Reset(editIconId);
            iconPickerWindow.IsOpen = true;
        }
        if (iconRightClicked)
            ImGui.OpenPopup("##iconMenu");
        if (iconHovered)
            UIStyles.EncoreTooltip("Left-click: pick game icon - Right-click: more options");

        bool hasCustom = !string.IsNullOrEmpty(editCustomIconPath) && File.Exists(editCustomIconPath);

        // Tile fill.
        uint tileBg = ColU32(iconHovered
            ? new Vector4(0.16f, 0.18f, 0.23f, 1f)
            : new Vector4(0.125f, 0.140f, 0.180f, 1f));
        dl.AddRectFilled(iconMin, iconMax, tileBg);

        // Tile content (custom image / game icon / placeholder).
        if (hasCustom)
        {
            var tex = GetCustomIcon(editCustomIconPath!);
            if (tex != null)
            {
                var (uv0, uv1) = MainWindow.CalcIconUV(tex.Width, tex.Height,
                    editIconZoom, editIconOffsetX, editIconOffsetY);
                var pad = 3f * scale;
                dl.AddImage(tex.Handle,
                    new Vector2(iconMin.X + pad, iconMin.Y + pad),
                    new Vector2(iconMax.X - pad, iconMax.Y - pad),
                    uv0, uv1);
            }
            dl.AddRect(iconMin, iconMax,
                ColAlpha(iconHovered ? Chr_Accent : Chr_Border, 1f), 0f, 0, 1f);
        }
        else if (editIconId.HasValue)
        {
            var icon = Plugin.TextureProvider.GetFromGameIcon(editIconId.Value)?.GetWrapOrEmpty();
            if (icon != null)
            {
                var pad = 3f * scale;
                dl.AddImage(icon.Handle,
                    new Vector2(iconMin.X + pad, iconMin.Y + pad),
                    new Vector2(iconMax.X - pad, iconMax.Y - pad));
            }
            dl.AddRect(iconMin, iconMax,
                ColAlpha(iconHovered ? Chr_Accent : Chr_Border, 1f), 0f, 0, 1f);
        }
        else
        {
            // Placeholder - dashed border + "?" glyph.
            DrawDashedRect(dl, iconMin, iconMax,
                ColAlpha(iconHovered ? Chr_TextDim : Chr_TextGhost, 1f),
                4f * scale, 3f * scale, 1f);
            string q = "?";
            var placeholderFont = Plugin.Instance?.TitleFont;
            if (placeholderFont is { Available: true })
            {
                using (placeholderFont.Push())
                {
                    var qSz = ImGui.CalcTextSize(q);
                    dl.AddText(
                        new Vector2(iconMin.X + (iconSize - qSz.X) * 0.5f,
                                    iconMin.Y + (iconSize - qSz.Y) * 0.5f),
                        ColU32(Chr_TextGhost), q);
                }
            }
            else
            {
                var qSz = ImGui.CalcTextSize(q);
                dl.AddText(
                    new Vector2(iconMin.X + (iconSize - qSz.X) * 0.5f,
                                iconMin.Y + (iconSize - qSz.Y) * 0.5f),
                    ColU32(Chr_TextGhost), q);
            }
        }

        // Four corner brackets on the tile (camera-viewfinder motif).
        {
            float brArm = 8f * scale;
            float brOut = 2f * scale;
            uint brCol = ColAlpha(Chr_Accent, iconHovered ? 1.0f : 0.80f);
            // TL
            dl.AddLine(new Vector2(iconMin.X - brOut, iconMin.Y - brOut),
                       new Vector2(iconMin.X - brOut + brArm, iconMin.Y - brOut), brCol, 1f);
            dl.AddLine(new Vector2(iconMin.X - brOut, iconMin.Y - brOut),
                       new Vector2(iconMin.X - brOut, iconMin.Y - brOut + brArm), brCol, 1f);
            // TR
            dl.AddLine(new Vector2(iconMax.X + brOut - brArm, iconMin.Y - brOut),
                       new Vector2(iconMax.X + brOut, iconMin.Y - brOut), brCol, 1f);
            dl.AddLine(new Vector2(iconMax.X + brOut, iconMin.Y - brOut),
                       new Vector2(iconMax.X + brOut, iconMin.Y - brOut + brArm), brCol, 1f);
            // BL
            dl.AddLine(new Vector2(iconMin.X - brOut, iconMax.Y + brOut - brArm),
                       new Vector2(iconMin.X - brOut, iconMax.Y + brOut), brCol, 1f);
            dl.AddLine(new Vector2(iconMin.X - brOut, iconMax.Y + brOut),
                       new Vector2(iconMin.X - brOut + brArm, iconMax.Y + brOut), brCol, 1f);
            // BR
            dl.AddLine(new Vector2(iconMax.X + brOut - brArm, iconMax.Y + brOut),
                       new Vector2(iconMax.X + brOut, iconMax.Y + brOut), brCol, 1f);
            dl.AddLine(new Vector2(iconMax.X + brOut, iconMax.Y + brOut - brArm),
                       new Vector2(iconMax.X + brOut, iconMax.Y + brOut), brCol, 1f);
        }

        // Status LED inside the tile (bottom-right). Green when any of
        // the routine's steps matches Configuration.ActivePresetId.
        {
            bool routineActive = false;
            var apId = Plugin.Instance?.Configuration?.ActivePresetId;
            if (!string.IsNullOrEmpty(apId))
            {
                foreach (var s in editSteps)
                {
                    if (s.PresetId == apId) { routineActive = true; break; }
                }
            }
            float ledR = 3f * scale;
            var ledCtr = new Vector2(iconMax.X - 7f * scale, iconMax.Y - 7f * scale);
            if (routineActive)
            {
                var green = new Vector4(0.45f, 0.92f, 0.55f, 1f);
                for (int g = 3; g >= 1; g--)
                {
                    float ga = 0.22f / g;
                    dl.AddCircleFilled(ledCtr, ledR + g * 1.5f * scale, ColAlpha(green, ga));
                }
                dl.AddCircleFilled(ledCtr, ledR, ColU32(green));
            }
            else
            {
                dl.AddCircleFilled(ledCtr, ledR, ColU32(Chr_TextGhost));
            }
        }

        // Right-click context menu - replaces the inline upload/clear chips entirely.
        if (ImGui.BeginPopup("##iconMenu"))
        {
            if (ImGui.MenuItem("Pick game icon"))
            {
                if (iconPickerWindow != null)
                {
                    iconPickerWindow.Reset(editIconId);
                    iconPickerWindow.IsOpen = true;
                }
            }
            if (ImGui.MenuItem("Upload custom image"))
                OpenCustomIconDialog();

            if (hasCustom && ImGui.BeginMenu("Frame (zoom / offset)"))
            {
                ImGui.SetNextItemWidth(180 * scale);
                ImGui.DragFloat("zoom##fz", ref editIconZoom, 0.02f, 1f, 4f, "%.2fx");
                ImGui.SetNextItemWidth(180 * scale);
                ImGui.DragFloat("offset x##fx", ref editIconOffsetX, 0.005f, -0.5f, 0.5f, "%+.3f");
                ImGui.SetNextItemWidth(180 * scale);
                ImGui.DragFloat("offset y##fy", ref editIconOffsetY, 0.005f, -0.5f, 0.5f, "%+.3f");
                if (ImGui.Button("reset framing"))
                {
                    editIconZoom = 1f;
                    editIconOffsetX = 0f;
                    editIconOffsetY = 0f;
                }
                ImGui.EndMenu();
            }

            if (hasCustom || editIconId.HasValue)
            {
                ImGui.Separator();
                ImGui.PushStyleColor(ImGuiCol.Text, Col_Error);
                if (ImGui.MenuItem("Clear icon"))
                {
                    editCustomIconPath = null;
                    editIconId = null;
                    editIconZoom = 1f;
                    editIconOffsetX = 0f;
                    editIconOffsetY = 0f;
                }
                ImGui.PopStyleColor();
            }
            ImGui.EndPopup();
        }

        //  MODULE 2 - NAME (the architectural nameplate) 
        UIStyles.DrawTrackedText(dl,
            new Vector2(nameModL + 10f * scale, consoleTop + 6f * scale),
            "NAME", ColU32(Chr_Accent), modLabelTrack);

        // Accent left bar with soft outward glow (compensates for the
        // Unbounded-Bold feel we can't reproduce typographically).
        {
            float barW = 3f * scale;
            float barInset = 8f * scale;
            var barMin = new Vector2(nameModL, consoleTop + barInset);
            var barMax = new Vector2(nameModL + barW, consoleMax.Y - barInset);
            for (int g = 3; g >= 1; g--)
            {
                float ga = 0.15f / g;
                dl.AddRectFilled(
                    new Vector2(barMin.X - g * 1.5f * scale, barMin.Y),
                    new Vector2(barMax.X + g * 1.5f * scale, barMax.Y),
                    ColAlpha(Chr_Accent, ga));
            }
            dl.AddRectFilled(barMin, barMax, ColU32(Chr_Accent));
        }

        float nameInputL = nameModL + 16f * scale;
        float nameInputR = cmdModL - 12f * scale;
        float nameInputW = nameInputR - nameInputL;

        var nameFont = Plugin.Instance?.TitleFont;
        bool useTitleFont = nameFont is { Available: true };
        float nameFontH = useTitleFont ? 30f * scale : ImGui.GetTextLineHeight();
        float nameFramePadY = 3f * scale;
        float nameFrameH = nameFontH + nameFramePadY * 2;

        float nameInputTop = moduleCenterY - nameFrameH * 0.5f;
        float nameInputBottomY = nameInputTop + nameFrameH;
        // Rule tight to the input (1px gap) so the text reads as resting
        // on the underline rather than floating.
        float nameRuleY = nameInputBottomY + 1f * scale;

        ImGui.SetCursorScreenPos(new Vector2(nameInputL, nameInputTop));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, ColAlpha(Chr_Accent, 0.05f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  ColAlpha(Chr_Accent, 0.08f));
        ImGui.PushStyleColor(ImGuiCol.Border,         new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Text,           Chr_Text);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f * scale, nameFramePadY));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.SetNextItemWidth(nameInputW);
        if (useTitleFont)
        {
            using (nameFont!.Push())
                ImGui.InputTextWithHint("##routineName", "Enter routine name...", ref editName, 64);
        }
        else
        {
            ImGui.InputTextWithHint("##routineName", "Enter routine name...", ref editName, 64);
        }
        var nameRectMin = ImGui.GetItemRectMin();
        var nameRectMax = ImGui.GetItemRectMax();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);

        // Horizontal rule - text rests on this line.
        dl.AddRectFilledMultiColor(
            new Vector2(nameRectMin.X, nameRuleY),
            new Vector2(nameRectMax.X, nameRuleY + 1f),
            ColAlpha(Chr_Accent, 0.65f), ColAlpha(Chr_Accent, 0.20f),
            ColAlpha(Chr_Accent, 0.20f), ColAlpha(Chr_Accent, 0.65f));

        //  MODULE 3 - CMD (framed pill with / + input) 
        UIStyles.DrawTrackedText(dl,
            new Vector2(cmdModL + 10f * scale, consoleTop + 6f * scale),
            "CMD", ColU32(Chr_TextFaint), modLabelTrack);

        float cmdPillL = cmdModL + 10f * scale;
        float cmdPillR = cmdModL + cmdModW - 10f * scale;
        float cmdPillH = 28f * scale;
        // CMD pill centered on moduleCenterY (same mid-line as NAME input).
        float cmdPillTop = moduleCenterY - cmdPillH * 0.5f;
        var cmdPillMin = new Vector2(cmdPillL, cmdPillTop);
        var cmdPillMax = new Vector2(cmdPillR, cmdPillTop + cmdPillH);
        dl.AddRectFilled(cmdPillMin, cmdPillMax, ColAlpha(Chr_Accent, 0.08f));
        dl.AddRect(cmdPillMin, cmdPillMax, ColAlpha(Chr_Accent, 0.35f), 0f, 0, 1f);

        var slashSz = ImGui.CalcTextSize("/");
        dl.AddText(
            new Vector2(cmdPillMin.X + 8f * scale,
                        cmdPillMin.Y + (cmdPillH - slashSz.Y) * 0.5f),
            ColU32(Chr_Accent), "/");
        float cmdInputX = cmdPillMin.X + 8f * scale + slashSz.X + 4f * scale;
        float cmdInputW = cmdPillMax.X - cmdInputX - 6f * scale;

        ImGui.SetCursorScreenPos(new Vector2(cmdInputX,
            cmdPillTop + (cmdPillH - ImGui.GetFrameHeight()) * 0.5f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border,         new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Text,           Chr_AccentBright);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(0f, 4f * scale));
        ImGui.SetNextItemWidth(cmdInputW);
        if (ImGui.InputTextWithHint("##routineCmd", "command", ref editCommand, 32))
            editCommand = editCommand.TrimStart('/').ToLowerInvariant();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);

        //  MODULE 4 - LOOP (channel strip) 
        UIStyles.DrawTrackedText(dl,
            new Vector2(loopModL + 10f * scale, consoleTop + 6f * scale),
            "LOOP",
            ColU32(editRepeatLoop ? Col_Loopback : Chr_TextFaint), modLabelTrack);

        float lSwitchW = 44f * scale;
        float lSwitchH = 18f * scale;
        float lSwitchX = loopModL + (loopModW - lSwitchW) * 0.5f;
        // Switch centered on moduleCenterY (same mid-line as NAME + CMD).
        // Status text hangs below the switch as a decoration.
        float lSwitchY = moduleCenterY - lSwitchH * 0.5f;
        DrawLoopSwitch(
            new Vector2(lSwitchX, lSwitchY),
            new Vector2(lSwitchW, lSwitchH),
            Col_Loopback,
            ref editRepeatLoop);

        string statusText = editRepeatLoop ? "ON" : "OFF";
        float statusTrack = 1.1f * scale;
        float statusW = UIStyles.MeasureTrackedWidth(statusText, statusTrack);
        float statusX = loopModL + (loopModW - statusW) * 0.5f;
        float statusY = lSwitchY + lSwitchH + 6f * scale;
        UIStyles.DrawTrackedText(dl,
            new Vector2(statusX, statusY),
            statusText,
            ColU32(editRepeatLoop ? Col_Loopback : Chr_TextFaint),
            statusTrack);

        //  Step ribbon (below the console) 
        float ribbonTop = consoleMax.Y + 14f * scale;
        float ribbonBot = end.Y - 14f * scale;
        DrawStepRibbon(dl,
            new Vector2(start.X + padX, ribbonTop),
            new Vector2(end.X - padX, ribbonBot));

        // SetCursorScreenPos calls above leave the cursor mid-marquee; reset before reserving
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(availW, marqueeH));
    }

    // Settings-compatible pill switch - sliding knob, halo bloom when on, toggle-flare
    // ripple on click. Mirrors SettingsWindow.DrawSwitch so both windows share the
    // same switch vocabulary.
    private void DrawLoopSwitch(Vector2 pos, Vector2 size, Vector4 color, ref bool value)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var io = ImGui.GetIO();

        ImGui.SetCursorScreenPos(pos);
        bool clicked = ImGui.InvisibleButton("##loopSwitch", size);
        bool hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (clicked)
        {
            value = !value;
            loopSwitchFlareStartAt = ImGui.GetTime();
        }

        // Damped-spring toward target (0 off, 1 on). Natural overshoot gives
        // the knob a bouncy snap when toggled. High stiffness + moderate
        // damping -> ~8-10% overshoot, settles in ~400ms.
        float target = value ? 1f : 0f;
        if (loopSwitchAnim < 0f) { loopSwitchAnim = target; loopSwitchVel = 0f; }
        float dt = io.DeltaTime > 0.05f ? 0.05f : io.DeltaTime;   // clamp for stability
        const float stiffness = 320f;
        const float damping = 18f;
        float dx = target - loopSwitchAnim;
        loopSwitchVel += (dx * stiffness - loopSwitchVel * damping) * dt;
        loopSwitchAnim += loopSwitchVel * dt;
        float anim = loopSwitchAnim;

        // Halo bloom underneath when on - concentric rects, decaying alpha, breathing.
        if (anim > 0.01f)
        {
            float breath = 0.85f + 0.15f * (0.5f + 0.5f * MathF.Sin(
                (float)ImGui.GetTime() * MathF.Tau / 2.6f));
            for (int r = 4; r >= 1; r--)
            {
                float pad = r * 2.5f * scale;
                float a = (0.18f / (r + 1)) * anim * breath;
                dl.AddRectFilled(
                    new Vector2(pos.X - pad, pos.Y - pad),
                    new Vector2(pos.X + size.X + pad, pos.Y + size.Y + pad),
                    ColAlpha(color, a));
            }
        }

        // Track - blends from neutral dark to accent-tint left-to-right.
        var surface = new Vector4(0.125f, 0.140f, 0.180f, 1f);
        var trackOff = surface;
        var trackOnLeft = new Vector4(
            color.X * 0.28f + surface.X * 0.72f,
            color.Y * 0.28f + surface.Y * 0.72f,
            color.Z * 0.28f + surface.Z * 0.72f, 1f);
        var trackOnRight = new Vector4(
            color.X * 0.14f + surface.X * 0.86f,
            color.Y * 0.14f + surface.Y * 0.86f,
            color.Z * 0.14f + surface.Z * 0.86f, 1f);
        uint tL = ColU32(new Vector4(
            trackOff.X + (trackOnLeft.X - trackOff.X) * anim,
            trackOff.Y + (trackOnLeft.Y - trackOff.Y) * anim,
            trackOff.Z + (trackOnLeft.Z - trackOff.Z) * anim, 1f));
        uint tR = ColU32(new Vector4(
            trackOff.X + (trackOnRight.X - trackOff.X) * anim,
            trackOff.Y + (trackOnRight.Y - trackOff.Y) * anim,
            trackOff.Z + (trackOnRight.Z - trackOff.Z) * anim, 1f));
        dl.AddRectFilledMultiColor(pos, pos + size, tL, tR, tR, tL);

        // Track border.
        var borderBase = new Vector4(Chr_Border.X, Chr_Border.Y, Chr_Border.Z, 1f);
        var borderOn = color;
        Vector4 bc = new Vector4(
            borderBase.X + (borderOn.X - borderBase.X) * anim,
            borderBase.Y + (borderOn.Y - borderBase.Y) * anim,
            borderBase.Z + (borderOn.Z - borderBase.Z) * anim, 1f);
        if (hovered) bc = new Vector4(
            bc.X + (Chr_AccentBright.X - bc.X) * 0.15f,
            bc.Y + (Chr_AccentBright.Y - bc.Y) * 0.15f,
            bc.Z + (Chr_AccentBright.Z - bc.Z) * 0.15f, 1f);
        dl.AddRect(pos, pos + size, ColU32(bc), 0f, 0, 1f);

        // Knob - 14px wide, travels between left (off) and right (on).
        float knobW = 14f * scale;
        float knobH = size.Y - 4f * scale;
        float knobTravel = size.X - knobW - 4f * scale;
        float kx = pos.X + 2f * scale + knobTravel * anim;
        float ky = pos.Y + 2f * scale;
        var kMin = new Vector2(kx, ky);
        var kMax = new Vector2(kx + knobW, ky + knobH);

        // Knob fill gradient - dark off, accent on.
        var knobOffTop = new Vector4(0.25f, 0.27f, 0.32f, 1f);
        var knobOffBot = new Vector4(0.15f, 0.16f, 0.20f, 1f);
        var knobOnTop = new Vector4(
            MathF.Min(1f, color.X * 1.10f),
            MathF.Min(1f, color.Y * 1.10f),
            MathF.Min(1f, color.Z * 1.10f), 1f);
        var knobOnBot = color;
        Vector4 knobTop = new Vector4(
            knobOffTop.X + (knobOnTop.X - knobOffTop.X) * anim,
            knobOffTop.Y + (knobOnTop.Y - knobOffTop.Y) * anim,
            knobOffTop.Z + (knobOnTop.Z - knobOffTop.Z) * anim, 1f);
        Vector4 knobBot = new Vector4(
            knobOffBot.X + (knobOnBot.X - knobOffBot.X) * anim,
            knobOffBot.Y + (knobOnBot.Y - knobOffBot.Y) * anim,
            knobOffBot.Z + (knobOnBot.Z - knobOffBot.Z) * anim, 1f);
        uint kT = ColU32(knobTop);
        uint kB = ColU32(knobBot);
        dl.AddRectFilledMultiColor(kMin, kMax, kT, kT, kB, kB);

        // Knob glow outline when on.
        if (anim > 0.01f)
        {
            float ga = anim * 0.55f;
            dl.AddRect(
                new Vector2(kMin.X - 1.5f * scale, kMin.Y - 1.5f * scale),
                new Vector2(kMax.X + 1.5f * scale, kMax.Y + 1.5f * scale),
                ColAlpha(color, ga), 0f, 0, 1f);
        }

        // Knob border.
        var kBorder = new Vector4(0.23f + (color.X * 0.70f - 0.23f) * anim,
                                  0.25f + (color.Y * 0.70f - 0.25f) * anim,
                                  0.31f + (color.Z * 0.70f - 0.31f) * anim, 1f);
        dl.AddRect(kMin, kMax, ColU32(kBorder), 0f, 0, 1f);

        // Grip lines in the knob.
        uint gripCol = anim > 0.5f
            ? ColAlpha(Chr_AccentDark, 0.55f)
            : ColAlpha(Chr_Accent, 0.25f);
        float gMidY0 = ky + knobH * 0.30f;
        float gMidY1 = ky + knobH * 0.70f;
        float gCx = kx + knobW * 0.5f;
        float gGap = 2f * scale;
        for (int i = -1; i <= 1; i++)
        {
            float gx = gCx + i * gGap;
            dl.AddLine(new Vector2(gx, gMidY0), new Vector2(gx, gMidY1), gripCol, 1f);
        }

        // Toggle-flare ripple.
        if (loopSwitchFlareStartAt >= 0)
        {
            float elapsed = (float)(ImGui.GetTime() - loopSwitchFlareStartAt);
            const float flareDur = 0.55f;
            if (elapsed >= flareDur)
            {
                loopSwitchFlareStartAt = -1;
            }
            else
            {
                float tRaw = elapsed / flareDur;
                float te = 1f - MathF.Pow(1f - tRaw, 3f);
                var ctr = new Vector2(pos.X + size.X * 0.5f, pos.Y + size.Y * 0.5f);
                float baseR = size.Y * 0.55f;
                float rippleR = baseR + te * size.X * 1.35f;
                float rippleA = (1f - te) * 0.55f;
                if (rippleA > 0.01f)
                {
                    dl.AddCircle(ctr, rippleR, ColAlpha(color, rippleA), 28, 1.5f);
                    float innerR = baseR + te * size.X * 0.85f;
                    float innerA = (1f - te) * 0.30f;
                    dl.AddCircle(ctr, innerR, ColAlpha(color, innerA), 28, 1.0f);
                }
            }
        }
    }

    // FontAwesome glyph centered on its visible bounds at a given point.
    private static void DrawFAGlyph(ImDrawListPtr dl, FontAwesomeIcon icon,
                                     Vector2 center, uint color)
    {
        ImGui.PushFont(UiBuilder.IconFont);
        var glyph = icon.ToIconString();
        var sz = ImGui.CalcTextSize(glyph);
        float visX0 = 0f;
        float visW = sz.X;
        try
        {
            unsafe
            {
                var gp = ImGui.GetFont().FindGlyph(glyph[0]);
                if (gp != null)
                {
                    visX0 = gp->X0;
                    visW = gp->X1 - gp->X0;
                }
            }
        }
        catch { }
        dl.AddText(
            new Vector2(center.X - visW * 0.5f - visX0,
                        center.Y - sz.Y * 0.5f),
            color, glyph);
        ImGui.PopFont();
    }

    // Horizontal step-ribbon preview - a SET label, numbered dots colored by
    // step index, optional LOOP cap. Matches the mockup's signature element.
    private void DrawStepRibbon(ImDrawListPtr dl, Vector2 rMin, Vector2 rMax)
    {
        ImGui.SetWindowFontScale(0.85f);
        try { DrawStepRibbonInner(dl, rMin, rMax); }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    private void DrawStepRibbonInner(ImDrawListPtr dl, Vector2 rMin, Vector2 rMax)
    {
        var scale = UIStyles.Scale;
        float w = rMax.X - rMin.X;
        float h = rMax.Y - rMin.Y;

        // Container bg + border with left accent bar.
        dl.AddRectFilled(rMin, rMax, ColAlpha(Chr_Accent, 0.04f));
        dl.AddRect(rMin, rMax, ColAlpha(Chr_BorderSoft, 1f), 0f, 0, 1f);
        dl.AddRectFilled(rMin, new Vector2(rMin.X + 2f * scale, rMax.Y), ColU32(Chr_Accent));

        float padX = 10f * scale;
        float innerL = rMin.X + padX;
        float innerR = rMax.X - padX;
        float midY = rMin.Y + h * 0.5f;
        float textH = ImGui.GetTextLineHeight();
        float textY = midY - textH * 0.5f;

        // "SET" eyebrow label.
        string setLbl = "SET";
        float setTrack = 1.0f * scale;
        float setW = UIStyles.MeasureTrackedWidth(setLbl, setTrack);
        UIStyles.DrawTrackedText(dl,
            new Vector2(innerL, textY),
            setLbl, ColU32(Chr_TextFaint), setTrack);
        float cursor = innerL + setW + 10f * scale;

        // Separator dot.
        dl.AddCircleFilled(new Vector2(cursor, midY), 1.6f * scale,
            ColAlpha(Chr_TextGhost, 1f));
        cursor += 8f * scale;

        // Stats: "N CUES - RUNTIME M:SS" (migrated out of the header meta row).
        var cfg = Plugin.Instance?.Configuration;
        float totalSecs = ComputeRoutineTotalSeconds(cfg);
        bool hasDynamic = editSteps.Any(s =>
            s.DurationKind == RoutineStepDuration.UntilLoopEnds ||
            s.DurationKind == RoutineStepDuration.Forever);

        string cuesTxt = (editSteps.Count == 0 ? "0" : editSteps.Count.ToString())
                         + " CUE" + (editSteps.Count == 1 ? "" : "S");
        float cuesW = UIStyles.MeasureTrackedWidth(cuesTxt, setTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), cuesTxt,
            ColU32(editSteps.Count == 0 ? Chr_TextFaint : Chr_Text), setTrack);
        cursor += cuesW + 10f * scale;

        // Separator dot.
        dl.AddCircleFilled(new Vector2(cursor, midY), 1.6f * scale,
            ColAlpha(Chr_TextGhost, 1f));
        cursor += 8f * scale;

        string rtLbl = "RUNTIME ";
        string rtVal = editSteps.Count == 0
            ? "--:--"
            : ((hasDynamic ? "~" : "") + FormatDurationMmSs(totalSecs));
        float rtLblW = UIStyles.MeasureTrackedWidth(rtLbl, setTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), rtLbl,
            ColU32(Chr_TextDim), setTrack);
        float rtValW = UIStyles.MeasureTrackedWidth(rtVal, setTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor + rtLblW, textY), rtVal,
            ColU32(editSteps.Count == 0 ? Chr_TextFaint : Chr_AccentBright), setTrack);
        cursor += rtLblW + rtValW + 12f * scale;

        float dotsLeft = cursor;
        float dotsRight = innerR;

        // "(reset) LOOP" cap on the right - FontAwesome Redo glyph (Unicode (reset)
        // doesn't render reliably in Dalamud's default font).
        if (editRepeatLoop)
        {
            string loopLbl = "LOOP";
            float loopTrack = 1.1f * scale;
            float loopTextW = UIStyles.MeasureTrackedWidth(loopLbl, loopTrack);

            // Measure FA Redo glyph width for layout.
            ImGui.PushFont(UiBuilder.IconFont);
            var redoGlyph = FontAwesomeIcon.Redo.ToIconString();
            var redoSz = ImGui.CalcTextSize(redoGlyph);
            ImGui.PopFont();

            float arrowW = redoSz.X + 6f * scale;
            float capW = loopTextW + arrowW;
            float capLeft = innerR - capW;

            // Soft glow behind the glyph.
            for (int r = 3; r >= 1; r--)
            {
                float a = 0.14f / r;
                dl.AddCircleFilled(
                    new Vector2(capLeft + redoSz.X * 0.5f, midY),
                    6f * scale + r * 1.5f * scale,
                    ColAlpha(Col_Loopback, a));
            }

            // Draw the FA glyph itself.
            DrawFAGlyph(dl, FontAwesomeIcon.Redo,
                new Vector2(capLeft + redoSz.X * 0.5f, midY),
                ColU32(Col_Loopback));

            UIStyles.DrawTrackedText(dl,
                new Vector2(capLeft + arrowW, textY),
                loopLbl, ColU32(Col_Loopback), loopTrack);

            dotsRight = capLeft - 10f * scale;
        }

        // Rail line behind the dots.
        dl.AddLine(new Vector2(dotsLeft, midY), new Vector2(dotsRight, midY),
            ColAlpha(Chr_Accent, 0.15f), 1f);

        // Dots - real steps when present, placeholder dots when empty.
        int dotCount = editSteps.Count > 0 ? editSteps.Count : 7;
        float dotSize = 14f * scale;
        float gap = 6f * scale;
        float totalDotsW = dotCount * dotSize + (dotCount - 1) * gap;
        float maxDotsW = dotsRight - dotsLeft;

        // If too many dots to fit - shrink gap/dot or truncate. Simple shrink.
        if (totalDotsW > maxDotsW && dotCount > 1)
        {
            gap = MathF.Max(2f * scale, (maxDotsW - dotCount * dotSize) / (dotCount - 1));
            totalDotsW = dotCount * dotSize + (dotCount - 1) * gap;
            if (totalDotsW > maxDotsW)
            {
                dotSize = MathF.Max(8f * scale, (maxDotsW - (dotCount - 1) * gap) / dotCount);
                totalDotsW = dotCount * dotSize + (dotCount - 1) * gap;
            }
        }

        float x = dotsLeft;
        for (int i = 0; i < dotCount; i++)
        {
            var dMin = new Vector2(x, midY - dotSize * 0.5f);
            var dMax = new Vector2(x + dotSize, midY + dotSize * 0.5f);
            if (editSteps.Count == 0)
            {
                // Dashed empty placeholder.
                DrawDashedRect(dl, dMin, dMax, ColAlpha(Chr_TextFaint, 0.7f),
                    3f * scale, 2f * scale, 1f);
            }
            else
            {
                var step = editSteps[i];
                if (step.IsMacroStep)
                {
                    // Dashed violet outline + "M" glyph.
                    DrawDashedRect(dl, dMin, dMax, ColU32(Col_Macro),
                        3f * scale, 2f * scale, 1.5f);
                    var labelSz = ImGui.CalcTextSize("M");
                    dl.AddText(
                        new Vector2((dMin.X + dMax.X - labelSz.X) * 0.5f,
                                    (dMin.Y + dMax.Y - labelSz.Y) * 0.5f),
                        ColU32(Col_Macro), "M");
                }
                else
                {
                    var col = GetStepMarkerColor(i);
                    // Soft halo behind the dot.
                    for (int r = 2; r >= 1; r--)
                    {
                        float a = 0.18f / r;
                        dl.AddRectFilled(
                            new Vector2(dMin.X - r * 1.5f, dMin.Y - r * 1.5f),
                            new Vector2(dMax.X + r * 1.5f, dMax.Y + r * 1.5f),
                            ColAlpha(col, a));
                    }
                    dl.AddRectFilled(dMin, dMax, ColU32(col));
                    dl.AddRect(dMin, dMax, ColAlpha(col, 0.85f), 0f, 0, 1f);
                    string numStr = (i + 1).ToString();
                    var numSz = ImGui.CalcTextSize(numStr);
                    dl.AddText(
                        new Vector2((dMin.X + dMax.X - numSz.X) * 0.5f,
                                    (dMin.Y + dMax.Y - numSz.Y) * 0.5f),
                        ColU32(Chr_AccentDark), numStr);
                }
            }
            x += dotSize + gap;
        }
    }

    // 
    //   BODY  -  Library (260px) | 1px divider | Timeline (flex)
    // 
    private void DrawBody()
    {
        var scale = UIStyles.Scale;
        var avail = ImGui.GetContentRegionAvail();
        float libW = 260f * scale;
        float dividerGap = 1f * scale;
        float timelineW = avail.X - libW - dividerGap;

        var bodyStart = ImGui.GetCursorScreenPos();

        // Library panel (left).
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, 4f * scale));
        if (ImGui.BeginChild("##libraryPanel", new Vector2(libW, avail.Y), false,
                ImGuiWindowFlags.NoBackground))
        {
            DrawLibraryPanel(libW);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        // Vertical divider.
        var dl = ImGui.GetWindowDrawList();
        float divX = bodyStart.X + libW + dividerGap * 0.5f;
        dl.AddLine(
            new Vector2(divX, bodyStart.Y + 8f * scale),
            new Vector2(divX, bodyStart.Y + avail.Y - 8f * scale),
            ColAlpha(Chr_Border, 1f), 1f);

        ImGui.SameLine(0, dividerGap);

        // Timeline panel (right).
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, 4f * scale));
        if (ImGui.BeginChild("##timelinePanel", new Vector2(timelineW, avail.Y), false,
                ImGuiWindowFlags.NoBackground))
        {
            DrawTimelinePanel();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        // Global mouse-release: clear drag state if released outside drop targets.
        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            dragSourcePresetId = null;
            dragSourceStepIndex = -1;
        }
    }

    // 
    //   LIBRARY PANEL
    // 
    private void DrawLibraryPanel(float panelW)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();

        // Panel head: "A" eyebrow + "LIBRARY" title + count meta (right).
        DrawPanelHead("A", "LIBRARY",
            Plugin.Instance?.Configuration is { } cfg
                ? $"{cfg.Presets.Count} preset{(cfg.Presets.Count == 1 ? "" : "s")}"
                : "",
            Chr_Accent);
        DrawPanelHint("drag onto the timeline ->");

        // Search box (with prefix search icon).
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.Indent(14f * scale);
        var searchStart = ImGui.GetCursorScreenPos();
        float searchW = panelW - 28f * scale;
        float searchH = 26f * scale;

        // Background rect.
        var searchMin = searchStart;
        var searchMax = new Vector2(searchStart.X + searchW, searchStart.Y + searchH);
        dl.AddRectFilled(searchMin, searchMax, ColU32(new Vector4(0.125f, 0.140f, 0.180f, 1f)));
        dl.AddRect(searchMin, searchMax, ColAlpha(Chr_Border, 1f), 0f, 0, 1f);

        // Search icon.
        ImGui.PushFont(UiBuilder.IconFont);
        var searchGlyph = FontAwesomeIcon.Search.ToIconString();
        var sgSz = ImGui.CalcTextSize(searchGlyph);
        dl.AddText(
            new Vector2(searchStart.X + 7f * scale,
                        searchStart.Y + (searchH - sgSz.Y) * 0.5f),
            ColU32(Chr_TextFaint), searchGlyph);
        ImGui.PopFont();

        // Input itself - sits inside the rect, 24px left-indented past the glyph.
        ImGui.SetCursorScreenPos(new Vector2(searchStart.X + 26f * scale,
            searchStart.Y + (searchH - ImGui.GetFrameHeight()) * 0.5f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Border,         new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 0f);
        ImGui.SetNextItemWidth(searchW - 32f * scale);
        ImGui.InputTextWithHint("##presetSearch", "search presets...", ref presetSearch, 64);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        // Advance cursor past the search rect.
        ImGui.SetCursorScreenPos(new Vector2(searchStart.X, searchMax.Y + 6f * scale));
        ImGui.Dummy(new Vector2(1, 1));
        ImGui.Unindent(14f * scale);
        ImGui.PopStyleVar();

        // Scrollable list.
        var config = Plugin.Instance?.Configuration;
        if (config == null) return;

        var presets = config.Presets
            .Where(p => string.IsNullOrEmpty(presetSearch) ||
                        p.Name.Contains(presetSearch, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name)
            .ToList();

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        // AlwaysVerticalScrollbar reserves the scrollbar width at all times so
        // content doesn't shift horizontally when the list crosses the
        // overflow threshold (filtering / adding / removing presets).
        if (ImGui.BeginChild("##libList", ImGui.GetContentRegionAvail(), false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            ImGui.Indent(14f * scale);
            foreach (var preset in presets)
                DrawLibraryRow(preset);
            ImGui.Unindent(14f * scale);
            ImGui.Dummy(new Vector2(1, 8f * scale));
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawLibraryRow(DancePreset preset)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();

        var rowStart = ImGui.GetCursorScreenPos();
        var iconSize = 28f * scale;
        float rowH = iconSize + 6f * scale;
        float rowW = ImGui.GetContentRegionAvail().X - 14f * scale;

        ImGui.InvisibleButton($"##libitem_{preset.Id}", new Vector2(rowW, rowH));
        bool hovered = ImGui.IsItemHovered();

        // Register drag source IMMEDIATELY - the invisible button is the
        // "last item" anchor for BeginDragDropSource.
        if (ImGui.BeginDragDropSource())
        {
            dragSourcePresetId = preset.Id;
            dragSourceStepIndex = -1;
            ImGui.SetDragDropPayload("ROUTINE_PRESET", ReadOnlySpan<byte>.Empty, ImGuiCond.None);
            DrawPresetIcon(preset, iconSize);
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(preset.Name);
            ImGui.EndDragDropSource();
        }

        // Hover highlight + left accent bar.
        if (hovered)
        {
            dl.AddRectFilled(rowStart, new Vector2(rowStart.X + rowW, rowStart.Y + rowH),
                ColAlpha(Chr_Accent, 0.06f));
            dl.AddRectFilled(rowStart, new Vector2(rowStart.X + 2f * scale, rowStart.Y + rowH),
                ColU32(Chr_Accent));
        }

        // Icon.
        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + 6f * scale, rowStart.Y + 3f * scale));
        DrawPresetIcon(preset, iconSize);

        // Name + /cmd.
        float nameY = rowStart.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
        float nameX = rowStart.X + 6f * scale + iconSize + 8f * scale;
        string cmdText = preset.EmoteCommand ?? "";
        var cmdSz = !string.IsNullOrWhiteSpace(cmdText)
            ? ImGui.CalcTextSize(cmdText) : Vector2.Zero;
        float cmdRightPad = 6f * scale;
        float nameBudget = rowStart.X + rowW - cmdRightPad - (cmdSz.X > 0 ? cmdSz.X + 8f * scale : 0f) - nameX;

        string displayName = TruncateToFit(preset.Name, nameBudget);
        dl.AddText(new Vector2(nameX, nameY), ColU32(Chr_Text), displayName);

        if (cmdSz.X > 0)
        {
            var cmdCol = hovered ? Chr_Accent : Chr_TextFaint;
            dl.AddText(new Vector2(rowStart.X + rowW - cmdSz.X - cmdRightPad, nameY),
                ColU32(cmdCol), cmdText);
        }
    }

    private void DrawPresetIcon(DancePreset preset, float size)
    {
        var scale = UIStyles.Scale;
        if (!string.IsNullOrEmpty(preset.CustomIconPath) && File.Exists(preset.CustomIconPath))
        {
            var tex = GetCustomIcon(preset.CustomIconPath);
            if (tex != null)
            {
                var (uv0, uv1) = MainWindow.CalcIconUV(tex.Width, tex.Height,
                    preset.IconZoom, preset.IconOffsetX, preset.IconOffsetY);
                ImGui.Image(tex.Handle, new Vector2(size, size), uv0, uv1);
                return;
            }
        }
        if (preset.IconId.HasValue)
        {
            var icon = Plugin.TextureProvider.GetFromGameIcon(preset.IconId.Value)?.GetWrapOrEmpty();
            if (icon != null)
            {
                ImGui.Image(icon.Handle, new Vector2(size, size));
                return;
            }
        }
        DrawPlaceholderIcon(size, scale);
    }

    private static void DrawPlaceholderIcon(float size, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        dl.AddRectFilled(pos, pos + new Vector2(size, size),
            ColU32(new Vector4(0.2f, 0.2f, 0.22f, 0.9f)));
        var textSize = ImGui.CalcTextSize("?");
        var textPos = pos + new Vector2((size - textSize.X) / 2, (size - textSize.Y) / 2);
        dl.AddText(textPos, ColU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), "?");
        ImGui.Dummy(new Vector2(size, size));
    }

    // 
    //   TIMELINE PANEL
    // 
    private void DrawTimelinePanel()
    {
        var scale = UIStyles.Scale;

        var config2 = Plugin.Instance?.Configuration;
        float totalSecs = ComputeRoutineTotalSeconds(config2);
        bool hasDynamic = editSteps.Any(s =>
            s.DurationKind == RoutineStepDuration.UntilLoopEnds ||
            s.DurationKind == RoutineStepDuration.Forever);
        string metaLabel = editSteps.Count == 0
            ? "0 cues"
            : $"{editSteps.Count} cue{(editSteps.Count == 1 ? "" : "s")} - {(hasDynamic ? "~" : "")}{FormatDurationMmSs(totalSecs)}";

        DrawPanelHead("B", "TIMELINE", metaLabel, Chr_Accent);
        DrawPanelHint(editSteps.Count == 0
            ? "drop a preset to add a cue - or add a macro step"
            : "drag step markers to reorder - drop presets to append");

        var config = Plugin.Instance?.Configuration;
        if (config == null) return;

        // Scrollable timeline body.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, 4f * scale));
        //  Cleanup pass: steps whose removal animation has completed 
        // Collect IDs first, then remove from both the timestamp dict and
        // the editSteps list so indices stay valid for the draw loop below.
        if (stepRemovalStartAt.Count > 0)
        {
            var doneRemoving = new List<string>();
            var now = ImGui.GetTime();
            foreach (var kv in stepRemovalStartAt)
            {
                if (now - kv.Value >= StepRemovalDurSec)
                    doneRemoving.Add(kv.Key);
            }
            if (doneRemoving.Count > 0)
            {
                foreach (var id in doneRemoving) stepRemovalStartAt.Remove(id);
                editSteps.RemoveAll(s => doneRemoving.Contains(s.Id));
            }
        }

        // AlwaysVerticalScrollbar reserves the gutter so steps don't shift on overflow toggle
        if (ImGui.BeginChild("##timelineBody", ImGui.GetContentRegionAvail(), false,
                ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            if (pendingScrollToTop)
            {
                ImGui.SetScrollY(0f);
                pendingScrollToTop = false;
            }

            // Inset past the panel edge. Small extra left nudge so the leftmost
            // pixel of the marker circles isn't clipped.
            float leftPad = 14f * scale;
            ImGui.Indent(leftPad);

            if (editSteps.Count == 0)
            {
                DrawTimelineEmptyState();
            }
            else
            {
                for (int i = 0; i < editSteps.Count; i++)
                {
                    DrawStepCard(i, config);
                }
                if (editRepeatLoop)
                    DrawLoopBackIndicator();
            }

            // "+ macro step" button.
            ImGui.Dummy(new Vector2(1, 6f * scale));
            DrawAddMacroButton();

            // Append-drop zone below everything.
            var remaining = ImGui.GetContentRegionAvail();
            if (remaining.Y > 10)
            {
                var dropStart = ImGui.GetCursorScreenPos();
                var dropSize = new Vector2(remaining.X, Math.Max(40f * scale, remaining.Y - 10f));
                ImGui.InvisibleButton("##timelineAppend", dropSize);
                HandleManualDrop(dropStart, dropSize, editSteps.Count);
            }

            ImGui.Unindent(leftPad);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
    }

    private void DrawTimelineEmptyState()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X - 14f * scale;
        float h = 160f * scale;
        var boxMin = start;
        var boxMax = new Vector2(start.X + w, start.Y + h);

        ImGui.InvisibleButton("##emptyDrop", new Vector2(w, h));
        HandleManualDrop(start, new Vector2(w, h), 0);

        DrawDashedRect(dl, boxMin, boxMax,
            ColAlpha(Chr_Border, 1f), 5f * scale, 3f * scale, 1.5f);

        float centerX = (boxMin.X + boxMax.X) * 0.5f;

        // Crosshairs icon via FontAwesome - scaled up via drawlist text,
        // since FA doesn't have a bigger-variant font but renders crisp at any pos.
        ImGui.PushFont(UiBuilder.IconFont);
        var crossGlyph = FontAwesomeIcon.Crosshairs.ToIconString();
        var crossSz = ImGui.CalcTextSize(crossGlyph);
        float glyphY = boxMin.Y + 24f * scale;
        dl.AddText(
            new Vector2(centerX - crossSz.X * 0.5f, glyphY),
            ColU32(Chr_TextGhost), crossGlyph);
        ImGui.PopFont();

        // "DROP ZONE" - widely-tracked caps.
        string dz = "DROP  ZONE";
        float dzTrack = 3.0f * scale;
        float dzW = UIStyles.MeasureTrackedWidth(dz, dzTrack);
        float dzY = glyphY + crossSz.Y + 14f * scale;
        UIStyles.DrawTrackedText(dl,
            new Vector2(centerX - dzW * 0.5f, dzY),
            dz, ColU32(Chr_TextFaint), dzTrack);

        // Hint lines below.
        string hint1 = "Drag any preset from the library,";
        string hint2 = "or click \"+ macro step\" below.";
        var h1Sz = ImGui.CalcTextSize(hint1);
        var h2Sz = ImGui.CalcTextSize(hint2);
        float hint1Y = dzY + ImGui.GetTextLineHeight() + 12f * scale;
        dl.AddText(
            new Vector2(centerX - h1Sz.X * 0.5f, hint1Y),
            ColU32(Chr_TextDim), hint1);
        dl.AddText(
            new Vector2(centerX - h2Sz.X * 0.5f, hint1Y + h1Sz.Y + 4f * scale),
            ColU32(Chr_TextDim), hint2);

        ImGui.Dummy(new Vector2(1, 10f * scale));
    }

    private void DrawAddMacroButton()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();

        // Layout: small dashed "+" circle + pill button right next to it.
        float markerColW = 38f * scale;
        float btnH = 26f * scale;

        // Compute button width from actual tracked-text measurement.
        string label = "+  MACRO STEP";
        float labelTrack = 1.3f * scale;
        float labelW = UIStyles.MeasureTrackedWidth(label, labelTrack);
        float btnW = labelW + 20f * scale;   // 10px left/right padding

        // Dashed "+" marker - sits at the same X as step-card markers so the
        // column reads as continuous "next cue slot".
        float cx = startPos.X + markerColW * 0.5f - 4f * scale;
        float cy = startPos.Y + btnH * 0.5f;
        float r = 13f * scale;
        int segs = 24;
        for (int i = 0; i < segs; i++)
        {
            if ((i % 2) != 0) continue;
            float a0 = MathF.PI * 2f * i / segs;
            float a1 = MathF.PI * 2f * (i + 1) / segs;
            var p0 = new Vector2(cx + MathF.Cos(a0) * r, cy + MathF.Sin(a0) * r);
            var p1 = new Vector2(cx + MathF.Cos(a1) * r, cy + MathF.Sin(a1) * r);
            dl.AddLine(p0, p1, ColAlpha(Chr_TextFaint, 0.55f), 1f);
        }
        string plus = "+";
        var pSz = ImGui.CalcTextSize(plus);
        dl.AddText(new Vector2(cx - pSz.X * 0.5f, cy - pSz.Y * 0.5f),
            ColU32(Chr_TextFaint), plus);

        // Button - immediately right of the marker column.
        var btnMin = new Vector2(startPos.X + markerColW, startPos.Y);
        var btnMax = new Vector2(btnMin.X + btnW, btnMin.Y + btnH);
        ImGui.SetCursorScreenPos(btnMin);
        ImGui.InvisibleButton("##addMacro", new Vector2(btnW, btnH));
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);

        // Click-flash background - violet pulse that fades over 300ms so the
        // click registers before the new macro step's arrival flash takes over.
        float clickFlashAlpha = 0f;
        if (macroButtonClickAt >= 0)
        {
            float ce = (float)(ImGui.GetTime() - macroButtonClickAt);
            if (ce < 0.30f)
            {
                float ct = ce / 0.30f;
                clickFlashAlpha = (1f - ct) * 0.35f;
            }
            else macroButtonClickAt = -1;
        }
        if (clickFlashAlpha > 0.01f)
            dl.AddRectFilled(btnMin, btnMax, ColAlpha(Col_Macro, clickFlashAlpha));
        else if (hovered)
            dl.AddRectFilled(btnMin, btnMax, ColAlpha(Col_Macro, 0.10f));
        DrawDashedRect(dl, btnMin, btnMax,
            ColAlpha(hovered ? Col_Macro : Chr_Border, 1f),
            4f * scale, 3f * scale, 1f);

        UIStyles.DrawTrackedText(dl,
            new Vector2(btnMin.X + 10f * scale,
                        btnMin.Y + (btnH - ImGui.GetTextLineHeight()) * 0.5f),
            label, ColU32(hovered ? Col_Macro : Chr_TextDim), labelTrack);

        if (hovered) UIStyles.EncoreTooltip("Add a free-form macro step (FFXIV macro syntax with /wait support)");

        if (clicked)
        {
            var newMacro = new RoutineStep
            {
                IsMacroStep = true,
                MacroText = "",
                DurationKind = RoutineStepDuration.UntilLoopEnds,
                DurationSeconds = 0f,
            };
            editSteps.Add(newMacro);
            stepCreatedAt[newMacro.Id] = ImGui.GetTime();   // arrival flash
            macroButtonClickAt = ImGui.GetTime();           // button flash
        }

        ImGui.SetCursorScreenPos(new Vector2(startPos.X, btnMax.Y + 4f * scale));
        ImGui.Dummy(new Vector2(1, 1));
    }

    // Loopback indicator - rose accent, rounded marker with (reset), text card.
    private void DrawLoopBackIndicator()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var startPos = ImGui.GetCursorScreenPos();

        float markerR = 15f * scale;
        float markerColW = 38f * scale;
        float rowH = markerR * 2 + 6f * scale;
        float fullW = ImGui.GetContentRegionAvail().X - 14f * scale;

        var center = new Vector2(startPos.X + markerColW * 0.5f - 4f * scale,
                                 startPos.Y + rowH * 0.5f);

        // Rail drop from the last step card into the marker.
        dl.AddLine(
            new Vector2(center.X, startPos.Y - 6f * scale),
            new Vector2(center.X, center.Y - markerR),
            ColAlpha(Col_Loopback, 0.7f), 1f);

        // Marker: solid rose circle with glow.
        for (int r = 3; r >= 1; r--)
        {
            float a = 0.14f / r;
            dl.AddCircleFilled(center, markerR + r * 2f * scale,
                ColAlpha(Col_Loopback, a));
        }
        dl.AddCircleFilled(center, markerR, ColU32(new Vector4(0.07f, 0.08f, 0.12f, 1f)));
        dl.AddCircle(center, markerR, ColU32(Col_Loopback), 0, 2f);

        // Redo glyph.
        ImGui.PushFont(UiBuilder.IconFont);
        var glyph = FontAwesomeIcon.Redo.ToIconString();
        var gSz = ImGui.CalcTextSize(glyph);
        dl.AddText(
            new Vector2(center.X - gSz.X * 0.5f, center.Y - gSz.Y * 0.5f),
            ColU32(Col_Loopback), glyph);
        ImGui.PopFont();

        // Text card right of marker.
        var cardMin = new Vector2(startPos.X + markerColW, startPos.Y);
        var cardMax = new Vector2(cardMin.X + fullW - markerColW, cardMin.Y + rowH);
        dl.AddRectFilled(cardMin, cardMax, ColAlpha(Col_Loopback, 0.08f));
        dl.AddRect(cardMin, cardMax, ColAlpha(Col_Loopback, 0.45f), 0f, 0, 1f);
        dl.AddRectFilled(cardMin, new Vector2(cardMin.X + 3f * scale, cardMax.Y), ColU32(Col_Loopback));

        string prefix = "LOOPS BACK TO  ";
        string target = "CUE 01";
        float track = 1.2f * scale;
        float prefW = UIStyles.MeasureTrackedWidth(prefix, track);
        float textY = cardMin.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f;
        UIStyles.DrawTrackedText(dl,
            new Vector2(cardMin.X + 12f * scale, textY),
            prefix, ColU32(Chr_Text), track);
        UIStyles.DrawTrackedText(dl,
            new Vector2(cardMin.X + 12f * scale + prefW, textY),
            target, ColU32(Col_Loopback), track);

        ImGui.SetCursorScreenPos(new Vector2(startPos.X, cardMax.Y + 6f * scale));
        ImGui.Dummy(new Vector2(1, 1));
    }

    // 
    //   STEP CARD (preset step)
    // 
    private void DrawStepCard(int index, Configuration config)
    {
        var scale = UIStyles.Scale;
        var step = editSteps[index];
        if (step.IsMacroStep) { DrawMacroStepCard(index); return; }

        var preset = config.Presets.Find(p => p.Id == step.PresetId);
        var presetName = preset?.Name ?? "(missing preset)";
        var expressionExpanded = editingLayeredEmoteStep == index;
        var heelsExpanded = editingHeelsStep == index;

        var naturalPos = ImGui.GetCursorScreenPos();
        float slideOffset = ComputeReorderSlideOffset(step.Id);
        if (slideOffset != 0f)
            ImGui.SetCursorScreenPos(new Vector2(naturalPos.X, naturalPos.Y + slideOffset));
        var startPos = ImGui.GetCursorScreenPos();
        var fullWidth = ImGui.GetContentRegionAvail().X - 14f * scale;

        // Layout metrics.
        var markerR = 15f * scale;
        var markerColW = 38f * scale;
        var accentW = 3f * scale;
        var padX = 12f * scale;
        var padY = 10f * scale;

        var cardLeft = startPos.X + markerColW;
        var cardWidth = fullWidth - markerColW;
        var contentX = cardLeft + accentW + padX;
        var contentR = cardLeft + cardWidth - padX;
        var contentW = contentR - contentX;

        var frameH = ImGui.GetFrameHeight();
        var rowH = ImGui.GetFrameHeightWithSpacing();

        var btnW = 18f * scale;
        var btnH = 17f * scale;
        var btnGap = 2f * scale;
        var actionColW = btnW * 3 + btnGap * 2;

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        // Marker circle.
        var markerColor = GetStepMarkerColor(index);
        var markerCenter = new Vector2(startPos.X + markerColW * 0.5f - 4f * scale,
                                        startPos.Y + padY + frameH * 0.5f);
        bool isThisStepBeingDragged = dragSourceStepIndex == index;

        DrawStepMarker(markerCenter, (index + 1).ToString(), markerColor,
            active: expressionExpanded || heelsExpanded, scale);

        // Marker doubles as drag handle.
        ImGui.SetCursorScreenPos(new Vector2(markerCenter.X - markerR, markerCenter.Y - markerR));
        ImGui.InvisibleButton($"##stepDrag_{index}", new Vector2(markerR * 2, markerR * 2));
        if (ImGui.IsItemHovered() && !isThisStepBeingDragged)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UIStyles.EncoreTooltip("Drag to reorder");
        }
        if (ImGui.BeginDragDropSource())
        {
            dragSourceStepIndex = index;
            dragSourcePresetId = null;
            ImGui.SetDragDropPayload("ROUTINE_STEP", ReadOnlySpan<byte>.Empty, ImGuiCond.None);

            ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.12f, 0.14f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border, ColAlpha(markerColor, 0.6f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 8f * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            var pdl = ImGui.GetWindowDrawList();
            var ps = ImGui.GetCursorScreenPos();
            float pr = 11f * scale;
            var pc = new Vector2(ps.X + pr, ps.Y + pr);
            pdl.AddCircleFilled(pc, pr, ColU32(new Vector4(0.085f, 0.10f, 0.14f, 1f)));
            pdl.AddCircle(pc, pr, ColU32(markerColor), 0, 1f);
            var numSz = ImGui.CalcTextSize((index + 1).ToString());
            pdl.AddText(new Vector2(pc.X - numSz.X / 2f, pc.Y - numSz.Y / 2f),
                ColU32(markerColor), (index + 1).ToString());
            ImGui.Dummy(new Vector2(pr * 2 + 4f * scale, pr * 2));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text(presetName);
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
            ImGui.EndDragDropSource();
        }

        if (isThisStepBeingDragged) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);

        // Primary row: name + /cmd.
        var curY = startPos.Y + padY;
        ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
        ImGui.AlignTextToFramePadding();
        if (preset == null)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ColAlpha(Col_Error, 1f));
            ImGui.Text(presetName);
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.Text(presetName);
        }
        if (!string.IsNullOrWhiteSpace(preset?.EmoteCommand))
        {
            ImGui.SameLine(0, 10f * scale);
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Chr_TextFaint, preset!.EmoteCommand);
        }

        // Time row.
        curY += rowH;
        ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
        ImGui.AlignTextToFramePadding();
        UIStyles.DrawTrackedText(ImGui.GetWindowDrawList(),
            new Vector2(contentX, curY + (frameH - ImGui.GetTextLineHeight()) * 0.5f),
            "TIME", ColU32(Chr_TextFaint), 1.0f * scale);
        ImGui.SetCursorScreenPos(new Vector2(contentX + 34f * scale, curY));

        PushTimeInputStyle();

        var isLastStep = index == editSteps.Count - 1;
        var canOfferForever = isLastStep && !editRepeatLoop;
        if (!canOfferForever && step.DurationKind == RoutineStepDuration.Forever)
            step.DurationKind = RoutineStepDuration.Fixed;
        var kind = (int)step.DurationKind;
        ImGui.SetNextItemWidth(140 * scale);
        var comboItems = canOfferForever ? "Fixed\0Until emote ends\0Forever\0" : "Fixed\0Until emote ends\0";
        UIStyles.PushEncoreComboStyle();
        if (ImGui.Combo($"##kind_{index}", ref kind, comboItems))
            step.DurationKind = (RoutineStepDuration)kind;
        UIStyles.PopEncoreComboStyle();

        if (step.DurationKind == RoutineStepDuration.Fixed)
        {
            ImGui.SameLine(0, 6f * scale);
            ImGui.SetNextItemWidth(60 * scale);
            var durText = editStepDurationText.TryGetValue(index, out var cached)
                ? cached : FormatDurationMmSs(step.DurationSeconds);
            if (ImGui.InputText($"##dur_{index}", ref durText, 8,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll))
            {
                editStepDurationText[index] = durText;
                if (TryParseDurationMmSs(durText, out var parsed)) step.DurationSeconds = parsed;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (TryParseDurationMmSs(durText, out var parsed))
                    step.DurationSeconds = Math.Max(0.1f, parsed);
                editStepDurationText[index] = FormatDurationMmSs(step.DurationSeconds);
            }
            ImGui.SameLine(0, 4f * scale);
            ImGui.SetNextItemWidth(24 * scale);
            UIStyles.PushEncoreComboStyle();
            if (ImGui.BeginCombo($"##durPreset_{index}", "", ImGuiComboFlags.NoPreview))
            {
                foreach (var (label, secs) in DurationPresets)
                {
                    if (ImGui.Selectable(label))
                    {
                        step.DurationSeconds = secs;
                        editStepDurationText[index] = FormatDurationMmSs(secs);
                    }
                }
                ImGui.EndCombo();
            }
            UIStyles.PopEncoreComboStyle();
        }
        else if (step.DurationKind == RoutineStepDuration.UntilLoopEnds)
        {
            ImGui.SameLine(0, 8f * scale);
            ImGui.TextColored(Chr_TextFaint, "(one-shot emotes only)");
        }

        PopTimeInputStyle();

        // Badge row.
        curY += rowH + 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(contentX, curY));

        if (preset != null)
        {
            var durMod = !string.IsNullOrWhiteSpace(step.ModifierName)
                ? preset.Modifiers.FirstOrDefault(m =>
                    m.Name.Equals(step.ModifierName, StringComparison.OrdinalIgnoreCase))
                : null;
            var (dstate, dur) = Plugin.Instance?.GetPresetLoopDurationState(preset, durMod)
                                ?? (Plugin.LoopDurationState.NotApplicable, 0f);
            if (dstate == Plugin.LoopDurationState.Available)
            {
                if (DrawBadge($"loop_{index}", "loop", FormatDurationMmSs(dur), Bdg_Info, ghost: false, scale))
                {
                    step.DurationSeconds = dur;
                    step.DurationKind = RoutineStepDuration.Fixed;
                    editStepDurationText[index] = FormatDurationMmSs(dur);
                }
                if (ImGui.IsItemHovered()) UIStyles.EncoreTooltip("Click to match step duration to the mod's loop length");
                ImGui.SameLine(0, 5f * scale);
            }
            else if (dstate == Plugin.LoopDurationState.Measuring)
            {
                DrawBadge($"loop_{index}", "measuring", null, Bdg_Info, ghost: true, scale);
                ImGui.SameLine(0, 5f * scale);
            }
        }

        if (preset != null && preset.Modifiers.Count > 0)
        {
            var current = string.IsNullOrWhiteSpace(step.ModifierName) ? null : step.ModifierName;
            if (current != null)
            {
                if (DrawBadge($"var_{index}", "variant", current, Bdg_Variant, ghost: false, scale))
                {
                    var idx = preset.Modifiers.FindIndex(m =>
                        m.Name.Equals(current, StringComparison.OrdinalIgnoreCase));
                    var next = (idx + 1) % (preset.Modifiers.Count + 1);
                    step.ModifierName = next == preset.Modifiers.Count ? null : preset.Modifiers[next].Name;
                }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) step.ModifierName = null;
                if (ImGui.IsItemHovered())
                    UIStyles.EncoreTooltip("Click to cycle variants - right-click to reset to base");
            }
            else
            {
                if (DrawBadge($"var_{index}", "variant", null, Bdg_Variant, ghost: true, scale))
                    step.ModifierName = preset.Modifiers[0].Name;
                if (ImGui.IsItemHovered()) UIStyles.EncoreTooltip("Click to pick a preset variant");
            }
            ImGui.SameLine(0, 5f * scale);
        }

        {
            var hasExpr = !string.IsNullOrWhiteSpace(step.LayeredEmote);
            if (DrawBadge($"exp_{index}", "expression",
                hasExpr ? step.LayeredEmote : null, Bdg_Expression,
                ghost: !hasExpr, scale))
            {
                editingLayeredEmoteStep = expressionExpanded ? -1 : index;
                if (editingLayeredEmoteStep == index) editingHeelsStep = -1;
            }
            ImGui.SameLine(0, 5f * scale);
        }

        {
            var hasHeels = step.HeelsOverride != null;
            if (DrawBadge($"heels_{index}", "heels",
                hasHeels ? "set" : null, Bdg_Heels,
                ghost: !hasHeels, scale))
            {
                ToggleHeelsExpansion(step, index);
            }
        }

        curY += frameH + 4f * scale;

        // Inline expanded editors.
        if (expressionExpanded)
        {
            curY += 6f * scale;
            drawList.AddLine(
                new Vector2(contentX, curY), new Vector2(contentR, curY),
                ColAlpha(Chr_BorderSoft, 1f), 1f);
            curY += 6f * scale;
            ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
            DrawExpressionEditor(step, index);
            curY += rowH + 8f * scale;
        }

        if (heelsExpanded)
        {
            curY += 6f * scale;
            drawList.AddLine(
                new Vector2(contentX, curY), new Vector2(contentR, curY),
                ColAlpha(Chr_BorderSoft, 1f), 1f);
            curY += 6f * scale;
            ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
            var heelsChildH = 260f * scale;
            if (ImGui.BeginChild($"##heelsInline_{index}",
                    new Vector2(contentW, heelsChildH), false,
                    ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                ImGui.SetWindowFontScale(0.94f);
                DrawInlineHeelsEditor(step, index);
                ImGui.SetWindowFontScale(1.0f);
            }
            ImGui.EndChild();
            curY += heelsChildH + 4f * scale;
        }

        // Card rect.
        var cardBottomY = Math.Max(curY + padY, markerCenter.Y + markerR + padY);
        var cardHeight = cardBottomY - startPos.Y;
        var cardRectMin = new Vector2(cardLeft, startPos.Y);
        var cardRectMax = new Vector2(cardLeft + cardWidth, startPos.Y + cardHeight);

        // Action buttons - top-right, always visible, muted until hover.
        {
            var hoveringCard = ImGui.IsMouseHoveringRect(cardRectMin, cardRectMax, false);
            var btnRowY = startPos.Y + padY;
            var btnX = cardLeft + cardWidth - padX - actionColW;
            var baseAlpha = hoveringCard || expressionExpanded || heelsExpanded ? 1f : 0.35f;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, baseAlpha);
            var defaultBg = ColU32(new Vector4(0.17f, 0.19f, 0.24f, 1f));
            var defaultHover = ColU32(new Vector4(0.24f, 0.28f, 0.35f, 1f));
            var redBg = ColU32(new Vector4(0.35f, 0.16f, 0.16f, 1f));
            var redHover = ColU32(new Vector4(0.55f, 0.25f, 0.25f, 1f));
            var bsz = new Vector2(btnW, btnH);

            if (DrawIconButton($"##up_{index}", new Vector2(btnX, btnRowY), bsz,
                               FontAwesomeIcon.ArrowUp, defaultBg, defaultHover, drawList) && index > 0)
            {
                StampReorderSlide(editSteps[index].Id,      +cardHeight);
                StampReorderSlide(editSteps[index - 1].Id,  -cardHeight);
                (editSteps[index], editSteps[index - 1]) = (editSteps[index - 1], editSteps[index]);
            }
            if (DrawIconButton($"##dn_{index}", new Vector2(btnX + btnW + btnGap, btnRowY), bsz,
                               FontAwesomeIcon.ArrowDown, defaultBg, defaultHover, drawList) && index < editSteps.Count - 1)
            {
                StampReorderSlide(editSteps[index].Id,      -cardHeight);
                StampReorderSlide(editSteps[index + 1].Id,  +cardHeight);
                (editSteps[index], editSteps[index + 1]) = (editSteps[index + 1], editSteps[index]);
            }
            if (DrawIconButton($"##del_{index}", new Vector2(btnX + (btnW + btnGap) * 2, btnRowY), bsz,
                               FontAwesomeIcon.Times, redBg, redHover, drawList)
                && !stepRemovalStartAt.ContainsKey(step.Id))
            {
                stepRemovalStartAt[step.Id] = ImGui.GetTime();
            }
            ImGui.PopStyleVar();
        }

        // Card background (channel 0, behind content).
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(cardRectMin, cardRectMax,
            ColU32(new Vector4(0.085f, 0.094f, 0.125f, 1f)));
        drawList.AddRect(cardRectMin, cardRectMax, ColAlpha(Chr_BorderSoft, 1f), 0f, 0, 1f);
        drawList.AddRectFilled(
            cardRectMin, new Vector2(cardRectMin.X + accentW, cardRectMax.Y),
            ColU32(markerColor));

        // Vertical rail blending into next step's color (or loopback rose for the last step when looping).
        var nextColor = (index < editSteps.Count - 1)
            ? (editSteps[index + 1].IsMacroStep ? Col_Macro : GetStepMarkerColor(index + 1))
            : (editRepeatLoop ? Col_Loopback : markerColor);
        var railX = markerCenter.X;
        var railTopY = index == 0 ? markerCenter.Y : startPos.Y - 6f * scale;
        var railBotY = cardRectMax.Y + 6f * scale;
        float railMidY = (railTopY + railBotY) * 0.5f;
        // Two-segment gradient: markerColor fading at top to next color at bottom.
        drawList.AddRectFilledMultiColor(
            new Vector2(railX - 0.75f * scale, railTopY),
            new Vector2(railX + 0.75f * scale, railMidY),
            ColAlpha(markerColor, 0.75f), ColAlpha(markerColor, 0.75f),
            ColAlpha(markerColor, 0.40f), ColAlpha(markerColor, 0.40f));
        drawList.AddRectFilledMultiColor(
            new Vector2(railX - 0.75f * scale, railMidY),
            new Vector2(railX + 0.75f * scale, railBotY),
            ColAlpha(nextColor, 0.40f), ColAlpha(nextColor, 0.40f),
            ColAlpha(nextColor, 0.75f), ColAlpha(nextColor, 0.75f));

        drawList.ChannelsMerge();

        if (isThisStepBeingDragged) ImGui.PopStyleVar();

        //  Arrival flash 
        DrawStepArrivalFlash(drawList, step.Id, cardRectMin, cardRectMax, markerColor, scale);

        // 60% fade + red ring; height collapses to 0 so siblings shift up smoothly
        float removalProgress = DrawStepRemovalOverlay(
            drawList, step.Id, cardRectMin, cardRectMax,
            new Vector2(
                cardRectMax.X - padX - btnW * 0.5f,
                startPos.Y + padY + btnH * 0.5f),
            scale);

        // Reserve card footprint (height collapses during removal).
        float cardFullH = cardRectMax.Y - startPos.Y;
        float cardEffH = cardFullH * (1f - removalProgress);
        float gapEff = 8f * scale * (1f - removalProgress);
        ImGui.SetCursorScreenPos(new Vector2(naturalPos.X, naturalPos.Y + cardEffH + gapEff));
        ImGui.Dummy(new Vector2(1, 1));

        // Skip drop targets if this step is being removed.
        if (removalProgress > 0f) return;

        // Drop targets (top/bottom halves) for reordering.
        var halfH = cardHeight / 2f;
        var topRect = (Min: cardRectMin, Size: new Vector2(cardWidth, halfH));
        var bottomRect = (Min: new Vector2(cardRectMin.X, cardRectMin.Y + halfH),
                          Size: new Vector2(cardWidth, halfH + 8f * scale));
        var dragActive = dragSourcePresetId != null || dragSourceStepIndex >= 0;
        if (dragActive)
        {
            var indicatorCol = ColAlpha(Chr_Accent, 0.95f);
            if (ImGui.IsMouseHoveringRect(topRect.Min, topRect.Min + topRect.Size, false))
                DrawInsertionLine(cardRectMin.X, cardRectMax.X, cardRectMin.Y - 3f * scale, indicatorCol, scale);
            else if (ImGui.IsMouseHoveringRect(bottomRect.Min, bottomRect.Min + bottomRect.Size, false))
                DrawInsertionLine(cardRectMin.X, cardRectMax.X, cardRectMax.Y + 3f * scale, indicatorCol, scale);
        }
        HandleManualDrop(topRect.Min, topRect.Size, index);
        HandleManualDrop(bottomRect.Min, bottomRect.Size, index + 1);
    }

    // 
    //   MACRO STEP CARD  -  dashed violet marker + pilcrow glyph
    // 
    private void DrawMacroStepCard(int index)
    {
        var scale = UIStyles.Scale;
        var step = editSteps[index];
        var expressionExpanded = editingLayeredEmoteStep == index;

        // Reorder slide - same treatment as preset cards.
        var naturalPos = ImGui.GetCursorScreenPos();
        float slideOffset = ComputeReorderSlideOffset(step.Id);
        if (slideOffset != 0f)
            ImGui.SetCursorScreenPos(new Vector2(naturalPos.X, naturalPos.Y + slideOffset));
        var startPos = ImGui.GetCursorScreenPos();
        var fullWidth = ImGui.GetContentRegionAvail().X - 14f * scale;

        var markerR = 15f * scale;
        var markerColW = 38f * scale;
        var accentW = 3f * scale;
        var padX = 12f * scale;
        var padY = 10f * scale;

        var cardLeft = startPos.X + markerColW;
        var cardWidth = fullWidth - markerColW;
        var contentX = cardLeft + accentW + padX;
        var contentR = cardLeft + cardWidth - padX;
        var contentW = contentR - contentX;

        var frameH = ImGui.GetFrameHeight();
        var rowH = ImGui.GetFrameHeightWithSpacing();

        var btnW = 18f * scale;
        var btnH = 17f * scale;
        var btnGap = 2f * scale;
        var actionColW = btnW * 3 + btnGap * 2;

        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        var markerCenter = new Vector2(startPos.X + markerColW * 0.5f - 4f * scale,
                                        startPos.Y + padY + frameH * 0.5f);

        DrawMacroMarker(markerCenter, Col_Macro, expressionExpanded, scale);

        var isThisStepBeingDragged = dragSourceStepIndex == index;
        ImGui.SetCursorScreenPos(new Vector2(markerCenter.X - markerR, markerCenter.Y - markerR));
        ImGui.InvisibleButton($"##stepDrag_{index}", new Vector2(markerR * 2, markerR * 2));
        if (ImGui.IsItemHovered() && !isThisStepBeingDragged)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            UIStyles.EncoreTooltip("Drag to reorder");
        }
        if (ImGui.BeginDragDropSource())
        {
            dragSourceStepIndex = index;
            dragSourcePresetId = null;
            ImGui.SetDragDropPayload("ROUTINE_STEP", ReadOnlySpan<byte>.Empty, ImGuiCond.None);
            ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(0.12f, 0.14f, 0.18f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border, ColAlpha(Col_Macro, 0.6f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f * scale, 8f * scale));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            var pdl = ImGui.GetWindowDrawList();
            var ps = ImGui.GetCursorScreenPos();
            float pr = 11f * scale;
            var pc = new Vector2(ps.X + pr, ps.Y + pr);
            pdl.AddCircleFilled(pc, pr, ColU32(new Vector4(0.085f, 0.10f, 0.14f, 1f)));
            pdl.AddCircle(pc, pr, ColU32(Col_Macro), 0, 1f);
            var piSz = ImGui.CalcTextSize("¶");
            pdl.AddText(new Vector2(pc.X - piSz.X / 2f, pc.Y - piSz.Y / 2f),
                ColU32(Col_Macro), "¶");
            ImGui.Dummy(new Vector2(pr * 2 + 4f * scale, pr * 2));
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text($"Macro #{index + 1}");
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(2);
            ImGui.EndDragDropSource();
        }

        if (isThisStepBeingDragged) ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.45f);

        // Header label.
        var curY = startPos.Y + padY;
        ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
        ImGui.AlignTextToFramePadding();
        UIStyles.DrawTrackedText(drawList,
            new Vector2(contentX, curY + (frameH - ImGui.GetTextLineHeight()) * 0.5f),
            "MACRO", ColU32(Col_Macro), 1.4f * scale);
        float macroLblW = UIStyles.MeasureTrackedWidth("MACRO", 1.4f * scale);
        drawList.AddText(
            new Vector2(contentX + macroLblW + 10f * scale,
                        curY + (frameH - ImGui.GetTextLineHeight()) * 0.5f),
            ColU32(Chr_TextFaint), "free-form");

        // Macro text area.
        curY += rowH;
        ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
        var macroLines = 4;
        var macroBoxH = ImGui.GetFrameHeight() * macroLines + 4f * scale;
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.067f, 0.075f, 0.098f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.082f, 0.090f, 0.114f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0.094f, 0.102f, 0.125f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,         ColAlpha(Col_Macro, 0.25f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        var macroText = step.MacroText;
        if (ImGui.InputTextMultiline($"##macro_{index}", ref macroText, 4096,
            new Vector2(contentW, macroBoxH), ImGuiInputTextFlags.AllowTabInput))
        {
            step.MacroText = macroText;
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        curY += macroBoxH + 6f * scale;

        // Time row.
        ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
        UIStyles.DrawTrackedText(drawList,
            new Vector2(contentX, curY + (frameH - ImGui.GetTextLineHeight()) * 0.5f),
            "TIME", ColU32(Chr_TextFaint), 1.0f * scale);
        ImGui.SetCursorScreenPos(new Vector2(contentX + 34f * scale, curY));

        PushTimeInputStyle();

        var isLastStep = index == editSteps.Count - 1;
        var canOfferForever = isLastStep && !editRepeatLoop;
        if (!canOfferForever && step.DurationKind == RoutineStepDuration.Forever)
            step.DurationKind = RoutineStepDuration.Fixed;
        var kind = (int)step.DurationKind;
        ImGui.SetNextItemWidth(140 * scale);
        var comboItems = canOfferForever ? "Fixed\0Until macro ends\0Forever\0" : "Fixed\0Until macro ends\0";
        UIStyles.PushEncoreComboStyle();
        if (ImGui.Combo($"##kind_{index}", ref kind, comboItems))
            step.DurationKind = (RoutineStepDuration)kind;
        UIStyles.PopEncoreComboStyle();

        if (step.DurationKind == RoutineStepDuration.Fixed)
        {
            ImGui.SameLine(0, 6f * scale);
            ImGui.SetNextItemWidth(60 * scale);
            var durText = editStepDurationText.TryGetValue(index, out var cached)
                ? cached : FormatDurationMmSs(step.DurationSeconds);
            if (ImGui.InputText($"##dur_{index}", ref durText, 8,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll))
            {
                editStepDurationText[index] = durText;
                if (TryParseDurationMmSs(durText, out var parsed)) step.DurationSeconds = parsed;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (TryParseDurationMmSs(durText, out var parsed))
                    step.DurationSeconds = Math.Max(0.1f, parsed);
                editStepDurationText[index] = FormatDurationMmSs(step.DurationSeconds);
            }
            ImGui.SameLine(0, 4f * scale);
            ImGui.SetNextItemWidth(24 * scale);
            UIStyles.PushEncoreComboStyle();
            if (ImGui.BeginCombo($"##durPreset_{index}", "", ImGuiComboFlags.NoPreview))
            {
                foreach (var (label, secs) in DurationPresets)
                {
                    if (ImGui.Selectable(label))
                    {
                        step.DurationSeconds = secs;
                        editStepDurationText[index] = FormatDurationMmSs(secs);
                    }
                }
                ImGui.EndCombo();
            }
            UIStyles.PopEncoreComboStyle();
        }
        else if (step.DurationKind == RoutineStepDuration.UntilLoopEnds)
        {
            ImGui.SameLine(0, 6f * scale);
            ImGui.TextColored(Chr_TextFaint, "+");
            ImGui.SameLine(0, 4f * scale);
            ImGui.SetNextItemWidth(60 * scale);
            var bufText = editStepDurationText.TryGetValue(index, out var cachedB)
                ? cachedB : FormatDurationMmSs(step.DurationSeconds);
            if (ImGui.InputText($"##buf_{index}", ref bufText, 8,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.AutoSelectAll))
            {
                editStepDurationText[index] = bufText;
                if (TryParseDurationMmSs(bufText, out var parsed)) step.DurationSeconds = parsed;
            }
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                if (TryParseDurationMmSs(bufText, out var parsed))
                    step.DurationSeconds = Math.Max(0f, parsed);
                editStepDurationText[index] = FormatDurationMmSs(step.DurationSeconds);
            }
        }

        PopTimeInputStyle();

        // Badge row - expression only.
        curY += rowH + 4f * scale;
        ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
        {
            var hasExpr = !string.IsNullOrWhiteSpace(step.LayeredEmote);
            if (DrawBadge($"exp_{index}", "expression",
                hasExpr ? step.LayeredEmote : null, Bdg_Expression,
                ghost: !hasExpr, scale))
            {
                editingLayeredEmoteStep = expressionExpanded ? -1 : index;
            }
        }
        curY += frameH + 4f * scale;

        if (expressionExpanded)
        {
            curY += 6f * scale;
            drawList.AddLine(
                new Vector2(contentX, curY), new Vector2(contentR, curY),
                ColAlpha(Chr_BorderSoft, 1f), 1f);
            curY += 6f * scale;
            ImGui.SetCursorScreenPos(new Vector2(contentX, curY));
            DrawExpressionEditor(step, index);
            curY += rowH + 8f * scale;
        }

        var cardBottomY = Math.Max(curY + padY, markerCenter.Y + markerR + padY);
        var cardHeight = cardBottomY - startPos.Y;
        var cardRectMin = new Vector2(cardLeft, startPos.Y);
        var cardRectMax = new Vector2(cardLeft + cardWidth, startPos.Y + cardHeight);

        // Action buttons.
        {
            var hoveringCard = ImGui.IsMouseHoveringRect(cardRectMin, cardRectMax, false);
            var btnRowY = startPos.Y + padY;
            var btnX = cardLeft + cardWidth - padX - actionColW;
            var baseAlpha = hoveringCard || expressionExpanded ? 1f : 0.35f;
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, baseAlpha);
            var defaultBg = ColU32(new Vector4(0.17f, 0.19f, 0.24f, 1f));
            var defaultHover = ColU32(new Vector4(0.24f, 0.28f, 0.35f, 1f));
            var redBg = ColU32(new Vector4(0.35f, 0.16f, 0.16f, 1f));
            var redHover = ColU32(new Vector4(0.55f, 0.25f, 0.25f, 1f));
            var bsz = new Vector2(btnW, btnH);

            if (DrawIconButton($"##up_{index}", new Vector2(btnX, btnRowY), bsz,
                               FontAwesomeIcon.ArrowUp, defaultBg, defaultHover, drawList) && index > 0)
            {
                StampReorderSlide(editSteps[index].Id,      +cardHeight);
                StampReorderSlide(editSteps[index - 1].Id,  -cardHeight);
                (editSteps[index], editSteps[index - 1]) = (editSteps[index - 1], editSteps[index]);
            }
            if (DrawIconButton($"##dn_{index}", new Vector2(btnX + btnW + btnGap, btnRowY), bsz,
                               FontAwesomeIcon.ArrowDown, defaultBg, defaultHover, drawList) && index < editSteps.Count - 1)
            {
                StampReorderSlide(editSteps[index].Id,      -cardHeight);
                StampReorderSlide(editSteps[index + 1].Id,  +cardHeight);
                (editSteps[index], editSteps[index + 1]) = (editSteps[index + 1], editSteps[index]);
            }
            if (DrawIconButton($"##del_{index}", new Vector2(btnX + (btnW + btnGap) * 2, btnRowY), bsz,
                               FontAwesomeIcon.Times, redBg, redHover, drawList)
                && !stepRemovalStartAt.ContainsKey(step.Id))
            {
                stepRemovalStartAt[step.Id] = ImGui.GetTime();
            }
            ImGui.PopStyleVar();
        }

        // Card bg + macro accent bar.
        drawList.ChannelsSetCurrent(0);
        drawList.AddRectFilled(cardRectMin, cardRectMax,
            ColU32(new Vector4(0.085f, 0.094f, 0.125f, 1f)));
        drawList.AddRect(cardRectMin, cardRectMax, ColAlpha(Chr_BorderSoft, 1f), 0f, 0, 1f);
        drawList.AddRectFilled(
            cardRectMin, new Vector2(cardRectMin.X + accentW, cardRectMax.Y),
            ColU32(Col_Macro));

        // Rail into next step.
        var nextColor = (index < editSteps.Count - 1)
            ? (editSteps[index + 1].IsMacroStep ? Col_Macro : GetStepMarkerColor(index + 1))
            : (editRepeatLoop ? Col_Loopback : Col_Macro);
        var railX = markerCenter.X;
        var railTopY = index == 0 ? markerCenter.Y : startPos.Y - 6f * scale;
        var railBotY = cardRectMax.Y + 6f * scale;
        float railMidY = (railTopY + railBotY) * 0.5f;
        drawList.AddRectFilledMultiColor(
            new Vector2(railX - 0.75f * scale, railTopY),
            new Vector2(railX + 0.75f * scale, railMidY),
            ColAlpha(Col_Macro, 0.75f), ColAlpha(Col_Macro, 0.75f),
            ColAlpha(Col_Macro, 0.40f), ColAlpha(Col_Macro, 0.40f));
        drawList.AddRectFilledMultiColor(
            new Vector2(railX - 0.75f * scale, railMidY),
            new Vector2(railX + 0.75f * scale, railBotY),
            ColAlpha(nextColor, 0.40f), ColAlpha(nextColor, 0.40f),
            ColAlpha(nextColor, 0.75f), ColAlpha(nextColor, 0.75f));

        drawList.ChannelsMerge();

        if (isThisStepBeingDragged) ImGui.PopStyleVar();

        //  Arrival flash 
        DrawStepArrivalFlash(drawList, step.Id, cardRectMin, cardRectMax, Col_Macro, scale);

        //  Removal animation 
        float removalProgress = DrawStepRemovalOverlay(
            drawList, step.Id, cardRectMin, cardRectMax,
            new Vector2(
                cardRectMax.X - padX - btnW * 0.5f,
                startPos.Y + padY + btnH * 0.5f),
            scale);

        float cardFullH = cardRectMax.Y - startPos.Y;
        float collapseT = StepRemovalCollapseFactor(removalProgress);
        float cardEffH = cardFullH * (1f - collapseT);
        float gapEff = 8f * scale * (1f - collapseT);
        ImGui.SetCursorScreenPos(new Vector2(naturalPos.X, naturalPos.Y + cardEffH + gapEff));
        ImGui.Dummy(new Vector2(1, 1));

        if (removalProgress > 0f) return;

        // Drop targets.
        var halfH = cardHeight / 2f;
        var topRect = (Min: cardRectMin, Size: new Vector2(cardWidth, halfH));
        var bottomRect = (Min: new Vector2(cardRectMin.X, cardRectMin.Y + halfH),
                          Size: new Vector2(cardWidth, halfH + 8f * scale));
        var dragActive = dragSourcePresetId != null || dragSourceStepIndex >= 0;
        if (dragActive)
        {
            var indicatorCol = ColAlpha(Col_Macro, 0.95f);
            if (ImGui.IsMouseHoveringRect(topRect.Min, topRect.Min + topRect.Size, false))
                DrawInsertionLine(cardRectMin.X, cardRectMax.X, cardRectMin.Y - 3f * scale, indicatorCol, scale);
            else if (ImGui.IsMouseHoveringRect(bottomRect.Min, bottomRect.Min + bottomRect.Size, false))
                DrawInsertionLine(cardRectMin.X, cardRectMax.X, cardRectMax.Y + 3f * scale, indicatorCol, scale);
        }
        HandleManualDrop(topRect.Min, topRect.Size, index);
        HandleManualDrop(bottomRect.Min, bottomRect.Size, index + 1);
    }

    private static void DrawMacroMarker(Vector2 center, Vector4 color, bool active, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        float r = 15f * scale;
        var bgCol = ColU32(new Vector4(0.085f, 0.10f, 0.14f, 1f));
        var borderCol = ColAlpha(color, active ? 1.0f : 0.85f);

        dl.AddCircleFilled(center, r, bgCol);
        // Dashed circle to signify macro.
        int segs = 32;
        for (int i = 0; i < segs; i++)
        {
            if ((i % 2) != 0) continue;
            float a0 = MathF.PI * 2f * i / segs;
            float a1 = MathF.PI * 2f * (i + 1) / segs;
            var p0 = new Vector2(center.X + MathF.Cos(a0) * r, center.Y + MathF.Sin(a0) * r);
            var p1 = new Vector2(center.X + MathF.Cos(a1) * r, center.Y + MathF.Sin(a1) * r);
            dl.AddLine(p0, p1, borderCol, active ? 1.6f : 1.2f);
        }
        if (active)
            dl.AddCircle(center, r + 2f * scale, ColAlpha(color, 0.35f), 0, 1f);

        string glyph = "¶";
        var sz = ImGui.CalcTextSize(glyph);
        dl.AddText(new Vector2(center.X - sz.X / 2f, center.Y - sz.Y / 2f),
            ColU32(color), glyph);
    }

    // Standard numbered step marker (preset steps).
    private static (Vector2 min, Vector2 max) DrawStepMarker(Vector2 center, string label, Vector4 color, bool active, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var r = 15f * scale;
        var bgCol = ColU32(new Vector4(0.085f, 0.10f, 0.14f, 1f));
        var borderCol = ColAlpha(color, active ? 1.0f : 0.85f);
        var textCol = ColAlpha(color, active ? 1.0f : 0.95f);

        // Outer ring-cutout so rail visually passes behind but marker reads "on top".
        dl.AddCircleFilled(center, r + 4f * scale, ColU32(new Vector4(0.047f, 0.055f, 0.075f, 1f)));
        dl.AddCircleFilled(center, r, bgCol);
        dl.AddCircle(center, r, borderCol, 0, active ? 2f : 1.5f);

        if (active)
            dl.AddCircle(center, r + 2f * scale, ColAlpha(color, 0.45f), 0, 1f);

        var sz = ImGui.CalcTextSize(label);
        dl.AddText(new Vector2(center.X - sz.X / 2f, center.Y - sz.Y / 2f), textCol, label);

        return (new Vector2(center.X - r, center.Y - r), new Vector2(center.X + r, center.Y + r));
    }

    // 
    //   FOOTER  -  rainbow EQ along top edge + stats + Cancel/Save
    // 
    private void DrawFooter()
    {
        // Delayed close after Save - lets the kick animation play through.
        if (savePendingClose && ImGui.GetTime() >= savePendingCloseAt)
        {
            SaveRoutine();
            Confirmed = true;
            IsOpen = false;
            savePendingClose = false;
        }

        // Recompute validity each draw - cheap and always accurate.
        bool isValid = Validate();

        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availW = ImGui.GetContentRegionAvail().X;
        float footerH = 48f * scale;
        var end = new Vector2(start.X + availW, start.Y + footerH);

        uint bgCol = ColU32(new Vector4(0.031f, 0.039f, 0.055f, 1f));
        dl.AddRectFilled(start, end, bgCol);
        dl.AddLine(start, new Vector2(end.X, start.Y), ColU32(Chr_Border), 1f);

        // Rainbow EQ bars - straddle the top border, 8 colored bars.
        DrawFooterEq(dl, start, scale);

        float padX = 18f * scale;
        float contentY = start.Y + footerH * 0.5f;
        float textH = ImGui.GetTextLineHeight();
        float textY = contentY - textH * 0.5f;

        // Status line.
        string statusText;
        Vector4 statusCol;
        if (nameError != null) { statusText = nameError; statusCol = Col_Error; }
        else if (commandError != null) { statusText = commandError; statusCol = Col_Error; }
        else if (string.IsNullOrWhiteSpace(editName)) { statusText = "Routine name required"; statusCol = Col_Warning; }
        else if (editSteps.Count == 0) { statusText = "Empty timeline - add a step to save"; statusCol = Col_Warning; }
        else { statusText = "Ready to save"; statusCol = Chr_Accent; }

        // Breathing status dot on valid state.
        float dotR = 3.5f * scale;
        float dotX = start.X + padX + dotR;
        float dotY = contentY;
        float dotAlpha = 1f;
        if (isValid && nameError == null && commandError == null)
        {
            float t = (float)(ImGui.GetTime() * MathF.Tau / 2.2f);
            dotAlpha = 0.75f + 0.25f * (0.5f + 0.5f * MathF.Sin(t));
        }
        for (int r = 3; r >= 1; r--)
        {
            float pad = r * 1.5f * scale;
            float a = (0.18f / r) * dotAlpha;
            dl.AddCircleFilled(new Vector2(dotX, dotY), dotR + pad,
                ColAlpha(statusCol, a));
        }
        dl.AddCircleFilled(new Vector2(dotX, dotY), dotR,
            ColAlpha(statusCol, dotAlpha));
        dl.AddText(new Vector2(dotX + dotR + 8f * scale, textY),
            ColU32(statusCol), statusText);

        // Buttons on the right.
        float btnH = 28f * scale;
        float saveBtnW = 128f * scale;
        float cancelBtnW = 78f * scale;
        float btnGap = 10f * scale;

        float saveX = end.X - padX - saveBtnW;
        float saveY = contentY - btnH * 0.5f;
        float cancelX = saveX - btnGap - cancelBtnW;

        var cancelMin = new Vector2(cancelX, saveY);
        var cancelMax = new Vector2(cancelX + cancelBtnW, saveY + btnH);
        bool cancelHovered = ImGui.IsMouseHoveringRect(cancelMin, cancelMax);
        if (cancelHovered)
        {
            for (int r = 3; r >= 1; r--)
            {
                float pad = r * 1.5f * scale;
                float a = 0.10f / r;
                dl.AddRectFilled(
                    new Vector2(cancelMin.X - pad, cancelMin.Y - pad),
                    new Vector2(cancelMax.X + pad, cancelMax.Y + pad),
                    ColAlpha(Chr_TextDim, a));
            }
        }
        ImGui.SetCursorScreenPos(cancelMin);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.20f, 0.25f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.24f, 0.26f, 0.32f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,
            cancelHovered ? Chr_TextDim : new Vector4(0.24f, 0.26f, 0.32f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text, cancelHovered ? Chr_Text : Chr_TextDim);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        if (ImGui.Button("CANCEL", new Vector2(cancelBtnW, btnH)))
        {
            Confirmed = false;
            IsOpen = false;
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);

        // Save - primary with bloom halo + play-kick.
        var saveMin = new Vector2(saveX, saveY);
        var saveMax = new Vector2(saveX + saveBtnW, saveY + btnH);
        if (isValid)
            UIStyles.DrawPlayButtonBloom(dl, saveMin, saveMax, scale, Chr_Accent);

        ImGui.SetCursorScreenPos(saveMin);
        if (!isValid)
        {
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, 0.5f);
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.14f, 0.16f, 0.20f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border,        new Vector4(0.24f, 0.26f, 0.32f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Text,          Chr_TextFaint);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            ImGui.Button("SAVE ROUTINE", new Vector2(saveBtnW, btnH));
            ImGui.PopStyleVar(2);
            ImGui.PopStyleColor(5);
        }
        else
        {
            float kick = 1f;
            if (saveClickTime >= 0)
                kick = UIStyles.PlayKickScale((float)(ImGui.GetTime() - saveClickTime));
            if (UIStyles.DrawPlayButton("##routineSave",
                    new Vector2(saveBtnW, btnH), kick, scale,
                    label: "SAVE ROUTINE",
                    restCol:  Chr_Accent,
                    hoverCol: Chr_AccentBright,
                    heldCol:  Chr_AccentDeep,
                    borderCol: Chr_AccentDeep,
                    textColor: Chr_AccentDark)
                && !savePendingClose)
            {
                saveClickTime = ImGui.GetTime();
                savePendingClose = true;
                // 0.5s for kick-scale + bloom to finish before window closes
                savePendingCloseAt = ImGui.GetTime() + 0.5;
            }
        }
    }

    // 8 colored EQ bars straddling the top border - phase-offset sine heights.
    private static void DrawFooterEq(ImDrawListPtr dl, Vector2 footerStart, float scale)
    {
        float t = (float)ImGui.GetTime();
        float barW = 2.5f * scale;
        float barGap = 3f * scale;
        // Maximum bar height; bars peek above the top border (negative Y relative to
        // footerStart.Y) by roughly half this height.
        float maxH = 14f * scale;
        float x = footerStart.X + 18f * scale;
        // Per-bar heights (relative to maxH), matching the HTML percentages.
        float[] heightPct = { 0.40f, 0.60f, 0.75f, 0.85f, 0.70f, 0.55f, 0.42f, 0.30f };
        // Mirrored animation delays (HTML: 0, 0.12, 0.24, 0.36, 0.48, 0.36, 0.24, 0.12).
        float[] delay = { 0f, 0.12f, 0.24f, 0.36f, 0.48f, 0.36f, 0.24f, 0.12f };
        const float period = 2.8f;
        for (int i = 0; i < 8; i++)
        {
            float phase = (t + delay[i]) % period / period;
            // Base scale runs 0.35 -> 1.20 -> 0.35 over one period.
            float scaleY = 0.35f + 0.425f * (1f - MathF.Cos(phase * MathF.Tau));
            float h = maxH * heightPct[i] * scaleY;
            // Sit on top of the footer border: bar bottom at footerStart.Y + 1,
            // growing upward. Alpha a touch under full so the stack reads lively,
            // not neon.
            var min = new Vector2(x, footerStart.Y + 1f - h);
            var max = new Vector2(x + barW, footerStart.Y + 1f);
            dl.AddRectFilled(min, max, ColAlpha(FooterEqColors[i], 0.70f));
            x += barW + barGap;
        }
    }

    // 
    //   CORNER BRACKETS
    // 
    private void DrawWindowCornerBrackets()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winSize = ImGui.GetWindowSize();
        float armLen = 14f * scale;
        float inset = 6f * scale;
        uint col = ColAlpha(Chr_Accent, 0.50f);
        float left = winPos.X + inset;
        float right = winPos.X + winSize.X - inset;
        float bottom = winPos.Y + winSize.Y - inset;
        dl.AddLine(new Vector2(left, bottom - armLen), new Vector2(left, bottom), col, 1f);
        dl.AddLine(new Vector2(left, bottom), new Vector2(left + armLen, bottom), col, 1f);
        dl.AddLine(new Vector2(right - armLen, bottom), new Vector2(right, bottom), col, 1f);
        dl.AddLine(new Vector2(right, bottom - armLen), new Vector2(right, bottom), col, 1f);
    }

    // 
    //   PANEL HEAD / HINT  -  eyebrow + title + meta, under a thin hint line
    // 
    private void DrawPanelHead(string num, string title, string meta, Vector4 accent)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        float padX = 14f * scale;
        float rowY = start.Y + 12f * scale;

        // Number label ("A" / "B") - accent, 0.16em ~= 1.6px tracking (HTML panel-num).
        float numTrack = 1.6f * scale;
        float numW = UIStyles.MeasureTrackedWidth(num, numTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(start.X + padX, rowY),
            num, ColU32(accent), numTrack);

        // Title - 0.28em ~= 3.0px tracking (HTML panel-title).
        float titleTrack = 3.0f * scale;
        float titleX = start.X + padX + numW + 10f * scale;
        UIStyles.DrawTrackedText(dl, new Vector2(titleX, rowY),
            title, ColU32(Chr_Text), titleTrack);

        // Right-side meta - 0.14em ~= 1.3px tracking (HTML panel-meta).
        if (!string.IsNullOrEmpty(meta))
        {
            float metaTrack = 1.3f * scale;
            string metaUpper = meta.ToUpperInvariant();
            float metaW = UIStyles.MeasureTrackedWidth(metaUpper, metaTrack);
            float metaRightPad = ImGui.GetStyle().ScrollbarSize + padX;
            UIStyles.DrawTrackedText(dl,
                new Vector2(start.X + w - metaRightPad - metaW, rowY),
                metaUpper, ColU32(accent), metaTrack);
        }

        ImGui.SetCursorScreenPos(new Vector2(start.X,
            rowY + ImGui.GetTextLineHeight() + 4f * scale));
        ImGui.Dummy(new Vector2(1, 1));
    }

    private void DrawPanelHint(string text)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float padX = 14f * scale;
        string upper = text.ToUpperInvariant();
        float track = 1.0f * scale;

        // Shrink the hint text slightly - it's a quiet eyebrow, not a headline.
        ImGui.SetWindowFontScale(0.85f);
        try
        {
            UIStyles.DrawTrackedText(dl,
                new Vector2(start.X + padX, start.Y),
                upper, ColU32(Chr_TextFaint), track);
            ImGui.Dummy(new Vector2(1, ImGui.GetTextLineHeight() + 6f * scale));
        }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    // 
    //   BADGE  -  colored chip with optional inline value; ghost = dashed
    // 
    private bool DrawBadge(string idSalt, string label, string? value, Vector4 color, bool ghost, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        var padX = 9f * scale;
        var padY = 4f * scale;
        var gap = 6f * scale;

        var displayText = ghost ? "+ " + label : label;
        var displaySz = ImGui.CalcTextSize(displayText);
        var valueSz = (!ghost && !string.IsNullOrEmpty(value)) ? ImGui.CalcTextSize(value) : Vector2.Zero;
        var sep = valueSz.X > 0 ? gap : 0f;
        var w = padX * 2 + displaySz.X + sep + valueSz.X;
        var h = displaySz.Y + padY * 2;

        var pos = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton($"##badge_{idSalt}", new Vector2(w, h));
        var clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hovered = ImGui.IsItemHovered();

        var min = pos;
        var max = new Vector2(pos.X + w, pos.Y + h);
        if (ghost)
        {
            DrawDashedRect(dl, min, max,
                ColAlpha(hovered ? Chr_TextDim : Chr_Border, 1f),
                5f * scale, 3f * scale, 1f);
            var ghostCol = ColU32(hovered ? Chr_TextDim : Chr_TextFaint);
            dl.AddText(new Vector2(min.X + padX, min.Y + padY), ghostCol, displayText);
        }
        else
        {
            var bg = ColAlpha(color, hovered ? 0.30f : 0.20f);
            var border = ColAlpha(color, hovered ? 0.70f : 0.55f);
            dl.AddRectFilled(min, max, bg);
            dl.AddRect(min, max, border, 0f, 0, 1f);
            var tc = ColU32(color);
            dl.AddText(new Vector2(min.X + padX, min.Y + padY), tc, label);
            if (valueSz.X > 0)
            {
                var valX = min.X + padX + displaySz.X + gap;
                dl.AddText(new Vector2(valX, min.Y + padY), ColAlpha(color, 0.85f), value!);
            }
        }
        return clicked;
    }

    // 
    //   EXPRESSION + HEELS editors (unchanged logic - just restyled via family)
    // 
    private void DrawExpressionEditor(RoutineStep step, int index)
    {
        var scale = UIStyles.Scale;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("emote:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150 * scale);
        var expressions = Plugin.Instance?.ExpressionEmotes ?? new List<(string Command, string Label)>();
        var currentLabel = "(none)";
        if (!string.IsNullOrWhiteSpace(step.LayeredEmote))
        {
            var match = expressions.FirstOrDefault(e =>
                string.Equals(e.Command, step.LayeredEmote, StringComparison.OrdinalIgnoreCase));
            currentLabel = match.Label ?? step.LayeredEmote;
        }
        UIStyles.PushEncoreComboStyle();
        if (ImGui.BeginCombo($"##le_{index}", currentLabel))
        {
            if (ImGui.Selectable("(none)", string.IsNullOrEmpty(step.LayeredEmote)))
                step.LayeredEmote = "";
            foreach (var (cmd, label) in expressions)
            {
                var isSel = string.Equals(cmd, step.LayeredEmote, StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable($"{label}  ({cmd})", isSel))
                    step.LayeredEmote = cmd;
            }
            ImGui.EndCombo();
        }
        UIStyles.PopEncoreComboStyle();

        ImGui.SameLine();
        var hold = step.HoldExpression;
        if (ImGui.Checkbox($"Hold##hold_{index}", ref hold))
            step.HoldExpression = hold;
        if (ImGui.IsItemHovered())
            UIStyles.EncoreTooltip("Re-fires the expression every few seconds so it stays on the face for the whole step.");

        if (!hold)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("delay:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(70 * scale);
            var ld = step.LayerDelaySeconds;
            if (ImGui.InputFloat($"##ld_{index}", ref ld, 0f, 0f, "%.1f s"))
                step.LayerDelaySeconds = Math.Max(0f, ld);
        }
    }

    private void ToggleHeelsExpansion(RoutineStep step, int index)
    {
        var wasExpanded = editingHeelsStep == index;
        editingHeelsStep = wasExpanded ? -1 : index;
        if (editingHeelsStep == index)
        {
            editingLayeredEmoteStep = -1;
            heelsPopupStepIndex = index;
            heelsPopupTarget = new HeelsGizmoTarget();
            if (step.HeelsOverride != null)
            {
                heelsPopupTarget.X = step.HeelsOverride.X;
                heelsPopupTarget.Y = step.HeelsOverride.Y;
                heelsPopupTarget.Z = step.HeelsOverride.Z;
                heelsPopupTarget.Rotation = step.HeelsOverride.Rotation;
                heelsPopupTarget.Pitch = step.HeelsOverride.Pitch;
                heelsPopupTarget.Roll = step.HeelsOverride.Roll;
            }
        }
        else
        {
            if (HeelsGizmoOverlay.Target == heelsPopupTarget && heelsPopupTarget != null)
            {
                HeelsGizmoOverlay.Target = null;
                HeelsGizmoOverlay.Label = null;
            }
            heelsPopupTarget = null;
            heelsPopupStepIndex = -1;
            Plugin.Instance?.RefreshActivePresetHeels();
        }
    }

    private void DrawInlineHeelsEditor(RoutineStep step, int index)
    {
        if (heelsPopupTarget == null || heelsPopupStepIndex != index) return;

        var heelsAvailable = Plugin.Instance?.SimpleHeelsService?.IsAvailable ?? false;
        ImGui.TextColored(Bdg_Heels, "Per-step heel override");
        ImGui.SameLine();
        ImGui.TextDisabled(heelsAvailable
            ? "- overrides the preset's heels for this step"
            : "- Simple Heels not detected");

        HeelsGizmoOverlay.Target = heelsPopupTarget;
        HeelsGizmoOverlay.Label = $"Routine step {index + 1}";

        var sh = Plugin.Instance?.SimpleHeelsService;
        if (sh != null && sh.IsAvailable)
            sh.ApplyOffset(heelsPopupTarget.X, heelsPopupTarget.Y, heelsPopupTarget.Z,
                heelsPopupTarget.Rotation, heelsPopupTarget.Pitch, heelsPopupTarget.Roll);

        PresetEditorWindow.DrawHeelsControls(heelsPopupTarget);

        var t = heelsPopupTarget;
        if (MathF.Abs(t.X) < 0.0001f && MathF.Abs(t.Y) < 0.0001f && MathF.Abs(t.Z) < 0.0001f &&
            MathF.Abs(t.Rotation) < 0.0001f && MathF.Abs(t.Pitch) < 0.0001f && MathF.Abs(t.Roll) < 0.0001f)
        {
            step.HeelsOverride = null;
        }
        else
        {
            step.HeelsOverride = new HeelsOffset
            {
                X = t.X, Y = t.Y, Z = t.Z,
                Rotation = t.Rotation, Pitch = t.Pitch, Roll = t.Roll,
            };
        }

        if (step.HeelsOverride != null)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Clear override##heelsClr_{index}"))
            {
                step.HeelsOverride = null;
                t.X = t.Y = t.Z = 0f;
                t.Rotation = t.Pitch = t.Roll = 0f;
            }
        }
    }

    // returns 0..1 progress; caller derives delayed height-collapse
    private float DrawStepRemovalOverlay(ImDrawListPtr dl, string stepId,
        Vector2 cardMin, Vector2 cardMax, Vector2 xBtnCenter, float scale)
    {
        if (!stepRemovalStartAt.TryGetValue(stepId, out var removedAt)) return 0f;
        float elapsed = (float)(ImGui.GetTime() - removedAt);
        float progress = elapsed / StepRemovalDurSec;
        if (progress < 0f) progress = 0f;
        if (progress > 1f) progress = 1f;

        float ss = progress * progress * (3f - 2f * progress);

        dl.AddRectFilled(cardMin, cardMax,
            ColU32(new Vector4(0.047f, 0.055f, 0.075f, ss)));

        return progress;
    }

    // Maps raw removal progress (0..1) to a collapse factor (0..1) that
    // holds at 0 for the first 40% of the animation (card stays full
    // height while the fade gets a head start), then smoothsteps to 1.
    private static float StepRemovalCollapseFactor(float progress)
    {
        if (progress < 0.40f) return 0f;
        float c = (progress - 0.40f) / 0.60f;
        return c * c * (3f - 2f * c);
    }

    // initialYOffset = old Y - new Y (positive = was below); animates to 0
    private void StampReorderSlide(string stepId, float initialYOffset)
    {
        stepSlideStartAt[stepId] = ImGui.GetTime();
        stepSlideStartOffset[stepId] = initialYOffset;
    }

    // Returns the current slide offset for this step's card (0 if no slide
    // active). Uses easeOutCubic so the card decelerates into its slot.
    // Removes the entry from the dicts when the animation completes.
    private float ComputeReorderSlideOffset(string stepId)
    {
        if (!stepSlideStartAt.TryGetValue(stepId, out var startAt)) return 0f;
        float elapsed = (float)(ImGui.GetTime() - startAt);
        if (elapsed >= StepSlideDurSec)
        {
            stepSlideStartAt.Remove(stepId);
            stepSlideStartOffset.Remove(stepId);
            return 0f;
        }
        if (!stepSlideStartOffset.TryGetValue(stepId, out var init)) return 0f;
        float t = elapsed / StepSlideDurSec;
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);  // easeOutCubic
        return init * (1f - eased);
    }

    // Arrival flash - accent-tint overlay + expanding ring for ~500ms after
    // a step is first created. Removes itself from the dict when done.
    private void DrawStepArrivalFlash(ImDrawListPtr dl, string stepId,
        Vector2 cardMin, Vector2 cardMax, Vector4 accent, float scale)
    {
        if (!stepCreatedAt.TryGetValue(stepId, out var createdAt)) return;
        float elapsed = (float)(ImGui.GetTime() - createdAt);
        const float flashDur = 0.55f;
        if (elapsed >= flashDur)
        {
            stepCreatedAt.Remove(stepId);
            return;
        }
        float t = elapsed / flashDur;
        float easeOut = (1f - t) * (1f - t);  // quadratic ease-out

        // Accent tint overlay over the whole card.
        dl.AddRectFilled(cardMin, cardMax,
            ColAlpha(accent, easeOut * 0.32f));

        // Expanding ring - first 250ms only.
        if (elapsed < 0.25f)
        {
            float rt = elapsed / 0.25f;
            float pad = rt * 5f * scale;
            float ringAlpha = (1f - rt) * 0.80f;
            dl.AddRect(
                new Vector2(cardMin.X - pad, cardMin.Y - pad),
                new Vector2(cardMax.X + pad, cardMax.Y + pad),
                ColAlpha(accent, ringAlpha), 0f, 0, 1.5f);
        }
    }

    private static void DrawInsertionLine(float x0, float x1, float y, uint color, float scale)
    {
        var dl = ImGui.GetWindowDrawList();
        dl.AddLine(new Vector2(x0, y), new Vector2(x1, y), color, 3f * scale);
        dl.AddCircleFilled(new Vector2(x0, y), 4f * scale, color);
        dl.AddCircleFilled(new Vector2(x1, y), 4f * scale, color);
    }

    private void HandleManualDrop(Vector2 rectMin, Vector2 rectSize, int targetIndex)
    {
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left)) return;
        var rectMax = rectMin + rectSize;
        if (!ImGui.IsMouseHoveringRect(rectMin, rectMax, false)) return;

        if (dragSourcePresetId != null)
        {
            var newStep = new RoutineStep
            {
                PresetId = dragSourcePresetId,
                DurationKind = RoutineStepDuration.Fixed,
                DurationSeconds = 5f,
            };
            if (targetIndex >= editSteps.Count)
                editSteps.Add(newStep);
            else
                editSteps.Insert(targetIndex, newStep);
            stepCreatedAt[newStep.Id] = ImGui.GetTime();   // arrival flash
            dragSourcePresetId = null;
        }
        else if (dragSourceStepIndex >= 0 && dragSourceStepIndex < editSteps.Count &&
                 dragSourceStepIndex != targetIndex)
        {
            var moving = editSteps[dragSourceStepIndex];
            editSteps.RemoveAt(dragSourceStepIndex);
            var adjTarget = targetIndex > dragSourceStepIndex ? targetIndex - 1 : targetIndex;
            if (adjTarget >= editSteps.Count)
                editSteps.Add(moving);
            else
                editSteps.Insert(adjTarget, moving);
            dragSourceStepIndex = -1;
        }
    }

    // 
    //   SAVE / VALIDATE
    // 
    private bool Validate()
    {
        nameError = null;
        commandError = null;

        if (string.IsNullOrWhiteSpace(editName))
        {
            nameError = "Name is required";
            return false;
        }

        var cmd = editCommand.TrimStart('/').Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(cmd))
        {
            if (cmd == "encore" || cmd == "encorereset")
            {
                commandError = $"/{cmd} is reserved";
                return false;
            }
            var config = Plugin.Instance?.Configuration;
            if (config != null)
            {
                if (config.Routines.Any(r => r.Id != CurrentRoutine?.Id &&
                    string.Equals(r.ChatCommand.TrimStart('/'), cmd, StringComparison.OrdinalIgnoreCase)))
                {
                    commandError = $"/{cmd} is already used by another routine";
                    return false;
                }
                if (config.Presets.Any(p => string.Equals(p.ChatCommand.TrimStart('/'), cmd, StringComparison.OrdinalIgnoreCase)))
                {
                    commandError = $"/{cmd} is already used by a preset";
                    return false;
                }
            }
        }

        if (editSteps.Count == 0)
            return false;

        return true;
    }

    private void SaveRoutine()
    {
        if (CurrentRoutine == null)
            CurrentRoutine = new Routine();

        CurrentRoutine.Name = editName.Trim();
        CurrentRoutine.ChatCommand = editCommand.TrimStart('/').Trim();
        CurrentRoutine.RepeatLoop = editRepeatLoop;
        CurrentRoutine.IconId = editIconId;
        CurrentRoutine.CustomIconPath = editCustomIconPath;
        CurrentRoutine.IconZoom = editIconZoom;
        CurrentRoutine.IconOffsetX = editIconOffsetX;
        CurrentRoutine.IconOffsetY = editIconOffsetY;
        CurrentRoutine.Steps = editSteps.Select(s => s.Clone()).ToList();
    }

    // 
    //   DURATION / MACRO HELPERS
    // 
    private static string FormatDurationMmSs(float totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var mins = (int)(totalSeconds / 60);
        var secs = totalSeconds - mins * 60;
        if (Math.Abs(secs - Math.Round(secs)) < 0.05f)
            return $"{mins}:{(int)Math.Round(secs):D2}";
        return $"{mins}:{secs:00.0}";
    }

    private static bool TryParseDurationMmSs(string text, out float seconds)
    {
        seconds = 0f;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        var parts = text.Split(':');
        if (parts.Length == 1)
        {
            return float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds);
        }
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out var mins) &&
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
        {
            seconds = mins * 60f + secs;
            return true;
        }
        return false;
    }

    private float ComputeRoutineTotalSeconds(Configuration? config)
    {
        if (config == null) return 0f;
        float total = 0f;
        foreach (var step in editSteps)
        {
            switch (step.DurationKind)
            {
                case RoutineStepDuration.Fixed:
                    total += step.IsMacroStep
                        ? Math.Max(step.DurationSeconds, SumMacroWaits(step.MacroText))
                        : step.DurationSeconds;
                    break;
                case RoutineStepDuration.UntilLoopEnds:
                    if (!step.IsMacroStep)
                    {
                        var preset = config.Presets.Find(p => p.Id == step.PresetId);
                        if (preset != null)
                        {
                            var mod = !string.IsNullOrWhiteSpace(step.ModifierName)
                                ? preset.Modifiers.FirstOrDefault(m =>
                                    m.Name.Equals(step.ModifierName, StringComparison.OrdinalIgnoreCase))
                                : null;
                            var (s, d) = Plugin.Instance?.GetPresetLoopDurationState(preset, mod)
                                         ?? (Plugin.LoopDurationState.NotApplicable, 0f);
                            total += s == Plugin.LoopDurationState.Available ? d : step.DurationSeconds;
                        }
                    }
                    else
                    {
                        total += SumMacroWaits(step.MacroText) + step.DurationSeconds;
                    }
                    break;
                case RoutineStepDuration.Forever:
                    break;
            }
        }
        return total;
    }

    private static float SumMacroWaits(string? macroText)
    {
        if (string.IsNullOrWhiteSpace(macroText)) return 0f;
        float sum = 0f;
        foreach (var rawLine in macroText.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = line.Substring(5).Trim();
            var sb = new System.Text.StringBuilder();
            foreach (var c in rest)
            {
                if (char.IsDigit(c) || c == '.' || c == '-') sb.Append(c);
                else if (sb.Length > 0) break;
            }
            if (sb.Length > 0 && float.TryParse(sb.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var secs))
            {
                if (secs > 0) sum += secs;
            }
        }
        return sum;
    }

    // 
    //   TRUNCATION / DASHED RECT / ICON BUTTON
    // 
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

    private static void DrawDashedRect(ImDrawListPtr dl, Vector2 min, Vector2 max,
        uint col, float dashLen, float gapLen, float thickness)
    {
        float step = dashLen + gapLen;
        for (float x = min.X; x < max.X; x += step)
        {
            float xe = MathF.Min(x + dashLen, max.X);
            dl.AddLine(new Vector2(x, min.Y), new Vector2(xe, min.Y), col, thickness);
            dl.AddLine(new Vector2(x, max.Y), new Vector2(xe, max.Y), col, thickness);
        }
        for (float y = min.Y; y < max.Y; y += step)
        {
            float ye = MathF.Min(y + dashLen, max.Y);
            dl.AddLine(new Vector2(min.X, y), new Vector2(min.X, ye), col, thickness);
            dl.AddLine(new Vector2(max.X, y), new Vector2(max.X, ye), col, thickness);
        }
    }

    // Manually-drawn icon button - guarantees the glyph is pixel-centered on its
    // visible bounds rather than its advance width.
    private static bool DrawIconButton(string id, Vector2 pos, Vector2 size,
                                       FontAwesomeIcon icon, uint bgColor, uint hoverColor,
                                       ImDrawListPtr drawList)
    {
        ImGui.SetCursorScreenPos(pos);
        // Use InvisibleButton's return value (fires on a full press+release)
        // rather than IsItemClicked (which fires on mouse-down and can miss
        // frames when the click lands simultaneously with layout changes).
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();

        drawList.AddRectFilled(pos, pos + size, hovered ? hoverColor : bgColor);

        var glyph = icon.ToIconString();
        float visibleX0 = 0f;
        Vector2 glyphSize;
        float visibleWidth;
        ImGui.PushFont(UiBuilder.IconFont);
        glyphSize = ImGui.CalcTextSize(glyph);
        visibleWidth = glyphSize.X;
        if (!string.IsNullOrEmpty(glyph))
        {
            try
            {
                unsafe
                {
                    var glyphPtr = ImGui.GetFont().FindGlyph(glyph[0]);
                    if (glyphPtr != null)
                    {
                        visibleX0 = glyphPtr->X0;
                        visibleWidth = glyphPtr->X1 - glyphPtr->X0;
                    }
                }
            }
            catch { }
        }
        float targetLeftX = pos.X + (size.X - visibleWidth) * 0.5f;
        var glyphPos = new Vector2(
            targetLeftX - visibleX0,
            pos.Y + (size.Y - glyphSize.Y) * 0.5f);
        drawList.AddText(glyphPos, ImGui.GetColorU32(ImGuiCol.Text), glyph);
        ImGui.PopFont();

        return clicked;
    }

    // 
    //   STYLE HELPERS
    // 
    private static void PushSmallChipButton()
    {
        var scale = UIStyles.Scale;
        var accent = new Vector4(0.49f, 0.65f, 0.85f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.09f, 0.10f, 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(accent.X * 0.20f + 0.09f, accent.Y * 0.20f + 0.10f, accent.Z * 0.20f + 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(accent.X * 0.30f + 0.09f, accent.Y * 0.30f + 0.10f, accent.Z * 0.30f + 0.14f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,        new Vector4(accent.X * 0.55f, accent.Y * 0.55f, accent.Z * 0.55f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Text,          accent);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f * scale, 4f * scale));
    }
    private static void PopSmallChipButton()
    {
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);
    }

    private static void PushTimeInputStyle()
    {
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0.125f, 0.140f, 0.180f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0.150f, 0.168f, 0.210f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0.170f, 0.190f, 0.236f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Button,         new Vector4(0.125f, 0.140f, 0.180f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered,  new Vector4(0.150f, 0.168f, 0.210f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,   new Vector4(0.170f, 0.190f, 0.236f, 1f));
        ImGui.PushStyleColor(ImGuiCol.PopupBg,        new Vector4(0.086f, 0.098f, 0.133f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border,         new Vector4(0.184f, 0.208f, 0.259f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
    }
    private static void PopTimeInputStyle()
    {
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(8);
    }

    // 
    //   ICON PICKER / CUSTOM ICON
    // 
    private static IDalamudTextureWrap? GetCustomIcon(string path)
    {
        try { return Plugin.TextureProvider.GetFromFile(path)?.GetWrapOrEmpty(); }
        catch { return null; }
    }

    private void HandleIconPickerCompletion()
    {
        if (iconPickerWindow == null || iconPickerWindow.IsOpen || !iconPickerWindow.Confirmed)
            return;
        editIconId = iconPickerWindow.SelectedIconId;
        editCustomIconPath = null;
        editIconZoom = 1f;
        editIconOffsetX = 0f;
        editIconOffsetY = 0f;
        iconPickerWindow.Confirmed = false;
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
                try { File.Copy(sourcePath, destPath, true); }
                catch (Exception ex) { Plugin.Log.Error($"Failed to copy icon: {ex.Message}"); return; }
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
}
