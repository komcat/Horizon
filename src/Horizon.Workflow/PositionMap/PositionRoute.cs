namespace Horizon.Workflow;

/// <summary>
/// Route planning over a device's position map (<see cref="PositionMapLayout"/>): the
/// fewest-hop path along the links, through positions that still exist. Shared by the
/// <c>motion.movePath</c> step; the same rule as the Machine › Motion map's Follow path.
/// </summary>
public static class PositionRoute
{
    /// <summary>
    /// Fewest-hop route <paramref name="from"/> → <paramref name="to"/>, both ends included, or
    /// null when no linked route exists. <paramref name="exists"/> filters out links to positions
    /// that are no longer taught. Names compare case-insensitively.
    /// </summary>
    public static IReadOnlyList<string>? Find(PositionMapLayout map, string from, string to, Func<string, bool> exists)
    {
        var cmp = StringComparer.OrdinalIgnoreCase;
        if (cmp.Equals(from, to)) return new[] { to };

        var prev = new Dictionary<string, string?>(cmp) { [from] = null };
        var queue = new Queue<string>();
        queue.Enqueue(from);
        // Bounded: every position enters the queue at most once.
        while (queue.Count > 0 && !prev.ContainsKey(to))
        {
            var node = queue.Dequeue();
            foreach (var edge in map.Edges)
            {
                string? next = cmp.Equals(edge.A, node) ? edge.B : cmp.Equals(edge.B, node) ? edge.A : null;
                if (next == null || prev.ContainsKey(next) || !exists(next)) continue;
                prev[next] = node;
                queue.Enqueue(next);
            }
        }
        if (!prev.ContainsKey(to)) return null;

        var route = new List<string>();
        for (string? n = to; n != null; n = prev[n]) route.Add(n);
        route.Reverse();
        return route;
    }
}
