using WordFlow.App.Bootstrap;
using WordFlow.App.Views.Controls;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.Tests.Views;

public sealed class FloatingCardUiSmokeModelTests
{
    [Fact]
    public void Drawer_viewport_uses_at_most_five_measured_rows_and_available_work_area()
    {
        Assert.Equal(294, RelationDrawerViewport.Measure([52, 56, 58, 60, 64, 90], availableHeight: 500, rowGap: 1));
        Assert.Equal(180, RelationDrawerViewport.Measure([52, 56, 58, 60, 64, 90], availableHeight: 180, rowGap: 1));
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
}
