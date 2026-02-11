using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace Encore.Services;

public sealed unsafe class MovementService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IGameConfig gameConfig;
    private readonly IPluginLog log;

    // RMIWalk hook — overrides movement input to walk the character to a destination
    private delegate void RMIWalkDelegate(
        void* self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe,
        byte* a6, byte bAdditiveUnk);

    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", DetourName = nameof(RMIWalkDetour))]
    private Hook<RMIWalkDelegate>? RMIWalkHook { get; init; } = null;

    // Walk state
    private volatile bool isWalking;
    private Vector3 destination;
    private Action? onArrived;
    private Action? onCancelled;
    private long walkStartTick;
    private bool legacyMode;

    private const float ArrivalPrecision = 0.01f;
    private const long TimeoutMs = 2000;

    /// <summary>Whether a walk-to-destination is currently in progress.</summary>
    public bool IsMovingToDestination => isWalking;

    /// <summary>Whether the RMIWalk hook resolved successfully. If false, caller should use SetPosition fallback.</summary>
    public bool IsHookActive => RMIWalkHook != null;

    public MovementService(IGameInteropProvider gameInteropProvider, IObjectTable objectTable, IGameConfig gameConfig, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.gameConfig = gameConfig;
        this.log = log;

        try
        {
            gameInteropProvider.InitializeFromAttributes(this);
            RMIWalkHook?.Enable();
            log.Information($"MovementService initialized (hook active: {IsHookActive})");
        }
        catch (Exception ex)
        {
            log.Warning($"MovementService hook failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        isWalking = false;
        RMIWalkHook?.Dispose();
    }

    /// <summary>
    /// Start walking the local player toward the given destination.
    /// Cancels automatically if the user provides movement input or after 2 seconds.
    /// </summary>
    public void WalkTo(Vector3 dest, Action? arrived = null, Action? cancelled = null)
    {
        destination = dest;
        onArrived = arrived;
        onCancelled = cancelled;
        walkStartTick = Environment.TickCount64;

        // Cache movement mode at start (won't change during a < 2s walk)
        legacyMode = gameConfig.UiControl.TryGetUInt("MoveMode", out var mode) && mode == 1;

        isWalking = true;
        log.Debug($"WalkTo: destination=({dest.X:F2}, {dest.Y:F2}, {dest.Z:F2}), legacy={legacyMode}");
    }

    /// <summary>Cancel an in-progress walk. Safe to call even if not walking.</summary>
    public void Cancel()
    {
        if (!isWalking) return;
        isWalking = false;
        var cb = onCancelled;
        onCancelled = null;
        onArrived = null;
        cb?.Invoke();
        log.Debug("Walk cancelled");
    }

    private void Arrive()
    {
        isWalking = false;
        var cb = onArrived;
        onArrived = null;
        onCancelled = null;
        cb?.Invoke();
        log.Debug("Walk arrived at destination");
    }

    private void RMIWalkDetour(void* self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe, byte* a6, byte bAdditiveUnk)
    {
        // Always call original first to get real user input
        RMIWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft,
            haveBackwardOrStrafe, a6, bAdditiveUnk);

        if (!isWalking) return;

        // Timeout safety (2 seconds max)
        if (Environment.TickCount64 - walkStartTick > TimeoutMs)
        {
            log.Warning("Walk timed out after 2s");
            Arrive();
            return;
        }

        // Cancel if user provides movement input
        if (*sumLeft != 0 || *sumForward != 0)
        {
            Cancel();
            return;
        }

        // Only override on fresh input reads (not additive/continuation passes)
        if (bAdditiveUnk != 0) return;

        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            Cancel();
            return;
        }

        var playerPos = player.Position;
        var diff = destination - playerPos;
        var horizDist = MathF.Sqrt(diff.X * diff.X + diff.Z * diff.Z);

        // Check if arrived
        if (horizDist <= ArrivalPrecision)
        {
            Arrive();
            return;
        }

        // World-space direction to destination
        var dirH = MathF.Atan2(diff.X, diff.Z);

        // Reference direction depends on movement mode
        float refDir;
        if (legacyMode)
        {
            var camera = CameraManager.Instance()->GetActiveCamera();
            if (camera != null)
            {
                // DirH at offset 0x140 in the camera struct
                refDir = *(float*)((byte*)camera + 0x140) + MathF.PI;
            }
            else
            {
                refDir = player.Rotation;
            }
        }
        else
        {
            refDir = player.Rotation;
        }

        // Decompose relative direction into left/forward components
        var relAngle = dirH - refDir;
        *sumLeft = MathF.Sin(relAngle);
        *sumForward = MathF.Cos(relAngle);
    }
}
