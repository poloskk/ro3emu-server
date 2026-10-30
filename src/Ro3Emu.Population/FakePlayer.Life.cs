using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

public sealed partial class FakePlayer
{
    /// <summary>The novice's normal attack, used when JobConfig names none (the load test's fighter skill).</summary>
    private const long FallbackAttack = 1000001;
    private DateTime _lastChat = DateTime.UtcNow;

    private Town Home => ctx.Towns.TryGetValue(entry.Town, out var town) ? town : ctx.Towns.Values.First();

    private async Task LiveAsync(CancellationToken ct)
    {
        if (!entry.Prepared) await PrepareAsync(ct);
        StartGrowth();
        _statsAfter = DateTime.UtcNow + TimeSpan.FromSeconds(60 + _random.Next(540)); // spread the realm's logins
        await GoHomeAsync(ct);
        // Beyond --active, townies and hunters stand about instead (PopulationOptions.Active).
        var persona = !active && entry.Persona is Persona.Townie or Persona.Hunter ? Persona.Idler : entry.Persona;
        PopulationContext.Log($"{entry.Name}: online in {Home.Name} as {persona}");
        while (!ct.IsCancellationRequested && Online)
        {
            await GuildStepAsync(ct);
            await GrowAsync(ct);
            await FixJobAsync(ct);
            await StatsStepAsync(ct);
            await ClassItemsStepAsync(ct);
            await GearStepAsync(ct);
            await BoostStepAsync(ct);
            if (await PartyStepAsync(ct)) continue;
            if (await SiegeAsync(ct)) { await GoHomeAsync(ct); continue; }
            switch (persona)
            {
                case Persona.Hunter when Home.Fields.Count > 0: await HuntTripAsync(ct); break;
                case Persona.Idler: await IdleAsync(ct); break;
                case Persona.Vendor: await VendAsync(ct); break;
                default: await StrollAsync(ct); break;
            }
        }
    }

    /// <summary>
    /// A fresh role gets a job and a level so the town does not look like a novice camp: GM !job and !lvl (inferred
    /// population choice, not original content: the original server had only real players).
    /// </summary>
    private async Task PrepareAsync(CancellationToken ct)
    {
        if (ctx.Plan.PickJob(_random, entry.Sex) is { } job)
        {
            if (!await ctx.GmAsync(entry.Account, $"!job {job.Job}", ct)) return; // the next login tries again
            _job = job.Job;
            int baseLevel = Math.Clamp(10 + (int)job.Rank * 20 + _random.Next(25), 10, 90);
            if (!await ctx.GmAsync(entry.Account, $"!lvl {baseLevel} {Math.Max(1, baseLevel / 2)}", ct, ownRole: true)) return;
            entry.Level = baseLevel;
        }
        entry.Prepared = true;
        ctx.Roster.Save();
    }

    private async Task GoHomeAsync(CancellationToken ct)
    {
        if (_scene != Home.SceneEntry) await TravelAsync(Home.SceneEntry, ct);
        else await _bot!.JoinChatAsync(MapChat);
    }

    /// <summary>Townie: walk to another spot of the town (or wander off between them), look around, maybe say something.</summary>
    private async Task StrollAsync(CancellationToken ct)
    {
        var spot = Home.Spots[_random.Next(Home.Spots.Count)];
        var target = _random.NextDouble() < 0.35 ? Near(spot, 6, 25) ?? Jitter(spot) : Jitter(spot);
        if (!await GoAsync(target, ct)) await ShuffleAsync(6, ct);
        await FaceAsync(_random.Next(360), ct);
        await PauseAsync(5, 40, ct);
        if (_random.NextDouble() < 0.4) await ShuffleAsync(4, ct);
    }

    /// <summary>Idler: stands at one spot for minutes (the AFK merchant look), now and then a step.</summary>
    private async Task IdleAsync(CancellationToken ct)
    {
        var spot = Home.Spots[_random.Next(Home.Spots.Count)];
        await GoAsync(Jitter(spot), ct);
        for (int i = _random.Next(3, 10); i > 0 && !ct.IsCancellationRequested && !InParty; i--)
        {
            await PauseAsync(20, 60, ct);
            if (_random.NextDouble() < 0.3) await FaceAsync(_random.Next(360), ct);
        }
    }

    /// <summary>Hunter: a field next to home for a while, then back to town for a break.</summary>
    private async Task HuntTripAsync(CancellationToken ct)
    {
        await TravelAsync(Home.Fields[_random.Next(Home.Fields.Count)], ct);
        var until = DateTime.UtcNow.AddMinutes(_random.Next(8, 20));
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested && !InParty) await FightOnceAsync(ct);
        if (InParty) return; // the party step takes the bot where its host is
        await TravelAsync(Home.SceneEntry, ct);
        // Life and SP come back on their own during the rest (no GM heal: a GM line holds the whole realm).
        var rest = DateTime.UtcNow.AddMinutes(_random.Next(2, 5));
        while (DateTime.UtcNow < rest && !ct.IsCancellationRequested && !InParty) await StrollAsync(ct);
    }

    /// <summary>The nearest monster in view: walk up to it and swing (31029 lock, 31102 cast, as the load test's fighter).</summary>
    private async Task FightOnceAsync(CancellationToken ct)
    {
        if (await EvadeAsync(ct)) return;
        // A hunter leaves bosses to parties (FakePlayer.Evade).
        var target = _view.Monsters().Where(m => !ctx.Support.IsBoss((long)m.EntityTemplateID))
            .OrderBy(m => Distance(m.MovementInfo.Pos)).FirstOrDefault();
        if (target == null)
        {
            await ShuffleAsync(15, ct);
            await PauseAsync(1, 4, ct);
            return;
        }
        var at = target.MovementInfo.Pos;
        if (Distance(at) > 3f && !await GoAsync(new MapPoint(at.X, at.Y, at.Z, 0), ct, short_: 2f))
        {
            await ShuffleAsync(10, ct);
            return;
        }
        long skill = ctx.Plan.NormalAttack(_job) is > 0 and var s ? s : FallbackAttack;
        await SendAsync(CombatService.LockEnemy, new LockEnemyReq { EnemyId = (long)target.Uuid }, ct);
        for (int swing = 0; swing < 6 && _view.Monsters().Any(m => m.Uuid == target.Uuid); swing++)
        {
            await SendAsync(CombatService.UseSkill, new CastSkillReq { SkillId = (int)skill, TargetId = (long)target.Uuid }, ct);
            await Task.Delay(TimeSpan.FromSeconds(1.2 + _random.NextDouble() * 0.6), ct);
        }
        await ChatMaybeAsync(inTown: false, ct);
    }

    /// <summary>Waits a random time, chatting at the configured rate meanwhile.</summary>
    private async Task PauseAsync(int minSeconds, int maxSeconds, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(_random.Next(minSeconds, maxSeconds + 1)), ct);
        await ChatMaybeAsync(inTown: _scene == Home.SceneEntry, ct);
    }

    private async Task ChatMaybeAsync(bool inTown, CancellationToken ct)
    {
        double minutes = (DateTime.UtcNow - _lastChat).TotalMinutes;
        if (_random.NextDouble() >= 1 - Math.Exp(-ctx.Options.ChatPerMinute * minutes)) return;
        _lastChat = DateTime.UtcNow;
        string line = Phrases.Pick(_random, inTown, Home.Name, entry.Name);
        await _sender!.SendAsync(() => _bot!.SendChatAsync(MapChat, 2, line, channelIndex: 4), ct);
    }

    /// <summary>Somewhere around a spot (1-6 m off), so a crowd at one NPC spreads out instead of standing in one place.</summary>
    private MapPoint Jitter(MapPoint spot) => Near(spot, 1, 6) ?? spot;

    /// <summary>A walkable point <paramref name="min"/>-<paramref name="max"/> m from <paramref name="around"/>, or null.</summary>
    private MapPoint? Near(MapPoint around, float min, float max)
    {
        if (Map is not { } map) return null;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            float a = (float)(_random.NextDouble() * MathF.Tau), r = min + (float)_random.NextDouble() * (max - min);
            float x = around.X + r * MathF.Cos(a), z = around.Z + r * MathF.Sin(a);
            if (map.Nav is { } nav && (nav.WalkHeight(x, z, around.Y) is not { } y || !nav.IsWalkable(x, y, z))) continue;
            return map.Grounded(new MapPoint(x, around.Y, z, 0));
        }
        return null;
    }

    private float Distance(Vec3 p)
    {
        float dx = p.X - _pos.X, dz = p.Z - _pos.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
