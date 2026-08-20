using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace WordFlow.App.Views;

public static class CardSurfaceInteractionPolicy
{
    public readonly record struct RouteNode(Type ElementType, bool IsCardSurface = false, bool IsDragRegion = false);

    public static bool ShouldOpenDetails(
        IEnumerable<RouteNode> sourceToRoot,
        bool handled)
    {
        ArgumentNullException.ThrowIfNull(sourceToRoot);
        if (handled) return false;

        foreach (var element in sourceToRoot)
        {
            if (element.IsDragRegion || IsInteractive(element.ElementType)) return false;
            if (element.IsCardSurface) return true;
        }
        return false;
    }

    private static bool IsInteractive(Type elementType) => InteractiveTypes.Any(type => type.IsAssignableFrom(elementType));

    private static readonly Type[] InteractiveTypes =
    [
        typeof(ButtonBase), typeof(TextBoxBase), typeof(Selector), typeof(ListBoxItem), typeof(TreeViewItem),
        typeof(Expander), typeof(ScrollViewer), typeof(Thumb), typeof(RangeBase), typeof(PasswordBox),
        typeof(MenuItem), typeof(Hyperlink),
    ];
}
