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

    // RMIWalk hook overrides movement input to walk to a destination
    private delegate void RMIWalkDelegate(
        void* self, float* sumLeft, float* sumForward,
        float* sumTurnLeft, byte* haveBackwardOrStrafe,
        byte* a6, byte bAdditiveUnk);

    [Signature("E8 ?? ?? ?? ?? 80 7B 3E 00 48 8D 3D", DetourName = nameof(RMIWalkDetour))]
    private Hook<RMIWalkDelegate>? RMIWalkHook { get; init; } = null;

    private volatile bool isWalking;
    private Vector3 destination;
    private Action? onArrived;
    private Action? onCancelled;
    private long walkStartTick;
    private Action<Vector3>? onSnap;

    private const float SnapDistance = 0.05f;
    private const long TimeoutMs = 2000;

    public bool IsMovingToDestination => isWalking;
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
        RMIWalkHook!.Original(self, sumLeft, sumForward, sumTurnLeft,
            haveBackwardOrStrafe, a6, bAdditiveUnk);

        if (!isWalking) return;

        if (Environment.TickCount64 - walkStartTick > TimeoutMs)
        {
            log.Warning("Walk timed out after 2s");
            Arrive();
            return;
        }

        // user input cancels
        if (*sumLeft != 0 || *sumForward != 0)
        {
            Cancel();
            return;
        }

        // skip additive/continuation passes
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

        if (horizDist <= SnapDistance)
        {
            Arrive();
            return;
        }

        var dirH = MathF.Atan2(diff.X, diff.Z);

        // sumForward/sumLeft are camera-relative in both standard and legacy mode
        var camera = CameraManager.Instance()->GetActiveCamera();
        float refDir;
        if (camera != null)
        {
            refDir = *(float*)((byte*)camera + 0x140) + MathF.PI;
        }
        else
        {
            refDir = player.Rotation;
        }

        var relAngle = dirH - refDir;
        var forward = MathF.Cos(relAngle);
        var left = MathF.Sin(relAngle);

        const float slowdownRadius = 0.3f;
        if (horizDist < slowdownRadius)
        {
            var scale = horizDist / slowdownRadius;
            scale = MathF.Max(scale, 0.15f);
            forward *= scale;
            left *= scale;
        }

        *sumForward = forward;
        *sumLeft = left;
    }
}
