namespace WordFlow.App.Views.Controls;

public static class RelationDrawerViewport
{
    public static double Measure(IEnumerable<double> rowHeights, double availableHeight, double rowGap = 0)
    {
        ArgumentNullException.ThrowIfNull(rowHeights);
        if (!double.IsFinite(availableHeight) || availableHeight < 0) throw new ArgumentOutOfRangeException(nameof(availableHeight));
        var heights = rowHeights.Where(height => double.IsFinite(height) && height > 0).Take(5).ToArray();
        if (heights.Length == 0) return 0;
        return Math.Min(availableHeight, heights.Sum() + Math.Max(0, heights.Length - 1) * Math.Max(0, rowGap));
    }
}
