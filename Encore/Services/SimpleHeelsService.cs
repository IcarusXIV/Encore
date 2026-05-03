using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace Encore.Services;

// Reflection into SH's TempOffsets[0] + ForceUpdateLocal is the only path that syncs:
// RegisterPlayer IPC applies locally but isn't read by SH's broadcast serializer.
// IPC fallback used when reflection fails (apply-locally only, no sync).
public class SimpleHeelsService : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    private ICallGateSubscriber<(int, int)>? apiVersionSubscriber;
    private ICallGateSubscriber<int, string, object?>? registerPlayerSubscriber;
    private ICallGateSubscriber<int, object?>? unregisterPlayerSubscriber;

    public bool IsAvailable { get; private set; }

    private const int LocalPlayerObjectIndex = 0;

    private bool overrideActive;
    private bool lastAppliedViaReflection;

    // resolved lazily on first ApplyOffset
    private bool reflectionResolved;
    private bool reflectionWorks;
    private Array? tempOffsetsArray;
    private Array? tempOffsetEmoteArray;
    private ConstructorInfo? tempOffsetCtor;
    private MethodInfo? forceUpdateLocalMethod;
    private MethodInfo? emoteIdentifierGetMethod;

    public SimpleHeelsService(IDalamudPluginInterface pluginInterface, IObjectTable objectTable, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.objectTable = objectTable;
        this.log = log;
        Initialize();
    }

    private void Initialize()
    {
        try
        {
            apiVersionSubscriber = pluginInterface.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
            var (major, minor) = apiVersionSubscriber.InvokeFunc();
            IsAvailable = major >= 2;

            if (IsAvailable)
            {
                registerPlayerSubscriber = pluginInterface.GetIpcSubscriber<int, string, object?>("SimpleHeels.RegisterPlayer");
                unregisterPlayerSubscriber = pluginInterface.GetIpcSubscriber<int, object?>("SimpleHeels.UnregisterPlayer");
                log.Information($"Simple Heels IPC initialized. Version: {major}.{minor}");
            }
            else
            {
                log.Warning($"Simple Heels API version {major}.{minor} is not supported. Minimum required: 2.x");
            }
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            log.Debug($"Simple Heels is not available: {ex.Message}");
        }
    }

    private bool TryResolveReflection()
    {
        if (reflectionResolved) return reflectionWorks;
        reflectionResolved = true;

        try
        {
            var shAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SimpleHeels");
            if (shAsm == null)
            {
                log.Debug("[SimpleHeels] Reflection: assembly not loaded");
                return false;
            }

            var shPluginType = shAsm.GetType("SimpleHeels.Plugin");
            var tempOffsetType = shAsm.GetType("SimpleHeels.TempOffset");
            var apiProviderType = shAsm.GetType("SimpleHeels.ApiProvider");
            var emoteIdentifierType = shAsm.GetType("SimpleHeels.EmoteIdentifier");
            if (shPluginType == null || tempOffsetType == null
                || apiProviderType == null || emoteIdentifierType == null)
            {
                log.Warning("[SimpleHeels] Reflection: one or more SH types not found; sync via reflection disabled");
                return false;
            }

            var tempOffsetsProp = shPluginType.GetProperty("TempOffsets",
                BindingFlags.Public | BindingFlags.Static);
            if (tempOffsetsProp?.GetValue(null) is not Array arr)
            {
                log.Warning("[SimpleHeels] Reflection: Plugin.TempOffsets not accessible");
                return false;
            }
            tempOffsetsArray = arr;

            // SH clears TempOffsets when active emote != TempOffsetEmote, so write both in lock-step
            var tempOffsetEmoteProp = shPluginType.GetProperty("TempOffsetEmote",
                BindingFlags.Public | BindingFlags.Static);
            if (tempOffsetEmoteProp?.GetValue(null) is not Array emoteArr)
            {
                log.Warning("[SimpleHeels] Reflection: Plugin.TempOffsetEmote not accessible");
                return false;
            }
            tempOffsetEmoteArray = emoteArr;

            tempOffsetCtor = tempOffsetType.GetConstructor(new[]
            {
                typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float),
            });
            if (tempOffsetCtor == null)
            {
                log.Warning("[SimpleHeels] Reflection: TempOffset(float x 6) ctor not found");
                return false;
            }

            forceUpdateLocalMethod = apiProviderType.GetMethod("ForceUpdateLocal",
                BindingFlags.NonPublic | BindingFlags.Static);
            if (forceUpdateLocalMethod == null)
            {
                log.Warning("[SimpleHeels] Reflection: ApiProvider.ForceUpdateLocal not found");
                return false;
            }

            emoteIdentifierGetMethod = emoteIdentifierType.GetMethod("Get",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(IPlayerCharacter) }, null);
            if (emoteIdentifierGetMethod == null)
            {
                log.Warning("[SimpleHeels] Reflection: EmoteIdentifier.Get(IPlayerCharacter) not found");
                return false;
            }

            reflectionWorks = true;
            log.Information("[SimpleHeels] Reflection sync path ready (heels offsets will broadcast to sync peers)");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[SimpleHeels] Reflection resolve failed: {ex.Message}");
            return false;
        }
    }

    private bool ApplyOffsetReflected(float x, float y, float z, float r, float pitch, float roll)
    {
        if (!TryResolveReflection()) return false;
        try
        {
            var tempOffset = tempOffsetCtor!.Invoke(new object[] { x, y, z, r, pitch, roll });
            var emoteId = GetCurrentEmoteIdentifier();
            tempOffsetsArray!.SetValue(tempOffset, LocalPlayerObjectIndex);
            tempOffsetEmoteArray!.SetValue(emoteId, LocalPlayerObjectIndex);
            forceUpdateLocalMethod!.Invoke(null, null);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[SimpleHeels] Reflection ApplyOffset failed: {ex.Message}");
            return false;
        }
    }

    private bool ClearOffsetReflected()
    {
        if (!TryResolveReflection()) return false;
        try
        {
            tempOffsetsArray!.SetValue(null, LocalPlayerObjectIndex);
            tempOffsetEmoteArray!.SetValue(null, LocalPlayerObjectIndex);
            forceUpdateLocalMethod!.Invoke(null, null);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"[SimpleHeels] Reflection ClearOffset failed: {ex.Message}");
            return false;
        }
    }

    private object? GetCurrentEmoteIdentifier()
    {
        if (emoteIdentifierGetMethod == null) return null;
        var local = objectTable.LocalPlayer;
        if (local == null) return null;
        try
        {
            return emoteIdentifierGetMethod.Invoke(null, new object?[] { local });
        }
        catch
        {
            return null;
        }
    }

    // x/y/z world units (y vertical), rotation/pitch/roll radians
    public bool ApplyOffset(float x, float y, float z, float rotation, float pitch, float roll)
    {
        if (!IsAvailable) return false;

        if (ApplyOffsetReflected(x, y, z, rotation, pitch, roll))
        {
            overrideActive = true;
            lastAppliedViaReflection = true;
            return true;
        }

        // IPC fallback: applies locally only, no sync
        if (registerPlayerSubscriber == null) return false;
        try
        {
            var json = "{\"TempOffset\":" +
                       $"{{\"X\":{F(x)},\"Y\":{F(y)},\"Z\":{F(z)},\"R\":{F(rotation)},\"Pitch\":{F(pitch)},\"Roll\":{F(roll)}}}}}";
            registerPlayerSubscriber.InvokeAction(LocalPlayerObjectIndex, json);
            overrideActive = true;
            lastAppliedViaReflection = false;
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Simple Heels ApplyOffset failed: {ex.Message}");
            return false;
        }
    }

    private static string F(float v) => v.ToString("G", CultureInfo.InvariantCulture);

    public void ClearOffset()
    {
        if (!IsAvailable) return;
        if (!overrideActive) return;

        if (lastAppliedViaReflection)
        {
            if (ClearOffsetReflected())
            {
                overrideActive = false;
                return;
            }
            // reflection broke; fall through to IPC clear
        }

        if (unregisterPlayerSubscriber == null) return;
        try
        {
            unregisterPlayerSubscriber.InvokeAction(LocalPlayerObjectIndex);
            overrideActive = false;
        }
        catch (Exception ex)
        {
            log.Warning($"Simple Heels ClearOffset failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        ClearOffset();
    }
}
