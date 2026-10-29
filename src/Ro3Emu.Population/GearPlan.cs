using System.Collections.Concurrent;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Population;

/// <summary>A piece a fake player wears: an ItemConfig equipment row, the equipment grid it goes to (31400 pos), and
/// whether it is appraised (31401) before it can be worn (EquipConfig _iIfAppraise).</summary>
public sealed record GearPiece(int Item, int Grid, bool Appraise = false);

/// <summary>A talent node a fake player raises: a SeasonTalentConfig row, its level group and cap.</summary>
public sealed record TalentNode(long Id, long Group, int MaxLevel);

/// <summary>
/// What a fake player wears and grows into (a population choice, inferred: the original server had only real players).
/// Its gear is the client's own: ItemConfig equipment (_iType 5) its job may wear (_kJobNeed, 0 = anyone) at its level
/// (_iLevelNeed), of its quality or below, the highest level then quality per part (gold and red pieces from level 20 on
/// need appraisal, EquipConfig _iIfAppraise). Strengthening (StrengthenTable) and talents (SeasonTalentConfig / SeasonTalentEffectConfig) are
/// paid as a player pays them, at their configured costs.
/// </summary>
public sealed class GearPlan(GameData data)
{
    /// <summary>ItemConfig _iEquipPart to the grid it is worn in (CharacterService.Equipment): a two-handed (1) or
    /// one-handed (2) weapon in grid 1, the off-hand (3) in 2, armour 4..6 in 3..5, accessories (7) in 6 and 7.</summary>
    private static readonly Dictionary<long, int[]> Grids = new()
    {
        [1] = [1], [2] = [1], [3] = [2], [4] = [3], [5] = [4], [6] = [5], [7] = [6, 7],
    };
    private const long EquipmentType = 5, AnyJob = 0, Unreachable = 999; // _iLevelNeed 999: not a wearable tier

    private List<ItemConfig>? _gear;
    private Dictionary<(long Grid, long Level), StrengthenTable>? _strengthen;
    private Dictionary<(long Group, long Level), List<SeasonTalentEffectConfig>>? _effects;
    private readonly ConcurrentDictionary<long, IReadOnlyList<TalentNode>> _talents = new();

    /// <summary>
    /// The set a job wears at a level: per part the best piece of at most <paramref name="quality"/>; the weapon a
    /// two-handed one when <paramref name="twoHanded"/> (and the job has one), else a one-handed one with an off-hand.
    /// </summary>
    public IReadOnlyList<GearPiece> For(long job, int level, int quality, bool twoHanded)
    {
        ItemConfig? Best(long part) => Gear().Where(g => g.EquipPart == part && g.LevelNeed <= level && g.Quality <= quality
                && (g.JobNeed.Length == 0 || g.JobNeed.Contains(AnyJob) || g.JobNeed.Contains(job)))
            .OrderByDescending(g => g.LevelNeed).ThenByDescending(g => g.Quality).ThenBy(g => g.Id)
            .Cast<ItemConfig?>().FirstOrDefault();
        var pieces = new List<GearPiece>();
        if ((twoHanded ? Best(1) ?? Best(2) : Best(2) ?? Best(1)) is { } held)
        {
            pieces.Add(new((int)held.Id, Grids[held.EquipPart][0], Appraised(held)));
            if (held.EquipPart == 2 && Best(3) is { } off) pieces.Add(new((int)off.Id, Grids[3][0], Appraised(off)));
        }
        foreach (long part in new long[] { 4, 5, 6, 7 })
            if (Best(part) is { } piece)
                pieces.AddRange(Grids[part].Select(grid => new GearPiece((int)piece.Id, grid, Appraised(piece))));
        return pieces;
    }

    /// <summary>StrengthenTable's cost of raising a grid from <paramref name="from"/> by <paramref name="steps"/>
    /// levels (each level's _kDeplete), or null when a level is missing or needs more than <paramref name="baseLevel"/>
    /// (_iLev): EquipmentService refuses it.</summary>
    public List<(int Item, long Count)>? StrengthenCost(int grid, int from, int steps, int baseLevel)
    {
        _strengthen ??= StrengthenTable.All(data).GroupBy(r => (r.EquipType, r.StrengthenInterval)).ToDictionary(g => g.Key, g => g.First());
        var cost = new List<(int, long)>();
        for (int level = from + 1; level <= from + steps; level++)
        {
            if (!_strengthen.TryGetValue((grid, level), out var row) || row.Lev > baseLevel) return null;
            cost.AddRange(row.Deplete.Where(p => p.Length >= 2).Select(p => ((int)p[0], p[1])));
        }
        return cost;
    }

    /// <summary>
    /// The talent nodes a job can raise, in the order a player opens them: each tree's start points, then the nodes
    /// they lead to (_kAfterids). Only plain stat nodes (SeasonTalentService.Supported: attributes and costs, no special
    /// effect, profession or skill points).
    /// </summary>
    public IReadOnlyList<TalentNode> Talents(long job) => _talents.GetOrAdd(job, j =>
    {
        var nodes = SeasonTalentConfig.All(data).ToDictionary(n => n.Id);
        var order = new List<TalentNode>();
        var seen = new HashSet<long>();
        var queue = new Queue<long>(nodes.Values.Where(n => n.IsStartPoint == 1).OrderBy(n => n.TalentTreeId).ThenBy(n => n.Id).Select(n => n.Id));
        while (queue.TryDequeue(out long id))
        {
            if (!seen.Add(id) || !nodes.TryGetValue(id, out var node)) continue;
            if (node.MaxLevel > 0 && Effect(node.LevelGroupId, 1, j) is { } effect && Supported(effect))
                order.Add(new(node.Id, node.LevelGroupId, (int)node.MaxLevel));
            foreach (long next in node.Afterids) queue.Enqueue(next);
        }
        return order;
    });

    /// <summary>A talent level's _kCosts (SeasonTalentEffectConfig), or null.</summary>
    public List<(int Item, long Count)>? TalentCost(TalentNode node, int level, long job) =>
        Effect(node.Group, level, job) is { } effect && Supported(effect)
            ? [.. effect.Costs.Where(c => c.Length >= 2).Select(c => ((int)c[0], c[1]))] : null;

    /// <summary>The effect row of a group's level for a job: its own (_iJobId), else the generic one (SeasonTalentService.Effect).</summary>
    private SeasonTalentEffectConfig? Effect(long group, long level, long job)
    {
        _effects ??= SeasonTalentEffectConfig.All(data).GroupBy(e => (e.Group, e.Level)).ToDictionary(g => g.Key, g => g.ToList());
        if (!_effects.TryGetValue((group, level), out var rows)) return null;
        return rows.Cast<SeasonTalentEffectConfig?>().FirstOrDefault(r => r!.Value.JobId.Contains(job))
            ?? rows.Cast<SeasonTalentEffectConfig?>().FirstOrDefault(r => r!.Value.JobId.Length == 0 || r.Value.JobId.Contains(AnyJob));
    }

    /// <summary>
    /// The HP potion a fake player carries at a level: of the potions (ItemConfig _iUseEffect [[100, 1, flat, percent]]:
    /// RecoverFixed HP, CharacterService.ApplyUseEffectsAsync) it may use, the highest level, then quality, then heal.
    /// </summary>
    public int Potion(int level) => (int)(_potions ??= [.. ItemConfig.All(data).Where(i => i.IsUse == 1 && HealsHp(i))])
        .Where(i => i.LevelNeed <= level).OrderByDescending(i => i.LevelNeed).ThenByDescending(i => i.Quality).ThenBy(i => i.Id)
        .Select(i => i.Id).FirstOrDefault();

    private List<ItemConfig>? _potions;
    private const string RecoverFixed = "100", Hp = "1";

    private static bool HealsHp(ItemConfig item) => item.UseEffect is { ValueKind: System.Text.Json.JsonValueKind.Array } effects
        && effects.GetArrayLength() == 1 && effects[0] is { ValueKind: System.Text.Json.JsonValueKind.Array } e && e.GetArrayLength() >= 4
        && e[0].ToString() == RecoverFixed && e[1].ToString() == Hp;

    private static bool Supported(SeasonTalentEffectConfig e) => e.Attrs.Length > 0 && e.Costs.Length > 0
        && e.SpecialEffectList.Length == 0 && e.ProfessionId.Length == 0 && e.SkillPoint == 0 && e.SkillPointCosts == 0;

    private bool Appraised(ItemConfig item) => EquipConfig.Get(data, item.EquipId > 0 ? item.EquipId : item.Id) is { IfAppraise: 1 };

    private List<ItemConfig> Gear() => _gear ??= [.. ItemConfig.All(data).Where(g => g.Type == EquipmentType
        && Grids.ContainsKey(g.EquipPart) && g.LevelNeed < Unreachable)];
}
