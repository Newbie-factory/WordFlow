using System.Windows.Controls;
using WordFlow.App.Views;

namespace WordFlow.App.Tests.Views;

public sealed class CardSurfaceInteractionPolicyTests
{
    [Fact]
    public void Blank_content_route_opens_details_only_when_it_reaches_the_card_surface()
    {
        static CardSurfaceInteractionPolicy.RouteNode Node(Type type, bool surface = false) => new(type, surface);

        Assert.True(CardSurfaceInteractionPolicy.ShouldOpenDetails([Node(typeof(TextBlock)), Node(typeof(Grid)), Node(typeof(Border), surface: true)], handled: false));
        Assert.False(CardSurfaceInteractionPolicy.ShouldOpenDetails([Node(typeof(TextBlock)), Node(typeof(Grid))], handled: false));
        Assert.False(CardSurfaceInteractionPolicy.ShouldOpenDetails([Node(typeof(TextBlock)), Node(typeof(Border), surface: true)], handled: true));
    }

    [Fact]
    public void Drag_chrome_and_every_interactive_descendant_are_excluded()
    {
        var interactive = new Type[]
        {
            typeof(Button), typeof(TextBox), typeof(ListBoxItem), typeof(Expander), typeof(ScrollViewer), typeof(System.Windows.Controls.Primitives.Thumb),
        };

        Assert.False(CardSurfaceInteractionPolicy.ShouldOpenDetails(
            [new(typeof(Border), IsDragRegion: true), new(typeof(Border), IsCardSurface: true)], handled: false));
        Assert.All(interactive, type => Assert.False(CardSurfaceInteractionPolicy.ShouldOpenDetails(
            [new(typeof(TextBlock)), new(type), new(typeof(Border), IsCardSurface: true)], handled: false)));
    }
}
