using System.Numerics;
using AnoMech.Core.Game;
using AnoMech.Core.Native.Interfaces;
using AnoMech.Core.SimObjects;
using AnoMech.Scenarios;
using AnoMech.Scenarios.Top.P2PartySynergy;
using AnoMech.Scenarios.Umad.P3BlackHole;
using AnoMech.Scenarios.Umad.P3LimitCut;
using static AnoMech.Core.Game.Party.PartyRole;

namespace AnoMech.Tests;

// The user's pause (Game.Pause/Resume) and the holds a halted solo run puts on what the client
// advances by itself. Party Synergy: the glitch (27s) lands on everyone at 7.93, the Omega clones
// cast their attacks at 13.81 with their omens revealed 0.5s in.
public class GamePauseTests
{
    private const int Seed = 1;
    private const float Frame = ScenarioRun.FrameSeconds;
    private static readonly Type Synergy = typeof(TopP2PartySynergyScenario);

    private static ScenarioRun Run(Action<ScenarioProbe> probe, ScenarioRunOptions? options = null, Type? scenario = null)
        => ScenarioRun.Execute(scenario ?? Synergy, 0, Seed, (options ?? new ScenarioRunOptions()) with { Probe = probe });

    private static IEnumerable<TestCaseData> ScenarioStrats()
        => from scenario in ScenarioCatalog.Create().Scenarios
           from strat in scenario.AiStrats.Select((ai, index) => (ai, index))
           select new TestCaseData(scenario.GetType(), strat.index)
               .SetName($"{scenario.FullName()} [{strat.ai.Name}] with pauses");

    [TestCaseSource(nameof(ScenarioStrats))]
    public void SunnyDayStillClearsWithPauses(Type scenarioType, int strat)
    {
        var seeds = TestContext.Parameters.Get("SunnySeeds", 1);
        var runs = Enumerable.Range(0, seeds).Select(_ => RunWithPauses(scenarioType, strat, Random.Shared.Next())).ToList();
        var failures = runs.Where(r => !r.Run.Passed).Select(r => r.Run).ToList();

        Assert.That(runs.Select(r => r.PausedFrames), Has.All.GreaterThanOrEqualTo(600));
        Assert.That(failures, Is.Empty, () => $"{failures.Count}/{seeds} seeds failed:{Environment.NewLine}" + string.Join(Environment.NewLine, failures));
    }

    // A long pause early on, then a short one every 5s of the fight, from where the seed picks.
    private static (ScenarioRun Run, int PausedFrames) RunWithPauses(Type scenarioType, int strat, int seed)
    {
        var offset = 0.5f + 4.5f * (float)new Random(seed).NextDouble();
        var pauses = Enumerable.Range(0, 120).Select(i => offset + 5f * i).ToList();
        var paused = 0;
        var run = ScenarioRun.Execute(scenarioType, strat, seed, new ScenarioRunOptions
        {
            Probe = p =>
            {
                p.PauseFor(offset / 2f, 600);
                foreach (var at in pauses) p.PauseFor(at, 30);
                if (p.Game.UserPaused) paused++;
            },
        });
        return (run, paused);
    }

    [Test]
    public void ScheduledEventsWaitOutThePause()
    {
        float? firedAt = null;
        var firedWhilePaused = false;
        var clockWhilePaused = new List<float>();
        var run = Run(p =>
        {
            if (p.Crossed(12f))
                p.World.Events.Add(0.05f, () =>
                {
                    firedAt = p.World.Events.Elapsed;
                    firedWhilePaused = p.Game.UserPaused;
                });
            p.PauseFor(12f, 120);
            if (p.Game.UserPaused) clockWhilePaused.Add(p.Time);
        });

        Assert.That(run.Passed, run.ToString);
        Assert.That(clockWhilePaused, Has.Count.EqualTo(120));
        Assert.That(clockWhilePaused.Distinct().Count(), Is.EqualTo(1), "the clock moved during the pause");
        Assert.That(firedAt, Is.Not.Null.And.GreaterThanOrEqualTo(clockWhilePaused[0] + 0.05f - 1e-4f));
        Assert.That(firedWhilePaused, Is.False);
    }

    // After the first halted frame, whose settle moves are its only packets, the sim sends nothing
    // and nobody moves or turns. At 19.1s in Black Hole the bosses and bots are walking.
    [Test]
    public void NothingMovesOrIsSentWhilePaused()
    {
        var rowsSeen = -1;
        var rowsSentLater = new List<string>();
        Dictionary<SimCharacter, Placement>? atPause = null;
        Dictionary<SimCharacter, Placement> lastFrame = [];
        var movingAtPause = new List<SimCharacter>();
        var moved = new List<string>();
        var run = Run(p =>
        {
            p.PauseFor(19.1f, 90);
            var characters = p.World.Party.AllMembers().Concat(p.World.Children.OfType<SimEnemy>()).ToList();
            var now = characters.ToDictionary(c => c, c => new Placement(c.Position, c.Rotation));
            if (!p.Game.UserPaused)
            {
                if (atPause is not null) return;
                movingAtPause = characters.Where(c => lastFrame.TryGetValue(c, out var was) && was.Position != now[c].Position).ToList();
                lastFrame = now;
                return;
            }
            var rows = p.Fake.Timeline.Rows;
            if (atPause is null)
            {
                rowsSeen = rows.Count;
                atPause = now;
                return;
            }
            rowsSentLater.AddRange(rows.Skip(rowsSeen).Select(r => $"{r["kind"]} {r["actor_name"]} {r["id"]}"));
            rowsSeen = rows.Count;
            moved.AddRange(characters.Where(c => !atPause.TryGetValue(c, out var was) || was != now[c]).Select(c => c.GetType().Name));
        }, scenario: typeof(UmadP3BlackHoleScenario));

        Assert.That(run.Passed, run.ToString);
        Assert.That(atPause, Is.Not.Null);
        Assert.That(movingAtPause.OfType<SimEnemy>(), Is.Not.Empty, "no enemy was walking when the pause began");
        Assert.That(movingAtPause.OfType<SimPartyNpc>(), Is.Not.Empty, "no bot was walking when the pause began");
        Assert.That(rowsSentLater, Is.Empty);
        Assert.That(moved, Is.Empty);
    }

    // Everything the sim sends goes out at the same fight time as in an unpaused run of the same
    // seed. Model moves are left out: the settle restarts their send interval.
    [Test]
    public void ResumedRunPlaysOnAsIfNeverPaused()
    {
        var straight = Rows(_ => { });
        var paused = Rows(p =>
        {
            p.PauseFor(9f, 90);
            p.PauseFor(15.5f, 45);
        });

        Assert.That(paused, Is.EqualTo(straight));

        static List<string> Rows(Action<ScenarioProbe> pause)
        {
            IReadOnlyList<Dictionary<string, string>>? rows = null;
            var run = Run(p =>
            {
                rows ??= p.Fake.Timeline.Rows;
                pause(p);
            });
            Assert.That(run.Passed, run.ToString);
            return rows!.Where(r => r["kind"] != "move")
                .Select(r => $"{r["t"]} {r["kind"]} {r["actor"]} {r["id"]} {r["target"]}")
                .ToList();
        }
    }

    [Test]
    public void CastBarHoldsAndFinishesAfterResume()
    {
        var bars = HoldCastWhen((enemy, bar) => bar > 0.6f && bar < enemy.CastTotalSeconds - 1f, frames: 90);

        Assert.That(bars.Paused, Has.Count.EqualTo(90));
        Assert.That(bars.Paused, Has.All.EqualTo(bars.AtPause).Within(1e-4f));
        Assert.That(bars.FirstAfter, Is.EqualTo(bars.AtPause + Frame).Within(1e-4f));
        Assert.That(bars.Released, Is.True, "the cast never released after the pause");
    }

    // A pause in the last 0.45s with the omen up holds the bar 0.45s short of its end, where the
    // client still shows the omen; on resume the bar goes back to where it was.
    [Test]
    public void CastBarNearItsEndHoldsWhereTheOmenStaysUp()
    {
        var bars = HoldCastWhen((enemy, bar) => HasOmen(enemy) && bar > enemy.CastTotalSeconds - 0.42f && bar < enemy.CastTotalSeconds - 0.27f, frames: 60);

        Assert.That(bars.Paused, Has.All.EqualTo(bars.Total - 0.45f).Within(1e-4f));
        Assert.That(bars.AtPause, Is.GreaterThan(bars.Total - 0.45f));
        Assert.That(bars.FirstAfter, Is.EqualTo(bars.AtPause + Frame).Within(1e-4f));
        Assert.That(bars.Released, Is.True);
    }

    // Before its reveal the omen must stay hidden for the whole pause.
    [Test]
    public void CastBarBeforeTheOmenRevealHoldsBeforeIt()
    {
        var bars = HoldCastWhen((enemy, bar) => HasOmen(enemy) && enemy.Casting.OmenDelay > 0.3f && bar > 0.05f && bar < enemy.Casting.OmenDelay - 0.2f, frames: 60);

        Assert.That(bars.Paused, Has.All.EqualTo(bars.AtPause).Within(1e-4f));
        Assert.That(bars.Paused, Has.All.LessThan(bars.OmenDelay));
    }

    // AtPause is the bar the hold snapshots: the one on the frame the pause was asked for.
    private sealed record Bars(float AtPause, List<float> Paused, float FirstAfter, float Total, float OmenDelay, bool Released);

    // Pauses for `frames` frames from the first frame a casting enemy has a bar `when` picks, and
    // reads that enemy's bar as the client has it.
    private static Bars HoldCastWhen(Func<SimEnemy, float, bool> when, int frames)
    {
        SimEnemy? caster = null;
        uint actionId = 0;
        var effectSeq = 0;
        var atPause = 0f;
        var paused = new List<float>();
        float? firstAfter = null;
        var released = false;
        var run = Run(p =>
        {
            if (caster is null)
            {
                foreach (var enemy in p.World.Children.OfType<SimEnemy>())
                {
                    if (!enemy.IsCasting) continue;
                    var bar = Bar(p, enemy);
                    if (!when(enemy, bar)) continue;
                    caster = enemy;
                    actionId = enemy.CastActionId;
                    effectSeq = enemy.Casting.EffectSeq;
                    atPause = bar;
                    p.Game.Pause();
                    break;
                }
                return;
            }
            if (p.Game.UserPaused)
            {
                paused.Add(Bar(p, caster));
                if (paused.Count == frames) p.Game.Resume();
                return;
            }
            if (paused.Count < frames) return;
            firstAfter ??= Bar(p, caster);
            released |= caster.Casting.EffectSeq > effectSeq && caster.Casting.EffectActionId == actionId;
        });

        Assert.That(run.Passed, run.ToString);
        Assert.That(caster, Is.Not.Null, "no cast matched");
        return new Bars(atPause, paused, firstAfter ?? -1f, caster!.CastTotalSeconds, (byte)(caster.Casting.OmenDelay * 10) / 10f, released);
    }

    private static bool HasOmen(SimEnemy enemy) => Natives.Data.Action(enemy.CastActionId)?.OmenPath is not null;

    private static float Bar(ScenarioProbe p, SimCharacter character)
        => p.Fake.BattleCharas.Find(character.EntityId)?.CurrentCastTime ?? -1f;

    [Test]
    public void StatusTimersHoldWhilePaused()
    {
        SimCharacter? member = null;
        SimStatus? status = null;
        var simRemaining = new List<float>();
        var shownRemaining = new List<float>();
        var afterResume = new List<float>();
        var run = Run(p =>
        {
            p.PauseFor(10f, 120);
            if (p.Game.UserPaused)
            {
                member ??= p.Member(OffTank)!;
                status ??= member.ActiveStatuses.First(s => s.ShowsTimer);
                simRemaining.Add(status.RemainingTime);
                shownRemaining.Add(p.Fake.BattleCharas.Find(member.EntityId)!.ShownRemainingTime(status.StatusId));
            }
            else if (status is not null && afterResume.Count < 30) afterResume.Add(status.RemainingTime);
        });

        Assert.That(run.Passed, run.ToString);
        Assert.That(simRemaining, Has.Count.EqualTo(120));
        Assert.That(simRemaining, Has.All.EqualTo(simRemaining[0]));
        // The client counts one frame down after each resend.
        Assert.That(shownRemaining, Has.All.EqualTo(simRemaining[0] - Frame).Within(1e-3f));
        Assert.That(afterResume.Last(), Is.EqualTo(simRemaining[0] - afterResume.Count * Frame).Within(1e-3f));
    }

    // Walking out of the ring while paused is safe until the run goes on; then the fence takes the
    // player on its first frame.
    [Test]
    public void NobodyDiesWhilePausedButTheFenceKillsOnResume()
    {
        var deadWhilePaused = false;
        var pausedAt = 0f;
        var frames = 0;
        var run = Run(p =>
        {
            p.PauseFor(6f, 60);
            if (!p.Game.UserPaused) return;
            pausedAt = p.Time;
            if (++frames == 10)
                p.Fake.BattleCharas.Player.Position = p.World.Coordinates.ToGlobal(new Vector3(0f, 0f, 25f));
            deadWhilePaused |= p.World.Party.AllMembers().Any(m => m.Dead);
        }, new ScenarioRunOptions { PlayerRole = MeleeDpsA, WriteArtifactsOnFailure = false });

        Assert.That(frames, Is.EqualTo(60));
        Assert.That(deadWhilePaused, Is.False);
        Assert.That(run.Deaths, Is.Not.Empty, run.ToString);
        Assert.That(run.Deaths[0].Role, Is.EqualTo(MeleeDpsA));
        Assert.That(run.Deaths[0].ActionId, Is.EqualTo(SimCharacterDeathExtensions.Environment));
        Assert.That(run.Deaths[0].Time, Is.EqualTo(pausedAt + Frame).Within(1e-4f));
    }

    // Below the floor while paused: put back on the platform, not killed.
    [Test]
    public void FallWhilePausedIsCaughtWithoutDying()
    {
        var frames = 0;
        var heightAfterCatch = float.NaN;
        var run = Run(p =>
        {
            p.PauseFor(3f, 60);
            if (!p.Game.UserPaused) return;
            frames++;
            if (frames == 10)
                p.Fake.BattleCharas.Player.Position = p.World.Coordinates.ToGlobal(new Vector3(0f, -10f, 0f));
            if (frames == 20)
                heightAfterCatch = p.World.Coordinates.ToLocal(p.Fake.BattleCharas.Player.Position).Y;
        }, new ScenarioRunOptions { StopAt = 4f }, typeof(UmadP3LimitCutScenario));

        Assert.That(run.Deaths, Is.Empty, run.ToString);
        Assert.That(heightAfterCatch, Is.EqualTo(0f).Within(1e-3f));
    }

    [Test]
    public void PauseBlocksThePlayersActionsUntilResume()
    {
        var blocked = new List<bool>();
        bool? blockedAfter = null;
        var run = Run(p =>
        {
            p.PauseFor(5f, 30);
            if (p.Game.UserPaused) blocked.Add(p.Fake.PlayerInput.ActionsBlocked);
            else if (blocked.Count > 0) blockedAfter ??= p.Fake.PlayerInput.ActionsBlocked;
        }, new ScenarioRunOptions { StopAt = 6f });

        Assert.That(run.Passed, run.ToString);
        Assert.That(blocked, Has.Count.EqualTo(30).And.All.True);
        Assert.That(blockedAfter, Is.False);
    }

    // A walking bot's model gets one move to where the sim stopped it, then nothing; it ends up there
    // once the client has walked it in.
    [Test]
    public void WalkingBotSettlesWhereTheSimStoppedIt()
    {
        SimCharacter? bot = null;
        Vector3 stoppedAt = default;
        var frames = 0;
        var movesSent = 0;
        var rowsSeen = 0;
        var botMoved = false;
        var gapAtEnd = float.NaN;
        var run = Run(p =>
        {
            if (bot is null)
            {
                if (p.Time < 9f) return;
                bot = p.World.Party.AllMembers().FirstOrDefault(m => m is SimPartyNpc && m.MoveDestination is not null);
                if (bot is null) return;
                rowsSeen = p.Fake.Timeline.Rows.Count;
                stoppedAt = bot.Position;
                p.Game.Pause();
                return;
            }
            if (!p.Game.UserPaused) return;
            var rows = p.Fake.Timeline.Rows;
            movesSent += rows.Skip(rowsSeen).Count(r => r["kind"] == "move" && r["actor"] == bot.EntityId.ToString("X8"));
            rowsSeen = rows.Count;
            botMoved |= bot.Position != stoppedAt;
            var model = p.World.Coordinates.ToLocal(p.Fake.BattleCharas.Find(bot.EntityId)!.Position);
            gapAtEnd = Vector2.Distance(new Vector2(model.X, model.Z), new Vector2(bot.Position.X, bot.Position.Z));
            if (++frames == 90) p.Game.Resume();
        });

        Assert.That(run.Passed, run.ToString);
        Assert.That(bot, Is.Not.Null, "no bot was walking");
        Assert.That(botMoved, Is.False);
        Assert.That(movesSent, Is.EqualTo(1));
        Assert.That(gapAtEnd, Is.LessThan(0.01f));
    }

    // Events due on the resuming frame run before the player's next tick; they see the player's
    // input as it is now, not as it was when the pause began.
    [Test]
    public void ResumeSeesWhatThePlayerIsDoingNow()
    {
        var frames = 0;
        bool? movingWhilePaused = null;
        bool? movingOnResume = null;
        var run = Run(p =>
        {
            p.PauseFor(4f, 30);
            if (!p.Game.UserPaused || ++frames != 10) return;
            p.HoldMovementInput();
            movingWhilePaused = p.Game.Player!.IsMoving;
            p.World.Events.Add(0f, () => movingOnResume = p.Game.Player!.IsMoving);
        }, new ScenarioRunOptions { StopAt = 5f, Takeovers = [new Takeover(1f, null)] });

        Assert.That(run.Failure, Is.Null, run.ToString);
        Assert.That(movingWhilePaused, Is.False);
        Assert.That(movingOnResume, Is.True);
    }

    [Test]
    public void PauseWithoutARunIsRefused()
    {
        var fake = FakeGame.Install();
        var chat = ChatLog.Create();
        DalamudServices.Install(nameof(Plugin.ChatGui), chat);
        using var game = new Game();
        DalamudServices.Install(nameof(Plugin.Game), game);

        game.TogglePause();
        game.Resume();
        fake.Framework.RunPending();

        Assert.That(game.UserPaused, Is.False);
        Assert.That(chat.Lines, Is.EqualTo(new[] { $"[{Plugin.DisplayName}] No run to pause." }));
    }

    [Test]
    public void PauseIsRefusedInMultiplayer()
    {
        var haltedEver = false;
        List<string>? chat = null;
        var run = Run(p =>
        {
            if (p.Crossed(3f)) p.Game.Pause();
            haltedEver |= p.Game.IsHalted;
            if (p.Time < 3.5f) return;
            chat = p.Chat.ToList();
            p.Stop();
        }, new ScenarioRunOptions { Mode = SessionMode.Host });

        Assert.That(run.Failure, Is.Null, run.ToString);
        Assert.That(haltedEver, Is.False);
        Assert.That(chat, Does.Contain($"[{Plugin.DisplayName}] Pause is not available in multiplayer."));
    }

    // The freeze after a death is final: no pause on top of it, and the player stays free. It holds
    // the sim's actors still all the same.
    [Test]
    public void PauseIsRefusedDuringTheDeathFreeze()
    {
        var frozenFrames = 0;
        var userPaused = false;
        var blocked = false;
        var actorsHeld = 0;
        List<string>? chat = null;
        var run = Run(p =>
        {
            if (!p.Game.Paused) return;
            if (++frozenFrames == 1) p.Game.Pause();
            userPaused |= p.Game.UserPaused;
            blocked |= p.Fake.PlayerInput.ActionsBlocked;
            actorsHeld = p.Fake.ActorFreeze.Frozen.Count;
            if (frozenFrames < 10) return;
            chat = p.Chat.ToList();
            p.Stop();
        }, new ScenarioRunOptions
        {
            PlayerRole = MeleeDpsA,
            RunUntilStopped = true,
            WriteArtifactsOnFailure = false,
            Takeovers = [new Takeover(5f, new Vector2(0f, 25f))],
        });

        Assert.That(run.Deaths, Is.Not.Empty);
        Assert.That(frozenFrames, Is.EqualTo(10));
        Assert.That(userPaused, Is.False);
        Assert.That(blocked, Is.False);
        Assert.That(actorsHeld, Is.GreaterThan(0));
        Assert.That(chat, Does.Contain($"[{Plugin.DisplayName}] The run froze after a death. Stop to reset it."));
    }

    // Enemies and doppels hold still; the player, whom the user moves, never does.
    [Test]
    public void PauseFreezesTheAnimationOfEnemiesAndDoppels()
    {
        var heldBeforePause = false;
        HashSet<uint>? held = null, expected = null;
        int? heldAfterResume = null;
        uint player = 0;
        var run = Run(p =>
        {
            p.PauseFor(10f, 30);
            if (p.Game.UserPaused)
            {
                player = p.Fake.BattleCharas.Player.EntityId;
                held = p.Fake.ActorFreeze.Frozen.ToHashSet();
                expected = p.World.Party.AllMembers().OfType<SimPartyNpc>().Select(m => m.EntityId)
                    .Concat(p.World.Children.OfType<SimEnemy>().Select(e => e.EntityId))
                    .ToHashSet();
            }
            else if (held is null) heldBeforePause |= p.Fake.ActorFreeze.Frozen.Count > 0;
            else heldAfterResume ??= p.Fake.ActorFreeze.Frozen.Count;
        });

        Assert.That(run.Passed, run.ToString);
        Assert.That(heldBeforePause, Is.False);
        Assert.That(held, Is.EquivalentTo(expected!));
        Assert.That(held, Has.Count.GreaterThan(7));
        Assert.That(held, Has.None.EqualTo(0u).And.None.EqualTo(player));
        Assert.That(heldAfterResume, Is.EqualTo(0));
    }

    // The freeze comes 5s of fight time after the first death, however long a pause in between.
    [Test]
    public void PauseBetweenDeathAndFreezeDelaysTheFreeze()
    {
        var frozenWhilePaused = false;
        float? frozenAt = null;
        var run = Run(p =>
        {
            p.PauseFor(7f, 300);
            frozenWhilePaused |= p.Game.UserPaused && p.Game.Paused;
            if (p.Game.Paused) frozenAt ??= p.Time;
        }, new ScenarioRunOptions
        {
            PlayerRole = MeleeDpsA,
            WriteArtifactsOnFailure = false,
            Takeovers = [new Takeover(5f, new Vector2(0f, 25f))],
        });

        Assert.That(run.Deaths, Is.Not.Empty);
        Assert.That(frozenWhilePaused, Is.False);
        Assert.That(frozenAt, Is.EqualTo(run.Deaths[0].Time + 5f).Within(2 * Frame));
    }

    // A spawn sent just before the pause is taken in during it rather than left pending.
    [Test]
    public void PacketSpawnLandsDuringThePause()
    {
        var frames = 0;
        var pendingAtPause = 0;
        var pendingLater = -1;
        var run = Run(p =>
        {
            p.Fake.BattleCharas.EnemySpawnDelayFrames = 5;
            if (p.Crossed(2.23f)) pendingAtPause = p.World.Children.OfType<SimEnemy>().Count(e => e.PacketSpawnPending);
            p.PauseFor(2.23f, 30);
            if (p.Game.UserPaused && ++frames == 20)
                pendingLater = p.World.Children.OfType<SimEnemy>().Count(e => e.PacketSpawnPending || !e.IsActive);
        });

        Assert.That(run.Passed, run.ToString);
        Assert.That(pendingAtPause, Is.GreaterThan(0));
        Assert.That(pendingLater, Is.EqualTo(0));
    }

    [Test]
    public void ResetWhilePausedReleasesEverything()
    {
        var frames = 0;
        var resetAsked = false;
        bool? halted = null, blocked = null;
        SessionStatus? status = null;
        var enemiesLeft = -1;
        var actorsHeld = -1;
        var run = Run(p =>
        {
            p.PauseFor(15f, 600);
            if (!resetAsked)
            {
                if (p.Game.UserPaused && ++frames == 10)
                {
                    p.Game.Reset();
                    resetAsked = true;
                }
                return;
            }
            if (p.Game.ActiveScenario is not null) return;
            halted = p.Game.IsHalted;
            blocked = p.Fake.PlayerInput.ActionsBlocked;
            status = p.Game.Status;
            enemiesLeft = p.World.Children.OfType<SimEnemy>().Count();
            actorsHeld = p.Fake.ActorFreeze.Frozen.Count;
            p.Stop();
        });

        Assert.That(run.Failure, Is.Null, run.ToString);
        Assert.That(halted, Is.False);
        Assert.That(blocked, Is.False);
        Assert.That(status, Is.EqualTo(SessionStatus.InZone));
        Assert.That(enemiesLeft, Is.EqualTo(0));
        Assert.That(actorsHeld, Is.EqualTo(0));
    }

    [TestCase(false, 1)]
    [TestCase(true, 0)]
    public void PausedRunDoesNotExtendTheStreak(bool pause, int streak)
    {
        PracticeLoop? practice = null;
        var run = Run(p =>
        {
            practice ??= new PracticeLoop(p.Game);
            if (pause) p.PauseFor(5f, 10);
        });

        Assert.That(run.Passed, run.ToString);
        Assert.That(practice!.Streak, Is.EqualTo(streak));
    }
}
