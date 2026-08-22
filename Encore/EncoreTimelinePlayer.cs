using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Encore;

// Plays an animation timeline on any actor by overriding its base timeline.
public sealed unsafe class EncoreTimelinePlayer : IDisposable
{
    // Per-actor saved base timeline, so a stop can restore the original look.
    private readonly Dictionary<nint, ushort?> originalBaseByAddress = new();

    public void Dispose() => originalBaseByAddress.Clear();

    // Blend the intro (if any) and leave the loop as the actor's base override.
    public void PlayLoop(ICharacter character, ushort loopTimeline, ushort introTimeline)
    {
        if (loopTimeline == 0) return;

        var native = (Character*)character.Address;
        if (!originalBaseByAddress.TryGetValue(character.Address, out var saved) || saved == null)
            originalBaseByAddress[character.Address] = native->Timeline.BaseOverride;

        if (introTimeline != 0)
            Blend(character, introTimeline, 1);
        native->Timeline.BaseOverride = loopTimeline;
    }

    public void Stop(ICharacter character)
    {
        if (!originalBaseByAddress.TryGetValue(character.Address, out var saved) || saved == null)
            return;

        var native = (Character*)character.Address;
        native->Timeline.BaseOverride = saved.Value;
        originalBaseByAddress[character.Address] = null;
        Blend(character, 3); // standing idle
    }

    public bool IsPlaying(ICharacter character)
        => originalBaseByAddress.TryGetValue(character.Address, out var saved) && saved != null;

    private static void Blend(ICharacter character, ushort actionTimeline, int priority = -1)
    {
        var native = (Character*)character.Address;
        var animParams = (ActionTimelineAnimParams*)MemoryHelper.Allocate(0x60);
        Unsafe.InitBlockUnaligned(animParams, 0, 0x60);
        animParams->Intensity = 1.0f;
        animParams->StartTS = 0.0f;
        animParams->Unk1C = -1.0f;
        animParams->TargetObjId = 0xE0000000; // no target
        animParams->Priority = unchecked((uint)priority);
        animParams->Unk38 = -1;
        animParams->Unk3C = (actionTimeline == 3123) ? (byte)0 : (byte)0xFF;
        native->Timeline.TimelineSequencer.PlayTimeline(actionTimeline, animParams);
        MemoryHelper.Free((nint)animParams);
    }
}

[StructLayout(LayoutKind.Explicit, Size = 0x60)]
public struct ActionTimelineAnimParams
{
    [FieldOffset(0x00)] public nint vtblAddr;
    [FieldOffset(0x10)] public float Unk0;
    [FieldOffset(0x14)] public float Unk4;
    [FieldOffset(0x18)] public float Unk8;
    [FieldOffset(0x1C)] public float UnkC;
    [FieldOffset(0x20)] public float Unk10;
    [FieldOffset(0x24)] public float Intensity;
    [FieldOffset(0x28)] public float StartTS;
    [FieldOffset(0x2C)] public float Unk1C;
    [FieldOffset(0x30)] public ulong Unk20;
    [FieldOffset(0x38)] public ulong TargetObjId;
    [FieldOffset(0x40)] public uint Unk30;
    [FieldOffset(0x44)] public uint Priority;
    [FieldOffset(0x48)] public int Unk38;
    [FieldOffset(0x4C)] public byte Unk3C;
    [FieldOffset(0x4D)] public byte Unk3D;
    [FieldOffset(0x4E)] public byte Unk3E;
    [FieldOffset(0x4F)] public byte Unk3F;
    [FieldOffset(0x50)] public byte Unk40;
    [FieldOffset(0x52)] public byte Unk42;
}
