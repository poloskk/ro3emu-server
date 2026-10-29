using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Population;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    /// <summary>A fake player's fight (FakePlayer.AttackOnceAsync), as a player: a Wizard learns its attack skills with
    /// 31151 and its ground-aimed one is accepted at a monster's position.</summary>
    [ClientDataFact]
    public async Task AWizardLearnsItsAttackSkillsAndCastsOneAtAMonster()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99 70"); await Gm("alice", "!job 2200");
        var kit = new SupportKit(_app.Services.GetRequiredService<GameData>());
        var learned = new Dictionary<int, int>();
        var known = new List<AttackSkill>();
        foreach (var skill in kit.Attacks(2200))
            foreach (int level in new[] { skill.Level, (skill.Level + 1) / 2, 1 }.Distinct())
            {
                if (kit.Ask(2200, skill.Group, level, learned) is not { } ask) break;
                var req = new SkillGrowthSkillPlanUpdateReq();
                foreach (var (group, at) in ask) req.LevelUpList[group] = new SkillData { SkillId = group, SkillLevel = at };
                await alice.SendGameAsync(SkillService.PlanUpdate, req);
                if ((await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result != 0) continue;
                learned = ask;
                known.Add(skill with { Level = level });
                break;
            }
        var ground = Assert.Single(known.Take(1), s => s.Aim == AttackAim.Ground);
        var target = await Target("alice", stationary: true);
        var cast = new CastSkillReq
        {
            SkillId = (int)kit.Row(ground.Group, ground.Level)!.Value.Id,
            TargetPos = new Vec3 { X = target.X, Y = target.Y, Z = target.Z },
        };
        await alice.SendGameAsync(CombatService.UseSkill, cast);
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>
    /// What a support fake player does for its party host (FakePlayer.Support), as a player: a High Priest learns its
    /// support kit with the client's 31151 request, then Blessing (an area cast) lands on its party member.
    /// </summary>
    [ClientDataFact]
    public async Task AHighPriestLearnsItsSupportKitAndBlessesItsPartyMember()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99 70"); await Gm("alice", "!job 4300");
        var kit = new SupportKit(_app.Services.GetRequiredService<GameData>());
        var learned = new Dictionary<int, int>();
        foreach (var skill in kit.For(4300))
            foreach (int level in new[] { skill.Level, (skill.Level + 1) / 2, 1 }.Distinct())
            {
                if (kit.Ask(4300, skill.Group, level, learned) is not { } ask) break;
                var req = new SkillGrowthSkillPlanUpdateReq();
                foreach (var (group, at) in ask) req.LevelUpList[group] = new SkillData { SkillId = group, SkillLevel = at };
                await alice.SendGameAsync(SkillService.PlanUpdate, req);
                if ((await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result != 0) continue;
                learned = ask;
                break;
            }
        Assert.True(learned.GetValueOrDefault(14001) > 0, "Blessing learned");

        await bob.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(bob, TeamService.TeamCreate, TeamCreateResp.Parser);
        await bob.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)alice.Self!.Uuid });
        await Next(alice, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser);
        await alice.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
        { TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)bob.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);

        long blessing = kit.Row(14001, learned[14001])!.Value.Id;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = (int)blessing });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(bob, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.UnitId == (long)bob.Self.Uuid);
    }

    /// <summary>
    /// A Knight fake player's mount (FakePlayer.Mount), as a player: Peco Peco Riding learned with 31151, the Peco Peco
    /// leased with 32721 and ridden with 32745 tp OnBattle.
    /// </summary>
    [ClientDataFact]
    public async Task ALordKnightLearnsPecoRidingLeasesItsPecoAndRidesIt()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 60 40"); await Gm("alice", "!job 1300");
        var kit = new SupportKit(_app.Services.GetRequiredService<GameData>());
        var peco = kit.ClassItems(1300).First(i => i.Type == 2);
        var (group, level) = kit.UnlockSkill(peco)!.Value;
        var learn = new SkillGrowthSkillPlanUpdateReq();
        foreach (var (g, at) in kit.Ask(1300, group, level, new Dictionary<int, int>())!)
            learn.LevelUpList[g] = new SkillData { SkillId = g, SkillLevel = at };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);

        await alice.SendGameAsync(CareerSpecialService.Lease, new LeaseCareerSpecialItemReq { CartRemouldCfgId = (int)peco.Id });
        Assert.Equal(0, (await Next(alice, CareerSpecialService.Lease, LeaseCareerSpecialItemRsp.Parser)).ErrCode);
        await alice.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
        Assert.Equal(0, (await Next(alice, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        // Mounting again (a fake player's next session) is accepted and changes nothing.
        var riding = Session("alice").Mounts!.Clone();
        await alice.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
        Assert.Equal(0, (await Next(alice, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(riding, Session("alice").Mounts);
    }

    /// <summary>
    /// A fake player's gear (FakePlayer.Gear), as a player: GM !kit grants its set and names the pieces, each is worn with
    /// 31400, a grid is strengthened with 31404 at its configured cost, and with the talent panel's quests delivered a
    /// talent is raised with 83550.
    /// </summary>
    [ClientDataFact]
    public async Task AFakePlayersKitIsWornStrengthenedAndItsTalentsRaised()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 90 45"); await Gm("alice", "!job 1300");
        var plan = new GearPlan(_app.Services.GetRequiredService<GameData>());
        var set = plan.For(1300, 90, 6, twoHanded: false); // a guild leader's red set, appraised before it is worn
        Assert.All(set, p => Assert.True(p.Appraise));
        await Gm("alice", $"!quest done {AppraisalService.OpenQuest}");
        await Gm("alice", "!kit " + string.Join(' ', set.GroupBy(p => p.Item).Select(g => $"{g.Key}:{g.Count()}")));
        var reply = await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser, x => x.Content.StartsWith("kit"));
        var bag = reply.Content.Split(' ').Skip(1).Select(p => p.Split('='))
            .ToDictionary(p => int.Parse(p[0]), p => p[1].Split(',').Select(long.Parse).ToList());
        var guids = set.Select(p => { long g = bag[p.Item][0]; bag[p.Item].RemoveAt(0); return g; }).ToList();
        await alice.SendGameAsync(AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { guids } });
        Assert.Equal(0, (await Next(alice, AppraisalService.Appraise, EquipAppraiseResp.Parser)).Result);
        foreach (var (piece, guid) in set.Zip(guids))
        {
            await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Pos = (EquipGridType)piece.Grid, ItemGUID = guid });
            Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        }

        var cost = plan.StrengthenCost(1, 0, 2, 90)!;
        await Gm("alice", "!kit " + string.Join(' ', cost.GroupBy(c => c.Item).Select(g => $"{g.Key}:{g.Sum(c => c.Count)}")));
        await alice.SendGameAsync(EquipmentService.Strengthen, new StrengthenEquipmentReq { EquipPos = EquipGridType.MainHand, StrengthenNum = 2 });
        var strengthened = await Next(alice, EquipmentService.Strengthen, StrengthenEquipmentRsp.Parser);
        Assert.Equal((0, 2), (strengthened.Result, strengthened.StrengthenLv));

        await Gm("alice", $"!quest done {SeasonTalentService.ParentTask}"); await Gm("alice", $"!quest done {SeasonTalentService.TalentTask}");
        var node = plan.Talents(1300)[0];
        var price = plan.TalentCost(node, 1, 1300)!;
        await Gm("alice", "!kit " + string.Join(' ', price.GroupBy(c => c.Item).Select(g => $"{g.Key}:{g.Sum(c => c.Count)}")));
        await alice.SendGameAsync(SeasonTalentService.Upgrade, new SeasonTalentUpgradeReq { TalentId = (int)node.Id });
        Assert.Equal(0, (await Next(alice, SeasonTalentService.Upgrade, SeasonTalentUpgradeResp.Parser)).Result);
    }
}
