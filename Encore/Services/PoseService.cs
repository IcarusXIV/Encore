using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Encore.Services;

public sealed unsafe class PoseService : IDisposable
{
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly IPluginLog log;

    // useEmote function pointer
    [Signature("E8 ?? ?? ?? ?? 40 84 ED 74 ?? 48 8B 4B ?? 48 8B 01 FF 90")]
    private readonly delegate* unmanaged<nint, ushort, nint, byte, byte, void> useEmote = null!;

    // Hook to suppress snap-to-furniture on sit/doze
    private delegate byte ShouldSnapDelegate(Character* a1, SnapPosition* a2);

    [Signature("E8 ?? ?? ?? ?? 84 C0 0F 84 ?? ?? ?? ?? 4C 8D 74 24", DetourName = nameof(ShouldSnapDetour))]
    private Hook<ShouldSnapDelegate>? ShouldSnapHook { get; init; } = null;

    // Hook to restore position on unsit/undoze
    [Signature("48 83 EC 38 F3 0F 10 05 ?? ?? ?? ?? 45 33 C9", DetourName = nameof(ShouldSnapUnsitDetour))]
    private Hook<ShouldSnapDelegate>? ShouldSnapUnsitHook { get; init; } = null;

    [StructLayout(LayoutKind.Explicit, Size = 0x38)]
    public struct SnapPosition
    {
        [FieldOffset(0x00)] public FFXIVClientStructs.FFXIV.Common.Math.Vector3 PositionA;
        [FieldOffset(0x10)] public float RotationA;
        [FieldOffset(0x20)] public FFXIVClientStructs.FFXIV.Common.Math.Vector3 PositionB;
        [FieldOffset(0x30)] public float RotationB;
    }

    // State for snap suppression and position restore
    private bool suppressSnap;
    private Vector3? savedPosition;
    private float? savedRotation;

    public PoseService(IGameInteropProvider gameInteropProvider, IObjectTable objectTable, IFramework framework, IPluginLog log)
    {
        this.objectTable = objectTable;
        this.framework = framework;
        this.log = log;

        gameInteropProvider.InitializeFromAttributes(this);
        ShouldSnapHook?.Enable();
        ShouldSnapUnsitHook?.Enable();

        log.Information("PoseService initialized");
    }

    public void Dispose()
    {
        ShouldSnapHook?.Dispose();
        ShouldSnapUnsitHook?.Dispose();
    }

    // Write a pose index directly to PlayerState for the given pose type.
    public void SetPoseIndex(EmoteController.PoseType type, byte index)
    {
        PlayerState.Instance()->SelectedPoses[(int)type] = index;
        log.Debug($"Set pose index for {type} to {index}");
    }

    /// <summary>
    /// Execute chair-sit anywhere by saving position, then calling useEmote(96).
    /// Position is restored on stand-up via the ShouldSnapUnsit hook.
    /// </summary>
    public void ExecuteSitAnywhere()
    {
        var player = (Character*)(objectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null)
        {
            log.Warning("Cannot sit anywhere - local player not found");
            return;
        }

        savedPosition = new Vector3(
            player->GameObject.Position.X,
            player->GameObject.Position.Y,
            player->GameObject.Position.Z);
        savedRotation = player->GameObject.Rotation;

        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Emote);
        useEmote((nint)agent, 96, nint.Zero, 0, 0);
        log.Debug("Executed sit-anywhere (emote 96)");
    }

    // Execute doze anywhere by suppressing snap, then calling useEmote(88).
    public void ExecuteDozeAnywhere()
    {
        suppressSnap = true;
        var agent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Emote);
        useEmote((nint)agent, 88, nint.Zero, 0, 0);
        suppressSnap = false;
        log.Debug("Executed doze-anywhere (emote 88)");
    }

    // Cycle /cpose until the character's visible pose matches the target index.
    public void CycleCPoseToIndex(byte targetIndex)
    {
        var player = (Character*)(objectTable.LocalPlayer?.Address ?? nint.Zero);
        if (player == null)
        {
            log.Warning("Cannot cycle cpose - local player not found");
            return;
        }

        // Check if already at the target pose
        if (player->EmoteController.CPoseState == targetIndex)
        {
            log.Debug($"Already at cpose {targetIndex}, no cycling needed");
            return;
        }

        log.Debug($"Cycling /cpose from {player->EmoteController.CPoseState} to {targetIndex}");

        // Run cycling in background to avoid blocking the framework thread
        // Use Task.Delay().Wait() instead of await to avoid CS4004 (unsafe context)
        Task.Run(() =>
        {
            for (var i = 0; i < 8; i++)
            {
                framework.RunOnFrameworkThread(() => ExecuteChatCommand("/cpose"));
                Task.Delay(80).Wait();

                // Check if we reached the target
                var reached = false;
                framework.RunOnFrameworkThread(() =>
                {
                    var p = (Character*)(objectTable.LocalPlayer?.Address ?? nint.Zero);
                    if (p != null && p->EmoteController.CPoseState == targetIndex)
                        reached = true;
                }).Wait();

                if (reached)
                {
                    log.Debug($"Reached target cpose {targetIndex} after {i + 1} cycle(s)");
                    return;
                }
            }

            log.Warning($"Failed to reach cpose {targetIndex} after 8 cycles");
        });
    }

    private void ExecuteChatCommand(string command)
    {
        var uiModule = UIModule.Instance();
        if (uiModule != null)
        {
            using var str = new Utf8String(command);
            uiModule->ProcessChatBoxEntry(&str);
        }
    }

    // Hook detour: suppress snap-to-furniture when we're doing sit/doze anywhere
    private byte ShouldSnapDetour(Character* a1, SnapPosition* a2)
    {
        return (byte)(suppressSnap ? 0 : ShouldSnapHook!.Original(a1, a2));
    }

    // Hook detour: restore saved position when standing up from sit-anywhere
    private byte ShouldSnapUnsitDetour(Character* player, SnapPosition* snapPosition)
    {
        var orig = ShouldSnapUnsitHook!.Original(player, snapPosition);

        if (orig != 0 && savedPosition != null && savedRotation != null)
        {
            var dist = Vector3.Distance(
                new Vector3(player->GameObject.Position.X, player->GameObject.Position.Y, player->GameObject.Position.Z),
                savedPosition.Value);

            if (dist < 3f)
            {
                snapPosition->PositionB.X = savedPosition.Value.X;
                snapPosition->PositionB.Y = savedPosition.Value.Y;
                snapPosition->PositionB.Z = savedPosition.Value.Z;
                snapPosition->RotationB = savedRotation.Value;
            }

            savedPosition = null;
            savedRotation = null;
        }

        return orig;
    }
}
