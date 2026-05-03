using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Encore.Services;
using Encore.Windows;

namespace Encore;

public sealed class Plugin : IDalamudPlugin
{
    // Static instance for easy access from windows
    public static Plugin? Instance { get; private set; }

    // Dalamud services
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;

    // Icons directory for custom preset images
    internal static string IconsDirectory => Path.Combine(PluginInterface.GetPluginConfigDirectory(), "icons");

    // Main command
    private const string MainCommand = "/encore";

    // Patch notes version - bump when patch notes content changes (not every build)
    public const string PatchNotesVersion = "1.0.0.6";

    // Update check: fetches repo.json from GitHub to compare versions
    private const string UpdateCheckUrl = "https://raw.githubusercontent.com/IcarusXIV/Encore/master/Encore/repo.json";
    private const int UpdateCheckIntervalMs = 30 * 60 * 1000; // 30 minutes
    private static readonly HttpClient updateHttpClient = new();
    private System.Threading.Timer? updateCheckTimer;
    private bool updateNotified = false;

    // Configuration
    public Configuration Configuration { get; init; }

    // Services
    public PenumbraService? PenumbraService { get; private set; }
    public EmoteDetectionService? EmoteDetectionService { get; private set; }
    public MovementService? MovementService { get; private set; }
    public PoseService? PoseService { get; private set; }
    public SimpleHeelsService? SimpleHeelsService { get; private set; }
    public BgmAnalysisService? BgmAnalysisService { get; private set; }
    public BgmTrackerService? BgmTrackerService { get; private set; }

    // beat-matched visuals Hz; falls back to 2.0 (120 BPM) when idle or undetected
    public float CurrentBgmHz
    {
        get
        {
            bool performing = !string.IsNullOrEmpty(Configuration?.ActivePresetId)
                           || !string.IsNullOrEmpty(ActiveRoutineName);
            if (!performing) return 2.0f;
            var bpm = BgmTrackerService?.CurrentBpm;
            if (bpm == null || bpm <= 30f || bpm >= 250f) return 2.0f;
            return bpm.Value / 60f;
        }
    }

    // Windows
    public readonly WindowSystem WindowSystem = new("Encore");
    private MainWindow MainWindow { get; init; }
    private PresetEditorWindow PresetEditorWindow { get; init; }
    private RoutineEditorWindow RoutineEditorWindow { get; init; }
    private IconPickerWindow IconPickerWindow { get; init; }
    private HelpWindow HelpWindow { get; init; }
    private PatchNotesWindow PatchNotesWindow { get; init; }
    internal SettingsWindow SettingsWindow { get; init; }
    internal ImGuiFileBrowserWindow FileBrowserWindow { get; init; }
    internal HeelsGizmoOverlay HeelsGizmoOverlay { get; init; }

    internal IFontHandle? HeaderFont { get; private set; }
    internal IFontHandle? BannerFont { get; private set; }
    internal IFontHandle? NumeralFont { get; private set; }
    internal IFontHandle? TitleFont { get; private set; }


    private readonly HashSet<string> registeredPresetCommands = new();

    // Prevent overlapping preset executions
    private volatile bool isExecutingPreset = false;

    // Routine state
    private Routine? activeRoutine;
    private System.Threading.CancellationTokenSource? routineCts;
    private int activeRoutineStepIndex = 0;
    private DateTime activeRoutineStepStartedUtc;
    private ushort routineStepEmoteId = 0;
    private uint routineStepTimeline = 0;
    private bool routineWaitingForEmoteStart = false;
    private ushort routinePrevEmoteId = 0;
    private uint routinePrevTimeline = 0;
    private System.Numerics.Vector3 routinePrevPosition;
    private bool routineMovementBaselineSet = false;
    // Set to true by RunMacroAsync when a macro step's commands finish naturally (not cancelled).
    // Used by UpdateActiveRoutine to advance "Until macro ends" macro steps.
    private volatile bool routineMacroCompleted = false;
    private DateTime routineMacroCompletedAt = DateTime.MinValue;
    // Preset IDs whose priorities + conflict disables were applied at routine start.
    // Steps for these presets skip ExecutePreset's full cycle - they just fire their emote.
    private readonly HashSet<string> routineBulkAppliedPresetIds = new();
    private bool isAdvancingRoutine = false;  // suppresses cancellation while we run step transitions
    private readonly HashSet<string> registeredRoutineCommands = new();

    // Emote unlock bypass: Lumina-based lookup tables for PAP rewriting
    private Dictionary<string, ushort>? emoteCommandToId;
    // emote ID -> all ActionTimeline entries (slot index, key string, isLoop flag, loadType: 0=Facial, 1=PerJob, 2=Normal)
    private Dictionary<ushort, List<(int slot, string key, bool isLoop, byte loadType)>>? emoteIdToTimelineKeys;
    // Per-emote set of ActionTimeline row IDs - used to tell "internal intro->loop transition" from "emote ended"
    private Dictionary<ushort, HashSet<ushort>>? emoteIdToTimelineRowIds;

    // List of expression emotes (face-only) for the routine editor dropdown.
    // Each entry: (command like "/smile", display label like "Smile").
    public List<(string Command, string Label)> ExpressionEmotes { get; private set; } = new();
    // Track active emote swap state
    private bool emoteSwapActive = false;
    private int emoteSwapPriority = 0;
    private Guid emoteSwapCollectionId = Guid.Empty;
    // Real Penumbra mod directory name for emote swaps (written to Penumbra mod dir on disk)
    private const string EncoreSwapModName = "_EncoreEmoteSwap";
    private bool encoreSwapModRegistered = false;
    // Counter for unique swap file names (avoids Penumbra file-content caching)
    private int emoteSwapFileCounter = 0;
    // Cache carrier PAP names to avoid re-reading from game data each call
    private readonly Dictionary<ushort, string> carrierPapNameCache = new();
    // carrier sticking: reuse same carrier for same emote (sync plugin compat)
    private string? lastBypassEmoteCommand = null;
    private string? lastBypassCarrierCommand = null;
    private ushort lastBypassCarrierId = 0;
    private string? lastBypassRaceCode = null;
    // session-wide carrier tracking; game caches PAP per-animation-key (Penumbra ChangeCounter only busts TMBs)
    private readonly HashSet<ushort> usedCarrierIds = new();
    // Soft-disabled state: swap files + sticking state preserved, but Penumbra temp settings removed.
    // Re-enabled on next bypass for same emote via sticking check in TrySetupEmoteBypass.
    private bool emoteSwapSoftDisabled = false;
    // Guard flag: true while we're executing the carrier emote as part of bypass.
    // Prevents the ExecuteCommandInner hook from detecting our own carrier execution
    // and clearing the swap (the hook should only clear on USER-initiated carrier commands).
    private volatile bool isExecutingBypassCarrier = false;
    // EmoteMode.ConditionMode: 0 = any state, 3 = standing only
    private Dictionary<ushort, byte>? emoteIdToConditionMode;
    // Emote-type preset tracking; poses/movement stay sticky
    private ushort activePresetEmoteId = 0;
    private bool activePresetEmoteSeen = false;
    // either EmoteId or Timeline matching keeps the preset "alive"
    private ushort activePresetEmoteTimeline = 0;
    // frames where neither signal matches; tolerates brief blips
    private int activePresetEndCandidateFrames = 0;
    // EmoteId already playing when preset started; skipped to avoid latching onto previous preset's tail
    private ushort activePresetEmoteIgnoreId = 0;
    private bool activePresetIgnoreSnapshotted = false;
    private long activePresetIgnoreExpiryTicks = 0;

    // resolves the active SCD via option-group selections; falls back to game BGM if no mod music
    private void TryWireModMusicForBpm(DancePreset preset, PresetModifier? modifier)
    {
        try
        {
            if (BgmTrackerService == null) return;
            if (preset.IsVanilla || string.IsNullOrEmpty(preset.ModDirectory))
            {
                BgmTrackerService.ClearModScd();
                return;
            }

            var penumbraRoot = PenumbraService?.GetModDirectory();
            if (string.IsNullOrEmpty(penumbraRoot)) return;

            var modRoot = System.IO.Path.Combine(penumbraRoot, preset.ModDirectory);
            if (!System.IO.Directory.Exists(modRoot)) return;

            // mirror ApplyTempSettings: preset selections + modifier OptionOverrides on top
            var merged = new Dictionary<string, List<string>>(preset.ModOptions);
            if (modifier != null)
            {
                foreach (var (g, o) in modifier.OptionOverrides)
                    merged[g] = o;
            }

            var scdPath = ModMusicFinder.FindActiveScd(modRoot, merged, Log);
            // Always log what we resolved (or didn't) so users debugging
            // multi-music modifiers can see the resolution chain in /xllog.
            string mergedStr = merged.Count == 0 ? "(none)"
                : string.Join(", ", merged.Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value)}]"));
            Log.Debug($"[BGM] preset='{preset.Name}' mod='{preset.ModDirectory}' modifier='{modifier?.Name ?? "-"}' opts={mergedStr} -> SCD: {scdPath ?? "(none)"}");
            if (scdPath == null)
            {
                BgmTrackerService.ClearModScd();
                return;
            }
            BgmTrackerService.SetModScd(scdPath);
        }
        catch (Exception ex)
        {
            Log.Debug($"[BGM] mod music detection failed: {ex.Message}");
        }
    }

    private void ArmPresetEmoteIgnore(DancePreset preset)
    {
        activePresetEmoteIgnoreId = 0;
        activePresetIgnoreSnapshotted = false;
        activePresetIgnoreExpiryTicks = preset.AnimationType == 1
            ? Environment.TickCount64 + 1500
            : 0;
    }

    // Emote loop tracking (/loop command)
    private string? loopingEmoteCommand = null;
    private ushort previousEmoteId = 0;
    private ushort previousTimeline = 0;
    private ushort loopingEmoteId = 0;
    private ushort emoteTimeline = 0;
    private bool loopWaitingForStart = false;
    private System.Numerics.Vector3 previousPosition;
    // Locked emote loop: re-execute via carrier instead of normal ExecuteEmote
    private string? loopCarrierCommand = null;
    private ushort loopCarrierId = 0;
    // Weapon drawn state tracking for /loop cancellation
    private bool loopPreviousWeaponDrawn = false;

    // ShellCommandModule hook: intercept native emote commands for locked emote bypass
    private Hook<ShellCommandModule.Delegates.ExecuteCommandInner>? executeCommandInnerHook;
    // ConditionFlag tracking for /loop cancellation
    private bool conditionLoopCancelRegistered = false;

    public Plugin()
    {
        Instance = this;

        // Pre-load bundled assemblies that Dalamud's ManagedLoadContext can't resolve automatically
        try
        {
            var pluginDir = PluginInterface.AssemblyLocation.Directory?.FullName;
            if (pluginDir != null)
            {
                var alc = AssemblyLoadContext.GetLoadContext(typeof(Plugin).Assembly);
                if (alc != null)
                {
                    foreach (var dll in Directory.GetFiles(pluginDir, "SixLabors.*.dll"))
                    {
                        try { alc.LoadFromAssemblyPath(dll); }
                        catch (Exception ex) { Log.Warning($"Pre-load failed for {Path.GetFileName(dll)}: {ex.Message}"); }
                    }

                    // Backup resolver in case pre-load didn't register correctly
                    alc.Resolving += (context, name) =>
                    {
                        if (name.Name != null && name.Name.StartsWith("SixLabors."))
                        {
                            var path = Path.Combine(pluginDir, name.Name + ".dll");
                            if (File.Exists(path))
                            {
                                try { return context.LoadFromAssemblyPath(path); }
                                catch { }
                            }
                        }
                        return null;
                    };
                }
            }
        }
        catch { /* Non-critical - custom icon resize will fall back to file copy */ }

        // Load configuration
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Backfill CreatedAt for existing presets that predate this field
        bool needsCreatedAtBackfill = false;
        for (int i = 0; i < Configuration.Presets.Count; i++)
        {
            if (Configuration.Presets[i].CreatedAt == default)
            {
                Configuration.Presets[i].CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
                needsCreatedAtBackfill = true;
            }
        }
        if (needsCreatedAtBackfill) Configuration.Save();

        // One-time migration: check if we need to restore permanent changes from old approach
        bool needsLegacyRestore = Configuration.OriginalPriorities.Count > 0
            || Configuration.ModsWeEnabled.Count > 0
            || Configuration.ModsWeDisabled.Count > 0
            || Configuration.OriginalModOptions.Count > 0;

        // Ensure icons directory exists for custom preset images
        Directory.CreateDirectory(IconsDirectory);

        // Initialize services
        try
        {
            PenumbraService = new PenumbraService(PluginInterface, Log);
            EmoteDetectionService = new EmoteDetectionService(PenumbraService, PluginInterface, Log);
            PoseService = new PoseService(GameInteropProvider, ObjectTable, Framework, Log);
            MovementService = new MovementService(GameInteropProvider, ObjectTable, GameConfig, Log);
            SimpleHeelsService = new SimpleHeelsService(PluginInterface, ObjectTable, Log);

            try
            {
                BgmAnalysisService = new BgmAnalysisService(
                    PluginInterface.GetPluginConfigDirectory(), Log);
                BgmTrackerService = new BgmTrackerService(
                    SigScanner, DataManager, Log, BgmAnalysisService);
            }
            catch (Exception bgmEx)
            {
                Log.Warning($"BGM BPM tracking unavailable: {bgmEx.Message}");
                BgmAnalysisService = null;
                BgmTrackerService = null;
            }

            // Initialize emote mod cache in background (like CS+)
            if (PenumbraService.IsAvailable)
            {
                EmoteDetectionService.InitializeCacheAsync();

                // Subscribe to Penumbra mod events for cache invalidation
                SubscribeToPenumbraEvents();

                // Run legacy migration if needed (restore permanent Penumbra changes from old approach)
                if (needsLegacyRestore)
                {
                    RunLegacyRestore();
                }
                // Clean up stale temp settings on reload (handles crash/unclean unload)
                else if (Configuration.ModsWithTempSettings.Count > 0)
                {
                    foreach (var key in Configuration.ModsWithTempSettings.ToList())
                    {
                        var parts = key.Split('|');
                        if (parts.Length >= 2 && Guid.TryParse(parts[0], out var collId))
                            PenumbraService.RemoveTemporaryModSettings(collId, parts[1], parts.Length >= 3 ? parts[2] : "");
                    }
                    Configuration.ModsWithTempSettings.Clear();
                    Configuration.ActivePresetId = null;
                    Configuration.ActivePresetCollectionId = null;
                    Configuration.Save();
                    Log.Information("Cleaned up stale temp settings from previous session");
                }

                // Clean up stale EncoreEmoteSwap real mod from previous session
                try
                {
                    var (gotColl, collId, _) = PenumbraService.GetCurrentCollection();
                    if (gotColl && collId != Guid.Empty)
                    {
                        PenumbraService.RemoveTemporaryModSettings(collId, EncoreSwapModName);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to initialize services: {ex.Message}");
        }

        // Build emote command -> ID and emote ID -> timeline keys lookups from Lumina
        try
        {
            emoteCommandToId = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            emoteIdToTimelineKeys = new Dictionary<ushort, List<(int slot, string key, bool isLoop, byte loadType)>>();
            emoteIdToTimelineRowIds = new Dictionary<ushort, HashSet<ushort>>();
            emoteIdToConditionMode = new Dictionary<ushort, byte>();
            var emoteSheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
            if (emoteSheet != null)
            {
                foreach (var row in emoteSheet)
                {
                    var emoteId = (ushort)row.RowId;

                    // Store ConditionMode from EmoteMode (0=any state, 3=standing only)
                    var emoteModeRef = row.EmoteMode.ValueNullable;
                    if (emoteModeRef != null)
                        emoteIdToConditionMode[emoteId] = emoteModeRef.Value.ConditionMode;

                    // Store ActionTimeline entries with loop detection and load type
                    // LoadType: 0=Facial expression, 1=PerJob, 2=Normal body animation
                    var entries = new List<(int slot, string key, bool isLoop, byte loadType)>();
                    var rowIds = new HashSet<ushort>();
                    for (int i = 0; i < 7; i++)
                    {
                        var tlRef = row.ActionTimeline[i];
                        if (tlRef.RowId == 0 || !tlRef.IsValid) continue;
                        var tl = tlRef.Value;
                        var key = tl.Key.ToString();
                        if (!string.IsNullOrEmpty(key))
                        {
                            bool isLoop = key.Contains("loop", StringComparison.OrdinalIgnoreCase);
                            entries.Add((i, key, isLoop, tl.LoadType));
                            rowIds.Add((ushort)tlRef.RowId);
                        }
                    }
                    if (entries.Count > 0)
                    {
                        emoteIdToTimelineKeys.TryAdd(emoteId, entries);
                        emoteIdToTimelineRowIds.TryAdd(emoteId, rowIds);
                    }

                    // Map all command variants to emote ID
                    var textCmd = row.TextCommand.ValueNullable;
                    if (textCmd == null) continue;

                    var cmd = textCmd.Value.Command.ToString();
                    if (!string.IsNullOrEmpty(cmd))
                        emoteCommandToId.TryAdd(cmd.ToLowerInvariant(), emoteId);

                    var alias = textCmd.Value.Alias.ToString();
                    if (!string.IsNullOrEmpty(alias))
                        emoteCommandToId.TryAdd(alias.ToLowerInvariant(), emoteId);

                    var shortCmd = textCmd.Value.ShortCommand.ToString();
                    if (!string.IsNullOrEmpty(shortCmd))
                        emoteCommandToId.TryAdd(shortCmd.ToLowerInvariant(), emoteId);

                    var shortAlias = textCmd.Value.ShortAlias.ToString();
                    if (!string.IsNullOrEmpty(shortAlias))
                        emoteCommandToId.TryAdd(shortAlias.ToLowerInvariant(), emoteId);

                    // Build the expressions list for the routine editor dropdown.
                    // Filter to emotes whose category is "Expressions".
                    var category = row.EmoteCategory.ValueNullable;
                    if (category != null &&
                        string.Equals(category.Value.Name.ExtractText(), "Expressions", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(cmd))
                    {
                        var label = row.Name.ExtractText();
                        if (string.IsNullOrEmpty(label)) label = cmd.TrimStart('/');
                        ExpressionEmotes.Add((cmd, label));
                    }
                }

                // Sort expressions alphabetically by label
                ExpressionEmotes = ExpressionEmotes
                    .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var emotesWithTimelines = emoteIdToTimelineKeys.Count;
                // Count emotes that have facial (LoadType 0) entries - needed for expression support in unlock bypass
                int emotesWithFacial = 0;
                foreach (var (id, entries2) in emoteIdToTimelineKeys)
                {
                    if (entries2.Any(e => e.loadType == 0 && !string.IsNullOrEmpty(e.key)))
                        emotesWithFacial++;
                }
                var condMode0Count = emoteIdToConditionMode.Count(kv => kv.Value == 0);
                Log.Information($"Built emote lookup: {emoteCommandToId.Count} commands, {emotesWithTimelines} emotes with timeline keys, {emotesWithFacial} with facial entries, {condMode0Count} with ConditionMode 0 (any-state)");

                // Populate SpToEmote in EmoteDetectionService from Lumina data
                // This maps emote_sp/sp## filenames to emote commands for file path analysis
                try
                {
                    var spAdded = Services.EmoteDetectionService.PopulateSpToEmote(emoteIdToTimelineKeys, emoteCommandToId);
                    if (spAdded > 0)
                        Log.Information($"Populated SpToEmote with {spAdded} entries from Lumina");
                }
                catch (Exception spEx)
                {
                    Log.Warning($"Failed to populate SpToEmote: {spEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to build emote command lookup: {ex.Message}");
            emoteCommandToId = null;
            emoteIdToTimelineKeys = null;
        }

        // Create windows
        MainWindow = new MainWindow();
        PresetEditorWindow = new PresetEditorWindow();
        RoutineEditorWindow = new RoutineEditorWindow();
        IconPickerWindow = new IconPickerWindow();
        HelpWindow = new HelpWindow();
        PatchNotesWindow = new PatchNotesWindow(this);
        SettingsWindow = new SettingsWindow();
        FileBrowserWindow = new ImGuiFileBrowserWindow("Select Icon Image");
        FileBrowserWindow.SetConfiguration(Configuration);
        HeelsGizmoOverlay = new HeelsGizmoOverlay(PluginInterface, Condition, ObjectTable, GameGui);
        HeelsGizmoOverlay.OnChanged = t =>
        {
            if (SimpleHeelsService == null || !SimpleHeelsService.IsAvailable) return;
            SimpleHeelsService.ApplyOffset(t.X, t.Y, t.Z, t.Rotation, t.Pitch, t.Roll);
        };

        MainWindow.SetEditorWindow(PresetEditorWindow);
        MainWindow.SetRoutineEditor(RoutineEditorWindow);
        MainWindow.SetHelpWindow(HelpWindow);
        MainWindow.SetPatchNotesWindow(PatchNotesWindow);
        PresetEditorWindow.SetIconPicker(IconPickerWindow);
        RoutineEditorWindow.SetIconPicker(IconPickerWindow);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(PresetEditorWindow);
        WindowSystem.AddWindow(RoutineEditorWindow);
        WindowSystem.AddWindow(IconPickerWindow);
        WindowSystem.AddWindow(HelpWindow);
        WindowSystem.AddWindow(PatchNotesWindow);
        WindowSystem.AddWindow(SettingsWindow);
        WindowSystem.AddWindow(FileBrowserWindow);

        // Register main command
        CommandManager.AddHandler(MainCommand, new CommandInfo(OnMainCommand)
        {
            HelpMessage = "Open the Encore dance preset manager"
        });

        // Register reset command
        CommandManager.AddHandler($"{MainCommand}reset", new CommandInfo(OnResetCommand)
        {
            HelpMessage = "Reset all mod priorities to their original values"
        });

        // Register align command
        CommandManager.AddHandler("/align", new CommandInfo(OnAlignCommand)
        {
            HelpMessage = "Walk to your target's position for alignment"
        });

        // Register loop command
        CommandManager.AddHandler("/loop", new CommandInfo(OnLoopCommand)
        {
            HelpMessage = "Loop an emote (e.g., /loop stomp). Use /loop to stop."
        });

        // Register vanilla command
        CommandManager.AddHandler("/vanilla", new CommandInfo(OnVanillaCommand)
        {
            HelpMessage = "Play an emote with vanilla animation, or use a modded animation you don't have the emote for (e.g., /vanilla beesknees)"
        });

        // Register preset commands
        UpdatePresetCommands();

        // Show help on first launch
        if (!Configuration.HasSeenHelp)
        {
            HelpWindow.IsOpen = true;
            Configuration.HasSeenHelp = true;
            // first-time users skip patch notes
            Configuration.LastSeenPatchNotesVersion = PatchNotesVersion;
            Configuration.Save();
        }
        else if (Configuration.LastSeenPatchNotesVersion != PatchNotesVersion &&
                 Configuration.ShowPatchNotesOnStartup)
        {
            PatchNotesWindow.IsOpen = true;
            Configuration.LastSeenPatchNotesVersion = PatchNotesVersion;
            Configuration.Save();
        }

        if (Configuration.IsMainWindowOpen)
        {
            MainWindow.IsOpen = true;
        }

        // Build the header banner font (used for "ENCORE" in the main window
        // title bar). Real ~26px glyphs - not a scaled default font - so the
        // text stays crisp.
        try
        {
            HeaderFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            {
                e.OnPreBuild(tk => tk.AddDalamudDefaultFont(24f));
            });
            // Fire-and-forget WaitAsync so Dalamud kicks off the atlas
            // build immediately instead of on first use - avoids the
            // "small -> big" font pop when the window first opens.
            _ = HeaderFont?.WaitAsync();
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to build header font: {ex.Message} - ENCORE banner will fall back to default font");
            HeaderFont = null;
        }

        try
        {
            // Prefer a custom display TTF dropped into the plugin's Assets
            // folder (e.g. Unbounded-Bold.ttf from Google Fonts) for a more
            // distinctive wordmark. Falls back to the default Dalamud font.
            var assetDir = PluginInterface.AssemblyLocation.Directory?.FullName;
            var customPath = assetDir != null
                ? Path.Combine(assetDir, "Assets", "Unbounded-Bold.ttf")
                : null;
            bool haveCustom = customPath != null && File.Exists(customPath);
            BannerFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            {
                if (haveCustom)
                {
                    e.OnPreBuild(tk => tk.AddFontFromFile(
                        customPath!,
                        new Dalamud.Interface.ManagedFontAtlas.SafeFontConfig { SizePx = 56f }));
                }
                else
                {
                    e.OnPreBuild(tk => tk.AddDalamudDefaultFont(54f));
                }
            });
            if (haveCustom) Log.Information($"Banner font loaded from {customPath}");
            _ = BannerFont?.WaitAsync();
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to build banner font: {ex.Message} - patch notes banner will fall back to header font");
            BannerFont = null;
        }

        try
        {
            // Unbounded-Bold (Assets) -> Segoe UI Black -> Dalamud Axis
            var assetDir = PluginInterface.AssemblyLocation.Directory?.FullName;
            var customPath = assetDir != null
                ? Path.Combine(assetDir, "Assets", "Unbounded-Bold.ttf")
                : null;
            bool haveCustom = customPath != null && File.Exists(customPath);

            string? systemBoldPath = null;
            var winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(winFonts))
            {
                foreach (var name in new[] { "seguibl.ttf", "segoeuib.ttf", "ariblk.ttf" })
                {
                    var p = Path.Combine(winFonts, name);
                    if (File.Exists(p)) { systemBoldPath = p; break; }
                }
            }

            NumeralFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            {
                if (haveCustom)
                {
                    e.OnPreBuild(tk => tk.AddFontFromFile(
                        customPath!,
                        new Dalamud.Interface.ManagedFontAtlas.SafeFontConfig { SizePx = 84f }));
                }
                else if (systemBoldPath != null)
                {
                    e.OnPreBuild(tk => tk.AddFontFromFile(
                        systemBoldPath,
                        new Dalamud.Interface.ManagedFontAtlas.SafeFontConfig { SizePx = 84f }));
                }
                else
                {
                    e.OnPreBuild(tk => tk.AddDalamudDefaultFont(84f));
                }
            });
            _ = NumeralFont?.WaitAsync();
            if (!haveCustom && systemBoldPath != null)
                Log.Information($"Numeral font using system bold fallback: {systemBoldPath}");
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to build numeral font: {ex.Message}. Guide chapter numerals will fall back to banner font.");
            NumeralFont = null;
        }

        try
        {
            // 22px; same fallback chain as NumeralFont
            var assetDir = PluginInterface.AssemblyLocation.Directory?.FullName;
            var customPath = assetDir != null
                ? Path.Combine(assetDir, "Assets", "Unbounded-Bold.ttf")
                : null;
            bool haveCustom = customPath != null && File.Exists(customPath);

            string? systemBoldPath = null;
            var winFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(winFonts))
            {
                foreach (var name in new[] { "seguibl.ttf", "segoeuib.ttf", "ariblk.ttf" })
                {
                    var p = Path.Combine(winFonts, name);
                    if (File.Exists(p)) { systemBoldPath = p; break; }
                }
            }

            TitleFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e =>
            {
                if (haveCustom)
                {
                    e.OnPreBuild(tk => tk.AddFontFromFile(
                        customPath!,
                        new Dalamud.Interface.ManagedFontAtlas.SafeFontConfig { SizePx = 30f }));
                }
                else if (systemBoldPath != null)
                {
                    e.OnPreBuild(tk => tk.AddFontFromFile(
                        systemBoldPath,
                        new Dalamud.Interface.ManagedFontAtlas.SafeFontConfig { SizePx = 30f }));
                }
                else
                {
                    e.OnPreBuild(tk => tk.AddDalamudDefaultFont(30f));
                }
            });
            _ = TitleFont?.WaitAsync();
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to build title font: {ex.Message}. Guide chapter titles will fall back to header font.");
            TitleFont = null;
        }

        // Hook UI drawing
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

        // Hook ShellCommandModule.ExecuteCommandInner to intercept native emote commands
        // for locked emote bypass (allows typing /runwaywalk directly instead of /vanilla runwaywalk)
        try
        {
            unsafe
            {
                executeCommandInnerHook = GameInteropProvider.HookFromAddress<ShellCommandModule.Delegates.ExecuteCommandInner>(
                    ShellCommandModule.MemberFunctionPointers.ExecuteCommandInner,
                    DetourExecuteCommandInner);
                executeCommandInnerHook.Enable();
                Log.Information("ExecuteCommandInner hook enabled for locked emote interception");
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to hook ExecuteCommandInner: {ex.Message} - /vanilla command still works");
            executeCommandInnerHook = null;
        }

        // Subscribe to ConditionFlag changes for /loop cancellation
        Condition.ConditionChange += OnConditionChanged;
        conditionLoopCancelRegistered = true;

        // Hydrate used-carrier tracking from persisted config so plugin reloads don't rotate back
        // to carriers the game still has cached from the previous session.
        foreach (var id in Configuration.UsedBypassCarrierIds)
            usedCarrierIds.Add(id);
        if (usedCarrierIds.Count > 0)
            Log.Information($"[UnlockBypass] Restored {usedCarrierIds.Count} previously-used carrier(s) from config");

        // Check for plugin updates in the background
        CheckForUpdates();

        Log.Information($"Encore loaded. Penumbra available: {PenumbraService?.IsAvailable ?? false}");
    }

    // Penumbra event subscriptions
    private ICallGateSubscriber<string, object?>? modAddedSubscriber;
    private ICallGateSubscriber<string, object?>? modDeletedSubscriber;
    private ICallGateSubscriber<string, string, object?>? modMovedSubscriber;
    private Action<string>? onModAddedAction;
    private Action<string>? onModDeletedAction;
    private Action<string, string>? onModMovedAction;

    public void Dispose()
    {
        // Stop update check timer
        updateCheckTimer?.Dispose();
        updateCheckTimer = null;

        // Unhook UI and framework
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        HeaderFont?.Dispose();
        HeaderFont = null;
        BannerFont?.Dispose();
        BannerFont = null;
        NumeralFont?.Dispose();
        NumeralFont = null;
        TitleFont?.Dispose();
        TitleFont = null;

        // Unsubscribe from condition changes
        if (conditionLoopCancelRegistered)
        {
            Condition.ConditionChange -= OnConditionChanged;
            conditionLoopCancelRegistered = false;
        }

        // Disable ExecuteCommandInner hook
        executeCommandInnerHook?.Disable();
        executeCommandInnerHook?.Dispose();
        executeCommandInnerHook = null;

        // Unsubscribe from Penumbra events
        UnsubscribeFromPenumbraEvents();

        // Remove windows
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();

        // Remove commands
        CommandManager.RemoveHandler(MainCommand);
        CommandManager.RemoveHandler($"{MainCommand}reset");
        CommandManager.RemoveHandler("/align");
        CommandManager.RemoveHandler("/loop");
        CommandManager.RemoveHandler("/vanilla");

        foreach (var cmd in registeredPresetCommands)
        {
            CommandManager.RemoveHandler($"/{cmd}");
        }
        foreach (var cmd in registeredRoutineCommands)
        {
            try { CommandManager.RemoveHandler($"/{cmd}"); } catch { }
        }
        registeredPresetCommands.Clear();

        // Clear any active emote swap
        ClearEmoteSwap();

        // Clean up temporary Penumbra settings before disposal
        if (PenumbraService?.IsAvailable == true && Configuration.ModsWithTempSettings.Count > 0)
        {
            foreach (var key in Configuration.ModsWithTempSettings.ToList())
            {
                var parts = key.Split('|');
                if (parts.Length >= 2 && Guid.TryParse(parts[0], out var collId))
                    PenumbraService.RemoveTemporaryModSettings(collId, parts[1], parts.Length >= 3 ? parts[2] : "");
            }
            Configuration.ModsWithTempSettings.Clear();
            Configuration.ActivePresetId = null;
            Configuration.ActivePresetCollectionId = null;
            Configuration.Save();
        }

        // Dispose services
        HeelsGizmoOverlay?.Dispose();
        MovementService?.Dispose();
        PoseService?.Dispose();
        SimpleHeelsService?.Dispose();
        BgmTrackerService?.Dispose();
        BgmAnalysisService?.Dispose();
        PenumbraService?.Dispose();

        Instance = null;
    }

    private void SubscribeToPenumbraEvents()
    {
        try
        {
            // Create action handlers
            onModAddedAction = OnPenumbraModAdded;
            onModDeletedAction = OnPenumbraModDeleted;
            onModMovedAction = OnPenumbraModMoved;

            // Subscribe to mod added event
            modAddedSubscriber = PluginInterface.GetIpcSubscriber<string, object?>("Penumbra.ModAdded");
            modAddedSubscriber.Subscribe(onModAddedAction);

            // Subscribe to mod deleted event
            modDeletedSubscriber = PluginInterface.GetIpcSubscriber<string, object?>("Penumbra.ModDeleted");
            modDeletedSubscriber.Subscribe(onModDeletedAction);

            // Subscribe to mod moved event
            modMovedSubscriber = PluginInterface.GetIpcSubscriber<string, string, object?>("Penumbra.ModMoved");
            modMovedSubscriber.Subscribe(onModMovedAction);

            Log.Information("Subscribed to Penumbra mod events");
        }
        catch (Exception ex)
        {
            Log.Warning($"Failed to subscribe to Penumbra events: {ex.Message}");
        }
    }

    private void UnsubscribeFromPenumbraEvents()
    {
        try
        {
            if (modAddedSubscriber != null && onModAddedAction != null)
                modAddedSubscriber.Unsubscribe(onModAddedAction);

            if (modDeletedSubscriber != null && onModDeletedAction != null)
                modDeletedSubscriber.Unsubscribe(onModDeletedAction);

            if (modMovedSubscriber != null && onModMovedAction != null)
                modMovedSubscriber.Unsubscribe(onModMovedAction);
        }
        catch (Exception ex)
        {
            Log.Debug($"Error unsubscribing from Penumbra events: {ex.Message}");
        }
    }

    private void OnPenumbraModAdded(string modDirectory)
    {
        EmoteDetectionService?.OnModAdded(modDirectory);
    }

    private void OnPenumbraModDeleted(string modDirectory)
    {
        EmoteDetectionService?.OnModDeleted(modDirectory);
    }

    private void OnPenumbraModMoved(string oldDirectory, string newDirectory)
    {
        EmoteDetectionService?.OnModMoved(oldDirectory, newDirectory);
    }

    private void OnMainCommand(string command, string args)
    {
        // Handle subcommands
        args = args.Trim().ToLower();

        if (args == "reset")
        {
            ResetAllPriorities();
            return;
        }

        if (args == "whatsnew")
        {
            PatchNotesWindow.IsOpen = true;
            return;
        }

        if (args == "bpm" || args == "bpm clear")
        {
            var tracker = BgmTrackerService;
            if (tracker == null)
            {
                ChatGui.Print("[Encore] BPM tracking unavailable (sig scan failed).");
                return;
            }
            if (args == "bpm clear")
            {
                BgmAnalysisService?.ClearCache();
                if (!string.IsNullOrEmpty(tracker.ModScdPath))
                    tracker.SetModScd(tracker.ModScdPath);
                ChatGui.Print("[Encore] BPM cache cleared. Re-analyzing current track.");
                return;
            }
            var source = !string.IsNullOrEmpty(tracker.ModScdPath)
                ? $"mod SCD: {tracker.ModScdPath}"
                : $"BGM row {tracker.CurrentSongId}: {tracker.CurrentScdPath ?? "(none)"}";
            ChatGui.Print($"[Encore] {source} - BPM: {(tracker.CurrentBpm.HasValue ? tracker.CurrentBpm.Value.ToString("F0") : "analyzing...")}");
            return;
        }

        if (args == "random" || args.StartsWith("random "))
        {
            var folderQuery = args.Length > 6 ? args.Substring(7).Trim() : "";
            ExecuteRandomPreset(folderQuery);
            return;
        }

        if (args == "stoproutine")
        {
            if (activeRoutine != null) CancelRoutine(reason: "manual");
            return;
        }

        // Default: toggle main window
        MainWindow.Toggle();
    }

    private readonly Random randomGenerator = new();

    private void ExecuteRandomPreset(string folderQuery)
    {
        // Filter presets by folder if a query was given
        if (!string.IsNullOrEmpty(folderQuery))
        {
            // Fuzzy folder-name match: exact (case-insensitive) first, then prefix, then contains
            var folder = Configuration.Folders.FirstOrDefault(f =>
                string.Equals(f.Name, folderQuery, StringComparison.OrdinalIgnoreCase))
                ?? Configuration.Folders.FirstOrDefault(f =>
                    f.Name.StartsWith(folderQuery, StringComparison.OrdinalIgnoreCase))
                ?? Configuration.Folders.FirstOrDefault(f =>
                    f.Name.Contains(folderQuery, StringComparison.OrdinalIgnoreCase));

            if (folder == null)
            {
                Log.Warning($"[Random] No folder matching \"{folderQuery}\".");
                return;
            }

            ExecuteRandomFromFolder(folder.Id);
            return;
        }

        ExecuteRandomFromFolder(null);
    }

    public void ExecuteRandomFromFolder(string? folderId)
    {
        var candidates = folderId == null
            ? Configuration.Presets
            : Configuration.Presets.Where(p => p.FolderId == folderId).ToList();

        var list = candidates.ToList();
        if (list.Count == 0)
        {
            Log.Warning("[Random] No presets to pick from.");
            return;
        }

        var pick = list[randomGenerator.Next(list.Count)];
        Log.Information($"[Random] Picked: {pick.Name}");
        ExecutePreset(pick);
    }

    private void OnResetCommand(string command, string args)
    {
        ResetAllPriorities();
    }

    private static readonly HashSet<string> NoLoopCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/sit", "/groundsit", "/doze", "/cpose", "/changepose",
    };

    private void OnLoopCommand(string command, string args)
    {
        var emote = args.Trim();

        // /loop with no args = stop looping
        if (string.IsNullOrEmpty(emote))
        {
            ClearEmoteLoop();
            return;
        }

        // Normalize: add leading slash if missing
        if (!emote.StartsWith("/"))
            emote = "/" + emote;

        // Block state-changing emotes
        if (NoLoopCommands.Contains(emote))
            return;

        // Check if this is a locked emote that needs bypass
        var cmdLookup = emote.ToLowerInvariant();
        if (emoteCommandToId != null && emoteCommandToId.TryGetValue(cmdLookup, out var emoteId)
            && !IsEmoteUnlocked(emoteId) && Configuration.AllowUnlockedEmotes)
        {
            // Set up bypass and loop the carrier
            var (carrierCmd, carrierId, _) = TrySetupEmoteBypass(emote);
            if (carrierCmd != null)
            {
                loopingEmoteCommand = emote;
                loopCarrierCommand = carrierCmd;
                loopCarrierId = carrierId;
                loopingEmoteId = 0;
                emoteTimeline = 0;
                loopWaitingForStart = true;
                loopPreviousWeaponDrawn = ReadIsWeaponDrawn();

                // Wait for Penumbra to process the temp mod before executing
                Task.Run(async () =>
                {
                    await Task.Delay(150);
                    await Framework.RunOnFrameworkThread(() => ExecuteEmoteDirect(carrierCmd));
                });

                return;
            }
            // Bypass failed - fall through to normal (will fail silently via game rejection)
        }

        // Start looping (normal unlocked emote path)
        loopingEmoteCommand = emote;
        loopCarrierCommand = null;
        loopCarrierId = 0;
        loopingEmoteId = 0;
        emoteTimeline = 0;
        loopWaitingForStart = true;
        loopPreviousWeaponDrawn = ReadIsWeaponDrawn();
        ExecuteEmote(emote);
    }

    private void OnAlignCommand(string command, string args)
    {
        Framework.RunOnFrameworkThread(() => AlignToTarget());
    }

    private void OnVanillaCommand(string command, string args)
    {
        var emote = args.Trim();
        if (string.IsNullOrEmpty(emote))
        {
            PrintChat("Usage: /vanilla <emote> (e.g., /vanilla beesknees)");
            return;
        }

        if (!emote.StartsWith("/"))
            emote = "/" + emote;

        // Resolve FFXIV aliases (e.g., /dance5 -> /balldance) so mod matching works
        var lookupKey = emote.TrimStart('/').ToLowerInvariant();
        if (Services.EmoteDetectionService.EmoteToCommand.TryGetValue(lookupKey, out var canonical))
        {
            emote = canonical;
        }

        ExecuteVanilla(emote);
    }

    private void ExecuteVanilla(string emoteCommand)
    {
        if (PenumbraService == null || !PenumbraService.IsAvailable)
        {
            PrintChat("Penumbra is not available!", XivChatType.ErrorMessage);
            return;
        }

        if (isExecutingPreset)
        {
            PrintChat("Please wait - still applying previous preset.", XivChatType.Echo);
            return;
        }

        var (success, collectionId, collectionName) = PenumbraService.GetCurrentCollection();
        if (!success)
        {
            PrintChat("Could not determine current Penumbra collection!", XivChatType.ErrorMessage);
            return;
        }

        Log.Information($"Executing vanilla emote '{emoteCommand}' in collection '{collectionName}'");

        // Snapshot which mods have active temp settings (from previous preset)
        var previousModsWithTemp = new HashSet<string>(Configuration.ModsWithTempSettings);

        // Check if previous preset was a movement mod (need redraw to reload original walk/run animations)
        var previousPreset = Configuration.ActivePresetId != null
            ? Configuration.Presets.Find(p => p.Id == Configuration.ActivePresetId)
            : null;
        var wasMovementPreset = previousPreset?.AnimationType == 6;

        isExecutingPreset = true;

        Task.Run(async () =>
        {
            try
            {
                // Disable mods affecting this emote
                var newModsWithTemp = await DisableModsForVanillaEmote(emoteCommand, collectionId);

                // Clear active preset (vanilla has no preset)
                Configuration.ActivePresetId = null;
                Configuration.ActivePresetCollectionId = collectionId.ToString();

                // Execute the emote - set up bypass first if needed, then execute carrier
                var emoteCmd = emoteCommand; // Capture for closure
                string? bypassCommand = null;
                ushort bypassCarrierId = 0;
                bool bypassTargetIsLoop = false;

                // Phase 1: Clear state, set up bypass
                await Framework.RunOnFrameworkThread(() =>
                {
                    loopingEmoteCommand = null; // Clear any active loop

                    if (wasMovementPreset)
                        ExecuteRedraw();

                    var (cmd, cId, tIsLoop) = TrySetupEmoteBypass(emoteCmd);
                    bypassCommand = cmd;
                    bypassCarrierId = cId;
                    bypassTargetIsLoop = tIsLoop;
                });

                // Wait for Penumbra to process the temp mod / real mod before executing.
                if (bypassCommand != null)
                    await Task.Delay(150);

                // Execute the carrier (bypassed) or original emote
                await Framework.RunOnFrameworkThread(() =>
                {
                    FaceTarget();

                    if (bypassCommand != null)
                    {
                        ExecuteEmoteDirect(bypassCommand);
                        // Swap stays active after emote for sync - cleanup via carrier command detection
                        // in DetourExecuteCommandInner, or next preset/vanilla/reset
                    }
                    else
                    {
                        ExecuteEmote(emoteCmd);
                    }
                });

                // delay during bypass: RemoveTemporaryModSettings disrupts in-flight TMB/PAP loads
                if (bypassCommand != null && previousModsWithTemp.Count > 0)
                    await Task.Delay(500);

                // Restore previous preset's temp settings (empty modDirectory = nothing skipped)
                if (previousModsWithTemp.Count > 0)
                {
                    await RestorePreviousChanges(collectionId, previousModsWithTemp, "", newModsWithTemp);
                }

                Framework.RunOnFrameworkThread(() =>
                {
                    Configuration.Save();
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Error executing vanilla emote: {ex.Message}");
                Framework.RunOnFrameworkThread(() =>
                {
                    PrintChat($"Error executing vanilla emote: {ex.Message}", XivChatType.ErrorMessage);
                });
            }
            finally
            {
                isExecutingPreset = false;
            }
        });
    }

    private async Task<HashSet<string>> DisableModsForVanillaEmote(string emoteCommand, Guid collectionId)
    {
        var disabledMods = new HashSet<string>();

        if (EmoteDetectionService == null || PenumbraService == null)
            return disabledMods;

        var allEmoteMods = EmoteDetectionService.GetEmoteMods();
        var targetCmd = emoteCommand.TrimStart('/').ToLowerInvariant();
        int count = 0;

        foreach (var mod in allEmoteMods)
        {
            // Check if this mod affects the target emote (by command or affected emote name)
            var modCommands = mod.EmoteCommands
                .Select(c => c.TrimStart('/').ToLowerInvariant())
                .ToList();
            var modEmotes = mod.AffectedEmotes
                .Select(e => e.ToLowerInvariant())
                .ToList();

            if (!modCommands.Contains(targetCmd) && !modEmotes.Contains(targetCmd))
                continue;

            // Check permanent state - skip mods that are already permanently disabled
            var (gotSettings, isEnabled, _, _) = PenumbraService.GetCurrentModSettings(collectionId, mod.ModDirectory, mod.ModName);
            if (!gotSettings || !isEnabled)
                continue;

            // Don't disable pinned mods
            if (Configuration.PinnedModDirectories.Contains(mod.ModDirectory))
            {
                Log.Debug($"Mod '{mod.ModName}' is pinned, not disabling for vanilla");
                continue;
            }

            var modKey = $"{collectionId}|{mod.ModDirectory}|{mod.ModName}";

            var result = PenumbraService.SetTemporaryModSettings(collectionId, mod.ModDirectory,
                enabled: false, priority: 0, options: new Dictionary<string, List<string>>(),
                modName: mod.ModName);
            if (result)
            {
                Configuration.ModsWithTempSettings.Add(modKey);
                disabledMods.Add(modKey);
                count++;
                Log.Information($"Temp-disabled '{mod.ModName}' for vanilla {emoteCommand}");
            }

            await Task.Delay(10);
        }

        if (count > 0)
            Log.Information($"Temp-disabled {count} mod(s) for vanilla {emoteCommand}");

        return disabledMods;
    }

    public void ToggleMainUi()
    {
        MainWindow.Toggle();
    }

    public void UpdatePresetCommands()
    {
        // Remove old commands
        foreach (var cmd in registeredPresetCommands)
        {
            try
            {
                CommandManager.RemoveHandler($"/{cmd}");
            }
            catch (Exception ex)
            {
                Log.Debug($"Failed to remove command /{cmd}: {ex.Message}");
            }
        }
        registeredPresetCommands.Clear();

        // Register new commands
        foreach (var preset in Configuration.Presets)
        {
            if (!preset.Enabled || string.IsNullOrWhiteSpace(preset.ChatCommand))
                continue;

            var cmd = preset.ChatCommand.TrimStart('/').ToLower();

            // Skip if command would conflict with system commands
            if (cmd == "encore" || cmd == "encorereset")
                continue;

            // Skip duplicates
            if (registeredPresetCommands.Contains(cmd))
            {
                Log.Warning($"Duplicate command /{cmd} - skipping");
                continue;
            }

            try
            {
                // Capture preset ID for the lambda
                var presetId = preset.Id;

                CommandManager.AddHandler($"/{cmd}", new CommandInfo((c, a) => OnPresetCommand(presetId, a))
                {
                    HelpMessage = $"Encore: {preset.Name}",
                    ShowInHelp = false  // Hide from plugin installer command list
                });

                registeredPresetCommands.Add(cmd);
                Log.Debug($"Registered command /{cmd} for preset '{preset.Name}'");
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to register command /{cmd}: {ex.Message}");
            }
        }

        // routines registered after presets for conflict skipping
        UpdateRoutineCommands();
    }

    private void OnPresetCommand(string presetId, string args)
    {
        var preset = Configuration.Presets.Find(p => p.Id == presetId);
        if (preset == null) return;

        var modifierName = args.Trim();
        if (string.IsNullOrEmpty(modifierName))
        {
            ExecutePreset(preset);
            return;
        }

        var modifier = preset.Modifiers.Find(m =>
            string.Equals(m.Name, modifierName, StringComparison.OrdinalIgnoreCase));
        if (modifier != null)
            ExecutePreset(preset, modifier);
        else
            PrintChat($"Unknown modifier '{modifierName}' for preset '{preset.Name}'.");
    }

    /// <summary>
    /// Execute a dance preset - adjust priorities, disable conflicts, and perform the emote/pose action.
    /// </summary>
    public void ExecutePreset(DancePreset preset, PresetModifier? modifier = null, bool forceBypass = false)
    {
        if (PenumbraService == null || !PenumbraService.IsAvailable)
        {
            PrintChat("Penumbra is not available!", XivChatType.ErrorMessage);
            return;
        }

        if (string.IsNullOrEmpty(preset.ModDirectory))
        {
            if (!preset.IsVanilla)
            {
                PrintChat($"Preset '{preset.Name}' has no target mod configured!", XivChatType.ErrorMessage);
                return;
            }
            // Vanilla preset - continue to conflict disabling
        }

        // If a routine is running and this execution didn't come from the routine itself, cancel it
        if (activeRoutine != null && !isAdvancingRoutine)
            CancelRoutine(reason: "preset played");

        // Prevent overlapping executions
        if (isExecutingPreset)
        {
            PrintChat("Please wait - still applying previous preset.", XivChatType.Echo);
            return;
        }

        // Get current collection
        var (success, collectionId, collectionName) = PenumbraService.GetCurrentCollection();
        if (!success)
        {
            PrintChat("Could not determine current Penumbra collection!", XivChatType.ErrorMessage);
            return;
        }

        Log.Information($"Executing preset '{preset.Name}' in collection '{collectionName}'");

        // Check if this preset is already active in THIS collection (avoid re-boosting priority)
        var isAlreadyActive = Configuration.ActivePresetId == preset.Id &&
                              Configuration.ActivePresetCollectionId == collectionId.ToString();

        // Check if previous preset was a movement mod (need redraw to reload original walk/run animations)
        var previousPreset = Configuration.ActivePresetId != null
            ? Configuration.Presets.Find(p => p.Id == Configuration.ActivePresetId)
            : null;
        var wasMovementPreset = previousPreset?.AnimationType == 6;

        // same-base-emote between different presets: force bypass (carrier emote retriggers animation)
        if (!forceBypass && !isAlreadyActive && !preset.IsVanilla && !preset.EmoteLocked &&
            previousPreset != null && previousPreset.Id != preset.Id &&
            !string.IsNullOrWhiteSpace(previousPreset.EmoteCommand) &&
            !string.IsNullOrWhiteSpace(preset.EmoteCommand) &&
            string.Equals(previousPreset.EmoteCommand, preset.EmoteCommand, StringComparison.OrdinalIgnoreCase) &&
            preset.AnimationType == 1)  // emote type only - poses handle this differently
        {
            Log.Debug($"[ExecutePreset] Same-base-emote transition ('{preset.EmoteCommand}') - forcing bypass");
            forceBypass = true;
        }

        // Snapshot which mods have active temp settings (from previous preset)
        var previousModsWithTemp = new HashSet<string>(Configuration.ModsWithTempSettings);

        // snapshot before Task.Run; isAdvancingRoutine resets synchronously in BeginRoutineStep's finally
        var runningAsRoutineStep = isAdvancingRoutine;

        isExecutingPreset = true;

        Task.Run(async () =>
        {
            try
            {
                HashSet<string> newModsWithTemp = new();

                if (isAlreadyActive)
                {
                    // Same preset - re-apply temp settings (idempotent) to ensure mod stays enabled
                    // even if user manually changed Penumbra settings since activation
                    Log.Debug($"Preset '{preset.Name}' already active, re-applying temp settings");

                    if (!preset.IsVanilla && PenumbraService != null)
                    {
                        ApplyTempSettingsForPresetMod(collectionId, preset, modifier);
                    }
                    TryWireModMusicForBpm(preset, modifier);
                }
                else
                {
                    newModsWithTemp = await ApplyPresetPriorities(preset, collectionId, modifier);

                    Configuration.ActivePresetId = preset.Id;
                    Configuration.ActivePresetCollectionId = collectionId.ToString();
                    // emote-end detection only for AnimationType 1; poses/movement stay sticky
                    activePresetEmoteId = 0;
                    activePresetEmoteSeen = preset.AnimationType != 1;
                    ArmPresetEmoteIgnore(preset);
                    // mod-bundled music: BPM tracker locks to that track
                    // instead of the ambient game BGM.
                    TryWireModMusicForBpm(preset, modifier);
                }

                        Framework.RunOnFrameworkThread(() => ApplyPresetHeels(preset));

                var effectiveEmoteCommand = preset.EmoteCommand;
                var effectiveAnimationType = preset.AnimationType;
                var effectivePoseIndex = preset.PoseIndex;
                if (modifier != null)
                {
                    if (modifier.EmoteCommandOverride != null)
                    {
                        effectiveEmoteCommand = modifier.EmoteCommandOverride;
                        var cmdAnimType = GetAnimationTypeForCommand(modifier.EmoteCommandOverride);
                        if (cmdAnimType != 1)
                        {
                            effectiveAnimationType = cmdAnimType;
                            effectivePoseIndex = modifier.PoseIndexOverride ?? -1;
                        }
                        else
                        {
                            effectiveAnimationType = 1;
                        }
                    }
                    // Pose index override without emote change (same command, different pose number)
                    else if (modifier.PoseIndexOverride.HasValue)
                    {
                        effectivePoseIndex = modifier.PoseIndexOverride.Value;
                    }
                }

                // Track whether EmoteLocked bypass was used (delay restore to avoid animation stutter)
                var bypassUsed = false;

                // movement presets always redraw even if ExecuteEmote=false
                if (preset.ExecuteEmote || preset.AnimationType == 6)
                {
                    var emoteCmd = effectiveEmoteCommand;
                    var animType = effectiveAnimationType;
                    var poseIdx = effectivePoseIndex;
                    var hasModifier = modifier != null;
                    var presetHasModifiers = preset.Modifiers.Count > 0;
                    var baseEmoteCmd = preset.EmoteCommand;

                    // forceBypass: same-base-emote routine transitions reuse this pipeline
                    if ((preset.EmoteLocked || forceBypass) && animType == 1 && !preset.IsVanilla)
                    {
                        string? bypassCommand = null;
                        ushort bypassCarrierId = 0;
                        bool bypassTargetIsLoop = false;

                        // Penumbra IPC returns sync but its mod-resolution cache updates next tick
                        await Task.Delay(300);

                        // Phase 1: Clear state, set up bypass on framework thread
                        await Framework.RunOnFrameworkThread(() =>
                        {
                            loopingEmoteCommand = null;

                            if (wasMovementPreset && !isAlreadyActive)
                                ExecuteRedraw();

                            var result = TrySetupEmoteBypass(emoteCmd, forPreset: true);
                            bypassCommand = result.carrierCommand;
                            bypassCarrierId = result.carrierId;
                            bypassTargetIsLoop = result.targetIsLoop;
                        });

                        if (bypassCommand != null)
                        {
                            // wait for Penumbra to process the mod
                            await Task.Delay(150);

                            await Framework.RunOnFrameworkThread(() =>
                            {
                                FaceTarget();
                                ExecuteEmoteDirect(bypassCommand);

                                // swap stays active for sync; cleared via carrier-command detection
                                // in DetourExecuteCommandInner, or next preset/vanilla/reset
                            });

                            // chat pipeline silently drops emotes near hold-loop re-fires; retry once at 250ms, give up at 1000ms
                            var capturedCarrierId = bypassCarrierId;
                            var capturedCarrierCmd = bypassCommand;
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(250);
                                bool retried = false;
                                await Framework.RunOnFrameworkThread(() =>
                                {
                                    var id = ReadCurrentEmoteId();
                                    var tl = ReadBaseTimeline();
                                    Log.Information($"[BypassCheck] 250ms post-carrier: EmoteId={id} (expected={capturedCarrierId}) TimelineSlot0={tl}");
                                    if (id != capturedCarrierId && !string.IsNullOrEmpty(capturedCarrierCmd))
                                    {
                                        Log.Information($"[BypassCheck] Carrier didn't take - retrying once");
                                        ExecuteEmoteDirect(capturedCarrierCmd!);
                                        retried = true;
                                    }
                                });
                                await Task.Delay(750);
                                await Framework.RunOnFrameworkThread(() =>
                                {
                                    var id = ReadCurrentEmoteId();
                                    var tl = ReadBaseTimeline();
                                    Log.Information($"[BypassCheck] 1000ms post-carrier: EmoteId={id} (expected={capturedCarrierId}) TimelineSlot0={tl}{(retried ? " (after retry)" : "")}");
                                });
                            });

                            // Wait for animation system to fully load TMB/PAP before restoring previous mods.
                            // RemoveTemporaryModSettings changes Penumbra's ChangeCounter which can disrupt
                            // in-flight animation resource loading and cause a visible stutter.
                            bypassUsed = true;
                        }
                        else
                        {
                            // Bypass failed - fall back to normal emote execution (will likely fail for locked emotes)
                            Log.Warning($"[EmoteLocked] Bypass failed for '{emoteCmd}', falling back to normal execution");
                            await Framework.RunOnFrameworkThread(() =>
                            {
                                if (!string.IsNullOrEmpty(emoteCmd))
                                    ExecuteEmote(emoteCmd);
                            });
                        }
                    }
                    else
                    {
                        // Normal execution path (non-locked emotes, poses, movement)
                        Framework.RunOnFrameworkThread(() =>
                        {
                            loopingEmoteCommand = null; // Clear any active loop

                            // Switching away from a movement preset - redraw to reload original walk/run animations
                            // (skip for types that already redraw: StandingIdle=2 and Movement=6)
                            if (wasMovementPreset && !isAlreadyActive && animType != 2 && animType != 6)
                            {
                                ExecuteRedraw();
                            }

                            switch (animType)
                            {
                                case 2: // StandingIdle
                                    if (PoseService != null && poseIdx >= 0)
                                    {
                                        PoseService.SetPoseIndex(EmoteController.PoseType.Idle, (byte)poseIdx);
                                        ExecuteRedraw();
                                        PoseService.CycleCPoseToIndex((byte)poseIdx);
                                    }
                                    else
                                    {
                                        ExecuteRedraw();
                                    }
                                    break;

                                case 3: // ChairSitting
                                case 4: // GroundSitting
                                case 5: // LyingDozing
                                {
                                    // Check if player is currently in a non-standing state (sitting/dozing)
                                    var localPlayer = ObjectTable.LocalPlayer;
                                    var isCurrentlyPosed = false;
                                    if (localPlayer != null)
                                    {
                                        unsafe
                                        {
                                            var character = (Character*)localPlayer.Address;
                                            isCurrentlyPosed = character->Mode != CharacterModes.Normal;
                                        }
                                    }

                                    if (isAlreadyActive && isCurrentlyPosed && PoseService != null && poseIdx >= 0)
                                    {
                                        // Already in pose state - don't re-execute sit/groundsit/doze (would toggle out)
                                        if (presetHasModifiers)
                                            ExecuteRedraw(); // Swap mod files (modifier->base or base->modifier)
                                        // Don't SetPoseIndex first (corrupts CPoseState, making cycling think it's already there)
                                        PoseService.CycleCPoseToIndex((byte)poseIdx);
                                    }
                                    else
                                    {
                                        // Not yet in pose state - do full execution
                                        var poseType = animType switch
                                        {
                                            3 => EmoteController.PoseType.Sit,
                                            4 => EmoteController.PoseType.GroundSit,
                                            5 => EmoteController.PoseType.Doze,
                                            _ => EmoteController.PoseType.Idle
                                        };
                                        if (PoseService != null && poseIdx >= 0)
                                            PoseService.SetPoseIndex(poseType, (byte)poseIdx);

                                        if (animType == 3)
                                        {
                                            if (Configuration.AllowSitDozeAnywhere && PoseService != null)
                                                PoseService.ExecuteSitAnywhere();
                                            else
                                                ExecuteEmote("/sit");
                                        }
                                        else if (animType == 4)
                                        {
                                            ExecuteEmote("/groundsit");
                                        }
                                        else // animType == 5
                                        {
                                            if (Configuration.AllowSitDozeAnywhere && PoseService != null)
                                                PoseService.ExecuteDozeAnywhere();
                                            else
                                                ExecuteEmote("/doze");
                                        }
                                    }
                                }
                                    break;

                                case 6: // Movement - redraw to apply new walk/run animations; optionally execute an emote too
                                    ExecuteRedraw();
                                    if (!string.IsNullOrEmpty(emoteCmd))
                                    {
                                        FaceTarget();
                                        ExecuteEmote(emoteCmd);
                                    }
                                    break;

                                default: // AnimationType 1 (Emote) or unknown
                                    // Modifier with same emote + still in emote mode -> redraw only (seamless option swap mid-dance)
                                    // Modifier with different emote, or stopped, or no modifier -> execute emote
                                    var isInEmoteMode = false;
                                    if (isAlreadyActive && hasModifier &&
                                        string.Equals(emoteCmd, baseEmoteCmd, StringComparison.OrdinalIgnoreCase))
                                    {
                                        unsafe
                                        {
                                            var lp = ObjectTable.LocalPlayer;
                                            if (lp != null)
                                            {
                                                var ch = (Character*)lp.Address;
                                                isInEmoteMode = ch->Mode != CharacterModes.Normal;
                                            }
                                        }
                                    }
                                    if (isInEmoteMode)
                                        ExecuteRedraw();
                                    else if (!string.IsNullOrEmpty(emoteCmd))
                                    {
                                        FaceTarget();
                                        ExecuteEmote(emoteCmd);
                                    }
                                    break;
                            }
                        });
                    }
                }

                // When bypass was used, wait for animation to fully load before restoring previous mods.
                // RemoveTemporaryModSettings changes Penumbra's ChangeCounter which can disrupt
                // in-flight animation resource loading and cause a visible stutter.
                if (bypassUsed && !isAlreadyActive && previousModsWithTemp.Count > 0)
                    await Task.Delay(500);

                // skip mid-routine: bulk-applied mods must stay enabled for later steps
                if (!isAlreadyActive && previousModsWithTemp.Count > 0 && !runningAsRoutineStep)
                {
                    Log.Debug("Restoring previous preset changes in background...");
                    await RestorePreviousChanges(collectionId, previousModsWithTemp, preset.ModDirectory, newModsWithTemp);
                }
                else if (runningAsRoutineStep)
                {
                    Log.Debug("[Routine] Skipping RestorePreviousChanges - preserving bulk-applied routine mods");
                }

                // Save config AFTER both apply and restore are complete
                // This ensures the persisted state reflects the final result
                Framework.RunOnFrameworkThread(() =>
                {
                    Configuration.Save();
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Error executing preset: {ex.Message}");
                Framework.RunOnFrameworkThread(() =>
                {
                    PrintChat($"Error executing preset: {ex.Message}", XivChatType.ErrorMessage);
                });
            }
            finally
            {
                isExecutingPreset = false;
            }
        });
    }

    // ========== Routines ==========

    public void ExecuteRoutine(Routine routine)
    {
        if (routine.Steps.Count == 0)
        {
            Log.Warning($"Routine '{routine.Name}' has no steps.");
            return;
        }

        // Cancel any running routine (the new one replaces it)
        if (activeRoutine != null)
            CancelRoutine(reason: "new routine", announce: false);

        activeRoutine = routine;
        routineCts = new System.Threading.CancellationTokenSource();
        activeRoutineStepIndex = -1;   // BeginStep increments to 0
        routineMovementBaselineSet = false;
        routineBulkAppliedPresetIds.Clear();

        Log.Information($"[Routine] Started: {routine.Name}");

        // Bulk-apply non-conflicting preset priorities upfront so step transitions only need to
        // fire an emote (no Penumbra churn mid-routine). A preset is "non-conflicting" when it's
        // the only mod in the routine that targets its EmoteCommand.
        BulkApplyRoutinePriorities(routine);

        BeginRoutineStep();
    }

    private void BulkApplyRoutinePriorities(Routine routine)
    {
        if (PenumbraService == null || !PenumbraService.IsAvailable) return;

        var (ok, collectionId, _) = PenumbraService.GetCurrentCollection();
        if (!ok) return;

        // Gather all non-vanilla, non-macro preset steps with valid mods
        var presetSteps = routine.Steps
            .Where(s => !s.IsMacroStep)
            .Select(s => Configuration.Presets.Find(p => p.Id == s.PresetId))
            .Where(p => p != null && !p.IsVanilla && !string.IsNullOrWhiteSpace(p.ModDirectory))
            .Select(p => p!)
            .ToList();

        // Group by EmoteCommand - presets with UNIQUE emotes within the routine are safe to bulk-apply.
        // Presets sharing an emote with others stay per-step so dynamic priority swaps and bypass can handle them.
        var byEmote = presetSteps
            .GroupBy(p => (p.EmoteCommand ?? "").ToLowerInvariant())
            .ToList();

        var toBulkApply = new List<DancePreset>();
        foreach (var group in byEmote)
        {
            var distinctMods = group.Select(p => p.ModDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (distinctMods == 1)
            {
                // All steps using this emote use the same mod - one preset's state suffices
                toBulkApply.Add(group.First());
            }
            // If >1 distinct mods share the emote, fall back to per-step execution (handles priority swap / bypass)
        }

        if (toBulkApply.Count == 0) return;

        Log.Information($"[Routine] Bulk-applying {toBulkApply.Count} non-conflicting preset(s) at routine start");

        // add to routineBulkAppliedPresetIds only after apply completes
        // (early add would let step 0 fire before Penumbra knows the new priorities)
        // ApplyPresetPriorities already calls DisableConflictingMods internally
        var routineSnapshot = activeRoutine;
        Task.Run(async () =>
        {
            try
            {
                foreach (var preset in toBulkApply)
                {
                    if (activeRoutine != routineSnapshot) return;
                    await ApplyPresetPriorities(preset, collectionId);
                    routineBulkAppliedPresetIds.Add(preset.Id);
                }
            }
            catch (Exception ex) { Log.Debug($"[Routine] Bulk-apply error: {ex.Message}"); }
        });
    }

    private void BeginRoutineStep()
    {
        if (activeRoutine == null) return;

        var prevStepIndex = activeRoutineStepIndex;
        var prevStep = prevStepIndex >= 0 && prevStepIndex < activeRoutine.Steps.Count
            ? activeRoutine.Steps[prevStepIndex] : null;
        var prevHadExpression = prevStep != null && !string.IsNullOrWhiteSpace(prevStep.LayeredEmote);
        string? prevEmoteCommand = null;
        if (prevStep != null && !prevStep.IsMacroStep)
        {
            var prevPreset = Configuration.Presets.Find(p => p.Id == prevStep.PresetId);
            if (prevPreset != null)
            {
                var prevModifier = !string.IsNullOrWhiteSpace(prevStep.ModifierName)
                    ? prevPreset.Modifiers.FirstOrDefault(m => string.Equals(m.Name, prevStep.ModifierName, StringComparison.OrdinalIgnoreCase))
                    : null;
                prevEmoteCommand = prevModifier?.EmoteCommandOverride ?? prevPreset.EmoteCommand;
            }
        }

        activeRoutineStepIndex++;
        if (activeRoutineStepIndex >= activeRoutine.Steps.Count)
        {
            if (activeRoutine.RepeatLoop)
            {
                activeRoutineStepIndex = 0;
            }
            else
            {
                Log.Information($"[Routine] Finished: {activeRoutine.Name}");
                var finishedHadExpression = activeRoutine.Steps.Any(s => !string.IsNullOrWhiteSpace(s.LayeredEmote));
                activeRoutine = null;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(150);
                        if (finishedHadExpression)
                            await Framework.RunOnFrameworkThread(() => ExecuteEmoteDirect("/straightface"));
                        await Task.Delay(350);
                        await Framework.RunOnFrameworkThread(() => StopActiveEmoteAnimation());
                    }
                    catch (Exception ex) { Log.Debug($"[Routine] Finish cleanup error: {ex.Message}"); }
                });
                return;
            }
        }

        var step = activeRoutine.Steps[activeRoutineStepIndex];
        DancePreset? preset = null;
        if (!step.IsMacroStep)
        {
            preset = Configuration.Presets.Find(p => p.Id == step.PresetId);
            if (preset == null)
            {
                Log.Warning($"[Routine] Step {activeRoutineStepIndex} references missing preset - skipping");
                BeginRoutineStep();
                return;
            }
        }

        // /straightface clears leftover expression; skip on same-base-emote (interferes with bypass handoff)
        var willUseBypass = !step.IsMacroStep && preset != null &&
                            !string.IsNullOrWhiteSpace(prevEmoteCommand) &&
                            !string.IsNullOrWhiteSpace(preset.EmoteCommand) &&
                            string.Equals(prevEmoteCommand, preset.EmoteCommand, StringComparison.OrdinalIgnoreCase);
        if (prevHadExpression && string.IsNullOrWhiteSpace(step.LayeredEmote) && !willUseBypass)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150);
                    await Framework.RunOnFrameworkThread(() => ExecuteEmoteDirect("/straightface"));
                }
                catch (Exception ex) { Log.Debug($"[Routine] /straightface clear error: {ex.Message}"); }
            });
        }

        activeRoutineStepStartedUtc = DateTime.UtcNow;
        routineStepEmoteId = 0;
        routineWaitingForEmoteStart = true;
        routineMacroCompleted = false;

        // Apply Simple Heels for this step. Per-step override (HeelsOverride) wins; otherwise falls
        // back to the preset's own offset. Cleared if neither. Macro steps get cleared too since there's
        // no preset to source from unless the step itself specifies an override.
        if (step.IsMacroStep)
        {
            if (SimpleHeelsService != null && SimpleHeelsService.IsAvailable)
            {
                var h = step.HeelsOverride;
                if (h != null && !h.IsZero())
                    SimpleHeelsService.ApplyOffset(h.X, h.Y, h.Z, h.Rotation, h.Pitch, h.Roll);
                else
                    SimpleHeelsService.ClearOffset();
            }
        }
        else if (preset != null)
        {
            ApplyPresetHeels(preset, step.HeelsOverride);
        }

        if (step.IsMacroStep)
        {
            // Run the macro text in the background, line-by-line, honoring /wait <seconds>.
            // Cancellation tied to the routine CTS so step transitions stop in-flight macros.
            var macroText = step.MacroText;
            var routineSnapshotM = activeRoutine;
            var stepSnapshotM = activeRoutineStepIndex;
            var ctM = routineCts?.Token ?? System.Threading.CancellationToken.None;
            Task.Run(async () =>
            {
                try { await RunMacroAsync(macroText, routineSnapshotM, stepSnapshotM, ctM); }
                catch (TaskCanceledException) { /* normal */ }
                catch (Exception ex) { Log.Debug($"[Routine] Macro error: {ex.Message}"); }
            });
        }
        else
        {
            // Resolve optional step modifier - overrides the preset's default emote/options.
            PresetModifier? stepModifier = null;
            if (!string.IsNullOrWhiteSpace(step.ModifierName))
            {
                stepModifier = preset!.Modifiers.FirstOrDefault(m =>
                    string.Equals(m.Name, step.ModifierName, StringComparison.OrdinalIgnoreCase));
                if (stepModifier == null)
                    Log.Warning($"[Routine] Step {activeRoutineStepIndex} references modifier '{step.ModifierName}' not found on preset '{preset.Name}'");
            }

            // Effective emote command accounts for modifier override when picking the transition path.
            var effectiveStepEmote = stepModifier?.EmoteCommandOverride ?? preset!.EmoteCommand;

            // If the previous step shared this preset's base emote, clear the emote state first so
            // the game re-triggers the animation with the new mod (otherwise it won't reload anim).
            var sameBaseEmote = !string.IsNullOrWhiteSpace(prevEmoteCommand) &&
                                !string.IsNullOrWhiteSpace(effectiveStepEmote) &&
                                string.Equals(prevEmoteCommand, effectiveStepEmote, StringComparison.OrdinalIgnoreCase);
            Log.Information($"[Routine] step {activeRoutineStepIndex} transition: prevEmoteCmd='{prevEmoteCommand}' newEmoteCmd='{effectiveStepEmote}' sameBaseEmote={sameBaseEmote} modifier='{stepModifier?.Name ?? "(none)"}'");

            // fast path requires: bulk-applied + no shared emote with prev + no modifier + not locked
            bool presetEmoteIsLocked = false;
            if (preset != null && preset.AnimationType == 1 && !preset.IsVanilla
                && !string.IsNullOrWhiteSpace(preset.EmoteCommand)
                && emoteCommandToId != null
                && emoteCommandToId.TryGetValue(preset.EmoteCommand, out var _presetEmoteIdForLockCheck))
            {
                try { presetEmoteIsLocked = !IsEmoteUnlocked(_presetEmoteIdForLockCheck); }
                catch { presetEmoteIsLocked = false; }
            }
            var requiresBypass = preset != null && (preset.EmoteLocked || presetEmoteIsLocked);

            var bulkFastPath = routineBulkAppliedPresetIds.Contains(preset!.Id)
                               && !sameBaseEmote
                               && stepModifier == null
                               && !requiresBypass;

            if (bulkFastPath)
            {
                var fastEmote = preset.EmoteCommand;
                if (!string.IsNullOrWhiteSpace(fastEmote))
                {
                    Framework.RunOnFrameworkThread(() =>
                    {
                        FaceTarget();
                        ExecuteEmote(fastEmote);
                    });
                }
                Configuration.ActivePresetId = preset.Id;
                activePresetEmoteId = 0;
                activePresetEmoteSeen = preset.AnimationType != 1;
                ArmPresetEmoteIgnore(preset);
            }
            else if (sameBaseEmote)
            {
                // Reuse the emote-bypass pipeline. Before firing the bypass we give the game ~1.5s
                // to settle from the previous step's hold-loop emote activity - the game's emote
                // input pipeline drops rapid consecutive commands, which silently rejects the carrier.
                var presetSnapshot = preset!;
                var modifierSnapshot = stepModifier;
                var routineSnapshotE = activeRoutine;
                var stepSnapshotE = activeRoutineStepIndex;
                var ctE = routineCts?.Token ?? System.Threading.CancellationToken.None;
                Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1500, ctE);
                        if (ctE.IsCancellationRequested ||
                            activeRoutine != routineSnapshotE || activeRoutineStepIndex != stepSnapshotE) return;
                        await Framework.RunOnFrameworkThread(() =>
                        {
                            isAdvancingRoutine = true;
                            try { ExecutePreset(presetSnapshot, modifierSnapshot, forceBypass: true); }
                            finally { isAdvancingRoutine = false; }
                        });
                    }
                    catch (TaskCanceledException) { /* normal */ }
                    catch (Exception ex) { Log.Debug($"[Routine] Same-emote transition error: {ex.Message}"); }
                });
            }
            else
            {
                isAdvancingRoutine = true;
                try { ExecutePreset(preset!, stepModifier, forceBypass: requiresBypass); }
                finally { isAdvancingRoutine = false; }
            }
        }

        if (!string.IsNullOrWhiteSpace(step.LayeredEmote))
        {
            var expression = step.LayeredEmote.StartsWith("/") ? step.LayeredEmote : "/" + step.LayeredEmote;
            var hold = step.HoldExpression;
            // 1.5s minimum hold delay so dance establishes before expression fires
            var initialDelay = hold ? Math.Max(step.LayerDelaySeconds, 1.5f) : Math.Max(0f, step.LayerDelaySeconds);
            var presetEmote = preset?.EmoteCommand ?? "";
            var routineSnapshot = activeRoutine;
            var stepSnapshot = activeRoutineStepIndex;
            var ct = routineCts?.Token ?? System.Threading.CancellationToken.None;
            const float HoldRefireIntervalSeconds = 4f;
            const int RestoreDelayMs = 200;
            Task.Run(async () =>
            {
                try
                {
                    if (initialDelay > 0)
                        await Task.Delay(TimeSpan.FromSeconds(initialDelay), ct);
                    if (ct.IsCancellationRequested ||
                        activeRoutine != routineSnapshot || activeRoutineStepIndex != stepSnapshot)
                        return;

                    await Framework.RunOnFrameworkThread(() => ExecuteEmote(expression));

                    if (!hold) return;

                    if (!string.IsNullOrWhiteSpace(presetEmote))
                    {
                        await Task.Delay(RestoreDelayMs, ct);
                        if (ct.IsCancellationRequested ||
                            activeRoutine != routineSnapshot || activeRoutineStepIndex != stepSnapshot)
                            return;
                        await Framework.RunOnFrameworkThread(() => ExecuteEmote(presetEmote));
                    }

                    while (!ct.IsCancellationRequested &&
                           activeRoutine == routineSnapshot && activeRoutineStepIndex == stepSnapshot)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(HoldRefireIntervalSeconds), ct);
                        if (ct.IsCancellationRequested ||
                            activeRoutine != routineSnapshot || activeRoutineStepIndex != stepSnapshot) break;
                        Log.Debug($"[Routine] hold re-fire: expression={expression}");
                        await Framework.RunOnFrameworkThread(() => ExecuteEmote(expression));

                        if (!string.IsNullOrWhiteSpace(presetEmote))
                        {
                            await Task.Delay(RestoreDelayMs, ct);
                            if (ct.IsCancellationRequested ||
                                activeRoutine != routineSnapshot || activeRoutineStepIndex != stepSnapshot) break;
                            await Framework.RunOnFrameworkThread(() => ExecuteEmote(presetEmote));
                        }
                    }
                    Log.Debug($"[Routine] hold loop exited: ct.cancelled={ct.IsCancellationRequested} routineMatch={activeRoutine == routineSnapshot} stepMatch={activeRoutineStepIndex == stepSnapshot}");
                }
                catch (TaskCanceledException) { /* normal: routine cancelled */ }
                catch (Exception ex)
                {
                    Log.Debug($"[Routine] Expression schedule error: {ex.Message}");
                }
            });
        }
    }

    // /wait N pauses for N seconds; sets routineMacroCompleted=true on natural finish
    private async Task RunMacroAsync(string macroText, Routine routineSnapshot, int stepSnapshot,
                                     System.Threading.CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(macroText))
        {
            // Empty macro = "instantly done" so Until-macro-ends still advances
            routineMacroCompleted = true;
            return;
        }

        var lines = macroText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            if (ct.IsCancellationRequested) return;
            if (activeRoutine != routineSnapshot || activeRoutineStepIndex != stepSnapshot) return;

            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var waitSecs) &&
                    waitSecs > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(waitSecs), ct);
                }
                continue;
            }

            await Framework.RunOnFrameworkThread(() => ExecuteEmote(line));
            await Task.Delay(50, ct);
        }

        // Natural completion - only signal if we're still on this step (don't fire after a transition)
        if (activeRoutine == routineSnapshot && activeRoutineStepIndex == stepSnapshot)
        {
            routineMacroCompletedAt = DateTime.UtcNow;
            routineMacroCompleted = true;
        }
    }

    public string? ActiveRoutineName => activeRoutine?.Name;
    public bool IsRoutineActive(string routineId) => activeRoutine?.Id == routineId;
    /// <summary>0-based step index currently executing, or -1 when no active routine.</summary>
    public int ActiveRoutineStepIndex => activeRoutine != null ? activeRoutineStepIndex : -1;
    /// <summary>Total steps in the active routine, or 0 when none.</summary>
    public int ActiveRoutineStepCount => activeRoutine?.Steps.Count ?? 0;

    public void CancelRoutine(string reason = "", bool announce = true)
    {
        if (activeRoutine == null) return;
        var name = activeRoutine.Name;
        // If any step had a layered expression, clear the face on stop with /straightface
        var hadExpression = activeRoutine.Steps.Any(s => !string.IsNullOrWhiteSpace(s.LayeredEmote));

        activeRoutine = null;
        activeRoutineStepIndex = 0;
        routineStepEmoteId = 0;
        routineWaitingForEmoteStart = false;
        routineMovementBaselineSet = false;
        // Cancel any pending expression-hold loops immediately
        try { routineCts?.Cancel(); routineCts?.Dispose(); } catch (Exception ex) { Log.Debug($"[Routine] CTS cancel error: {ex.Message}"); }
        routineCts = null;

        if (announce)
        {
            var suffix = string.IsNullOrEmpty(reason) ? "" : $" ({reason})";
            Log.Information($"[Routine] Stopped: {name}{suffix}");
        }

        // ORDER: /straightface (150ms) before jump (500ms); reversed, jump interrupts /straightface mid-apply
        if (reason != "moved")
        {
            Task.Run(async () =>
            {
                try
                {
                    // Let any in-flight hold-loop re-fire settle
                    await Task.Delay(150);
                    if (hadExpression)
                        await Framework.RunOnFrameworkThread(() => ExecuteEmoteDirect("/straightface"));
                    // Give the face expression time to register before the jump cancels the body
                    await Task.Delay(350);
                    await Framework.RunOnFrameworkThread(() => StopActiveEmoteAnimation());
                }
                catch (Exception ex) { Log.Debug($"[Routine] Cancel cleanup error: {ex.Message}"); }
            });
        }
        else if (hadExpression)
        {
            // Moved-to-stop: body already interrupted by movement, just clear the face.
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(150);
                    await Framework.RunOnFrameworkThread(() => ExecuteEmoteDirect("/straightface"));
                }
                catch (Exception ex) { Log.Debug($"[Routine] /straightface clear error: {ex.Message}"); }
            });
        }
    }

    // mirrors CancelRoutine: does NOT restore mod priorities (use /encorereset) and does NOT chat-message
    public void StopActivePreset()
    {
        if (string.IsNullOrEmpty(Configuration.ActivePresetId)) return;
        loopingEmoteCommand = null;
        Configuration.ActivePresetId = null;
        Configuration.ActivePresetCollectionId = null;
        Configuration.Save();
        SimpleHeelsService?.ClearOffset();
        Task.Run(async () =>
        {
            try
            {
                await Framework.RunOnFrameworkThread(() => StopActiveEmoteAnimation());
            }
            catch (Exception ex) { Log.Debug($"[Preset] Stop error: {ex.Message}"); }
        });
    }

    // ActionManager.UseAction(GeneralAction, 2) = jump; cleanly cancels looping emote/dance
    private unsafe void StopActiveEmoteAnimation()
    {
        try
        {
            var actionManager = FFXIVClientStructs.FFXIV.Client.Game.ActionManager.Instance();
            if (actionManager == null) return;
            // GeneralAction 2 = Jump
            actionManager->UseAction(FFXIVClientStructs.FFXIV.Client.Game.ActionType.GeneralAction, 2);
            Log.Debug("[Routine] Triggered jump to interrupt emote");
        }
        catch (Exception ex)
        {
            Log.Debug($"[Routine] StopActiveEmoteAnimation error: {ex.Message}");
        }
    }

    private void UpdateActiveRoutine(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter localPlayer)
    {
        if (activeRoutine == null) return;
        if (activeRoutineStepIndex < 0 || activeRoutineStepIndex >= activeRoutine.Steps.Count) return;

        var step = activeRoutine.Steps[activeRoutineStepIndex];

        // Movement detection - sensitive to any real player movement (single key tap).
        // routinePrevPosition is the BASELINE captured at routine start, NOT the previous frame.
        // Real movement displaces the character significantly faster than emote animation drift.
        if (!routineMovementBaselineSet)
        {
            routinePrevPosition = localPlayer.Position;
            routineMovementBaselineSet = true;
        }
        else
        {
            var dx = localPlayer.Position.X - routinePrevPosition.X;
            var dz = localPlayer.Position.Z - routinePrevPosition.Z;
            // Squared-distance threshold of 0.04 ~ 0.2 unit displacement from baseline.
            // Emote animations drift well below this; even a single movement key press exceeds it.
            if (dx * dx + dz * dz > 0.04f)
            {
                CancelRoutine(reason: "moved");
                return;
            }
        }

        // Advance based on the step's duration kind
        switch (step.DurationKind)
        {
            case RoutineStepDuration.Forever:
                // Never advance
                break;

            case RoutineStepDuration.Fixed:
                {
                    var elapsed = (DateTime.UtcNow - activeRoutineStepStartedUtc).TotalSeconds;
                    if (elapsed >= step.DurationSeconds)
                        BeginRoutineStep();
                }
                break;

            case RoutineStepDuration.UntilLoopEnds:
                {
                    // For macro steps, this means "until macro completes + optional trailing seconds".
                    // step.DurationSeconds here is the post-macro buffer (0 = advance immediately).
                    if (step.IsMacroStep)
                    {
                        if (routineMacroCompleted)
                        {
                            var trailing = Math.Max(0f, step.DurationSeconds);
                            var elapsedSinceFinish = (DateTime.UtcNow - routineMacroCompletedAt).TotalSeconds;
                            if (elapsedSinceFinish >= trailing)
                                BeginRoutineStep();
                        }
                        break;
                    }

                    // "Until emote ends" - works for one-shot emotes (bow, cheer, psych, etc).
                    // For looping emotes (dances), the emote plays forever - use Fixed time instead.
                    var currentEmoteId = ReadCurrentEmoteId();
                    var currentTimeline = ReadBaseTimeline();
                    var stepElapsed = (DateTime.UtcNow - activeRoutineStepStartedUtc).TotalSeconds;

                    // Capture the first non-zero emote as this step's reference
                    if (routineWaitingForEmoteStart && currentEmoteId != 0)
                    {
                        routineStepEmoteId = currentEmoteId;
                        routineStepTimeline = currentTimeline;
                        routineWaitingForEmoteStart = false;
                    }
                    // Settling window (1.5s): some emotes split intro/loop into different emote IDs
                    // (e.g., /golddance 108->119). Keep rebaselining so the settled reference is the loop.
                    else if (routineStepEmoteId != 0 && stepElapsed < 1.5 && currentEmoteId != 0 &&
                             currentEmoteId != routineStepEmoteId)
                    {
                        routineStepEmoteId = currentEmoteId;
                        routineStepTimeline = currentTimeline;
                    }

                    // Safety net - if no emote has started by 3s (pose/movement step), advance anyway
                    if (routineStepEmoteId == 0 && stepElapsed >= 3.0)
                    {
                        BeginRoutineStep();
                        break;
                    }

                    // End detection runs after the settling window
                    if (routineStepEmoteId != 0 && stepElapsed >= 1.5)
                    {
                        // Timeline still inside this emote's row-ID set -> it's still playing (loop, intro, etc.)
                        bool timelineInsideEmote = false;
                        if (currentEmoteId == routineStepEmoteId &&
                            emoteIdToTimelineRowIds != null &&
                            emoteIdToTimelineRowIds.TryGetValue(routineStepEmoteId, out var validIds))
                        {
                            timelineInsideEmote = validIds.Contains(currentTimeline);
                        }

                        // Emote has truly ended when: id reverts to 0, OR shifts to a different emote entirely
                        bool emoteFinished =
                            currentEmoteId == 0 ||
                            (currentEmoteId != routineStepEmoteId && !timelineInsideEmote);

                        if (emoteFinished)
                        {
                            BeginRoutineStep();
                            break;
                        }
                    }

                    routinePrevEmoteId = currentEmoteId;
                    routinePrevTimeline = currentTimeline;
                }
                break;
        }
    }

    public void UpdateRoutineCommands()
    {
        foreach (var cmd in registeredRoutineCommands)
        {
            try { CommandManager.RemoveHandler($"/{cmd}"); }
            catch (Exception ex) { Log.Debug($"Failed to remove routine command /{cmd}: {ex.Message}"); }
        }
        registeredRoutineCommands.Clear();

        foreach (var routine in Configuration.Routines)
        {
            if (!routine.Enabled || string.IsNullOrWhiteSpace(routine.ChatCommand))
                continue;

            var cmd = routine.ChatCommand.TrimStart('/').ToLower();
            if (cmd == "encore" || cmd == "encorereset")
                continue;

            if (registeredPresetCommands.Contains(cmd) || registeredRoutineCommands.Contains(cmd))
                continue;

            try
            {
                var routineId = routine.Id;
                CommandManager.AddHandler($"/{cmd}", new CommandInfo((c, a) =>
                {
                    var r = Configuration.Routines.Find(x => x.Id == routineId);
                    if (r != null) ExecuteRoutine(r);
                })
                {
                    HelpMessage = $"Encore routine: {routine.Name}",
                    ShowInHelp = false
                });
                registeredRoutineCommands.Add(cmd);
            }
            catch (Exception ex)
            {
                Log.Warning($"Failed to register routine command /{cmd}: {ex.Message}");
            }
        }
    }

    private async Task RestorePreviousChanges(Guid collectionId,
        HashSet<string> previousModsWithTemp,
        string currentPresetModDirectory,
        HashSet<string> newModsWithTemp)
    {
        int restored = 0;

        foreach (var key in previousModsWithTemp)
        {
            var parts = key.Split('|');
            if (parts.Length < 2) continue;
            if (!Guid.TryParse(parts[0], out var storedCollectionId)) continue;
            if (storedCollectionId != collectionId) continue;

            var modDirectory = parts[1];
            var modName = parts.Length >= 3 ? parts[2] : "";

            // Skip if this is the current preset's mod
            if (!string.IsNullOrEmpty(currentPresetModDirectory) &&
                string.Equals(modDirectory, currentPresetModDirectory, StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug($"Skipping restore for '{modDirectory}' - it's the current preset's mod");
                continue;
            }

            // Skip if new preset still needs temp settings on this mod
            if (newModsWithTemp.Contains(key))
            {
                Log.Debug($"Skipping restore for '{modDirectory}' - new preset still needs it");
                continue;
            }

            // Remove temp settings -> auto-reverts to permanent/inherited state
            if (PenumbraService!.RemoveTemporaryModSettings(storedCollectionId, modDirectory, modName))
            {
                restored++;
                Log.Debug($"Removed temp settings for '{modDirectory}'");
            }
            Configuration.ModsWithTempSettings.Remove(key);
            await Task.Delay(5);
        }

        Log.Debug($"Previous preset changes restored ({restored} mods)");
    }

    private async Task<HashSet<string>> ApplyPresetPriorities(DancePreset preset, Guid collectionId, PresetModifier? modifier = null)
    {
        Log.Information($"Applying preset '{preset.Name}' (mod: {preset.ModName}, vanilla: {preset.IsVanilla})");

        var newModsWithTemp = new HashSet<string>();

        // Vanilla preset - skip mod enable/priority, just disable conflicts
        if (preset.IsVanilla)
        {
            Log.Information($"Vanilla preset - skipping mod enable/priority, only disabling conflicts");
            var vanillaConflicts = await DisableConflictingMods(preset, collectionId);
            foreach (var key in vanillaConflicts)
                newModsWithTemp.Add(key);
            return newModsWithTemp;
        }

        // Apply temp settings for the preset's mod (enable + priority + options in one call)
        ApplyTempSettingsForPresetMod(collectionId, preset, modifier);
        var modKey = $"{collectionId}|{preset.ModDirectory}|{preset.ModName}";
        newModsWithTemp.Add(modKey);

        // Disable conflicting mods via temp settings
        var conflictsDisabled = await DisableConflictingMods(preset, collectionId);
        foreach (var key in conflictsDisabled)
            newModsWithTemp.Add(key);

        return newModsWithTemp;
    }

    // Returns set of mod keys that were temp-disabled by this call
    private async Task<HashSet<string>> DisableConflictingMods(DancePreset preset, Guid collectionId)
    {
        var disabledMods = new HashSet<string>();

        if (EmoteDetectionService == null)
            return disabledMods;

        // Get the emotes affected by the preset's mod
        var presetModInfo = EmoteDetectionService.AnalyzeMod(preset.ModDirectory, preset.ModName);

        // Build the set of emotes to check for conflicts
        var presetEmotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presetEmoteCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Build exclusion sets from the preset's ConflictExclusions list.
        // Each entry is an emote command (e.g., "/beesknees", "/cpose"). Normalized to no slash, lowercase.
        var excludedCommands = new HashSet<string>(
            preset.ConflictExclusions.Select(c => c.TrimStart('/').ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
        // Derive the matching emote names by reverse-looking-up EmoteToCommand, so we can also filter AffectedEmotes.
        var excludedEmotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, cmd) in EmoteDetectionService.EmoteToCommand)
        {
            if (excludedCommands.Contains(cmd.TrimStart('/').ToLowerInvariant()))
                excludedEmotes.Add(name);
        }
        // Pose-type commands map to AffectedEmotes tags.
        foreach (var cmd in excludedCommands)
        {
            var tag = cmd switch
            {
                "cpose" => "idle",
                "sit" => "sit",
                "groundsit" => "groundsit",
                "doze" => "doze",
                _ => null
            };
            if (tag != null)
                excludedEmotes.Add(tag);
        }

        // Add emotes from mod analysis if available (filtered by exclusions)
        if (presetModInfo != null && presetModInfo.AffectedEmotes.Count > 0)
        {
            foreach (var emote in presetModInfo.AffectedEmotes)
            {
                if (excludedEmotes.Contains(emote))
                    continue;
                presetEmotes.Add(emote);
            }
            foreach (var cmd in presetModInfo.EmoteCommands)
            {
                var normalized = cmd.TrimStart('/').ToLowerInvariant();
                if (excludedCommands.Contains(normalized))
                    continue;
                presetEmoteCommands.Add(normalized);
            }
        }

        // Fallback: use the preset's configured emote command if we don't have emotes from analysis
        if (presetEmotes.Count == 0 && !string.IsNullOrEmpty(preset.EmoteCommand))
        {
            var emoteCmd = preset.EmoteCommand.TrimStart('/').ToLowerInvariant();
            presetEmoteCommands.Add(emoteCmd);
            presetEmotes.Add(emoteCmd);
            Log.Debug($"Using preset's configured emote command as fallback: {emoteCmd}");
        }

        if (presetEmotes.Count == 0 && presetEmoteCommands.Count == 0)
        {
            Log.Debug($"No affected emotes found for preset '{preset.Name}', skipping conflict check");
            return disabledMods;
        }

        Log.Debug($"Preset '{preset.Name}' affects emotes: [{string.Join(", ", presetEmotes)}], commands: [{string.Join(", ", presetEmoteCommands)}]");

        // Get all emote mods from cache
        var allEmoteMods = EmoteDetectionService.GetEmoteMods();
        int conflictsDisabled = 0;

        foreach (var mod in allEmoteMods)
        {
            // Skip the preset's own mod (unless vanilla preset which has no mod)
            if (!preset.IsVanilla && string.Equals(mod.ModDirectory, preset.ModDirectory, StringComparison.OrdinalIgnoreCase))
                continue;

            // Check if this mod affects any of the same emotes (by name or command)
            var sharedEmotes = mod.AffectedEmotes.Where(e => presetEmotes.Contains(e)).ToList();
            var sharedCommands = mod.EmoteCommands
                .Select(c => c.TrimStart('/').ToLowerInvariant())
                .Where(c => presetEmoteCommands.Contains(c))
                .ToList();

            if (sharedEmotes.Count == 0 && sharedCommands.Count == 0)
                continue;

            // For pose presets, only conflict with mods targeting the same pose number
            if (preset.AnimationType >= 2 && preset.AnimationType <= 5 && preset.PoseIndex >= 0)
            {
                if (mod.AnimationType == (EmoteDetectionService.AnimationType)preset.AnimationType)
                {
                    bool modAffectsTargetPose;
                    if (mod.AffectedPoseIndices != null && mod.AffectedPoseIndices.Count > 0)
                        modAffectsTargetPose = mod.AffectedPoseIndices.Contains(preset.PoseIndex);
                    else
                        modAffectsTargetPose = mod.PoseIndex < 0 || mod.PoseIndex == preset.PoseIndex;

                    if (!modAffectsTargetPose)
                    {
                        Log.Debug($"Mod '{mod.ModName}' targets pose(s) [{string.Join(",", mod.AffectedPoseIndices ?? new List<int>())}] (PoseIndex={mod.PoseIndex}), preset targets {preset.PoseIndex} - not a conflict");
                        continue;
                    }
                }
            }

            var sharedDescription = string.Join(", ",
                sharedEmotes.Concat(sharedCommands.Select(c => $"/{c}")).Distinct());

            // Check permanent state - GetCurrentModSettings ignores temp overrides
            var (gotSettings, isEnabled, _, _) = PenumbraService!.GetCurrentModSettings(collectionId, mod.ModDirectory, mod.ModName);
            // Also consider mods currently temp-enabled by a previous preset - they're active now
            // even if permanently disabled, and still compete with this preset for the same emote.
            var modKey = $"{collectionId}|{mod.ModDirectory}|{mod.ModName}";
            var hasTempSettings = Configuration.ModsWithTempSettings.Contains(modKey);

            // Not in collection, permanently disabled, AND not temp-enabled -> nothing to do
            if ((!gotSettings || !isEnabled) && !hasTempSettings)
                continue;

            // Don't disable pinned mods
            if (Configuration.PinnedModDirectories.Contains(mod.ModDirectory))
            {
                Log.Debug($"Mod '{mod.ModName}' is pinned, not disabling");
                continue;
            }

            // Temp-disable the conflicting mod (modKey already built above for temp-settings check)
            var result = PenumbraService.SetTemporaryModSettings(collectionId, mod.ModDirectory,
                enabled: false, priority: 0, options: new Dictionary<string, List<string>>(),
                modName: mod.ModName);
            if (result)
            {
                Configuration.ModsWithTempSettings.Add(modKey);
                disabledMods.Add(modKey);
                conflictsDisabled++;
                Log.Information($"Temp-disabled conflicting mod '{mod.ModName}' (shares: {sharedDescription})");
            }
            else
            {
                Log.Warning($"Failed to temp-disable conflicting mod '{mod.ModName}'");
            }

            await Task.Delay(10);
        }

        if (conflictsDisabled > 0)
        {
            Log.Information($"Temp-disabled {conflictsDisabled} conflicting mod(s)");
        }

        return disabledMods;
    }

    // Apply the effective Simple Heels offset for a preset, honoring an optional routine-step override.
    // Silently no-ops when SimpleHeels isn't installed. Null/zero offsets clear any existing override
    // so the previous preset's heels don't leak into the next one.
    private void ApplyPresetHeels(DancePreset preset, HeelsOffset? routineOverride = null)
    {
        if (SimpleHeelsService == null || !SimpleHeelsService.IsAvailable) return;
        var heels = routineOverride ?? preset.HeelsOffset;
        if (heels != null && !heels.IsZero())
            SimpleHeelsService.ApplyOffset(heels.X, heels.Y, heels.Z, heels.Rotation, heels.Pitch, heels.Roll);
        else
            SimpleHeelsService.ClearOffset();
    }

    // Restore Simple Heels to whatever the currently active preset expects. Used by the editor
    // when the user closes the window or collapses the heels section - any preview offset we were
    // showing should roll back to the real active state.
    public void RefreshActivePresetHeels()
    {
        if (SimpleHeelsService == null || !SimpleHeelsService.IsAvailable) return;
        if (Configuration.ActivePresetId == null)
        {
            SimpleHeelsService.ClearOffset();
            return;
        }
        var active = Configuration.Presets.Find(p => p.Id == Configuration.ActivePresetId);
        if (active == null)
        {
            SimpleHeelsService.ClearOffset();
            return;
        }
        ApplyPresetHeels(active);
    }

    public void ResetAllPriorities()
    {
        loopingEmoteCommand = null;
        ClearEmoteSwap();
        SimpleHeelsService?.ClearOffset();
        BgmTrackerService?.ClearModScd();
        if (activeRoutine != null) CancelRoutine(reason: "reset", announce: false);

        if (PenumbraService == null || !PenumbraService.IsAvailable)
        {
            PrintChat("Penumbra is not available!", XivChatType.ErrorMessage);
            return;
        }

        if (Configuration.ModsWithTempSettings.Count == 0)
        {
            PrintChat("No changes to restore.", XivChatType.Echo);
            return;
        }

        // If active preset was a movement mod, redraw to reload original walk/run animations
        var activePreset = Configuration.ActivePresetId != null
            ? Configuration.Presets.Find(p => p.Id == Configuration.ActivePresetId)
            : null;
        var needsRedraw = activePreset?.AnimationType == 6;

        Task.Run(async () =>
        {
            try
            {
                int restored = 0;

                // Remove all temp settings -> auto-reverts each mod to permanent/inherited state
                foreach (var key in Configuration.ModsWithTempSettings.ToList())
                {
                    var parts = key.Split('|');
                    if (parts.Length < 2) continue;
                    if (!Guid.TryParse(parts[0], out var collectionId)) continue;
                    var modDir = parts[1];
                    var modName = parts.Length >= 3 ? parts[2] : "";

                    if (PenumbraService.RemoveTemporaryModSettings(collectionId, modDir, modName))
                        restored++;

                    await Task.Delay(5);
                }

                Configuration.ModsWithTempSettings.Clear();
                Configuration.ActivePresetId = null;
                Configuration.ActivePresetCollectionId = null;

                Framework.RunOnFrameworkThread(() =>
                {
                    if (needsRedraw)
                        ExecuteRedraw();
                    Configuration.Save();
                    PrintChat($"Restored state for {restored} mod(s).", XivChatType.Echo);
                });
            }
            catch (Exception ex)
            {
                Log.Error($"Error resetting: {ex.Message}");
                Framework.RunOnFrameworkThread(() =>
                {
                    PrintChat($"Error resetting: {ex.Message}", XivChatType.ErrorMessage);
                });
            }
        });
    }

    /// <summary>
    /// Apply temporary Penumbra settings for a preset's mod (enable + priority boost + merged options).
    /// </summary>
    private void ApplyTempSettingsForPresetMod(Guid collectionId, DancePreset preset, PresetModifier? modifier)
    {
        // Read permanent settings (unaffected by any temp overrides)
        var (gotSettings, _, permPriority, permOptions) = PenumbraService!.GetCurrentModSettings(
            collectionId, preset.ModDirectory, preset.ModName);

        // Merge: permanent options -> preset options -> modifier overrides
        var mergedOptions = gotSettings ? new Dictionary<string, List<string>>(permOptions)
                                        : new Dictionary<string, List<string>>();
        foreach (var (g, o) in preset.ModOptions)
            mergedOptions[g] = o;
        if (modifier != null)
        {
            foreach (var (g, o) in modifier.OptionOverrides)
                mergedOptions[g] = o;
        }

        // Calculate boosted priority from permanent base (no escalation possible)
        int boostAmount = preset.PriorityBoost != 0 ? preset.PriorityBoost : Configuration.DefaultPriorityBoost;
        int basePriority = gotSettings ? permPriority : 0;

        PenumbraService.SetTemporaryModSettings(collectionId, preset.ModDirectory,
            enabled: true, priority: basePriority + boostAmount,
            options: mergedOptions, modName: preset.ModName);

        var modKey = $"{collectionId}|{preset.ModDirectory}|{preset.ModName}";
        Configuration.ModsWithTempSettings.Add(modKey);

        Log.Information($"Applied temp settings for '{preset.ModName}': enabled, priority {basePriority}->{basePriority + boostAmount} (+{boostAmount}), {mergedOptions.Count} option groups");
    }

    /// <summary>
    /// One-time migration: restore permanent Penumbra changes from old approach (pre-temp-settings).
    /// </summary>
    private void RunLegacyRestore()
    {
        Log.Information("Running one-time migration: restoring permanent Penumbra changes from old approach");
        int restored = 0;

        // Restore priorities
        foreach (var (key, originalPriority) in Configuration.OriginalPriorities.ToList())
        {
            var parts = key.Split('|');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var collectionId))
            {
                if (PenumbraService!.TrySetModPriority(collectionId, parts[1], originalPriority))
                    restored++;
            }
        }

        // Disable mods we enabled
        foreach (var key in Configuration.ModsWeEnabled.ToList())
        {
            var parts = key.Split('|');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var collectionId))
            {
                if (PenumbraService!.TrySetModEnabled(collectionId, parts[1], false))
                    restored++;
            }
        }

        // Re-enable mods we disabled
        foreach (var key in Configuration.ModsWeDisabled.ToList())
        {
            var parts = key.Split('|');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var collectionId))
            {
                if (PenumbraService!.TrySetModEnabled(collectionId, parts[1], true))
                    restored++;
            }
        }

        // Restore mod options
        foreach (var (key, originalOptions) in Configuration.OriginalModOptions.ToList())
        {
            var parts = key.Split('|');
            if (parts.Length == 2 && Guid.TryParse(parts[0], out var collectionId))
            {
                foreach (var (groupName, options) in originalOptions)
                    PenumbraService!.TrySetModSettings(collectionId, parts[1], groupName, options);
                restored++;
            }
        }

        // Clear all legacy tracking fields
        Configuration.OriginalPriorities.Clear();
        Configuration.ModsWeEnabled.Clear();
        Configuration.ModsWeDisabled.Clear();
        Configuration.OriginalModOptions.Clear();
        Configuration.ActivePresetId = null;
        Configuration.ActivePresetCollectionId = null;
        Configuration.Save();

        Log.Information($"Legacy migration complete: restored {restored} mod(s)");
    }

    public const float MaxAlignDistance = 2f;

    public unsafe (bool hasTarget, string targetName, float distance, bool inRange, CharacterModes mode, bool isWalking) GetAlignState()
    {
        var isWalking = MovementService?.IsMovingToDestination ?? false;

        var target = TargetManager.Target ?? TargetManager.SoftTarget;
        if (target == null)
            return (false, "", 0f, false, CharacterModes.Normal, isWalking);

        var player = (Character*)(ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null)
            return (false, "", 0f, false, CharacterModes.Normal, isWalking);

        var playerMode = player->Mode;

        var playerPos = new System.Numerics.Vector3(
            player->GameObject.Position.X,
            player->GameObject.Position.Y,
            player->GameObject.Position.Z);
        var distance = System.Numerics.Vector3.Distance(playerPos, target.Position);

        return (true, target.Name.TextValue, distance, distance <= MaxAlignDistance, playerMode, isWalking);
    }

    public unsafe void AlignToTarget()
    {
        // Block if already walking
        if (MovementService?.IsMovingToDestination == true)
        {
            PrintChat("Already walking to target.", XivChatType.Echo);
            return;
        }

        var target = TargetManager.Target ?? TargetManager.SoftTarget;
        if (target == null)
        {
            PrintChat("Select a target first.", XivChatType.ErrorMessage);
            return;
        }

        var player = (Character*)(ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null) return;

        // Block align while sitting, dozing, mounted, or in any non-standing state
        if (player->Mode != CharacterModes.Normal)
        {
            var msg = player->Mode switch
            {
                CharacterModes.Mounted => "Dismount first.",
                CharacterModes.EmoteLoop => "Stop your emote first.",
                CharacterModes.InPositionLoop => "Stand up first.",
                CharacterModes.Performance => "Stop performing first.",
                _ => "Stop what you're doing first.",
            };
            PrintChat(msg, XivChatType.ErrorMessage);
            return;
        }

        var playerPos = new System.Numerics.Vector3(
            player->GameObject.Position.X,
            player->GameObject.Position.Y,
            player->GameObject.Position.Z);
        var targetPos = target.Position;
        var distance = System.Numerics.Vector3.Distance(playerPos, targetPos);

        if (distance > MaxAlignDistance)
        {
            PrintChat($"Move closer to {target.Name.TextValue} and try again.", XivChatType.ErrorMessage);
            return;
        }

        var targetRotation = target.Rotation;

        // Use RMIWalk movement override if hook is active, otherwise fall back to SetPosition
        if (MovementService != null && MovementService.IsHookActive)
        {
            MovementService.WalkTo(targetPos,
                arrived: () => ApplyTargetRotation(targetRotation),
                cancelled: null,
                snap: (pos) => SnapToPosition(pos));
            Log.Debug($"Walking to target '{target.Name}' ({distance:F2} units away)");
        }
        else
        {
            // Fallback: direct position set (if signature broke)
            player->GameObject.SetPosition(targetPos.X, targetPos.Y, targetPos.Z);
            player->GameObject.SetRotation(targetRotation);
            Log.Debug($"Aligned to target '{target.Name}' ({distance:F2} units away) [fallback]");
        }
    }

    private unsafe void ApplyTargetRotation(float rotation)
    {
        var player = (Character*)(ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player != null)
            player->GameObject.SetRotation(rotation);
    }

    private unsafe void SnapToPosition(System.Numerics.Vector3 pos)
    {
        var player = (Character*)(ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player != null)
            player->GameObject.SetPosition(pos.X, pos.Y, pos.Z);
    }

    private static int GetAnimationTypeForCommand(string? command)
    {
        return command?.ToLowerInvariant() switch
        {
            "/cpose" => 2,      // StandingIdle
            "/sit" => 3,        // ChairSitting
            "/groundsit" => 4,  // GroundSitting
            "/doze" => 5,       // LyingDozing
            _ => 1              // Emote
        };
    }

    // direct ProcessChatBoxEntry; isExecutingBypassCarrier flag tells the hook not to clear the swap
    private unsafe void ExecuteEmoteDirect(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!command.StartsWith("/")) command = "/" + command;
        isExecutingBypassCarrier = true;
        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule != null)
            {
                using var str = new Utf8String(command);
                uiModule->ProcessChatBoxEntry(&str);
                Log.Debug($"Executed emote (direct): {command}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to execute emote '{command}': {ex.Message}");
        }
        finally
        {
            isExecutingBypassCarrier = false;
        }
    }

    private unsafe void ExecuteEmote(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        // Ensure command starts with /
        if (!command.StartsWith("/"))
            command = "/" + command;

        // Clear any active emote swap from a previous /vanilla bypass
        // so it doesn't interfere with normal emote execution
        ClearEmoteSwap();

        try
        {
            var uiModule = UIModule.Instance();
            if (uiModule != null)
            {
                using var str = new Utf8String(command);
                uiModule->ProcessChatBoxEntry(&str);
                Log.Debug($"Executed emote: {command}");
            }
            else
            {
                Log.Warning("UIModule not available, cannot execute emote");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to execute emote '{command}': {ex.Message}");
        }
    }

    // returns true if check fails (avoid blocking normal execution)
    private static unsafe bool IsEmoteUnlocked(ushort emoteId)
    {
        try
        {
            var uiState = UIState.Instance();
            return uiState != null && uiState->IsEmoteUnlocked(emoteId);
        }
        catch { return true; }
    }

    // All 18 race/gender IDs in FFXIV
    private static readonly string[] RaceIds = {
        "c0101", "c0201", "c0301", "c0401", "c0501", "c0601",
        "c0701", "c0801", "c0901", "c1001", "c1101", "c1201",
        "c1301", "c1401", "c1501", "c1601", "c1701", "c1801"
    };

    // race fallback by body-type affinity (skeleton sharing): standard f / tall f / standard m / etc.
    private static readonly Dictionary<string, string[]> RaceAnimationAffinity = new()
    {
        // Male - standard body
        ["c0101"] = new[] { "c0701", "c1301", "c1701", "c0301", "c0501" },           // Hyur Mid M
        ["c0301"] = new[] { "c0101", "c0901", "c0501", "c0701", "c1301", "c1701" },  // Hyur High M
        ["c0501"] = new[] { "c0101", "c0701", "c1301", "c1701", "c0301" },           // Elezen M
        ["c0701"] = new[] { "c0101", "c1301", "c1701", "c0301", "c0501" },           // Miqo M
        ["c0901"] = new[] { "c0301", "c0101", "c1501", "c0501" },                    // Roe M
        ["c1101"] = new[] { "c1201" },                                                // Lala M
        ["c1301"] = new[] { "c0101", "c0701", "c1701", "c0301", "c0501" },           // Au Ra M
        ["c1501"] = new[] { "c0901", "c0301", "c0101" },                             // Hrothgar M
        ["c1701"] = new[] { "c0101", "c0701", "c1301", "c0301", "c0501" },           // Viera M
        // Female - standard body
        ["c0201"] = new[] { "c0801", "c1401", "c1801", "c0401", "c0601" },           // Hyur Mid F
        ["c0401"] = new[] { "c0201", "c0801", "c1401", "c1801", "c0601" },           // Hyur High F
        ["c0601"] = new[] { "c1001", "c0401", "c0201", "c0801" },                    // Elezen F
        ["c0801"] = new[] { "c0201", "c1401", "c1801", "c0401", "c0601" },           // Miqo F
        ["c1001"] = new[] { "c0601", "c0401", "c0201", "c0801" },                    // Roe F
        ["c1201"] = new[] { "c1101" },                                                // Lala F
        ["c1401"] = new[] { "c0801", "c0201", "c1801", "c0401", "c0601" },           // Au Ra F
        ["c1601"] = new[] { "c1001", "c0601", "c0401", "c0201", "c0801" },           // Hrothgar F
        ["c1801"] = new[] { "c0801", "c0201", "c1401", "c0401", "c0601" },           // Viera F
    };

    // Subfolders to check for animation files
    // Full set of subfolders for resolving TARGET PAP paths (ResolveEmotePaths).
    // Includes job-specific subfolders so PickBestPath can find the player's job-specific animation.
    private static readonly string[] AnimSubfolders = {
        "", "bt_common/", "resident/", "nonresident/",
        // Job-specific battle animation subfolders (for emotes like Zantetsuken under bt_swd_sld)
        "bt_swd_sld/",  // PLD/GLA - sword + shield
        "bt_2ax_emp/",  // WAR/MRD - greataxe
        "bt_2sw_emp/",  // DRK - greatsword
        "bt_2gb_emp/",  // GNB - gunblade
        "bt_2sp_emp/",  // DRG/LNC - spear
        "bt_2km_emp/",  // RPR - scythe
        "bt_clw_clw/",  // MNK/PGL - claws/fists
        "bt_dgr_dgr/",  // NIN/ROG - daggers
        "bt_nin_nin/",  // NIN - alternate
        "bt_2kt_emp/",  // SAM - katana
        "bt_bld_bld/",  // VPR - twinfangs
        "bt_2bw_emp/",  // BRD/ARC - bow
        "bt_2gn_emp/",  // MCH - gun
        "bt_chk_chk/",  // DNC - chakrams
        "bt_stf_sld/",  // WHM/CNJ/BLM - staff + shield
        "bt_jst_sld/",  // BLM/THM - scepter + shield
        "bt_2bk_emp/",  // SCH/SMN/ACN - book
        "bt_2gl_emp/",  // AST - globe
        "bt_2ff_emp/",  // SGE - nouliths
        "bt_rod_emp/",  // BLU - rod
        "bt_2rp_emp/",  // RDM - rapier
        "bt_brs_plt/",  // PCT - brush + palette
    };

    // base subfolders only; job-specific bt_ folders create phantom redirects
    private static readonly string[] CarrierPathSubfolders = {
        "", "bt_common/", "resident/", "nonresident/"
    };

    private string EmoteSwapDir => Path.Combine(PluginInterface.GetPluginConfigDirectory(), "emoteswap");

    private static readonly HashSet<string> NoCarrierCommands = new(StringComparer.OrdinalIgnoreCase)
        { "/sit", "/groundsit", "/doze", "/changepose", "/cpose", "/hug", "/pet", "/handover", "/embrace", "/dote",
          "/showleft", "/showright", "/savortea", "/hildy", "/songbird" };

    // looping carriers are scanned dynamically per-account (vary, often emote_sp/_loop keys)
    private static readonly string[] PreferredOneShotCarriers = { "/no", "/me", "/bow", "/clap", "/yes", "/wave", "/cheer", "/dance", "/laugh" };

    // matches loop type; prefers slot 1 carriers when target has intro animation
    private (ushort id, string command, string key, string papName, bool isLoop, string? slot1Key, string? slot1PapName)? FindCarrier(bool needsLoop, bool preferSlot1 = false, int maxNameLen = 0, int maxSlot1NameLen = 0, byte? requiredConditionMode = null, HashSet<ushort>? excludeCarrierIds = null)
    {
        if (emoteCommandToId == null || emoteIdToTimelineKeys == null) return null;

        var excludedIds = new HashSet<ushort>();
        foreach (var (cmd, id) in emoteCommandToId)
        {
            if (NoCarrierCommands.Contains(cmd))
                excludedIds.Add(id);
        }
        if (excludeCarrierIds != null)
            excludedIds.UnionWith(excludeCarrierIds);

        bool FitsNameConstraint((ushort id, string command, string key, string papName, bool isLoop, string? slot1Key, string? slot1PapName)? result)
        {
            if (result == null) return false;
            if (maxNameLen > 0 && result.Value.papName.Length > maxNameLen) return false;
            if (preferSlot1 && maxSlot1NameLen > 0 && result.Value.slot1PapName != null && result.Value.slot1PapName.Length > maxSlot1NameLen) return false;
            return true;
        }

        // For one-shot targets: try preferred one-shot carriers first
        if (!needsLoop)
        {
            foreach (var cmd in PreferredOneShotCarriers)
            {
                var result = TryGetCarrierInfo(cmd, excludedIds, requireLoop: false, requiredConditionMode: requiredConditionMode);
                if (result != null && (!preferSlot1 || result.Value.slot1Key != null) && FitsNameConstraint(result)) return result;
            }
        }

        // Scan all unlocked emotes for a carrier matching the requested loop type
        // When preferSlot1, first pass requires slot 1, second pass accepts any
        if (preferSlot1)
        {
            var withSlot1 = ScanForCarrier(excludedIds, needsLoop, requireSlot1: true, maxNameLen, maxSlot1NameLen, requiredConditionMode: requiredConditionMode);
            if (withSlot1 != null) return withSlot1;
        }
        var matchingCarrier = ScanForCarrier(excludedIds, needsLoop, requireSlot1: false, maxNameLen, requiredConditionMode: requiredConditionMode);
        if (matchingCarrier != null) return matchingCarrier;

        // Fallback: accept any carrier regardless of loop type (mismatched is better than nothing)
        Log.Debug($"[UnlockBypass] No {(needsLoop ? "looping" : "one-shot")} carriers found with name length <= {maxNameLen}, trying any carrier");
        if (needsLoop)
        {
            // For looping targets that couldn't find a looping carrier, try preferred one-shot
            foreach (var cmd in PreferredOneShotCarriers)
            {
                var result = TryGetCarrierInfo(cmd, excludedIds, requireLoop: null, requiredConditionMode: requiredConditionMode);
                if (FitsNameConstraint(result)) return result;
            }
        }
        return ScanForCarrier(excludedIds, loopType: null, requireSlot1: false, maxNameLen, requiredConditionMode: requiredConditionMode);
    }

    /// <summary>Scan all unlocked emotes for a carrier. loopType=null means accept any. maxNameLen=0 means no limit.</summary>
    private (ushort id, string command, string key, string papName, bool isLoop, string? slot1Key, string? slot1PapName)? ScanForCarrier(HashSet<ushort> excludedIds, bool? loopType, bool requireSlot1, int maxNameLen = 0, int maxSlot1NameLen = 0, byte? requiredConditionMode = null)
    {
        foreach (var (id, keys) in emoteIdToTimelineKeys!)
        {
            if (excludedIds.Contains(id)) continue;
            if (!IsEmoteUnlocked(id)) continue;
            // Filter by ConditionMode (e.g., only ConditionMode 0 carriers when player is sitting)
            if (requiredConditionMode.HasValue && emoteIdToConditionMode != null &&
                emoteIdToConditionMode.TryGetValue(id, out var cm) && cm != requiredConditionMode.Value) continue;
            var slot0 = keys.FirstOrDefault(k => k.slot == 0);
            if (string.IsNullOrEmpty(slot0.key)) continue;
            if (loopType.HasValue && slot0.isLoop != loopType.Value) continue;

            var (slot1Key, slot1PapName) = GetCarrierSlot1Info(keys);
            if (requireSlot1 && slot1Key == null) continue;

            var papName = GetCarrierPapName(id, slot0.key);
            if (papName == null) continue;

            // Check name length constraint
            if (maxNameLen > 0 && papName.Length > maxNameLen) continue;
            if (requireSlot1 && maxSlot1NameLen > 0 && slot1PapName != null && slot1PapName.Length > maxSlot1NameLen) continue;

            string? foundCmd = null;
            foreach (var (c, cid) in emoteCommandToId!)
            {
                if (cid == id && c.StartsWith("/"))
                {
                    if (foundCmd == null || c.Length < foundCmd.Length)
                        foundCmd = c;
                }
            }
            if (foundCmd == null) continue;

            return (id, foundCmd, slot0.key, papName, slot0.isLoop, slot1Key, slot1PapName);
        }
        return null;
    }

    /// <summary>Try to resolve a preferred carrier command into full carrier info.</summary>
    /// <param name="requireLoop">If set, only return carriers matching this loop type. Null = accept any.</param>
    private (ushort id, string command, string key, string papName, bool isLoop, string? slot1Key, string? slot1PapName)? TryGetCarrierInfo(string command, HashSet<ushort> excludedIds, bool? requireLoop, byte? requiredConditionMode = null)
    {
        var cmdLookup = command.TrimStart('/').ToLowerInvariant();
        if (!emoteCommandToId!.TryGetValue(cmdLookup, out var id)) return null;
        if (excludedIds.Contains(id)) return null;
        if (!IsEmoteUnlocked(id)) return null;
        // Filter by ConditionMode (e.g., only ConditionMode 0 carriers when player is sitting)
        if (requiredConditionMode.HasValue && emoteIdToConditionMode != null &&
            emoteIdToConditionMode.TryGetValue(id, out var cm) && cm != requiredConditionMode.Value) return null;
        if (!emoteIdToTimelineKeys!.TryGetValue(id, out var keys)) return null;

        var slot0 = keys.FirstOrDefault(k => k.slot == 0);
        if (string.IsNullOrEmpty(slot0.key)) return null;
        if (requireLoop.HasValue && slot0.isLoop != requireLoop.Value) return null;

        var papName = GetCarrierPapName(id, slot0.key);
        if (papName == null) return null;

        var (slot1Key, slot1PapName) = GetCarrierSlot1Info(keys);
        return (id, command, slot0.key, papName, slot0.isLoop, slot1Key, slot1PapName);
    }

    /// <summary>Get the internal PAP animation name for a carrier (cached).</summary>
    private string? GetCarrierPapName(ushort id, string slot0Key)
    {
        if (carrierPapNameCache.TryGetValue(id, out var cached))
            return cached;
        var paths = ResolveEmotePaths(slot0Key);
        if (paths.Count == 0) return null;
        var file = DataManager.GetFile(paths[0]);
        if (file == null) return null;
        var papName = ReadPapAnimationName(file.Data);
        if (papName == null) return null;
        carrierPapNameCache[id] = papName;
        return papName;
    }

    /// <summary>Get slot 1 (intro/start) info for a carrier if available.</summary>
    private (string? slot1Key, string? slot1PapName) GetCarrierSlot1Info(List<(int slot, string key, bool isLoop, byte loadType)> keys)
    {
        var slot1 = keys.FirstOrDefault(k => k.slot == 1);
        if (string.IsNullOrEmpty(slot1.key)) return (null, null);
        var slot1Paths = ResolveEmotePaths(slot1.key);
        if (slot1Paths.Count == 0) return (null, null);
        var slot1File = DataManager.GetFile(slot1Paths[0]);
        if (slot1File == null) return (null, null);
        var slot1PapName = ReadPapAnimationName(slot1File.Data);
        if (slot1PapName == null) return (null, null);
        return (slot1.key, slot1PapName);
    }

    // forPreset (EmoteLocked): skips unlock check + carrier sticking, reads target PAP from mod files
    private (string? carrierCommand, ushort carrierId, bool targetIsLoop) TrySetupEmoteBypass(
        string command, bool forPreset = false)
    {
        if (!Configuration.AllowUnlockedEmotes || PenumbraService?.IsAvailable != true)
            return (null, 0, false);
        if (emoteCommandToId == null || emoteIdToTimelineKeys == null)
            return (null, 0, false);

        var cmdLower = command.ToLowerInvariant();
        if (!emoteCommandToId.TryGetValue(cmdLower, out var targetEmoteId))
        {
            Log.Debug($"[UnlockBypass] Command '{cmdLower}' not found in emote lookup");
            return (null, 0, false);
        }

        // skip if already unlocked (unless forPreset). Zantetsuken misreports as unlocked (Mogstation bug)
        if (!forPreset && IsEmoteUnlocked(targetEmoteId) && cmdLower != "zantetsuken" && cmdLower != "ztk")
        {
            Log.Debug($"[UnlockBypass] Emote '{cmdLower}' (id={targetEmoteId}) is unlocked, skipping bypass");
            return (null, 0, false);
        }

        if (!emoteIdToTimelineKeys.TryGetValue(targetEmoteId, out var targetKeys))
        {
            Log.Debug($"[UnlockBypass] No timeline keys for emote ID {targetEmoteId}");
            return (null, 0, false);
        }

        // Read race early - needed for both sticking check and PAP selection
        var playerRaceCode = GetPlayerRaceCode();

        // Carrier sticking: if same emote as last time and swap is still active, reuse the same carrier.
        // Skip for preset bypass - mod options may have changed (modifier switch), must always re-read PAP.
        // Also skip if race changed (character switch via Glamourer/Character Select+) - PAP data is race-specific.
        if (!forPreset && emoteSwapActive && lastBypassEmoteCommand == cmdLower && lastBypassCarrierCommand != null)
        {
            if (lastBypassRaceCode != playerRaceCode)
            {
                Log.Information($"[UnlockBypass] Race changed ({lastBypassRaceCode} -> {playerRaceCode}), re-running swap setup for '{cmdLower}'");
            }
            else
            {
                // If swap was soft-disabled by one-shot cleanup (carrier returned to normal after emote ended),
                // re-enable the mod. SetTemporaryModSettings increments Penumbra's ChangeCounter, which changes
                // the resolved path encoding - giving the game's resource cache a fresh path to load from.
                if (emoteSwapSoftDisabled)
                {
                    try
                    {
                        PenumbraService.SetTemporaryModSettings(
                            emoteSwapCollectionId, EncoreSwapModName,
                            true, emoteSwapPriority,
                            new Dictionary<string, List<string>>(),
                            EncoreSwapModName);
                        emoteSwapSoftDisabled = false;
                        Log.Information($"[UnlockBypass] Re-enabled swap mod for '{cmdLower}' (was soft-disabled after one-shot)");
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[UnlockBypass] Failed to re-enable swap mod: {ex.Message}, falling back to full setup");
                        emoteSwapActive = false; // Force full setup path
                    }
                }

                if (emoteSwapActive) // May have been cleared by re-enable failure above
                {
                    var stickIsLoop = targetKeys.FirstOrDefault(k => k.slot == 0).isLoop;
                    Log.Information($"[UnlockBypass] Reusing carrier '{lastBypassCarrierCommand}' (id={lastBypassCarrierId}) for '{cmdLower}' - swap still active (race={playerRaceCode})");
                    return (lastBypassCarrierCommand, lastBypassCarrierId, stickIsLoop);
                }
            }
        }

        Log.Debug($"[UnlockBypass] Target emote '{cmdLower}' (id={targetEmoteId}) has {targetKeys.Count} timeline(s): {string.Join(", ", targetKeys.Select(t => $"[{t.slot}]={t.key}{(t.isLoop ? " (loop)" : "")}{(t.loadType == 0 ? " (facial)" : t.loadType == 1 ? " (perjob)" : "")}"))}");

        var targetSlot0 = targetKeys.FirstOrDefault(k => k.slot == 0);
        // Some emotes store their main animation in slot 4 instead of slot 0 (e.g., Water Float, Water Flip).
        // Fall back to slot 4 if slot 0 has no key.
        if (string.IsNullOrEmpty(targetSlot0.key))
        {
            var targetSlot4 = targetKeys.FirstOrDefault(k => k.slot == 4);
            if (!string.IsNullOrEmpty(targetSlot4.key))
            {
                targetSlot0 = targetSlot4;
                Log.Debug($"[UnlockBypass] Slot 0 empty, using slot 4: [{targetSlot4.slot}]={targetSlot4.key}");
            }
        }
        var targetIsLoop = targetSlot0.isLoop;

        // Check if target has a slot 1 (intro/start animation)
        var targetSlot1 = targetKeys.FirstOrDefault(k => k.slot == 1);
        var targetHasSlot1 = !string.IsNullOrEmpty(targetSlot1.key);
        if (targetHasSlot1)
            Log.Debug($"[UnlockBypass] Target has intro animation: [{targetSlot1.slot}]={targetSlot1.key}");

        // Read target PAP names to determine max carrier name length.
        // The C009.Path in TMB has limited padded space - carrier name must fit.
        // Use the player's race+job-specific PAP when available for correct skeleton/expression data.
        int maxSlot0NameLen = 0;
        int maxSlot1NameLen = 0;
        var playerJobSub = GetPlayerJobSubfolder();

        if (!string.IsNullOrEmpty(targetSlot0.key))
        {
            var targetPaths = ResolveEmotePaths(targetSlot0.key);
            if (targetPaths.Count > 0)
            {
                var targetFile = DataManager.GetFile(PickBestPath(targetPaths, playerRaceCode, playerJobSub));
                if (targetFile != null)
                {
                    var targetName = ReadPapAnimationName(targetFile.Data);
                    if (targetName != null)
                        maxSlot0NameLen = targetName.Length;
                }
            }
        }

        if (targetHasSlot1)
        {
            var targetSlot1Paths = ResolveEmotePaths(targetSlot1.key);
            if (targetSlot1Paths.Count > 0)
            {
                var targetSlot1File = DataManager.GetFile(PickBestPath(targetSlot1Paths, playerRaceCode, playerJobSub));
                if (targetSlot1File != null)
                {
                    var targetSlot1Name = ReadPapAnimationName(targetSlot1File.Data);
                    if (targetSlot1Name != null)
                        maxSlot1NameLen = targetSlot1Name.Length;
                }
            }
        }

        Log.Debug($"[UnlockBypass] Target PAP name constraints: slot0 max={maxSlot0NameLen}, slot1 max={maxSlot1NameLen}");

        // Check player state for sitting-aware carrier selection.
        // Only InPositionLoop (sit/doze) actually rejects standing-only emote commands -
        // EmoteLoop (mid-dance) accepts any emote (new one cancels the current), so no filter needed there.
        byte? requiredConditionMode = null;
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer != null && localPlayer.Address != nint.Zero)
        {
            unsafe
            {
                var ch = (Character*)localPlayer.Address;
                if (ch->Mode == CharacterModes.InPositionLoop)
                {
                    requiredConditionMode = 0;
                    Log.Debug($"[UnlockBypass] Player mode={ch->Mode}, requiring ConditionMode 0 carriers");
                }
            }
        }

        // game caches PAP per-animation-key; rotate carriers (Penumbra ChangeCounter only busts TMBs)
        var targetSlot1PapExists = maxSlot1NameLen > 0;
        // forPreset keeps the same carrier across invocations (sync plugins see consistent carrier)
        HashSet<ushort>? excludeCarrierIds = (!forPreset && usedCarrierIds.Count > 0) ? usedCarrierIds : null;
        if (excludeCarrierIds != null)
            Log.Debug($"[UnlockBypass] Excluding {excludeCarrierIds.Count} previously used carrier(s) for PAP cache busting");
        var carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, maxNameLen: maxSlot0NameLen, maxSlot1NameLen: maxSlot1NameLen, requiredConditionMode: requiredConditionMode, excludeCarrierIds: excludeCarrierIds);

        // If no carrier fits with slot 1 preference, try without slot 1 requirement
        if (carrier == null && targetHasSlot1 && targetSlot1PapExists)
        {
            Log.Debug($"[UnlockBypass] No carrier with slot 1 fits, trying without slot 1");
            carrier = FindCarrier(targetIsLoop, preferSlot1: false, maxNameLen: maxSlot0NameLen, requiredConditionMode: requiredConditionMode, excludeCarrierIds: excludeCarrierIds);
        }

        // If still no carrier fits the name constraint, try without name constraint (partial rewrite - may not animate but better than nothing)
        if (carrier == null && maxSlot0NameLen > 0)
        {
            Log.Warning($"[UnlockBypass] No carrier with name length <= {maxSlot0NameLen} found, trying without length constraint");
            carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, requiredConditionMode: requiredConditionMode, excludeCarrierIds: excludeCarrierIds);
        }

        // If ConditionMode constraint prevented finding a carrier, fall back to any carrier (better to try than fail silently)
        if (carrier == null && requiredConditionMode.HasValue)
        {
            Log.Warning($"[UnlockBypass] No ConditionMode {requiredConditionMode.Value} carriers found, trying without ConditionMode constraint");
            carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, maxNameLen: maxSlot0NameLen, maxSlot1NameLen: maxSlot1NameLen, excludeCarrierIds: excludeCarrierIds);
            if (carrier == null && targetHasSlot1 && targetSlot1PapExists)
                carrier = FindCarrier(targetIsLoop, preferSlot1: false, maxNameLen: maxSlot0NameLen, excludeCarrierIds: excludeCarrierIds);
            if (carrier == null && maxSlot0NameLen > 0)
                carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, excludeCarrierIds: excludeCarrierIds);
        }

        // Pool exhaustion: all carriers used this session. Clear and retry - game's LRU cache
        // may have evicted old entries. Better to try a reused carrier than fail entirely.
        if (carrier == null && usedCarrierIds.Count > 0)
        {
            Log.Information($"[UnlockBypass] All carriers exhausted ({usedCarrierIds.Count} used), clearing pool and retrying");
            usedCarrierIds.Clear();
            Configuration.UsedBypassCarrierIds.Clear();
            Configuration.Save();
            excludeCarrierIds = null;
            carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, maxNameLen: maxSlot0NameLen, maxSlot1NameLen: maxSlot1NameLen, requiredConditionMode: requiredConditionMode);
        }

        // Loop-target mismatch rescue: if target wants a loop but FindCarrier fell back to a
        // non-loop carrier AND we had any exclusions, clear the used pool and retry for a loop.
        // A non-loop carrier for a looping target will stop after one play - unacceptable for loops.
        if (targetIsLoop && carrier != null && !carrier.Value.isLoop && usedCarrierIds.Count > 0)
        {
            Log.Information($"[UnlockBypass] Got non-loop carrier for loop target while {usedCarrierIds.Count} carriers were excluded - resetting pool to find a real loop carrier");
            usedCarrierIds.Clear();
            Configuration.UsedBypassCarrierIds.Clear();
            Configuration.Save();
            excludeCarrierIds = null;
            var retry = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, maxNameLen: maxSlot0NameLen, maxSlot1NameLen: maxSlot1NameLen, requiredConditionMode: requiredConditionMode);
            if (retry != null && retry.Value.isLoop)
                carrier = retry;
        }

        if (carrier == null)
        {
            Log.Warning($"[UnlockBypass] No carriers available for '{cmdLower}'");
            return (null, 0, false);
        }

        var (carrierId, carrierCommand, carrierKey, carrierPapName, carrierIsLoop, carrierSlot1Key, carrierSlot1PapName) = carrier.Value;

        Log.Debug($"[UnlockBypass] Using carrier '{carrierCommand}' (id={carrierId}, papName='{carrierPapName}' ({carrierPapName.Length}ch), loop={carrierIsLoop}, targetLoop={targetIsLoop}, slot1={carrierSlot1Key != null})");

        // Log carrier's full timeline entries for diagnostic purposes
        if (emoteIdToTimelineKeys!.TryGetValue(carrierId, out var carrierAllKeys))
            Log.Debug($"[UnlockBypass] Carrier timelines: {string.Join(", ", carrierAllKeys.Select(t => $"[{t.slot}]={t.key}{(t.loadType == 0 ? " (facial)" : t.loadType == 1 ? " (perjob)" : "")}"))}");

        // Build key pairs: always slot 0 (main/loop), optionally slot 1 (intro/start)
        var keyPairs = new List<(string carrierKey, string targetKey)>();
        if (!string.IsNullOrEmpty(targetSlot0.key) && !string.IsNullOrEmpty(carrierKey))
        {
            keyPairs.Add((carrierKey, targetSlot0.key));
        }

        if (targetHasSlot1 && targetSlot1PapExists && carrierSlot1Key != null)
        {
            // Both target and carrier have usable slot 1 - redirect to target's intro
            keyPairs.Add((carrierSlot1Key, targetSlot1.key));
            Log.Debug($"[UnlockBypass] Including slot 1 pair: carrier='{carrierSlot1Key}' -> target='{targetSlot1.key}'");
        }
        else if (carrierSlot1Key != null && !string.IsNullOrEmpty(targetSlot0.key))
        {
            // Carrier has an intro but target doesn't (or target's intro PAP doesn't exist).
            // Redirect carrier's slot 1 to target's slot 0 so the target animation plays during
            // the intro phase instead of the carrier's own intro animation bleeding through.
            keyPairs.Add((carrierSlot1Key, targetSlot0.key));
            Log.Debug($"[UnlockBypass] Suppressing carrier intro: redirecting slot 1 '{carrierSlot1Key}' -> target slot 0 '{targetSlot0.key}'");
        }
        else if (targetHasSlot1 && carrierSlot1Key == null)
        {
            Log.Debug($"[UnlockBypass] Carrier '{carrierCommand}' has no slot 1 - intro will be skipped");
        }

        // Add key pairs for ALL remaining target slots (facial, upper body/head targeting, etc.)
        // Match by slot index: for each target entry not already paired (slot 0/1), find the carrier's entry at the same slot.
        var pairedSlots = new HashSet<int> { 0 }; // slot 0 always paired above
        if (carrierSlot1Key != null) // slot 1 is paired (either normal redirect or intro suppression)
            pairedSlots.Add(1);

        if (emoteIdToTimelineKeys!.TryGetValue(carrierId, out var carrierKeys2))
        {
            foreach (var targetEntry in targetKeys)
            {
                if (pairedSlots.Contains(targetEntry.slot)) continue;
                if (string.IsNullOrEmpty(targetEntry.key)) continue;

                var carrierEntry = carrierKeys2.FirstOrDefault(k => k.slot == targetEntry.slot);
                if (!string.IsNullOrEmpty(carrierEntry.key))
                {
                    keyPairs.Add((carrierEntry.key, targetEntry.key));
                    pairedSlots.Add(targetEntry.slot);
                    var typeLabel = targetEntry.loadType == 0 ? "facial" : targetEntry.loadType == 1 ? "perjob" : "extra";
                    Log.Debug($"[UnlockBypass] Including {typeLabel} pair [{targetEntry.slot}]: carrier='{carrierEntry.key}' -> target='{targetEntry.key}'");
                }
                else
                {
                    var typeLabel = targetEntry.loadType == 0 ? "facial" : targetEntry.loadType == 1 ? "perjob" : "extra";
                    Log.Debug($"[UnlockBypass] Carrier has no slot [{targetEntry.slot}] for {typeLabel} '{targetEntry.key}'");
                }
            }
        }

        if (keyPairs.Count == 0)
        {
            Log.Warning($"[UnlockBypass] No key pairs for '{cmdLower}' with carrier '{carrierCommand}'");
            return (null, 0, false);
        }

        if (SetupEmoteSwap(keyPairs, readTargetFromMod: forPreset))
        {
            emoteSwapActive = true;
            lastBypassEmoteCommand = cmdLower;
            lastBypassCarrierCommand = carrierCommand;
            lastBypassCarrierId = carrierId;
            lastBypassRaceCode = playerRaceCode;
            // For preset-driven bypass, don't pollute the used-carrier pool - we WANT to
            // pick the same carrier on subsequent invocations (same preset / routine loop).
            // Skipping the add keeps /pray (or whatever preferred) available forever.
            if (!forPreset)
            {
                usedCarrierIds.Add(carrierId); // Track for PAP cache busting
                Configuration.UsedBypassCarrierIds.Add(carrierId);
                Configuration.Save();
            }
            Log.Information($"[UnlockBypass] Swap active for '{cmdLower}', executing {carrierCommand} ({keyPairs.Count} slot(s), fromMod={forPreset}, usedCarriers={usedCarrierIds.Count})");
            return (carrierCommand, carrierId, targetIsLoop);
        }

        Log.Warning($"[UnlockBypass] SetupEmoteSwap failed for '{cmdLower}' with carrier '{carrierCommand}'");
        return (null, 0, false);
    }

    /// <summary>Get game file paths that actually exist for a given ActionTimeline key.</summary>
    private List<string> ResolveEmotePaths(string timelineKey)
    {
        var paths = new List<string>();

        var globalPath = $"chara/animation/{timelineKey}.pap";
        if (DataManager.FileExists(globalPath))
            paths.Add(globalPath);

        foreach (var race in RaceIds)
        {
            foreach (var layer in new[] { "a0001", "a0002" })
            {
                foreach (var sub in AnimSubfolders)
                {
                    var path = $"chara/human/{race}/animation/{layer}/{sub}{timelineKey}.pap";
                    if (DataManager.FileExists(path))
                        paths.Add(path);
                }
            }
        }
        return paths;
    }

    // unlike ResolveEmotePaths, does NOT check FileExists; needed so Penumbra can redirect ANY race
    private List<string> GenerateAllPossiblePaths(string timelineKey)
    {
        var paths = new List<string>();
        paths.Add($"chara/animation/{timelineKey}.pap");

        foreach (var race in RaceIds)
            foreach (var layer in new[] { "a0001", "a0002" })
                foreach (var sub in CarrierPathSubfolders)
                    paths.Add($"chara/human/{race}/animation/{layer}/{sub}{timelineKey}.pap");

        return paths;
    }

    // race code (e.g., "c0101"). Tries Glamourer IPC first; falls back to ObjectTable customize bytes
    private string? GetPlayerRaceCode()
    {
        // Try Glamourer first - ObjectTable[0].Customize doesn't reflect visual race changes
        // from Glamourer/Penumbra meta manipulations (e.g., Character Select+ character switches)
        try
        {
            var glamourerState = PluginInterface.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState");
            var (ec, state) = glamourerState.InvokeFunc(0, 0u);
            if (ec == 0 && state != null)
            {
                var customize = state["Customize"];
                if (customize != null)
                {
                    // Try nested {"Value": byte} format first, then direct value
                    var glamRace = customize["Race"]?["Value"]?.Value<byte>() ?? customize["Race"]?.Value<byte>();
                    var glamGender = customize["Gender"]?["Value"]?.Value<byte>() ?? customize["Gender"]?.Value<byte>();
                    var glamTribe = customize["Clan"]?["Value"]?.Value<byte>() ?? customize["Clan"]?.Value<byte>();
                    if (glamRace.HasValue && glamGender.HasValue && glamTribe.HasValue)
                    {
                        var glamCode = RaceToModelCode(glamRace.Value, glamGender.Value, glamTribe.Value);
                        if (glamCode != null)
                        {
                            Log.Information($"[RaceCode] Glamourer: race={glamRace}, gender={glamGender}, tribe={glamTribe} -> {glamCode}");
                            return glamCode;
                        }
                        Log.Warning($"[RaceCode] Glamourer: RaceToModelCode returned null for race={glamRace}, gender={glamGender}, tribe={glamTribe}");
                    }
                    else
                    {
                        Log.Warning($"[RaceCode] Glamourer: Failed to parse Customize - Race={customize["Race"]}, Gender={customize["Gender"]}, Clan={customize["Clan"]}");
                    }
                }
                else
                {
                    Log.Warning($"[RaceCode] Glamourer: state has no 'Customize' key. Keys: {string.Join(", ", state.Properties().Select(p => p.Name))}");
                }
            }
            else
            {
                Log.Warning($"[RaceCode] Glamourer: GetState returned ec={ec}, state={state != null}");
            }
        }
        catch (Exception glamEx)
        {
            Log.Warning($"[RaceCode] Glamourer IPC failed: {glamEx.Message}");
        }

        // Fallback: ObjectTable customize bytes (server data, may not reflect visual changes)
        var player = ObjectTable[0];
        if (player is not Dalamud.Game.ClientState.Objects.Types.ICharacter character)
            return null;

        var custBytes = character.Customize;
        byte race = custBytes[0];    // CustomizeIndex.Race (1-8)
        byte gender = custBytes[1];  // CustomizeIndex.Gender (0=M, 1=F)
        byte tribe = custBytes[4];   // CustomizeIndex.Tribe (1-16)

        var code = RaceToModelCode(race, gender, tribe);
        Log.Information($"[RaceCode] ObjectTable fallback: race={race}, gender={gender}, tribe={tribe} -> {code}");
        return code;
    }

    /// <summary>Map FFXIV Race+Gender+Tribe to character model code (e.g., "c0101").</summary>
    private static string? RaceToModelCode(byte race, byte gender, byte tribe)
    {
        // Model order (c01-c18) does NOT match the Race enum order
        int modelBase = race switch
        {
            1 when tribe == 1 => 1,   // Hyur Midlander
            1 => 3,                    // Hyur Highlander
            2 => 5,                    // Elezen
            3 => 11,                   // Lalafell
            4 => 7,                    // Miqo'te
            5 => 9,                    // Roegadyn
            6 => 13,                   // Au Ra
            7 => 15,                   // Hrothgar
            8 => 17,                   // Viera
            _ => 0
        };
        if (modelBase == 0) return null;
        return $"c{(modelBase + gender):D2}01";
    }

    /// <summary>Get the player's current job's animation subfolder (e.g., "bt_chk_chk/" for DNC).</summary>
    private string? GetPlayerJobSubfolder()
    {
        var player = ObjectTable.LocalPlayer;
        if (player == null) return null;

        return player.ClassJob.RowId switch
        {
            1 or 19 => "bt_swd_sld/",  // GLA/PLD
            2 or 20 => "bt_clw_clw/",  // PGL/MNK
            3 or 21 => "bt_2ax_emp/",  // MRD/WAR
            4 or 22 => "bt_2sp_emp/",  // LNC/DRG
            5 or 23 => "bt_2bw_emp/",  // ARC/BRD
            6 or 24 => "bt_stf_sld/",  // CNJ/WHM
            7 or 25 => "bt_jst_sld/",  // THM/BLM
            26 or 27 => "bt_2bk_emp/", // ACN/SMN
            28 => "bt_2bk_emp/",       // SCH
            29 or 30 => "bt_dgr_dgr/", // ROG/NIN
            31 => "bt_2gn_emp/",       // MCH
            32 => "bt_2sw_emp/",       // DRK
            33 => "bt_2gl_emp/",       // AST
            34 => "bt_2kt_emp/",       // SAM
            35 => "bt_2rp_emp/",       // RDM
            36 => "bt_rod_emp/",       // BLU
            37 => "bt_2gb_emp/",       // GNB
            38 => "bt_chk_chk/",       // DNC
            39 => "bt_2km_emp/",       // RPR
            40 => "bt_2ff_emp/",       // SGE
            41 => "bt_bld_bld/",       // VPR
            42 => "bt_brs_plt/",       // PCT
            _ => null                  // DoH/DoL/Unknown
        };
    }

    /// <summary>Pick the path best matching the player's race and job. Falls back to race-only, then first path.</summary>
    private static string PickBestPath(List<string> paths, string? raceCode, string? jobSubfolder)
    {
        // Best: matches both race and job subfolder
        if (raceCode != null && jobSubfolder != null)
        {
            var match = paths.FirstOrDefault(p => p.Contains($"/{raceCode}/") && p.Contains($"/{jobSubfolder}"));
            if (match != null) return match;
        }
        // Next: matches race only
        if (raceCode != null)
        {
            var match = paths.FirstOrDefault(p => p.Contains($"/{raceCode}/"));
            if (match != null) return match;
        }
        // Body-type affinity fallback: prefer races with similar skeleton/proportions.
        // e.g., Au Ra F (c1401) -> Miqo F (c0801), NOT Elezen F (c0601)
        if (raceCode != null && RaceAnimationAffinity.TryGetValue(raceCode, out var affinityChain))
        {
            foreach (var fallbackRace in affinityChain)
            {
                var match = paths.FirstOrDefault(p => p.Contains($"/{fallbackRace}/"));
                if (match != null) return match;
            }
        }
        // Last resort gender fallback: any path with the same gender parity.
        if (raceCode != null && int.TryParse(raceCode.Substring(1, 2), out int playerModelNum))
        {
            bool playerIsMale = playerModelNum % 2 != 0;
            foreach (var p in paths)
            {
                var cIdx = p.IndexOf("/c", StringComparison.Ordinal);
                if (cIdx >= 0 && cIdx + 4 < p.Length && int.TryParse(p.Substring(cIdx + 2, 2), out int pathModelNum))
                {
                    if ((pathModelNum % 2 != 0) == playerIsMale)
                        return p;
                }
            }
        }
        return paths[0];
    }

    // walks default_mod.json -> group_*.json picks; substitutes race for single-race mods
    private byte[]? ReadPresetModFile(DancePreset preset, string gamePath, PresetModifier? modifier = null)
    {
        if (PenumbraService == null) return null;
        var penumbraModDir = PenumbraService.GetModDirectory();
        if (string.IsNullOrEmpty(penumbraModDir)) return null;
        var modRoot = Path.Combine(penumbraModDir, preset.ModDirectory);
        if (!Directory.Exists(modRoot)) return null;

        var candidates = new List<string> { gamePath };
        var currentRace = ExtractRaceCodeFromPath(gamePath);
        if (currentRace != null)
        {
            foreach (var altRace in RaceIds.Where(r => r != currentRace))
                candidates.Add(gamePath.Replace($"/{currentRace}/", $"/{altRace}/"));
        }

        // Highest-priority match wins. Groups override default when the preset selects their options.
        string? chosenRel = null;
        int chosenPriority = int.MinValue;

        foreach (var groupFile in Directory.GetFiles(modRoot, "group_*.json"))
        {
            try
            {
                var groupJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(groupFile));
                var groupName = groupJson["Name"]?.Value<string>() ?? "";
                var groupPri = groupJson["Priority"]?.Value<int>() ?? 0;
                var options = groupJson["Options"] as Newtonsoft.Json.Linq.JArray;
                if (options == null) continue;

                // Effective options = preset options overlaid with modifier option overrides.
                var effectiveOptions = new Dictionary<string, List<string>>(preset.ModOptions);
                if (modifier != null)
                    foreach (var (g, v) in modifier.OptionOverrides)
                        effectiveOptions[g] = v;

                // If the preset/modifier explicitly selected options for this group, only those apply.
                // Otherwise skip this group - we can't guess which option would be active.
                if (!effectiveOptions.TryGetValue(groupName, out var selected) || selected == null || selected.Count == 0)
                    continue;

                foreach (var opt in options)
                {
                    var optName = opt["Name"]?.Value<string>() ?? "";
                    if (!selected.Any(s => s.Equals(optName, StringComparison.OrdinalIgnoreCase))) continue;
                    var files = opt["Files"] as Newtonsoft.Json.Linq.JObject;
                    if (files == null) continue;
                    foreach (var cand in candidates)
                    {
                        if (files.TryGetValue(cand, out var tok))
                        {
                            var rel = tok?.Value<string>();
                            if (!string.IsNullOrEmpty(rel) && groupPri >= chosenPriority)
                            {
                                chosenPriority = groupPri;
                                chosenRel = rel;
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Debug($"[Duration] group file parse skipped: {ex.Message}"); }
        }

        // Default file - lowest priority, used if no group option covered this path.
        if (chosenRel == null)
        {
            var defaultJsonPath = Path.Combine(modRoot, "default_mod.json");
            if (File.Exists(defaultJsonPath))
            {
                try
                {
                    var defaultJson = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(defaultJsonPath));
                    var files = defaultJson["Files"] as Newtonsoft.Json.Linq.JObject;
                    if (files != null)
                    {
                        foreach (var cand in candidates)
                        {
                            if (files.TryGetValue(cand, out var tok))
                            {
                                chosenRel = tok?.Value<string>();
                                if (!string.IsNullOrEmpty(chosenRel)) break;
                            }
                        }
                    }
                }
                catch (Exception ex) { Log.Debug($"[Duration] default_mod.json parse skipped: {ex.Message}"); }
            }
        }

        if (!string.IsNullOrEmpty(chosenRel))
        {
            var full = Path.Combine(modRoot, chosenRel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                try { return File.ReadAllBytes(full); }
                catch (Exception ex) { Log.Debug($"[Duration] read failed '{full}': {ex.Message}"); }
            }
        }

        // Some mods ship files mirrored at the game path with no JSON mapping. Last-resort probe.
        foreach (var cand in candidates)
        {
            var mirrored = Path.Combine(modRoot, cand.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(mirrored))
            {
                try { return File.ReadAllBytes(mirrored); }
                catch { }
            }
        }

        return null;
    }

    // Cache of loop durations keyed by "{modDirectory}|{emoteCommand}" (lowercase). Null = attempted and failed;
    // missing key = never tried. Session-scoped - rebuilt on plugin reload.
    private readonly Dictionary<string, float?> presetDurationCache = new();
    private readonly HashSet<string> presetDurationInFlight = new();

    // Status of a preset's loop-duration compute.
    public enum LoopDurationState { NotApplicable, Measuring, Unavailable, Available }

    // Tri-state lookup. State distinguishes still-computing from permanently-failed so the UI
    // can show "measuring..." once and stop afterward. Duration is only valid when State == Available.
    public (LoopDurationState state, float duration) GetPresetLoopDurationState(DancePreset preset, PresetModifier? modifier = null)
    {
        if (preset == null || string.IsNullOrWhiteSpace(preset.ModDirectory))
            return (LoopDurationState.NotApplicable, 0f);
        if (emoteCommandToId == null || emoteIdToTimelineKeys == null)
            return (LoopDurationState.NotApplicable, 0f);

        var effectiveCommand = modifier?.EmoteCommandOverride ?? preset.EmoteCommand;
        if (string.IsNullOrWhiteSpace(effectiveCommand))
            return (LoopDurationState.NotApplicable, 0f);
        var effectiveAnimType = modifier?.EmoteCommandOverride != null
            ? (int)GetAnimationTypeForCommand(modifier.EmoteCommandOverride)
            : preset.AnimationType;
        if (effectiveAnimType != 1)
            return (LoopDurationState.NotApplicable, 0f);

        var key = $"{preset.ModDirectory}|{effectiveCommand.ToLowerInvariant()}|{modifier?.Name ?? ""}";
        if (presetDurationCache.TryGetValue(key, out var cached))
            return cached.HasValue
                ? (LoopDurationState.Available, cached.Value)
                : (LoopDurationState.Unavailable, 0f);
        if (presetDurationInFlight.Contains(key))
            return (LoopDurationState.Measuring, 0f);

        presetDurationInFlight.Add(key);
        _ = Task.Run(() => ComputePresetDurationAsync(preset, modifier, effectiveCommand, key));
        return (LoopDurationState.Measuring, 0f);
    }

    // Backwards-compat shim - returns the duration when available, null otherwise. Callers that
    // need to distinguish measuring from failed should use GetPresetLoopDurationState.
    public float? GetPresetLoopDuration(DancePreset preset, PresetModifier? modifier = null)
    {
        var (state, dur) = GetPresetLoopDurationState(preset, modifier);
        return state == LoopDurationState.Available ? dur : null;
    }

    private async Task ComputePresetDurationAsync(DancePreset preset, PresetModifier? modifier, string effectiveCommand, string key)
    {
        try
        {
            Log.Information($"[Duration] Computing for '{preset.Name}' cmd='{effectiveCommand}' mod='{modifier?.Name ?? "-"}'");
            if (!emoteCommandToId!.TryGetValue(effectiveCommand.TrimStart('/'), out var emoteId) &&
                !emoteCommandToId!.TryGetValue(effectiveCommand, out emoteId) &&
                !emoteCommandToId!.TryGetValue("/" + effectiveCommand.TrimStart('/'), out emoteId))
            {
                Log.Warning($"[Duration] No emote ID for command '{effectiveCommand}' - giving up");
                presetDurationCache[key] = null;
                return;
            }
            if (!emoteIdToTimelineKeys!.TryGetValue(emoteId, out var timelineKeys) || timelineKeys.Count == 0)
            {
                Log.Warning($"[Duration] No timeline keys for emote ID {emoteId} ('{effectiveCommand}')");
                presetDurationCache[key] = null;
                return;
            }

            // Prefer slot 0 (main/loop). Fall back to slot 1 if slot 0 is missing.
            var main = timelineKeys.FirstOrDefault(t => t.slot == 0);
            if (string.IsNullOrEmpty(main.key))
                main = timelineKeys.First();
            Log.Debug($"[Duration] Timeline key: '{main.key}' (slot {main.slot}, loop={main.isLoop})");

            // read mod file directly: ResolvePlayerPath returns the priority winner, often vanilla
            string? chosenGamePath = null;
            await Framework.RunOnFrameworkThread(() =>
            {
                var paths = ResolveEmotePaths(main.key);
                if (paths.Count == 0) return;
                var raceCode = GetPlayerRaceCode();
                var jobSub = GetPlayerJobSubfolder();
                chosenGamePath = PickBestPath(paths, raceCode, jobSub);
            });
            if (chosenGamePath == null)
            {
                Log.Warning($"[Duration] Couldn't resolve a game path for timeline key '{main.key}'");
                presetDurationCache[key] = null;
                return;
            }
            Log.Debug($"[Duration] Game path: {chosenGamePath}");

            var modBytes = ReadPresetModFile(preset, chosenGamePath, modifier);
            var fromMod = modBytes != null;
            var papBytes = modBytes
                           ?? await Framework.RunOnFrameworkThread(() => DataManager.GetFile(chosenGamePath)?.Data?.ToArray());
            if (papBytes == null || papBytes.Length == 0)
            {
                Log.Warning($"[Duration] Neither mod nor game data returned bytes for '{chosenGamePath}'");
                presetDurationCache[key] = null;
                return;
            }
            Log.Debug($"[Duration] Read {papBytes.Length} bytes from {(fromMod ? "mod" : "game data")}");

            // Havok runtime is not thread-safe - parse on the framework thread.
            float? duration = null;
            await Framework.RunOnFrameworkThread(() =>
            {
                duration = PapDurationReader.ReadFromPapBytes(papBytes);
            });

            if (duration.HasValue)
                Log.Information($"[Duration] '{preset.Name}' = {duration.Value:F3}s (from {(fromMod ? "mod" : "vanilla")})");
            else
                Log.Warning($"[Duration] Havok parse returned null for '{preset.Name}' (pap={papBytes.Length}b)");

            presetDurationCache[key] = duration;
        }
        catch (Exception ex)
        {
            Log.Debug($"[Duration] Failed for '{preset.Name}': {ex.Message}");
            presetDurationCache[key] = null;
        }
        finally
        {
            presetDurationInFlight.Remove(key);
        }
    }

    /// <summary>Read the first internal animation name from a PAP file's binary data.</summary>
    private string? ReadPapAnimationName(byte[] papData)
    {
        if (papData.Length < 0x20) return null;

        // Check "pap" magic
        if (papData[0] != 0x70 || papData[1] != 0x61 || papData[2] != 0x70)
            return null;

        ushort animCount = BitConverter.ToUInt16(papData, 0x06);
        if (animCount == 0) return null;

        // Try two possible offsets for the animation info section
        uint infoOffset = BitConverter.ToUInt32(papData, 0x0E);
        if (infoOffset == 0 || infoOffset >= papData.Length)
        {
            infoOffset = BitConverter.ToUInt32(papData, 0x10);
            if (infoOffset == 0 || infoOffset >= papData.Length) return null;
        }

        int nameStart = (int)infoOffset;
        int nameEnd = nameStart;
        while (nameEnd < papData.Length && papData[nameEnd] != 0)
            nameEnd++;

        if (nameEnd == nameStart) return null;
        return Encoding.ASCII.GetString(papData, nameStart, nameEnd - nameStart);
    }

    // C009.Path string table is fixed-size; carrier name must fit (use FindCarrier maxNameLen)
    private (byte[]? data, int replaced, int found) RewritePapAnimationNames(byte[] data, string targetName, string carrierName)
    {
        var result = (byte[])data.Clone();
        var targetBytes = Encoding.ASCII.GetBytes(targetName);
        var carrierBytes = Encoding.ASCII.GetBytes(carrierName);
        int replaced = 0;
        int found = 0;

        for (int i = 0; i <= result.Length - targetBytes.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < targetBytes.Length; j++)
            {
                if (result[i + j] != targetBytes[j]) { match = false; break; }
            }
            if (!match) continue;

            // Verify null-terminated (don't match partial strings)
            if (i + targetBytes.Length < result.Length && result[i + targetBytes.Length] != 0)
                continue;

            found++;

            // Determine available padded space at this occurrence
            int available = targetBytes.Length;
            while (i + available < result.Length && result[i + available] == 0)
                available++;

            if (carrierBytes.Length + 1 <= available)
            {
                // Fits - replace in place
                Array.Copy(carrierBytes, 0, result, i, carrierBytes.Length);
                for (int j = carrierBytes.Length; j < available; j++)
                    result[i + j] = 0;
                replaced++;
                Log.Verbose($"[EmoteSwap] Replaced in-place at offset {i}: '{targetName}' -> '{carrierName}' ({carrierBytes.Length}/{available}b)");
            }
            else
            {
                Log.Warning($"[EmoteSwap] Cannot fit '{carrierName}' at offset {i} ({carrierBytes.Length + 1}b needed, {available}b available), skipping");
            }
        }

        if (found == 0)
        {
            Log.Warning($"[EmoteSwap] Animation name '{targetName}' not found in PAP data ({data.Length} bytes)");
            return (null, 0, 0);
        }

        Log.Debug($"[EmoteSwap] Rewrote {replaced}/{found} occurrence(s) of '{targetName}' -> '{carrierName}'");
        return (result, replaced, found);
    }

    private byte[]? ReadTargetFile(string gamePath, bool readFromMod)
    {
        if (readFromMod && PenumbraService?.IsAvailable == true)
        {
            // First: try the exact requested path
            var resolvedPath = PenumbraService.ResolvePlayerPath(gamePath);
            Log.Information($"[ReadTargetFile] ResolvePlayerPath: {gamePath} -> {resolvedPath ?? "<null>"}");
            if (resolvedPath != null && resolvedPath != gamePath && File.Exists(resolvedPath))
            {
                try
                {
                    var data = File.ReadAllBytes(resolvedPath);
                    // Compare size against the vanilla file - if they match, Penumbra resolved
                    // to a mod file that is identical to vanilla (the mod's actual change happens
                    // via meta/option redirection that ResolvePlayerPath doesn't evaluate).
                    var vanillaFile = DataManager.GetFile(gamePath);
                    if (vanillaFile != null && vanillaFile.Data.Length == data.Length)
                        Log.Warning($"[ReadTargetFile] Resolved path '{resolvedPath}' matches vanilla size ({data.Length} bytes) - mod may use meta/option redirection that bypass can't capture");
                    else
                        Log.Debug($"[ReadTargetFile] Read mod file: {data.Length} bytes (vanilla would be {vanillaFile?.Data.Length ?? 0})");
                    return data;
                }
                catch (Exception ex) { Log.Warning($"[ReadTargetFile] Failed to read mod file '{resolvedPath}': {ex.Message}"); }
            }

            // ResolvePlayerPath doesn't walk race affinity; do it manually then fall back to all races
            var currentRace = ExtractRaceCodeFromPath(gamePath);
            if (currentRace != null)
            {
                var triedRaces = new HashSet<string> { currentRace };
                var candidateRaces = new List<string>();
                if (RaceAnimationAffinity.TryGetValue(currentRace, out var affinity))
                    candidateRaces.AddRange(affinity);
                // Then all other races (gender-parity first for better skeleton match)
                var isMaleRace = currentRace.Length >= 5 && currentRace[4] == '1';  // c##01 odd trailing = male
                candidateRaces.AddRange(RaceIds.Where(r =>
                    r.Length >= 5 && (r[4] == '1') == isMaleRace));
                candidateRaces.AddRange(RaceIds.Where(r => r.Length >= 5 && (r[4] == '1') != isMaleRace));

                foreach (var altRace in candidateRaces)
                {
                    if (!triedRaces.Add(altRace)) continue;
                    var altPath = gamePath.Replace($"/{currentRace}/", $"/{altRace}/");
                    if (altPath == gamePath) continue;
                    var altResolved = PenumbraService.ResolvePlayerPath(altPath);
                    if (altResolved != null && altResolved != altPath && File.Exists(altResolved))
                    {
                        try
                        {
                            Log.Debug($"[ReadTargetFile] Race fallback: {currentRace} -> {altRace} (mod file found)");
                            return File.ReadAllBytes(altResolved);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[ReadTargetFile] Failed to read fallback mod file '{altResolved}': {ex.Message}");
                        }
                    }
                }
                Log.Debug($"[ReadTargetFile] No mod file found for any race - falling back to vanilla ({gamePath})");
            }
        }
        var file = DataManager.GetFile(gamePath);
        return file?.Data;
    }

    /// <summary>Extracts the cXX01 race code from a chara/human/cXX01/... path, or null if not present.</summary>
    private static string? ExtractRaceCodeFromPath(string path)
    {
        const string prefix = "chara/human/";
        var idx = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var start = idx + prefix.Length;
        if (start + 5 > path.Length) return null;
        if (path[start] != 'c') return null;
        return path.Substring(start, 5);
    }

    // creates a real on-disk Penumbra mod, AddMod -> ReloadMod -> SetTemporaryModSettings.
    // readTargetFromMod=true for EmoteLocked presets (target PAP/TMB come from mod, not game data)
    private bool SetupEmoteSwap(List<(string carrierKey, string targetKey)> keyPairs, bool readTargetFromMod = false)
    {
        if (PenumbraService == null) return false;

        try
        {
            ClearEmoteSwap();

            // Unique suffix for file names (avoids Penumbra file-content caching by path)
            var swapId = (++emoteSwapFileCounter).ToString();

            // Determine directories for both approaches
            var configSwapDir = EmoteSwapDir;
            Directory.CreateDirectory(configSwapDir);

            var allMappings = new Dictionary<string, string>(); // game path -> absolute file path
            int successfulPairs = 0;

            // Use race+job-specific PAP for correct skeleton data
            var playerRaceCode = GetPlayerRaceCode();
            var playerJobSub = GetPlayerJobSubfolder();

            for (int i = 0; i < keyPairs.Count; i++)
            {
                var (carrierKey, targetKey) = keyPairs[i];
                Log.Debug($"[EmoteSwap] Pair {i}: carrier='{carrierKey}', target='{targetKey}'");

                // Load target PAP - from mod (EmoteLocked preset) or game data
                var targetPaths = ResolveEmotePaths(targetKey);
                if (targetPaths.Count == 0) { Log.Warning($"[EmoteSwap] Pair {i}: No target paths found"); continue; }

                // Diagnostic: log all resolved paths with sizes to identify gender/race PAP differences
                if (i == 0)
                {
                    var pathSummary = new System.Text.StringBuilder();
                    foreach (var tp in targetPaths)
                    {
                        var f = DataManager.GetFile(tp);
                        var raceTag = tp.Contains("/c") ? tp.Substring(tp.IndexOf("/c") + 1, 5) : "global";
                        pathSummary.Append($"  {raceTag}: {f?.Data.Length ?? -1}b\n");
                    }
                    Log.Information($"[EmoteSwap] Target '{targetKey}' - {targetPaths.Count} resolved paths:\n{pathSummary}");
                }

                var selectedTargetPath = PickBestPath(targetPaths, playerRaceCode, playerJobSub);
                Log.Verbose($"[EmoteSwap] Pair {i}: target path={selectedTargetPath} (from {targetPaths.Count} resolved, race={playerRaceCode}, job={playerJobSub}, fromMod={readTargetFromMod})");
                var targetData = readTargetFromMod ? ReadTargetFile(selectedTargetPath, true) : DataManager.GetFile(selectedTargetPath)?.Data;
                if (targetData == null) { Log.Warning($"[EmoteSwap] Pair {i}: Can't read target PAP from {selectedTargetPath}"); continue; }

                // Load carrier PAP to read its actual internal animation name - prefer the player's race+job-specific file
                var carrierExistingPaths = ResolveEmotePaths(carrierKey);
                if (carrierExistingPaths.Count == 0) { Log.Warning($"[EmoteSwap] Pair {i}: No carrier paths for name lookup"); continue; }
                var selectedCarrierPath = PickBestPath(carrierExistingPaths, playerRaceCode, playerJobSub);
                Log.Verbose($"[EmoteSwap] Pair {i}: carrier path={selectedCarrierPath} (from {carrierExistingPaths.Count} resolved)");
                var carrierFile = DataManager.GetFile(selectedCarrierPath);
                if (carrierFile == null) { Log.Warning($"[EmoteSwap] Pair {i}: Can't read carrier PAP"); continue; }

                // Read the internal animation names from both PAPs
                var targetName = ReadPapAnimationName(targetData);
                var carrierName = ReadPapAnimationName(carrierFile.Data);
                if (targetName == null || carrierName == null)
                {
                    Log.Warning($"[EmoteSwap] Pair {i}: Can't read PAP names (target={targetName}, carrier={carrierName})");
                    continue;
                }

                Log.Debug($"[EmoteSwap] Pair {i}: rewriting '{targetName}' -> '{carrierName}' ({targetData.Length} bytes)");

                // Rewrite target PAP: replace animation name to match carrier's expected name.
                // The surgical TMB-aware rewriter extends the string table when needed,
                // so partial rewrites due to C009 size constraints no longer occur.
                var (modifiedData, replaced, found) = RewritePapAnimationNames(targetData, targetName, carrierName);
                if (modifiedData == null)
                {
                    Log.Warning($"[EmoteSwap] Pair {i}: Name rewrite failed - no occurrences found");
                    continue;
                }

                if (replaced < found)
                {
                    Log.Warning($"[EmoteSwap] Pair {i}: Incomplete rewrite ({replaced}/{found}) - some occurrences could not be updated");
                    if (i == 0) { Log.Warning($"[EmoteSwap] Slot 0 incomplete - swap may not work correctly"); }
                    else { continue; } // Skip non-critical slots
                }

                // Save modified PAP with unique file name (to both directories)
                var fileName = $"swap_{i}_{swapId}.pap";
                var configPath = Path.Combine(configSwapDir, fileName);
                File.WriteAllBytes(configPath, modifiedData);

                // Generate ALL possible carrier paths (every race x layer x subfolder).
                var carrierAllPaths = GenerateAllPossiblePaths(carrierKey);
                foreach (var cp in carrierAllPaths)
                {
                    allMappings[cp] = configPath;
                }

                // redirect carrier TMB -> target TMB (timing/events/facial expression triggers)
                var carrierTmbPath = $"chara/action/{carrierKey}.tmb";
                var targetTmbPath = $"chara/action/{targetKey}.tmb";
                var targetTmbData = readTargetFromMod ? ReadTargetFile(targetTmbPath, true) : DataManager.GetFile(targetTmbPath)?.Data;
                if (targetTmbData != null)
                {
                    // Rewrite any target animation name references in the TMB to carrier name
                    var tmbData = targetTmbData;
                    if (targetName != null && carrierName != null)
                    {
                        var (rewrittenTmb, tmbReplaced, tmbFound) = RewritePapAnimationNames(tmbData, targetName, carrierName);
                        if (rewrittenTmb != null && tmbReplaced > 0)
                        {
                            tmbData = rewrittenTmb;
                            Log.Debug($"[EmoteSwap] Pair {i}: TMB name rewrite {tmbReplaced}/{tmbFound} '{targetName}' -> '{carrierName}'");
                        }
                    }
                    var tmbFileName = $"swap_tmb_{i}_{swapId}.tmb";
                    var tmbConfigPath = Path.Combine(configSwapDir, tmbFileName);
                    File.WriteAllBytes(tmbConfigPath, tmbData);
                    allMappings[carrierTmbPath] = tmbConfigPath;
                    Log.Debug($"[EmoteSwap] Pair {i}: TMB redirect {carrierTmbPath} -> {tmbFileName} ({tmbData.Length} bytes)");
                }
                else
                {
                    Log.Debug($"[EmoteSwap] Pair {i}: No target TMB at {targetTmbPath}");
                }

                Log.Debug($"[EmoteSwap] Pair {i}: {replaced}/{found} rewrites OK, {carrierAllPaths.Count} carrier paths -> {fileName}");
                successfulPairs++;
            }

            if (allMappings.Count == 0)
            {
                Log.Warning($"[EmoteSwap] No valid mappings from {keyPairs.Count} pair(s)");
                return false;
            }

            var (success, collectionId, _) = PenumbraService.GetCurrentCollection();
            if (!success) { Log.Warning("[EmoteSwap] Can't get collection"); return false; }

            emoteSwapPriority = 200;
            emoteSwapCollectionId = collectionId;

            // === PRIMARY: Real Penumbra mod on disk (visible to sync plugins) ===
            var penumbraModDir = PenumbraService.GetModDirectory();
            if (penumbraModDir != null)
            {
                try
                {
                    var realModDir = Path.Combine(penumbraModDir, EncoreSwapModName);
                    Directory.CreateDirectory(realModDir);

                    // Copy swap PAP files to the real mod directory and build relative mappings
                    var relMappings = new Dictionary<string, string>(); // game path -> local filename
                    foreach (var (gamePath, absPath) in allMappings)
                    {
                        var fileName = Path.GetFileName(absPath);
                        var destPath = Path.Combine(realModDir, fileName);
                        if (!File.Exists(destPath))
                            File.Copy(absPath, destPath, true);
                        relMappings[gamePath] = fileName;
                    }

                    // Write default_mod.json with file mappings
                    var filesJson = string.Join(",\n", relMappings.Select(kv =>
                        $"    \"{kv.Key.Replace("\\", "/")}\": \"{kv.Value}\""));
                    var defaultModJson = $"{{\n  \"Name\": \"\",\n  \"Priority\": 0,\n  \"Files\": {{\n{filesJson}\n  }},\n  \"FileSwaps\": {{}},\n  \"Manipulations\": []\n}}";
                    File.WriteAllText(Path.Combine(realModDir, "default_mod.json"), defaultModJson);

                    // Write meta.json on first registration
                    var metaPath = Path.Combine(realModDir, "meta.json");
                    if (!File.Exists(metaPath))
                    {
                        var metaJson = "{\n  \"FileVersion\": 3,\n  \"Name\": \"_EncoreEmoteSwap\",\n  \"Author\": \"Encore\",\n  \"Description\": \"Auto-generated by Encore for emote unlock bypass.\",\n  \"Version\": \"1.0.0\",\n  \"Website\": \"\",\n  \"ModTags\": []\n}";
                        File.WriteAllText(metaPath, metaJson);
                    }

                    // Register or reload the mod
                    if (!encoreSwapModRegistered)
                    {
                        // AddMod triggers async file compaction; ReloadMod is required to activate redirects
                        var addOk = PenumbraService.AddMod(EncoreSwapModName);
                        if (addOk)
                        {
                            encoreSwapModRegistered = true;
                            Log.Debug($"[EmoteSwap] Real mod registered via AddMod");
                            // Brief delay for async file compaction to finish before ReloadMod reads the files
                            System.Threading.Thread.Sleep(50);
                            PenumbraService.ReloadMod(EncoreSwapModName);
                            Log.Debug("[EmoteSwap] Real mod reloaded after AddMod");
                        }
                        else
                        {
                            Log.Warning("[EmoteSwap] AddMod failed - falling back to AddTemporaryMod only");
                        }
                    }
                    else
                    {
                        // Already registered: ReloadMod picks up new default_mod.json
                        var reloadOk = PenumbraService.ReloadMod(EncoreSwapModName);
                        if (!reloadOk)
                            Log.Warning("[EmoteSwap] ReloadMod failed");
                        else
                            Log.Debug("[EmoteSwap] Real mod reloaded");
                    }

                    // Enable the real mod via temporary settings (priority 200 to override user mods)
                    if (encoreSwapModRegistered)
                    {
                        var tempSettingsOk = PenumbraService.SetTemporaryModSettings(
                            collectionId, EncoreSwapModName,
                            true, emoteSwapPriority,
                            new Dictionary<string, List<string>>(),
                            EncoreSwapModName);
                        if (!tempSettingsOk)
                            Log.Warning("[EmoteSwap] SetTemporaryModSettings for real mod failed");
                        else
                            Log.Debug($"[EmoteSwap] Real mod enabled via temp settings, priority={emoteSwapPriority}");
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning($"[EmoteSwap] Real mod setup failed: {ex.Message} - using AddTemporaryMod only");
                }
            }

            if (!encoreSwapModRegistered)
            {
                Log.Warning("[EmoteSwap] Real mod failed to register - unlock bypass unavailable");
                return false;
            }

            Log.Information($"[EmoteSwap] Active: {allMappings.Count} paths from {successfulPairs}/{keyPairs.Count} pair(s)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[EmoteSwap] Failed: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    private void ClearEmoteSwap()
    {
        var wasActive = emoteSwapActive;
        emoteSwapActive = false;

        if (PenumbraService?.IsAvailable == true)
        {
            // Remove real mod's temporary settings
            if (encoreSwapModRegistered)
            {
                try
                {
                    if (emoteSwapCollectionId != Guid.Empty)
                        PenumbraService.RemoveTemporaryModSettings(emoteSwapCollectionId, EncoreSwapModName);

                    var (ok2, currentCollId2, _) = PenumbraService.GetCurrentCollection();
                    if (ok2 && currentCollId2 != Guid.Empty && currentCollId2 != emoteSwapCollectionId)
                        PenumbraService.RemoveTemporaryModSettings(currentCollId2, EncoreSwapModName);
                }
                catch { }

                // Write empty default_mod.json and reload to clear file redirects
                var penumbraModDir = PenumbraService.GetModDirectory();
                if (penumbraModDir != null)
                {
                    try
                    {
                        var realModDir = Path.Combine(penumbraModDir, EncoreSwapModName);
                        if (Directory.Exists(realModDir))
                        {
                            File.WriteAllText(Path.Combine(realModDir, "default_mod.json"),
                                "{\n  \"Name\": \"\",\n  \"Priority\": 0,\n  \"Files\": {},\n  \"FileSwaps\": {},\n  \"Manipulations\": []\n}");
                            PenumbraService.ReloadMod(EncoreSwapModName);

                            // Delete swap PAP and TMB files from real mod dir
                            foreach (var f in Directory.GetFiles(realModDir, "swap_*.pap"))
                            {
                                try { File.Delete(f); } catch { }
                            }
                            foreach (var f in Directory.GetFiles(realModDir, "swap_tmb_*.tmb"))
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug($"[EmoteSwap] Real mod cleanup: {ex.Message}");
                    }
                }
            }
        }

        emoteSwapCollectionId = Guid.Empty;
        emoteSwapPriority = 0;
        lastBypassEmoteCommand = null;
        lastBypassCarrierCommand = null;
        lastBypassCarrierId = 0;
        lastBypassRaceCode = null;
        emoteSwapSoftDisabled = false;
        // forces AddMod + 50ms + ReloadMod on next setup; bare ReloadMod doesn't reactivate redirects
        encoreSwapModRegistered = false;


        try
        {
            var swapDir = EmoteSwapDir;
            if (Directory.Exists(swapDir))
            {
                foreach (var f in Directory.GetFiles(swapDir, "swap_*.pap"))
                {
                    try { File.Delete(f); } catch { }
                }
                foreach (var f in Directory.GetFiles(swapDir, "swap_tmb_*.tmb"))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[EmoteSwap] Config cleanup: {ex.Message}");
        }

        if (wasActive)
            Log.Debug("[EmoteSwap] Cleared");
    }

    private unsafe void ExecuteRedraw()
    {
        try
        {
            // Execute via game's chat system (same as emotes)
            var uiModule = UIModule.Instance();
            if (uiModule != null)
            {
                using var str = new Utf8String("/penumbra redraw self");
                uiModule->ProcessChatBoxEntry(&str);
                Log.Debug("Executed Penumbra redraw for pose mod");
            }
            else
            {
                Log.Warning("UIModule not available, cannot execute redraw");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to execute redraw: {ex.Message}");
        }
    }

    // ExecuteCommandInner hook: locked emote -> route through bypass; carrier typed during swap -> clear swap
    private unsafe void DetourExecuteCommandInner(ShellCommandModule* commandModule, Utf8String* rawMessage, UIModule* uiModule)
    {
        try
        {
            var msgSpan = rawMessage != null
                ? new ReadOnlySpan<byte>(rawMessage->StringPtr, (int)rawMessage->Length)
                : ReadOnlySpan<byte>.Empty;
            var message = Encoding.UTF8.GetString(msgSpan).Trim();

            if (!string.IsNullOrEmpty(message) && message.StartsWith('/'))
            {
                var commandPart = message.Split(' ')[0].ToLowerInvariant();

                // user typing the carrier command is the explicit "off switch"; skip our own bypass calls
                if (emoteSwapActive && !isExecutingBypassCarrier && lastBypassCarrierCommand != null &&
                    string.Equals(commandPart, lastBypassCarrierCommand, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information($"[CommandHook] Carrier '{commandPart}' typed while swap active - clearing swap so real emote plays");
                    ClearEmoteSwap();
                    // Fall through to Original - game plays the real carrier emote
                }
                // Locked emote bypass: intercept locked emote commands and route through our bypass
                else if (Configuration.AllowUnlockedEmotes && emoteCommandToId != null &&
                    emoteCommandToId.TryGetValue(commandPart, out var emoteId) &&
                    !IsEmoteUnlocked(emoteId))
                {
                    Log.Information($"[CommandHook] Intercepted locked emote '{commandPart}' (id={emoteId}), routing to bypass");

                    // Resolve alias to canonical command for mod matching
                    var emoteCmd = commandPart;
                    var lookupKey = emoteCmd.TrimStart('/');
                    if (Services.EmoteDetectionService.EmoteToCommand.TryGetValue(lookupKey, out var canonical))
                        emoteCmd = canonical;

                    // Fire off the bypass async (same flow as /vanilla)
                    var capturedCmd = emoteCmd;
                    Task.Run(() =>
                    {
                        Framework.RunOnFrameworkThread(() => ExecuteVanilla(capturedCmd));
                    });

                    // DON'T call Original - suppress the game's "not learned" message
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[CommandHook] Error in detour: {ex.Message}");
        }

        // Normal path: let the game handle it
        executeCommandInnerHook!.Original(commandModule, rawMessage, uiModule);
    }

    /// <summary>
    /// ConditionFlag change handler - cancels /loop when player starts casting, mounts, crafts, etc.
    /// </summary>
    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (!value || loopingEmoteCommand == null) return;

        if (flag is ConditionFlag.Casting
            or ConditionFlag.Casting87
            or ConditionFlag.Mounted
            or ConditionFlag.OccupiedInEvent
            or ConditionFlag.OccupiedInQuestEvent
            or ConditionFlag.Crafting
            or ConditionFlag.ExecutingCraftingAction
            or ConditionFlag.PreparingToCraft
            or ConditionFlag.Gathering
            or ConditionFlag.ExecutingGatheringAction
            or ConditionFlag.OccupiedInCutSceneEvent
            or ConditionFlag.WatchingCutscene
            or ConditionFlag.WatchingCutscene78)
        {
            Log.Debug($"[EmoteLoop] Cancelled by condition flag: {flag}");
            ClearEmoteLoop();
        }
    }

    // skipped if no target, target is self, or player is sitting/dozing
    private unsafe void FaceTarget()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero) return;

        var target = TargetManager.Target ?? TargetManager.SoftTarget;
        if (target == null || target.Address == localPlayer.Address) return;

        var player = (Character*)localPlayer.Address;
        if (player == null) return;

        // Don't rotate if sitting, dozing, mounted, etc.
        if (player->Mode != CharacterModes.Normal) return;

        var rot = MathF.Atan2(
            target.Position.X - localPlayer.Position.X,
            target.Position.Z - localPlayer.Position.Z);

        player->GameObject.SetRotation(rot);
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        try
        {
            // BGM tracker polls regardless of preset state so it's ready when one starts
            BgmTrackerService?.Update();

            var localPlayer = ObjectTable.LocalPlayer;
            if (localPlayer == null) return;

            // Routine advancement / cancellation
            if (activeRoutine != null)
                UpdateActiveRoutine(localPlayer);
        }
        catch (Exception ex)
        {
            Log.Debug($"[Routine] update error: {ex.Message}");
        }

        // Active-emote-preset lifetime tracking - run independently so a routine exception
        // can't suppress it. Clears Configuration.ActivePresetId when the emote ends or the
        // player moves so the main window's "Running: ..." indicator disappears.
        try { UpdateActivePresetTracking(); }
        catch (Exception ex) { Log.Debug($"[ActivePreset] tracking error: {ex.Message}"); }

        var hasLoopActive = loopingEmoteCommand != null;
        if (!hasLoopActive) return;

        try
        {
            var localPlayer = ObjectTable.LocalPlayer;
            if (localPlayer == null) return;

            var currentEmoteId = ReadCurrentEmoteId();

            var currentTimeline = ReadBaseTimeline();

            // weapon check must precede emote re-execution; otherwise IsWeaponDrawn can't transition
            var isWeaponDrawn = ReadIsWeaponDrawn();
            if (isWeaponDrawn != loopPreviousWeaponDrawn)
            {
                Log.Debug($"[EmoteLoop] Cancelled by weapon state change (drawn={isWeaponDrawn})");
                ClearEmoteLoop();
                loopPreviousWeaponDrawn = isWeaponDrawn;
                previousEmoteId = currentEmoteId;
                previousTimeline = currentTimeline;
                return;
            }

            // Capture the emote's ID and timeline when it first starts playing
            if (loopWaitingForStart && currentEmoteId != previousEmoteId && currentEmoteId != 0)
            {
                if (loopingEmoteId == 0)
                {
                    // First execution - learn the emote's ID and timeline
                    loopingEmoteId = currentEmoteId;
                    emoteTimeline = currentTimeline;
                    loopWaitingForStart = false;
                    previousPosition = localPlayer.Position;
                }
                else if (currentEmoteId == loopingEmoteId)
                {
                    // Re-execution - same emote started again
                    emoteTimeline = currentTimeline;
                    loopWaitingForStart = false;
                    previousPosition = localPlayer.Position;
                }
                else
                {
                    // A different emote started - user did something else, cancel loop
                    ClearEmoteLoop();
                    previousEmoteId = currentEmoteId;
                    previousTimeline = currentTimeline;
                    return;
                }
            }

            // Detect emote end via EmoteId reverting
            if (loopingEmoteId != 0 && currentEmoteId != previousEmoteId && previousEmoteId == loopingEmoteId)
            {
                if (loopCarrierCommand != null)
                    ExecuteEmoteDirect(loopCarrierCommand);
                else
                    ExecuteEmote(loopingEmoteCommand);
                loopWaitingForStart = true;
            }

            // Detect emote end via timeline change (backup)
            if (loopingEmoteId != 0 && currentTimeline != previousTimeline &&
                previousTimeline == emoteTimeline && currentTimeline != emoteTimeline)
            {
                if (loopCarrierCommand != null)
                    ExecuteEmoteDirect(loopCarrierCommand);
                else
                    ExecuteEmote(loopingEmoteCommand);
                loopWaitingForStart = true;
            }

            previousEmoteId = currentEmoteId;
            previousTimeline = currentTimeline;

            // Cancel if player moves (skip while emote animation is active)
            if (currentEmoteId != loopingEmoteId)
            {
                var currentPos = localPlayer.Position;
                var dx = currentPos.X - previousPosition.X;
                var dz = currentPos.Z - previousPosition.Z;

                if (dx * dx + dz * dz > 0.001f)
                    ClearEmoteLoop();

                previousPosition = currentPos;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[EmoteLoop] Error: {ex.Message}");
        }
    }

    private unsafe ushort ReadCurrentEmoteId()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero) return 0;

        var player = (Character*)localPlayer.Address;
        if (player == null) return 0;

        return player->EmoteController.EmoteId;
    }

    // pose/movement presets are persistent (no-op via emoteSeen=true + emoteId=0)
    private void UpdateActivePresetTracking()
    {
        if (Configuration.ActivePresetId == null) return;
        if (activePresetEmoteSeen && activePresetEmoteId == 0) return;

        var preset = Configuration.Presets.Find(p => p.Id == Configuration.ActivePresetId);
        if (preset == null || preset.AnimationType != 1) return;

        ushort currentEmoteId;
        try { currentEmoteId = ReadCurrentEmoteId(); }
        catch { return; }

        if (!activePresetEmoteSeen)
        {
            // first frame: snapshot trailing emote to avoid latching onto previous preset
            if (!activePresetIgnoreSnapshotted)
            {
                activePresetEmoteIgnoreId = currentEmoteId;
                activePresetIgnoreSnapshotted = true;
                return;
            }

            if (currentEmoteId != 0)
            {
                bool graceExpired = Environment.TickCount64 >= activePresetIgnoreExpiryTicks;
                if (currentEmoteId != activePresetEmoteIgnoreId || graceExpired)
                {
                    activePresetEmoteId = currentEmoteId;
                    activePresetEmoteTimeline = ReadBaseTimeline();
                    activePresetEmoteSeen = true;
                    activePresetEndCandidateFrames = 0;
                    // re-apply heels now that Character.Mode is EmoteLoop/InPositionLoop (SH render path clears stale offsets)
                    if (preset.HeelsOffset != null && !preset.HeelsOffset.IsZero())
                        ApplyPresetHeels(preset);
                }
            }
            return;
        }

        // OR-evidence end detection with 15-frame debounce (~250ms at 60fps)
        ushort currentTimeline = ReadBaseTimeline();
        bool emoteIdStillOurs = currentEmoteId != 0 && currentEmoteId == activePresetEmoteId;
        bool timelineStillOurs = activePresetEmoteTimeline != 0
                                 && currentTimeline == activePresetEmoteTimeline;

        if (emoteIdStillOurs || timelineStillOurs)
        {
            activePresetEndCandidateFrames = 0;
        }
        else
        {
            activePresetEndCandidateFrames++;
            if (activePresetEndCandidateFrames >= 15)
            {
                Configuration.ActivePresetId = null;
                activePresetEmoteId = 0;
                activePresetEmoteTimeline = 0;
                activePresetEmoteSeen = false;
                activePresetEndCandidateFrames = 0;
                // Preset's heels offset was active - clear it now that the
                // emote has ended, or the character stays "floating" at the
                // preset's Y/rotation after the animation finishes.
                SimpleHeelsService?.ClearOffset();
            }
        }
    }

    private unsafe ushort ReadBaseTimeline()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero) return 0;

        var player = (Character*)localPlayer.Address;
        if (player == null) return 0;

        return player->Timeline.TimelineSequencer.GetSlotTimeline(0);
    }


    private unsafe bool ReadIsWeaponDrawn()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero) return false;

        var player = (Character*)localPlayer.Address;
        if (player == null) return false;

        return player->IsWeaponDrawn;
    }

    public void ClearEmoteLoop()
    {
        loopingEmoteCommand = null;
        loopingEmoteId = 0;
        emoteTimeline = 0;
        loopWaitingForStart = false;
        loopCarrierCommand = null;
        loopCarrierId = 0;
        ClearEmoteSwap();
    }

    private void PrintChat(string message, XivChatType type = XivChatType.Echo)
    {
        var seString = new SeStringBuilder()
            .AddUiForeground("[Encore] ", 35)
            .AddText(message)
            .Build();

        ChatGui.Print(new XivChatEntry
        {
            Message = seString,
            Type = type
        });
    }

    private void CheckForUpdates()
    {
        updateHttpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("Encore-FFXIV-Plugin");
        // Initial check after 8s, then every 30 minutes
        updateCheckTimer = new System.Threading.Timer(_ => DoUpdateCheck(), null, 8000, UpdateCheckIntervalMs);
    }

    private async void DoUpdateCheck()
    {
        if (updateNotified || !Configuration.ShowUpdateNotification)
            return;

        try
        {
            var response = await updateHttpClient.GetStringAsync(UpdateCheckUrl);
            var json = JArray.Parse(response);
            var remoteVersionStr = json[0]?["AssemblyVersion"]?.ToString();

            if (string.IsNullOrEmpty(remoteVersionStr))
                return;

            var remoteVersion = new Version(remoteVersionStr);
            var localVersion = typeof(Plugin).Assembly.GetName().Version;

            if (localVersion == null || remoteVersion <= localVersion)
                return;

            updateNotified = true;
            updateCheckTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            var seString = new SeStringBuilder()
                .AddUiForeground("[Encore] ", 35)
                .AddText("A new version is available: ")
                .AddUiForeground($"v{remoteVersion.ToString(3)}", 45)
                .AddText($" (current: v{localVersion.ToString(3)})")
                .Build();

            ChatGui.Print(new XivChatEntry
            {
                Message = seString,
                Type = XivChatType.Echo
            });
        }
        catch (Exception ex)
        {
            Log.Debug($"Update check failed: {ex.Message}");
        }
    }
}
