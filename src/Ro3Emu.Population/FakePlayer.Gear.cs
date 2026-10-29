using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A fake player wears its job's gear for its level (GearPlan) and grows it as a player does: it strengthens one grid
/// at a time (31404) and raises its talents (83550), each paid at its configured cost. What it wears, each grid's level
/// and each talent's level are in the roster, so a login sends nothing new. The gear and the materials come from GM
/// !kit (one grant in the bot's own scope, never the realm's); the talent panel opens with its two quests (GM !quest
/// done, once, also in the bot's own scope). A bot that joins a real player's party gears up at once. A population choice (inferred): the original
/// server had only real players.
/// </summary>
public sealed partial class FakePlayer
{
    private const int StrengthenSteps = 2, TalentsPerStep = 2;
    private DateTime _gearAfter, _growAfter;
    private readonly HashSet<long> _talentsClosed = [];

    /// <summary>Red (quality 6) for a guild leader, gold (5) for the others; a two- or one-handed weapon fixed per role.</summary>
    private int GearQuality => entry.GuildLeader ? 6 : 5;
    private bool TwoHanded => entry.RoleId % 2 == 0;

    private async Task GearStepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // The first gear-up of a realm full of bare fake players is spread over three quarters of an hour.
        if (_gearAfter == default) _gearAfter = now + TimeSpan.FromMinutes(2 + _random.Next(43));
        if (now < _gearAfter || entry.Level == 0 || !entry.Prepared) return;
        var set = ctx.Gear.For(_job, entry.Level, GearQuality, TwoHanded);
        string worn = string.Join(',', set.Select(p => p.Item));
        bool wear = set.Count > 0 && worn != entry.Gear;
        if (!wear && now < _growAfter) return;
        // A few at a time across the realm (GearLane); a real player's party member does not queue.
        bool party = InParty;
        if (!party && !await ctx.GearLane.WaitAsync(0, ct)) { _gearAfter = now + TimeSpan.FromSeconds(30 + _random.Next(90)); return; }
        try
        {
            if (wear)
            {
                if (!await WearAsync(set, ct)) { _gearAfter = now + TimeSpan.FromMinutes(10 + _random.Next(10)); return; }
                entry.Gear = worn;
                ctx.Roster.Save();
            }
            if (now < _growAfter) return;
            _growAfter = now + TimeSpan.FromMinutes(40 + _random.Next(40));
            await GrowGearAsync(set.Select(p => p.Grid).Distinct().ToList(), ct);
        }
        finally { if (!party) ctx.GearLane.Release(); }
    }

    /// <summary>The set granted (!kit) and put on piece by piece (31400). False when anything failed.</summary>
    private async Task<bool> WearAsync(IReadOnlyList<GearPiece> set, CancellationToken ct)
    {
        if (set.Any(p => p.Appraise) && !await OpenAppraisalAsync(ct)) return false;
        if (await KitAsync(set.GroupBy(p => p.Item).Select(g => (g.Key, (long)g.Count())), ct) is not { } bag) return false;
        var pieces = new List<(GearPiece Piece, long Guid)>();
        foreach (var piece in set)
        {
            if (!bag.TryGetValue(piece.Item, out var guids) || guids.Count == 0) return false;
            pieces.Add((piece, guids[0]));
            guids.RemoveAt(0);
        }
        var unappraised = pieces.Where(p => p.Piece.Appraise).Select(p => p.Guid).ToList();
        if (unappraised.Count > 0 && !await AppraiseAsync(unappraised, ct)) return false;
        foreach (var (piece, guid) in pieces)
        {
            var req = new EquipItemOprReq { Pos = (EquipGridType)piece.Grid, ItemGUID = guid };
            int result = EquipItemOprResp.Parser.ParseFrom(await RequestAsync(CharacterService.EquipItemOpr, req, ct)).Result;
            if (result != 0)
            {
                PopulationContext.Log($"{entry.Name}: equip {piece.Item} refused ({result})");
                return false;
            }
            await Task.Delay(TimeSpan.FromSeconds(1 + _random.NextDouble() * 2), ct);
        }
        PopulationContext.Log($"{entry.Name}: wears {string.Join(',', set.Select(p => p.Item).Distinct())}");
        return true;
    }

    /// <summary>
    /// The pieces appraised (31401) at once; when that is refused as a whole because one already is (AppraiseParameter:
    /// a piece left in the bag by a gear-up cut short), one by one, those already appraised passed over.
    /// </summary>
    private async Task<bool> AppraiseAsync(IReadOnlyList<long> guids, CancellationToken ct)
    {
        async Task<int> Appraise(IEnumerable<long> some) => EquipAppraiseResp.Parser.ParseFrom(await RequestAsync(
            AppraisalService.Appraise, new EquipAppraiseReq { ItemGuids = { some } }, ct)).Result;
        int result = await Appraise(guids);
        if (result == ErrorCodes.AppraiseParameter && guids.Count > 1)
            foreach (long guid in guids)
                if ((result = await Appraise([guid])) is not (0 or ErrorCodes.AppraiseParameter)) break;
        if (result is 0 or ErrorCodes.AppraiseParameter) return true;
        PopulationContext.Log($"{entry.Name}: appraisal refused ({result})");
        return false;
    }

    /// <summary>
    /// One growth step: the least strengthened grid +<see cref="StrengthenSteps"/> (as far as the base level allows)
    /// and the next <see cref="TalentsPerStep"/> talent levels, their materials granted in one !kit first.
    /// </summary>
    private async Task GrowGearAsync(IReadOnlyList<int> grids, CancellationToken ct)
    {
        await OpenTalentsAsync(ct);
        var cost = new List<(int Item, long Count)>();
        int grid = grids.OrderBy(g => entry.Strength.GetValueOrDefault(g)).FirstOrDefault(), steps = 0;
        for (int s = StrengthenSteps; s > 0 && grid > 0 && steps == 0; s--)
            if (ctx.Gear.StrengthenCost(grid, entry.Strength.GetValueOrDefault(grid), s, entry.Level) is { } more)
            { cost.AddRange(more); steps = s; }
        var talents = new List<TalentNode>();
        if (entry.TalentsOpen)
            foreach (var node in ctx.Gear.Talents(_job).Where(n => entry.Talents.GetValueOrDefault(n.Id) < n.MaxLevel && !_talentsClosed.Contains(n.Id)))
            {
                if (talents.Count == TalentsPerStep) break;
                if (ctx.Gear.TalentCost(node, entry.Talents.GetValueOrDefault(node.Id) + 1, _job) is not { } price) continue;
                cost.AddRange(price);
                talents.Add(node);
            }
        if (cost.Count == 0) return;
        if (await KitAsync(cost.GroupBy(c => c.Item).Select(g => (g.Key, g.Sum(c => c.Count))), ct) == null) return;
        if (steps > 0) await StrengthenAsync(grid, steps, ct);
        foreach (var node in talents) await RaiseTalentAsync(node, ct);
        ctx.Roster.Save();
    }

    private async Task StrengthenAsync(int grid, int steps, CancellationToken ct)
    {
        var req = new StrengthenEquipmentReq { EquipPos = (EquipGridType)grid, StrengthenNum = steps };
        var rsp = StrengthenEquipmentRsp.Parser.ParseFrom(await RequestAsync(EquipmentService.Strengthen, req, ct));
        entry.Strength[grid] = rsp.StrengthenLv; // the server's level, on a refusal too
        if (rsp.Result != 0) PopulationContext.Log($"{entry.Name}: strengthen grid {grid} refused ({rsp.Result})");
    }

    private async Task RaiseTalentAsync(TalentNode node, CancellationToken ct)
    {
        var req = new SeasonTalentUpgradeReq { TalentId = (int)node.Id };
        int result = SeasonTalentUpgradeResp.Parser.ParseFrom(await RequestAsync(SeasonTalentService.Upgrade, req, ct)).Result;
        if (result == 0) entry.Talents[node.Id] = entry.Talents.GetValueOrDefault(node.Id) + 1;
        else if (result == ErrorCodes.SkillMaxLevel) entry.Talents[node.Id] = node.MaxLevel;
        // Locked (its parent not maxed, its tree not open this season) or not for this job: not again this session.
        else _talentsClosed.Add(node.Id);
    }

    /// <summary>The quest that opens appraisal (function 7), delivered once (GM !quest done, in the bot's own scope).</summary>
    private async Task<bool> OpenAppraisalAsync(CancellationToken ct)
    {
        if (entry.AppraiseOpen) return true;
        if (!await ctx.GmAsync(entry.Account, $"!quest done {AppraisalService.OpenQuest}", ct, ownRole: true)) return false;
        entry.AppraiseOpen = true;
        ctx.Roster.Save();
        return true;
    }

    /// <summary>The two quests that open the talent panel, delivered once (GM !quest done, in the bot's own scope).</summary>
    private async Task OpenTalentsAsync(CancellationToken ct)
    {
        if (entry.TalentsOpen) return;
        foreach (long quest in new[] { SeasonTalentService.ParentTask, SeasonTalentService.TalentTask })
            if (!await ctx.GmAsync(entry.Account, $"!quest done {quest}", ct, ownRole: true)) return;
        entry.TalentsOpen = true;
        ctx.Roster.Save();
    }

    /// <summary>
    /// Items granted through GM !kit in the bot's own scope; its reply ("kit entry=guid,guid ...") names the bag's unworn
    /// pieces of each equipment entry. Null when the grant failed or no reply came.
    /// </summary>
    private async Task<Dictionary<int, List<long>>?> KitAsync(IEnumerable<(int Item, long Count)> items, CancellationToken ct)
    {
        var bot = _bot ?? throw new IOException("not connected");
        using var reply = bot.Inbox.Expect(e => e.IsGame(GmCommands.BroadcastNotify)
            && BroadCastNotify.Parser.ParseFrom(e.Body).Content.StartsWith("kit", StringComparison.Ordinal));
        if (!await ctx.GmAsync(entry.Account, "!kit " + string.Join(' ', items.Select(i => $"{i.Item}:{i.Count}")), ct, ownRole: true))
            return null;
        string text;
        try { text = BroadCastNotify.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body).Content; }
        catch (TimeoutException) { return null; }
        var bag = new Dictionary<int, List<long>>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            if (part.Split('=') is [var item, var guids] && int.TryParse(item, out int id))
                bag[id] = [.. guids.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse)];
        return bag;
    }
}
