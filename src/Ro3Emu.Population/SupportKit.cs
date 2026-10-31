using System.Collections.Concurrent;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Population;

/// <summary>How a support skill is cast: around the caster (SkillRelease FixCircle, the party in its radius), at the
/// host, at the host (else the caster) when hurt, at a dead member, around the caster when the host or the caster in
/// its radius is hurt (PartyHeal), or on the host's ground when it is hurt (GroundHeal).</summary>
public enum SupportUse { Area, Ally, Heal, Revive, PartyHeal, GroundHeal }

/// <summary>A support skill a fake player keeps on its party host: a SkillConfig group, the level learned, how it is used.</summary>
public sealed record SupportSkill(int Group, int Level, SupportUse Use);

/// <summary>How an attack skill is aimed: at the monster (SkillRelease Lock), around the caster (FixCircle with an area
/// _iRangeType) or at the monster's ground (DragCircle, a target position).</summary>
public enum AttackAim { Target, Self, Ground }

/// <summary>A damaging skill a fake player uses on monsters: a SkillConfig group, the level learned, how it is aimed,
/// whether it is magic (_iDmgType 2).</summary>
public sealed record AttackSkill(int Group, int Level, AttackAim Aim, bool Magic = false);

/// <summary>
/// The support skills fake players cast for a real player they party with (a population choice: the original server had
/// no such players). Each is a SkillConfig group of the client's own support classes, kept only when the job's
/// SkillTreeConfig chain teaches it, and learned with its _kPreSkillRequired through the client's own 31151 request.
/// </summary>
public sealed class SupportKit(GameData data)
{
    /// <summary>In casting order (the first found is learned first, when skill points run short).</summary>
    private static readonly (int Group, SupportUse Use)[] Skills =
    [
        (14303, SupportUse.PartyHeal),  // Coluseo Heal (Archbishop): _iRelease 2 round the caster, 23 m, 5 targets
        (14115, SupportUse.GroundHeal), // Sanctuary (Priest): _iRelease 7 on a ground point, 7 m, 4 targets
        (14007, SupportUse.Heal),   // Heal (Acolyte)
        (14301, SupportUse.Heal),   // Highness Heal (Archbishop)
        (11405, SupportUse.Heal),   // Heal (Crusader)
        (14112, SupportUse.Revive), // Resurrection (Priest)
        (14001, SupportUse.Area),   // Blessing
        (14010, SupportUse.Area),   // Increase AGI
        (14015, SupportUse.Area),   // Angelus
        (14105, SupportUse.Area),   // Magnificat (Priest)
        (14106, SupportUse.Area),   // Gloria
        (14119, SupportUse.Area),   // Impositio Manus
        (14123, SupportUse.Area),   // Suffragium
        (14117, SupportUse.Ally),   // Kyrie Eleison
        (14201, SupportUse.Area),   // Assumptio (High Priest)
        (14304, SupportUse.Area),   // Sacred Lily (Archbishop)
        (16008, SupportUse.Area),   // Crazy Uproar (Merchant)
        (16105, SupportUse.Area),   // Adrenaline Rush (Blacksmith)
        (16106, SupportUse.Area),   // Power Thrust
        (13201, SupportUse.Area),   // Wind Walker (Sniper)
        (11414, SupportUse.Area),   // Providence (Crusader)
        (11413, SupportUse.Ally),   // Devotion
        (11503, SupportUse.Area),   // Battle Chant (Paladin)
    ];

    private readonly ConcurrentDictionary<long, HashSet<long>> _trees = new();
    private Dictionary<(long Group, long Level), SkillConfig>? _rows;

    /// <summary>The support skills a job can learn, at their learnable cap (SkillConfig _iMaxLevel).</summary>
    public IReadOnlyList<SupportSkill> For(long job) =>
        [.. Skills.Where(s => Tree(job).Contains(s.Group) && Row(s.Group, 1) is { MaxLevel: > 0 })
            .Select(s => new SupportSkill(s.Group, (int)Row(s.Group, 1)!.Value.MaxLevel, s.Use))];

    /// <summary>
    /// The damaging skills a job can aim at monsters, at their learnable cap: active (_iBasicType 1) job skills
    /// (_iSystemType 1, not the normal attack) that deal damage (_iDmgType 1 physical, 2 magic), aimed at a target
    /// (_iRelease 1), around the caster at an area (_iRelease 2, _iRangeType 2) or at a ground point (_iRelease 8). All
    /// of them, the latest job's first (its skills hit hardest, and are learned first when points run short); never one
    /// of the support skills.
    /// </summary>
    public IReadOnlyList<AttackSkill> Attacks(long job) =>
        [.. ByTier(job, r => r is { DmgType: 1 or 2 } && (r.Release is 1 or 8 || r.Release == 2 && r.RangeType == 2))
            .Select(r => new AttackSkill((int)r.SkillId, (int)r.MaxLevel,
                r.Release switch { 1 => AttackAim.Target, 8 => AttackAim.Ground, _ => AttackAim.Self }, r.DmgType == 2))];

    /// <summary>
    /// The job's own buffs: active job skills without damage cast on the caster (_iRelease 2, _iRangeType 0: Aura Blade,
    /// True Sight, Maximize Power, Mystical Amplification...), the latest job's first. Not the support skills (cast for
    /// the host), nor those that would take a fake player out of the fight (<see cref="NotBuffs"/>).
    /// </summary>
    public IReadOnlyList<int> Buffs(long job) =>
        [.. ByTier(job, r => r is { DmgType: 0, Release: 2, RangeType: 0 } && !NotBuffs.Contains(r.SkillId)).Select(r => (int)r.SkillId)];

    /// <summary>Self-cast skills that are not buffs to keep up: stealth (Hiding, Disguise, Hide-and-Seek), sitting (Tension
    /// Relax), Berserk (no other skills while it lasts), jumps back to a shadow, reveals (Sight, Ruwach), a spellbook's
    /// reading and the tree spirit's Respawn.</summary>
    private static readonly HashSet<long> NotBuffs =
        [15003, 15112, 151001, 158009, 11207, 158001, 11214, 15330, 115332, 162022, 12002, 14005, 158014, 12317, 12319, 14308, 14395];

    /// <summary>Learnable active job skills (not the normal attack, not a support skill) passing <paramref name="kind"/>,
    /// the job's latest tier first.</summary>
    private IEnumerable<SkillConfig> ByTier(long job, Func<SkillConfig, bool> kind)
    {
        var chain = new List<long>();
        for (long c = job; c > 0 && JobConfig.Get(data, c) is { } row; c = row.ParentId) chain.Add(c);
        return Tree(job).Select(g => Row(g, 1)).OfType<SkillConfig>()
            .Where(r => r is { BasicType: 1, SystemType: 1, IsNormalAtk: 0, MaxLevel: > 0 } && kind(r) && !IsTaunt(r)
                && Skills.All(s => s.Group != r.SkillId)) // Heal is magic too, but for the host
            .OrderBy(r => chain.IndexOf(r.Job) is >= 0 and var tier ? tier : chain.Count).ThenByDescending(r => r.SkillId);
    }

    /// <summary>An attack's strength at a level: its damage multiplier (SkillConfig._kDamageParam1[0], the ratio of the
    /// attack the server applies, CombatService.Effects), 0 when it has none.</summary>
    public long Power(int group, int level) => Row(group, level)?.DamageParam1 is [var multiplier, ..] ? multiplier : 0;

    /// <summary>How far a skill reaches in metres (SkillConfig._iDistanceMax in cm), 0 when unknown.</summary>
    public float Reach(long skillId) => SkillConfig.Get(data, skillId)?.DistanceMax is > 0 and var cm ? cm / 100f : 0;

    /// <summary>A skill's area radius in metres (SkillConfig._kRange[0] in cm, as BossSkillSets reads a boss skill's), 0
    /// when it names none.</summary>
    public float AreaRadius(long skillId) => SkillConfig.Get(data, skillId)?.Range is [>= 100 and var cm, ..] ? cm / 100f : 0;

    /// <summary>A skill that strikes an area, not one target: _iRelease other than 1 (single target) with an area radius.
    /// A boss's swings are release 1 without one (in-game 30.10: every boss area cast was release 2 or 4, 10-20 m).</summary>
    public bool StrikesArea(long skillId) => SkillConfig.Get(data, skillId) is { Release: not 1 } && AreaRadius(skillId) > 0;

    /// <summary>A class that fights with spells: most of its attack skills are magic (a Wizard has one physical Spell
    /// Smash among its spells). It never swings a normal attack.</summary>
    public static bool Casts(IReadOnlyCollection<AttackSkill> attacks) => attacks.Count(a => a.Magic) * 2 > attacks.Count;

    /// <summary>
    /// The Special Class Items a job may lease (CareerSpecialService 32721): CartRemouldTable rows of the pendant (_iType 1:
    /// cart, falcon) and mount (_iType 2: Peco Peco, dragon, gryphon, wolf, Madogear) slots whose _kProfessionID holds the
    /// job, without a _iTaskId quest. Ordered by id (the plain Peco Peco before its armored one).
    /// </summary>
    public IReadOnlyList<CartRemouldTable> ClassItems(long job) =>
        [.. CartRemouldTable.All(data).Where(r => r.Type is 1 or 2 && r.TaskId == 0 && r.ProfessionId.Contains(job)).OrderBy(r => r.Id)];

    /// <summary>A class item's _kUnlockSkillID as a skill group and level to learn, or null when it needs none.</summary>
    public (int Group, int Level)? UnlockSkill(CartRemouldTable item) =>
        item.UnlockSkillId > 0 && SkillConfig.Get(data, item.UnlockSkillId) is { } skill ? ((int)skill.SkillId, (int)skill.Level) : null;

    /// <summary>A monster to keep away from: NPCConfig _iNPCSubType enum_MonsterType MT_MINI (3), MT_MVP (4), MT_BOSS (6).</summary>
    public bool IsBoss(long npcId) => NPCConfig.Get(data, npcId)?.NPCSubType is 3 or 4 or 6;

    /// <summary>The SkillConfig row of a group at a level (_iMode 0), as SkillService.RealId finds it.</summary>
    public SkillConfig? Row(long group, long level)
    {
        _rows ??= SkillConfig.All(data).Where(r => r.Mode == 0).GroupBy(r => (r.SkillId, r.Level))
            .ToDictionary(g => g.Key, g => g.First());
        return _rows.TryGetValue((group, level), out var row) ? row : null;
    }

    /// <summary>
    /// The levels to ask for so that <paramref name="skill"/> at <paramref name="level"/> may be learned: it and every
    /// _kPreSkillRequired of each level up to it, recursively, at the highest level asked; null when a requirement is
    /// outside the job's tree, or the skill is barred by one <paramref name="learned"/> (or asked with it): a level -1
    /// requirement is a branch the skill excludes (a Mechanic's Madogear License 16309 line, Arm Cannon 16311 and
    /// Knuckle Boost 16313, against its Axe line 16351).
    /// </summary>
    public Dictionary<int, int>? Requirements(long job, int skill, int level, IReadOnlyDictionary<int, int>? learned = null)
    {
        var wanted = new Dictionary<int, int>();
        var barred = new HashSet<int>();
        var tree = Tree(job);
        bool Add(int group, int at)
        {
            if (!tree.Contains(group)) return false;
            if (wanted.GetValueOrDefault(group) >= at) return true;
            wanted[group] = at;
            for (int l = 1; l <= at; l++)
                foreach (var pair in Row(group, l)?.PreSkillRequired ?? [])
                {
                    if (pair is not [var g, var need]) continue;
                    if (need == -1) barred.Add((int)g);
                    else if (need > 0 && !Add((int)g, (int)need)) return false;
                }
            return true;
        }
        if (!Add(skill, level)) return null;
        // An excluded branch neither learned already nor asked with it; nor a learned skill that excludes one asked.
        bool Has(int group) => wanted.ContainsKey(group) || learned?.GetValueOrDefault(group) > 0;
        if (barred.Any(Has)) return null;
        foreach (var (group, at) in learned ?? new Dictionary<int, int>())
            for (int l = 1; l <= at; l++)
                if ((Row(group, l)?.PreSkillRequired ?? []).Any(p => p is [var g, -1] && wanted.ContainsKey((int)g))) return null;
        return wanted;
    }

    /// <summary>A frontline class: JobConfig _iDungeonJobPositionID holds 101, DungeonJobPositionConfig's "DMG Taken" role
    /// (Swordman, Crusader, Paladin, Royal Guard).</summary>
    public bool IsTank(long job) => JobConfig.Get(data, job)?.DungeonJobPositionId.Contains(TankPosition) == true;
    private const long TankPosition = 101;

    /// <summary>The job's skills that draw a monster's attacks (SkillConfig _kScriptLogic Provoke or Taunt, CombatService's
    /// MonsterService.Provoke), at their learnable cap.</summary>
    public IReadOnlyList<AttackSkill> Taunts(long job) =>
        [.. Tree(job).Select(g => Row(g, 1)).OfType<SkillConfig>()
            .Where(r => r is { BasicType: 1, MaxLevel: > 0 } && IsTaunt(r))
            .Select(r => new AttackSkill((int)r.SkillId, (int)r.MaxLevel, r.Release == 1 ? AttackAim.Target : AttackAim.Self))];

    private static bool IsTaunt(SkillConfig r) => r.ScriptLogic is "Provoke" or "Taunt";

    /// <summary>
    /// Whether a skill may be cast as the client allows it (SkillConfig _iSkillCondition, LC_ConditionList; the server
    /// does not check): its weapon (2001 CheckEquipType01, the held weapon's ItemConfig _iSubType), riding a mount (2003
    /// CheckMountType01) or not riding it (2004 CheckMountType02). A node's same-type conditions are any of them
    /// (LC_ConditionManager._mergeOrConditionSameType), the types all of them (enum_NodeType AND); other types pass.
    /// A riding Ranger's normal attack is Warg Strike and its bow skills wait, a Mechanic fights with Madogear skills only
    /// in its Madogear.
    /// </summary>
    public bool Usable(long skillId, long ridden, long weaponSubType)
    {
        if (SkillConfig.Get(data, skillId) is not { SkillCondition: > 0 } row || LCConditionList.Get(data, row.SkillCondition) is not { } node
            || node.SubConditionList is not { ValueKind: System.Text.Json.JsonValueKind.Array } subs) return true;
        var byType = new Dictionary<long, bool>();
        foreach (var sub in subs.EnumerateArray())
        {
            if (!sub.TryGetProperty("m_kData", out var d) || d.ValueKind != System.Text.Json.JsonValueKind.Array || d.GetArrayLength() < 2) continue;
            long type = d[0].GetInt64(), value = d[1].GetInt64();
            bool? holds = type switch
            {
                EquipType => weaponSubType == value,
                Riding => ridden == value,
                NotRiding => ridden != value,
                _ => null,
            };
            if (holds is not { } ok) continue;
            if (sub.TryGetProperty("m_bInverse", out var inverse) && inverse.ValueKind == System.Text.Json.JsonValueKind.True) ok = !ok;
            byType[type] = byType.GetValueOrDefault(type) || ok;
        }
        return byType.Values.All(v => v);
    }

    private const long EquipType = 2001, Riding = 2003, NotRiding = 2004;

    /// <summary>
    /// The levels of a 31151 request that learns <paramref name="skill"/> at <paramref name="level"/> on top of what is
    /// <paramref name="learned"/>: both, with its requirements, each at least its _iInitialLevel; null when it cannot be.
    /// </summary>
    public Dictionary<int, int>? Ask(long job, int skill, int level, IReadOnlyDictionary<int, int> learned)
    {
        if (Requirements(job, skill, level, learned) is not { } needed) return null;
        var ask = new Dictionary<int, int>(learned);
        foreach (var (group, at) in needed)
            ask[group] = Math.Max(ask.GetValueOrDefault(group), Math.Max(at, (int)(Row(group, 1)?.InitialLevel ?? 0)));
        return ask;
    }

    /// <summary>A job's learnable groups: SkillTreeConfig _iX1.._iX7 along the JobConfig _iParentId chain, each row's group
    /// replaced by the job's _kSkillReplace (as SkillService.BuildTree).</summary>
    private HashSet<long> Tree(long job) => _trees.GetOrAdd(job, j =>
    {
        var replace = (JobConfig.Get(data, j)?.SkillReplace ?? []).Where(p => p.Length >= 2).ToDictionary(p => p[0], p => p[1]);
        var groups = new HashSet<long>();
        for (long c = j; c > 0 && JobConfig.Get(data, c) is { } row; c = row.ParentId)
            foreach (var tree in SkillTreeConfig.All(data).Where(t => t.Job == c))
                foreach (long g in tree.Groups.Where(g => g > 0)) groups.Add(replace.GetValueOrDefault(g, g));
        return groups;
    });
}
