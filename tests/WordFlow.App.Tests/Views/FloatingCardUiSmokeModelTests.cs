using WordFlow.App.Bootstrap;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.Tests.Views;

public sealed class FloatingCardUiSmokeModelTests
{
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
