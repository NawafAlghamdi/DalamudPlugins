using System.Collections.Generic;
using AnoMech.Core.Native.Interfaces;
using Dalamud.Hooking;
using Dalamud.Utility.Signatures;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace AnoMech.Core.Native.Implementations;

// The engine recomputes TimelineContainer.OverallSpeed for every character each frame, after
// Framework.Update, so a plain write is undone in the same frame; the hook re-asserts 0 right after
// the recompute, the way Brio freezes actors. Letting an actor go is the whole resume: the next
// recompute sees the stored 0 differ from the real speed and restores it, so every timeline slot
// goes on from the frame it stopped on. Actor VFX (head markers) keep their own lifetime.
//
// The hook sits on a per-character, per-frame path in all content, so it is created on the first
// freeze rather than at load, and enabled only while something is frozen. Fallible: if a patch
// breaks the signature, the hook stays null and frozen actors just keep animating.
internal sealed unsafe class ActorFreeze : IActorFreeze
{
    // Call site of the recompute the engine actually runs. FFXIVClientStructs'
    // TimelineContainer.Addresses.CalculateAndApplyOverallSpeed resolves to an unreferenced twin of
    // this function, so hooking it would do nothing.
    [Signature("E8 ?? ?? ?? ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? 48 8D 8B ?? ?? ?? ?? 48 8B 01 FF 50 ?? F6 83",
        DetourName = nameof(CalculateAndApplyOverallSpeedDetour), Fallibility = Fallibility.Fallible)]
    private Hook<TimelineContainer.Delegates.CalculateAndApplyOverallSpeed>? overallSpeedHook = null;

    private bool hookResolved;
    private readonly HashSet<uint> frozen = [];

    public void Freeze(IReadOnlyCollection<uint> entityIds)
    {
        if (frozen.SetEquals(entityIds)) return;
        frozen.Clear();
        frozen.UnionWith(entityIds);
        if (frozen.Count == 0)
        {
            overallSpeedHook?.Disable();
            return;
        }
        ResolveHook();
        overallSpeedHook?.Enable();
    }

    private void ResolveHook()
    {
        if (hookResolved) return;
        hookResolved = true;
        Plugin.GameInterop.InitializeFromAttributes(this);
        if (overallSpeedHook == null)
            DiagnosticLog.Warn("[ActorFreeze] Animation-speed signature not found; paused actors will keep animating.");
    }

    public void Dispose() => overallSpeedHook?.Dispose();

    private bool CalculateAndApplyOverallSpeedDetour(TimelineContainer* timeline)
    {
        var changed = overallSpeedHook!.Original(timeline);
        var owner = timeline->OwnerObject;
        if (owner == null || !frozen.Contains(owner->EntityId)) return changed;
        timeline->OverallSpeed = 0f;
        // The dirty bit the recompute sets when it changes the speed.
        timeline->Flags3 |= 0x04;
        return true;
    }
}
