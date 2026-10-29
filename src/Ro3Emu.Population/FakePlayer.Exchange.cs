using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A vendor also lists goods in the trade exchange (TradeService, 326xx), so its shelves are not empty: the realm's
/// exchange goods (StallGoods.Exchange) are shared out among the vendors by item id, a handful each, and a vendor keeps
/// its share listed through the client's own requests (32607 its booth, 32613 a listing) at a price in the server's
/// range. The goods and the listing fee (Minted Coin) come from GM !kit in the bot's own scope. A population choice
/// (inferred): the original server had only real players.
/// </summary>
public sealed partial class FakePlayer
{
    /// <summary>TradeConfigUnlockPlace: the six free listing places (DBUserTradeInfo.StallsNum).</summary>
    private const int ExchangeSlots = 6;
    /// <summary>FunctionConfig 2 (the exchange) opens with condition 2000000: base level 30.</summary>
    private const int ExchangeLevel = 30;
    private const int MintedCoin = 2; // the listing fee's currency (TradeService.ListingFee)
    private DateTime _exchangeAfter;
    private IReadOnlyList<StallGood>? _exchangeShare;
    private readonly HashSet<int> _exchangeClosed = [];

    private async Task ExchangeStepAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        // Within five minutes of the login spread, then every half hour to an hour: an expired or sold listing is replaced.
        if (_exchangeAfter == default) _exchangeAfter = _statsAfter + TimeSpan.FromSeconds(_random.Next(300));
        if (now < _exchangeAfter || entry.Level < ExchangeLevel) return;
        _exchangeAfter = now + TimeSpan.FromMinutes(30 + _random.Next(30));
        var share = ExchangeShare();
        if (share.Count == 0) return;
        var booth = CQueryPlayerBoothInfoResp.Parser.ParseFrom(
            await RequestAsync(TradeService.OwnBooth, new QueryPlayerBoothInfoRequest(), ct)).Data;
        var listed = booth.DataList.Select(c => int.TryParse(c.ItemId, out int id) ? id : 0).ToHashSet();
        int free = ExchangeSlots - booth.DataList.Count;
        foreach (var good in share.Where(g => !listed.Contains(g.ItemId) && !_exchangeClosed.Contains(g.ItemId)))
        {
            if (free <= 0) break;
            if (await ListAsync(good, ct)) free--;
            else _exchangeClosed.Add(good.ItemId); // out of season, bound, ... : not again this session
        }
    }

    /// <summary>This vendor's share: every Nth exchange good, N the realm's vendors, by its place among them.</summary>
    private IReadOnlyList<StallGood> ExchangeShare()
    {
        if (_exchangeShare != null) return _exchangeShare;
        var vendors = ctx.Roster.Players.Where(p => p.Persona == Persona.Vendor).OrderBy(p => p.Account, StringComparer.Ordinal).ToList();
        int place = vendors.IndexOf(entry);
        return _exchangeShare = place < 0 ? [] : [.. ctx.Exchange.Where((_, i) => i % vendors.Count == place)];
    }

    /// <summary>One good granted with its fee (!kit) and listed (32613); false when the server refused it.</summary>
    private async Task<bool> ListAsync(StallGood good, CancellationToken ct)
    {
        int count = good.ShelveLimit > 1 ? Math.Min(good.ShelveLimit, _random.Next(5, 21)) : 1;
        int price = ExchangePrice(good);
        long value = (long)price * count * (good.Currency == CrystalCurrency ? ctx.Options.CrystalGearRate : 1);
        var before = _bag.Find(good.ItemId).Select(i => i.Guid).ToHashSet();
        if (!await ctx.GmAsync(entry.Account, $"!kit {good.ItemId}:{count} {MintedCoin}:{ctx.ListingFee(value)}", ct, ownRole: true))
            return false;
        // A fresh piece; a stackable good may join a stack already in the bag.
        var item = await NewInstanceAsync(good.ItemId, before, ct)
            ?? (good.ShelveLimit > 1 ? _bag.Find(good.ItemId).Where(i => i.Stack >= count).Cast<(long Guid, int Stack)?>().FirstOrDefault() : null);
        if (item is not { } piece) return false;
        var req = new TradePutOnItemReq { ItemGuid = piece.Guid, ItemPrice = price, CurrencyType = good.Currency, Quantity = Math.Min(count, piece.Stack) };
        int result = TradePutOnItemResp.Parser.ParseFrom(await RequestAsync(TradeService.PutOn, req, ct)).Result;
        PopulationContext.Log(result == 0 ? $"{entry.Name}: exchange lists {good.Name} x{req.Quantity} at {price}"
            : $"{entry.Name}: exchange refused {good.Name} ({result})");
        return result == 0;
    }

    /// <summary>A price in TradeService.PriceRule's range: the base times a stable-range step, within its bounds.</summary>
    private int ExchangePrice(StallGood good)
    {
        long min = (good.BasePrice * good.MinStep + 9) / 10, max = Math.Max(min, good.BasePrice * good.MaxStep / 10);
        return (int)Math.Clamp((good.BasePrice * _random.Next(good.MinStep, good.MaxStep + 1) + 5) / 10, min, max);
    }
}
