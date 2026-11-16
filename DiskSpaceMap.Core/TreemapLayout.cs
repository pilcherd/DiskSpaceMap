namespace DiskSpaceMap.Core;

public readonly record struct MapRect(double X, double Y, double Width, double Height);
public readonly record struct MapTile(int Index, MapRect Bounds, bool IsRemainder = false);

/// <summary>Squarifies descending weights. Tiny tails remain a proportional aggregate, not lost area.</summary>
public static class TreemapLayout
{
    /// <summary>A fixed binary partition for live maps. Changing weights never reorders
    /// entries or changes split directions, so interpolated weights cannot overlap.</summary>
    public static IReadOnlyList<MapTile> LayoutOrdered(IReadOnlyList<double> weights, MapRect bounds)
    {
        if (weights.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0) return [];
        var sums = new double[weights.Count + 1];
        for (int i = 0; i < weights.Count; i++) sums[i + 1] = sums[i] + Math.Max(0, weights[i]);
        var output = new List<MapTile>(weights.Count);
        Split(0, weights.Count, bounds, bounds.Width >= bounds.Height);
        return output;

        void Split(int start, int end, MapRect rect, bool vertical)
        {
            double total = sums[end] - sums[start];
            if (total <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
            if (end - start == 1) { output.Add(new(start, rect)); return; }
            int middle = start + (end - start) / 2;
            double fraction = Math.Clamp((sums[middle] - sums[start]) / total, 0, 1);
            if (vertical)
            {
                double width = rect.Width * fraction;
                Split(start, middle, rect with { Width = width }, !vertical);
                Split(middle, end, new(rect.X + width, rect.Y, rect.Width - width, rect.Height), !vertical);
            }
            else
            {
                double height = rect.Height * fraction;
                Split(start, middle, rect with { Height = height }, !vertical);
                Split(middle, end, new(rect.X, rect.Y + height, rect.Width, rect.Height - height), !vertical);
            }
        }
    }

    public static IReadOnlyList<MapTile> Layout(IReadOnlyList<long> weights, MapRect bounds, int maxTiles = 1800)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || maxTiles < 1) return [];
        double total = 0;
        foreach (long weight in weights) total += Math.Max(0, weight);
        if (total <= 0) return [];
        var values = new List<(int Index, double Weight, bool Remainder)>(Math.Min(weights.Count, maxTiles));
        double tail = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            if (weights[i] <= 0) continue;
            if (values.Count < maxTiles - 1 || i == weights.Count - 1 && tail == 0)
                values.Add((i, weights[i], false));
            else tail += weights[i];
        }
        if (tail > 0) values.Add((-1, tail, true));
        values.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
        double scale = bounds.Width * bounds.Height / total;
        var output = new List<MapTile>(values.Count);
        double x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height;
        int start = 0;
        while (start < values.Count && width > 0 && height > 0)
        {
            double side = Math.Min(width, height);
            int end = start + 1;
            double sum = values[start].Weight * scale, min = sum, max = sum;
            double worst = Worst(sum, min, max, side);
            while (end < values.Count)
            {
                double next = values[end].Weight * scale;
                double nextWorst = Worst(sum + next, Math.Min(min, next), Math.Max(max, next), side);
                if (nextWorst > worst) break;
                sum += next; min = Math.Min(min, next); max = Math.Max(max, next); worst = nextWorst; end++;
            }
            bool vertical = width >= height;
            double thickness = Math.Min(vertical ? width : height, sum / side);
            double offset = 0;
            for (int i = start; i < end; i++)
            {
                double length = i == end - 1 ? side - offset : values[i].Weight * scale / thickness;
                var rect = vertical ? new MapRect(x, y + offset, thickness, length) : new MapRect(x + offset, y, length, thickness);
                output.Add(new(values[i].Index, rect, values[i].Remainder));
                offset += length;
            }
            if (vertical) { x += thickness; width -= thickness; } else { y += thickness; height -= thickness; }
            start = end;
        }
        return output;
    }

    private static double Worst(double sum, double min, double max, double side)
        => Math.Max(side * side * max / (sum * sum), sum * sum / (side * side * min));
}
