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
    private ICallGateSubscriber<object?>? readySubscriber;
    private ICallGateSubscriber<string>? getLocalPlayerSubscriber;

    private float cachedNaturalOffsetY;
    private long naturalOffsetFetchedTicks = long.MinValue;

    public bool IsAvailable { get; private set; }

    private const int LocalPlayerObjectIndex = 0;

    private bool overrideActive;
    private bool lastAppliedViaReflection;

    // resolved lazily, retried on a throttle until it succeeds
    private long lastReflectionAttemptTicks;
    private bool reflectionWorks;
    private string lastAppliedPath = "";
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

        // SimpleHeels.Ready fires at the end of SH's init and on every reload.
        readySubscriber = pluginInterface.GetIpcSubscriber<object?>("SimpleHeels.Ready");
        readySubscriber.Subscribe(TryInitialize);
        TryInitialize();
    }

    private void TryInitialize()
    {
        try
        {
            apiVersionSubscriber ??= pluginInterface.GetIpcSubscriber<(int, int)>("SimpleHeels.ApiVersion");
            var (major, minor) = apiVersionSubscriber.InvokeFunc();
            if (major < 2) { IsAvailable = false; return; }
            if (IsAvailable) return;

            registerPlayerSubscriber = pluginInterface.GetIpcSubscriber<int, string, object?>("SimpleHeels.RegisterPlayer");
            unregisterPlayerSubscriber = pluginInterface.GetIpcSubscriber<int, object?>("SimpleHeels.UnregisterPlayer");
            getLocalPlayerSubscriber = pluginInterface.GetIpcSubscriber<string>("SimpleHeels.GetLocalPlayer");
            IsAvailable = true;
            log.Information($"Simple Heels IPC initialized. Version: {major}.{minor}");
        }
        catch
        {
            IsAvailable = false;
        }
    }

    private bool TryResolveReflection()
    {
        if (reflectionWorks) return true;
        if (Environment.TickCount64 - lastReflectionAttemptTicks < 2000) return false;
        lastReflectionAttemptTicks = Environment.TickCount64;

        try
        {
            var shAsm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "SimpleHeels");
            if (shAsm == null)
            {
                log.Debug("[SimpleHeels] Reflection: SimpleHeels assembly not loaded yet");
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

    // Unaffected by temp overrides, so re-applies never compound
    private float GetNaturalOffsetY()
    {
        if (getLocalPlayerSubscriber == null) return 0f;
        if (Environment.TickCount64 - naturalOffsetFetchedTicks < 500) return cachedNaturalOffsetY;
        naturalOffsetFetchedTicks = Environment.TickCount64;

        try
        {
            var json = getLocalPlayerSubscriber.InvokeFunc();
            if (!string.IsNullOrEmpty(json))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("DefaultOffset", out var offset) &&
                    offset.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    cachedNaturalOffsetY = offset.GetSingle();
                    return cachedNaturalOffsetY;
                }
            }
        }
        catch (Exception ex)
        {
            log.Debug($"[SimpleHeels] GetLocalPlayer parse failed: {ex.Message}");
        }

        cachedNaturalOffsetY = 0f;
        return 0f;
    }

    // x/y/z world units (y vertical), rotation/pitch/roll radians
    public bool ApplyOffset(float x, float y, float z, float rotation, float pitch, float roll)
    {
        if (!IsAvailable) return false;

        y += GetNaturalOffsetY();

        if (ApplyOffsetReflected(x, y, z, rotation, pitch, roll))
        {
            overrideActive = true;
            lastAppliedViaReflection = true;
            LogAppliedPath("reflection (syncs)");
            return true;
        }

        // IPC fallback: applies locally only, no sync
        if (registerPlayerSubscriber == null)
        {
            LogAppliedPath("none (no RegisterPlayer subscriber)");
            return false;
        }
        try
        {
            var json = "{\"TempOffset\":" +
                       $"{{\"X\":{F(x)},\"Y\":{F(y)},\"Z\":{F(z)},\"R\":{F(rotation)},\"Pitch\":{F(pitch)},\"Roll\":{F(roll)}}}}}";
            registerPlayerSubscriber.InvokeAction(LocalPlayerObjectIndex, json);
            overrideActive = true;
            lastAppliedViaReflection = false;
            LogAppliedPath("IPC RegisterPlayer (local only)");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Simple Heels ApplyOffset failed: {ex.Message}");
            return false;
        }
    }

    // Logs only when the path changes; ApplyOffset can run every frame
    private void LogAppliedPath(string path)
    {
        if (path == lastAppliedPath) return;
        lastAppliedPath = path;
        log.Information($"[SimpleHeels] ApplyOffset using: {path}");
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
        readySubscriber?.Unsubscribe(TryInitialize);
        ClearOffset();
    }
}
