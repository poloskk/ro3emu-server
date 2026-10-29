using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// Vendor: opens a player stall in a stall map (PublicParamConfig.StallsMapID) with rare goods, stands by it and
/// restocks what sold. The goods are made by GM !item (a population choice: the original server had no such
/// sellers); everything after that is the ordinary player stall protocol (33401 open, 33403 put, 33408 info).
/// </summary>
public sealed partial class FakePlayer
{
    private const int StallSize = 6;
    private readonly BagTracker _bag = new();

    private async Task VendAsync(CancellationToken ct)
    {
        await ExchangeStepAsync(ct);
        if (ctx.Goods.Count == 0)
        {
            await StrollAsync(ct);
            return;
        }
        var stall = await StallInfoAsync(ct);
        if (stall == null || stall.BEnd)
        {
            if (!await OpenStallAsync(ct))
            {
                await PauseAsync(60, 120, ct); // a busy server answers GM late: try again soon
                return;
            }
        }
        else if (stall.Itembase.Count < StallSize / 2) await RestockAsync(StallSize - stall.Itembase.Count, ct);
        for (int i = _random.Next(10, 30); i > 0 && !ct.IsCancellationRequested && !InParty; i--)
        {
            await PauseAsync(20, 40, ct);
            if (_random.NextDouble() < 0.15) await FaceAsync(_random.Next(360), ct);
        }
    }

    /// <summary>33408: the own stall, or null when none was ever opened.</summary>
    private async Task<StallsInfo?> StallInfoAsync(CancellationToken ct)
    {
        using var reply = _bot!.Inbox.Expect(e => e.IsGame(StallsService.Info));
        await SendAsync(StallsService.Info, new PlayerStallsInfoReq(), ct);
        var info = PlayerStallsInfoResp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(15), ct)).Body);
        return info.CurrentStallsID == 0 ? null : info.Info;
    }

    /// <summary>A free spot (StallsIntervalRadius from other stalls), the fee, the goods, then 33401; false when no stall.</summary>
    private async Task<bool> OpenStallAsync(CancellationToken ct)
    {
        await PayFeeAsync(ct);
        _stocked.Clear();
        var offers = await StockAsync(StallSize, ct);
        if (offers.Count == 0)
        {
            PopulationContext.Log($"{entry.Name}: no goods reached the bag; no stall this time");
            return false;
        }
        var spot = Home.Spots[_random.Next(Home.Spots.Count)];
        await GoAsync(Jitter(spot), ct);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var req = new PlayerStartStallsReq { StallsTitle = StallTitle() };
            req.Itembase.Add(offers);
            int result = await StallRequestAsync(StallsService.Start, req, PlayerStartStallsRsp.Parser, r => r.Result, ct);
            if (result is 0 or ErrorCodes.StallExists)
            {
                PopulationContext.Log($"{entry.Name}: stall \"{req.StallsTitle}\" open with {offers.Count} goods");
                return true;
            }
            if (result != ErrorCodes.StallTooClose)
            {
                PopulationContext.Log($"{entry.Name}: stall refused ({result})");
                return false;
            }
            await ShuffleAsync(8, ct);
        }
        return false;
    }

    private async Task RestockAsync(int count, CancellationToken ct)
    {
        foreach (var offer in await StockAsync(count, ct))
            await StallRequestAsync(StallsService.Put, new PlayerStallsPutItemReq { PutItem = offer }, PlayerStallsPutItemRsp.Parser, r => r.Result, ct);
    }

    /// <summary>
    /// Offers for up to <paramref name="count"/> goods: those already in the bag (an earlier opening that failed) first,
    /// then fresh ones (GM !item), one to a few of each, priced on the item's ladder.
    /// </summary>
    private async Task<List<StallsPutItem>> StockAsync(int count, CancellationToken ct)
    {
        var offers = new List<StallsPutItem>();
        var goods = MyGoods();
        foreach (var good in goods)
        {
            if (offers.Count >= count) return offers;
            foreach (var (guid, stack) in _bag.Find(good.ItemId).Take(count - offers.Count))
            {
                offers.Add(new StallsPutItem { Itemid = guid, Itemnum = Math.Min(stack, good.ShelveLimit), Price = good.Price(_random), Pricetype = good.Currency });
                _stocked.Add(good);
            }
        }
        foreach (var good in goods.OrderBy(_ => _random.Next()).Take(count - offers.Count))
        {
            int num = Math.Min(good.ShelveLimit, _random.Next(1, 4));
            var before = _bag.Find(good.ItemId).Select(i => i.Guid).ToHashSet();
            // !kit: a grant in the bot's own scope (an !item line held the whole realm).
            if (!await ctx.GmAsync(entry.Account, $"!kit {good.ItemId}:{num}", ct, ownRole: true)) break;
            var added = await NewInstanceAsync(good.ItemId, before, ct);
            if (added is not { } item) continue;
            offers.Add(new StallsPutItem { Itemid = item.Guid, Itemnum = Math.Min(num, item.Stack), Price = good.Price(_random), Pricetype = good.Currency });
            _stocked.Add(good);
        }
        return offers;
    }

    /// <summary>Waits for the bag sync that brings an instance of the item not in <paramref name="before"/>.</summary>
    private async Task<(long Guid, int Stack)?> NewInstanceAsync(int itemId, HashSet<long> before, CancellationToken ct)
    {
        for (int i = 0; i < 150; i++) // up to 15 s: a busy server syncs the bag late
        {
            var fresh = _bag.Find(itemId).Where(x => !before.Contains(x.Guid)).ToArray();
            if (fresh.Length > 0) return fresh[0];
            await Task.Delay(100, ct);
        }
        return null;
    }

    private async Task PayFeeAsync(CancellationToken ct)
    {
        if (ctx.OpeningFee is [var currency, var amount] && amount > 0)
            await ctx.GmAsync(entry.Account, $"!kit {currency}:{amount}", ct, ownRole: true); // own scope: !item held the realm
    }

    private async Task<int> StallRequestAsync<T>(uint id, Google.Protobuf.IMessage req, Google.Protobuf.MessageParser<T> parser,
        Func<T, int> result, CancellationToken ct) where T : Google.Protobuf.IMessage<T>
    {
        using var reply = _bot!.Inbox.Expect(e => e.IsGame(id));
        await SendAsync(id, req, ct);
        return result(parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(15), ct)).Body));
    }

    private const int CrystalCurrency = 3; // Ro3Emu:CrystalGear's currency, Sacred Relic Crystal
    private readonly List<StallGood> _stocked = [];

    /// <summary>A third of the vendors (fixed per role) sell red gear for crystals when the realm offers it; the others
    /// their usual goods.</summary>
    private IReadOnlyList<StallGood> MyGoods()
    {
        bool crystal = entry.RoleId % 3 == 0 && ctx.Goods.Any(g => g.Currency == CrystalCurrency);
        return [.. ctx.Goods.Where(g => (g.Currency == CrystalCurrency) == crystal)];
    }

    /// <summary>The stall's sign names what it sells, the way players advertise ("S&gt; Red Lv90, Red Lv70"), as many
    /// goods as the title holds (ServerOptions.StallNameMaxLength, 24).</summary>
    private string StallTitle()
    {
        const int Room = 24;
        string title = "";
        foreach (var label in _stocked.Select(g => g.Label is "" ? g.Name : g.Label).Distinct())
        {
            string next = title == "" ? $"S> {label}" : $"{title}, {label}";
            if (next.Length > Room) { if (title == "") title = next[..Room]; break; }
            title = next;
        }
        return title == "" ? $"{entry.Name}'s shop" : title;
    }
}
