using AnoMech.Scenarios.Top.P2PartySynergy;
using static AnoMech.Core.Game.Party.PartyRole;

namespace AnoMech.Tests;

// Solo seats only the player, so a Spotlight on them would always be a stack of one.
public class TopP2PartySynergySoloTests
{
    [Test]
    public void SoloPlayerPickedForAStackGetsNoSpotlight()
    {
        var run = ScenarioRun.Execute(typeof(TopP2PartySynergyScenario), 0, Random.Shared.Next(), new ScenarioRunOptions
        {
            PlayerRole = RegenHealer,
            Solo = true,
            GodMode = true,
            StopAt = 35f,
            Overrides = o => ((TopP2PartySynergyStateOverrides)o).Stack[RegenHealer] = true,
        });

        Assert.That(run.Warnings.Where(w => w.Contains("Spotlight")), Is.Empty, run.ToString);
    }
}
