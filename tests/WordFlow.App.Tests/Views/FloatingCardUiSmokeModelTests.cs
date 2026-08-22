using WordFlow.App.Bootstrap;
using WordFlow.App.ViewModels;
using WordFlow.App.Views.Controls;
using WordFlow.Application.Shortcuts;
using System.Xml.Linq;

namespace WordFlow.App.Tests.Views;

public sealed class FloatingCardUiSmokeModelTests
{
    [Fact]
    public void Appearance_exposes_persisted_strong_topmost_without_fullscreen_suppression_path()
    {
        string project = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "WordFlow.App"));
        var controlCenter = XDocument.Load(Path.Combine(project, "Views", "ControlCenterWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var checkbox = controlCenter.Descendants(presentation + "CheckBox")
            .Single(element => (string?)element.Attribute("AutomationProperties.Name") == "悬浮卡始终置顶");
        string binding = (string?)checkbox.Attribute("IsChecked") ?? "";
        Assert.Contains("AlwaysOnTopEnabled", binding);
        Assert.Contains("Mode=TwoWay", binding);

        string cardCode = File.ReadAllText(Path.Combine(project, "Views", "FloatingCardWindow.xaml.cs"));
        Assert.Contains("StrongTopmostController", cardCode);
        Assert.DoesNotContain("fullscreenTimer", cardCode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsForegroundFullscreen", cardCode, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", cardCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Drawer_viewport_uses_at_most_five_measured_rows_and_available_work_area()
    {
        Assert.Equal(294, RelationDrawerViewport.Measure([52, 56, 58, 60, 64, 90], availableHeight: 500, rowGap: 1));
        Assert.Equal(168, RelationDrawerViewport.Measure([52, 56, 58, 60, 64, 90], availableHeight: 180, rowGap: 1));
        Assert.Equal(0, RelationDrawerViewport.Measure([], availableHeight: 180, rowGap: 1));
    }

    [Fact]
    public void Drawer_placement_is_downward_first_and_never_invents_height_outside_work_area()
    {
        var belowFits = RelationDrawerPlacementPlanner.Plan(desiredHeight: 340, belowAvailable: 360, aboveAvailable: 600);
        var flipAbove = RelationDrawerPlacementPlanner.Plan(desiredHeight: 340, belowAvailable: 220, aboveAvailable: 360);
        var constrained = RelationDrawerPlacementPlanner.Plan(desiredHeight: 340, belowAvailable: 92, aboveAvailable: 118);

        Assert.Equal(RelationDrawerDirection.Below, belowFits.Direction);
        Assert.Equal(340, belowFits.MaxHeight);
        Assert.Equal(RelationDrawerDirection.Above, flipAbove.Direction);
        Assert.Equal(340, flipAbove.MaxHeight);
        Assert.Equal(RelationDrawerDirection.Above, constrained.Direction);
        Assert.Equal(118, constrained.MaxHeight);
        Assert.True(constrained.UseCompactRows);
    }
    [Fact]
    public async Task Automation_fixture_opens_both_complete_drawers_without_changing_the_word()
    {
        using var viewModel = UiSmokeCardFactory.CreateViewModel();
        await viewModel.InitializeAsync();
        var word = viewModel.Word;

        await viewModel.HandleShortcutAsync(ShortcutAction.ToggleSynonyms);
        Assert.True(viewModel.Synonyms.AllItems.Count > 5);
        await viewModel.HandleShortcutAsync(ShortcutAction.ToggleConfusables);

        Assert.True(viewModel.Confusables.AllItems.Count > 5);
        Assert.Equal(word, viewModel.Word);
    }

    [Fact]
    public async Task Production_automation_fixture_uses_the_truthful_unavailable_action_host()
    {
        using var viewModel = UiSmokeCardFactory.CreateViewModel(FloatingCardActionHost.Unavailable);
        await viewModel.InitializeAsync();

        Assert.False(viewModel.CanSpeakCurrentWord);
        Assert.False(viewModel.CanOpenCurrentDetails);
        Assert.Equal("功能将在对应离线模块就绪后可用", viewModel.DetailsAvailabilityHelp);

        viewModel.RequestCurrentDetailsFromSurface();

        Assert.Equal("功能将在对应离线模块就绪后可用", viewModel.AccessibleStatus);
    }
}
