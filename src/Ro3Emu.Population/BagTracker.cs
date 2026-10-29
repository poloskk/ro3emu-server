using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Romsg;

namespace Ro3Emu.Population;

/// <summary>
/// The bot's bag as the client sees it: 5003 DBItemsInfoIncrement (CharacterService.ItemsSync), every pack (the
/// bag's tabs: goods land in packs 1, 2 and 4 by item type), applied the way the client applies increments: an "all"
/// pack or item replaces, "inc" changes fields, "del" removes.
/// </summary>
public sealed class BagTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<int, Dictionary<long, (int Entry, int Stack)>> _packs = [];

    public void Observe(BotEvent e)
    {
        if (!e.IsGame(CharacterService.ItemsSync)) return;
        var sync = DBItemsInfoIncrement.Parser.ParseFrom(e.Body);
        lock (_sync)
        {
            if (sync.SyncAllMark) _packs.Clear();
            foreach (var (id, pack) in sync.PackInfo)
            {
                if (pack.Del) { _packs.Remove(id); continue; }
                if (!_packs.TryGetValue(id, out var items)) _packs[id] = items = [];
                if (pack.All is { } all)
                {
                    items.Clear();
                    foreach (var (guid, item) in all.Items) items[guid] = (item.ItemEntry, item.Stack);
                }
                else if (pack.Inc is { } inc) Apply(items, inc);
            }
        }
    }

    private static void Apply(Dictionary<long, (int Entry, int Stack)> items, DBPackInfoIncrement inc)
    {
        foreach (var (guid, change) in inc.Items)
        {
            if (change.Del) items.Remove(guid);
            else if (change.All is { } item) items[guid] = (item.ItemEntry, item.Stack);
            else if (change.Inc is { } fields && items.TryGetValue(guid, out var known))
                items[guid] = (fields.HasItemEntry ? fields.ItemEntry : known.Entry, fields.HasStack ? fields.Stack : known.Stack);
        }
    }

    /// <summary>Bag instances of <paramref name="entry"/> in any pack: (guid, stack).</summary>
    public (long Guid, int Stack)[] Find(int entry)
    {
        lock (_sync)
            return [.. _packs.Values.SelectMany(p => p).Where(i => i.Value.Entry == entry).Select(i => (i.Key, i.Value.Stack))];
    }
}
