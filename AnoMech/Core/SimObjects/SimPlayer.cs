using AnoMech.Core.Game;
using AnoMech.Core.Game.Geometry;
using AnoMech.Core.Game.Motion;
using AnoMech.Core.Game.Party;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Core.UserActions;

namespace AnoMech.Core.SimObjects;

public sealed class SimPlayer : SimCharacter, ISimPartyMember
{
    private readonly PlayerMotion motion;

    public SimPlayer(Coordinates coordinates, ObstacleField obstacles)
        : base(coordinates, self => new PlayerMotion(coordinates, self.ActorControl, obstacles))
        => motion = (PlayerMotion)Motion;

    public PartyRole Role { get; set; }

    // For stillness/movement mechanics: IsMoving = movement input, a jump, any action, or an
    // in-flight debug-bot MoveTo; IsActing also counts auto-attacks. Forced false while KO'd.
    public bool IsMoving { get; private set; }
    public bool IsActing { get; private set; }

    internal override IBattleCharaProxy Proxy => Natives.BattleCharas.LocalPlayer;

    public void UseSprint(float duration)
    {
        if (DebugBotControl.Enabled && !Dead && !ActionsLocked) SprintHandler.Apply(this, duration);
    }

    public override void Tick(float deltaSeconds)
    {
        base.Tick(deltaSeconds);
        SyncStatusAffliction();
        SampleActivity();
    }

    // Compared against the client every tick, so a PlayerStateFlags from the real server that
    // clears it is undone on the next one.
    private void SyncStatusAffliction()
    {
        var status = AnyActiveStatusRow(row => row.LockMovement || row.LockActions);
        var control = AnyActiveStatusRow(row => row.LockControl);
        var conditions = Natives.PlayerConditions;
        if (conditions.StatusAffliction != status || conditions.ControlAffliction != control)
            conditions.SetAfflictions(status, control);
    }

    private void SampleActivity()
    {
        var hooks = Natives.PlayerInput;
        // Drained every frame, even while dead, so a stale press can't carry over.
        var actedThisFrame = hooks.PollActionUsed();
        if (Dead)
        {
            IsMoving = false;
            IsActing = false;
            return;
        }
        IsMoving = hooks.MovementInputActive || actedThisFrame || hooks.IsJumping || motion.IsMoving || motion.IsHeld;
        // The press is one frame, but the whole cast bar counts: an Acceleration Bomb landing
        // mid-cast catches the player acting, exactly as it would in the fight.
        IsActing = IsMoving || hooks.IsAutoAttacking || Proxy.IsCasting;
    }

    // The player's cast bar is the client's own: a halt lets it finish and resolve.
    internal override void BeginHalt() => Motion.Settle();

    // The user's pause: the player can walk around to look, but can't start anything new. A press
    // from before the pause mustn't count as acting once it ends.
    internal void SetHalted(bool halted)
    {
        Natives.PlayerInput.ActionsBlocked = halted;
        if (halted) Natives.PlayerInput.PollActionUsed();
    }

    // Events due on the resuming frame run before this player's next tick, and must see what the
    // player is doing now.
    internal override void EndHalt()
    {
        base.EndHalt();
        SetHalted(false);
        SampleActivity();
    }

    internal override void ClearRunState()
    {
        base.ClearRunState();
        SetHalted(false);
    }

    public void OnKilled(bool byEnvironment)
    {
        GoDown(byEnvironment);
    }

    public override void Despawn()
    {
        base.Despawn();
        SetHalted(false);
        Revive();
        if (Natives.PlayerConditions is { StatusAffliction: true } or { ControlAffliction: true })
            Natives.PlayerConditions.SetAfflictions(false, false);
    }
}
