using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Population;

/// <summary>A town where fake players live: the places they walk between and the fields its portals lead to.</summary>
public sealed record Town(long SceneEntry, string Name, IReadOnlyList<MapPoint> Spots, IReadOnlyList<long> Fields);

/// <summary>
/// Where fake players go, from the exported game data: towns are the Kafra destinations (MapLinkData.Capra) of
/// Prontera's MapConfig._iSceneType (inferred: a "town" type is not named in the client); spots are the standable
/// points next to the town's NPCs, Kafras and portals; fields are the maps the town's portals lead to that spawn monsters.
/// </summary>
public sealed class WorldPlan(GameData data, WorldData world)
{
    public const long Prontera = 10100;

    /// <summary>A found town needs this many standable spots (inferred: drops story scenes like Garden of Time); a town
    /// asked for by id needs one.</summary>
    private const int MinSpots = 5;

    public IReadOnlyList<Town> Towns(IReadOnlyList<long> wanted)
    {
        var ids = wanted.Count > 0 ? wanted : TownIds();
        int minSpots = wanted.Count > 0 ? 1 : MinSpots;
        return [.. ids.Select(Build).OfType<Town>().Where(t => t.Spots.Count >= minSpots)];
    }

    private List<long> TownIds()
    {
        long? townType = MapConfig.Get(data, Prontera)?.SceneType;
        var towns = MapConfig.All(data)
            .Where(m => world.IsKafraDestination(m.Id) && (townType == null || m.SceneType == townType))
            .Select(m => m.Id).ToList();
        return towns.Count > 0 ? towns : [.. MapConfig.All(data).Select(m => m.Id).Take(1)];
    }

    private Town? Build(long scene)
    {
        if (world.Map(scene) is not { } map) return null;
        var spots = map.Npcs.Select(n => map.Standable(new MapPoint(n.X + 1.5f, n.Y, n.Z + 1.5f, n.Face), radius: 4))
            .Where(p => map.IsWalkable(p.X, p.Y, p.Z)).ToList();
        if (map.Born is { } born) spots.Add(map.Standable(born));
        var fields = map.Npcs.Where(n => n.Kind == WorldNpcKind.Portal && n.Target is { } t && t.Map != scene)
            .Select(n => n.Target!.Value.Map).Distinct()
            .Where(f => world.Map(f) is { SpawnGroups.Count: > 0 }).ToList();
        return spots.Count == 0 ? null : new Town(scene, data.MapName(scene), spots, fields);
    }

    /// <summary>
    /// A job for a fresh role: any JobConfig row below the root (_iParentId set) the sex may take (_iGenderLimit 0 or
    /// the sex), with its rank; set by GM !job, which skips the in-game advancement rules.
    /// </summary>
    public (long Job, long Rank)? PickJob(Random random, int sex)
    {
        var jobs = JobConfig.All(data).Where(j => j.ParentId != 0 && (j.GenderLimit == 0 || j.GenderLimit == sex)).ToList();
        if (jobs.Count == 0) return null;
        var job = jobs[random.Next(jobs.Count)];
        return (job.Id, job.JobRank);
    }

    /// <summary>The highest base level the game allows: BaseLvLimitConfig._iBaseLimit at its largest
    /// (CharacterService.MaxLevel reads the same), 99 without the table.</summary>
    public int MaxLevel => _maxLevel ??= data.Table("BaseLvLimitConfig").Values
        .Select(r => (int)GameData.Int(r, "_iBaseLimit")).DefaultIfEmpty(0).Max() is > 0 and var cap ? cap : 99;
    private int? _maxLevel;

    /// <summary>
    /// The next job of <paramref name="job"/>'s line for this sex (a JobConfig row whose _iParentId is it) and the base
    /// and job levels it needs (PublicParamConfig.TransformJobLevelLimit by the next job's _iJobRank); null at a line's end.
    /// </summary>
    public (long Job, int Base, int JobLevel)? NextJob(long job, int sex, Random random)
    {
        var next = JobConfig.All(data).Where(j => j.ParentId == job && j.ParentId != 0 && (j.GenderLimit == 0 || j.GenderLimit == sex)).ToList();
        if (next.Count == 0) return null;
        var pick = next[random.Next(next.Count)];
        if (!data.Table("PublicParamConfig").TryGetValue("TransformJobLevelLimit", out var limits)
            || limits.ValueKind != System.Text.Json.JsonValueKind.Array) return (pick.Id, 0, 0);
        foreach (var limit in limits.EnumerateArray())
            if (GameData.Int(limit, "Rank") == pick.JobRank)
                return (pick.Id, (int)GameData.Int(limit, "BaseLevelLimit"), (int)GameData.Int(limit, "JobLevelLimit"));
        return (pick.Id, 0, 0);
    }

    /// <summary>The job's normal attack (JobConfig._kSkillNormalID, first entry).</summary>
    public long NormalAttack(long job) => JobConfig.Get(data, job)?.SkillNormalId is [var first, ..] ? first : 0;
}
