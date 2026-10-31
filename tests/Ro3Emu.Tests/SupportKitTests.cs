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

    /// <summary>A level -1 requirement is a branch the skill excludes, not a skill nobody learns: a Mechanic learns its
    /// Madogear line (License 16309, Knuckle Boost 16313 to Arm Cannon's 16315), and then not the Axe line (16351) that
    /// excludes it, nor its skills (Axe Tornado 16308).</summary>
    [ClientDataFact]
    public void AMechanicLearnsOneOfItsTwoExclusiveLines()
    {
        var kit = Kit.Value;
        var mado = kit.Ask(6400, 16315, 5, new Dictionary<int, int>());
        Assert.NotNull(mado);
        Assert.True(mado!.ContainsKey(16313) && mado.ContainsKey(16309), string.Join(',', mado.Keys));
        Assert.DoesNotContain(16351, mado.Keys);
        Assert.Null(kit.Ask(6400, 16308, 5, mado)); // the Axe line, after the Madogear one
        Assert.Null(kit.Ask(6400, 16315, 5, kit.Ask(6400, 16308, 5, new Dictionary<int, int>())!)); // and the other way round
        Assert.Contains(kit.Attacks(6400), a => a.Group == 16315);
    }

    /// <summary>The frontline classes (JobConfig _iDungeonJobPositionID 101 "DMG Taken") hold a boss with Provoke.</summary>
    [ClientDataFact]
    public void TanksAreTheFrontlineClassesAndProvoke()
    {
        var kit = Kit.Value;
        Assert.All(new long[] { 1100, 1210, 1310, 1410 }, job => Assert.True(kit.IsTank(job), $"job {job}"));
        Assert.All(new long[] { 1400, 2400, 3400, 4400, 5400, 6400 }, job => Assert.False(kit.IsTank(job), $"job {job}"));
        Assert.Contains(kit.Taunts(1410), t => t.Group == 11004 && t.Aim == AttackAim.Self); // Provoke, round the Royal Guard
        Assert.DoesNotContain(kit.Attacks(1410), a => a.Group == 11004);
    }

    /// <summary>LC_ConditionList as the client checks a skill: a riding Ranger has Warg Hunt and not Arrow Storm (a bow
    /// and no wolf ridden), a Mechanic its Madogear attack only in its Madogear.</summary>
    [ClientDataFact]
    public void SkillsNeedTheirMountAndWeapon()
    {
        var kit = Kit.Value;
        const long Wolf = 1500400, Madogear = 1500300, Bow = 11;
        long arrowStorm = kit.Row(13302, 1)!.Value.Id, wargHunt = kit.Row(13323, 1)!.Value.Id, madoAttack = kit.Row(16310, 1)!.Value.Id;
        Assert.True(kit.Usable(arrowStorm, 0, Bow));
        Assert.False(kit.Usable(arrowStorm, Wolf, Bow));
        Assert.False(kit.Usable(arrowStorm, 0, 0)); // no bow
        Assert.True(kit.Usable(wargHunt, Wolf, Bow));
        Assert.False(kit.Usable(wargHunt, 0, Bow));
        Assert.True(kit.Usable(madoAttack, Madogear, 7));
        Assert.False(kit.Usable(madoAttack, 0, 7));
        Assert.True(kit.Usable(kit.Row(11004, 1)!.Value.Id, 0, 0)); // Provoke has no condition
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
