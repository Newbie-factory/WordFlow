using System.Xml.Linq;

namespace WordFlow.App.Tests.Views;

public sealed class FloatingCardAccessibilityTests
{
    [Fact]
    public void Xaml_keeps_reading_order_focus_names_and_bounded_drawer_scroll()
    {
        string project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WordFlow.App"));
        var card = XDocument.Load(Path.Combine(project, "Views", "FloatingCardWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var names = card.Descendants().Select(element => (string?)element.Attribute(x + "Name")).Where(name => name is not null).ToArray();

        Assert.True(Array.IndexOf(names, "WordText") < Array.IndexOf(names, "PhoneticText"));
        Assert.True(Array.IndexOf(names, "PhoneticText") < Array.IndexOf(names, "ChineseText"));
        Assert.Contains("AgainButton", names);
        Assert.Contains("SynonymsButton", names);
        Assert.All(card.Descendants(presentation + "Button"), button =>
            Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name"))));
        var tabOrder = card.Descendants(presentation + "Button")
            .Select(button => int.Parse((string?)button.Attribute("TabIndex") ?? throw new Xunit.Sdk.XunitException("Every card button requires an explicit TabIndex.")))
            .OrderBy(index => index)
            .ToArray();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], tabOrder);

        var drawer = XDocument.Load(Path.Combine(project, "Views", "Controls", "RelationDrawer.xaml"));
        var list = drawer.Descendants(presentation + "ListBox").Single();
        Assert.Equal("420", (string?)list.Attribute("MaxHeight"));
        Assert.Equal("Auto", (string?)list.Attribute("ScrollViewer.VerticalScrollBarVisibility"));
    }
}
