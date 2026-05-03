using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Encore.Styles;

namespace Encore.Windows;

public class MainWindow : Window, IDisposable
{
    private PresetEditorWindow? editorWindow;
    private RoutineEditorWindow? routineEditorWindow;
    private HelpWindow? helpWindow;

    // Tab state
    private enum MainTab { Presets, Routines }
    private MainTab currentTab = MainTab.Presets;
    private PatchNotesWindow? patchNotesWindow;
    private int selectedPresetIndex = -1;

    // Sorting options
    private enum SortMode { Custom, Name, Command, Favorites, Newest, Oldest }
    private SortMode currentSort = SortMode.Custom;

    // Search filter
    private string presetSearchFilter = "";

    // Drag & drop state
    private int dragSourceIndex = -1;
    private string? dragSourcePresetId = null;
    private string? dragSourceFolderId = null;
    private string? dragSourceRoutineId = null;
    private int dragSourceRoutineIndex = -1;

    // Folder rename state
    private string? renamingFolderId = null;
    private string renamingFolderName = "";

    // New folder dialog state
    private bool showNewFolderDialog = false;
    private string newFolderName = "New Folder";
    private Vector3 newFolderColor = new Vector3(0.45f, 0.55f, 0.75f);
    private string? newFolderParentId = null;

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

    // Base sizes (before scaling)
    private const float BaseWidth = 500f;
    private const float BaseHeight = 600f;
    private const float BaseMaxWidth = 800f;
    private const float BaseMaxHeight = 900f;

    private bool isDragging = false;
    private bool anyCardHovered = false;

    // sibling dim uses a one-frame lag (cards render in order)
    private string? hoveredCardIdLastFrame = null;
    private string? hoveredCardIdThisFrame = null;

    private readonly Dictionary<string, float> cardHoverAlpha = new();
    private readonly Dictionary<string, float> cardDimAlpha = new();

    // PLAY/STOP click timestamps; entries expire after ~900ms
    private readonly Dictionary<string, float> playActivationStart = new();
    private readonly Dictionary<string, float> stopActivationStart = new();

    private readonly Dictionary<string, (float start, Vector2 pos)> cardRippleStart = new();

    // tab slide: 400ms ease, 3-phase width (squash/overshoot/settle)
    private MainTab tabSlideToTab = MainTab.Presets;
    private float tabSlideProgress = 1f;
    private float tabSlideFromCenterX = 0f;
    private float tabSlideFromW = 0f;
    private float tabUnderlineX = -1f;
    private float tabUnderlineW = 0f;

    private double tabClickedAt = -1;
    private Vector2 tabClickedPos = Vector2.Zero;
    private MainTab tabClickedTab = MainTab.Presets;

    private float searchFocusAlpha = 0f;

    private double footerCtaClickTime = -1;

    // wordmark fade: IFontHandle builds async, ease 0->1 over ~350ms
    private float encoreFadeAlpha = 0f;

    private static float ApproachAlpha(float current, float target, float rate)
    {
        float dt = ImGui.GetIO().DeltaTime;
        float step = MathF.Min(1f, dt * rate);
        return current + (target - current) * step;
    }


    public MainWindow() : base("Encore###EncoreMain")
    {
        SizeCondition = ImGuiCond.FirstUseEver;
        // outer never scrolls; PresetList/RoutineList child owns scrolling
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (Plugin.Instance?.Configuration is { } config)
        {
            config.IsMainWindowOpen = true;
            config.Save();
        }
    }

    public override void OnClose()
    {
        base.OnClose();
        if (Plugin.Instance?.Configuration is { } config)
        {
            config.IsMainWindowOpen = false;
            config.Save();
        }
    }

    public override void PreDraw()
    {
        base.PreDraw();
        var scale = UIStyles.WindowScale;

        Size = new Vector2(BaseWidth * scale, BaseHeight * scale);

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(BaseWidth * scale, BaseHeight * scale),
            MaximumSize = new Vector2(BaseMaxWidth * scale, BaseMaxHeight * scale)
        };

        // hover-over-card sets NoMove so click+drag goes to BeginDragDropSource not window-move
        var cardHoverUnlocksDrag = (currentTab == MainTab.Presets && currentSort == SortMode.Custom)
                                || currentTab == MainTab.Routines;
        if (isDragging || (anyCardHovered && cardHoverUnlocksDrag))
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }

        UIStyles.PushEncoreWindow();
    }

    public override void PostDraw()
    {
        UIStyles.PopEncoreWindow();
        base.PostDraw();
    }

    public void SetEditorWindow(PresetEditorWindow editor)
    {
        editorWindow = editor;
    }

    public void SetRoutineEditor(RoutineEditorWindow editor)
    {
        routineEditorWindow = editor;
    }

    public void SetHelpWindow(HelpWindow help)
    {
        helpWindow = help;
    }

    public void SetPatchNotesWindow(PatchNotesWindow patchNotes)
    {
        patchNotesWindow = patchNotes;
    }

    public override void Draw()
    {
        HandleEditorCompletion();

        // Reset per-frame hover tracking (read by PreDraw on NEXT frame)
        anyCardHovered = false;
        // Shift sibling-dim state: cards this frame will read lastFrame; the
        // hovered-this-frame card sets itself into hoveredCardIdThisFrame for
        // next frame to read.
        hoveredCardIdLastFrame = hoveredCardIdThisFrame;
        hoveredCardIdThisFrame = null;

        // active preset/routine acts as fallback hover focus
        if (hoveredCardIdLastFrame == null)
        {
            if (currentTab == MainTab.Presets)
            {
                var activeId = Plugin.Instance?.Configuration?.ActivePresetId;
                if (!string.IsNullOrEmpty(activeId))
                    hoveredCardIdLastFrame = activeId;
            }
            else
            {
                var activeName = Plugin.Instance?.ActiveRoutineName;
                if (!string.IsNullOrEmpty(activeName))
                {
                    var r = Plugin.Instance?.Configuration?.Routines?
                        .FirstOrDefault(x => x.Name == activeName);
                    if (r != null) hoveredCardIdLastFrame = r.Id;
                }
            }
        }


        UIStyles.PushMainWindowStyle();
        UIStyles.PushEncoreContent();

        // edge-to-edge chrome: zero WindowPadding/ItemSpacing; body child restores its own padding
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0f, 0f));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(0f, 0f));

        try
        {
            DrawRibbon();
            DrawHero();
            DrawPresetList();
            DrawFooter();
            bool somethingPlaying = !string.IsNullOrEmpty(Plugin.Instance?.Configuration?.ActivePresetId)
                                    || Plugin.Instance?.ActiveRoutineName != null;
            if (somethingPlaying)
            {
                var glowDl = ImGui.GetForegroundDrawList();
                var wMin = ImGui.GetWindowPos();
                var wSz = ImGui.GetWindowSize();
                UIStyles.DrawWindowEqEdges(glowDl,
                    wMin, new Vector2(wMin.X + wSz.X, wMin.Y + wSz.Y),
                    MW_Accent, UIStyles.Scale);
            }
            UIStyles.DrawWindowCornerBrackets(MW_Accent, 0.45f);
        }
        finally
        {
            ImGui.PopStyleVar(2); // WindowPadding + ItemSpacing
            UIStyles.PopEncoreContent();
            UIStyles.PopMainWindowStyle();
        }
    }

    private static readonly Vector4 MW_Text       = new(0.86f, 0.87f, 0.89f, 1f);
    private static readonly Vector4 MW_TextDim    = new(0.54f, 0.55f, 0.60f, 1f);
    private static readonly Vector4 MW_TextFaint  = new(0.34f, 0.35f, 0.40f, 1f);
    private static readonly Vector4 MW_Accent     = new(0.49f, 0.65f, 0.85f, 1f);
    private static readonly Vector4 MW_AccentBright = new(0.65f, 0.77f, 0.92f, 1f);
    private static readonly Vector4 MW_Success    = new(0.45f, 0.92f, 0.55f, 1f);
    private static readonly Vector4 MW_Danger     = new(0.82f, 0.42f, 0.42f, 1f);
    private static readonly Vector4 MW_Border     = new(0.20f, 0.22f, 0.27f, 1f);
    private static readonly Vector4 MW_MacroGreen = new(0.50f, 0.71f, 0.60f, 1f);
    private static readonly Vector4 MW_Cyan       = new(0.28f, 0.88f, 0.92f, 1f);
    private static readonly Vector4 MW_Gold       = new(1.00f, 0.82f, 0.30f, 1f);
    private static readonly Vector4 MW_Rose       = new(0.81f, 0.53f, 0.66f, 1f);

    private static uint MW_Col(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
    private static uint MW_ColA(Vector4 c, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(c.X, c.Y, c.Z, a));

    private static string MW_LetterSpace(string s) => string.Join(" ", s.ToCharArray());

    private static void PushEncoreMenuStyle()
    {
        var scale = UIStyles.Scale;

        ImGui.PushStyleColor(ImGuiCol.PopupBg,        new Vector4(0.070f, 0.082f, 0.118f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border,         new Vector4(MW_Accent.X, MW_Accent.Y, MW_Accent.Z, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.Text,           new Vector4(0.86f, 0.87f, 0.89f, 1f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered,  new Vector4(0.49f, 0.65f, 0.85f, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,   new Vector4(0.49f, 0.65f, 0.85f, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.Separator,      new Vector4(0.20f, 0.22f, 0.27f, 1f));

        // popups render as windows; zero WindowRounding too
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,     0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,    0f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding,     0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize,   1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize,  1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,     new Vector2(6f * scale, 6f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,       new Vector2(4f * scale, 3f * scale));
    }

    private static void PopEncoreMenuStyle()
    {
        ImGui.PopStyleVar(7);
        ImGui.PopStyleColor(6);
    }

    private static void MW_PushTooltipStyle()
    {
        var scale = UIStyles.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,    new Vector2(12f * scale, 8f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,      new Vector2(0f, 3f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding,    0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding,   0f);
        ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize,  1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg,
            new Vector4(0.035f, 0.042f, 0.063f, 0.98f));
        ImGui.PushStyleColor(ImGuiCol.Border,
            new Vector4(MW_Accent.X, MW_Accent.Y, MW_Accent.Z, 0.45f));
    }

    private static void MW_PopTooltipStyle()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(6);
    }

    private static void MW_SetTooltip(string text)
    {
        MW_PushTooltipStyle();
        ImGui.SetTooltip(text);
        MW_PopTooltipStyle();
    }

    private void DrawRibbon()
    {
        // Dalamud default 18px is chunky; shrink to fit the 30px strip
        ImGui.SetWindowFontScale(0.85f);
        try { DrawRibbonInner(); }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    private void DrawRibbonInner()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winW = ImGui.GetWindowWidth();

        var start = new Vector2(winPos.X, ImGui.GetCursorScreenPos().Y);
        var availW = winW;
        var ribbonH = 26f * scale;
        var end = new Vector2(start.X + availW, start.Y + ribbonH);
        float t = (float)ImGui.GetTime();

        uint bgTop = MW_Col(new Vector4(0.047f, 0.055f, 0.071f, 1f));
        uint bgBot = MW_Col(new Vector4(0.024f, 0.031f, 0.043f, 1f));
        dl.AddRectFilledMultiColor(start, end, bgTop, bgTop, bgBot, bgBot);

        uint aSolid = MW_ColA(MW_Accent, 0.55f);
        uint aClear = MW_ColA(MW_Accent, 0f);
        dl.AddRectFilledMultiColor(
            start, new Vector2(start.X + availW * 0.42f, start.Y + 1f),
            aSolid, aClear, aClear, aSolid);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + availW * 0.58f, start.Y),
            new Vector2(end.X, start.Y + 1f),
            aClear, aSolid, aSolid, aClear);
        uint aSoft = MW_ColA(MW_Accent, 0.28f);
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

        // ON-AIR pip: breathing core + two expanding rings
        float pipX = start.X + padX + 9f * scale;
        float coreR = 2f * scale;
        float coreBreath = 0.5f + 0.5f * MathF.Sin(t * MathF.Tau / 1.8f);
        float corePulse = 1.0f + 0.2f * coreBreath;
        float pr = coreR * corePulse;
        for (int g = 2; g >= 1; g--)
        {
            float pad = g * 1.5f * scale;
            dl.AddCircleFilled(new Vector2(pipX, pipCenterY), pr + pad,
                MW_ColA(MW_Accent, (0.28f - g * 0.08f) * (0.7f + 0.3f * coreBreath)));
        }
        dl.AddCircleFilled(new Vector2(pipX, pipCenterY), pr,
            MW_ColA(MW_AccentBright, 1f));
        for (int ring = 0; ring < 2; ring++)
        {
            float phase = ((float)t + ring * 0.9f) % 1.8f / 1.8f;
            float rRadius = 2f * scale + (9f * scale - 2f * scale) * phase;
            float rAlpha = 0.85f * (1f - phase);
            if (rAlpha < 0.02f) continue;
            dl.AddCircle(new Vector2(pipX, pipCenterY), rRadius,
                MW_ColA(MW_Accent, rAlpha), 16, 1f);
        }

        //  Meta run: "MAINSTAGE - N PRESETS - N ROUTINES"
        float metaTrack = 0.5f * scale;
        float metaX = pipX + 9f * scale + 12f * scale;
        float cursor = metaX;

        string labelTxt = "MAINSTAGE";
        string sep = " - ";
        float labelW = UIStyles.MeasureTrackedWidth(labelTxt, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), labelTxt,
            MW_Col(MW_Text), metaTrack);
        cursor += labelW;
        float sepW = UIStyles.MeasureTrackedWidth(sep, metaTrack);
        UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), sep,
            MW_Col(MW_TextFaint), metaTrack);
        cursor += sepW;

        int presetCount = Plugin.Instance?.Configuration?.Presets?.Count ?? 0;
        int routineCount = Plugin.Instance?.Configuration?.Routines?.Count ?? 0;
        string presetN = presetCount.ToString();
        string presetsLbl = " PRESETS";
        string routineN = routineCount.ToString();
        string routinesLbl = " ROUTINES";

        // Reserve right-side room for the Penumbra status ("Penumbra online"
        // ~95px + dot + gap). Budget leaves breathing room for either state.
        float tagReserve = 130f * scale;
        float rightLimit = end.X - padX - tagReserve;

        float pNW = UIStyles.MeasureTrackedWidth(presetN, metaTrack);
        float pLW = UIStyles.MeasureTrackedWidth(presetsLbl, metaTrack);
        float rNW = UIStyles.MeasureTrackedWidth(routineN, metaTrack);
        float rLW = UIStyles.MeasureTrackedWidth(routinesLbl, metaTrack);
        float totalCountsW = pNW + pLW + sepW + rNW + rLW;
        if (cursor + totalCountsW <= rightLimit)
        {
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), presetN,
                MW_Col(MW_Accent), metaTrack);
            cursor += pNW;
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), presetsLbl,
                MW_Col(MW_TextDim), metaTrack);
            cursor += pLW;
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), sep,
                MW_Col(MW_TextFaint), metaTrack);
            cursor += sepW;
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), routineN,
                MW_Col(MW_MacroGreen), metaTrack);
            cursor += rNW;
            UIStyles.DrawTrackedText(dl, new Vector2(cursor, textY), routinesLbl,
                MW_Col(MW_TextDim), metaTrack);
        }

        {
            bool ribbonPenumbraOk = Plugin.Instance?.PenumbraService?.IsAvailable == true;
            var pStatusCol = ribbonPenumbraOk ? MW_Success : MW_Danger;
            string pKicker = "Penumbra";
            string pValue = ribbonPenumbraOk ? " online" : " offline";

            float pDotR = 2.5f * scale;
            float pDotGap = 6f * scale;
            float wPKicker = ImGui.CalcTextSize(pKicker).X;
            float wPValue = ImGui.CalcTextSize(pValue).X;
            float pTotalW = pDotR * 2 + pDotGap + wPKicker + wPValue;
            float pRightEdge = end.X - padX;
            float pLeft = pRightEdge - pTotalW;
            float pDotCx = pLeft + pDotR;
            float pDotCy = textY + textH * 0.5f;

            float pBreath = 0.5f + 0.5f * MathF.Sin(t * MathF.Tau / 2.4f);
            for (int g = 3; g >= 1; g--)
            {
                float pad = g * 1.2f * scale;
                float a = 0.20f / g * (0.6f + 0.4f * pBreath);
                dl.AddCircleFilled(new Vector2(pDotCx, pDotCy), pDotR + pad,
                    MW_ColA(pStatusCol, a));
            }
            dl.AddCircleFilled(new Vector2(pDotCx, pDotCy), pDotR,
                MW_Col(pStatusCol));

            dl.AddText(new Vector2(pLeft + pDotR * 2 + pDotGap, textY),
                MW_Col(pStatusCol), pKicker);
            dl.AddText(new Vector2(pLeft + pDotR * 2 + pDotGap + wPKicker, textY),
                MW_Col(MW_Text), pValue);
        }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(1, ribbonH));
    }

    private void DrawHero()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winW = ImGui.GetWindowWidth();

        var start = new Vector2(winPos.X, ImGui.GetCursorScreenPos().Y);
        float heroH = 108f * scale;
        var end = new Vector2(start.X + winW, start.Y + heroH);

        uint bgTop = MW_Col(new Vector4(0.079f, 0.094f, 0.147f, 1f));
        uint bgMid = MW_Col(new Vector4(0.043f, 0.059f, 0.094f, 1f));
        uint bgBot = MW_Col(new Vector4(0.027f, 0.035f, 0.059f, 1f));
        dl.AddRectFilledMultiColor(start,
            new Vector2(end.X, start.Y + heroH * 0.6f),
            bgTop, bgTop, bgMid, bgMid);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X, start.Y + heroH * 0.6f), end,
            bgMid, bgMid, bgBot, bgBot);

        {
            float heroT = (float)ImGui.GetTime();
            void Spot(float period, float phase, float xFracBase, float xFracRange,
                      float yFrac, float maxR, float peakAlpha)
            {
                float st = 0.5f + 0.5f * MathF.Sin((heroT + phase) * MathF.Tau / period);
                float cx = start.X + winW * (xFracBase + xFracRange * st);
                float cy = start.Y + heroH * yFrac;
                const int layers = 18;
                for (int l = layers - 1; l >= 0; l--)
                {
                    float u = (float)l / (layers - 1);
                    float r = maxR * (0.12f + 0.88f * u);
                    float fall = (1f - u) * (1f - u);
                    float a = peakAlpha * fall;
                    dl.AddCircleFilled(new Vector2(cx, cy), r,
                        MW_ColA(MW_AccentBright, a), 48);
                }
            }
            Spot(period: 22f, phase: 0f,  xFracBase: 0.18f, xFracRange: 0.50f,
                 yFrac: 0.28f, maxR: 130f * scale, peakAlpha: 0.022f);
            Spot(period: 18f, phase: 7f,  xFracBase: 0.30f, xFracRange: 0.44f,
                 yFrac: 0.55f, maxR: 110f * scale, peakAlpha: 0.016f);
            Spot(period: 28f, phase: 14f, xFracBase: 0.40f, xFracRange: 0.30f,
                 yFrac: 0.15f, maxR: 90f * scale,  peakAlpha: 0.012f);
        }

        {
            uint tSolid = MW_ColA(MW_Accent, 0.35f);
            uint tClear = MW_ColA(MW_Accent, 0f);
            float midX = start.X + winW * 0.5f;
            dl.AddRectFilledMultiColor(
                start, new Vector2(midX, start.Y + 1f),
                tClear, tSolid, tSolid, tClear);
            dl.AddRectFilledMultiColor(
                new Vector2(midX, start.Y), new Vector2(end.X, start.Y + 1f),
                tSolid, tClear, tClear, tSolid);
        }
        {
            float hlInset = 20f * scale;
            uint bSolid = MW_ColA(MW_Accent, 0.70f);
            uint bClear = MW_ColA(MW_Accent, 0f);
            float left = start.X + hlInset;
            float right = end.X - hlInset;
            float midX = (left + right) * 0.5f;
            dl.AddRectFilledMultiColor(
                new Vector2(left, end.Y - 1f), new Vector2(midX, end.Y),
                bClear, bSolid, bSolid, bClear);
            dl.AddRectFilledMultiColor(
                new Vector2(midX, end.Y - 1f), new Vector2(right, end.Y),
                bSolid, bClear, bClear, bSolid);
        }

        {
            float armLen = 14f * scale;
            float inset = 8f * scale;
            uint bCol = MW_ColA(MW_Accent, 0.45f);
            float tl = start.X + inset, tt = start.Y + inset;
            float tr = end.X - inset;
            dl.AddLine(new Vector2(tl, tt), new Vector2(tl + armLen, tt), bCol, 1f);
            dl.AddLine(new Vector2(tl, tt), new Vector2(tl, tt + armLen), bCol, 1f);
            dl.AddLine(new Vector2(tr, tt), new Vector2(tr - armLen, tt), bCol, 1f);
            dl.AddLine(new Vector2(tr, tt), new Vector2(tr, tt + armLen), bCol, 1f);
        }

        // EQ first so wordmark sits on top
        {
            float eqLeft = start.X + 8f * scale;
            float eqRight = end.X - 8f * scale;
            float eqH = 44f * scale;
            float eqBottom = end.Y - 1f;
            var eqMin = new Vector2(eqLeft, eqBottom - eqH);
            var eqMax = new Vector2(eqRight, eqBottom);
            UIStyles.DrawRainbowBars(dl, eqMin, eqMax, scale,
                opacity: 1.0f, leftAlign: false, peakFrac: 0.5f);
        }

        const string banner = "ENCORE";
        float letterSpacing = 8f * scale;
        var bannerFont = Plugin.Instance?.BannerFont;
        var headerFontFallback = Plugin.Instance?.HeaderFont;
        IFontHandle? bannerFontH = null;
        if (bannerFont is { Available: true }) bannerFontH = bannerFont;
        else if (headerFontFallback is { Available: true }) bannerFontH = headerFontFallback;
        if (bannerFontH != null)
        {
            encoreFadeAlpha = MathF.Min(1f,
                encoreFadeAlpha + ImGui.GetIO().DeltaTime / 0.40f);
            float fade = encoreFadeAlpha;
            float tBreath = (float)ImGui.GetTime();

            var letterWidths = new float[banner.Length];
            float letterH = 0f;
            using (bannerFontH.Push())
            {
                for (int i = 0; i < banner.Length; i++)
                {
                    var sz = ImGui.CalcTextSize(banner[i].ToString());
                    letterWidths[i] = sz.X;
                    letterH = MathF.Max(letterH, sz.Y);
                }
            }
            float totalW = 0f;
            for (int i = 0; i < banner.Length; i++)
            {
                totalW += letterWidths[i];
                if (i < banner.Length - 1) totalW += letterSpacing;
            }
            float bannerX = start.X + (winW - totalW) * 0.5f;
            // True vertical center in the full hero - glyphs layer on top of
            // the EQ bars behind.
            float bannerTopY = start.Y + (heroH - letterH) * 0.5f;

            // Glow sample colors.
            uint uA = MW_ColA(MW_Accent, 0.09f * fade);
            uint uB = MW_ColA(MW_Accent, 0.04f * fade);

            using (bannerFontH.Push())
            {
                for (int i = 0; i < banner.Length; i++)
                {
                    float phaseL = tBreath * MathF.Tau / 4f - i * 0.28f;
                    float bulb = 0.5f + 0.5f * MathF.Sin(phaseL);
                    float letterA = 0.94f + 0.06f * bulb;
                    var s = banner[i].ToString();
                    var letterPos = new Vector2(bannerX, bannerTopY);

                    // Inner glow ring - 10 samples at small radius.
                    const int innerCount = 10;
                    float innerR = 2.2f * scale;
                    for (int a = 0; a < innerCount; a++)
                    {
                        float ang = (a / (float)innerCount) * MathF.Tau;
                        dl.AddText(
                            new Vector2(letterPos.X + MathF.Cos(ang) * innerR,
                                        letterPos.Y + MathF.Sin(ang) * innerR),
                            uA, s);
                    }
                    // Outer glow ring - 16 samples at larger radius.
                    const int outerCount = 16;
                    float outerR = 5f * scale;
                    for (int a = 0; a < outerCount; a++)
                    {
                        float ang = (a / (float)outerCount) * MathF.Tau;
                        dl.AddText(
                            new Vector2(letterPos.X + MathF.Cos(ang) * outerR,
                                        letterPos.Y + MathF.Sin(ang) * outerR),
                            uB, s);
                    }
                    // Main letter on top.
                    dl.AddText(letterPos, MW_ColA(MW_Text, letterA * fade), s);
                    bannerX += letterWidths[i] + letterSpacing;
                }
            }
        }

        // Advance layout cursor by exactly the hero height - tab strip sits
        // flush below the hero's fading hairline.
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(1, heroH));
    }

    private void DrawPresetList()
    {
        var config = Plugin.Instance?.Configuration;
        var presets = config?.Presets;
        if (presets == null || config == null)
            return;

        // Family chrome order: tab strip -> controls row (sort/search/pose) -> content
        DrawTabBar();
        DrawSortControls();

        // Reserve exactly the footer's 48px - no wasted gap above it.
        var listHeight = ImGui.GetContentRegionAvail().Y - 48f * UIStyles.Scale;

        if (currentTab == MainTab.Routines)
        {
            DrawRoutineList(config, listHeight);
            return;
        }

        // restore inner padding (Draw() pushed 0,0 for edge-to-edge chrome)
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * UIStyles.Scale, 8f * UIStyles.Scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(6f * UIStyles.Scale, 4f * UIStyles.Scale));

        // AlwaysVerticalScrollbar reserves the gutter so tab switches don't shift content width
        if (ImGui.BeginChild("PresetList", new Vector2(-1, listHeight), true,
                ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {

            if (presets.Count == 0)
            {
                DrawEmptyState(
                    heading: "THE STAGE IS EMPTY",
                    body: "Create your first preset to get started. A preset pairs an animation mod with a chat command, handles priority conflicts, and plays back with a single click.",
                    primaryCta: "+ New Preset",
                    primaryAction: () => editorWindow?.OpenNew(),
                    secondaryCta: "Open Playbook",
                    secondaryAction: () => helpWindow?.Toggle());
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
                    if (DrawPresetCard(presets[i], i)) break;
                }
            }

            // Handle drop to unfiled area (drop on empty space below all content)
            if (currentSort == SortMode.Custom && ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsWindowHovered())
            {
                if (dragSourcePresetId != null)
                {
                    // Preset dropped on empty area - unfile
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
                else if (dragSourceFolderId != null)
                {
                    // Folder dropped on empty area - move to top level
                    var draggedFolder = config.Folders.FirstOrDefault(f => f.Id == dragSourceFolderId);
                    if (draggedFolder != null)
                    {
                        draggedFolder.ParentFolderId = null;
                        config.Save();
                    }
                    dragSourceFolderId = null;
                    isDragging = false;
                }
            }

            var fadeDl = ImGui.GetWindowDrawList();
            var childMin = ImGui.GetWindowPos();
            var childSize = ImGui.GetWindowSize();
            float fadeH = 18f * UIStyles.Scale;
            uint surface = ImGui.ColorConvertFloat4ToU32(new Vector4(0.086f, 0.098f, 0.133f, 1f));
            uint clearS  = ImGui.ColorConvertFloat4ToU32(new Vector4(0.086f, 0.098f, 0.133f, 0f));
            fadeDl.AddRectFilledMultiColor(
                childMin,
                new Vector2(childMin.X + childSize.X, childMin.Y + fadeH),
                surface, surface, clearS, clearS);
            fadeDl.AddRectFilledMultiColor(
                new Vector2(childMin.X, childMin.Y + childSize.Y - fadeH),
                new Vector2(childMin.X + childSize.X, childMin.Y + childSize.Y),
                clearS, clearS, surface, surface);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2); // WindowPadding + ItemSpacing

        // Clear drag state if mouse released outside
        if ((dragSourcePresetId != null || dragSourceFolderId != null || dragSourceRoutineId != null)
            && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            dragSourceIndex = -1;
            dragSourcePresetId = null;
            dragSourceFolderId = null;
            dragSourceRoutineId = null;
            dragSourceRoutineIndex = -1;
            isDragging = false;
        }

        // Also clear isDragging if no drag source is active
        if (dragSourcePresetId == null && dragSourceFolderId == null && dragSourceRoutineId == null)
        {
            isDragging = false;
        }
    }

    // Maximum folder nesting depth (0 = top-level, MaxNestDepth = deepest allowed child)
    private const int MaxNestDepth = 2;

    private void DrawCustomSortPresets(List<DancePreset> presets, Configuration config)
    {
        var sortedIndices = GetSortedPresetIndices(presets);
        var folderOrder = (config.FolderOrder ?? new List<string>()).ToList();
        // Preset tab only sees non-routine folders
        var folders = (config.Folders ?? new List<PresetFolder>()).Where(f => !f.IsRoutineFolder).ToList();
        var isSearching = !string.IsNullOrWhiteSpace(presetSearchFilter);

        // Build set of folder IDs that have search matches (presets or descendant folders with matches)
        // so we can auto-expand and hide empty branches during search
        HashSet<string>? foldersWithMatches = null;
        if (isSearching)
        {
            foldersWithMatches = new HashSet<string>();
            foreach (var i in sortedIndices)
            {
                var fid = presets[i].FolderId;
                while (fid != null)
                {
                    foldersWithMatches.Add(fid);
                    var parentFolder = folders.FirstOrDefault(f => f.Id == fid);
                    fid = parentFolder?.ParentFolderId;
                }
            }
        }

        // single channel split (nested splits corrupt the draw list). content=1, bgs=0
        var deferredBackgrounds = new List<(Vector2 start, float width, float height, Vector3 color, float scale)>();
        var drawList = ImGui.GetWindowDrawList();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        var deletedThisFrame = DrawFolderLevel(null, 0, presets, config, sortedIndices, folderOrder, folders, isSearching, foldersWithMatches, deferredBackgrounds);

        drawList.ChannelsSetCurrent(0);
        foreach (var (start, width, height, color, bgScale) in deferredBackgrounds)
        {
            var accentWidth = 4f * bgScale;
            var bgR = color.X * 0.07f + 0.086f * 0.93f;
            var bgG = color.Y * 0.07f + 0.098f * 0.93f;
            var bgB = color.Z * 0.07f + 0.133f * 0.93f;
            drawList.AddRectFilled(
                start,
                new Vector2(start.X + width, start.Y + height),
                ImGui.ColorConvertFloat4ToU32(new Vector4(bgR, bgG, bgB, 1f)));

            drawList.AddRectFilled(
                start,
                new Vector2(start.X + accentWidth, start.Y + height),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 1f)));

            var brR = color.X * 0.25f + MW_Border.X * 0.75f;
            var brG = color.Y * 0.25f + MW_Border.Y * 0.75f;
            var brB = color.Z * 0.25f + MW_Border.Z * 0.75f;
            var borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(brR, brG, brB, 1f));
            drawList.AddLine(
                new Vector2(start.X + width - 1, start.Y),
                new Vector2(start.X + width - 1, start.Y + height),
                borderCol, 1f);
            drawList.AddLine(
                new Vector2(start.X, start.Y + height - 1),
                new Vector2(start.X + width, start.Y + height - 1),
                borderCol, 1f);
        }
        drawList.ChannelsMerge();

        // Unfile presets referencing deleted/orphaned folders
        if (!deletedThisFrame)
        {
            var knownFolderIds = new HashSet<string>(folders.Select(f => f.Id));
            var orphanIndices = sortedIndices
                .Where(i => presets[i].FolderId != null && !knownFolderIds.Contains(presets[i].FolderId!))
                .ToList();
            if (orphanIndices.Count > 0)
            {
                foreach (var i in orphanIndices)
                {
                    presets[i].FolderId = null;
                    if (DrawPresetCard(presets[i], i)) break;
                }
                config.Save();
            }
        }
    }

    // returns true if a preset was deleted (caller stops further rendering)
    private bool DrawFolderLevel(string? parentFolderId, int depth,
        List<DancePreset> presets, Configuration config,
        List<int> sortedIndices, List<string> folderOrder, List<PresetFolder> folders,
        bool isSearching, HashSet<string>? foldersWithMatches,
        List<(Vector2 start, float width, float height, Vector3 color, float scale)> deferredBackgrounds)
    {
        // Draw presets at this level
        var levelIndices = sortedIndices.Where(i => presets[i].FolderId == parentFolderId).ToList();
        foreach (var i in levelIndices)
        {
            if (DrawPresetCard(presets[i], i, parentFolderId != null ? (folders.FirstOrDefault(f => f.Id == parentFolderId)?.Color ?? DefaultFolderColor) : null))
                return true;
        }

        // Draw child folders at this level (in FolderOrder sequence)
        var childFolders = folderOrder
            .Select(id => folders.FirstOrDefault(f => f.Id == id))
            .Where(f => f != null && f.ParentFolderId == parentFolderId)
            .ToList();

        foreach (var folder in childFolders)
        {
            if (folder == null) continue;

            // During search: skip folders with no matches in their subtree
            if (isSearching && foldersWithMatches != null && !foldersWithMatches.Contains(folder.Id)) continue;

            // Count items for the badge: presets in subtree + direct child folders
            var totalPresets = CountPresetsInSubtree(folder.Id, presets, sortedIndices, folders);
            var directChildFolders = folders.Count(f => f.ParentFolderId == folder.Id);
            var totalCount = totalPresets + directChildFolders;

            // Auto-expand during search if this folder has matches
            var hasMatchesInSubtree = isSearching && foldersWithMatches != null && foldersWithMatches.Contains(folder.Id);
            var isExpanded = !folder.IsCollapsed || hasMatchesInSubtree;
            var hasContent = totalCount > 0 || folders.Any(f => f.ParentFolderId == folder.Id);
            var isExpandedWithContent = isExpanded && hasContent;

            DrawFolderHeader(folder, totalCount, config, isExpandedWithContent);

            if (isExpandedWithContent)
            {
                var scale = UIStyles.Scale;
                var folderColor = folder.Color ?? DefaultFolderColor;
                var accentWidth = 4f * scale;
                // 10px of daylight between the folder's extended stripe and each card so the
                // card's own 3px accent bar doesn't visually crowd the folder stripe (mirrors
                // HTML .folder-body { padding-left: 10px }).
                var indent = accentWidth + 10f * scale;
                var paddingTop = 6f * scale;
                var paddingBottom = 22f * scale;
                var itemSpacing = ImGui.GetStyle().ItemSpacing.Y;

                var startPos = ImGui.GetCursorScreenPos();
                var contentWidth = ImGui.GetContentRegionAvail().X;

                ImGui.SetCursorScreenPos(new Vector2(startPos.X, startPos.Y + paddingTop));
                ImGui.Indent(indent);

                // Recurse: draw presets and subfolders inside this folder
                var childDeleted = DrawFolderLevel(folder.Id, depth + 1, presets, config, sortedIndices, folderOrder, folders, isSearching, foldersWithMatches, deferredBackgrounds);

                ImGui.Unindent(indent);

                var totalHeight = ImGui.GetCursorScreenPos().Y - startPos.Y + paddingBottom;

                // Defer background drawing - will be rendered on channel 0 by the caller
                deferredBackgrounds.Add((startPos, contentWidth, totalHeight, folderColor, scale));

                var endY = startPos.Y + totalHeight + itemSpacing;
                if (ImGui.GetCursorScreenPos().Y < endY)
                    ImGui.SetCursorScreenPos(new Vector2(startPos.X, endY));

                ImGui.Spacing();

                if (childDeleted) return true;
            }
        }

        return false;
    }

    /// <summary>Count all presets in a folder and all its descendant folders.</summary>
    private int CountPresetsInSubtree(string folderId, List<DancePreset> presets, List<int> sortedIndices, List<PresetFolder> folders)
    {
        var count = sortedIndices.Count(i => presets[i].FolderId == folderId);
        foreach (var child in folders.Where(f => f.ParentFolderId == folderId))
            count += CountPresetsInSubtree(child.Id, presets, sortedIndices, folders);
        return count;
    }

    /// <summary>Get the nesting depth of a folder (0 = top-level).</summary>
    private int GetFolderDepth(string? folderId, List<PresetFolder> folders)
    {
        int depth = 0;
        var current = folderId;
        while (current != null && depth <= MaxNestDepth + 1)
        {
            var folder = folders.FirstOrDefault(f => f.Id == current);
            if (folder == null) break;
            current = folder.ParentFolderId;
            depth++;
        }
        return depth;
    }

    /// <summary>Check if a folder is an ancestor of another (prevents circular nesting).</summary>
    private bool IsAncestor(string ancestorId, string descendantId, List<PresetFolder> folders)
    {
        var current = descendantId;
        while (current != null)
        {
            if (current == ancestorId) return true;
            var folder = folders.FirstOrDefault(f => f.Id == current);
            if (folder == null) break;
            current = folder.ParentFolderId;
        }
        return false;
    }

    /// <summary>Collect all descendant folder IDs (children, grandchildren, etc.).</summary>
    private void CollectDescendantFolderIds(string folderId, List<PresetFolder> folders, List<string> result)
    {
        foreach (var child in folders.Where(f => f.ParentFolderId == folderId))
        {
            result.Add(child.Id);
            CollectDescendantFolderIds(child.Id, folders, result);
        }
    }

    /// <summary>Recursive "Move to Folder" submenu for presets.</summary>
    private void DrawPresetMoveToFolderMenu(string? parentId, DancePreset preset, Configuration config)
    {
        var children = (config.FolderOrder ?? new List<string>())
            .Select(id => config.Folders.FirstOrDefault(f => f.Id == id))
            .Where(f => f != null && f.ParentFolderId == parentId)
            .ToList();

        foreach (var folder in children)
        {
            if (folder == null) continue;
            var isInFolder = preset.FolderId == folder.Id;
            var hasChildren = config.Folders.Any(f => f.ParentFolderId == folder.Id);

            if (hasChildren)
            {
                if (ImGui.BeginMenu(folder.Name))
                {
                    // Option to place directly in this folder (not a subfolder)
                    if (ImGui.MenuItem("(Here)", "", isInFolder))
                    {
                        preset.FolderId = folder.Id;
                        config.Save();
                    }
                    ImGui.Separator();
                    DrawPresetMoveToFolderMenu(folder.Id, preset, config);
                    ImGui.EndMenu();
                }
            }
            else
            {
                if (ImGui.MenuItem(folder.Name, "", isInFolder))
                {
                    preset.FolderId = folder.Id;
                    config.Save();
                }
            }
        }
    }

    /// <summary>Recursive "Move to Folder" submenu for folders (nesting).</summary>
    private void DrawFolderMoveToFolderMenu(string? parentId, PresetFolder folderToMove, Configuration config)
    {
        var children = (config.FolderOrder ?? new List<string>())
            .Select(id => config.Folders.FirstOrDefault(f => f.Id == id))
            .Where(f => f != null && f.ParentFolderId == parentId && f.Id != folderToMove.Id)
            .ToList();

        foreach (var folder in children)
        {
            if (folder == null) continue;

            // Prevent circular nesting and depth limit
            if (IsAncestor(folderToMove.Id, folder.Id, config.Folders)) continue;
            var targetDepth = GetFolderDepth(folder.Id, config.Folders);
            if (targetDepth >= MaxNestDepth) continue;

            var isInFolder = folderToMove.ParentFolderId == folder.Id;
            var hasChildren = config.Folders.Any(f => f.ParentFolderId == folder.Id && f.Id != folderToMove.Id);

            if (hasChildren)
            {
                if (ImGui.BeginMenu(folder.Name))
                {
                    if (ImGui.MenuItem("(Here)", "", isInFolder))
                    {
                        folderToMove.ParentFolderId = folder.Id;
                        config.Save();
                    }
                    ImGui.Separator();
                    DrawFolderMoveToFolderMenu(folder.Id, folderToMove, config);
                    ImGui.EndMenu();
                }
            }
            else
            {
                if (ImGui.MenuItem(folder.Name, "", isInFolder))
                {
                    folderToMove.ParentFolderId = folder.Id;
                    config.Save();
                }
            }
        }
    }

    private void DrawFolderHeader(PresetFolder folder, int presetCount, Configuration config, bool hasExpandedContent = false)
    {
        var scale = UIStyles.Scale;
        var headerHeight = 32f * scale;
        var drawList = ImGui.GetWindowDrawList();
        var folderColor = folder.Color ?? DefaultFolderColor;

        ImGui.PushID($"folder_{folder.Id}");

        var cursorPos = ImGui.GetCursorScreenPos();
        var headerMin = cursorPos;
        var headerMax = new Vector2(cursorPos.X + ImGui.GetContentRegionAvail().X, cursorPos.Y + headerHeight);

        // Detect hover on the header rect (we use a raw rect test since the invisible button
        // hasn't been placed yet).
        var mp = ImGui.GetMousePos();
        var folderHovered = mp.X >= headerMin.X && mp.X <= headerMax.X
                         && mp.Y >= headerMin.Y && mp.Y <= headerMax.Y
                         && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);

        // HTML .folder-header: bg = mix(fcolor 14%, surface-2); on hover, mix bumps to 20%.
        // surface-2 = (0.11, 0.126, 0.172).
        var mixAmt = folderHovered ? 0.22f : 0.14f;
        var hBgR = folderColor.X * mixAmt + 0.11f  * (1f - mixAmt);
        var hBgG = folderColor.Y * mixAmt + 0.126f * (1f - mixAmt);
        var hBgB = folderColor.Z * mixAmt + 0.172f * (1f - mixAmt);

        // active mode lights the folder's waveform brighter when a descendant is playing
        var activePresetId = Plugin.Instance?.Configuration?.ActivePresetId;
        bool folderHasPlaying = false;
        if (!string.IsNullOrEmpty(activePresetId))
        {
            var active = Plugin.Instance!.Configuration.Presets.FirstOrDefault(p => p.Id == activePresetId);
            if (active != null && !string.IsNullOrEmpty(active.FolderId))
            {
                var descendantIds = new List<string> { folder.Id };
                CollectDescendantFolderIds(folder.Id, config.Folders, descendantIds);
                folderHasPlaying = descendantIds.Contains(active.FolderId);
            }
        }

        drawList.AddRectFilled(headerMin, headerMax,
            ImGui.ColorConvertFloat4ToU32(new Vector4(hBgR, hBgG, hBgB, 1f)));

        // bottom-anchored; rainbow + brighter + faster when a child is live
        UIStyles.DrawFolderWaveform(
            drawList, headerMin, headerMax,
            new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f),
            scale,
            hasPlaying: folderHasPlaying,
            bottomAnchor: true);

        // Colored border: mix(fcolor 25%, neutral border). Top + sides always; bottom only when collapsed.
        var brR = folderColor.X * 0.25f + MW_Border.X * 0.75f;
        var brG = folderColor.Y * 0.25f + MW_Border.Y * 0.75f;
        var brB = folderColor.Z * 0.25f + MW_Border.Z * 0.75f;
        var borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(brR, brG, brB, 1f));
        // Top
        drawList.AddLine(headerMin, new Vector2(headerMax.X, headerMin.Y), borderCol, 1f);
        // Right
        drawList.AddLine(new Vector2(headerMax.X - 1, headerMin.Y), new Vector2(headerMax.X - 1, headerMax.Y), borderCol, 1f);
        // Bottom - only if collapsed (no body to connect to)
        if (!hasExpandedContent)
            drawList.AddLine(new Vector2(headerMin.X, headerMax.Y - 1), new Vector2(headerMax.X, headerMax.Y - 1), borderCol, 1f);

        // Left accent bar (4px wide) - sharp, vivid, matches HTML border-left: 4px
        var accentWidth = 4f * scale;
        drawList.AddRectFilled(
            headerMin,
            new Vector2(headerMin.X + accentWidth, headerMax.Y),
            ImGui.ColorConvertFloat4ToU32(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f)));

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
            // Folder name - bright text, native size, bold-ish via color contrast
            var namePos = new Vector2(textStartX, headerMin.Y + (headerHeight - ImGui.CalcTextSize(folder.Name).Y) / 2);
            drawList.AddText(namePos, ImGui.ColorConvertFloat4ToU32(new Vector4(0.86f, 0.87f, 0.89f, 1f)), folder.Name);

            // Inline mono count tinted 70% folder color (matches HTML .folder-header .count)
            var countText = $" {presetCount}";
            var countR = folderColor.X * 0.70f + MW_TextFaint.X * 0.30f;
            var countG = folderColor.Y * 0.70f + MW_TextFaint.Y * 0.30f;
            var countB = folderColor.Z * 0.70f + MW_TextFaint.Z * 0.30f;
            var countPos = new Vector2(namePos.X + ImGui.CalcTextSize(folder.Name).X + 2 * scale, namePos.Y);
            drawList.AddText(countPos, ImGui.ColorConvertFloat4ToU32(new Vector4(countR, countG, countB, 1f)), countText);

            // Invisible button for click-to-toggle + right-click + drag source
            ImGui.SetCursorScreenPos(headerMin);
            if (ImGui.InvisibleButton($"folderBtn_{folder.Id}", new Vector2(headerMax.X - headerMin.X, headerHeight)))
            {
                if (dragSourceFolderId == null) // Don't toggle if we were dragging
                {
                    folder.IsCollapsed = !folder.IsCollapsed;
                    config.Save();
                }
            }

            // Track hover for NoMove (prevents window drag when starting folder drag)
            if (ImGui.IsItemHovered())
                anyCardHovered = true;
            // Click ripple from the exact click point - same material pulse
            // used on cards, tinted in the folder's color.
            if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                cardRippleStart[folder.Id] = ((float)ImGui.GetTime(), ImGui.GetMousePos());
            if (cardRippleStart.TryGetValue(folder.Id, out var fRipple))
            {
                float fElapsed = (float)ImGui.GetTime() - fRipple.start;
                if (fElapsed > 1.1f)
                    cardRippleStart.Remove(folder.Id);
                else
                {
                    // Clip to the header rect so the ripple doesn't spill onto
                    // adjacent cards / folder bodies.
                    drawList.PushClipRect(headerMin, headerMax, true);
                    UIStyles.DrawCardRipple(drawList, fRipple.pos, fElapsed,
                        new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f), scale);
                    drawList.PopClipRect();
                }
            }

            // Drag source: make folder header draggable
            if (ImGui.BeginDragDropSource())
            {
                dragSourceFolderId = folder.Id;
                isDragging = true;
                ImGui.SetDragDropPayload("FOLDER_REORDER", ReadOnlySpan<byte>.Empty, ImGuiCond.None);
                // Drag preview tooltip
                ImGui.PushFont(UiBuilder.IconFont);
                ImGui.TextColored(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f),
                    FontAwesomeIcon.Folder.ToIconString());
                ImGui.PopFont();
                ImGui.SameLine();
                ImGui.Text(folder.Name);
                ImGui.EndDragDropSource();
            }

            // Right-click context menu - Encore-themed
            PushEncoreMenuStyle();
            if (ImGui.BeginPopupContextItem($"folderCtx_{folder.Id}"))
            {
                if (!folder.IsRoutineFolder && ImGui.MenuItem("Play Random"))
                {
                    Plugin.Instance?.ExecuteRandomFromFolder(folder.Id);
                }

                if (!folder.IsRoutineFolder) ImGui.Separator();

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

                // Move to Folder (nest under another folder)
                if (ImGui.BeginMenu("Move to Folder"))
                {
                    var isTopLevel = folder.ParentFolderId == null;
                    if (ImGui.MenuItem("(Top Level)", "", isTopLevel))
                    {
                        folder.ParentFolderId = null;
                        config.Save();
                    }
                    DrawFolderMoveToFolderMenu(null, folder, config);
                    ImGui.EndMenu();
                }

                ImGui.Separator();

                // Move Up/Down within same parent level
                var siblings = config.FolderOrder
                    .Where(id => {
                        var f = config.Folders.FirstOrDefault(x => x.Id == id);
                        return f != null && f.ParentFolderId == folder.ParentFolderId
                               && f.IsRoutineFolder == folder.IsRoutineFolder;
                    }).ToList();
                var sibIdx = siblings.IndexOf(folder.Id);
                var folderOrder = config.FolderOrder;
                var folderIdx = folderOrder.IndexOf(folder.Id);

                if (sibIdx <= 0) ImGui.BeginDisabled();
                if (ImGui.MenuItem("Move Up"))
                {
                    var prevSibId = siblings[sibIdx - 1];
                    var prevIdx = folderOrder.IndexOf(prevSibId);
                    folderOrder.RemoveAt(folderIdx);
                    folderOrder.Insert(prevIdx, folder.Id);
                    config.Save();
                }
                if (sibIdx <= 0) ImGui.EndDisabled();

                if (sibIdx < 0 || sibIdx >= siblings.Count - 1) ImGui.BeginDisabled();
                if (ImGui.MenuItem("Move Down"))
                {
                    var nextSibId = siblings[sibIdx + 1];
                    var nextIdx = folderOrder.IndexOf(nextSibId);
                    folderOrder.RemoveAt(folderIdx);
                    folderOrder.Insert(nextIdx, folder.Id);
                    config.Save();
                }
                if (sibIdx < 0 || sibIdx >= siblings.Count - 1) ImGui.EndDisabled();

                ImGui.Separator();

                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.93f, 0.48f, 0.48f, 1f));
                if (ImGui.MenuItem("Delete Folder"))
                {
                    var io = ImGui.GetIO();
                    if (io.KeyCtrl && io.KeyShift)
                    {
                        var toDelete = new List<string> { folder.Id };
                        CollectDescendantFolderIds(folder.Id, config.Folders, toDelete);
                        var deleteSet = new HashSet<string>(toDelete);

                        // Move presets from deleted folders to parent (or top-level)
                        foreach (var p in config.Presets.Where(p => p.FolderId != null && deleteSet.Contains(p.FolderId)))
                            p.FolderId = folder.ParentFolderId;
                        // Move routines from deleted folders to parent (or top-level)
                        foreach (var r in config.Routines.Where(r => r.FolderId != null && deleteSet.Contains(r.FolderId)))
                            r.FolderId = folder.ParentFolderId;

                        config.Folders.RemoveAll(f => deleteSet.Contains(f.Id));
                        config.FolderOrder.RemoveAll(id => deleteSet.Contains(id));
                        config.Save();
                    }
                }
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    MW_SetTooltip("Hold Ctrl+Shift and click to delete\nContents move to parent folder");

                ImGui.EndPopup();
            }
            PopEncoreMenuStyle();
        }

        // Drop target: accept preset, routine, or folder drag onto folder header
        if (dragSourcePresetId != null || dragSourceFolderId != null || dragSourceRoutineId != null)
        {
            var mousePos = ImGui.GetMousePos();
            var isHovering = mousePos.X >= headerMin.X && mousePos.X <= headerMax.X &&
                             mousePos.Y >= headerMin.Y && mousePos.Y <= headerMax.Y;

            if (isHovering)
            {
                // Validate folder drop: can't drop into self, own descendant, or exceed depth
                var folderDropValid = true;
                if (dragSourceFolderId != null)
                {
                    if (dragSourceFolderId == folder.Id) folderDropValid = false;
                    else if (IsAncestor(dragSourceFolderId, folder.Id, config.Folders)) folderDropValid = false;
                    else if (GetFolderDepth(folder.Id, config.Folders) >= MaxNestDepth) folderDropValid = false;
                    // Cross-tab guard: only nest folders of the same kind
                    var draggedFolder0 = config.Folders.FirstOrDefault(f => f.Id == dragSourceFolderId);
                    if (draggedFolder0 != null && draggedFolder0.IsRoutineFolder != folder.IsRoutineFolder)
                        folderDropValid = false;
                }
                // Cross-tab guard for routine drops: only onto a routine folder
                var routineDropValid = dragSourceRoutineId == null || folder.IsRoutineFolder;
                // Cross-tab guard for preset drops: only onto a preset folder
                var presetDropValid = dragSourcePresetId == null || !folder.IsRoutineFolder;

                if ((dragSourcePresetId != null && presetDropValid)
                    || (dragSourceRoutineId != null && routineDropValid)
                    || (dragSourceFolderId != null && folderDropValid))
                {
                    // Highlight with folder color
                    drawList.AddRect(headerMin, headerMax,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(folderColor.X, folderColor.Y, folderColor.Z, 1f)),
                        4f * scale, ImDrawFlags.None, 2f * scale);

                    if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    {
                        if (dragSourcePresetId != null)
                        {
                            var draggedPreset = config.Presets.FirstOrDefault(p => p.Id == dragSourcePresetId);
                            if (draggedPreset != null)
                            {
                                draggedPreset.FolderId = folder.Id;
                                config.Save();
                            }
                            dragSourceIndex = -1;
                            dragSourcePresetId = null;
                        }
                        else if (dragSourceRoutineId != null)
                        {
                            var draggedRoutine = config.Routines.FirstOrDefault(r => r.Id == dragSourceRoutineId);
                            if (draggedRoutine != null)
                            {
                                draggedRoutine.FolderId = folder.Id;
                                config.Save();
                            }
                            dragSourceRoutineIndex = -1;
                            dragSourceRoutineId = null;
                        }
                        else if (dragSourceFolderId != null)
                        {
                            var draggedFolder = config.Folders.FirstOrDefault(f => f.Id == dragSourceFolderId);
                            if (draggedFolder != null)
                            {
                                draggedFolder.ParentFolderId = folder.Id;
                                config.Save();
                            }
                            dragSourceFolderId = null;
                        }
                        isDragging = false;
                    }
                }
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(headerMin.X, headerMax.Y));
        if (!hasExpandedContent)
            ImGui.Spacing();

        ImGui.PopID();
    }

    // 
    //   CONTROLS ROW  -  Sort pill - Search pill - Pose pill
    // 
    private void DrawSortControls()
    {
        // Readable pill text - ~15px rendered. Tighter than default but not
        // cramped; pose labels and sort values need to breathe.
        ImGui.SetWindowFontScale(0.85f);
        try { DrawSortControlsInner(); }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    private void DrawSortControlsInner()
    {
        var scale = UIStyles.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winW = ImGui.GetWindowWidth();

        // Edge-to-edge row bg + bottom border.
        var stripStart = new Vector2(winPos.X, ImGui.GetCursorScreenPos().Y);
        float stripH = 44f * scale;
        var stripEnd = new Vector2(stripStart.X + winW, stripStart.Y + stripH);
        dl.AddRectFilled(stripStart, stripEnd,
            MW_Col(new Vector4(0.047f, 0.055f, 0.075f, 1f)));
        dl.AddLine(new Vector2(stripStart.X, stripEnd.Y - 1f),
                   new Vector2(stripEnd.X, stripEnd.Y - 1f),
                   MW_ColA(MW_Border, 1f), 1f);

        float edgePad = 14f * scale;
        float pillH = 28f * scale;
        float rowY = stripStart.Y + (stripH - pillH) * 0.5f;
        float gap = 8f * scale;
        float labelTrack = 0.8f * scale;
        float textH = ImGui.GetTextLineHeight();
        float labelY = rowY + (pillH - textH) * 0.5f;

        //  Sort pill - compact: only the current VALUE + caret. The "SORT"
        // kicker was dropped to free horizontal space for the search pill.
        string[] sortOptions = { "Custom", "Name", "Command", "Favorites", "Newest", "Oldest" };
        string sortValue = sortOptions[(int)currentSort].ToUpperInvariant();
        float wSortValue = UIStyles.MeasureTrackedWidth(sortValue, labelTrack);
        float caretSz = 5f * scale;
        float sortInnerPad = 10f * scale;
        float sortValueToCaretGap = 8f * scale;
        float sortPillW = sortInnerPad + wSortValue + sortValueToCaretGap
                        + caretSz * 2f + sortInnerPad;
        float sortX = stripStart.X + edgePad;
        var sortMin = new Vector2(sortX, rowY);
        var sortMax = new Vector2(sortX + sortPillW, rowY + pillH);
        dl.AddRectFilled(sortMin, sortMax, MW_Col(new Vector4(0.078f, 0.094f, 0.125f, 1f)));
        dl.AddRect(sortMin, sortMax, MW_ColA(MW_Border, 1f), 0f, 0, 1f);

        UIStyles.DrawTrackedText(dl, new Vector2(sortX + sortInnerPad, labelY),
            sortValue, MW_Col(MW_Text), labelTrack);
        // Caret at the far right of the pill.
        {
            float caretX = sortX + sortPillW - sortInnerPad - caretSz * 2f;
            float caretY = rowY + (pillH - caretSz) * 0.5f;
            dl.AddTriangleFilled(
                new Vector2(caretX, caretY),
                new Vector2(caretX + caretSz * 2f, caretY),
                new Vector2(caretX + caretSz, caretY + caretSz),
                MW_Col(MW_TextFaint));
        }
        // Full-pill hit area -> open combo popup via transparent Combo widget.
        ImGui.SetCursorScreenPos(new Vector2(sortX, rowY));
        if (ImGui.InvisibleButton("##sortPill", new Vector2(sortPillW, pillH)))
            ImGui.OpenPopup("##sortPopup");
        bool sortHovered = ImGui.IsItemHovered();
        if (sortHovered)
        {
            dl.AddRectFilled(sortMin, sortMax, MW_ColA(MW_Accent, 0.05f));
            dl.AddRect(sortMin, sortMax, MW_ColA(MW_Accent, 0.6f), 0f, 0, 1f);
        }
        // Popup menu - custom-drawn rows with an accent dot on the active
        // selection + tracked-caps "SORT BY" header. Hovered row gets a
        // subtle accent wash; active item labels in accent, others in text.
        PushEncoreMenuStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 1f * scale));
        if (ImGui.BeginPopup("##sortPopup"))
        {
            var popDl = ImGui.GetWindowDrawList();
            float headerTrack = 1.6f * scale;
            // Header row
            {
                var hPos = ImGui.GetCursorScreenPos();
                UIStyles.DrawTrackedText(popDl,
                    new Vector2(hPos.X + 8f * scale, hPos.Y + 2f * scale),
                    "SORT BY",
                    MW_Col(MW_TextFaint),
                    headerTrack);
                ImGui.Dummy(new Vector2(1f, ImGui.GetTextLineHeight() + 6f * scale));
            }
            // Thin separator
            {
                var sPos = ImGui.GetCursorScreenPos();
                float sepAvail = ImGui.GetContentRegionAvail().X;
                popDl.AddLine(sPos, new Vector2(sPos.X + sepAvail, sPos.Y),
                    MW_ColA(MW_Border, 0.6f), 1f);
                ImGui.Dummy(new Vector2(1f, 5f * scale));
            }

            float rowH = 22f * scale;
            float rowW = 150f * scale;
            for (int i = 0; i < sortOptions.Length; i++)
            {
                bool selected = i == (int)currentSort;
                var rowPos = ImGui.GetCursorScreenPos();
                ImGui.SetCursorScreenPos(rowPos);
                if (ImGui.InvisibleButton($"##sortItem_{i}", new Vector2(rowW, rowH)))
                {
                    currentSort = (SortMode)i;
                    ImGui.CloseCurrentPopup();
                }
                bool hovered = ImGui.IsItemHovered();
                var rowMin = rowPos;
                var rowMax = new Vector2(rowPos.X + rowW, rowPos.Y + rowH);

                // Hover / selection wash.
                if (selected)
                    popDl.AddRectFilled(rowMin, rowMax, MW_ColA(MW_Accent, 0.14f));
                else if (hovered)
                    popDl.AddRectFilled(rowMin, rowMax, MW_ColA(MW_Accent, 0.08f));

                // Active accent dot at left, dim dot otherwise.
                float dotR = 2.5f * scale;
                float dotX = rowMin.X + 10f * scale + dotR;
                float dotY = rowMin.Y + rowH * 0.5f;
                popDl.AddCircleFilled(new Vector2(dotX, dotY), dotR,
                    selected ? MW_Col(MW_Accent)
                             : MW_ColA(MW_TextFaint, hovered ? 0.75f : 0.40f));

                // Label.
                var labelColor = selected ? MW_Accent : (hovered ? MW_Text : MW_TextDim);
                popDl.AddText(new Vector2(dotX + dotR + 10f * scale,
                                          rowMin.Y + (rowH - ImGui.GetTextLineHeight()) * 0.5f),
                    MW_Col(labelColor), sortOptions[i]);
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleVar();
        PopEncoreMenuStyle();

        //  Pose pill (right-aligned, fixed-ish width): live pose strip 
        // "POSE - IDLE 1 - SIT 2 - GSIT 0 - DOZE 0"
        var poses = Plugin.Instance?.PoseService?.GetCurrentPoseIndices();
        float posePillW = 0f;
        float posePillX = 0f;
        if (poses != null)
        {
            var (idle, sit, groundSit, doze) = poses.Value;

            // Live-slot detection (kept) - highlight the pose type the
            // player is currently IN.
            int activeSlotIdx = -1;
            var currentPoseType = Plugin.Instance?.PoseService?.GetCurrentPoseType();
            if (currentPoseType != null)
            {
                activeSlotIdx = currentPoseType switch
                {
                    EmoteController.PoseType.Idle      => 0,
                    EmoteController.PoseType.Sit       => 1,
                    EmoteController.PoseType.GroundSit => 2,
                    EmoteController.PoseType.Doze      => 3,
                    _ => -1,
                };
            }

            // Context-only format: show just the ONE pose type the player is
            // currently in. Standing -> `IDLE #N`, sitting -> `SIT #N`, etc.
            // All four indices available via the pill's hover tooltip.
            string poseLabel;
            string poseValue;
            bool poseKnown;
            switch (activeSlotIdx)
            {
                case 0: poseLabel = "IDLE"; poseValue = "#" + idle;      poseKnown = true; break;
                case 1: poseLabel = "SIT";  poseValue = "#" + sit;       poseKnown = true; break;
                case 2: poseLabel = "GSIT"; poseValue = "#" + groundSit; poseKnown = true; break;
                case 3: poseLabel = "DOZE"; poseValue = "#" + doze;      poseKnown = true; break;
                default: poseLabel = "POSE"; poseValue = "-";            poseKnown = false; break;
            }

            float poseTrack = 0.9f * scale;
            float labelValueGap = 6f * scale;
            float wPoseLabel = UIStyles.MeasureTrackedWidth(poseLabel, poseTrack);
            float wPoseValue = UIStyles.MeasureTrackedWidth(poseValue, poseTrack);
            float posePad = 12f * scale;
            posePillW = wPoseLabel + labelValueGap + wPoseValue + posePad * 2;
            posePillX = stripEnd.X - edgePad - posePillW;

            var poseMin = new Vector2(posePillX, rowY);
            var poseMax = new Vector2(posePillX + posePillW, rowY + pillH);
            dl.AddRectFilled(poseMin, poseMax, MW_Col(new Vector4(0.078f, 0.094f, 0.125f, 1f)));
            dl.AddRect(poseMin, poseMax, MW_ColA(MW_Border, 1f), 0f, 0, 1f);

            var lblCol = poseKnown ? MW_Success : MW_TextFaint;
            var valCol = poseKnown ? MW_Success : MW_TextDim;
            float poseX = posePillX + posePad;
            UIStyles.DrawTrackedText(dl, new Vector2(poseX, labelY),
                poseLabel, MW_Col(lblCol), poseTrack);
            poseX += wPoseLabel + labelValueGap;
            UIStyles.DrawTrackedText(dl, new Vector2(poseX, labelY),
                poseValue, MW_Col(valCol), poseTrack);

            // Hover tooltip - always lists all four indices so users can
            // plan pose-preset cycling without cycling into each state.
            if (ImGui.IsMouseHoveringRect(poseMin, poseMax))
            {
                MW_PushTooltipStyle();
                ImGui.BeginTooltip();
                ImGui.TextColored(MW_TextFaint, "Current pose indices");
                ImGui.Separator();
                void Row(string lbl, int idx, bool active)
                {
                    ImGui.TextColored(active ? MW_Success : MW_TextDim, lbl);
                    ImGui.SameLine(70f * scale);
                    ImGui.TextColored(active ? MW_Success : MW_AccentBright, "#" + idx);
                }
                Row("Idle",       idle,      activeSlotIdx == 0);
                Row("Sit",        sit,       activeSlotIdx == 1);
                Row("Ground sit", groundSit, activeSlotIdx == 2);
                Row("Doze",       doze,      activeSlotIdx == 3);
                ImGui.EndTooltip();
                MW_PopTooltipStyle();
            }
        }

        // search pill: flex fill capped at 340px, right-aligned within its span
        float searchX = sortX + sortPillW + gap;
        float searchMaxRight = posePillW > 0f
            ? (posePillX - gap)
            : (stripEnd.X - edgePad);
        float searchAvail = MathF.Max(60f * scale, searchMaxRight - searchX);
        float searchW = MathF.Min(340f * scale, searchAvail);
        var searchMin = new Vector2(searchX, rowY);
        var searchMax = new Vector2(searchX + searchW, rowY + pillH);
        dl.AddRectFilled(searchMin, searchMax, MW_Col(new Vector4(0.078f, 0.094f, 0.125f, 1f)));
        dl.AddRect(searchMin, searchMax, MW_ColA(MW_Border, 1f), 0f, 0, 1f);

        // FontAwesome search glyph in the left inner padding.
        var magGlyph = FontAwesomeIcon.Search.ToIconString();
        ImGui.PushFont(UiBuilder.IconFont);
        var magSz = ImGui.CalcTextSize(magGlyph);
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(),
            new Vector2(searchX + 10f * scale,
                         rowY + (pillH - magSz.Y) * 0.5f),
            MW_Col(MW_TextFaint), magGlyph);
        ImGui.PopFont();

        // Transparent input text on top of the pill bg.
        float inputX = searchX + 10f * scale + magSz.X + 8f * scale;
        float inputW = searchW - (inputX - searchX) - 10f * scale;
        ImGui.SetCursorScreenPos(new Vector2(inputX,
            rowY + (pillH - ImGui.GetFrameHeight()) * 0.5f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.Text,           MW_Text);
        ImGui.SetNextItemWidth(inputW);
        string hint = currentTab == MainTab.Routines ? "filter routines..." : "filter presets...";
        ImGui.InputTextWithHint("##presetSearch", hint, ref presetSearchFilter, 100);
        // Focus ring - accent border + soft halo when active, eased alpha.
        {
            float targetFocus = ImGui.IsItemActive() ? 1f : 0f;
            searchFocusAlpha = ApproachAlpha(searchFocusAlpha, targetFocus, 8f);
            if (searchFocusAlpha > 0.02f)
            {
                uint ringCol = MW_ColA(MW_Accent, 0.95f * searchFocusAlpha);
                dl.AddRect(searchMin, searchMax, ringCol, 0f, 0, 1f);
                for (int i = 1; i <= 2; i++)
                {
                    float pad = i * 1.5f * scale;
                    float a = (0.22f - i * 0.08f) * searchFocusAlpha;
                    if (a < 0.02f) continue;
                    dl.AddRect(
                        new Vector2(searchMin.X - pad, searchMin.Y - pad),
                        new Vector2(searchMax.X + pad, searchMax.Y + pad),
                        MW_ColA(MW_Accent, a), 0f, 0, 1f);
                }
            }
        }
        ImGui.PopStyleColor(4);

        // Advance layout cursor by exactly the controls strip height.
        ImGui.SetCursorScreenPos(stripStart);
        ImGui.Dummy(new Vector2(1, stripH));
        ImGui.PopStyleVar(); // ItemSpacing
    }


    // Tab accents - match the routine editor's section accents so visual identity is consistent
    private static readonly Vector4 PresetsTabAccent = new(0.55f, 0.75f, 1f, 1f);     // blue
    private static readonly Vector4 RoutinesTabAccent = new(0.55f, 0.85f, 0.55f, 1f); // green


    private void DrawTabBar()
    {
        // Shrink tab labels to ~15px - readable while staying tighter than
        // the default 18px. CalcTextSize tracks the scale automatically.
        ImGui.SetWindowFontScale(0.85f);
        try { DrawTabBarInner(); }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    private void DrawTabBarInner()
    {
        var scale = UIStyles.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0));

        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winW = ImGui.GetWindowWidth();
        // Edge-to-edge strip. Absolute screen X so the background fills past any window padding.
        var rowStart = new Vector2(winPos.X, ImGui.GetCursorScreenPos().Y);
        var availW = winW;
        var rowHeight = 40f * scale;

        // Strip background + bottom border line.
        var bgEnd = new Vector2(rowStart.X + availW, rowStart.Y + rowHeight);
        dl.AddRectFilled(rowStart, bgEnd, MW_Col(new Vector4(0.047f, 0.055f, 0.075f, 1f)));
        dl.AddLine(new Vector2(rowStart.X, bgEnd.Y - 1f),
                   new Vector2(bgEnd.X, bgEnd.Y - 1f),
                   MW_ColA(MW_Border, 1f), 1f);

        // Tracked-caps labels + pilled count chips alongside each label. Family pattern.
        var presetsLabel = "PRESETS";
        var routinesLabel = "ROUTINES";
        var presetCount = Plugin.Instance?.Configuration?.Presets?.Count ?? 0;
        var routineCount = Plugin.Instance?.Configuration?.Routines?.Count ?? 0;
        var presetsCountStr = presetCount.ToString();
        var routinesCountStr = routineCount.ToString();

        var presetsActive = currentTab == MainTab.Presets;
        var routinesActive = currentTab == MainTab.Routines;

        float labelTrack = 1.4f * scale;
        float countTrack = 0.9f * scale;
        float tabPadX = 12f * scale;
        float chipPadX = 5f * scale;
        float chipPadY = 2f * scale;
        float labelToChipGap = 7f * scale;
        float edgePad = 14f * scale;

        float presetsLabelW = UIStyles.MeasureTrackedWidth(presetsLabel, labelTrack);
        float routinesLabelW = UIStyles.MeasureTrackedWidth(routinesLabel, labelTrack);
        float presetsCountW = UIStyles.MeasureTrackedWidth(presetsCountStr, countTrack);
        float routinesCountW = UIStyles.MeasureTrackedWidth(routinesCountStr, countTrack);

        // Chip width = inner text + padding; min width so single-digit counts don't pinch.
        float presetsChipInner = MathF.Max(presetsCountW, 14f * scale);
        float routinesChipInner = MathF.Max(routinesCountW, 14f * scale);
        float presetsChipW = presetsChipInner + chipPadX * 2;
        float routinesChipW = routinesChipInner + chipPadX * 2;

        float presetsW = presetsLabelW + labelToChipGap + presetsChipW + tabPadX * 2;
        float routinesW = routinesLabelW + labelToChipGap + routinesChipW + tabPadX * 2;

        float textH = ImGui.GetTextLineHeight();
        float labelY = rowStart.Y + (rowHeight - textH) * 0.5f;

        // Presets tab hit area
        float presetsX = rowStart.X + edgePad;
        ImGui.SetCursorScreenPos(new Vector2(presetsX, rowStart.Y));
        if (ImGui.InvisibleButton("##tabPresets", new Vector2(presetsW, rowHeight)))
        {
            if (currentTab != MainTab.Presets)
            {
                tabClickedAt = ImGui.GetTime();
                tabClickedPos = ImGui.GetMousePos();
                tabClickedTab = MainTab.Presets;
            }
            currentTab = MainTab.Presets;
        }
        var presetsHovered = ImGui.IsItemHovered();
        var presetsCol = presetsActive ? MW_Text : (presetsHovered ? MW_TextDim : MW_TextFaint);
        UIStyles.DrawTrackedText(dl,
            new Vector2(presetsX + tabPadX, labelY),
            presetsLabel, MW_Col(presetsCol), labelTrack);
        // Pilled count chip.
        {
            float chipX = presetsX + tabPadX + presetsLabelW + labelToChipGap;
            var chipMin = new Vector2(chipX, rowStart.Y + (rowHeight - textH) * 0.5f - chipPadY);
            var chipMax = new Vector2(chipX + presetsChipW, chipMin.Y + textH + chipPadY * 2);
            var chipAccent = presetsActive ? MW_Accent : MW_TextFaint;
            dl.AddRectFilled(chipMin, chipMax,
                MW_ColA(chipAccent, presetsActive ? 0.18f : 0.05f));
            dl.AddRect(chipMin, chipMax,
                MW_ColA(chipAccent, presetsActive ? 1f : 0.35f), 0f, 0, 1f);
            float chipTextX = chipX + (presetsChipW - presetsCountW) * 0.5f;
            UIStyles.DrawTrackedText(dl,
                new Vector2(chipTextX, labelY),
                presetsCountStr,
                MW_Col(presetsActive ? MW_Accent : MW_TextDim),
                countTrack);
        }

        // Routines tab - draw hit area + label first so we know both rects
        // before the sliding underline decides where it's headed.
        bool somethingPlayingTabs = !string.IsNullOrEmpty(Plugin.Instance?.Configuration?.ActivePresetId)
                                    || Plugin.Instance?.ActiveRoutineName != null;
        float routinesX = presetsX + presetsW;
        ImGui.SetCursorScreenPos(new Vector2(routinesX, rowStart.Y));
        if (ImGui.InvisibleButton("##tabRoutines", new Vector2(routinesW, rowHeight)))
        {
            if (currentTab != MainTab.Routines)
            {
                tabClickedAt = ImGui.GetTime();
                tabClickedPos = ImGui.GetMousePos();
                tabClickedTab = MainTab.Routines;
            }
            currentTab = MainTab.Routines;
        }
        var routinesHovered = ImGui.IsItemHovered();
        var routinesCol = routinesActive ? MW_Text : (routinesHovered ? MW_TextDim : MW_TextFaint);
        UIStyles.DrawTrackedText(dl,
            new Vector2(routinesX + tabPadX, labelY),
            routinesLabel, MW_Col(routinesCol), labelTrack);
        {
            float chipX = routinesX + tabPadX + routinesLabelW + labelToChipGap;
            var chipMin = new Vector2(chipX, rowStart.Y + (rowHeight - textH) * 0.5f - chipPadY);
            var chipMax = new Vector2(chipX + routinesChipW, chipMin.Y + textH + chipPadY * 2);
            var chipAccent = routinesActive ? MW_MacroGreen : MW_TextFaint;
            dl.AddRectFilled(chipMin, chipMax,
                MW_ColA(chipAccent, routinesActive ? 0.18f : 0.05f));
            dl.AddRect(chipMin, chipMax,
                MW_ColA(chipAccent, routinesActive ? 1f : 0.35f), 0f, 0, 1f);
            float chipTextX = chipX + (routinesChipW - routinesCountW) * 0.5f;
            UIStyles.DrawTrackedText(dl,
                new Vector2(chipTextX, labelY),
                routinesCountStr,
                MW_Col(routinesActive ? MW_MacroGreen : MW_TextDim),
                countTrack);
        }

        // 400ms slide on tab change: X eases center-to-center, width squashes/overshoots/settles
        float targetCenterX = (presetsActive ? presetsX : routinesX)
                              + (presetsActive ? presetsW : routinesW) * 0.5f;
        float targetW = presetsActive ? presetsW : routinesW;

        // Detect tab change -> start a slide from whatever was drawn last
        // frame. If nothing's ever been drawn, snap instantly (first frame).
        if (tabSlideToTab != currentTab)
        {
            bool neverDrawn = tabUnderlineX < 0f;
            if (neverDrawn)
            {
                tabSlideFromCenterX = targetCenterX;
                tabSlideFromW = targetW;
                tabSlideProgress = 1f;                    // snap, no animation
            }
            else
            {
                tabSlideFromCenterX = tabUnderlineX + tabUnderlineW * 0.5f;
                tabSlideFromW = tabUnderlineW;
                tabSlideProgress = 0f;                    // kick off the slide
            }
            tabSlideToTab = currentTab;
        }

        // Advance progress toward 1 over ~360ms.
        tabSlideProgress = MathF.Min(1f, tabSlideProgress + ImGui.GetIO().DeltaTime / 0.36f);

        float p = tabSlideProgress;

        // easeOutBack with ~4% overshoot. c1 = 0.85 bounce depth.
        const float c1 = 0.85f;
        const float c3 = c1 + 1f;
        float xm1 = p - 1f;
        float posEase = 1f + c3 * xm1 * xm1 * xm1 + c1 * xm1 * xm1;
        float widthEase = 1f - (1f - p) * (1f - p);

        // mid-transit -10% width breathe (sine peaking at p=0.5)
        float breathe = 1f - 0.10f * MathF.Sin(p * MathF.PI);

        float fromCX = tabSlideFromCenterX;
        float toCX   = targetCenterX;
        float curCX  = fromCX + (toCX - fromCX) * posEase;
        float curW   = (tabSlideFromW + (targetW - tabSlideFromW) * widthEase) * breathe;

        tabUnderlineX = curCX - curW * 0.5f;
        tabUnderlineW = MathF.Max(2f, curW);

        // Color - blend source -> target based on how far along the journey.
        float colorT = p;                                  // matches the X ease roughly
        var sourceCol = presetsActive ? MW_MacroGreen : MW_Accent;
        var targetCol = presetsActive ? MW_Accent : MW_MacroGreen;
        var slidingCol = new Vector4(
            sourceCol.X + (targetCol.X - sourceCol.X) * colorT,
            sourceCol.Y + (targetCol.Y - sourceCol.Y) * colorT,
            sourceCol.Z + (targetCol.Z - sourceCol.Z) * colorT,
            1f);

        var lineMin = new Vector2(tabUnderlineX, rowStart.Y + rowHeight - 2f * scale);
        var lineMax = new Vector2(tabUnderlineX + tabUnderlineW, rowStart.Y + rowHeight);
        // Soft halo under the underline when playing - three expanding rects
        // at low alpha fake the HTML blur(6px).
        if (somethingPlayingTabs)
        {
            for (int i = 1; i <= 3; i++)
            {
                float pad = i * 2f * scale;
                var halo = new Vector4(slidingCol.X, slidingCol.Y, slidingCol.Z, 0.16f - i * 0.04f);
                dl.AddRectFilled(
                    new Vector2(lineMin.X - pad, lineMin.Y - pad),
                    new Vector2(lineMax.X + pad, lineMax.Y),
                    ImGui.ColorConvertFloat4ToU32(halo));
            }
        }
        dl.AddRectFilled(lineMin, lineMax, MW_Col(slidingCol));

        // always-on strip; "READY" placeholder when idle so layout never shifts
        string? runningName = Plugin.Instance?.ActiveRoutineName;
        if (string.IsNullOrEmpty(runningName))
        {
            var activeId = Plugin.Instance?.Configuration?.ActivePresetId;
            if (!string.IsNullOrEmpty(activeId))
                runningName = Plugin.Instance?.Configuration?.Presets?
                    .FirstOrDefault(p => p.Id == activeId)?.Name;
        }
        bool isPlaying = !string.IsNullOrEmpty(runningName);

        float tabsRightEdge = routinesX + routinesW;
        float npRightPad = 14f * scale;
        float npRightEdge = rowStart.X + availW - npRightPad;
        float npBudget = npRightEdge - (tabsRightEdge + 16f * scale);

        if (npBudget > 60f * scale)
        {
            string kicker = isPlaying ? "NOW PLAYING" : "READY";
            string sepDot = "  -  ";
            float npTrack = 1.4f * scale;
            float eqW = UIStyles.NameEqWidth(scale);
            float eqGap = 8f * scale;

            float wKick = UIStyles.MeasureTrackedWidth(kicker, npTrack);
            var kickerCol = isPlaying ? MW_Col(MW_Rose) : MW_ColA(MW_TextFaint, 0.85f);

            if (isPlaying)
            {
                float wSepD = UIStyles.MeasureTrackedWidth(sepDot, npTrack);
                float fixedW = wKick + wSepD + eqGap + eqW;
                float nameMaxW = MathF.Max(40f * scale, npBudget - fixedW);

                // Reserve the full name-slot width even when the name is
                // short - keeps the EQ anchored so it doesn't jump around
                // when preset names change length.
                float nameW = ImGui.CalcTextSize(runningName!).X;
                float nameSlotW = MathF.Min(nameW, nameMaxW);
                float totalW = wKick + wSepD + nameSlotW + eqGap + eqW;
                float npX = npRightEdge - totalW;

                UIStyles.DrawTrackedText(dl, new Vector2(npX, labelY), kicker,
                    kickerCol, npTrack);
                npX += wKick;
                UIStyles.DrawTrackedText(dl, new Vector2(npX, labelY), sepDot,
                    MW_Col(MW_TextFaint), npTrack);
                npX += wSepD;
                // Marquee scroll when the name exceeds its budget; otherwise
                // draws static. Uses Text color to match the family pattern.
                UIStyles.DrawMarqueeTextAt(dl, new Vector2(npX, labelY),
                    runningName!, nameMaxW, MW_Text);
                npX += nameSlotW + eqGap;

                float eqH = textH * 0.72f;
                float eqBarY = labelY + (textH - eqH) * 0.5f;
                UIStyles.DrawNameEq(new Vector2(npX, eqBarY), MW_Rose, eqH, scale);
            }
            else
            {
                // Idle placeholder: dim "READY" with a static dot on the left
                // so the area still reads as the "now playing" slot.
                float dotR = 2.5f * scale;
                float dotGap = 6f * scale;
                float totalW = dotR * 2 + dotGap + wKick;
                float npX = npRightEdge - totalW;
                float dotCy = labelY + textH * 0.5f;
                dl.AddCircleFilled(new Vector2(npX + dotR, dotCy), dotR,
                    MW_ColA(MW_TextFaint, 0.6f));
                UIStyles.DrawTrackedText(dl,
                    new Vector2(npX + dotR * 2 + dotGap, labelY),
                    kicker, kickerCol, npTrack);
            }
        }

        // 200ms in / 600ms out spotlight wash beneath the newly-active tab
        if (tabClickedAt >= 0)
        {
            float age = (float)(ImGui.GetTime() - tabClickedAt);
            const float washDur = 0.8f;
            if (age >= washDur)
            {
                tabClickedAt = -1;
            }
            else
            {
                float washA;
                if (age < 0.20f)
                {
                    // Fade in: 0 -> 1 over 200ms (smoothstep).
                    float u = age / 0.20f;
                    washA = u * u * (3f - 2f * u);
                }
                else
                {
                    // Fade out: 1 -> 0 over remaining 600ms (easeOutCubic).
                    float u = (age - 0.20f) / (washDur - 0.20f);
                    washA = 1f - MathF.Pow(u, 3f);
                }

                var washCol = tabClickedTab == MainTab.Presets ? MW_Accent : MW_MacroGreen;
                float washTabX = tabClickedTab == MainTab.Presets ? presetsX : routinesX;
                float washTabW = tabClickedTab == MainTab.Presets ? presetsW : routinesW;

                // Full-tab wash - softer at the top, brighter at the bottom
                // (toward the underline) so it reads as a gentle spotlight
                // from the active underline upward, not a square block.
                uint washTop = MW_ColA(washCol, 0.04f * washA);
                uint washBot = MW_ColA(washCol, 0.22f * washA);
                dl.AddRectFilledMultiColor(
                    new Vector2(washTabX, rowStart.Y),
                    new Vector2(washTabX + washTabW, rowStart.Y + rowHeight),
                    washTop, washTop, washBot, washBot);
            }
        }

        // Advance layout cursor by exactly the tab strip height.
        ImGui.SetCursorScreenPos(rowStart);
        ImGui.Dummy(new Vector2(1, rowHeight));
        ImGui.PopStyleVar(); // ItemSpacing
    }

    // Pre-hover check for a button rect at the given screen position. Used
    // before calling UIStyles.IconButton so we can pass the correct icon
    // color (rest vs. hover-accent) into that single-call render.
    private static bool MW_IsHoveringButton(float x, float y, Vector2 size)
    {
        var mouse = ImGui.GetMousePos();
        return mouse.X >= x && mouse.X <= x + size.X
            && mouse.Y >= y && mouse.Y <= y + size.Y;
    }

    // Simple truncation helper - appends "..." (three literal dots, per bible).
    private static string MW_TruncateToFit(string s, float maxW)
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

    private void DrawRoutineList(Configuration config, float listHeight)
    {
        var scale = UIStyles.Scale;
        // Draw() pushed WindowPadding=(0,0); restore inner body padding.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 8f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(6f * scale, 4f * scale));
        if (ImGui.BeginChild("RoutineList", new Vector2(-1, listHeight), true,
                ImGuiWindowFlags.AlwaysVerticalScrollbar))
        {
            if (config.Routines.Count == 0)
            {
                DrawEmptyState(
                    heading: "NO ROUTINES YET",
                    body: "Build your first setlist. A routine stitches presets together into a timed performance, auto-advancing step by step with optional loops and macros.",
                    primaryCta: "+ New Routine",
                    primaryAction: () => routineEditorWindow?.OpenNew(),
                    secondaryCta: "Open Playbook",
                    secondaryAction: () => helpWindow?.Toggle());
            }
            else
            {
                DrawRoutinesWithFolders(config);
            }

            // Drop on empty space inside the routine list -> unfile routine / move folder to top
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left) && ImGui.IsWindowHovered())
            {
                if (dragSourceRoutineId != null)
                {
                    var dragged = config.Routines.FirstOrDefault(r => r.Id == dragSourceRoutineId);
                    if (dragged != null)
                    {
                        dragged.FolderId = null;
                        config.Save();
                    }
                    dragSourceRoutineIndex = -1;
                    dragSourceRoutineId = null;
                    isDragging = false;
                }
                else if (dragSourceFolderId != null)
                {
                    var draggedFolder = config.Folders.FirstOrDefault(f => f.Id == dragSourceFolderId);
                    if (draggedFolder != null && draggedFolder.IsRoutineFolder)
                    {
                        draggedFolder.ParentFolderId = null;
                        config.Save();
                    }
                    dragSourceFolderId = null;
                    isDragging = false;
                }
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2); // WindowPadding + ItemSpacing
    }

    private void DrawRoutinesWithFolders(Configuration config)
    {
        var routines = config.Routines;
        var folderOrder = (config.FolderOrder ?? new List<string>()).ToList();
        var folders = (config.Folders ?? new List<PresetFolder>()).Where(f => f.IsRoutineFolder).ToList();
        var folderIds = new HashSet<string>(folders.Select(f => f.Id));

        // Unfile routines whose FolderId points to a deleted folder
        foreach (var r in routines)
            if (r.FolderId != null && !folderIds.Contains(r.FolderId))
                r.FolderId = null;

        // Channel-split so folder body backgrounds paint BEHIND inner content
        var drawList = ImGui.GetWindowDrawList();
        var deferredBackgrounds = new List<(Vector2 start, float width, float height, Vector3 color, float scale)>();
        drawList.ChannelsSplit(2);
        drawList.ChannelsSetCurrent(1);

        DrawRoutineFolderLevel(null, routines, config, folderOrder, folders, deferredBackgrounds);

        // Channel 0: paint folder-body backgrounds behind the cards.
        drawList.ChannelsSetCurrent(0);
        foreach (var (start, width, height, color, bgScale) in deferredBackgrounds)
        {
            var accentWidth = 4f * bgScale;
            var bgR = color.X * 0.07f + 0.086f * 0.93f;
            var bgG = color.Y * 0.07f + 0.098f * 0.93f;
            var bgB = color.Z * 0.07f + 0.133f * 0.93f;
            drawList.AddRectFilled(start,
                new Vector2(start.X + width, start.Y + height),
                ImGui.ColorConvertFloat4ToU32(new Vector4(bgR, bgG, bgB, 1f)));
            drawList.AddRectFilled(start,
                new Vector2(start.X + accentWidth, start.Y + height),
                ImGui.ColorConvertFloat4ToU32(new Vector4(color.X, color.Y, color.Z, 1f)));
            var brR = color.X * 0.25f + MW_Border.X * 0.75f;
            var brG = color.Y * 0.25f + MW_Border.Y * 0.75f;
            var brB = color.Z * 0.25f + MW_Border.Z * 0.75f;
            var borderCol = ImGui.ColorConvertFloat4ToU32(new Vector4(brR, brG, brB, 1f));
            drawList.AddLine(new Vector2(start.X + width - 1, start.Y),
                new Vector2(start.X + width - 1, start.Y + height), borderCol, 1f);
            drawList.AddLine(new Vector2(start.X, start.Y + height - 1),
                new Vector2(start.X + width, start.Y + height - 1), borderCol, 1f);
        }
        drawList.ChannelsMerge();
    }

    private void DrawRoutineFolderLevel(string? parentFolderId,
        List<Routine> routines, Configuration config,
        List<string> folderOrder, List<PresetFolder> folders,
        List<(Vector2 start, float width, float height, Vector3 color, float scale)> deferredBackgrounds)
    {
        var scale = UIStyles.Scale;

        // Routines at this level (preserve list order)
        for (int i = 0; i < routines.Count; i++)
            if (routines[i].FolderId == parentFolderId)
                DrawRoutineCard(routines[i], i, config);

        // Child folders at this level, in FolderOrder sequence
        var childFolders = folderOrder
            .Select(id => folders.FirstOrDefault(f => f.Id == id))
            .Where(f => f != null && f.ParentFolderId == parentFolderId)
            .ToList();

        foreach (var folder in childFolders)
        {
            if (folder == null) continue;
            // Count items in this subtree (routines + direct child folders) for the header badge
            var subtreeRoutines = CountRoutinesInSubtree(folder.Id, routines, folders);
            var directChildFolders = folders.Count(f => f.ParentFolderId == folder.Id);
            var totalCount = subtreeRoutines + directChildFolders;

            var hasContent = totalCount > 0;
            var isExpandedWithContent = !folder.IsCollapsed && hasContent;

            DrawFolderHeader(folder, totalCount, config, isExpandedWithContent);

            if (isExpandedWithContent)
            {
                var folderColor = folder.Color ?? DefaultFolderColor;
                var accentWidth = 4f * scale;
                var indent = accentWidth + 10f * scale;
                var paddingTop = 6f * scale;
                var paddingBottom = 22f * scale;

                var startPos = ImGui.GetCursorScreenPos();
                var contentWidth = ImGui.GetContentRegionAvail().X;

                ImGui.SetCursorScreenPos(new Vector2(startPos.X, startPos.Y + paddingTop));
                ImGui.Indent(indent);

                DrawRoutineFolderLevel(folder.Id, routines, config, folderOrder, folders, deferredBackgrounds);

                ImGui.Unindent(indent);

                var totalHeight = ImGui.GetCursorScreenPos().Y - startPos.Y + paddingBottom;
                deferredBackgrounds.Add((startPos, contentWidth, totalHeight, folderColor, scale));

                var endY = startPos.Y + totalHeight + ImGui.GetStyle().ItemSpacing.Y;
                if (ImGui.GetCursorScreenPos().Y < endY)
                    ImGui.SetCursorScreenPos(new Vector2(startPos.X, endY));
                ImGui.Spacing();
            }
        }
    }

    /// <summary>Count routines directly in a folder plus routines in all descendant folders.</summary>
    private int CountRoutinesInSubtree(string folderId, List<Routine> routines, List<PresetFolder> folders)
    {
        var count = routines.Count(r => r.FolderId == folderId);
        foreach (var child in folders.Where(f => f.ParentFolderId == folderId))
            count += CountRoutinesInSubtree(child.Id, routines, folders);
        return count;
    }

    private void DrawRoutineCard(Routine routine, int index, Configuration config)
    {
        ImGui.PushID($"routine_{routine.Id}");

        var scale = UIStyles.Scale;
        var isActive = Plugin.Instance?.IsRoutineActive(routine.Id) == true;
        var cardHeight = 74f * scale;

        // Routines always use the green accent regardless of active state; the active state
        // instead shows as a subtle green-tinted hover/selected wash.
        var accentColor = RoutinesTabAccent;
        var cardColor = new Vector4(0.11f, 0.12f, 0.17f, 1f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, cardColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));

        // Capture card position BEFORE BeginChild for drop-target detection after EndChild
        var cardScreenPos = ImGui.GetCursorScreenPos();
        var cardWidth = ImGui.GetContentRegionAvail().X;

        if (ImGui.BeginChild($"routinecard_{index}", new Vector2(-1, cardHeight), false,
                ImGuiWindowFlags.NoScrollbar))
        {
            // Track hover - drives NoMove on next frame AND hover visuals
            var isHovered = ImGui.IsWindowHovered();
            if (isHovered)
            {
                anyCardHovered = true;
                hoveredCardIdThisFrame = routine.Id;
            }

            // Eased hover alpha - ramps 0->1 on hover, back to 0 on blur.
            // Drives cursor glow + icon scale + name underline intensity.
            cardHoverAlpha.TryGetValue(routine.Id, out var hoverAlpha);
            hoverAlpha = ApproachAlpha(hoverAlpha, isHovered ? 1f : 0f, 6f);
            cardHoverAlpha[routine.Id] = hoverAlpha;

            // Sibling dim: fade to 45% when another card was hovered last
            // frame and this routine isn't active. Eased so it doesn't snap.
            var shouldDim = hoveredCardIdLastFrame != null
                            && hoveredCardIdLastFrame != routine.Id
                            && !isActive;
            cardDimAlpha.TryGetValue(routine.Id, out var dimAlpha);
            dimAlpha = ApproachAlpha(dimAlpha, shouldDim ? 0.45f : 0f, 5f);
            cardDimAlpha[routine.Id] = dimAlpha;

            var cardContentWidth = ImGui.GetContentRegionAvail().X;

            //  Card visuals: bg -> border -> accent stripe, with hover + active states 
            {
                var dl = ImGui.GetWindowDrawList();
                var winPos = ImGui.GetWindowPos();
                var winSize = ImGui.GetWindowSize();
                var cardMinIn = winPos;
                var cardMaxIn = new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y);

                var hoverBg = new Vector4(0.137f, 0.153f, 0.214f, 1f);
                var hoverBorder = new Vector4(0.30f, 0.33f, 0.40f, 1f);
                var restBorder = new Vector4(0.20f, 0.22f, 0.27f, 1f);
                var activeBg = new Vector4(
                    accentColor.X * 0.18f + 0.11f * 0.82f,
                    accentColor.Y * 0.18f + 0.12f * 0.82f,
                    accentColor.Z * 0.18f + 0.17f * 0.82f, 1f);

                Vector4 bg;
                if (isActive) bg = activeBg;
                else if (isHovered) bg = hoverBg;
                else bg = cardColor;
                var brd = isHovered || isActive ? hoverBorder : restBorder;

                dl.AddRectFilled(cardMinIn, cardMaxIn, ImGui.ColorConvertFloat4ToU32(bg));
                dl.AddRect(cardMinIn, cardMaxIn, ImGui.ColorConvertFloat4ToU32(brd));
                // Gradient accent stripe (light-top -> saturated-mid -> dark-bot) + 1px
                // inner top highlight. Replaces the old flat-color stripe.
                UIStyles.DrawCardAccentStripe(dl, cardMinIn, cardMaxIn, accentColor,
                    isHovered || isActive, scale);
                UIStyles.DrawCardTopHighlight(dl, cardMinIn, cardMaxIn, scale);
                // hoverAlpha-faded radial glow; active cards drift even unhovered
                if (hoverAlpha > 0.02f)
                {
                    UIStyles.DrawCursorGlow(dl, cardMinIn, cardMaxIn, accentColor, scale, hoverAlpha);
                }
                else if (isActive)
                {
                    var idleC = UIStyles.IdleGlowCenter(cardMinIn, cardMaxIn);
                    UIStyles.DrawCursorGlow(dl, cardMinIn, cardMaxIn, accentColor, scale,
                        alphaMul: 0.70f, centerOverride: idleC);
                }
                // stripe glow only; sibling-dim handles the focus signal
                if (isActive)
                    UIStyles.DrawPlayingStripeGlow(dl, cardMinIn, cardMaxIn, accentColor, scale);
            }

            // Double-click anywhere on the card -> open the editor
            if (isHovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                routineEditorWindow?.OpenEdit(routine);

            // Top padding
            ImGui.Dummy(new Vector2(1, 10f * scale));
            // Left padding
            ImGui.Indent(14f * scale);

            // Drag handle - InvisibleButton over the card's main content area (left of the
            // play/kebab column). Mirrors the preset card pattern.
            {
                var saveCursor = ImGui.GetCursorScreenPos();
                var cardWinPos = ImGui.GetWindowPos();
                var playKebabWHere = 50f * scale + ImGui.GetStyle().ItemSpacing.X + 28f * scale;
                var handleW = ImGui.GetWindowSize().X - playKebabWHere - 16f * scale;
                if (handleW < 40f * scale) handleW = 40f * scale;
                ImGui.SetCursorScreenPos(cardWinPos);
                ImGui.InvisibleButton($"routineDragHandle_{index}", new Vector2(handleW, cardHeight));
                // Double-click on the handle -> open editor. Needed because the
                // invisible button eats clicks before the outer hover check sees them.
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    routineEditorWindow?.OpenEdit(routine);
                // Single-click spawns a material ripple from the click point.
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    cardRippleStart[routine.Id] = ((float)ImGui.GetTime(), ImGui.GetMousePos());
                }
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
                {
                    isDragging = true;
                    dragSourceRoutineIndex = index;
                    dragSourceRoutineId = routine.Id;
                    ImGui.SetDragDropPayload("ROUTINE_REORDER", ReadOnlySpan<byte>.Empty, ImGuiCond.None);
                    ImGui.Text(routine.Name);
                    ImGui.EndDragDropSource();
                }
                ImGui.SetCursorScreenPos(saveCursor);
            }

            // Capture icon top Y so the text group can top-align with the icon
            var iconTopY = ImGui.GetCursorScreenPos().Y;

            // Icon - 44px
            var iconSize = 44f * scale;

            // Beat ripple + vinyl groove halo rings around the icon when
            // active. Drawn BEFORE the icon so it renders on top.
            if (isActive)
            {
                var rippleDl = ImGui.GetWindowDrawList();
                var iconLeft = ImGui.GetCursorScreenPos();
                var iconCenter = new Vector2(
                    iconLeft.X + iconSize * 0.5f,
                    iconLeft.Y + iconSize * 0.5f);
                UIStyles.DrawBeatRipple(rippleDl, iconCenter, accentColor, iconSize * 0.5f);
                UIStyles.DrawIconHaloRings(rippleDl, iconCenter, iconSize * 0.5f, accentColor, scale);
            }

            // Resolve texture + uvs once, then render - beat-kick path for
            // isActive routines uses drawlist AddImage with a scaled size;
            // idle routines use ImGui.Image as before.
            Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? rtTex = null;
            Vector2 rtUv0 = Vector2.Zero, rtUv1 = Vector2.One;
            if (!string.IsNullOrEmpty(routine.CustomIconPath) && File.Exists(routine.CustomIconPath))
            {
                rtTex = GetCustomIcon(routine.CustomIconPath);
                if (rtTex != null)
                    (rtUv0, rtUv1) = CalcIconUV(rtTex.Width, rtTex.Height,
                        routine.IconZoom, routine.IconOffsetX, routine.IconOffsetY);
            }
            else if (routine.IconId.HasValue)
            {
                rtTex = GetGameIcon(routine.IconId.Value);
            }

            // Icon scaling: hover 1.06x (eased via hoverAlpha) x beat-kick
            // when active. Draw via drawlist whenever any scaling is active;
            // Dummy reserves layout space so surrounding widgets don't shift.
            float rtHoverScale = 1f + 0.06f * hoverAlpha;
            float rtBeatKick = isActive ? UIStyles.BeatKickScale(0.08f) : 1f;
            float rtScaleFactor = rtHoverScale * rtBeatKick;
            bool drawRtIconScaled = rtTex != null && (hoverAlpha > 0.02f || isActive);
            if (drawRtIconScaled)
            {
                var iconLeft2 = ImGui.GetCursorScreenPos();
                var drawSize = iconSize * rtScaleFactor;
                var off = (iconSize - drawSize) * 0.5f;
                var iconDl = ImGui.GetWindowDrawList();
                iconDl.AddImage(rtTex!.Handle,
                    new Vector2(iconLeft2.X + off, iconLeft2.Y + off),
                    new Vector2(iconLeft2.X + off + drawSize, iconLeft2.Y + off + drawSize),
                    rtUv0, rtUv1);
                ImGui.Dummy(new Vector2(iconSize, iconSize));
            }
            else if (rtTex != null)
            {
                ImGui.Image(rtTex.Handle, new Vector2(iconSize, iconSize), rtUv0, rtUv1);
            }
            else
            {
                DrawPlaceholderIcon(iconSize, scale);
            }

            ImGui.SameLine();

            // Center text block vertically against icon (original behavior).
            var textBlockH = ImGui.GetTextLineHeight() * 3f + 2f * scale * 2f;
            var textTopY = iconTopY + (iconSize - textBlockH) / 2f;
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, textTopY));

            // Precompute type label (step summary / loop status) - rendered as
            // small loose text in the card's bottom-right corner, no box.
            string chipText;
            Vector4 chipCol;
            {
                var stepCount = routine.Steps.Count;
                chipText = $"{stepCount} step{(stepCount == 1 ? "" : "s")}";
                if (routine.RepeatLoop) chipText += " - loop";
                chipCol = new Vector4(0.55f, 0.58f, 0.63f, 1f);                     // neutral info
            }
            var chipTextSize = ImGui.CalcTextSize(chipText);
            var chipW = chipTextSize.X;

            // Right-column reservation - FIXED at play+kebab width so play/kebab sits at the
            // same X position on every card.
            var textStartX = ImGui.GetCursorPosX();
            var buttonWidth = 50f * scale;
            var menuWidth = 28f * scale;
            var btnSpacing = ImGui.GetStyle().ItemSpacing.X;
            var rightInset = 4f * scale;
            var playKebabW = buttonWidth + btnSpacing + menuWidth;
            var rightGroupW = playKebabW;
            var buttonsStartX = cardContentWidth - rightGroupW - rightInset;
            var maxTextWidth = buttonsStartX - textStartX - 8 * scale;

            // Text group: name / /cmd / step summary
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f * scale));
            ImGui.BeginGroup();

            // Cap name at 30% of card width - same rule as preset card.
            var nameMax = MathF.Min(maxTextWidth, cardContentWidth * 0.30f);
            if (!routine.Enabled)
                nameMax -= ImGui.CalcTextSize(" (disabled)").X + ImGui.GetStyle().ItemSpacing.X;
            if (isActive)
                nameMax -= UIStyles.NameEqWidth(scale) + 6f * scale;

            var nameRowStartY = ImGui.GetCursorScreenPos().Y;
            UIStyles.DrawMarqueeText(routine.Name, nameMax,
                routine.Enabled ? MW_Text : new Vector4(0.55f, 0.56f, 0.60f, 1f),
                disabled: !routine.Enabled,
                animate: isHovered || isActive);
            // Accent underline on hover - spans the rendered name width,
            // fades via hoverAlpha.
            if (hoverAlpha > 0.02f && routine.Enabled)
            {
                var nmMin = ImGui.GetItemRectMin();
                var nmMax = ImGui.GetItemRectMax();
                UIStyles.DrawNameUnderline(ImGui.GetWindowDrawList(),
                    nmMin.X, nmMax.X, nmMax.Y + 1f * scale, accentColor, scale, hoverAlpha);
            }
            if (!routine.Enabled)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(disabled)");
            }
            if (isActive)
            {
                var lastItemMax = ImGui.GetItemRectMax();
                var textH = ImGui.GetTextLineHeight();
                var eqH = textH * 0.72f;
                var eqPos = new Vector2(
                    lastItemMax.X + 6f * scale,
                    nameRowStartY + (textH - eqH) * 0.5f);
                UIStyles.DrawNameEq(eqPos, accentColor, eqH, scale);
            }

            if (!string.IsNullOrEmpty(routine.ChatCommand))
                ImGui.TextColored(new Vector4(0.49f, 0.65f, 0.85f, 1f), $"/{routine.ChatCommand}");
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.40f, 0.41f, 0.45f, 1f));
                ImGui.Text("- no command");
                ImGui.PopStyleColor();
            }

            // Row 3 - when running, show step-progress dots. Otherwise show the
            // usual "routine" / "looping routine" descriptor.
            if (isActive && Plugin.Instance is { } plug
                && plug.ActiveRoutineStepCount > 0)
            {
                var dotsStartPos = ImGui.GetCursorScreenPos();
                UIStyles.DrawRoutineStepDots(dotsStartPos,
                    plug.ActiveRoutineStepIndex,
                    plug.ActiveRoutineStepCount,
                    accentColor, scale);
                // Reserve vertical space for the dots row so layout advances.
                ImGui.Dummy(new Vector2(maxTextWidth, 6f * scale + 2f * scale));
            }
            else
            {
                var row3 = routine.RepeatLoop ? "looping routine" : "routine";
                UIStyles.DrawMarqueeText(row3, maxTextWidth,
                    new Vector4(0.40f, 0.41f, 0.45f, 1f),
                    animate: isHovered);
            }

            ImGui.EndGroup();
            ImGui.PopStyleVar();

            //  Action row: Play/Stop + kebab centered and nudged up so the
            // small bottom-right type tag has clean space under them. 
            ImGui.SameLine(buttonsStartX);
            var playH = ImGui.GetFrameHeight();
            var cardTopScreenY = ImGui.GetWindowPos().Y;
            var actionRowY = cardTopScreenY + (cardHeight - playH) / 2f - 16f * scale;
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, actionRowY));

            var playRowStartPos = ImGui.GetCursorScreenPos();
            var menuH = ImGui.GetFrameHeight();

            float nowT = (float)ImGui.GetTime();
            bool rtPlayActivating = playActivationStart.TryGetValue(routine.Id, out var _rtPlayStart)
                                    && nowT - _rtPlayStart < 0.9f;
            bool rtStopActivating = stopActivationStart.TryGetValue(routine.Id, out var _rtStopStart)
                                    && nowT - _rtStopStart < 0.9f;
            bool rtShowAsActive;
            if (rtPlayActivating)      rtShowAsActive = false;
            else if (rtStopActivating) rtShowAsActive = true;
            else                       rtShowAsActive = isActive;
            ImGui.SetCursorScreenPos(playRowStartPos);
            if (rtShowAsActive)
            {
                float stopKick = 1f;
                if (stopActivationStart.TryGetValue(routine.Id, out var sKickStart))
                    stopKick = UIStyles.PlayKickScale(nowT - sKickStart);
                if (UIStyles.DrawStopButton($"##stopR_{routine.Id}",
                        new Vector2(buttonWidth, playH), stopKick, scale))
                {
                    Plugin.Instance?.CancelRoutine("manual");
                    stopActivationStart[routine.Id] = nowT;
                    playActivationStart.Remove(routine.Id);
                }
                var stopBtnMin = ImGui.GetItemRectMin();
                var stopBtnMax = ImGui.GetItemRectMax();
                if (stopActivationStart.TryGetValue(routine.Id, out var stopBurstStart))
                {
                    float el = nowT - stopBurstStart;
                    if (el > 0.7f)
                        stopActivationStart.Remove(routine.Id);
                    else
                    {
                        var bc = new Vector2((stopBtnMin.X + stopBtnMax.X) * 0.5f,
                                             (stopBtnMin.Y + stopBtnMax.Y) * 0.5f);
                        UIStyles.DrawPlayActivation(ImGui.GetWindowDrawList(),
                            bc, el, scale, new Vector4(0.83f, 0.53f, 0.47f, 1f));
                    }
                }
            }
            else
            {
                float kick = 1f;
                if (playActivationStart.TryGetValue(routine.Id, out var kickStart))
                    kick = UIStyles.PlayKickScale((float)ImGui.GetTime() - kickStart);
                if (UIStyles.DrawPlayButton($"##playR_{routine.Id}",
                        new Vector2(buttonWidth, playH), kick, scale))
                {
                    Plugin.Instance?.ExecuteRoutine(routine);
                    playActivationStart[routine.Id] = nowT;
                    // Clear any stale stop activation so there's no conflict.
                    stopActivationStart.Remove(routine.Id);
                }
                var playBtnMin = ImGui.GetItemRectMin();
                var playBtnMax = ImGui.GetItemRectMax();
                // Resting green breath around the PLAY button.
                UIStyles.DrawPlayButtonBloom(ImGui.GetWindowDrawList(),
                    playBtnMin, playBtnMax, scale);
                // Click burst - ring + 8 sparks for 700ms.
                if (playActivationStart.TryGetValue(routine.Id, out var playStart))
                {
                    float elapsed = (float)ImGui.GetTime() - playStart;
                    if (elapsed > 0.7f)
                        playActivationStart.Remove(routine.Id);
                    else
                    {
                        var btnCenter = new Vector2(
                            (playBtnMin.X + playBtnMax.X) * 0.5f,
                            (playBtnMin.Y + playBtnMax.Y) * 0.5f);
                        UIStyles.DrawPlayActivation(ImGui.GetWindowDrawList(),
                            btnCenter, elapsed, scale);
                    }
                }
            }

            // Kebab button explicitly to the right of Play
            var kebabX = playRowStartPos.X + buttonWidth + btnSpacing;
            ImGui.SetCursorScreenPos(new Vector2(kebabX, playRowStartPos.Y));
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.16f, 0.18f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.25f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.26f, 0.29f, 0.35f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border,        new Vector4(0.28f, 0.30f, 0.36f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            if (ImGui.Button("...", new Vector2(menuWidth, menuH)))
                ImGui.OpenPopup($"routineMenu_{index}");
            UIStyles.DrawHoverSheenOnLastItem(ImGui.GetWindowDrawList(),
                ImGui.IsItemHovered(), 0.22f);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);

            // Small loose text tucked into the card's bottom-right corner.
            // No box, no border - rendered at 82% font scale.
            if (chipW > 0)
            {
                var chipDl = ImGui.GetWindowDrawList();
                var cardWinPos2 = ImGui.GetWindowPos();
                var cardWinSize = ImGui.GetWindowSize();
                // Native font size (no SetWindowFontScale) so the text stays
                // crisp - GPU downscaling via font scale was reading blurry.
                var smallSz = ImGui.CalcTextSize(chipText);
                var rightInsetPx = 6f * scale;
                var bottomInsetPx = 4f * scale;
                var chipX = cardWinPos2.X + cardWinSize.X - smallSz.X - rightInsetPx;
                var chipY = cardWinPos2.Y + cardWinSize.Y - smallSz.Y - bottomInsetPx;
                chipDl.AddText(new Vector2(chipX, chipY),
                    ImGui.ColorConvertFloat4ToU32(chipCol), chipText);
            }

            //  Context menu (Encore-themed) 
            bool deleted = false;
            PushEncoreMenuStyle();
            if (ImGui.BeginPopup($"routineMenu_{index}"))
            {
                if (ImGui.MenuItem("Edit"))
                    routineEditorWindow?.OpenEdit(routine);

                if (ImGui.MenuItem(routine.Enabled ? "Disable" : "Enable"))
                {
                    routine.Enabled = !routine.Enabled;
                    Plugin.Instance?.UpdatePresetCommands();
                    config.Save();
                }

                if (ImGui.MenuItem("Duplicate"))
                {
                    config.Routines.Add(routine.Clone());
                    config.Save();
                }

                ImGui.Separator();

                // Move to Folder submenu
                var routineFolders = (config.Folders ?? new List<PresetFolder>())
                    .Where(f => f.IsRoutineFolder).ToList();
                if (routineFolders.Count > 0 && ImGui.BeginMenu("Move to Folder"))
                {
                    if (ImGui.MenuItem("(None)", "", routine.FolderId == null))
                    {
                        routine.FolderId = null;
                        config.Save();
                    }
                    foreach (var rf in routineFolders)
                    {
                        if (ImGui.MenuItem(rf.Name, "", routine.FolderId == rf.Id))
                        {
                            routine.FolderId = rf.Id;
                            config.Save();
                        }
                    }
                    ImGui.EndMenu();
                }

                ImGui.Separator();

                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.93f, 0.48f, 0.48f, 1f));
                if (ImGui.MenuItem("Delete"))
                {
                    var io = ImGui.GetIO();
                    if (io.KeyCtrl && io.KeyShift)
                    {
                        config.Routines.RemoveAt(index);
                        Plugin.Instance?.UpdatePresetCommands();
                        config.Save();
                        deleted = true;
                    }
                }
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    MW_SetTooltip("Hold Ctrl+Shift and click to delete");

                ImGui.EndPopup();
            }
            PopEncoreMenuStyle();

            if (deleted)
            {
                ImGui.EndChild();
                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
                ImGui.PopID();
                return;
            }

            // Playing-card overlay flair - rotating rainbow border.
            if (isActive)
            {
                var flairDl = ImGui.GetWindowDrawList();
                var wp = ImGui.GetWindowPos();
                var ws = ImGui.GetWindowSize();
                var cMin = wp;
                var cMax = new Vector2(wp.X + ws.X, wp.Y + ws.Y);
                UIStyles.DrawConicRainbowRing(flairDl, cMin, cMax, scale);
            }

            // Click ripple - expanding material circle from click point.
            if (cardRippleStart.TryGetValue(routine.Id, out var ripple))
            {
                float elapsed = (float)ImGui.GetTime() - ripple.start;
                if (elapsed > 1.1f)
                    cardRippleStart.Remove(routine.Id);
                else
                    UIStyles.DrawCardRipple(ImGui.GetWindowDrawList(),
                        ripple.pos, elapsed, accentColor, scale);
            }

            // Sibling dim - translucent overlay covering the whole card, eased
            // via dimAlpha so the transition isn't abrupt.
            if (dimAlpha > 0.02f)
            {
                var dimDl = ImGui.GetWindowDrawList();
                var dimMin = ImGui.GetWindowPos();
                var dimSize = ImGui.GetWindowSize();
                dimDl.AddRectFilled(dimMin,
                    new Vector2(dimMin.X + dimSize.X, dimMin.Y + dimSize.Y),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, dimAlpha)));
            }

        }
        ImGui.EndChild();
        ImGui.PopStyleVar();   // WindowPadding
        ImGui.PopStyleColor(); // ChildBg

        //  Drop target on the card for reordering routines 
        if (dragSourceRoutineId != null && dragSourceRoutineId != routine.Id)
        {
            var cardMin = cardScreenPos;
            var cardMax = new Vector2(cardScreenPos.X + cardWidth, cardScreenPos.Y + cardHeight);
            var mousePos = ImGui.GetMousePos();
            var isHovering = mousePos.X >= cardMin.X && mousePos.X <= cardMax.X &&
                             mousePos.Y >= cardMin.Y && mousePos.Y <= cardMax.Y;
            if (isHovering)
            {
                var dl = ImGui.GetWindowDrawList();
                uint col = ImGui.ColorConvertFloat4ToU32(new Vector4(0.45f, 0.92f, 0.55f, 1f));
                dl.AddRect(cardMin, cardMax, col, 0, ImDrawFlags.None, 2 * UIStyles.Scale);
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    var routines = config.Routines;
                    if (dragSourceRoutineIndex >= 0 && dragSourceRoutineIndex < routines.Count)
                    {
                        var dragged = routines[dragSourceRoutineIndex];
                        routines.RemoveAt(dragSourceRoutineIndex);
                        var targetIdx = routines.IndexOf(routine);
                        if (targetIdx >= 0)
                        {
                            routines.Insert(targetIdx, dragged);
                            dragged.FolderId = routine.FolderId;
                        }
                        else
                        {
                            routines.Add(dragged);
                        }
                        config.Save();
                    }
                    dragSourceRoutineIndex = -1;
                    dragSourceRoutineId = null;
                    isDragging = false;
                }
            }
        }

        ImGui.PopID();
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

    // Step-color cycle reused from routine editor for scanable preset cards.
    private static readonly Vector4[] PresetCardCycle =
    {
        new(0.38f, 0.72f, 1.00f, 1f),  // sky blue
        new(0.72f, 0.52f, 1.00f, 1f),  // violet
        new(1.00f, 0.42f, 0.70f, 1f),  // magenta-pink
        new(1.00f, 0.62f, 0.25f, 1f),  // orange
        new(0.45f, 0.92f, 0.55f, 1f),  // spring green
        new(0.28f, 0.88f, 0.92f, 1f),  // cyan
        new(1.00f, 0.82f, 0.30f, 1f),  // gold
        new(1.00f, 0.50f, 0.45f, 1f),  // coral
    };

    private bool DrawPresetCard(DancePreset preset, int index, Vector3? folderColor = null)
    {
        ImGui.PushID($"preset_{preset.Id}");

        var scale = UIStyles.Scale;
        var isFavorite = Plugin.Instance?.Configuration.FavoritePresetIds?.Contains(preset.Id) ?? false;
        // Playing state - drives beat ripple, pulse, stripe glow, name-EQ, and
        // PLAY->STOP button swap.
        var isPlaying = !string.IsNullOrEmpty(Plugin.Instance?.Configuration?.ActivePresetId)
                        && Plugin.Instance.Configuration.ActivePresetId == preset.Id;
        var cardHeight = 74f * scale;

        // Pick per-card accent - folder-scoped cards inherit the folder color so the cluster
        // reads as a family; top-level cards cycle through the vibrant palette.
        var accentColor = folderColor.HasValue
            ? new Vector4(folderColor.Value.X, folderColor.Value.Y, folderColor.Value.Z, 1f)
            : PresetCardCycle[index % PresetCardCycle.Length];

        var cardColor = new Vector4(0.11f, 0.12f, 0.17f, 1f);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, cardColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));

        var cardScreenPos = ImGui.GetCursorScreenPos();
        var cardWidth = ImGui.GetContentRegionAvail().X;

        // border=false: own border drawn after EndChild via drawlist
        if (ImGui.BeginChild($"card_{index}", new Vector2(-1, cardHeight), false,
                ImGuiWindowFlags.NoScrollbar))
        {
            var isHovered = ImGui.IsWindowHovered();
            if (isHovered)
            {
                anyCardHovered = true;
                hoveredCardIdThisFrame = preset.Id;
            }

            // Eased hover alpha - ramps 0->1 on hover, back to 0 on blur. Drives
            // cursor glow, icon scale, star glow, and name underline intensity
            // so those all fade in/out instead of snapping.
            cardHoverAlpha.TryGetValue(preset.Id, out var hoverAlpha);
            hoverAlpha = ApproachAlpha(hoverAlpha, isHovered ? 1f : 0f, 6f);
            cardHoverAlpha[preset.Id] = hoverAlpha;

            // sibling dim to 45%; playing cards always full-bright
            var shouldDim = hoveredCardIdLastFrame != null
                            && hoveredCardIdLastFrame != preset.Id
                            && !isPlaying;
            cardDimAlpha.TryGetValue(preset.Id, out var dimAlpha);
            dimAlpha = ApproachAlpha(dimAlpha, shouldDim ? 0.45f : 0f, 5f);
            cardDimAlpha[preset.Id] = dimAlpha;

            // Content width for right-aligning buttons (must capture before drawing changes cursor)
            var cardContentWidth = ImGui.GetContentRegionAvail().X;

            // Draw card visuals directly on the child's drawlist: bg -> accent bar -> border.
            // On hover: bg brightens to surface-3 and border lifts - matches HTML .card:hover.
            {
                var dl = ImGui.GetWindowDrawList();
                var winPos = ImGui.GetWindowPos();
                var winSize = ImGui.GetWindowSize();
                var cardMinIn = winPos;
                var cardMaxIn = new Vector2(winPos.X + winSize.X, winPos.Y + winSize.Y);

                var hoverBg = new Vector4(0.137f, 0.153f, 0.214f, 1f);     // surface-3
                var hoverBorder = new Vector4(0.30f, 0.33f, 0.40f, 1f);    // border-hi (brighter)
                var restBg = cardColor;
                var restBorder = new Vector4(0.20f, 0.22f, 0.27f, 1f);

                var bg = isHovered ? hoverBg : restBg;
                var brd = isHovered ? hoverBorder : restBorder;

                dl.AddRectFilled(cardMinIn, cardMaxIn, ImGui.ColorConvertFloat4ToU32(bg));
                dl.AddRect(cardMinIn, cardMaxIn, ImGui.ColorConvertFloat4ToU32(brd));
                UIStyles.DrawCardAccentStripe(dl, cardMinIn, cardMaxIn, accentColor, isHovered || isPlaying, scale);
                UIStyles.DrawCardTopHighlight(dl, cardMinIn, cardMaxIn, scale);
                if (hoverAlpha > 0.02f)
                {
                    UIStyles.DrawCursorGlow(dl, cardMinIn, cardMaxIn, accentColor, scale, hoverAlpha);
                }
                else if (isPlaying)
                {
                    var idleC = UIStyles.IdleGlowCenter(cardMinIn, cardMaxIn);
                    UIStyles.DrawCursorGlow(dl, cardMinIn, cardMaxIn, accentColor, scale,
                        alphaMul: 0.70f, centerOverride: idleC);
                }
                // stripe glow only; sibling-dim handles the spotlight
                if (isPlaying)
                    UIStyles.DrawPlayingStripeGlow(dl, cardMinIn, cardMaxIn, accentColor, scale);
            }

            // Top padding - pushes icon/text/buttons down from the card's top edge.
            ImGui.Dummy(new Vector2(1, 10f * scale));

            // Inner left padding - accent bar (3px) + breathing room so icon sits inset from
            // the card's left edge, matching HTML .card { padding: 10px 12px 10px 10px }.
            ImGui.Indent(14f * scale);

            // explicit InvisibleButton drag handle (SourceAllowNullId fails with positioned children)
            if (currentSort == SortMode.Custom)
            {
                var saveCursor = ImGui.GetCursorScreenPos();
                var cardWinPos = ImGui.GetWindowPos();
                var playKebabWHere = 50f * scale + ImGui.GetStyle().ItemSpacing.X + 28f * scale;
                var handleW = ImGui.GetWindowSize().X - playKebabWHere - 16f * scale;
                if (handleW < 40f * scale) handleW = 40f * scale;
                ImGui.SetCursorScreenPos(cardWinPos);
                ImGui.InvisibleButton($"dragHandle_{index}", new Vector2(handleW, cardHeight));
                // Double-click on the handle -> open editor. Needed because the
                // invisible button eats clicks before the outer IsWindowHovered
                // check at the end of the function can see them.
                if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    editorWindow?.OpenEdit(preset);
                // ripple from click point; PLAY/STOP/kebab buttons don't hit this handle
                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    cardRippleStart[preset.Id] = ((float)ImGui.GetTime(), ImGui.GetMousePos());
                }
                if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullId))
                {
                    isDragging = true;
                    dragSourceIndex = index;
                    dragSourcePresetId = preset.Id;
                    ImGui.SetDragDropPayload("PRESET_REORDER", ReadOnlySpan<byte>.Empty, ImGuiCond.None);
                    ImGui.Text(preset.Name);
                    ImGui.EndDragDropSource();
                }
                // Restore cursor for subsequent icon/text rendering
                ImGui.SetCursorScreenPos(saveCursor);
            }

            // Capture the icon's top-Y so we can align the text group's first line with it.
            // After SameLine() ImGui baseline-aligns subsequent text to the image, which drops
            // text down to near the icon's bottom. We override that below to start from iconTopY.
            var iconTopY = ImGui.GetCursorScreenPos().Y;

            // Icon - 44px to leave ~12px top/bottom breathing room in a 68px card
            var iconSize = 44f * scale;

            // 2 ripple rings + 3 halo rings; drawn before icon
            if (isPlaying)
            {
                var rippleDl = ImGui.GetWindowDrawList();
                var iconLeft = ImGui.GetCursorScreenPos();
                var iconCenter = new Vector2(
                    iconLeft.X + iconSize * 0.5f,
                    iconLeft.Y + iconSize * 0.5f);
                UIStyles.DrawBeatRipple(rippleDl, iconCenter, accentColor, iconSize * 0.5f);
                UIStyles.DrawIconHaloRings(rippleDl, iconCenter, iconSize * 0.5f, accentColor, scale);
            }
            // resolved separately so beat-kick path can drawlist-render at scaled size with fixed layout space
            Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? resolvedTex = null;
            Vector2 iconUv0 = Vector2.Zero, iconUv1 = Vector2.One;
            if (!string.IsNullOrEmpty(preset.CustomIconPath) && File.Exists(preset.CustomIconPath))
            {
                resolvedTex = GetCustomIcon(preset.CustomIconPath);
                if (resolvedTex != null)
                    (iconUv0, iconUv1) = CalcIconUV(resolvedTex.Width, resolvedTex.Height,
                        preset.IconZoom, preset.IconOffsetX, preset.IconOffsetY);
            }
            else if (preset.IconId.HasValue)
            {
                resolvedTex = GetGameIcon(preset.IconId.Value);
            }

            // Icon scaling combines hover (1.06x, eased via hoverAlpha) and
            // beat-kick (playing). Draw via drawlist whenever any scaling is
            // active; Dummy reserves layout space so widgets don't shift.
            float iconHoverScale = 1f + 0.06f * hoverAlpha;
            float iconBeatKick = isPlaying ? UIStyles.BeatKickScale(0.08f) : 1f;
            float iconScaleFactor = iconHoverScale * iconBeatKick;
            bool drawIconScaled = resolvedTex != null && (hoverAlpha > 0.02f || isPlaying);
            if (drawIconScaled)
            {
                var iconLeft2 = ImGui.GetCursorScreenPos();
                var drawSize = iconSize * iconScaleFactor;
                var off = (iconSize - drawSize) * 0.5f;
                var iconDl = ImGui.GetWindowDrawList();
                iconDl.AddImage(resolvedTex!.Handle,
                    new Vector2(iconLeft2.X + off, iconLeft2.Y + off),
                    new Vector2(iconLeft2.X + off + drawSize, iconLeft2.Y + off + drawSize),
                    iconUv0, iconUv1);
                ImGui.Dummy(new Vector2(iconSize, iconSize));
            }
            else if (resolvedTex != null)
            {
                ImGui.Image(resolvedTex.Handle, new Vector2(iconSize, iconSize), iconUv0, iconUv1);
            }
            else
            {
                DrawPlaceholderIcon(iconSize, scale);
            }

            ImGui.SameLine();
            // text block (~52px) centered on icon midline (~44px); small negative offset is intentional
            var textBlockH = ImGui.GetTextLineHeight() * 3f + 2f * scale * 2f;
            var textTopY = iconTopY + (iconSize - textBlockH) / 2f;
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, textTopY));

            // Compute the type label (emote/pose/bypass) - no longer a boxed chip.
            // Renders as loose text in the card's bottom-right corner.
            string chipText;
            Vector4 chipCol;
            {
                var defaultChipCol = new Vector4(0.55f, 0.58f, 0.63f, 1f);           // neutral info
                var poseChipCol   = new Vector4(0.70f, 0.63f, 0.85f, 1f);           // lavender
                var lockedChipCol = new Vector4(0.90f, 0.66f, 0.45f, 1f);           // amber

                chipText = preset.EmoteCommand;
                chipCol = defaultChipCol;
                if (preset.AnimationType == 6) chipText = "walk / run";
                else if (preset.AnimationType >= 2 && preset.AnimationType <= 5)
                {
                    var poseStr = preset.PoseIndex >= 0 ? $" {preset.PoseIndex}" : "";
                    chipText = preset.AnimationType switch
                    {
                        2 => $"/cpose{poseStr}",
                        3 => $"/sit{poseStr}",
                        4 => $"/groundsit{poseStr}",
                        5 => $"/doze{poseStr}",
                        _ => chipText,
                    };
                    chipCol = poseChipCol;
                }
                if (preset.EmoteLocked)
                {
                    chipText = chipText + " [bypass]";
                    chipCol = lockedChipCol;
                }
            }
            var chipTextSize = string.IsNullOrEmpty(chipText) ? Vector2.Zero : ImGui.CalcTextSize(chipText);
            var chipW = chipTextSize.X;

            // Right column reservation includes inline chip + Play + kebab + a 12px right inset
            // (mirrors HTML .card { padding-right: 12px }). chipW is 0 when no chip.
            var textStartX = ImGui.GetCursorPosX();
            var buttonWidth = 50f * scale;
            var menuWidth = 28f * scale;
            var btnSpacing = ImGui.GetStyle().ItemSpacing.X;
            var rightInset = 4f * scale;
            // Chip sits BELOW play+kebab and is right-aligned to the kebab's right edge.
            // The column reservation is fixed at play+kebab width so play/kebab sit at the
            // same X position on every card regardless of chip text length.
            var playKebabW = buttonWidth + btnSpacing + menuWidth;
            var rightGroupW = playKebabW;
            var buttonsStartX = cardContentWidth - rightGroupW - rightInset;
            var maxTextWidth = buttonsStartX - textStartX - 8 * scale;

            // 3 tight lines: name + star, /cmd, mod descriptor
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(ImGui.GetStyle().ItemSpacing.X, 2f * scale));
            ImGui.BeginGroup();

            // row 1: star + name (+ disabled) + name-EQ. Name capped at 30% card width.
            var nameMaxWidth = MathF.Min(maxTextWidth, cardContentWidth * 0.30f);
            if (isFavorite)
            {
                // Star magnetize: on hover the star brightens and gets a small
                // warm glow behind it. Both fade via hoverAlpha so the change
                // rides in with the card's general hover response.
                var starRest = new Vector4(1f, 0.82f, 0.30f, 1f);
                var starHot  = new Vector4(1f, 0.92f, 0.55f, 1f);
                if (hoverAlpha > 0.02f)
                {
                    var starPos = ImGui.GetCursorScreenPos();
                    var starSz = ImGui.CalcTextSize("*");
                    var starDl = ImGui.GetWindowDrawList();
                    var glowC = new Vector2(starPos.X + starSz.X * 0.5f,
                                            starPos.Y + starSz.Y * 0.5f);
                    starDl.AddCircleFilled(glowC, 7f * scale,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.30f, 0.22f * hoverAlpha)), 16);
                    starDl.AddCircleFilled(glowC, 4f * scale,
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.30f, 0.35f * hoverAlpha)), 16);
                }
                var starColor = new Vector4(
                    starRest.X + (starHot.X - starRest.X) * hoverAlpha,
                    starRest.Y + (starHot.Y - starRest.Y) * hoverAlpha,
                    starRest.Z + (starHot.Z - starRest.Z) * hoverAlpha,
                    1f);
                ImGui.TextColored(starColor, "*");
                ImGui.SameLine(0, 4 * scale);
                nameMaxWidth -= ImGui.CalcTextSize("*").X + 4 * scale;
            }
            if (!preset.Enabled)
                nameMaxWidth -= ImGui.CalcTextSize(" (disabled)").X + ImGui.GetStyle().ItemSpacing.X;
            if (isPlaying)
                nameMaxWidth -= UIStyles.NameEqWidth(scale) + 6f * scale;

            // Capture row-1 top-Y before the name renders - needed for the
            // EQ's vertical position. We use a drawlist-only EQ after the row
            // renders (no SameLine/NewLine gymnastics -> no alignment drift).
            var nameRowStartY = ImGui.GetCursorScreenPos().Y;

            // Name - marquee when overflowing AND the card is either hovered
            // or currently playing. Otherwise truncate with "..." + tooltip.
            UIStyles.DrawMarqueeText(preset.Name, nameMaxWidth,
                preset.Enabled ? MW_Text : new Vector4(0.55f, 0.56f, 0.60f, 1f),
                disabled: !preset.Enabled,
                animate: isHovered || isPlaying);
            // Accent underline on hover - hooked to the name item's actual
            // rendered rect so it spans only the visible text (not the full
            // budget width). Faded via hoverAlpha.
            if (hoverAlpha > 0.02f && preset.Enabled)
            {
                var nameMin = ImGui.GetItemRectMin();
                var nameMax2 = ImGui.GetItemRectMax();
                var underlineY = nameMax2.Y + 1f * scale;
                UIStyles.DrawNameUnderline(ImGui.GetWindowDrawList(),
                    nameMin.X, nameMax2.X, underlineY, accentColor, scale, hoverAlpha);
            }
            // (disabled) tag on the same row if applicable.
            if (!preset.Enabled)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(disabled)");
            }
            // Mini-EQ, anchored just after the last row-1 item. Drawlist only
            // so the name row's item heights remain the authority for layout.
            if (isPlaying)
            {
                var lastItemMax = ImGui.GetItemRectMax();
                var textH = ImGui.GetTextLineHeight();
                var eqH = textH * 0.72f;
                var eqPos = new Vector2(
                    lastItemMax.X + 6f * scale,
                    nameRowStartY + (textH - eqH) * 0.5f);
                UIStyles.DrawNameEq(eqPos, accentColor, eqH, scale);
            }

            // Row 2: /cmd - accent blue mono (or italic faint "no command" placeholder)
            if (!string.IsNullOrEmpty(preset.ChatCommand))
            {
                ImGui.TextColored(new Vector4(0.49f, 0.65f, 0.85f, 1f), $"/{preset.ChatCommand}");
            }
            else
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.40f, 0.41f, 0.45f, 1f));
                ImGui.Text("- no command");
                ImGui.PopStyleColor();
            }

            // Row 3: mod descriptor - marquee only while hovered or playing.
            var modText = string.IsNullOrEmpty(preset.ModName) ? "no mod selected" : preset.ModName;
            UIStyles.DrawMarqueeText(modText, maxTextWidth,
                new Vector4(0.40f, 0.41f, 0.45f, 1f),
                animate: isHovered || isPlaying);

            ImGui.EndGroup();
            ImGui.PopStyleVar();

            // Action row - single horizontal run: [chip] [PLAY] [...]. Matches HTML .card-actions.
            // All items vertically centered in the card against the icon row.
            var buttonsX = buttonsStartX;
            ImGui.SameLine(buttonsX);

            // Vertically center play+kebab in the card, then nudge the pair up
            // a bit so the bottom-right corner tag has clean space under them.
            var playH = ImGui.GetFrameHeight();
            var cardTopScreenY = ImGui.GetWindowPos().Y;
            var actionRowY = cardTopScreenY + (cardHeight - playH) / 2f - 16f * scale;
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, actionRowY));

            // Capture where the play-row should start in screen coords.
            var playRowStartPos = ImGui.GetCursorScreenPos();
            var menuH = ImGui.GetFrameHeight();

            // PLAY/STOP: hold the clicked state for ~900ms so kick/sheen/burst finish before swap
            float nowT = (float)ImGui.GetTime();
            bool playActivating = playActivationStart.TryGetValue(preset.Id, out var _playStart)
                                  && nowT - _playStart < 0.9f;
            bool stopActivating = stopActivationStart.TryGetValue(preset.Id, out var _stopStart)
                                  && nowT - _stopStart < 0.9f;
            bool showAsPlaying;
            if (playActivating)      showAsPlaying = false;       // hold PLAY
            else if (stopActivating) showAsPlaying = true;        // hold STOP
            else                     showAsPlaying = isPlaying;
            ImGui.SetCursorScreenPos(playRowStartPos);
            if (showAsPlaying)
            {
                // Custom drawlist-rendered STOP - coral transparent with
                // breathing border + pulse dot (always-on), plus kick + sheen
                // + click-burst matching PLAY's reactivity.
                float stopKick = 1f;
                if (stopActivationStart.TryGetValue(preset.Id, out var sKickStart))
                    stopKick = UIStyles.PlayKickScale(nowT - sKickStart);
                if (UIStyles.DrawStopButton($"##stop_{preset.Id}",
                        new Vector2(buttonWidth, playH), stopKick, scale))
                {
                    Plugin.Instance?.StopActivePreset();
                    stopActivationStart[preset.Id] = nowT;
                    // Clear any stale play activation so there's no conflict.
                    playActivationStart.Remove(preset.Id);
                }
                var stopBtnMin = ImGui.GetItemRectMin();
                var stopBtnMax = ImGui.GetItemRectMax();
                if (stopActivationStart.TryGetValue(preset.Id, out var stopBurstStart))
                {
                    float el = nowT - stopBurstStart;
                    if (el > 0.7f)
                        stopActivationStart.Remove(preset.Id);
                    else
                    {
                        var bc = new Vector2((stopBtnMin.X + stopBtnMax.X) * 0.5f,
                                             (stopBtnMin.Y + stopBtnMax.Y) * 0.5f);
                        UIStyles.DrawPlayActivation(ImGui.GetWindowDrawList(),
                            bc, el, scale, new Vector4(0.83f, 0.53f, 0.47f, 1f));
                    }
                }
                if (ImGui.IsItemHovered())
                    UIStyles.EncoreTooltip("Stop the dance (jump). Mods stay until you hit reset.");
            }
            else
            {
                // Custom drawlist-rendered PLAY - enables the compress-bounce
                // kick (0.84 -> 1.18 -> 1.0 over 900ms) and a looping sheen
                // sweep on hover, neither of which ImGui.Button can do.
                float kick = 1f;
                if (playActivationStart.TryGetValue(preset.Id, out var kickStart))
                    kick = UIStyles.PlayKickScale(nowT - kickStart);
                if (UIStyles.DrawPlayButton($"##play_{preset.Id}",
                        new Vector2(buttonWidth, playH), kick, scale))
                {
                    Plugin.Instance?.ExecutePreset(preset);
                    playActivationStart[preset.Id] = nowT;
                    // Clear any stale stop activation so there's no conflict.
                    stopActivationStart.Remove(preset.Id);
                }
                var playBtnMin = ImGui.GetItemRectMin();
                var playBtnMax = ImGui.GetItemRectMax();
                // Always-on soft green bloom around the resting PLAY.
                UIStyles.DrawPlayButtonBloom(ImGui.GetWindowDrawList(),
                    playBtnMin, playBtnMax, scale);
                // Click burst - ring + 8 sparks for 700ms post-click.
                if (playActivationStart.TryGetValue(preset.Id, out var playStart))
                {
                    float elapsed = (float)ImGui.GetTime() - playStart;
                    if (elapsed > 0.7f)
                        playActivationStart.Remove(preset.Id);
                    else
                    {
                        var btnCenter = new Vector2(
                            (playBtnMin.X + playBtnMax.X) * 0.5f,
                            (playBtnMin.Y + playBtnMax.Y) * 0.5f);
                        UIStyles.DrawPlayActivation(ImGui.GetWindowDrawList(),
                            btnCenter, elapsed, scale);
                    }
                }
                if (ImGui.IsItemHovered())
                {
                    UIStyles.EncoreTooltip("Tip: Stop dancing before switching between mods that share the same base emote.");
                }
            }

            //  Kebab (positioned explicitly to the right of Play, same Y) 
            var kebabX = playRowStartPos.X + buttonWidth + btnSpacing;
            ImGui.SetCursorScreenPos(new Vector2(kebabX, playRowStartPos.Y));
            ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0.16f, 0.18f, 0.22f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.22f, 0.25f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(0.26f, 0.29f, 0.35f, 1f));
            ImGui.PushStyleColor(ImGuiCol.Border,        new Vector4(0.28f, 0.30f, 0.36f, 1f));
            ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
            if (ImGui.Button("...", new Vector2(menuWidth, menuH)))
            {
                ImGui.OpenPopup($"presetMenu_{index}");
            }
            UIStyles.DrawHoverSheenOnLastItem(ImGui.GetWindowDrawList(),
                ImGui.IsItemHovered(), 0.22f);
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(4);

            // type label: command tinted by type (lavender pose, amber [bypass], neutral emote) at 82% scale
            if (chipW > 0)
            {
                var chipDl = ImGui.GetWindowDrawList();
                var cardWinPos2 = ImGui.GetWindowPos();
                var cardWinSize = ImGui.GetWindowSize();
                // Native font size (no SetWindowFontScale) so the text stays
                // crisp - GPU downscaling via font scale was reading blurry.
                var smallSz = ImGui.CalcTextSize(chipText);
                var rightInsetPx = 6f * scale;
                var bottomInsetPx = 4f * scale;
                var chipX = cardWinPos2.X + cardWinSize.X - smallSz.X - rightInsetPx;
                var chipY = cardWinPos2.Y + cardWinSize.Y - smallSz.Y - bottomInsetPx;
                chipDl.AddText(new Vector2(chipX, chipY),
                    ImGui.ColorConvertFloat4ToU32(chipCol), chipText);
            }

            // Context menu - Encore-themed: slate surface, sharp corners, accent hover.
            bool deleted = false;
            PushEncoreMenuStyle();
            if (ImGui.BeginPopup($"presetMenu_{index}"))
            {
                // Modifier play entries
                if (preset.Modifiers.Count > 0)
                {
                    foreach (var modifier in preset.Modifiers)
                    {
                        if (ImGui.MenuItem($"Play: {modifier.Name}"))
                            Plugin.Instance?.ExecutePreset(preset, modifier);
                    }
                    ImGui.Separator();
                }

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

                // Move to Folder submenu (hierarchical)
                var config = Plugin.Instance?.Configuration;
                if (config != null && config.Folders.Count > 0 && ImGui.BeginMenu("Move to Folder"))
                {
                    if (ImGui.MenuItem("(None)", "", preset.FolderId == null))
                    {
                        preset.FolderId = null;
                        config.Save();
                    }
                    DrawPresetMoveToFolderMenu(null, preset, config);
                    ImGui.EndMenu();
                }

                ImGui.Separator();

                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.93f, 0.48f, 0.48f, 1f));
                if (ImGui.MenuItem("Delete"))
                {
                    var io = ImGui.GetIO();
                    if (io.KeyCtrl && io.KeyShift)
                    {
                        Plugin.Instance!.Configuration.Presets.RemoveAt(index);
                        Plugin.Instance.UpdatePresetCommands();
                        Plugin.Instance.Configuration.Save();
                        selectedPresetIndex = -1;
                        deleted = true;
                    }
                }
                ImGui.PopStyleColor();
                if (ImGui.IsItemHovered())
                    MW_SetTooltip("Hold Ctrl+Shift and click to delete");

                ImGui.EndPopup();
            }
            PopEncoreMenuStyle();

            if (deleted)
            {
                ImGui.EndChild();
                ImGui.PopStyleVar();   // WindowPadding
                ImGui.PopStyleColor(); // ChildBg
                ImGui.PopID();
                return true;
            }

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

            // (The base-emote corner text that used to live here moved into the type chip next to Play.)
            ImGui.Unindent(14f * UIStyles.Scale);

            // Playing-card overlay flair - rotating rainbow border. Drawn
            // above content so it's visible, still inside the child clip so
            // nothing escapes.
            if (isPlaying)
            {
                var flairDl = ImGui.GetWindowDrawList();
                var wp = ImGui.GetWindowPos();
                var ws = ImGui.GetWindowSize();
                var cMin = wp;
                var cMax = new Vector2(wp.X + ws.X, wp.Y + ws.Y);
                UIStyles.DrawConicRainbowRing(flairDl, cMin, cMax, scale);
            }

            // Click ripple - expanding material circle from the exact click
            // point. Auto-expires from the dict after its 600ms duration.
            if (cardRippleStart.TryGetValue(preset.Id, out var ripple))
            {
                float elapsed = (float)ImGui.GetTime() - ripple.start;
                if (elapsed > 1.1f)
                    cardRippleStart.Remove(preset.Id);
                else
                    UIStyles.DrawCardRipple(ImGui.GetWindowDrawList(),
                        ripple.pos, elapsed, accentColor, scale);
            }

            // Sibling dim - translucent black overlay over all content. Alpha
            // is eased via dimAlpha so hover/blur fades in/out instead of
            // snapping. Drawn last so it covers icon/text/chip.
            if (dimAlpha > 0.02f)
            {
                var dimDl = ImGui.GetWindowDrawList();
                var dimMin = ImGui.GetWindowPos();
                var dimSize = ImGui.GetWindowSize();
                dimDl.AddRectFilled(dimMin,
                    new Vector2(dimMin.X + dimSize.X, dimMin.Y + dimSize.Y),
                    ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, dimAlpha)));
            }

        }
        ImGui.EndChild();
        ImGui.PopStyleVar(); // WindowPadding
        // (Accent bar + border now drawn inside the child - see top of BeginChild block.)

        // Drop target on the card (Custom sort only) - reorder preset position
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
        return false;
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

    private static void DrawTruncatedText(string text, float maxWidth, bool disabled = false)
    {
        var fullSize = ImGui.CalcTextSize(text);
        if (fullSize.X <= maxWidth || maxWidth <= 0)
        {
            if (disabled)
                ImGui.TextDisabled(text);
            else
                ImGui.Text(text);
            return;
        }

        var ellipsis = "...";
        var ellipsisWidth = ImGui.CalcTextSize(ellipsis).X;
        var truncated = text;
        for (var i = text.Length - 1; i > 0; i--)
        {
            truncated = text[..i];
            if (ImGui.CalcTextSize(truncated).X + ellipsisWidth <= maxWidth)
            {
                truncated += ellipsis;
                break;
            }
        }

        if (disabled)
            ImGui.TextDisabled(truncated);
        else
            ImGui.Text(truncated);

        if (ImGui.IsItemHovered())
            MW_SetTooltip(text);
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

    // 
    //   FOOTER  -  Penumbra status - utility icons - primary CTA
    // 
    private void DrawFooter()
    {
        // Modest shrink - keep status + CTA readable while still reading
        // smaller than Dalamud's bulky 18px default.
        ImGui.SetWindowFontScale(0.90f);
        try { DrawFooterInner(); }
        finally { ImGui.SetWindowFontScale(1.0f); }
    }

    private void DrawFooterInner()
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();
        var winPos = ImGui.GetWindowPos();
        var winW = ImGui.GetWindowWidth();
        float footerH = 48f * scale;

        var start = new Vector2(winPos.X, ImGui.GetCursorScreenPos().Y);
        var end = new Vector2(start.X + winW, start.Y + footerH);

        // Background + top border.
        dl.AddRectFilled(start, end, MW_Col(new Vector4(0.047f, 0.055f, 0.075f, 1f)));
        dl.AddLine(start, new Vector2(end.X, start.Y), MW_ColA(MW_Border, 1f), 1f);
        // Soft accent glow just below the top border - gives the footer
        // lift, like light bleeding off the underside of the top hairline.
        {
            uint glowTop = MW_ColA(MW_Accent, 0.10f);
            uint glowBot = MW_ColA(MW_Accent, 0f);
            float glowInset = winW * 0.18f;
            dl.AddRectFilledMultiColor(
                new Vector2(start.X + glowInset, start.Y + 1f),
                new Vector2(end.X - glowInset, start.Y + 6f * scale),
                glowTop, glowTop, glowBot, glowBot);
        }
        // Scan streak 2px above the top border.
        UIStyles.DrawActionbarScan(start.Y - 2f * scale, start.X, end.X, MW_Accent);

        float edgePad = 14f * scale;
        float contentY = start.Y + footerH * 0.5f;
        float textH = ImGui.GetTextLineHeight();
        float textY = contentY - textH * 0.5f;

        //  LEFT: Primary CTA - "+ NEW PRESET" / "+ NEW ROUTINE" 
        // Penumbra status moved to the ribbon; left footer now anchors the
        // main creation action.
        var ctaAccent = currentTab == MainTab.Routines ? MW_MacroGreen : MW_Accent;
        var ctaAccentDeep = new Vector4(ctaAccent.X * 0.80f, ctaAccent.Y * 0.80f, ctaAccent.Z * 0.85f, 1f);
        var ctaAccentBright = new Vector4(
            MathF.Min(1f, ctaAccent.X * 1.20f + 0.06f),
            MathF.Min(1f, ctaAccent.Y * 1.20f + 0.06f),
            MathF.Min(1f, ctaAccent.Z * 1.20f + 0.06f), 1f);
        var ctaTextCol = new Vector4(0.05f, 0.08f, 0.13f, 1f);

        string ctaLabel = currentTab == MainTab.Routines ? "+ NEW ROUTINE" : "+ NEW PRESET";
        float ctaLabelW = ImGui.CalcTextSize(ctaLabel).X;
        float ctaBtnW = ctaLabelW + 36f * scale;
        float ctaBtnH = 28f * scale;
        // Bottom-left anchor, nudged a hair past the left corner bracket
        // (~22px reserved) so the bloom halo has room.
        float ctaLeftMargin = 22f * scale;
        float ctaX = start.X + ctaLeftMargin;
        float ctaY = contentY - ctaBtnH * 0.5f;
        var ctaMin = new Vector2(ctaX, ctaY);
        var ctaMax = new Vector2(ctaX + ctaBtnW, ctaY + ctaBtnH);
        UIStyles.DrawPlayButtonBloom(dl, ctaMin, ctaMax, scale, ctaAccent);

        ImGui.SetCursorScreenPos(ctaMin);
        float ctaKick = 1f;
        if (footerCtaClickTime >= 0)
            ctaKick = UIStyles.PlayKickScale((float)(ImGui.GetTime() - footerCtaClickTime));
        if (UIStyles.DrawPlayButton("##mainCTA",
                new Vector2(ctaBtnW, ctaBtnH), ctaKick, scale,
                label: ctaLabel,
                restCol:  ctaAccent,
                hoverCol: ctaAccentBright,
                heldCol:  ctaAccentDeep,
                borderCol: ctaAccentDeep,
                textColor: ctaTextCol))
        {
            footerCtaClickTime = ImGui.GetTime();
            if (currentTab == MainTab.Routines) routineEditorWindow?.OpenNew();
            else editorWindow?.OpenNew();
        }
        float ctaRightEdge = ctaX + ctaBtnW;

        //  CENTER: Utility icons (pin - folder+ - sep - align - reset - sep - help - gift - gear) 
        // Matches HTML: 30px btns, transparent rest + accent tint on hover.
        var iconBtnSize = new Vector2(28 * scale, 28 * scale);
        var iconBg = new Vector4(0f, 0f, 0f, 0f);
        var iconHover = new Vector4(0.105f, 0.113f, 0.145f, 1f);
        var iconActive = new Vector4(0.15f, 0.17f, 0.21f, 1f);
        // Icon rest/hover colors - matches HTML `.util-btn` and `.util-btn:hover`.
        var iconRestCol = MW_TextDim;
        var iconHoverCol = MW_Accent;
        float iconGap = 3f * scale;
        float sepGap = 7f * scale;
        float sepW = 1f;
        float sepH = 18f * scale;

        var pinnedCount = Plugin.Instance?.Configuration.PinnedModDirectories.Count ?? 0;
        var hasPinned = pinnedCount > 0;
        var alignState = Plugin.Instance?.GetAlignState() ?? (false, "", 0f, false, CharacterModes.Normal, false);
        var isWalking = alignState.isWalking;
        var alignBlocked = alignState.hasTarget && alignState.mode != CharacterModes.Normal && !isWalking;
        Vector4 alignIconCol = MW_TextDim;
        if (isWalking)
        {
            var pulse = (float)(Math.Sin(ImGui.GetTime() * 4.0) * 0.3 + 0.7);
            alignIconCol = new Vector4(MW_Cyan.X * pulse + 0.1f, MW_Cyan.Y * pulse + 0.1f, MW_Cyan.Z * pulse + 0.1f, 1f);
        }
        else if (alignBlocked) alignIconCol = MW_Gold;
        else if (alignState.inRange) alignIconCol = MW_Success;
        else if (alignState.hasTarget) alignIconCol = MW_Danger;

        var hasStoredState = Plugin.Instance?.Configuration.ModsWithTempSettings.Count > 0;

        // flush-right cluster, 22px before the corner bracket
        float groupPinFolderW = iconBtnSize.X * 2 + iconGap * 1;
        float groupActionW = iconBtnSize.X * 3 + iconGap * 2;
        float groupTrayW = iconBtnSize.X * 3 + iconGap * 2;
        float iconClusterW =
            groupPinFolderW + sepGap * 2 + sepW +
            groupActionW + sepGap * 2 + sepW +
            groupTrayW;

        // Right-anchor the cluster, but clamp so it never overlaps the CTA.
        float iconRightMargin = 22f * scale;
        float clusterRight = end.X - iconRightMargin;
        float clusterLeft = clusterRight - iconClusterW;
        float minClusterLeft = ctaRightEdge + 18f * scale;
        if (clusterLeft < minClusterLeft)
        {
            // Fallback: if the window is too narrow, push the cluster into
            // whatever fits and let the CTA's bloom + the cluster share edge
            // padding.
            clusterLeft = minClusterLeft;
        }
        float iconY = contentY - iconBtnSize.Y * 0.5f;

        float ix = clusterLeft;

        // Pin button.
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        bool pinHoverPre = MW_IsHoveringButton(ix, iconY, iconBtnSize);
        bool pinClicked = UIStyles.IconButton(FontAwesomeIcon.Thumbtack, iconBtnSize,
            tooltip: null,
            iconColor: hasPinned ? MW_Gold : (pinHoverPre ? iconHoverCol : iconRestCol),
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive);
        if (ImGui.IsItemHovered())
        {
            MW_PushTooltipStyle();
            ImGui.BeginTooltip();
            ImGui.Text(hasPinned ? $"Pinned Mods ({pinnedCount})" : "Pinned Mods");
            ImGui.TextColored(MW_TextDim,
                "Pinned mods stay enabled during preset switches.");
            ImGui.EndTooltip();
            MW_PopTooltipStyle();
        }
        if (pinClicked) ImGui.OpenPopup("Pinned Mods###pinnedMods");
        ix += iconBtnSize.X + iconGap;

        // New folder button.
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        bool newFolderHoverPre = MW_IsHoveringButton(ix, iconY, iconBtnSize);
        if (UIStyles.IconButton(FontAwesomeIcon.FolderPlus, iconBtnSize,
            tooltip: null,
            iconColor: newFolderHoverPre ? iconHoverCol : iconRestCol,
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive))
        {
            showNewFolderDialog = true;
            newFolderName = "New Folder";
            newFolderColor = DefaultFolderColor;
            newFolderParentId = null;
            ImGui.OpenPopup("New Folder###newFolderDialog");
        }
        if (ImGui.IsItemHovered()) MW_SetTooltip("New Folder");
        DrawNewFolderDialog();
        ix += iconBtnSize.X + iconGap;

        // Separator.
        ix += sepGap;
        dl.AddLine(new Vector2(ix, contentY - sepH * 0.5f),
                   new Vector2(ix, contentY + sepH * 0.5f),
                   MW_ColA(MW_Border, 0.5f), 1f);
        ix += sepW + sepGap;

        // Align button.
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        bool alignClicked = UIStyles.IconButton(FontAwesomeIcon.LocationCrosshairs, iconBtnSize,
            tooltip: null,
            iconColor: alignIconCol,
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive);
        if (ImGui.IsItemHovered())
        {
            MW_PushTooltipStyle();
            ImGui.BeginTooltip();
            if (isWalking)
            {
                ImGui.TextColored(MW_Cyan, "Walking to target...");
                ImGui.TextColored(MW_TextDim, "Click to cancel.");
            }
            else if (!alignState.hasTarget)
            {
                ImGui.Text("Align to Target");
                ImGui.TextColored(MW_TextDim, "Select a target first.");
            }
            else if (alignBlocked)
            {
                var blockedMsg = alignState.mode switch
                {
                    CharacterModes.Mounted => "Dismount first.",
                    CharacterModes.EmoteLoop => "Stop your emote first.",
                    CharacterModes.InPositionLoop => "Stand up first.",
                    CharacterModes.Performance => "Stop performing first.",
                    _ => "Stop what you're doing first.",
                };
                ImGui.TextColored(MW_Gold, blockedMsg);
                ImGui.Text($"Target: {alignState.targetName}");
            }
            else if (alignState.inRange)
            {
                ImGui.TextColored(MW_Success, "Ready to align!");
                ImGui.Text($"Target: {alignState.targetName}");
            }
            else
            {
                ImGui.TextColored(MW_Danger, "Move closer to your target.");
                ImGui.Text($"Target: {alignState.targetName}");
            }
            ImGui.EndTooltip();
            MW_PopTooltipStyle();
        }
        if (alignClicked)
        {
            if (isWalking) Plugin.Instance?.MovementService?.Cancel();
            else Plugin.Framework.RunOnFrameworkThread(() => Plugin.Instance?.AlignToTarget());
        }
        ix += iconBtnSize.X + iconGap;

        // Random preset button.
        var presetCount = Plugin.Instance?.Configuration.Presets.Count ?? 0;
        var hasPresets = presetCount > 0;
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        if (!hasPresets) ImGui.BeginDisabled();
        bool randomHoverPre = MW_IsHoveringButton(ix, iconY, iconBtnSize);
        bool randomClicked = UIStyles.IconButton(FontAwesomeIcon.Random, iconBtnSize,
            tooltip: null,
            iconColor: randomHoverPre && hasPresets ? iconHoverCol : iconRestCol,
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive);
        if (!hasPresets) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            MW_PushTooltipStyle();
            ImGui.BeginTooltip();
            if (hasPresets)
            {
                ImGui.Text("Play Random Preset");
                ImGui.TextColored(MW_TextDim, "Picks one preset at random and plays it.");
            }
            else
            {
                ImGui.Text("No presets to pick from");
            }
            ImGui.EndTooltip();
            MW_PopTooltipStyle();
        }
        if (randomClicked && hasPresets)
            Plugin.Instance?.ExecuteRandomFromFolder(null);
        ix += iconBtnSize.X + iconGap;

        // Reset button (Undo icon). Disabled when no temp state to restore.
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        if (!hasStoredState) ImGui.BeginDisabled();
        bool resetHoverPre = MW_IsHoveringButton(ix, iconY, iconBtnSize);
        bool resetClicked = UIStyles.IconButton(FontAwesomeIcon.Undo, iconBtnSize,
            tooltip: null,
            iconColor: resetHoverPre && hasStoredState ? iconHoverCol : iconRestCol,
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive);
        if (!hasStoredState) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            MW_PushTooltipStyle();
            ImGui.BeginTooltip();
            if (hasStoredState)
            {
                ImGui.Text("Reset Priorities");
                ImGui.TextDisabled("Ctrl+Shift + Click to restore mods.");
            }
            else
            {
                ImGui.Text("No changes to restore");
            }
            ImGui.EndTooltip();
            MW_PopTooltipStyle();
        }
        if (resetClicked && ImGui.GetIO().KeyCtrl && ImGui.GetIO().KeyShift)
            Plugin.Instance?.ResetAllPriorities();
        ix += iconBtnSize.X + iconGap;

        // Separator.
        ix += sepGap;
        dl.AddLine(new Vector2(ix, contentY - sepH * 0.5f),
                   new Vector2(ix, contentY + sepH * 0.5f),
                   MW_ColA(MW_Border, 0.5f), 1f);
        ix += sepW + sepGap;

        // Help.
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        bool helpHoverPre = MW_IsHoveringButton(ix, iconY, iconBtnSize);
        if (UIStyles.IconButton(FontAwesomeIcon.QuestionCircle, iconBtnSize,
            tooltip: null,
            iconColor: helpHoverPre ? iconHoverCol : iconRestCol,
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive))
        {
            helpWindow?.Toggle();
        }
        if (ImGui.IsItemHovered()) MW_SetTooltip("Playbook (Guide)");
        ix += iconBtnSize.X + iconGap;

        // What's new (Gift icon).
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        bool giftHoverPre = MW_IsHoveringButton(ix, iconY, iconBtnSize);
        if (UIStyles.IconButton(FontAwesomeIcon.Gift, iconBtnSize,
            tooltip: null,
            iconColor: giftHoverPre ? iconHoverCol : iconRestCol,
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive))
        {
            if (patchNotesWindow != null) patchNotesWindow.IsOpen = true;
        }
        if (ImGui.IsItemHovered()) MW_SetTooltip("Patch Notes (What's New)");
        ix += iconBtnSize.X + iconGap;

        // Settings (Cog).
        ImGui.SetCursorScreenPos(new Vector2(ix, iconY));
        bool cogHoverPre = MW_IsHoveringButton(ix, iconY, iconBtnSize);
        if (UIStyles.IconButton(FontAwesomeIcon.Cog, iconBtnSize,
            tooltip: null,
            iconColor: cogHoverPre ? iconHoverCol : iconRestCol,
            bgOverride: iconBg, hoverOverride: iconHover, activeOverride: iconActive))
        {
            if (Plugin.Instance?.SettingsWindow != null)
                Plugin.Instance.SettingsWindow.IsOpen = !Plugin.Instance.SettingsWindow.IsOpen;
        }
        if (ImGui.IsItemHovered()) MW_SetTooltip("Board (Settings)");

        // Advance layout cursor by exactly the footer height.
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(1, footerH));

        // Popup draws.
        DrawPinnedModsPopup();
    }

    // 
    //   EMPTY STATE  -  dashed drop-zone card + radiating rings + twin CTAs
    // 
    private void DrawEmptyState(string heading, string body,
        string primaryCta, Action primaryAction,
        string secondaryCta, Action secondaryAction)
    {
        var scale = UIStyles.Scale;
        var dl = ImGui.GetWindowDrawList();

        // Reserve a vertical region inside the content child, then use screen
        // coordinates for the dashed card so draw order is stable.
        var availW = ImGui.GetContentRegionAvail().X;
        var availH = ImGui.GetContentRegionAvail().Y;

        // Dashed card dimensions - capped so it reads as a card, not a panel.
        float cardW = MathF.Min(availW - 40f * scale, 560f * scale);
        float cardH = 360f * scale;
        float cardX = ImGui.GetCursorScreenPos().X + (availW - cardW) * 0.5f;
        float cardY = ImGui.GetCursorScreenPos().Y + MathF.Max(30f * scale, (availH - cardH) * 0.5f);
        var cardMin = new Vector2(cardX, cardY);
        var cardMax = new Vector2(cardX + cardW, cardY + cardH);

        // 2px dashed border - family pattern.
        uint dashCol = MW_ColA(MW_Border, 1f);
        MW_DrawDashedRect(dl, cardMin, cardMax, dashCol,
            dashLen: 6f * scale, gapLen: 4f * scale, thickness: 1f);

        //  Radiating rings behind the glyph (3s full cycle, staggered by 1.5s) 
        float centerX = cardX + cardW * 0.5f;
        float glyphCy = cardY + 60f * scale;
        float t = (float)ImGui.GetTime();
        for (int ring = 0; ring < 2; ring++)
        {
            float phase = ((t + ring * 1.5f) % 3f) / 3f;
            float r = (0.7f + 0.6f * phase) * 42f * scale;
            float a = 0.40f * (1f - phase);
            if (a < 0.02f) continue;
            dl.AddCircle(new Vector2(centerX, glyphCy), r,
                MW_ColA(MW_TextGhostEmpty, a), 40, 1f);
        }

        //  Glyph - FontAwesome Crosshairs in TitleFont (30px Unbounded-Bold) if available 
        string glyphChar = FontAwesomeIcon.LocationCrosshairs.ToIconString();
        var titleFont = Plugin.Instance?.TitleFont;
        ImGui.PushFont(UiBuilder.IconFont);
        var glyphSz = ImGui.CalcTextSize(glyphChar);
        // Scale the icon glyph up manually - there's no built-in hook to render FA at 30pt.
        float glyphScale = 2.2f * scale;
        float glyphW = glyphSz.X * glyphScale;
        float glyphH = glyphSz.Y * glyphScale;
        var glyphPos = new Vector2(centerX - glyphW * 0.5f, glyphCy - glyphH * 0.5f);
        dl.AddText(ImGui.GetFont(), ImGui.GetFontSize() * glyphScale,
            glyphPos, MW_Col(MW_TextGhostEmpty), glyphChar);
        ImGui.PopFont();

        //  Heading - tracked caps 
        float headTrack = 3.0f * scale;
        float wHead = UIStyles.MeasureTrackedWidth(heading, headTrack);
        float headY = cardY + 140f * scale;
        UIStyles.DrawTrackedText(dl,
            new Vector2(centerX - wHead * 0.5f, headY),
            heading, MW_Col(MW_TextFaint), headTrack);

        //  Body paragraph - wrapped, TextDim, centered block 
        float bodyY = headY + ImGui.GetTextLineHeight() + 14f * scale;
        float bodyMaxW = 400f * scale;
        float bodyX = centerX - bodyMaxW * 0.5f;
        // Render via ImGui.TextWrapped in a properly positioned cursor.
        ImGui.SetCursorScreenPos(new Vector2(bodyX, bodyY));
        ImGui.PushTextWrapPos(bodyX + bodyMaxW);
        ImGui.PushStyleColor(ImGuiCol.Text, MW_TextDim);
        // Force text alignment left with manual centering via wrap column.
        ImGui.TextWrapped(body);
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();

        //  Twin CTAs (primary + secondary) centered at the card bottom 
        float ctaH = 30f * scale;
        float primaryW = 150f * scale;
        float secondaryW = 150f * scale;
        float ctaGap = 12f * scale;
        float ctaTotalW = primaryW + ctaGap + secondaryW;
        float ctaY = cardY + cardH - ctaH - 30f * scale;
        float ctaX = centerX - ctaTotalW * 0.5f;

        // Primary - filled accent, DrawPlayButton treatment.
        var primMin = new Vector2(ctaX, ctaY);
        var primMax = new Vector2(ctaX + primaryW, ctaY + ctaH);
        UIStyles.DrawPlayButtonBloom(dl, primMin, primMax, scale, MW_Accent);
        ImGui.SetCursorScreenPos(primMin);
        if (UIStyles.DrawPlayButton("##emptyPrimary",
                new Vector2(primaryW, ctaH), 1f, scale,
                label: primaryCta,
                restCol:  MW_Accent,
                hoverCol: MW_AccentBright,
                heldCol:  new Vector4(MW_Accent.X * 0.8f, MW_Accent.Y * 0.8f, MW_Accent.Z * 0.85f, 1f),
                borderCol: new Vector4(MW_Accent.X * 0.8f, MW_Accent.Y * 0.8f, MW_Accent.Z * 0.85f, 1f),
                textColor: new Vector4(0.05f, 0.08f, 0.13f, 1f)))
        {
            primaryAction?.Invoke();
        }

        // Secondary - outlined accent, transparent fill.
        float secX = ctaX + primaryW + ctaGap;
        var secMin = new Vector2(secX, ctaY);
        var secMax = new Vector2(secX + secondaryW, ctaY + ctaH);
        bool secHovered = ImGui.IsMouseHoveringRect(secMin, secMax);
        ImGui.SetCursorScreenPos(secMin);
        ImGui.PushStyleColor(ImGuiCol.Button,        new Vector4(0f, 0f, 0f, 0f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(MW_Accent.X, MW_Accent.Y, MW_Accent.Z, 0.10f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive,  new Vector4(MW_Accent.X, MW_Accent.Y, MW_Accent.Z, 0.18f));
        ImGui.PushStyleColor(ImGuiCol.Border,        MW_Accent);
        ImGui.PushStyleColor(ImGuiCol.Text,          MW_Accent);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f);
        if (ImGui.Button(secondaryCta, new Vector2(secondaryW, ctaH)))
        {
            secondaryAction?.Invoke();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(5);
        if (secHovered)
        {
            // Glow halo on hover - matches mockup.
            for (int g = 2; g >= 1; g--)
            {
                float pad = g * 1.5f * scale;
                float a = 0.14f / g;
                dl.AddRect(
                    new Vector2(secMin.X - pad, secMin.Y - pad),
                    new Vector2(secMax.X + pad, secMax.Y + pad),
                    MW_ColA(MW_Accent, a), 0f, 0, 1f);
            }
        }

        // Advance the cursor so subsequent content (none, usually) doesn't overlap.
        ImGui.SetCursorScreenPos(new Vector2(cardX, cardY + cardH + 10f * scale));
        ImGui.Dummy(new Vector2(1, 1));
    }

    // Dashed-rectangle helper - walks each edge in dash/gap segments. Same
    // pattern as the editor windows.
    private static void MW_DrawDashedRect(Dalamud.Bindings.ImGui.ImDrawListPtr dl,
        Vector2 min, Vector2 max, uint col,
        float dashLen, float gapLen, float thickness)
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

    // Matches CSS `--text-ghost: #3c4150` from the mockup - used by the empty-
    // state glyph + rings. Slightly darker than MW_TextFaint for a quieter read.
    private static readonly Vector4 MW_TextGhostEmpty = new(0.235f, 0.255f, 0.314f, 1f);

    private string pinnedModsFilter = "";

    private void DrawPinnedModsPopup()
    {
        var scale = UIStyles.Scale;
        var popupSize = new Vector2(420 * scale, 380 * scale);

        ImGui.SetNextWindowSize(popupSize, ImGuiCond.Always);
        // Inherit the Encore menu surface (slate bg, sharp corners, hairline border) but
        // override WindowPadding for a roomier popup than the tight-packed kebab menus.
        PushEncoreMenuStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f * scale, 14f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(6f * scale, 6f * scale));

        if (ImGui.BeginPopup("Pinned Mods###pinnedMods"))
        {
            var currentPinnedCount = Plugin.Instance?.Configuration.PinnedModDirectories.Count ?? 0;
            var popDl = ImGui.GetWindowDrawList();

            //  Header: thumbtack icon + tracked-caps "PINNED MODS" +
            // accent count chip. Matches the family kicker vocabulary.
            {
                var hStart = ImGui.GetCursorScreenPos();
                float hLineH = ImGui.GetTextLineHeight();
                float hIconW = 0f;
                // Thumbtack glyph in gold (indicates "active pinned slot").
                ImGui.PushFont(UiBuilder.IconFont);
                var pinGlyph = FontAwesomeIcon.Thumbtack.ToIconString();
                var pinSz = ImGui.CalcTextSize(pinGlyph);
                popDl.AddText(new Vector2(hStart.X, hStart.Y + (hLineH - pinSz.Y) * 0.5f),
                    MW_Col(currentPinnedCount > 0 ? MW_Gold : MW_TextDim), pinGlyph);
                ImGui.PopFont();
                hIconW = pinSz.X + 10f * scale;

                // Tracked caps title.
                float kTrack = 1.6f * scale;
                string kickerS = "PINNED MODS";
                float kW = UIStyles.MeasureTrackedWidth(kickerS, kTrack);
                UIStyles.DrawTrackedText(popDl,
                    new Vector2(hStart.X + hIconW, hStart.Y + 2f * scale),
                    kickerS, MW_Col(MW_Text), kTrack);

                // Count chip (pilled).
                if (currentPinnedCount > 0)
                {
                    string cStr = currentPinnedCount.ToString();
                    float cW = ImGui.CalcTextSize(cStr).X;
                    float chipPadX = 7f * scale;
                    float chipPadY = 2f * scale;
                    float chipX = hStart.X + hIconW + kW + 10f * scale;
                    float chipTop = hStart.Y - chipPadY + 2f * scale;
                    float chipBot = chipTop + hLineH + chipPadY * 2f;
                    float chipRight = chipX + cW + chipPadX * 2;
                    popDl.AddRectFilled(new Vector2(chipX, chipTop), new Vector2(chipRight, chipBot),
                        MW_ColA(MW_Accent, 0.18f));
                    popDl.AddRect(new Vector2(chipX, chipTop), new Vector2(chipRight, chipBot),
                        MW_Col(MW_Accent), 0f, 0, 1f);
                    popDl.AddText(new Vector2(chipX + chipPadX, hStart.Y + 2f * scale),
                        MW_Col(MW_Accent), cStr);
                }

                ImGui.Dummy(new Vector2(1, hLineH + 4f * scale));
            }

            // Description.
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
            ImGui.TextColored(MW_TextDim,
                "Pinned mods stay enabled during preset switches - conflict detection won't disable them.");
            ImGui.PopTextWrapPos();

            ImGui.Dummy(new Vector2(1, 6f * scale));
            // Fading hairline divider (matches family pattern).
            {
                var sPos = ImGui.GetCursorScreenPos();
                float w = ImGui.GetContentRegionAvail().X;
                uint sSolid = MW_ColA(MW_Accent, 0.30f);
                uint sClear = MW_ColA(MW_Accent, 0f);
                popDl.AddRectFilledMultiColor(sPos, new Vector2(sPos.X + w * 0.5f, sPos.Y + 1f),
                    sClear, sSolid, sSolid, sClear);
                popDl.AddRectFilledMultiColor(
                    new Vector2(sPos.X + w * 0.5f, sPos.Y), new Vector2(sPos.X + w, sPos.Y + 1f),
                    sSolid, sClear, sClear, sSolid);
            }
            ImGui.Dummy(new Vector2(1, 8f * scale));

            //  Search pill - FA Search glyph + transparent input (matches
            // the controls row's search pill).
            {
                float pillH = 28f * scale;
                float pillW = ImGui.GetContentRegionAvail().X;
                var pillStart = ImGui.GetCursorScreenPos();
                var pillEnd = new Vector2(pillStart.X + pillW, pillStart.Y + pillH);
                popDl.AddRectFilled(pillStart, pillEnd,
                    MW_Col(new Vector4(0.078f, 0.094f, 0.125f, 1f)));
                popDl.AddRect(pillStart, pillEnd, MW_ColA(MW_Border, 1f), 0f, 0, 1f);

                ImGui.PushFont(UiBuilder.IconFont);
                var magGlyph = FontAwesomeIcon.Search.ToIconString();
                var magSz = ImGui.CalcTextSize(magGlyph);
                popDl.AddText(
                    new Vector2(pillStart.X + 10f * scale,
                                pillStart.Y + (pillH - magSz.Y) * 0.5f),
                    MW_Col(MW_TextFaint), magGlyph);
                ImGui.PopFont();

                float inputX = pillStart.X + 10f * scale + magSz.X + 8f * scale;
                float inputW = pillW - (inputX - pillStart.X) - 10f * scale;
                ImGui.SetCursorScreenPos(new Vector2(inputX,
                    pillStart.Y + (pillH - ImGui.GetFrameHeight()) * 0.5f));
                ImGui.PushStyleColor(ImGuiCol.FrameBg,        new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.FrameBgActive,  new Vector4(0f, 0f, 0f, 0f));
                ImGui.PushStyleColor(ImGuiCol.Text,           MW_Text);
                ImGui.SetNextItemWidth(inputW);
                ImGui.InputTextWithHint("##pinnedFilter", "Search mods...", ref pinnedModsFilter, 256);
                ImGui.PopStyleColor(4);
                // Advance past the pill.
                ImGui.SetCursorScreenPos(new Vector2(pillStart.X, pillEnd.Y + 6f * scale));
            }

            var emoteMods = Plugin.Instance?.EmoteDetectionService?.GetEmoteMods();
            var pinnedMods = Plugin.Instance?.Configuration.PinnedModDirectories ?? new HashSet<string>();

            if (emoteMods == null || emoteMods.Count == 0)
            {
                ImGui.Dummy(new Vector2(1, 8f * scale));
                ImGui.TextColored(MW_TextDim, "No emote mods found in cache.");
                ImGui.TextColored(MW_TextFaint, "Create some presets first to populate.");
            }
            else
            {
                var filteredMods = emoteMods
                    .Where(m => string.IsNullOrEmpty(pinnedModsFilter) ||
                                m.ModName.Contains(pinnedModsFilter, StringComparison.OrdinalIgnoreCase) ||
                                m.ModDirectory.Contains(pinnedModsFilter, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(m => pinnedMods.Contains(m.ModDirectory))
                    .ThenBy(m => m.ModName)
                    .ToList();

                // Encore-themed mod list: slate-3 bg, neutral border, each row is a big
                // clickable strip with accent-gold check glyph for pinned state.
                ImGui.PushStyleColor(ImGuiCol.ChildBg,        new Vector4(0.086f, 0.098f, 0.133f, 1f));
                ImGui.PushStyleColor(ImGuiCol.Border,         MW_ColA(MW_Border, 1f));
                ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(2f * scale, 2f * scale));
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,  new Vector2(0, 1f * scale));
                if (ImGui.BeginChild("PinnedModsList", new Vector2(-1, 240 * scale), true))
                {
                    var dl = ImGui.GetWindowDrawList();
                    var rowH = ImGui.GetTextLineHeight() + 8f * scale;

                    foreach (var mod in filteredMods)
                    {
                        var isPinned = pinnedMods.Contains(mod.ModDirectory);
                        var rowStart = ImGui.GetCursorScreenPos();
                        var rowW = ImGui.GetContentRegionAvail().X;
                        var rowEnd = new Vector2(rowStart.X + rowW, rowStart.Y + rowH);

                        // Full-row clickable - InvisibleButton avoids ImGui's default check frame.
                        if (ImGui.InvisibleButton($"pinRow_{mod.ModDirectory}", new Vector2(rowW, rowH)))
                        {
                            if (isPinned)
                                Plugin.Instance?.Configuration.PinnedModDirectories.Remove(mod.ModDirectory);
                            else
                                Plugin.Instance?.Configuration.PinnedModDirectories.Add(mod.ModDirectory);
                            Plugin.Instance?.Configuration.Save();
                            isPinned = !isPinned;
                        }
                        var rowHovered = ImGui.IsItemHovered();

                        // Hover background
                        if (rowHovered)
                            dl.AddRectFilled(rowStart, rowEnd,
                                ImGui.ColorConvertFloat4ToU32(new Vector4(0.14f, 0.16f, 0.20f, 1f)));

                        // Pinned-row accent tint (gold wash)
                        if (isPinned)
                            dl.AddRectFilled(rowStart, rowEnd,
                                ImGui.ColorConvertFloat4ToU32(new Vector4(MW_Gold.X, MW_Gold.Y, MW_Gold.Z, 0.08f)));

                        // Square checkbox (18px) - slate bg, gold check glyph when pinned
                        var cbSize = 14f * scale;
                        var cbX = rowStart.X + 10f * scale;
                        var cbY = rowStart.Y + (rowH - cbSize) / 2f;
                        var cbMin = new Vector2(cbX, cbY);
                        var cbMax = new Vector2(cbX + cbSize, cbY + cbSize);
                        dl.AddRectFilled(cbMin, cbMax,
                            ImGui.ColorConvertFloat4ToU32(new Vector4(0.06f, 0.07f, 0.10f, 1f)));
                        var cbBorder = isPinned
                            ? ImGui.ColorConvertFloat4ToU32(MW_Gold)
                            : MW_ColA(MW_Border, 1f);
                        dl.AddRect(cbMin, cbMax, cbBorder, 0f, ImDrawFlags.None, 1f);
                        if (isPinned)
                        {
                            // Inline checkmark via two short lines
                            var mid = new Vector2(cbMin.X + cbSize * 0.38f, cbMin.Y + cbSize * 0.62f);
                            var left = new Vector2(cbMin.X + cbSize * 0.20f, cbMin.Y + cbSize * 0.50f);
                            var right = new Vector2(cbMin.X + cbSize * 0.80f, cbMin.Y + cbSize * 0.25f);
                            dl.AddLine(left, mid, MW_Col(MW_Gold), 1.6f * scale);
                            dl.AddLine(mid, right, MW_Col(MW_Gold), 1.6f * scale);
                        }

                        // Mod name (brighter if pinned)
                        var nameX = cbMax.X + 10f * scale;
                        var nameY = rowStart.Y + (rowH - ImGui.GetTextLineHeight()) / 2f;
                        dl.AddText(new Vector2(nameX, nameY),
                            MW_Col(isPinned ? MW_Text : MW_TextDim), mod.ModName);

                        if (rowHovered && mod.AffectedEmotes.Count > 0)
                        {
                            MW_PushTooltipStyle();
                            ImGui.BeginTooltip();
                            ImGui.Text($"Emotes: {string.Join(", ", mod.AffectedEmotes.Take(5))}");
                            if (mod.AffectedEmotes.Count > 5)
                                ImGui.Text($"... and {mod.AffectedEmotes.Count - 5} more");
                            ImGui.EndTooltip();
                            MW_PopTooltipStyle();
                        }
                    }
                }
                ImGui.EndChild();
                ImGui.PopStyleVar(2);
                ImGui.PopStyleColor(2);

                ImGui.Dummy(new Vector2(1, 2f * scale));
                ImGui.TextColored(MW_TextFaint, $"{filteredMods.Count} / {emoteMods.Count} mods");
            }

            ImGui.EndPopup();
        }
        ImGui.PopStyleVar(2);  // WindowPadding + ItemSpacing
        PopEncoreMenuStyle();
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

        PushEncoreMenuStyle();
        // Dialog wants more breathing room than the tight kebab menus.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14f * scale, 12f * scale));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing,   new Vector2(6f * scale, 6f * scale));

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
                    MW_SetTooltip(colorName);
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

            // Parent folder selector
            ImGui.Spacing();
            var parentConfig = Plugin.Instance?.Configuration;
            if (parentConfig != null && parentConfig.Folders.Count > 0)
            {
                ImGui.Text("Inside:");
                ImGui.SetNextItemWidth(-1);
                var parentLabel = "(Top Level)";
                if (newFolderParentId != null)
                {
                    var pf = parentConfig.Folders.FirstOrDefault(f => f.Id == newFolderParentId);
                    parentLabel = pf?.Name ?? "(Top Level)";
                }
                UIStyles.PushEncoreComboStyle();
                if (ImGui.BeginCombo("##newFolderParent", parentLabel))
                {
                    if (ImGui.Selectable("(Top Level)", newFolderParentId == null))
                        newFolderParentId = null;
                    foreach (var pf in parentConfig.Folders)
                    {
                        // Only show folders within depth limit
                        if (GetFolderDepth(pf.Id, parentConfig.Folders) >= MaxNestDepth) continue;
                        if (ImGui.Selectable(pf.Name, newFolderParentId == pf.Id))
                            newFolderParentId = pf.Id;
                    }
                    ImGui.EndCombo();
                }
                UIStyles.PopEncoreComboStyle();
            }

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
                        ParentFolderId = newFolderParentId,
                        IsRoutineFolder = currentTab == MainTab.Routines,
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
        ImGui.PopStyleVar(2); // extra WindowPadding + ItemSpacing
        PopEncoreMenuStyle();
    }


    private void HandleEditorCompletion()
    {
        HandlePresetEditorCompletion();
        HandleRoutineEditorCompletion();
    }

    private void HandlePresetEditorCompletion()
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
            config.Presets.Add(preset);

        Plugin.Instance?.UpdatePresetCommands();
        config.Save();

        editorWindow.Confirmed = false;
    }

    private void HandleRoutineEditorCompletion()
    {
        if (routineEditorWindow == null || routineEditorWindow.IsOpen || !routineEditorWindow.Confirmed)
            return;

        var routine = routineEditorWindow.CurrentRoutine;
        if (routine == null)
            return;

        var config = Plugin.Instance?.Configuration;
        if (config == null)
            return;

        if (routineEditorWindow.IsNewRoutine)
            config.Routines.Add(routine);

        Plugin.Instance?.UpdatePresetCommands();  // refreshes routine command registrations too
        config.Save();

        routineEditorWindow.Confirmed = false;
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

    private IDalamudTextureWrap? GetCustomIcon(string path)
    {
        try
        {
            var texture = Plugin.TextureProvider.GetFromFile(path);
            return texture?.GetWrapOrEmpty();
        }
        catch
        {
            return null;
        }
    }

    // zoom > 1 = zoomed in (smaller UV range); offset shifts visible area
    internal static (Vector2 uv0, Vector2 uv1) CalcIconUV(int texWidth, int texHeight, float zoom, float offsetX, float offsetY)
    {
        Vector2 uv0 = Vector2.Zero, uv1 = Vector2.One;

        // Center-crop for non-square images
        if (texWidth > texHeight)
        {
            float excess = (texWidth - texHeight) / (2f * texWidth);
            uv0.X = excess; uv1.X = 1f - excess;
        }
        else if (texHeight > texWidth)
        {
            float excess = (texHeight - texWidth) / (2f * texHeight);
            uv0.Y = excess; uv1.Y = 1f - excess;
        }

        // Apply zoom (shrink UV range around center)
        if (zoom > 1f)
        {
            var cx = (uv0.X + uv1.X) * 0.5f;
            var cy = (uv0.Y + uv1.Y) * 0.5f;
            var halfW = (uv1.X - uv0.X) * 0.5f / zoom;
            var halfH = (uv1.Y - uv0.Y) * 0.5f / zoom;
            uv0.X = cx - halfW;
            uv1.X = cx + halfW;
            uv0.Y = cy - halfH;
            uv1.Y = cy + halfH;
        }

        // Apply offset (shift UV range)
        if (offsetX != 0f || offsetY != 0f)
        {
            uv0.X += offsetX;
            uv1.X += offsetX;
            uv0.Y += offsetY;
            uv1.Y += offsetY;
        }

        return (uv0, uv1);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
