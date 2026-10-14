using System;
using System.Collections.Generic;
using System.Linq;
using AnoMech.Core.Game.Ai;
using AnoMech.Core.Game.Party;
using AnoMech.Core.SimObjects;

namespace AnoMech.Scenarios.Top.P2PartySynergy;

public class TopP2PartySynergyLpduAi : IScenarioAi<TopP2PartySynergyState>
{
    public string Name => "[LPDU] BPOG/GOPB";
    public string? Group => "EU";

    private TopP2PartySynergyState state = null!;
    private List<PartyRole> symbolFlexers = [];

    public void Run(TopP2PartySynergyState s, SimWorld world)
    {
        state = s;
        symbolFlexers = SymbolFlexers();
        var ai = new AiManager(world);
        ai.Move(1f, LightPartyColumns);
        ai.Move(10f, LightPartyColumnsAfterSymbolFlex);
        ai.Move(11f, AttackDodge);
        ai.Move(15.1f, SpreadPositions, arrivalTime: 21.5f);
        ai.Move(23f, KnockbackPositions, arrivalTime: 28.4f);
        ai.Move(30f, StackPositions, arrivalTime: 33f);
    }

    private ISwapStep LightPartyColumns()
    {
        return AiMove.Create(
            new(-8, -6),
            new(8, -6),
            new(-8, -3),
            new(8, -3),
            new(-8, 0),
            new(8, 0),
            new(-8, 3),
            new(8, 3)
        ).NaturalOrder();
    }

    private IAiMove LightPartyColumnsAfterSymbolFlex()
    {
        return LightPartyColumns().ApplySwaps(TradeColumnSpotsBetweenSymbolFlexers);
    }

    private IAiMove AttackDodge()
    {
        return AiMove.Create(
                         new(-1, 1),
                         new(1, 1),
                         new(-1, 1),
                         new(1, 1),
                         new(-1, 1),
                         new(1, 1),
                         new(-1, 1),
                         new(1, 1)
                     )
                     .Assignments(state.Order.List)
                     .ApplySwaps(SwapIntoLightPartySides)
                     .ApplyPositions(ScaleToAttackSafeSpot, KeepLightPartyOneInWesternPocket, state.AttackDir.Apply);
    }

    private IAiMove SpreadPositions()
    {
        return AiMove.Create(
                         new(-11, -16),
                         new(11, -16),
                         new(-11, -5.5f),
                         new(11, -5.5f),
                         new(-11, 5.5f),
                         new(11, 5.5f),
                         new(-11, 16),
                         new(11, 16)
                     )
                     .Assignments(state.Order.List)
                     .ApplySwaps(SwapIntoLightPartySides, ReverseRightColumnOnFarGlitch)
                     .ApplyPositions(WidenMiddleRowsOnFarGlitch, state.NewNorthA.Apply);
    }

    private IAiMove KnockbackPositions()
    {
        return AiMove.Create(
                         new(-2, 0),
                         new(2, 0),
                         new(-2, 0),
                         new(2, 0),
                         new(-2, 0),
                         new(2, 0),
                         new(-2, 0),
                         new(2, 0)
                     )
                     .Assignments(state.Order.List)
                     .ApplySwaps(SwapIntoLightPartySides, ReverseRightColumnOnFarGlitch, SwapSouthernmostStackWithPartner)
                     .ApplyPositions(SendRightGroupBehindOnMidGlitch, StretchToWallOnFarGlitch, state.NewNorthB.Apply);
    }

    private IAiMove StackPositions()
    {
        return AiMove.Create(
                         new(-15, 0),
                         new(15, 0),
                         new(-15, 0),
                         new(15, 0),
                         new(-15, 0),
                         new(15, 0),
                         new(-15, 0),
                         new(15, 0)
                     )
                     .Assignments(state.Order.List)
                     .ApplySwaps(SwapIntoLightPartySides, ReverseRightColumnOnFarGlitch, SwapSouthernmostStackWithPartner)
                     .ApplyPositions(SendRightGroupBehindOnMidGlitch, KeepMidTetherPartnersApartAtStacks, StretchToWallOnFarGlitch, state.NewNorthB.Apply);
    }

    private void TradeColumnSpotsBetweenSymbolFlexers(IAiRoles s)
    {
        var leavingLightPartyOne = symbolFlexers.Where(IsLightPartyOne).OrderBy(LineupRank).ToList();
        var leavingLightPartyTwo = symbolFlexers.Where(r => !IsLightPartyOne(r)).OrderBy(LineupRank).ToList();
        for (var i = 0; i < leavingLightPartyOne.Count; i++)
            s.ByRole(leavingLightPartyOne[i], leavingLightPartyTwo[i]);
    }

    private void SwapIntoLightPartySides(IAiRoles s)
    {
        for (var i = 0; i < 4; i++)
            if (!EndsInLightPartyOne(s.RoleAt(2 * i)))
                s.ByPosition(2 * i, 2 * i + 1);
    }

    private void ReverseRightColumnOnFarGlitch(IAiRoles s)
    {
        if (state.Glitch != GlitchType.Far) return;
        s.ByPosition(1, 7);
        s.ByPosition(3, 5);
    }

    private void SwapSouthernmostStackWithPartner(IAiRoles s)
    {
        var first = s.PositionOf(state.Stacks[0]);
        var second = s.PositionOf(state.Stacks[1]);
        if (first % 2 != second % 2) return;
        var southernmost = s.RoleAt(Math.Max(first, second));
        s.ByRole(southernmost, TetherPartner(southernmost));
    }

    private void ScaleToAttackSafeSpot(IAiPositions move)
    {
        if (state.AttackF == OmegaAttack.Legs)
        {
            move.Multiply(1.77f);
            if (state.AttackM == OmegaAttack.Sword)
                move.MultiplyY(-1f);
        }
        else
            move.Multiply(state.AttackM == OmegaAttack.Sword ? 12f : 7.07f);
    }

    private void KeepLightPartyOneInWesternPocket(IAiPositions move)
    {
        if (MathF.Cos(state.AttackDir.RadiansFromNorth) < 0)
            move.MultiplyX(-1f);
    }

    private void WidenMiddleRowsOnFarGlitch(IAiPositions move)
    {
        if (state.Glitch != GlitchType.Far) return;
        for (var i = 2; i < 6; i++)
            move.MultiplyX(i, 18f / 11);
    }

    private void SendRightGroupBehindOnMidGlitch(IAiPositions move)
    {
        if (state.Glitch != GlitchType.Mid) return;
        for (var i = 0; i < 4; i++)
            move.Rotate(2 * i + 1, MathF.PI / 2);
    }

    private void KeepMidTetherPartnersApartAtStacks(IAiPositions move)
    {
        if (state.Glitch == GlitchType.Mid)
            move.Multiply(16f / 15);
    }

    private void StretchToWallOnFarGlitch(IAiPositions move)
    {
        if (state.Glitch == GlitchType.Far)
            move.Multiply(19f / 15);
    }

    private List<PartyRole> SymbolFlexers()
    {
        var flexers = new List<PartyRole>();
        for (var i = 0; i < 4; i++)
        {
            var a = state.Order[2 * i];
            var b = state.Order[2 * i + 1];
            if (IsLightPartyOne(a) == IsLightPartyOne(b))
                flexers.Add(LineupRank(a) > LineupRank(b) ? a : b);
        }
        return flexers;
    }

    private bool EndsInLightPartyOne(PartyRole role) => IsLightPartyOne(role) != symbolFlexers.Contains(role);

    private PartyRole TetherPartner(PartyRole role) => state.Order[Array.IndexOf(state.Order.List, role) ^ 1];

    private static bool IsLightPartyOne(PartyRole role) =>
        role is PartyRole.MainTank or PartyRole.RegenHealer or PartyRole.MeleeDpsA or PartyRole.PhysRangedDps;

    private static int LineupRank(PartyRole role) => role switch
    {
        PartyRole.MainTank or PartyRole.OffTank => 0,
        PartyRole.RegenHealer or PartyRole.ShieldHealer => 1,
        PartyRole.MeleeDpsA or PartyRole.MeleeDpsB => 2,
        _ => 3,
    };
}
