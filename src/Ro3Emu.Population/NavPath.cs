using Ro3Emu.Server.Maps;

namespace Ro3Emu.Population;

/// <summary>
/// Paths over a map's nav grid (1 m cells, NavGrid): A* over walkable cells whose heights join (at most 1 m per cell,
/// inferred from the client's 2 m layer tolerance), then shortened to the corners the client's direct-walk check
/// (NavGrid.CanWalk) needs, so every leg passes the server's walk check.
/// </summary>
public static class NavPath
{
    private const float StepHeight = 1f;
    private const int MaxExpanded = 40_000;
    private static readonly (int X, int Z, float Cost)[] Neighbours =
        [(1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1), (1, 1, 1.414f), (1, -1, 1.414f), (-1, 1, 1.414f), (-1, -1, 1.414f)];

    /// <summary>Corner points from <paramref name="from"/> to <paramref name="to"/> (to included), or null when none.</summary>
    public static List<MapPoint>? Find(MapWorld map, MapPoint from, MapPoint to)
    {
        if (map.Nav is not { } nav) return [to];
        if (nav.CanWalk(from.X, from.Y, from.Z, to.X, to.Z)) return [to];
        var cells = Search(nav, from, to);
        return cells == null ? null : Shorten(nav, from, cells);
    }

    private static List<MapPoint>? Search(NavGrid nav, MapPoint from, MapPoint to)
    {
        var start = ((int)MathF.Floor(from.X), (int)MathF.Floor(from.Z));
        var goal = ((int)MathF.Floor(to.X), (int)MathF.Floor(to.Z));
        var height = new Dictionary<(int, int), float> { [start] = from.Y };
        var cost = new Dictionary<(int, int), float> { [start] = 0 };
        var parent = new Dictionary<(int, int), (int, int)>();
        var open = new PriorityQueue<(int X, int Z), float>();
        open.Enqueue(start, 0);
        int expanded = 0;
        while (open.TryDequeue(out var cell, out _) && ++expanded < MaxExpanded)
        {
            if (cell == goal) return Trace(parent, height, goal);
            foreach (var (dx, dz, step) in Neighbours)
            {
                var next = (cell.X + dx, cell.Z + dz);
                // No corner cutting: a diagonal step needs both side cells, or the walk grazes a blocked cell.
                if (dx != 0 && dz != 0 && (!Open(nav, cell.X + dx, cell.Z, height[cell]) || !Open(nav, cell.X, cell.Z + dz, height[cell]))) continue;
                if (nav.WalkHeight(next.Item1 + 0.5f, next.Item2 + 0.5f, height[cell]) is not { } h || MathF.Abs(h - height[cell]) > StepHeight) continue;
                float g = cost[cell] + step;
                if (cost.TryGetValue(next, out var known) && known <= g) continue;
                cost[next] = g;
                height[next] = h;
                parent[next] = cell;
                open.Enqueue(next, g + MathF.Sqrt((goal.Item1 - next.Item1) * (goal.Item1 - next.Item1) + (goal.Item2 - next.Item2) * (goal.Item2 - next.Item2)));
            }
        }
        return null;
    }

    private static bool Open(NavGrid nav, int x, int z, float y) =>
        nav.WalkHeight(x + 0.5f, z + 0.5f, y) is { } h && MathF.Abs(h - y) <= StepHeight;

    private static List<MapPoint> Trace(Dictionary<(int, int), (int, int)> parent, Dictionary<(int, int), float> height, (int, int) goal)
    {
        var path = new List<MapPoint>();
        for (var c = goal; parent.ContainsKey(c); c = parent[c]) path.Add(new MapPoint(c.Item1 + 0.5f, height[c], c.Item2 + 0.5f, 0));
        path.Reverse();
        return path;
    }

    /// <summary>Keeps a cell only where the straight walk from the last kept point stops passing.</summary>
    private static List<MapPoint> Shorten(NavGrid nav, MapPoint from, List<MapPoint> cells)
    {
        var corners = new List<MapPoint>();
        var anchor = from;
        for (int i = 0; i < cells.Count; i++)
        {
            bool last = i == cells.Count - 1;
            if (!last && nav.CanWalk(anchor.X, anchor.Y, anchor.Z, cells[i + 1].X, cells[i + 1].Z)) continue;
            corners.Add(cells[i]);
            anchor = cells[i];
        }
        return corners;
    }
}
