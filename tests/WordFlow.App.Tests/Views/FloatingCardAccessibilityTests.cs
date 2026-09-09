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
        Assert.DoesNotContain("UndoButton", names);
        Assert.DoesNotContain(card.Descendants(), element => (string?)element.Attribute("Text") == "{Binding ProgressText}");
        var cardSurface = card.Descendants().Single(element => element.Name.LocalName == "AccessibleBorder" && (string?)element.Attribute(x + "Name") == "WindowSurface");
        Assert.Equal("True", (string?)cardSurface.Attribute("Focusable"));
        Assert.Equal("9", (string?)cardSurface.Attribute("KeyboardNavigation.TabIndex"));
        Assert.False(string.IsNullOrWhiteSpace((string?)cardSurface.Attribute("AutomationProperties.Name")));
        Assert.False(string.IsNullOrWhiteSpace((string?)cardSurface.Attribute("AutomationProperties.HelpText")));
        var accessibleBorder = File.ReadAllText(Path.Combine(project, "Views", "Controls", "AccessibleBorder.cs"));
        Assert.Contains("OnCreateAutomationPeer", accessibleBorder, StringComparison.Ordinal);
        Assert.All(card.Descendants(presentation + "Button"), button =>
            Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.Name"))));
        Assert.All(card.Descendants(presentation + "Button").Where(button => (string?)button.Attribute(x + "Name") is "WordText" or "CurrentDetailsButton"), button =>
            Assert.False(string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.HelpText"))));
        var tabOrder = card.Descendants(presentation + "Button")
            .Select(button => int.Parse((string?)button.Attribute("TabIndex") ?? throw new Xunit.Sdk.XunitException("Every card button requires an explicit TabIndex.")))
            .OrderBy(index => index)
            .ToArray();
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 10], tabOrder);

        var cardSlider = card.Descendants(presentation + "Slider").Single(element => (string?)element.Attribute(x + "Name") == "CardScaleSlider");
        Assert.Equal("0.5", (string?)cardSlider.Attribute("Minimum"));
        Assert.Equal("1.0", (string?)cardSlider.Attribute("Maximum"));
        Assert.Equal("True", (string?)cardSlider.Attribute("IsMoveToPointEnabled"));
        Assert.Equal("8", (string?)cardSlider.Attribute("TabIndex"));
        Assert.Equal("卡片内容缩放", (string?)cardSlider.Attribute("AutomationProperties.Name"));

        var controlCenter = XDocument.Load(Path.Combine(project, "Views", "ControlCenterWindow.xaml"));
        var themeSlider = controlCenter.Descendants(presentation + "Slider")
            .Single(element => ((string?)element.Attribute("Value"))?.Contains("Theme.Opacity", StringComparison.Ordinal) == true);
        Assert.Equal("0", (string?)themeSlider.Attribute("Minimum"));
        Assert.Equal("1", (string?)themeSlider.Attribute("Maximum"));
        Assert.Equal("True", (string?)themeSlider.Attribute("IsMoveToPointEnabled"));

        var drawer = XDocument.Load(Path.Combine(project, "Views", "Controls", "RelationDrawer.xaml"));
        var scroll = drawer.Descendants(presentation + "ScrollViewer").Single();
        Assert.Equal("Auto", (string?)scroll.Attribute("VerticalScrollBarVisibility"));
        Assert.All(drawer.Descendants(presentation + "ListBoxItem"), item =>
            Assert.False(string.IsNullOrWhiteSpace((string?)item.Attribute("AutomationProperties.Name"))));
        Assert.Equal("搜索全部关系词", (string?)drawer.Descendants(presentation + "TextBox").Single().Attribute("AutomationProperties.Name"));
        var flatResults = drawer.Descendants(presentation + "ListBox").Single(element => (string?)element.Attribute(x + "Name") == "FlatResultsList");
        Assert.Equal("{Binding FilteredItems}", (string?)flatResults.Attribute("ItemsSource"));
        Assert.Equal("易混词结果列表", (string?)flatResults.Attribute("AutomationProperties.Name"));
        Assert.Contains(drawer.Descendants(presentation + "Expander"), element => (string?)element.Attribute(x + "Name") == "CompactEvidence");
        Assert.All(drawer.Descendants(presentation + "MenuItem"), item =>
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)item.Attribute("AutomationProperties.Name")));
            Assert.False(string.IsNullOrWhiteSpace((string?)item.Attribute("AutomationProperties.HelpText")));
        });
    }
}
