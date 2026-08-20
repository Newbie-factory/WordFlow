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
