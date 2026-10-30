using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Population;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>The support skills fake players learn and cast for a party host, resolved from the exported client data.</summary>
public sealed class SupportKitTests
{
    private static readonly Lazy<SupportKit> Kit = new(() => new SupportKit(new GameData(
        Options.Create(new ServerOptions { GameDataDir = Path.Combine(ClientData.RepoRoot, "data") }), NullLogger<GameData>.Instance)));

    private static int[] Groups(long job) => [.. Kit.Value.For(job).Select(s => s.Group)];

    [ClientDataFact]
    public void EachSupportLineKeepsItsOwnBuffsAndTheJobsBefore()
    {
        Assert.Contains(14001, Groups(4100)); // Acolyte: Blessing
        Assert.DoesNotContain(14106, Groups(4100)); // Gloria is the Priest's
        Assert.Contains(14106, Groups(4200));
        Assert.Contains(14001, Groups(4300)); // a High Priest keeps the Acolyte's
        Assert.Contains(14201, Groups(4300)); // Assumptio
        Assert.Contains(16105, Groups(6200)); // Blacksmith: Adrenaline Rush
        Assert.Contains(13201, Groups(3300)); // Sniper: Wind Walker
        Assert.Contains(11413, Groups(1210)); // Crusader: Devotion
        Assert.Empty(Groups(2100)); // a Mage has none
    }

    /// <summary>The attack skills each class fights with: its latest job's damaging skills, aimed as they are cast.</summary>
    [ClientDataFact]
    public void EachClassFightsWithItsOwnDamagingSkills()
    {
        var wizard = Kit.Value.Attacks(2200);
        Assert.Contains(wizard, s => s.Group == 12108 && s.Aim == AttackAim.Ground); // Storm Gust
        var cross = Kit.Value.Attacks(5300);
        Assert.Contains(cross, s => s.Group == 15210 && s.Aim == AttackAim.Target); // Soul Destroyer
        Assert.Contains(Kit.Value.Attacks(1100), s => s.Group == 11002 && s.Aim == AttackAim.Self); // Magnum Break
        Assert.DoesNotContain(Kit.Value.Attacks(4100), s => s.Group == 14007); // Heal stays the host's
        Assert.All(new long[] { 2200, 5300, 1400, 3300 }, job => Assert.NotEmpty(Kit.Value.Attacks(job)));
        Assert.True(SupportKit.Casts(Kit.Value.Attacks(2200))); // a Wizard casts (one physical Spell Smash): it never swings
        Assert.False(SupportKit.Casts(Kit.Value.Attacks(1300))); // a Lord Knight swings between skills
        Assert.True(Kit.Value.IsBoss(5001027)); // a mini-boss: kept away from, fought from afar
        Assert.False(Kit.Value.IsBoss(5000001));
    }

    /// <summary>Each class's own buffs: kept up before it fights, never one that takes it out of the fight.</summary>
    [ClientDataFact]
    public void EachClassKeepsItsOwnBuffsButNeverStealthOrBerserk()
    {
        var kit = Kit.Value;
        Assert.Contains(11201, kit.Buffs(1300)); // Lord Knight: Aura Blade
        Assert.DoesNotContain(11214, kit.Buffs(1300)); // Berserk: no skills while it lasts
        Assert.Contains(12203, kit.Buffs(2300)); // High Wizard: Mystical Amplification
        Assert.Contains(13203, kit.Buffs(3300)); // Sniper: True Sight
        Assert.DoesNotContain(13201, kit.Buffs(3300)); // Wind Walker is a support skill, cast for the host
        Assert.DoesNotContain(15003, kit.Buffs(5300)); // Hiding
        Assert.DoesNotContain(15112, kit.Buffs(5300)); // Disguise
        Assert.Contains(16107, kit.Buffs(6200)); // Blacksmith: Maximize Power
    }

    [ClientDataFact]
    public void LearningASkillAsksForItsRequirementsInTheJobsTree()
    {
        var kit = Kit.Value;
        var agi = kit.For(4100).Single(s => s.Group == 14010);
        Assert.Equal(SupportUse.Area, agi.Use);
        var asked = kit.Requirements(4100, agi.Group, agi.Level);
        Assert.NotNull(asked);
        Assert.Equal(agi.Level, asked![agi.Group]);
        foreach (var pair in kit.Row(agi.Group, agi.Level)!.Value.PreSkillRequired.Where(p => p is [_, > 0]))
            Assert.True(asked.GetValueOrDefault((int)pair[0]) >= pair[1], $"requirement {pair[0]} {pair[1]} asked");
    }

    /// <summary>The class items each line leases and the skill each needs: a Knight's Peco Peco, a Rune Knight's dragon
    /// too, a Hunter's falcon; a Mage has none.</summary>
    [ClientDataFact]
    public void KnightsLeaseAPecoPecoAndLearnItsRiding()
    {
        var kit = Kit.Value;
        var knight = kit.ClassItems(1200);
        Assert.Contains(knight, i => i.Id == 1500201 && i.Type == 2);
        Assert.Equal((11107, 1), kit.UnlockSkill(knight.First(i => i.Id == 1500201)));
        Assert.Contains(kit.ClassItems(1400), i => i.Type == 2 && i.UnlockBaseLv > 30); // the Rune Knight's dragon
        Assert.Contains(kit.ClassItems(3200), i => i.Type == 1); // the falcon
        Assert.Empty(kit.ClassItems(2100));
    }

    /// <summary>A boss's area casts are dodged, not its swings (FakePlayer.Dodge): the 30.10 field MVP fight's casts.</summary>
    [ClientDataFact]
    public void ABossesAreaSkillsAreToldFromItsSwings()
    {
        var kit = Kit.Value;
        Assert.True(kit.StrikesArea(5201501)); // release 2, 20 m round the boss
        Assert.Equal(20f, kit.AreaRadius(5201501));
        Assert.True(kit.StrikesArea(5201401)); // release 4, 13 m ahead
        Assert.False(kit.StrikesArea(5204001)); // a swing: release 1, no area
    }
}
