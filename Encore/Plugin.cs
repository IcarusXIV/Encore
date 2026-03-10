using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
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

    // Icons directory for custom preset images
    internal static string IconsDirectory => Path.Combine(PluginInterface.GetPluginConfigDirectory(), "icons");

    // Main command
    private const string MainCommand = "/encore";

    // Patch notes version — bump when patch notes content changes (not every build)
    public const string PatchNotesVersion = "1.0.0.5";

    // Configuration
    public Configuration Configuration { get; init; }

    // Services
    public PenumbraService? PenumbraService { get; private set; }
    public EmoteDetectionService? EmoteDetectionService { get; private set; }
    public MovementService? MovementService { get; private set; }
    public PoseService? PoseService { get; private set; }

    // Windows
    public readonly WindowSystem WindowSystem = new("Encore");
    private MainWindow MainWindow { get; init; }
    private PresetEditorWindow PresetEditorWindow { get; init; }
    private IconPickerWindow IconPickerWindow { get; init; }
    private HelpWindow HelpWindow { get; init; }
    private PatchNotesWindow PatchNotesWindow { get; init; }
    internal ImGuiFileBrowserWindow FileBrowserWindow { get; init; }

    // Dynamic command tracking
    private readonly HashSet<string> registeredPresetCommands = new();

    // Prevent overlapping preset executions
    private volatile bool isExecutingPreset = false;

    // Emote unlock bypass: Lumina-based lookup tables for PAP rewriting
    private Dictionary<string, ushort>? emoteCommandToId;
    // emote ID → all ActionTimeline entries (slot index, key string, isLoop flag, loadType: 0=Facial, 1=PerJob, 2=Normal)
    private Dictionary<ushort, List<(int slot, string key, bool isLoop, byte loadType)>>? emoteIdToTimelineKeys;
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
    // Carrier sticking: reuse the same carrier for the same emote across repeated bypass calls.
    // Penumbra's ChangeCounter-based cache invalidation ensures the game reloads resources when
    // mod settings change, so rotating carriers is unnecessary. Sticking enables sync plugin compatibility
    // by keeping the animation on a consistent carrier path.
    private string? lastBypassEmoteCommand = null;
    private string? lastBypassCarrierCommand = null;
    private ushort lastBypassCarrierId = 0;
    private string? lastBypassRaceCode = null;
    // One-shot emote swap auto-cleanup: detect when a one-shot carrier finishes and soft-disable the swap.
    // Soft disable = remove Penumbra temp settings (carrier returns to normal) but keep swap files + sticking
    // state intact so the next /vanilla call for the same emote can re-enable without full re-setup.
    private bool emoteSwapIsOneShot = false;
    private ushort emoteSwapCarrierId = 0;
    private bool emoteSwapWaitingForStart = false;
    private bool emoteSwapSoftDisabled = false;
    // Emote ConditionMode lookup: maps emote ID → EmoteMode.ConditionMode
    // 0 = works in any state (sitting, dozing, etc.), 3 = standing only
    private Dictionary<ushort, byte>? emoteIdToConditionMode;
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
        catch { /* Non-critical — custom icon resize will fall back to file copy */ }

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

        // Build emote command → ID and emote ID → timeline keys lookups from Lumina
        try
        {
            emoteCommandToId = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            emoteIdToTimelineKeys = new Dictionary<ushort, List<(int slot, string key, bool isLoop, byte loadType)>>();
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
                        }
                    }
                    if (entries.Count > 0)
                        emoteIdToTimelineKeys.TryAdd(emoteId, entries);

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
                }

                var emotesWithTimelines = emoteIdToTimelineKeys.Count;
                // Count emotes that have facial (LoadType 0) entries — needed for expression support in unlock bypass
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
        IconPickerWindow = new IconPickerWindow();
        HelpWindow = new HelpWindow();
        PatchNotesWindow = new PatchNotesWindow(this);
        FileBrowserWindow = new ImGuiFileBrowserWindow("Select Icon Image");
        FileBrowserWindow.SetConfiguration(Configuration);

        // Wire up window dependencies
        MainWindow.SetEditorWindow(PresetEditorWindow);
        MainWindow.SetHelpWindow(HelpWindow);
        MainWindow.SetPatchNotesWindow(PatchNotesWindow);
        PresetEditorWindow.SetIconPicker(IconPickerWindow);

        // Add windows to system
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(PresetEditorWindow);
        WindowSystem.AddWindow(IconPickerWindow);
        WindowSystem.AddWindow(HelpWindow);
        WindowSystem.AddWindow(PatchNotesWindow);
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
            // First-time user — mark patch notes as seen so they don't get both windows
            Configuration.LastSeenPatchNotesVersion = PatchNotesVersion;
            Configuration.Save();
        }
        else if (Configuration.LastSeenPatchNotesVersion != PatchNotesVersion &&
                 Configuration.ShowPatchNotesOnStartup)
        {
            // Existing user updating — show what's new
            PatchNotesWindow.IsOpen = true;
            Configuration.LastSeenPatchNotesVersion = PatchNotesVersion;
            Configuration.Save();
        }

        // Restore main window open state from last session
        if (Configuration.IsMainWindowOpen)
        {
            MainWindow.IsOpen = true;
        }

        // Hook UI drawing
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Framework.Update += OnFrameworkUpdate;

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
        // Unhook UI and framework
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

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
        MovementService?.Dispose();
        PoseService?.Dispose();
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

        // Default: toggle main window
        MainWindow.Toggle();
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

                // Wait for Penumbra to process the temp mod before executing
                Task.Run(async () =>
                {
                    await Task.Delay(150);
                    await Framework.RunOnFrameworkThread(() => ExecuteEmoteDirect(carrierCmd));
                });

                return;
            }
            // Bypass failed — fall through to normal (will fail silently via game rejection)
        }

        // Start looping (normal unlocked emote path)
        loopingEmoteCommand = emote;
        loopCarrierCommand = null;
        loopCarrierId = 0;
        loopingEmoteId = 0;
        emoteTimeline = 0;
        loopWaitingForStart = true;
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

        // Resolve FFXIV aliases (e.g., /dance5 → /balldance) so mod matching works
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

                // Execute the emote — set up bypass first if needed, then execute carrier
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
                // Wait for Penumbra's CollectionCache to update before executing.
                if (bypassCommand != null)
                    await Task.Delay(150);

                // Execute the carrier (bypassed) or original emote
                await Framework.RunOnFrameworkThread(() =>
                {
                    if (bypassCommand != null)
                    {
                        ExecuteEmoteDirect(bypassCommand);

                        // Enable one-shot auto-cleanup for non-looping emotes
                        if (!bypassTargetIsLoop)
                        {
                            emoteSwapIsOneShot = true;
                            emoteSwapCarrierId = bypassCarrierId;
                            emoteSwapWaitingForStart = true;
                        }
                    }
                    else
                    {
                        ExecuteEmote(emoteCmd);
                    }
                });

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

            // Check permanent state — skip mods that are already permanently disabled
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
    /// Execute a dance preset — adjust priorities, disable conflicts, and perform the emote/pose action.
    /// </summary>
    public void ExecutePreset(DancePreset preset, PresetModifier? modifier = null)
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

        // Snapshot which mods have active temp settings (from previous preset)
        var previousModsWithTemp = new HashSet<string>(Configuration.ModsWithTempSettings);

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
                }
                else
                {
                    // Apply new preset first (fast path)
                    newModsWithTemp = await ApplyPresetPriorities(preset, collectionId, modifier);

                    // Track this as the active preset in this collection
                    Configuration.ActivePresetId = preset.Id;
                    Configuration.ActivePresetCollectionId = collectionId.ToString();
                }

                // Determine effective emote command, animation type, and pose index
                // (modifier can override emote + pose, which changes the execution path)
                var effectiveEmoteCommand = preset.EmoteCommand;
                var effectiveAnimationType = preset.AnimationType;
                var effectivePoseIndex = preset.PoseIndex;
                if (modifier != null)
                {
                    // Emote command override changes the execution path
                    if (modifier.EmoteCommandOverride != null)
                    {
                        effectiveEmoteCommand = modifier.EmoteCommandOverride;
                        var cmdAnimType = GetAnimationTypeForCommand(modifier.EmoteCommandOverride);
                        if (cmdAnimType != 1) // Not a regular emote — it's a pose command
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

                // Execute emote/pose action (immediate - don't wait)
                // Movement presets always need redraw even if ExecuteEmote is false (legacy presets)
                if (preset.ExecuteEmote || preset.AnimationType == 6)
                {
                    var emoteCmd = effectiveEmoteCommand; // Capture for closure
                    var animType = effectiveAnimationType;
                    var poseIdx = effectivePoseIndex;
                    var hasModifier = modifier != null;
                    var presetHasModifiers = preset.Modifiers.Count > 0; // Preset supports variants — may need redraw even without modifier (returning to base)
                    var baseEmoteCmd = preset.EmoteCommand; // For detecting same-emote modifier switches

                    // EmoteLocked bypass: for locked emote presets, use carrier emote to play the mod's animation.
                    // This is async (needs 150ms delay for Penumbra) so it gets its own execution path.
                    if (preset.EmoteLocked && animType == 1 && !preset.IsVanilla)
                    {
                        string? bypassCommand = null;
                        ushort bypassCarrierId = 0;
                        bool bypassTargetIsLoop = false;

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
                            // Wait for Penumbra to process the real mod before executing.
                            // Wait for Penumbra's CollectionCache to update before executing.
                            await Task.Delay(150);

                            await Framework.RunOnFrameworkThread(() =>
                            {
                                ExecuteEmoteDirect(bypassCommand);

                                // Enable one-shot auto-cleanup for non-looping emotes
                                if (!bypassTargetIsLoop)
                                {
                                    emoteSwapIsOneShot = true;
                                    emoteSwapCarrierId = bypassCarrierId;
                                    emoteSwapWaitingForStart = true;
                                }
                            });
                        }
                        else
                        {
                            // Bypass failed — fall back to normal emote execution (will likely fail for locked emotes)
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

                            // Switching away from a movement preset — redraw to reload original walk/run animations
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
                                        // Already in pose state — don't re-execute sit/groundsit/doze (would toggle out)
                                        if (presetHasModifiers)
                                            ExecuteRedraw(); // Swap mod files (modifier→base or base→modifier)
                                        // Don't SetPoseIndex first (corrupts CPoseState, making cycling think it's already there)
                                        PoseService.CycleCPoseToIndex((byte)poseIdx);
                                    }
                                    else
                                    {
                                        // Not yet in pose state — do full execution
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

                                case 6: // Movement — just redraw to apply new walk/run animations
                                    ExecuteRedraw();
                                    break;

                                default: // AnimationType 1 (Emote) or unknown
                                    // Modifier with same emote + still in emote mode → redraw only (seamless option swap mid-dance)
                                    // Modifier with different emote, or stopped, or no modifier → execute emote
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
                                        ExecuteEmote(emoteCmd);
                                    break;
                            }
                        });
                    }
                }

                // Restore previous preset's temp settings in background (after emote starts)
                if (!isAlreadyActive && previousModsWithTemp.Count > 0)
                {
                    Log.Debug("Restoring previous preset changes in background...");
                    await RestorePreviousChanges(collectionId, previousModsWithTemp, preset.ModDirectory, newModsWithTemp);
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

            // Remove temp settings → auto-reverts to permanent/inherited state
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

        // Add emotes from mod analysis if available
        if (presetModInfo != null && presetModInfo.AffectedEmotes.Count > 0)
        {
            foreach (var emote in presetModInfo.AffectedEmotes)
                presetEmotes.Add(emote);
            foreach (var cmd in presetModInfo.EmoteCommands)
                presetEmoteCommands.Add(cmd.TrimStart('/').ToLowerInvariant());
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

            // Check permanent state — GetCurrentModSettings ignores temp overrides
            var (gotSettings, isEnabled, _, _) = PenumbraService!.GetCurrentModSettings(collectionId, mod.ModDirectory, mod.ModName);

            // Not in collection or permanently disabled — nothing to do
            if (!gotSettings || !isEnabled)
                continue;

            // Don't disable pinned mods
            if (Configuration.PinnedModDirectories.Contains(mod.ModDirectory))
            {
                Log.Debug($"Mod '{mod.ModName}' is pinned, not disabling");
                continue;
            }

            var modKey = $"{collectionId}|{mod.ModDirectory}|{mod.ModName}";

            // Temp-disable the conflicting mod
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

    public void ResetAllPriorities()
    {
        loopingEmoteCommand = null;
        ClearEmoteSwap();

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

                // Remove all temp settings → auto-reverts each mod to permanent/inherited state
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

        // Merge: permanent options → preset options → modifier overrides
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

        Log.Information($"Applied temp settings for '{preset.ModName}': enabled, priority {basePriority}→{basePriority + boostAmount} (+{boostAmount}), {mergedOptions.Count} option groups");
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

    /// <summary>Maps an emote command to its animation type (int). Used by modifier execution.</summary>
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

    /// <summary>
    /// Execute an emote command directly via ProcessChatBoxEntry, without clearing swaps.
    /// Used by the unlock bypass to execute the carrier command while a swap is active.
    /// </summary>
    private unsafe void ExecuteEmoteDirect(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        if (!command.StartsWith("/")) command = "/" + command;
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

    /// <summary>
    /// Check if an emote is unlocked for the current player via UIState.
    /// Returns true if unlocked (or if check fails — assume unlocked to avoid blocking normal execution).
    /// </summary>
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

    // Subfolders to check for animation files
    // Full set of subfolders for resolving TARGET PAP paths (ResolveEmotePaths).
    // Includes job-specific subfolders so PickBestPath can find the player's job-specific animation.
    private static readonly string[] AnimSubfolders = {
        "", "bt_common/", "resident/", "nonresident/",
        // Job-specific battle animation subfolders (for emotes like Zantetsuken under bt_swd_sld)
        "bt_swd_sld/",  // PLD/GLA — sword + shield
        "bt_2ax_emp/",  // WAR/MRD — greataxe
        "bt_2sw_emp/",  // DRK — greatsword
        "bt_2gb_emp/",  // GNB — gunblade
        "bt_2sp_emp/",  // DRG/LNC — spear
        "bt_2km_emp/",  // RPR — scythe
        "bt_clw_clw/",  // MNK/PGL — claws/fists
        "bt_dgr_dgr/",  // NIN/ROG — daggers
        "bt_nin_nin/",  // NIN — alternate
        "bt_2kt_emp/",  // SAM — katana
        "bt_bld_bld/",  // VPR — twinfangs
        "bt_2bw_emp/",  // BRD/ARC — bow
        "bt_2gn_emp/",  // MCH — gun
        "bt_chk_chk/",  // DNC — chakrams
        "bt_stf_sld/",  // WHM/CNJ/BLM — staff + shield
        "bt_jst_sld/",  // BLM/THM — scepter + shield
        "bt_2bk_emp/",  // SCH/SMN/ACN — book
        "bt_2gl_emp/",  // AST — globe
        "bt_2ff_emp/",  // SGE — nouliths
        "bt_rod_emp/",  // BLU — rod
        "bt_2rp_emp/",  // RDM — rapier
        "bt_brs_plt/",  // PCT — brush + palette
    };

    // Minimal set of subfolders for CARRIER path generation (GenerateAllPossiblePaths).
    // Only base subfolders — do NOT include job-specific bt_ folders here because mapping
    // those paths creates phantom Penumbra redirects at paths that don't normally exist.
    // The game's normal fallback (job-specific → bt_common) handles job resolution;
    // our redirect at bt_common intercepts correctly.
    private static readonly string[] CarrierPathSubfolders = {
        "", "bt_common/", "resident/", "nonresident/"
    };

    // Temp directory for modified PAP files
    private string EmoteSwapDir => Path.Combine(PluginInterface.GetPluginConfigDirectory(), "emoteswap");

    private static readonly HashSet<string> NoCarrierCommands = new(StringComparer.OrdinalIgnoreCase)
        { "/sit", "/groundsit", "/doze", "/changepose", "/cpose", "/hug", "/pet", "/handover", "/embrace", "/dote",
          "/showleft", "/showright", "/savortea", "/hildy", "/songbird" };

    /// <summary>
    /// Preferred one-shot carriers (common starter emotes, always unlocked).
    /// Looping carriers can't use a static list — they're uncommon (emote_sp/ with _loop keys)
    /// and vary by account. We scan all unlocked emotes for looping carriers dynamically.
    /// </summary>
    private static readonly string[] PreferredOneShotCarriers = { "/no", "/me", "/bow", "/clap", "/yes", "/wave", "/cheer", "/dance", "/laugh" };

    /// <summary>
    /// Find a suitable carrier emote. Returns the carrier's info or null if none available.
    /// Matches loop type (looping target needs looping carrier because ActionTimeline controls
    /// whether the game loops the animation). Prefers carriers with slot 1 (intro/start animation)
    /// when the target has one. Falls back to mismatched loop type if needed.
    /// The surgical TMB rewriter eliminates C009 size constraints, so any carrier works.
    /// </summary>
    /// <param name="needsLoop">Whether the target emote loops (looping carriers preferred).</param>
    /// <param name="preferSlot1">Whether to prefer carriers with slot 1 (intro animation).</param>
    /// <param name="maxNameLen">Max carrier PAP name length (must be &lt;= target name length to fit C009.Path). 0 = no limit.</param>
    /// <param name="maxSlot1NameLen">Max carrier slot 1 PAP name length. 0 = no limit. Only checked when preferSlot1=true.</param>
    private (ushort id, string command, string key, string papName, bool isLoop, string? slot1Key, string? slot1PapName)? FindCarrier(bool needsLoop, bool preferSlot1 = false, int maxNameLen = 0, int maxSlot1NameLen = 0, byte? requiredConditionMode = null)
    {
        if (emoteCommandToId == null || emoteIdToTimelineKeys == null) return null;

        // Collect emote IDs to exclude (NoCarrierCommands)
        var excludedIds = new HashSet<ushort>();
        foreach (var (cmd, id) in emoteCommandToId)
        {
            if (NoCarrierCommands.Contains(cmd))
                excludedIds.Add(id);
        }

        // Helper: check if carrier name fits the length constraint
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

    /// <summary>
    /// Try to set up an emote bypass for the given command. Returns the carrier command
    /// to execute instead, or null if bypass not applicable/failed.
    /// Picks a carrier whose PAP name fits the target's C009.Path field (name length constraint).
    /// If no fitting carrier found, falls back to any carrier (partial rewrite — may not animate).
    /// </summary>
    /// <param name="forPreset">When true (EmoteLocked preset), skips unlock check, skips carrier sticking
    /// (mod options may have changed), and reads target PAP from mod files instead of game data.</param>
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

        // Skip bypass for emotes the player already has unlocked (unless forPreset — preset explicitly marked as locked).
        // Zantetsuken reports as unlocked via UIState even when not owned (Mogstation bug).
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

        // Read race early — needed for both sticking check and PAP selection
        var playerRaceCode = GetPlayerRaceCode();

        // Carrier sticking: if same emote as last time and swap is still active, reuse the same carrier.
        // Skip for preset bypass — mod options may have changed (modifier switch), must always re-read PAP.
        // Also skip if race changed (character switch via Glamourer/Character Select+) — PAP data is race-specific.
        if (!forPreset && emoteSwapActive && lastBypassEmoteCommand == cmdLower && lastBypassCarrierCommand != null)
        {
            if (lastBypassRaceCode != playerRaceCode)
            {
                Log.Information($"[UnlockBypass] Race changed ({lastBypassRaceCode} → {playerRaceCode}), re-running swap setup for '{cmdLower}'");
            }
            else
            {
                // If swap was soft-disabled by one-shot cleanup (carrier returned to normal after emote ended),
                // re-enable the mod. SetTemporaryModSettings increments Penumbra's ChangeCounter, which changes
                // the resolved path encoding — giving the game's resource cache a fresh path to load from.
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
                    Log.Information($"[UnlockBypass] Reusing carrier '{lastBypassCarrierCommand}' (id={lastBypassCarrierId}) for '{cmdLower}' — swap still active (race={playerRaceCode})");
                    return (lastBypassCarrierCommand, lastBypassCarrierId, stickIsLoop);
                }
            }
        }

        Log.Debug($"[UnlockBypass] Target emote '{cmdLower}' (id={targetEmoteId}) has {targetKeys.Count} timeline(s): {string.Join(", ", targetKeys.Select(t => $"[{t.slot}]={t.key}{(t.isLoop ? " (loop)" : "")}{(t.loadType == 0 ? " (facial)" : t.loadType == 1 ? " (perjob)" : "")}"))}");

        var targetSlot0 = targetKeys.FirstOrDefault(k => k.slot == 0);
        var targetIsLoop = targetSlot0.isLoop;

        // Check if target has a slot 1 (intro/start animation)
        var targetSlot1 = targetKeys.FirstOrDefault(k => k.slot == 1);
        var targetHasSlot1 = !string.IsNullOrEmpty(targetSlot1.key);
        if (targetHasSlot1)
            Log.Debug($"[UnlockBypass] Target has intro animation: [{targetSlot1.slot}]={targetSlot1.key}");

        // Read target PAP names to determine max carrier name length.
        // The C009.Path in TMB has limited padded space — carrier name must fit.
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
        // When sitting/groundsitting/dozing, only carriers with ConditionMode 0 (any-state) will work.
        byte? requiredConditionMode = null;
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer != null && localPlayer.Address != nint.Zero)
        {
            unsafe
            {
                var ch = (Character*)localPlayer.Address;
                if (ch->Mode != CharacterModes.Normal)
                {
                    requiredConditionMode = 0;
                    Log.Debug($"[UnlockBypass] Player mode={ch->Mode}, requiring ConditionMode 0 carriers");
                }
            }
        }

        // Find a carrier whose PAP name fits the target's C009.Path field.
        // Prefer carriers with slot 1 only when target has an intro animation AND its PAP actually exists.
        // Target may have a slot 1 timeline entry but no PAP file on disk (e.g., emote_sp/sp67_start).
        var targetSlot1PapExists = maxSlot1NameLen > 0;
        var carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, maxNameLen: maxSlot0NameLen, maxSlot1NameLen: maxSlot1NameLen, requiredConditionMode: requiredConditionMode);

        // If no carrier fits with slot 1 preference, try without slot 1 requirement
        if (carrier == null && targetHasSlot1 && targetSlot1PapExists)
        {
            Log.Debug($"[UnlockBypass] No carrier with slot 1 fits, trying without slot 1");
            carrier = FindCarrier(targetIsLoop, preferSlot1: false, maxNameLen: maxSlot0NameLen, requiredConditionMode: requiredConditionMode);
        }

        // If still no carrier fits the name constraint, try without name constraint (partial rewrite — may not animate but better than nothing)
        if (carrier == null && maxSlot0NameLen > 0)
        {
            Log.Warning($"[UnlockBypass] No carrier with name length <= {maxSlot0NameLen} found, trying without length constraint");
            carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, requiredConditionMode: requiredConditionMode);
        }

        // If ConditionMode constraint prevented finding a carrier, fall back to any carrier (better to try than fail silently)
        if (carrier == null && requiredConditionMode.HasValue)
        {
            Log.Warning($"[UnlockBypass] No ConditionMode {requiredConditionMode.Value} carriers found, trying without ConditionMode constraint");
            carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists, maxNameLen: maxSlot0NameLen, maxSlot1NameLen: maxSlot1NameLen);
            if (carrier == null && targetHasSlot1 && targetSlot1PapExists)
                carrier = FindCarrier(targetIsLoop, preferSlot1: false, maxNameLen: maxSlot0NameLen);
            if (carrier == null && maxSlot0NameLen > 0)
                carrier = FindCarrier(targetIsLoop, preferSlot1: targetHasSlot1 && targetSlot1PapExists);
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
            keyPairs.Add((carrierSlot1Key, targetSlot1.key));
            Log.Debug($"[UnlockBypass] Including slot 1 pair: carrier='{carrierSlot1Key}' → target='{targetSlot1.key}'");
        }
        else if (targetHasSlot1 && !targetSlot1PapExists)
        {
            Log.Debug($"[UnlockBypass] Target slot 1 PAP doesn't exist — skipping intro swap");
        }
        else if (targetHasSlot1 && carrierSlot1Key == null)
        {
            Log.Debug($"[UnlockBypass] Carrier '{carrierCommand}' has no slot 1 — intro will be skipped");
        }

        // Add key pairs for ALL remaining target slots (facial, upper body/head targeting, etc.)
        // Match by slot index: for each target entry not already paired (slot 0/1), find the carrier's entry at the same slot.
        var pairedSlots = new HashSet<int> { 0 }; // slot 0 always paired above
        if (targetHasSlot1 && targetSlot1PapExists && carrier.Value.slot1Key != null)
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
                    Log.Debug($"[UnlockBypass] Including {typeLabel} pair [{targetEntry.slot}]: carrier='{carrierEntry.key}' → target='{targetEntry.key}'");
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
            Log.Information($"[UnlockBypass] Swap active for '{cmdLower}', executing {carrierCommand} ({keyPairs.Count} slot(s), fromMod={forPreset})");
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

    /// <summary>
    /// Generate ALL possible game file paths for a timeline key across all races/layers/subfolders.
    /// Unlike ResolveEmotePaths, does NOT check DataManager.FileExists — generates every possible
    /// path so Penumbra can redirect ANY race's request to our file. This is critical because
    /// only mapping paths that exist in base game data would miss the player's specific race.
    /// </summary>
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

    /// <summary>Get the player's character model race code (e.g., "c0101" for Hyur Midlander Male).</summary>
    /// <remarks>
    /// Tries Glamourer IPC first to get the visual race (reflects character switches via CS+/Glamourer).
    /// Falls back to ObjectTable customize bytes (server data, doesn't reflect Glamourer meta manipulations).
    /// </remarks>
    private string? GetPlayerRaceCode()
    {
        // Try Glamourer first — ObjectTable[0].Customize doesn't reflect visual race changes
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
                    var glamRace = customize["Race"]?["Value"]?.Value<byte>();
                    var glamGender = customize["Gender"]?["Value"]?.Value<byte>();
                    var glamTribe = customize["Clan"]?["Value"]?.Value<byte>();
                    if (glamRace.HasValue && glamGender.HasValue && glamTribe.HasValue)
                    {
                        var glamCode = RaceToModelCode(glamRace.Value, glamGender.Value, glamTribe.Value);
                        if (glamCode != null)
                        {
                            Log.Verbose($"[RaceCode] Glamourer: race={glamRace}, gender={glamGender}, tribe={glamTribe} → {glamCode}");
                            return glamCode;
                        }
                    }
                }
            }
        }
        catch
        {
            // Glamourer not available — fall through to ObjectTable
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
        Log.Verbose($"[RaceCode] ObjectTable: race={race}, gender={gender}, tribe={tribe} → {code}");
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
        // Gender-aware fallback: prefer a path with the same gender as the player.
        // Model codes: odd base number (c01, c03, ...) = male, even (c02, c04, ...) = female.
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

    /// <summary>
    /// Rewrite all occurrences of a target animation name in PAP binary data to a carrier name.
    /// Simple in-place replacement — carrier name must fit within the available padded space at
    /// each occurrence. PapAnimation.Name (32 bytes padded) always fits. The C009.Path in the TMB
    /// string table has limited space — carrier names longer than the target will fail there.
    /// Use FindCarrier's maxNameLen constraint to ensure the carrier name fits.
    /// Returns (modifiedData, replacedCount, foundCount).
    /// </summary>
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
                // Fits — replace in place
                Array.Copy(carrierBytes, 0, result, i, carrierBytes.Length);
                for (int j = carrierBytes.Length; j < available; j++)
                    result[i + j] = 0;
                replaced++;
                Log.Verbose($"[EmoteSwap] Replaced in-place at offset {i}: '{targetName}' → '{carrierName}' ({carrierBytes.Length}/{available}b)");
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

        Log.Debug($"[EmoteSwap] Rewrote {replaced}/{found} occurrence(s) of '{targetName}' → '{carrierName}'");
        return (result, replaced, found);
    }

    /// <summary>
    /// Read a target animation file — from the mod (via ResolvePlayerPath) or from game data.
    /// When readFromMod is true, resolves the game path through Penumbra to get the mod's file on disk.
    /// Falls back to game data if the mod doesn't provide this specific file.
    /// </summary>
    private byte[]? ReadTargetFile(string gamePath, bool readFromMod)
    {
        if (readFromMod && PenumbraService?.IsAvailable == true)
        {
            var resolvedPath = PenumbraService.ResolvePlayerPath(gamePath);
            if (resolvedPath != null && resolvedPath != gamePath && File.Exists(resolvedPath))
            {
                try
                {
                    return File.ReadAllBytes(resolvedPath);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[ReadTargetFile] Failed to read mod file '{resolvedPath}': {ex.Message}");
                }
            }
        }
        var file = DataManager.GetFile(gamePath);
        return file?.Data;
    }

    /// <summary>
    /// Set up Penumbra file redirects for the emote swap.
    /// Creates a real Penumbra mod on disk (Dancy-style) with default_mod.json file mappings,
    /// then AddMod/ReloadMod + enable via SetTemporaryModSettings.
    /// Carrier should be pre-selected with a name length constraint to ensure the PAP name fits
    /// the target's C009.Path field. Partial rewrites are logged but slot 0 must succeed.
    /// When readTargetFromMod is true, reads target PAP/TMB from the mod's files (via ResolvePlayerPath)
    /// instead of game data — used for EmoteLocked presets where the mod provides custom animation.
    /// </summary>
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

            var allMappings = new Dictionary<string, string>(); // game path → absolute file path
            int successfulPairs = 0;

            // Use race+job-specific PAP for correct skeleton data
            var playerRaceCode = GetPlayerRaceCode();
            var playerJobSub = GetPlayerJobSubfolder();

            for (int i = 0; i < keyPairs.Count; i++)
            {
                var (carrierKey, targetKey) = keyPairs[i];
                Log.Debug($"[EmoteSwap] Pair {i}: carrier='{carrierKey}', target='{targetKey}'");

                // Load target PAP — from mod (EmoteLocked preset) or game data
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
                    Log.Information($"[EmoteSwap] Target '{targetKey}' — {targetPaths.Count} resolved paths:\n{pathSummary}");
                }

                var selectedTargetPath = PickBestPath(targetPaths, playerRaceCode, playerJobSub);
                Log.Verbose($"[EmoteSwap] Pair {i}: target path={selectedTargetPath} (from {targetPaths.Count} resolved, race={playerRaceCode}, job={playerJobSub}, fromMod={readTargetFromMod})");
                var targetData = readTargetFromMod ? ReadTargetFile(selectedTargetPath, true) : DataManager.GetFile(selectedTargetPath)?.Data;
                if (targetData == null) { Log.Warning($"[EmoteSwap] Pair {i}: Can't read target PAP from {selectedTargetPath}"); continue; }

                // Load carrier PAP to read its actual internal animation name — prefer the player's race+job-specific file
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

                Log.Debug($"[EmoteSwap] Pair {i}: rewriting '{targetName}' → '{carrierName}' ({targetData.Length} bytes)");

                // Rewrite target PAP: replace animation name to match carrier's expected name.
                // The surgical TMB-aware rewriter extends the string table when needed,
                // so partial rewrites due to C009 size constraints no longer occur.
                var (modifiedData, replaced, found) = RewritePapAnimationNames(targetData, targetName, carrierName);
                if (modifiedData == null)
                {
                    Log.Warning($"[EmoteSwap] Pair {i}: Name rewrite failed — no occurrences found");
                    continue;
                }

                if (replaced < found)
                {
                    Log.Warning($"[EmoteSwap] Pair {i}: Incomplete rewrite ({replaced}/{found}) — some occurrences could not be updated");
                    if (i == 0) { Log.Warning($"[EmoteSwap] Slot 0 incomplete — swap may not work correctly"); }
                    else { continue; } // Skip non-critical slots
                }

                // Save modified PAP with unique file name (to both directories)
                var fileName = $"swap_{i}_{swapId}.pap";
                var configPath = Path.Combine(configSwapDir, fileName);
                File.WriteAllBytes(configPath, modifiedData);

                // Generate ALL possible carrier paths (every race × layer × subfolder).
                var carrierAllPaths = GenerateAllPossiblePaths(carrierKey);
                foreach (var cp in carrierAllPaths)
                {
                    allMappings[cp] = configPath;
                }

                // Also redirect the carrier's TMB (separate timeline file) to the target's TMB.
                // The TMB controls timing, events, and may trigger facial expression loading.
                // This allows target-specific events (including additive facial layers) to fire.
                // Also redirect the carrier's TMB (separate timeline file) to the target's TMB.
                // The TMB controls timing, events, and may trigger facial expression loading.
                // Rewrite animation name references in the TMB to match the carrier name.
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
                            Log.Debug($"[EmoteSwap] Pair {i}: TMB name rewrite {tmbReplaced}/{tmbFound} '{targetName}' → '{carrierName}'");
                        }
                    }
                    var tmbFileName = $"swap_tmb_{i}_{swapId}.tmb";
                    var tmbConfigPath = Path.Combine(configSwapDir, tmbFileName);
                    File.WriteAllBytes(tmbConfigPath, tmbData);
                    allMappings[carrierTmbPath] = tmbConfigPath;
                    Log.Debug($"[EmoteSwap] Pair {i}: TMB redirect {carrierTmbPath} → {tmbFileName} ({tmbData.Length} bytes)");
                }
                else
                {
                    Log.Debug($"[EmoteSwap] Pair {i}: No target TMB at {targetTmbPath}");
                }

                Log.Debug($"[EmoteSwap] Pair {i}: {replaced}/{found} rewrites OK, {carrierAllPaths.Count} carrier paths → {fileName}");
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
                    var relMappings = new Dictionary<string, string>(); // game path → local filename
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
                        // First time: AddMod registers the mod in Penumbra's mod list.
                        // AddMod triggers async file compaction which may hold locks briefly.
                        // After AddMod, we must also call ReloadMod to fully activate the file
                        // redirects in the collection cache — AddMod alone doesn't do this.
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
                            Log.Warning("[EmoteSwap] AddMod failed — falling back to AddTemporaryMod only");
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
                    Log.Warning($"[EmoteSwap] Real mod setup failed: {ex.Message} — using AddTemporaryMod only");
                }
            }

            if (!encoreSwapModRegistered)
            {
                Log.Warning("[EmoteSwap] Real mod failed to register — unlock bypass unavailable");
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

    /// <summary>
    /// Soft-disable the emote swap: remove Penumbra temp settings so the carrier emote returns to normal,
    /// but keep swap files on disk and sticking state intact. The next /vanilla call for the same emote
    /// can re-enable the mod via SetTemporaryModSettings (incrementing ChangeCounter for cache invalidation)
    /// without rebuilding all swap files. Full teardown happens via ClearEmoteSwap on preset/reset/dispose.
    /// </summary>
    private void SoftDisableEmoteSwap()
    {
        if (PenumbraService?.IsAvailable == true && encoreSwapModRegistered)
        {
            try
            {
                if (emoteSwapCollectionId != Guid.Empty)
                    PenumbraService.RemoveTemporaryModSettings(emoteSwapCollectionId, EncoreSwapModName);

                var (ok, currentCollId, _) = PenumbraService.GetCurrentCollection();
                if (ok && currentCollId != Guid.Empty && currentCollId != emoteSwapCollectionId)
                    PenumbraService.RemoveTemporaryModSettings(currentCollId, EncoreSwapModName);
            }
            catch { }
        }

        emoteSwapSoftDisabled = true;
        emoteSwapIsOneShot = false;
        emoteSwapCarrierId = 0;
        emoteSwapWaitingForStart = false;
        Log.Debug("[EmoteSwap] Soft-disabled (carrier returns to normal, swap preserved for sticking)");
    }

    /// <summary>Remove the emote swap (both real Penumbra mod and temporary mod) and clean up temp files.</summary>
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
        emoteSwapIsOneShot = false;
        emoteSwapCarrierId = 0;
        emoteSwapWaitingForStart = false;
        emoteSwapSoftDisabled = false;

        // Clean up config-dir swap files
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

    private void OnFrameworkUpdate(IFramework fw)
    {
        var hasLoopActive = loopingEmoteCommand != null;
        var hasSwapCleanup = emoteSwapActive && emoteSwapIsOneShot;
        if (!hasLoopActive && !hasSwapCleanup) return;

        try
        {
            var localPlayer = ObjectTable.LocalPlayer;
            if (localPlayer == null) return;

            var currentEmoteId = ReadCurrentEmoteId();

            // One-shot emote swap auto-cleanup: detect when the carrier emote finishes playing.
            // Uses soft-disable instead of full teardown — removes Penumbra temp settings (carrier returns
            // to normal animation) but keeps swap files and sticking state intact. On the next /vanilla call
            // for the same emote, sticking re-enables the mod without rebuilding swap files.
            // Skip one-shot cleanup if we're looping a locked emote — swap must stay active
            if (hasSwapCleanup && emoteSwapCarrierId != 0 && loopCarrierCommand == null)
            {
                if (emoteSwapWaitingForStart)
                {
                    // Wait for carrier emote to start playing
                    if (currentEmoteId == emoteSwapCarrierId)
                        emoteSwapWaitingForStart = false;
                }
                else
                {
                    // Carrier was playing, detect when it stops
                    if (currentEmoteId != emoteSwapCarrierId)
                    {
                        Log.Debug($"[EmoteSwap] One-shot carrier finished (emoteId changed from {emoteSwapCarrierId} to {currentEmoteId}), soft-disabling swap");
                        SoftDisableEmoteSwap();
                    }
                }
            }

            if (!hasLoopActive) return;

            var currentTimeline = ReadBaseTimeline();

            // Capture the emote's ID and timeline when it first starts playing
            if (loopWaitingForStart && currentEmoteId != previousEmoteId && currentEmoteId != 0)
            {
                if (loopingEmoteId == 0)
                {
                    // First execution — learn the emote's ID and timeline
                    loopingEmoteId = currentEmoteId;
                    emoteTimeline = currentTimeline;
                    loopWaitingForStart = false;
                    previousPosition = localPlayer.Position;
                }
                else if (currentEmoteId == loopingEmoteId)
                {
                    // Re-execution — same emote started again
                    emoteTimeline = currentTimeline;
                    loopWaitingForStart = false;
                    previousPosition = localPlayer.Position;
                }
                else
                {
                    // A different emote started — user did something else, cancel loop
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

    private unsafe ushort ReadBaseTimeline()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null || localPlayer.Address == nint.Zero) return 0;

        var player = (Character*)localPlayer.Address;
        if (player == null) return 0;

        return player->Timeline.TimelineSequencer.GetSlotTimeline(0);
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
}
