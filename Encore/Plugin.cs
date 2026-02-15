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
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    // Icons directory for custom preset images
    internal static string IconsDirectory => Path.Combine(PluginInterface.GetPluginConfigDirectory(), "icons");

    // Main command
    private const string MainCommand = "/encore";

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
    internal ImGuiFileBrowserWindow FileBrowserWindow { get; init; }

    // Dynamic command tracking
    private readonly HashSet<string> registeredPresetCommands = new();

    // Prevent overlapping preset executions
    private volatile bool isExecutingPreset = false;

    // Emote loop tracking (/loop command)
    private string? loopingEmoteCommand = null;
    private ushort previousEmoteId = 0;
    private ushort previousTimeline = 0;
    private ushort loopingEmoteId = 0;
    private ushort emoteTimeline = 0;
    private bool loopWaitingForStart = false;
    private System.Numerics.Vector3 previousPosition;

    public Plugin()
    {
        Instance = this;

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
                        if (parts.Length == 2 && Guid.TryParse(parts[0], out var collId))
                            PenumbraService.RemoveTemporaryModSettings(collId, parts[1]);
                    }
                    Configuration.ModsWithTempSettings.Clear();
                    Configuration.ActivePresetId = null;
                    Configuration.ActivePresetCollectionId = null;
                    Configuration.Save();
                    Log.Information("Cleaned up stale temp settings from previous session");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to initialize services: {ex.Message}");
        }

        // Create windows
        MainWindow = new MainWindow();
        PresetEditorWindow = new PresetEditorWindow();
        IconPickerWindow = new IconPickerWindow();
        HelpWindow = new HelpWindow();
        FileBrowserWindow = new ImGuiFileBrowserWindow("Select Icon Image");
        FileBrowserWindow.SetConfiguration(Configuration);

        // Wire up window dependencies
        MainWindow.SetEditorWindow(PresetEditorWindow);
        MainWindow.SetHelpWindow(HelpWindow);
        PresetEditorWindow.SetIconPicker(IconPickerWindow);

        // Add windows to system
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(PresetEditorWindow);
        WindowSystem.AddWindow(IconPickerWindow);
        WindowSystem.AddWindow(HelpWindow);
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

        // Register preset commands
        UpdatePresetCommands();

        // Show help on first launch
        if (!Configuration.HasSeenHelp)
        {
            HelpWindow.IsOpen = true;
            Configuration.HasSeenHelp = true;
            Configuration.Save();
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

        foreach (var cmd in registeredPresetCommands)
        {
            CommandManager.RemoveHandler($"/{cmd}");
        }
        registeredPresetCommands.Clear();

        // Clean up temporary Penumbra settings before disposal
        if (PenumbraService?.IsAvailable == true && Configuration.ModsWithTempSettings.Count > 0)
        {
            foreach (var key in Configuration.ModsWithTempSettings.ToList())
            {
                var parts = key.Split('|');
                if (parts.Length == 2 && Guid.TryParse(parts[0], out var collId))
                    PenumbraService.RemoveTemporaryModSettings(collId, parts[1]);
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

        // Start looping
        loopingEmoteCommand = emote;
        loopingEmoteId = 0;
        emoteTimeline = 0;
        loopWaitingForStart = true;
        ExecuteEmote(emote);
    }

    private void OnAlignCommand(string command, string args)
    {
        Framework.RunOnFrameworkThread(() => AlignToTarget());
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
                    // Same preset - just execute emote, don't re-apply priorities
                    Log.Debug($"Preset '{preset.Name}' already active, skipping priority changes");

                    // Re-apply temp settings when modifiers exist (user may be switching variants)
                    if (!preset.IsVanilla && PenumbraService != null && preset.Modifiers.Count > 0)
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
                    var baseEmoteCmd = preset.EmoteCommand; // For detecting same-emote modifier switches
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
                                if (isAlreadyActive && hasModifier &&
                                    string.Equals(emoteCmd, baseEmoteCmd, StringComparison.OrdinalIgnoreCase))
                                {
                                    // Same emote, just different options — check if still in emote mode
                                    var isStillEmoting = false;
                                    var lp = ObjectTable.LocalPlayer;
                                    if (lp != null)
                                    {
                                        unsafe
                                        {
                                            var character = (Character*)lp.Address;
                                            isStillEmoting = character->Mode != CharacterModes.Normal;
                                        }
                                    }

                                    if (isStillEmoting)
                                    {
                                        // Still dancing — redraw to swap mod files without restarting animation
                                        ExecuteRedraw();
                                    }
                                    else if (!string.IsNullOrEmpty(emoteCmd))
                                    {
                                        // No longer emoting — re-execute the dance
                                        ExecuteEmote(emoteCmd);
                                    }
                                }
                                else if (!string.IsNullOrEmpty(emoteCmd))
                                    ExecuteEmote(emoteCmd);
                                break;
                        }
                    });
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
            if (parts.Length != 2) continue;
            if (!Guid.TryParse(parts[0], out var storedCollectionId)) continue;
            if (storedCollectionId != collectionId) continue;

            var modDirectory = parts[1];

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
            if (PenumbraService!.RemoveTemporaryModSettings(storedCollectionId, modDirectory))
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
        var modKey = $"{collectionId}|{preset.ModDirectory}";
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

            var modKey = $"{collectionId}|{mod.ModDirectory}";

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
                    if (parts.Length != 2) continue;
                    if (!Guid.TryParse(parts[0], out var collectionId)) continue;

                    if (PenumbraService.RemoveTemporaryModSettings(collectionId, parts[1]))
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

        var modKey = $"{collectionId}|{preset.ModDirectory}";
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

    public unsafe (bool hasTarget, string targetName, float distance, bool inRange, bool isStanding, bool isWalking) GetAlignState()
    {
        var isWalking = MovementService?.IsMovingToDestination ?? false;

        var target = TargetManager.Target ?? TargetManager.SoftTarget;
        if (target == null)
            return (false, "", 0f, false, true, isWalking);

        var player = (Character*)(ObjectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null)
            return (false, "", 0f, false, true, isWalking);

        var isStanding = player->Mode == CharacterModes.Normal;

        var playerPos = new System.Numerics.Vector3(
            player->GameObject.Position.X,
            player->GameObject.Position.Y,
            player->GameObject.Position.Z);
        var distance = System.Numerics.Vector3.Distance(playerPos, target.Position);

        return (true, target.Name.TextValue, distance, distance <= MaxAlignDistance, isStanding, isWalking);
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
            PrintChat("Stand up first.", XivChatType.ErrorMessage);
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
                cancelled: null);
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

    private unsafe void ExecuteEmote(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return;

        // Ensure command starts with /
        if (!command.StartsWith("/"))
            command = "/" + command;

        try
        {
            // Execute via game's chat system (not CommandManager - that's for plugin commands only)
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
        if (loopingEmoteCommand == null) return;

        try
        {
            var localPlayer = ObjectTable.LocalPlayer;
            if (localPlayer == null) return;

            var currentEmoteId = ReadCurrentEmoteId();
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
                ExecuteEmote(loopingEmoteCommand);
                loopWaitingForStart = true;
            }

            // Detect emote end via timeline change (backup)
            if (loopingEmoteId != 0 && currentTimeline != previousTimeline &&
                previousTimeline == emoteTimeline && currentTimeline != emoteTimeline)
            {
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
