using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;

namespace Encore.Services;

// Service for interacting with Penumbra via IPC
public class PenumbraService : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;

    // IPC subscribers
    private ICallGateSubscriber<int>? apiVersionSubscriber;
    private ICallGateSubscriber<Dictionary<string, string>>? getModListSubscriber;
    private ICallGateSubscriber<byte, (Guid, string)?>? getCollectionSubscriber;
    private ICallGateSubscriber<int, (bool, bool, (Guid, string))>? getCollectionForObjectSubscriber;
    private ICallGateSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>? getCurrentModSettingsSubscriber;
    private ICallGateSubscriber<Guid, string, string, int, int>? trySetModPrioritySubscriber;
    private ICallGateSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>? trySetModSettingsSubscriber;
    private ICallGateSubscriber<Guid, string, string, bool, int>? trySetModSubscriber;
    private ICallGateSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?>? getAvailableModSettingsSubscriber;

    public bool IsAvailable { get; private set; }
    public int ApiVersion { get; private set; }

    public PenumbraService(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;

        Initialize();
    }

    private void Initialize()
    {
        try
        {
            apiVersionSubscriber = pluginInterface.GetIpcSubscriber<int>("Penumbra.ApiVersion");
            ApiVersion = apiVersionSubscriber.InvokeFunc();
            IsAvailable = ApiVersion >= 5;

            if (IsAvailable)
            {
                getModListSubscriber = pluginInterface.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
                getCollectionSubscriber = pluginInterface.GetIpcSubscriber<byte, (Guid, string)?>("Penumbra.GetCollection");
                getCollectionForObjectSubscriber = pluginInterface.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
                getCurrentModSettingsSubscriber = pluginInterface.GetIpcSubscriber<Guid, string, string, bool, (int, (bool, int, Dictionary<string, List<string>>, bool)?)>("Penumbra.GetCurrentModSettings.V5");
                trySetModPrioritySubscriber = pluginInterface.GetIpcSubscriber<Guid, string, string, int, int>("Penumbra.TrySetModPriority.V5");
                trySetModSettingsSubscriber = pluginInterface.GetIpcSubscriber<Guid, string, string, string, IReadOnlyList<string>, int>("Penumbra.TrySetModSettings.V5");
                trySetModSubscriber = pluginInterface.GetIpcSubscriber<Guid, string, string, bool, int>("Penumbra.TrySetMod.V5");
                getAvailableModSettingsSubscriber = pluginInterface.GetIpcSubscriber<string, string, IReadOnlyDictionary<string, (string[], int)>?>("Penumbra.GetAvailableModSettings.V5");

                log.Information($"Penumbra IPC initialized successfully. API Version: {ApiVersion}");
            }
            else
            {
                log.Warning($"Penumbra API version {ApiVersion} is not supported. Minimum required: 5");
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            log.Warning($"Penumbra is not available: {ex.Message}");
        }
    }

    public Dictionary<string, string> GetModList()
    {
        if (!IsAvailable || getModListSubscriber == null)
            return new Dictionary<string, string>();

        try
        {
            return getModListSubscriber.InvokeFunc() ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to get mod list: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    public (bool success, Guid collectionId, string collectionName) GetCurrentCollection()
    {
        if (!IsAvailable)
            return (false, Guid.Empty, "");

        try
        {
            // Try GetCollectionForObject first (most accurate)
            if (getCollectionForObjectSubscriber != null)
            {
                var (objectValid, _, (id, name)) = getCollectionForObjectSubscriber.InvokeFunc(0); // 0 = player object
                if (objectValid && id != Guid.Empty)
                {
                    return (true, id, name);
                }
            }

            // Fallback to GetCollection with Current type
            if (getCollectionSubscriber != null)
            {
                var result = getCollectionSubscriber.InvokeFunc(0xE2); // ApiCollectionType.Current
                if (result.HasValue)
                {
                    return (true, result.Value.Item1, result.Value.Item2);
                }
            }

            return (false, Guid.Empty, "");
        }
        catch (Exception ex)
        {
            log.Error($"Failed to get current collection: {ex.Message}");
            return (false, Guid.Empty, "");
        }
    }

    public (bool success, bool enabled, int priority, Dictionary<string, List<string>> options) GetCurrentModSettings(
        Guid collectionId, string modDirectory, string modName = "")
    {
        if (!IsAvailable || getCurrentModSettingsSubscriber == null)
            return (false, false, 0, new Dictionary<string, List<string>>());

        try
        {
            var (resultCode, settings) = getCurrentModSettingsSubscriber.InvokeFunc(collectionId, modDirectory, modName, false);

            if (resultCode == 0 && settings.HasValue) // 0 = Success
            {
                var (enabled, priority, options, _) = settings.Value;
                return (true, enabled, priority, options);
            }

            return (false, false, 0, new Dictionary<string, List<string>>());
        }
        catch (Exception ex)
        {
            log.Error($"Failed to get mod settings for {modDirectory}: {ex.Message}");
            return (false, false, 0, new Dictionary<string, List<string>>());
        }
    }

    public IReadOnlyDictionary<string, (string[] options, int groupType)>? GetAvailableModSettings(
        string modDirectory, string modName = "")
    {
        if (!IsAvailable || getAvailableModSettingsSubscriber == null)
            return null;

        try
        {
            return getAvailableModSettingsSubscriber.InvokeFunc(modDirectory, modName);
        }
        catch (Exception ex)
        {
            log.Error($"Failed to get available mod settings for {modDirectory}: {ex.Message}");
            return null;
        }
    }

    public bool TrySetModPriority(Guid collectionId, string modDirectory, int priority, string modName = "")
    {
        if (!IsAvailable || trySetModPrioritySubscriber == null)
            return false;

        try
        {
            var result = trySetModPrioritySubscriber.InvokeFunc(collectionId, modDirectory, modName, priority);

            // 0 = Success, 1 = NothingChanged (also considered success)
            if (result == 0 || result == 1)
            {
                log.Debug($"Set priority for {modDirectory} to {priority}");
                return true;
            }

            log.Warning($"Failed to set priority for {modDirectory}: error code {result}");
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to set priority for {modDirectory}: {ex.Message}");
            return false;
        }
    }

    public bool TrySetModSettings(Guid collectionId, string modDirectory, string optionGroupName,
        IReadOnlyList<string> optionNames, string modName = "")
    {
        if (!IsAvailable || trySetModSettingsSubscriber == null)
            return false;

        try
        {
            var result = trySetModSettingsSubscriber.InvokeFunc(collectionId, modDirectory, modName, optionGroupName, optionNames);

            // 0 = Success, 1 = NothingChanged
            if (result == 0 || result == 1)
            {
                log.Debug($"Set options for {modDirectory}.{optionGroupName}");
                return true;
            }

            log.Warning($"Failed to set options for {modDirectory}.{optionGroupName}: error code {result}");
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to set options for {modDirectory}.{optionGroupName}: {ex.Message}");
            return false;
        }
    }

    public bool TrySetModEnabled(Guid collectionId, string modDirectory, bool enabled, string modName = "")
    {
        if (!IsAvailable || trySetModSubscriber == null)
            return false;

        try
        {
            var result = trySetModSubscriber.InvokeFunc(collectionId, modDirectory, modName, enabled);

            // 0 = Success, 1 = NothingChanged
            if (result == 0 || result == 1)
            {
                log.Debug($"Set mod {modDirectory} enabled={enabled}");
                return true;
            }

            log.Warning($"Failed to set mod {modDirectory} enabled={enabled}: error code {result}");
            return false;
        }
        catch (Exception ex)
        {
            log.Error($"Failed to set mod {modDirectory} enabled={enabled}: {ex.Message}");
            return false;
        }
    }


    public Dictionary<string, object?> GetModChangedItems(string modDirectory, string modName = "")
    {
        if (!IsAvailable)
            return new Dictionary<string, object?>();

        try
        {
            var ipc = pluginInterface.GetIpcSubscriber<string, string, Dictionary<string, object?>>("Penumbra.GetChangedItems.V5");
            return ipc.InvokeFunc(modDirectory, modName) ?? new Dictionary<string, object?>();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to get changed items for {modDirectory}: {ex.Message}");
            return new Dictionary<string, object?>();
        }
    }

    public string? GetModDirectory()
    {
        if (!IsAvailable)
            return null;

        try
        {
            var ipc = pluginInterface.GetIpcSubscriber<string>("Penumbra.GetModDirectory");
            return ipc.InvokeFunc();
        }
        catch (Exception ex)
        {
            log.Error($"Error getting mod directory: {ex}");
            return null;
        }
    }

    public void Dispose()
    {
        // IPC subscribers don't need explicit disposal
    }
}
