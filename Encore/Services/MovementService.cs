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

    // Snap callback — called when close enough to snap directly (avoids overshoot jitter)
    private Action<Vector3>? onSnap;

    private const float SnapDistance = 0.05f;
    private const long TimeoutMs = 2000;

    /// <summary>Whether a walk-to-destination is currently in progress.</summary>
    public bool IsMovingToDestination => isWalking;

    /// <summary>Whether the RMIWalk hook resolved successfully. If false, caller should use SetPosition fallback.</summary>
    public bool IsHookActive => RMIWalkHook != null;

    public MovementService(IGameInteropProvider gameInteropProvider, IObjectTable objectTable, IGameConfig gameConfig, IPluginLog log)
    {
        this.objectTable = objectTable;
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
    /// <param name="dest">World-space destination position.</param>
    /// <param name="arrived">Called when the character arrives (after snap).</param>
    /// <param name="cancelled">Called if the user cancels by providing movement input.</param>
    /// <param name="snap">Called to SetPosition the character for the final sub-0.05 unit snap. If null, arrival fires at snap distance without repositioning.</param>
    public void WalkTo(Vector3 dest, Action? arrived = null, Action? cancelled = null, Action<Vector3>? snap = null)
    {
        destination = dest;
        onArrived = arrived;
        onCancelled = cancelled;
        onSnap = snap;
        walkStartTick = Environment.TickCount64;

        isWalking = true;
        log.Debug($"WalkTo: destination=({dest.X:F2}, {dest.Y:F2}, {dest.Z:F2})");
    }

    /// <summary>Cancel an in-progress walk. Safe to call even if not walking.</summary>
    public void Cancel()
    {
        if (!isWalking) return;
        isWalking = false;
        var cb = onCancelled;
        onCancelled = null;
        onArrived = null;
        onSnap = null;
        cb?.Invoke();
        log.Debug("Walk cancelled");
    }

    private void Arrive()
    {
        isWalking = false;
        // Snap to exact position first, then fire arrived callback
        var snapCb = onSnap;
        var arrivedCb = onArrived;
        onSnap = null;
        onArrived = null;
        onCancelled = null;
        snapCb?.Invoke(destination);
        arrivedCb?.Invoke();
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

        // Close enough — snap to exact position instead of risking overshoot
        if (horizDist <= SnapDistance)
        {
            Arrive();
            return;
        }

        // World-space direction to destination
        var dirH = MathF.Atan2(diff.X, diff.Z);

        // Movement input is always relative to camera direction in both standard and legacy mode.
        // The game interprets sumForward/sumLeft relative to camera, then handles character
        // rotation differently per mode — but the input frame is always camera-relative.
        var camera = CameraManager.Instance()->GetActiveCamera();
        float refDir;
        if (camera != null)
        {
            refDir = *(float*)((byte*)camera + 0x140) + MathF.PI;
        }
        else
        {
            // Shouldn't happen, but fall back to player rotation
            refDir = player.Rotation;
        }

        // Decompose relative direction into left/forward components
        var relAngle = dirH - refDir;
        var forward = MathF.Cos(relAngle);
        var left = MathF.Sin(relAngle);

        // Scale down speed when close to destination to reduce overshoot
        const float slowdownRadius = 0.3f;
        if (horizDist < slowdownRadius)
        {
            var scale = horizDist / slowdownRadius;
            // Clamp minimum so the character doesn't stop moving entirely
            scale = MathF.Max(scale, 0.15f);
            forward *= scale;
            left *= scale;
        }

        *sumForward = forward;
        *sumLeft = left;
    }
}
