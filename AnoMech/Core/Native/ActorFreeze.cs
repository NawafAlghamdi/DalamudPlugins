using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Core.Native;

// Freezes the animation of the sim's own characters (bosses, helpers, doppels) while a run is
// halted. The engine recomputes TimelineContainer.OverallSpeed for every character each frame,
// after Framework.Update, so a plain write is undone in the same frame; this re-asserts 0 right
// after the recompute, the same way Brio freezes actors. Clearing Frozen is the whole resume: the
// next recompute sees the stored 0 differ from the real speed and restores it, so every timeline
// slot continues from the frame it stopped on. It doesn't reach actor VFX (head markers), which
// keep their own lifetime. Owned by Plugin (session-lifetime).
//
// Fallible by design: if a patch breaks the signature, the hook stays null and halted runs just
// keep animating, while everything else in a pause still works.
public sealed unsafe class ActorFreeze : IDisposable
{
    // Call site of the recompute the engine actually runs. FFXIVClientStructs'
    // TimelineContainer.Addresses.CalculateAndApplyOverallSpeed resolves to an unreferenced
    // twin of this function in the current build, so hooking it would do nothing.
    [Signature("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83",
        DetourName = nameof(CalculateAndApplyOverallSpeedDetour), Fallibility = Fallibility.Fallible)]
    private Hook<TimelineContainer.Delegates.CalculateAndApplyOverallSpeed>? overallSpeedHook = null;

    // Doppels and bosses get entity ids 0xE0000000 + their CharacterManager.BattleCharas index
    // (CharacterManagerHelper); index 0 is always the local player, which is never frozen.
    private const uint SimEntityIdBase = 0xE0000000;
    private const uint BattleCharaCapacity = 100;

    // Set to "a run is halted" every frame by Game, cleared on reset. The hook sits on a
    // per-character, per-frame path in all content, so it is only enabled while frozen; once
    // disabled, the original recompute alone restores the real speed.
    public bool Frozen
    {
        get => frozen;
        set
        {
            if (frozen == value) return;
            frozen = value;
            if (value) overallSpeedHook?.Enable();
            else overallSpeedHook?.Disable();
        }
    }
    private bool frozen;

    public ActorFreeze(IGameInteropProvider interop)
    {
        interop.InitializeFromAttributes(this);
        if (overallSpeedHook == null)
            Plugin.Log.Warning("[ActorFreeze] Animation-speed signature not found; halted runs will keep animating.");
    }

    public void Dispose() => overallSpeedHook?.Dispose();

    private bool CalculateAndApplyOverallSpeedDetour(TimelineContainer* timeline)
    {
        var changed = overallSpeedHook!.Original(timeline);
        if (!Frozen || !IsSimActor(timeline->OwnerObject)) return changed;
        timeline->OverallSpeed = 0f;
        timeline->Flags3 |= 0x04;   // the dirty bit the recompute sets when it changes the speed
        return true;
    }

    private static bool IsSimActor(Character* owner)
    {
        if (owner == null) return false;
        var id = owner->EntityId;
        return id > SimEntityIdBase && id < SimEntityIdBase + BattleCharaCapacity;
    }
}
