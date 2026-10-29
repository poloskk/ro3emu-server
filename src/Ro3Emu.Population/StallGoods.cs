using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Population;

/// <summary>One item a vendor may stock: its quality, stack cap and the price ladder the server accepts.</summary>
/// <remarks><paramref name="Label"/>: how a stall title names it (the item's name, or "Red Lv90" for crystal gear).</remarks>
public sealed record StallGood(int ItemId, string Name, long Quality, int ShelveLimit, int Currency, long BasePrice, int MinStep, int MaxStep,
    string Label = "")
{
    /// <summary>A price on the server's ladder: floor(base*step/10+0.5) for a step in the stable range (StallsService.ValidPrice).</summary>
    public int Price(Random random) => (int)((BasePrice * random.Next(MinStep, MaxStep + 1) + 5) / 10);
}

/// <summary>
/// What vendors sell: items a player stall accepts (TradeConfig._iTrade == 2, StallsService.SaleConfig) whose
/// ItemConfig._iQuality is one of the wanted qualities; and the stall rules vendors follow (PublicParamConfig).
/// Season windows (_kGameSeason) are left to the server, which refuses an out-of-season item with NotForSale.
/// </summary>
public sealed class StallGoods(GameData data)
{
    private const int PlayerStall = 2, Exchanged = 1; // TradeConfig _iTrade: 2 player stall, 1 trade exchange
    private const long EquipmentType = 5, Unreachable = 999; // ItemConfig _iType 5; _iLevelNeed 999: not a wearable tier

    public IReadOnlyList<StallGood> Catalogue(IReadOnlySet<long> qualities) =>
        [.. Sellable().Where(x => qualities.Contains(x.Item.Quality)).Select(x => Good(x.Id, x.Trade, x.Item))];

    /// <summary>
    /// Ro3Emu:CrystalGear (a realm policy, inferred): equipment of <paramref name="quality"/> with a TradeConfig price,
    /// sold for <paramref name="currency"/> on its own ladder from base / <paramref name="rate"/>
    /// (StallsService.CrystalBase), as the server accepts it.
    /// </summary>
    public IReadOnlyList<StallGood> CrystalGear(long rate, int quality, int currency) =>
        [.. data.Table("TradeConfig")
            .Select(p => (Id: long.TryParse(p.Key, out var id) ? id : 0, Trade: new TradeConfig(p.Value)))
            .Where(x => x.Id > 0 && x.Trade.BasePrice > 0)
            .SelectMany(x => ItemConfig.Get(data, x.Id) is { Type: EquipmentType, LevelNeed: < Unreachable } item && item.Quality == quality
                ? [Good(x.Id, x.Trade, item) with
                {
                    Currency = currency, BasePrice = StallsService.CrystalBase(x.Trade.BasePrice, rate), Label = $"Red Lv{item.LevelNeed}",
                }]
                : Array.Empty<StallGood>())];

    /// <summary>
    /// What vendors list in the trade exchange (TradeService): items it accepts (TradeConfig _iTrade 1) with a price,
    /// wearable when equipment, priced in _iMoney on the server's range (TradeService.PriceRule: _kStableRange tenths of
    /// the base, 10/10 without one); crystal gear (rate above 0: equipment of <paramref name="quality"/>) in
    /// <paramref name="currency"/> from base / rate. Ordered by item id.
    /// </summary>
    public IReadOnlyList<StallGood> Exchange(long rate, int quality, int currency) =>
        [.. data.Table("TradeConfig")
            .Select(p => (Id: long.TryParse(p.Key, out var id) ? id : 0, Trade: new TradeConfig(p.Value)))
            .Where(x => x.Id > 0 && x.Trade.Trade == Exchanged && x.Trade.BasePrice > 0)
            .SelectMany(x => ItemConfig.Get(data, x.Id) is { } item && (item.Type != EquipmentType || item.LevelNeed < Unreachable)
                ? [ExchangeGood(x.Id, x.Trade, item, rate > 0 && item.Type == EquipmentType && item.Quality == quality ? rate : 0, currency)]
                : Array.Empty<StallGood>())
            .OrderBy(g => g.ItemId)];

    private StallGood ExchangeGood(long id, TradeConfig trade, ItemConfig item, long crystalRate, int currency)
    {
        var range = trade.StableRange;
        (int min, int max) = range.Length < 2 ? (10, 10) : ((int)Math.Clamp(range[0], 1, 1000), (int)Math.Clamp(range[1], range[0], 1000));
        return new StallGood((int)id, data.ItemName(id), item.Quality, (int)Math.Max(1, trade.ShelveLimit),
            crystalRate > 0 ? currency : (int)trade.Money, crystalRate > 0 ? StallsService.CrystalBase(trade.BasePrice, crystalRate) : trade.BasePrice,
            min, max);
    }

    /// <summary>
    /// The exchange's listing fee in Minted Coin (item 2) for a listing worth <paramref name="value"/>
    /// (TradeService.ListingFee, _iMoney 1: PublicParamConfig ShelvesTaxation_Miao basis points, rounded up, within
    /// ShelvesTaxationInterval_Miao).
    /// </summary>
    public long ListingFee(long value)
    {
        long rate = data.Table("PublicParamConfig").TryGetValue("ShelvesTaxation_Miao", out var x) && GameData.Number(x) is { } n and > 0
            ? (long)n : 700;
        long fee = (value * rate + 9_999) / 10_000;
        return Numbers("ShelvesTaxationInterval_Miao") is [var min, var max, ..] ? Math.Clamp(fee, min, Math.Max(min, max)) : fee;
    }

    /// <summary>The qualities present among stall goods, with how many items each has (for the startup line).</summary>
    public IReadOnlyDictionary<long, int> QualityCounts() =>
        Sellable().GroupBy(x => x.Item.Quality).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>TradeConfig rows are keyed by item id.</summary>
    private IEnumerable<(long Id, TradeConfig Trade, ItemConfig Item)> Sellable() =>
        data.Table("TradeConfig")
            .Select(p => (Id: long.TryParse(p.Key, out var id) ? id : 0, Trade: new TradeConfig(p.Value)))
            .Where(x => x.Id > 0 && x.Trade.Trade == PlayerStall && x.Trade.BasePrice > 0)
            .SelectMany(x => ItemConfig.Get(data, x.Id) is { } item ? [(x.Id, x.Trade, item)] : Array.Empty<(long, TradeConfig, ItemConfig)>());

    private StallGood Good(long id, TradeConfig trade, ItemConfig item)
    {
        // StallsService.Range: _iTradeType 1 or no _kStableRange is the fixed 10/10 step.
        var range = trade.StableRange;
        (int min, int max) = trade.TradeType == 1 || range.Length < 2 ? (10, 10)
            : ((int)Math.Clamp(range[0], 1, 1000), (int)Math.Clamp(range[1], range[0], 1000));
        return new StallGood((int)id, data.ItemName(id), item.Quality, (int)Math.Max(1, trade.ShelveLimit), (int)trade.Money,
            trade.BasePrice, min, max);
    }

    /// <summary>PublicParamConfig.StallsMapID: the maps where players may open a stall.</summary>
    public long[] StallMaps() => Numbers("StallsMapID");

    /// <summary>PublicParamConfig.StallsExpenses: [currency, amount] paid on opening.</summary>
    public long[] OpeningFee() => Numbers("StallsExpenses");

    private long[] Numbers(string key) =>
        data.Table("PublicParamConfig").TryGetValue(key, out var x) && x.ValueKind == JsonValueKind.Array
            ? [.. x.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0))]
            : [];
}
