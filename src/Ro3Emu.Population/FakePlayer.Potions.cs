using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// A fake player in a real player's party drinks HP potions as a player does: below <see cref="DrinkBelow"/> of its life
/// (after it ran from a boss below FakePlayer.Evade's PartyEvadeLife, too) it uses its level's potion (GearPlan.Potion)
/// through the client's 31323, whose cooldowns (_iCD, _kCDGroup) the server keeps. Potions come from GM !kit in the
/// bot's own scope when it runs low. A population choice (inferred): the original server had only real players.
/// </summary>
public sealed partial class FakePlayer
{
    private const double DrinkBelow = 0.5;
    private const int PotionStock = 30, PotionLow = 5;
    private DateTime _nextDrink;

    /// <summary>One potion when the bot is low and its cooldown is over; true when it drank.</summary>
    private async Task<bool> DrinkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _nextDrink || OwnLife() >= DrinkBelow || ctx.Gear.Potion(entry.Level) is not (> 0 and var potion)) return false;
        _nextDrink = now + TimeSpan.FromSeconds(3); // the next try at the earliest, whatever comes of this one
        var stack = _bag.Find(potion).OrderByDescending(i => i.Stack).FirstOrDefault();
        if (stack.Stack < PotionLow)
        {
            var before = _bag.Find(potion).Select(i => i.Guid).ToHashSet();
            if (!await ctx.GmAsync(entry.Account, $"!kit {potion}:{PotionStock}", ct, ownRole: true)) return false;
            // The grant joins the stack, or is a fresh one.
            for (int i = 0; i < 50 && _bag.Find(potion).Sum(x => x.Stack) <= stack.Stack; i++) await Task.Delay(100, ct);
            stack = _bag.Find(potion).OrderByDescending(i => i.Stack).FirstOrDefault();
            if (stack.Stack == 0) return false;
        }
        var req = new ItemUseReq { ItemGuid = stack.Guid, ItemCount = 1 };
        var rsp = ItemUseResp.Parser.ParseFrom(await RequestAsync(CharacterService.ItemUse, req, ct));
        // A potion's shared cooldown (_kCDGroup) holds the next one; the server answers when that ends.
        if (rsp.Result != 0) { _nextDrink = now + TimeSpan.FromSeconds(5); return false; }
        if (rsp.ShareCd > 0 || rsp.ItemCd > 0)
            _nextDrink = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(rsp.ShareCd, rsp.ItemCd)).UtcDateTime;
        PopulationContext.Log($"{entry.Name}: drinks potion {potion}");
        return true;
    }
}
