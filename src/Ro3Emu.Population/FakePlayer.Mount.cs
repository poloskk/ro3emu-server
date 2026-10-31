using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A fake player of a class with Special Class Items (SupportKit.ClassItems) carries them as a player does: it learns
/// the item's skill (alone: the rest of its kit waits for a party), leases the item through the client's 32721 (a
/// Knight's Peco Peco, a Hunter's falcon, a Merchant's cart) and rides its battle mount (32745 tp OnBattle). The roster
/// remembers what was leased, so a login with the same items only mounts again (once a session: the server keeps the
/// rider, and neither death nor a map change takes it off). A population choice (inferred): the original server had
/// only real players.
/// </summary>
public sealed partial class FakePlayer
{
    private const long MountType = 2; // CartRemouldTable _iType of the mount slot
    private readonly Dictionary<long, long> _leased = [];
    private bool _rode, _dismounted;
    private DateTime _rideAfter;

    private async Task ClassItemsStepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _statsAfter || now < _rideAfter) return; // after the login spread, as the stats
        var items = ctx.Support.ClassItems(_job).Where(i => i.UnlockBaseLv <= entry.Level).ToList();
        string key = items.Count == 0 ? "" : $"{_job}:{string.Join(',', items.Select(i => i.Id))}";
        if (key != entry.ClassItems)
        {
            var (mount, all) = await LeaseClassItemsAsync(items, ct);
            entry.Mount = mount;
            // A refused lease (its skill not learned, points short) is tried again in a while, not at every step.
            if (all) entry.ClassItems = key;
            else _rideAfter = now + TimeSpan.FromMinutes(10 + _random.Next(10));
            ctx.Roster.Save();
            _rode = false;
        }
        if (entry.Mount == 0 || _rode) return;
        if (!Build.Rides)
        {
            // A build that fights on foot (a bow Ranger, its warg following): off the mount once a session, as the
            // server keeps a rider from the last one.
            if (_dismounted) return;
            await RequestAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OffBattle }, ct);
            _dismounted = true;
            return;
        }
        var req = new PlayerRiderMountReq { Tp = eRiderType.OnBattle };
        _rode = PlayerRiderMountResp.Parser.ParseFrom(await RequestAsync(MountService.Ride, req, ct)).Result == 0;
        if (!_rode) _rideAfter = now + TimeSpan.FromSeconds(60 + _random.Next(60));
    }

    /// <summary>One item per slot: of those the bot's level reaches, the latest (a Rune Knight's dragon over its Peco
    /// Peco), a random one of a tie (the plain or the armored Peco Peco). The mount leased (or 0), and whether every
    /// slot was.</summary>
    private async Task<(long Mount, bool All)> LeaseClassItemsAsync(IReadOnlyList<CartRemouldTable> items, CancellationToken ct)
    {
        // Only the items' own skills (a request or two): the whole kit waits for a party (LearnSkillsAsync), as 1500
        // logins learning ~60 skills each would flood the server.
        var learned = new Dictionary<int, int>();
        foreach (var item in items)
            if (ctx.Support.UnlockSkill(item) is var (group, level) && learned.GetValueOrDefault(group) < level)
                await LearnAsync(group, level, learned, ct);
        long mount = 0;
        bool all = true;
        foreach (var slot in items.GroupBy(i => i.Type))
        {
            var best = slot.Where(i => i.UnlockBaseLv == slot.Max(o => o.UnlockBaseLv)).ToList();
            var pick = _leased.TryGetValue(slot.Key, out long held) && best.Any(i => i.Id == held)
                ? best.First(i => i.Id == held) : best[_random.Next(best.Count)];
            if (!await LeaseAsync(pick, ct)) all = false;
            else if (slot.Key == MountType) mount = pick.Id;
        }
        return (mount, all);
    }

    private async Task<bool> LeaseAsync(CartRemouldTable item, CancellationToken ct)
    {
        var req = new LeaseCareerSpecialItemReq { CartRemouldCfgId = (int)item.Id };
        int result = LeaseCareerSpecialItemRsp.Parser.ParseFrom(await RequestAsync(CareerSpecialService.Lease, req, ct)).ErrCode;
        if (result != 0)
        {
            PopulationContext.Log($"{entry.Name}: class item {item.Id} refused ({result})");
            return false;
        }
        bool changed = _leased.GetValueOrDefault(item.Type) != item.Id;
        _leased[item.Type] = item.Id;
        if (changed) PopulationContext.Log($"{entry.Name}: class item {item.Id}");
        return true;
    }
}
