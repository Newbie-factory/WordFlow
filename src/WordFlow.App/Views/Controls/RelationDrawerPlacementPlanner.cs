namespace WordFlow.App.Views.Controls;

public enum RelationDrawerDirection { Below, Above }

public sealed record RelationDrawerPlacementPlan(RelationDrawerDirection Direction, double MaxHeight, bool UseCompactRows);

public static class RelationDrawerPlacementPlanner
{
    public static RelationDrawerPlacementPlan Plan(double desiredHeight, double belowAvailable, double aboveAvailable)
    {
        if (!double.IsFinite(desiredHeight) || desiredHeight < 0) throw new ArgumentOutOfRangeException(nameof(desiredHeight));
        if (!double.IsFinite(belowAvailable) || belowAvailable < 0) throw new ArgumentOutOfRangeException(nameof(belowAvailable));
        if (!double.IsFinite(aboveAvailable) || aboveAvailable < 0) throw new ArgumentOutOfRangeException(nameof(aboveAvailable));

        if (desiredHeight <= belowAvailable) return new(RelationDrawerDirection.Below, desiredHeight, false);
        if (desiredHeight <= aboveAvailable) return new(RelationDrawerDirection.Above, desiredHeight, false);
        var direction = belowAvailable >= aboveAvailable ? RelationDrawerDirection.Below : RelationDrawerDirection.Above;
        return new(direction, direction == RelationDrawerDirection.Below ? belowAvailable : aboveAvailable, true);
    }
}
