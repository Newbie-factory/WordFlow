using WordFlow.App.ViewModels;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;

namespace WordFlow.App.Tests.ViewModels;

public sealed class RelationDrawerViewModelTests
{
    [Fact]
    public async Task Stale_completion_cannot_overwrite_the_new_word_after_reset_and_reopen()
    {
        var first = new TaskCompletionSource<UseCaseResult<IReadOnlyList<RelationItemData>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<UseCaseResult<IReadOnlyList<RelationItemData>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词", (wordId, _) => wordId == Id(1) ? first.Task : second.Task);

        var oldLoad = drawer.OpenAsync(Id(1));
        drawer.Reset();
        var newLoad = drawer.OpenAsync(Id(2));
        second.SetResult(new Success<IReadOnlyList<RelationItemData>>([Row(Id(22), "new")]));
        await newLoad;
        first.SetResult(new Success<IReadOnlyList<RelationItemData>>([Row(Id(11), "stale")]));
        await oldLoad;

        Assert.True(drawer.IsOpen);
        Assert.Equal(Id(2), drawer.CurrentWordId);
        Assert.Equal("new", Assert.Single(drawer.AllItems).Word);
        Assert.False(drawer.IsLoading);
    }

    [Fact]
    public async Task Close_and_dispose_invalidate_ignored_cancellation_completions()
    {
        var pending = new TaskCompletionSource<UseCaseResult<IReadOnlyList<RelationItemData>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词", (_, _) => pending.Task);
        var load = drawer.OpenAsync(Id(1));

        drawer.Close();
        drawer.Dispose();
        pending.SetResult(new Success<IReadOnlyList<RelationItemData>>([Row(Id(2), "late")]));
        await load;

        Assert.False(drawer.IsOpen);
        Assert.Empty(drawer.AllItems);
    }

    [Fact]
    public async Task Sense_groups_keep_primary_open_and_search_reveals_folded_evidence()
    {
        var rows = new[]
        {
            Row(Id(2), "primary") with { SourceSenseId = "sense-a", PartOfSpeech = "n", SourceDefinition = "primary definition" },
            Row(Id(3), "folded") with { SourceSenseId = "sense-b", PartOfSpeech = "v", SourceDefinition = "secondary evidence" },
        };
        using var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(rows)), usesSenseGroups: true);

        await drawer.OpenAsync(Id(1));

        Assert.True(drawer.Groups[0].IsExpanded);
        Assert.False(drawer.Groups[1].IsExpanded);
        drawer.SearchText = "secondary evidence";
        Assert.True(drawer.Groups[1].IsExpanded);
        Assert.Equal("folded", Assert.Single(drawer.FilteredItems).Word);
    }

    [Fact]
    public async Task Non_sense_drawer_exposes_one_flat_result_list_without_empty_semantic_groups()
    {
        var rows = new[] { Row(Id(2), "methodical"), Row(Id(3), "metallic") };
        using var drawer = new RelationDrawerViewModel("形近易混", "暂无可靠易混词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(rows)), usesSenseGroups: false);

        await drawer.OpenAsync(Id(1));

        Assert.False(drawer.UsesSenseGroups);
        Assert.Empty(drawer.Groups);
        Assert.Equal(["methodical", "metallic"], drawer.FilteredItems.Select(item => item.Word));
    }

    [Fact]
    public async Task Matching_primary_sense_is_expanded_even_when_groups_arrive_shuffled()
    {
        var rows = new[]
        {
            Row(Id(3), "secondary") with { SourceSenseId = "sense-z", PartOfSpeech = "v", SourceDefinition = "secondary" },
            Row(Id(2), "primary") with { SourceSenseId = "sense-a", PartOfSpeech = "n", SourceDefinition = "actual primary" },
            Row(Id(4), "other evidence") with { SourceSenseId = "sense-a", PartOfSpeech = "n", SourceDefinition = "actual primary" },
        };
        using var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(rows)), usesSenseGroups: true);

        await drawer.OpenAsync(Id(1), new CurrentPrimarySense("sense-a", "actual primary", "n", false));

        Assert.Equal("sense-a", drawer.Groups[0].SenseId);
        Assert.True(drawer.Groups[0].IsExpanded);
        Assert.Equal(2, drawer.Groups[0].Items.Count);
        Assert.False(drawer.Groups[0].IsFallbackPrimary);
        Assert.False(drawer.Groups[1].IsExpanded);
    }

    [Fact]
    public async Task Missing_primary_sense_uses_a_deterministic_visibly_marked_fallback()
    {
        var rows = new[]
        {
            Row(Id(2), "z") with { SourceSenseId = "sense-z", PartOfSpeech = "v", SourceDefinition = "z definition" },
            Row(Id(3), "a") with { SourceSenseId = "sense-a", PartOfSpeech = "n", SourceDefinition = "a definition" },
        };
        using var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(rows)), usesSenseGroups: true);

        await drawer.OpenAsync(Id(1), new CurrentPrimarySense("missing", "primary definition", "n", true));

        Assert.Equal("sense-a", drawer.Groups[0].SenseId);
        Assert.True(drawer.Groups[0].IsFallbackPrimary);
        Assert.Contains("默认", drawer.Groups[0].Header);
    }

    [Fact]
    public async Task Projected_fallback_flag_remains_visible_when_its_deterministic_sense_id_matches()
    {
        var rows = new[] { Row(Id(2), "fallback") with { SourceSenseId = "sense-a", PartOfSpeech = "n", SourceDefinition = "fallback evidence" } };
        using var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(rows)), usesSenseGroups: true);

        await drawer.OpenAsync(Id(1), new CurrentPrimarySense("sense-a", "curated primary", "v", true));

        Assert.True(drawer.Groups[0].IsFallbackPrimary);
        Assert.Contains("默认", drawer.Groups[0].Header);
    }

    [Fact]
    public async Task Search_normalizes_query_and_all_data_to_unicode_form_c()
    {
        var row = Row(Id(2), "café") with { SourceDefinition = "résumé" };
        using var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>([row])));
        await drawer.OpenAsync(Id(1));

        drawer.SearchText = "cafe\u0301";

        Assert.Equal("café", Assert.Single(drawer.FilteredItems).Word);
    }
    [Fact]
    public async Task Complete_results_are_preserved_while_search_filters_every_visible_detail()
    {
        var rows = Enumerable.Range(1, 12)
            .Select(index => new RelationItemData(
                Id(index), $"word-{index}", $"/fəˈnetɪk-{index}/", $"中文释义 {index}",
                $"contrast {index}", $"collocation {index}", index % 2 == 0 ? "同词根" : "形近",
                IsCorrectionOnly: false))
            .ToArray();
        var drawer = new RelationDrawerViewModel(
            "形近易混", "暂无可靠易混词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>(rows)));

        await drawer.OpenAsync(Id(99));
        drawer.SearchText = "中文释义 11";

        Assert.Equal(12, drawer.AllItems.Count);
        var match = Assert.Single(drawer.FilteredItems);
        Assert.Equal("word-11", match.Word);
        Assert.True(drawer.HasMoreThanFiveItems);
        Assert.Null(drawer.EmptyMessage);
    }

    [Theory]
    [InlineData("近义辨析", "暂无可靠近义词")]
    [InlineData("形近易混", "暂无可靠易混词")]
    public async Task Empty_success_uses_the_exact_approved_no_result_copy(string title, string expected)
    {
        var drawer = new RelationDrawerViewModel(title, expected,
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>([])));

        await drawer.OpenAsync(Id(1));

        Assert.Equal(expected, drawer.EmptyMessage);
        Assert.Empty(drawer.AllItems);
    }

    [Fact]
    public async Task Relation_actions_publish_view_neutral_requests_and_misspellings_are_correction_only()
    {
        var target = new RelationItemData(Id(2), "stimulate", "/ˈstɪmjuleɪt/", "刺激", "区别", "stimulate growth", "形近", false);
        var typo = new RelationItemData(null, "stimuate → stimulate", "", "拼写纠正", "错误拼写，只用于纠正", "", "误拼纠正", true);
        var host = new FloatingCardActionHost(
            new FakeActionPort(), new FakeActionPort(), new FakeActionPort());
        var drawer = new RelationDrawerViewModel("形近易混", "暂无可靠易混词",
            (_, _) => Task.FromResult<UseCaseResult<IReadOnlyList<RelationItemData>>>(new Success<IReadOnlyList<RelationItemData>>([target, typo])), host);
        var requests = new List<RelationActionRequestedEventArgs>();
        drawer.ActionRequested += (_, args) => requests.Add(args);
        await drawer.OpenAsync(Id(1));

        drawer.AllItems[0].RequestSpeak();
        drawer.AllItems[0].RequestDetails();
        drawer.AllItems[0].RequestAddToLearning();
        drawer.AllItems[1].RequestDetails();

        Assert.Equal(
            [RelationActionKind.Speak, RelationActionKind.OpenDetails, RelationActionKind.AddToLearning],
            requests.Select(request => request.Action));
        Assert.All(requests, request => Assert.Equal(Id(2), request.WordId));
        Assert.True(drawer.AllItems[1].IsCorrectionOnly);
        Assert.False(drawer.AllItems[1].CanUseWordActions);
    }

    [Fact]
    public async Task Failure_is_announced_without_replacing_previous_results()
    {
        int calls = 0;
        var drawer = new RelationDrawerViewModel("近义辨析", "暂无可靠近义词", (_, _) =>
        {
            calls++;
            UseCaseResult<IReadOnlyList<RelationItemData>> result = calls == 1
                ? new Success<IReadOnlyList<RelationItemData>>([new(Id(2), "plain", "/pleɪn/", "清楚的", "", "", "近义", false)])
                : new StorageFailure<IReadOnlyList<RelationItemData>>("database busy");
            return Task.FromResult(result);
        });
        await drawer.OpenAsync(Id(1));

        await drawer.OpenAsync(Id(3));

        Assert.Equal("无法加载关系词：database busy", drawer.ErrorMessage);
        Assert.Single(drawer.AllItems);
    }

    private static Guid Id(int value) => new(value, 0, 0, new byte[8]);
    private static RelationItemData Row(Guid id, string word) =>
        new(id, word, "/test/", "释义", "辨析", "搭配", "近义", false);
    private sealed class FakeActionPort : IFloatingCardActionPort
    {
        public FloatingCardActionResult Execute(Guid wordId, string word) => FloatingCardActionResult.Completed($"completed {word}");
    }
}
