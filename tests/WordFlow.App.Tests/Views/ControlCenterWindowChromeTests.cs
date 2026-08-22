using System.Xml.Linq;
using WordFlow.App.Views;

namespace WordFlow.App.Tests.Views;

public sealed class ControlCenterWindowChromeTests
{
    [Theory]
    [InlineData(1, 1, 13)]
    [InlineData(999, 1, 14)]
    [InlineData(1, 699, 16)]
    [InlineData(999, 699, 17)]
    [InlineData(500, 699, 15)]
    [InlineData(500, 350, 1)]
    public void Resize_hit_test_maps_edges_and_interior(double x, double y, int expected)
    {
        Assert.Equal(expected, WindowResizeHitTest.Resolve(x, y, 1000, 700, 8));
    }

    [Fact]
    public void Xaml_uses_one_rounded_card_with_integrated_window_controls()
    {
        string project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WordFlow.App"));
        var document = XDocument.Load(Path.Combine(project, "Views", "ControlCenterWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var window = document.Root ?? throw new Xunit.Sdk.XunitException("Control center window root is missing.");

        Assert.Equal("None", (string?)window.Attribute("WindowStyle"));
        Assert.Equal("True", (string?)window.Attribute("AllowsTransparency"));
        Assert.Equal("Transparent", (string?)window.Attribute("Background"));

        var rootCard = window.Elements(presentation + "Border").Single();
        Assert.Equal("RootCard", (string?)rootCard.Attribute(x + "Name"));
        Assert.False(string.IsNullOrWhiteSpace((string?)rootCard.Attribute("CornerRadius")));

        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("TitleBar", names);
        Assert.Contains("MinimizeButton", names);
        Assert.Contains("MaximizeRestoreButton", names);
        Assert.Contains("CloseButton", names);
    }

    [Fact]
    public void Integrated_window_controls_are_accessible_and_wired()
    {
        string project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WordFlow.App"));
        var document = XDocument.Load(Path.Combine(project, "Views", "ControlCenterWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var buttons = document.Descendants()
            .Where(element => (string?)element.Attribute(x + "Name") is
                "MinimizeButton" or "MaximizeRestoreButton" or "CloseButton")
            .ToDictionary(element => (string)element.Attribute(x + "Name")!, StringComparer.Ordinal);

        Assert.Equal("最小化控制中心", (string?)buttons["MinimizeButton"].Attribute("AutomationProperties.Name"));
        Assert.Equal("最大化控制中心", (string?)buttons["MaximizeRestoreButton"].Attribute("AutomationProperties.Name"));
        Assert.Equal("隐藏控制中心", (string?)buttons["CloseButton"].Attribute("AutomationProperties.Name"));
        Assert.Equal("Minimize_Click", (string?)buttons["MinimizeButton"].Attribute("Click"));
        Assert.Equal("MaximizeRestore_Click", (string?)buttons["MaximizeRestoreButton"].Attribute("Click"));
        Assert.Equal("Close_Click", (string?)buttons["CloseButton"].Attribute("Click"));
    }

    [Fact]
    public void Dashboard_binds_one_way_goal_and_accessible_six_month_history_cells()
    {
        string project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WordFlow.App"));
        var document = XDocument.Load(Path.Combine(project, "Views", "ControlCenterWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var progress = document.Descendants(presentation + "ProgressBar")
            .Single(element => ((string?)element.Attribute("Value"))?.Contains("LearningHistory.GoalProgressRatio", StringComparison.Ordinal) == true);
        Assert.Contains("Mode=OneWay", (string?)progress.Attribute("Value"));

        var history = document.Descendants(presentation + "ItemsControl")
            .Single(element => (string?)element.Attribute("ItemsSource") == "{Binding LearningHistory.Days}");
        Assert.Contains(history.Descendants(), element => (string?)element.Attribute("AutomationProperties.Name") == "{Binding AutomationName}");
        Assert.Contains(history.Descendants(), element => element.Name.LocalName == "WrapPanel" && (string?)element.Attribute("ItemWidth") is "14" or "15" or "16");
    }
}
