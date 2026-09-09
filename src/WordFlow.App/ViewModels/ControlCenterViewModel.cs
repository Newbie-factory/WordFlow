using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;
using WordFlow.Application.Relations;
using WordFlow.Domain.Learning;

namespace WordFlow.App.ViewModels;

public sealed record SlashedWordRow(Guid WordId, string Word, string Phonetic, string Chinese, DateTimeOffset? SlashedAt);
public sealed record DailyTrendPoint(DateOnly Day, int Reviewed);

public sealed class ControlCenterViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILearningProgressReader progressReader;
    private readonly SynchronizationContext? context;
    private readonly TimeProvider clock;
    private readonly IVocabularyRepository? vocabulary;
    private readonly GetConfusables? getConfusables;
    private readonly ILearningStore? learningStore;
    private readonly RestoreSlashedWords? restoreSlashedWords;
    private readonly GetNotebookEntries? getNotebookEntries;
    private readonly RemoveFromNotebook? removeFromNotebook;
    private readonly LearningDataChangeNotifier? dataChanges;
    private IReadOnlyList<VocabularyWord>? vocabularyCache;
    private int reviewedToday;
    private int slashedTotal;
    private bool loading;
    private string vocabularyQuery = "";
    private string confusableQuery = "";
    private string libraryStatus = "输入单词后搜索本地 IELTS 词库";
    private string confusableStatus = "输入单词后查看本地易混关系";
    private string slashedStatus = "尚未加载已斩词汇";
    private string notebookStatus = "尚未加载生词本";
    private int slashedPage = 1;
    private int slashedPageSize = 100;
    private int slashedCount;
    private SlashedWordRow? selectedSlashedWord;
    private bool dailyPlanSubscribed;
    private bool alwaysOnTopEnabled;
    private CancellationTokenSource? dataChangeRefreshCancellation;
    private bool disposed;

    public ControlCenterViewModel(
        DailyPlanSettingsViewModel dailyPlan,
        ShortcutSettingsViewModel shortcuts,
        PronunciationSettingsViewModel pronunciation,
        ThemeSettingsViewModel theme,
        ILearningProgressReader progressReader,
        SynchronizationContext? context = null,
        IVocabularyRepository? vocabulary = null,
        GetConfusables? getConfusables = null,
        ILearningStore? learningStore = null,
        RestoreSlashedWords? restoreSlashedWords = null,
        IDailyQueueStore? dailyQueueStore = null,
        TimeProvider? clock = null,
        bool alwaysOnTopEnabled = true,
        LearningDataChangeNotifier? dataChanges = null,
        GetNotebookEntries? getNotebookEntries = null,
        RemoveFromNotebook? removeFromNotebook = null)
    {
        DailyPlan = dailyPlan ?? throw new ArgumentNullException(nameof(dailyPlan));
        Shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
        Pronunciation = pronunciation ?? throw new ArgumentNullException(nameof(pronunciation));
        Theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.progressReader = progressReader ?? throw new ArgumentNullException(nameof(progressReader));
        this.context = context;
        this.clock = clock ?? TimeProvider.System;
        this.vocabulary = vocabulary;
        this.getConfusables = getConfusables;
        this.learningStore = learningStore;
        this.restoreSlashedWords = restoreSlashedWords;
        this.alwaysOnTopEnabled = alwaysOnTopEnabled;
        this.dataChanges = dataChanges;
        this.getNotebookEntries = getNotebookEntries;
        this.removeFromNotebook = removeFromNotebook;
        LearningHistory = dailyQueueStore is null ? null : new LearningHistoryViewModel(dailyQueueStore, this.clock, context);
        if (dataChanges is not null) dataChanges.CommittedDataChanged += OnCommittedDataChanged;
    }

    public DailyPlanSettingsViewModel DailyPlan { get; }
    public ShortcutSettingsViewModel Shortcuts { get; }
    public PronunciationSettingsViewModel Pronunciation { get; }
    public ThemeSettingsViewModel Theme { get; }
    public LearningHistoryViewModel? LearningHistory { get; }
    public ObservableCollection<VocabularyWord> VocabularyResults { get; } = [];
    public ObservableCollection<ConfusableItem> ConfusableResults { get; } = [];
    public ObservableCollection<SlashedWordRow> SlashedWords { get; } = [];
    public ObservableCollection<NotebookItem> NotebookItems { get; } = [];
    public ObservableCollection<DailyTrendPoint> SevenDayTrend { get; } = [];

    public int ReviewedToday { get => reviewedToday; private set => Set(ref reviewedToday, value); }
    public int SlashedTotal { get => slashedTotal; private set => Set(ref slashedTotal, value); }
    public int DailyTarget => DailyPlan.NewLimit + DailyPlan.SoftReviewLimit;
    public int RemainingToday => Math.Max(0, DailyTarget - ReviewedToday);
    public double ProgressRatio => DailyTarget == 0 ? 0 : Math.Clamp((double)ReviewedToday / DailyTarget, 0, 1);
    public int EstimatedMinutes => Math.Max(0, (int)Math.Ceiling(RemainingToday * 0.35));
    public bool IsLoading { get => loading; private set => Set(ref loading, value); }
    public string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "本地构建";
    public string OfflineStatus => "完全离线 · 数据仅保存在本机";
    public bool AlwaysOnTopEnabled { get => alwaysOnTopEnabled; set => Set(ref alwaysOnTopEnabled, value); }
    public string VocabularyQuery { get => vocabularyQuery; set => Set(ref vocabularyQuery, value); }
    public string ConfusableQuery { get => confusableQuery; set => Set(ref confusableQuery, value); }
    public string LibraryStatus { get => libraryStatus; private set => Set(ref libraryStatus, value); }
    public string ConfusableStatus { get => confusableStatus; private set => Set(ref confusableStatus, value); }
    public string SlashedStatus { get => slashedStatus; private set => Set(ref slashedStatus, value); }
    public string NotebookStatus { get => notebookStatus; private set => Set(ref notebookStatus, value); }
    public int SlashedPage { get => slashedPage; private set => Set(ref slashedPage, value); }
    public int SlashedPageSize { get => slashedPageSize; set { if (Set(ref slashedPageSize, value)) { SlashedPage = 1; Raise(nameof(SlashedRange)); } } }
    public int SlashedCount { get => slashedCount; private set { if (Set(ref slashedCount, value)) { Raise(nameof(SlashedRange)); Raise(nameof(SlashedPageCount)); } } }
    public int SlashedPageCount => Math.Max(1, (int)Math.Ceiling((double)SlashedCount / Math.Max(1, SlashedPageSize)));
    public string SlashedRange => SlashedCount == 0 ? "0 条" : $"第 {(SlashedPage - 1) * SlashedPageSize + 1}–{Math.Min(SlashedPage * SlashedPageSize, SlashedCount)} 条，共 {SlashedCount} 条";
    public SlashedWordRow? SelectedSlashedWord { get => selectedSlashedWord; set => Set(ref selectedSlashedWord, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await DailyPlan.RestoreAsync(ct).ConfigureAwait(false);
        await Pronunciation.RestoreAsync(ct).ConfigureAwait(false);
        await RefreshProgressAsync(ct).ConfigureAwait(false);
        await LoadNotebookAsync(ct).ConfigureAwait(false);
        if (!dailyPlanSubscribed)
        {
            DailyPlan.PropertyChanged += OnDailyPlanChanged;
            dailyPlanSubscribed = true;
        }
    }

    public async Task RefreshProgressAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            Task historyLoad = LearningHistory?.LoadAsync(ct) ?? Task.CompletedTask;
            DateOnly today = GetDashboardDay(clock);
            var snapshot = await progressReader.GetDailyStatisticsAsync(today, ct).ConfigureAwait(false);
            var trend = new List<DailyTrendPoint>();
            for (int offset = 6; offset >= 0; offset--)
            {
                DateOnly day = today.AddDays(-offset);
                var point = await progressReader.GetDailyStatisticsAsync(day, ct).ConfigureAwait(false);
                trend.Add(new DailyTrendPoint(day, point.ReviewedToday));
            }
            Publish(() =>
            {
                ReviewedToday = snapshot.ReviewedToday;
                SlashedTotal = snapshot.SlashedTotal;
                SevenDayTrend.Clear();
                foreach (var point in trend) SevenDayTrend.Add(point);
                Raise(nameof(RemainingToday)); Raise(nameof(ProgressRatio)); Raise(nameof(EstimatedMinutes));
            });
            await historyLoad.ConfigureAwait(false);
        }
        finally { Publish(() => IsLoading = false); }
    }

    public Task SaveDailyPlanAsync(CancellationToken ct = default) => DailyPlan.SaveAsync(ct);

    public async Task SearchVocabularyAsync(CancellationToken ct = default)
    {
        if (vocabulary is null) { LibraryStatus = "词库服务尚未连接"; return; }
        string query = VocabularyQuery.Trim();
        if (query.Length == 0) { VocabularyResults.Clear(); LibraryStatus = "请输入英文单词或中文释义"; return; }
        var all = await GetVocabularyAsync(ct).ConfigureAwait(false);
        var matches = all.Where(word => word.Lemma.Contains(query, StringComparison.OrdinalIgnoreCase)
                || word.Chinese.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(200).ToArray();
        Publish(() =>
        {
            VocabularyResults.Clear();
            foreach (var word in matches) VocabularyResults.Add(word);
            LibraryStatus = matches.Length == 0 ? "本地词库中未找到匹配词条" : $"找到 {matches.Length} 条（最多显示 200 条）";
        });
    }

    public async Task SearchConfusablesAsync(CancellationToken ct = default)
    {
        if (vocabulary is null || getConfusables is null) { ConfusableStatus = "易混词服务尚未连接"; return; }
        string query = ConfusableQuery.Trim();
        var all = await GetVocabularyAsync(ct).ConfigureAwait(false);
        var word = all.FirstOrDefault(item => string.Equals(item.Lemma, query, StringComparison.OrdinalIgnoreCase));
        if (word is null) { ConfusableResults.Clear(); ConfusableStatus = "请先输入完整、正确的英文单词"; return; }
        var result = await getConfusables.HandleAsync(new GetConfusablesRequest(word.WordId), ct).ConfigureAwait(false);
        Publish(() =>
        {
            ConfusableResults.Clear();
            if (result is Success<IReadOnlyList<ConfusableItem>> success)
            {
                foreach (var item in success.Value) ConfusableResults.Add(item);
                ConfusableStatus = success.Value.Count == 0 ? "暂无可靠易混词" : $"{word.Lemma} · {success.Value.Count} 条已验证关系";
            }
            else ConfusableStatus = result switch { StorageFailure<IReadOnlyList<ConfusableItem>> failure => failure.Message, NotFound<IReadOnlyList<ConfusableItem>> missing => missing.Message, _ => "无法读取易混关系" };
        });
    }

    public async Task LoadSlashedPageAsync(CancellationToken ct = default)
    {
        if (learningStore is null || vocabulary is null) { SlashedStatus = "学习记录服务尚未连接"; return; }
        var allCards = new List<CardState>();
        int offset = 0;
        while (true)
        {
            var page = await learningStore.GetCardsAsync(new PageRequest(offset, 500), ct).ConfigureAwait(false);
            allCards.AddRange(page.Items);
            offset += page.Items.Count;
            if (page.HasMore != true || page.Items.Count == 0) break;
        }
        var slashed = allCards.Where(card => card.Slash.IsSlashed).OrderByDescending(card => card.Slash.SlashedAt).ToArray();
        SlashedCount = slashed.Length;
        int skip = Math.Max(0, (SlashedPage - 1) * SlashedPageSize);
        var rows = new List<SlashedWordRow>();
        foreach (var card in slashed.Skip(skip).Take(SlashedPageSize))
        {
            var word = await vocabulary.GetWordAsync(card.Id, ct).ConfigureAwait(false);
            rows.Add(new SlashedWordRow(card.Id, word?.Lemma ?? card.Id.ToString("D"), word?.Phonetic ?? "", word?.Chinese ?? "", card.Slash.SlashedAt));
        }
        Publish(() =>
        {
            SlashedWords.Clear();
            foreach (var row in rows) SlashedWords.Add(row);
            SlashedStatus = SlashedCount == 0 ? "还没有已斩词汇" : SlashedRange;
        });
    }

    public async Task ChangeSlashedPageAsync(int delta, CancellationToken ct = default)
    {
        int next = Math.Clamp(SlashedPage + delta, 1, SlashedPageCount);
        if (next == SlashedPage) return;
        SlashedPage = next; Raise(nameof(SlashedRange));
        await LoadSlashedPageAsync(ct).ConfigureAwait(false);
    }

    public async Task RestoreSelectedSlashedAsync(CancellationToken ct = default)
    {
        if (SelectedSlashedWord is null || learningStore is null || restoreSlashedWords is null) { SlashedStatus = "请先选择一个已斩词汇"; return; }
        var projection = await learningStore.GetCardProjectionAsync(SelectedSlashedWord.WordId, ct).ConfigureAwait(false);
        if (projection is null) { SlashedStatus = "该词的学习状态不存在"; return; }
        var result = await restoreSlashedWords.HandleAsync(new RestoreSlashedWordRequest(Guid.NewGuid(), Guid.NewGuid(), projection.Card.Id, projection.Revision, RestoreMode.Scheduled), ct).ConfigureAwait(false);
        SlashedStatus = result is Success<CardState> ? $"已取消斩：{SelectedSlashedWord.Word}" : "取消斩失败，请刷新后重试";
        if (result is Success<CardState>)
        {
            await LoadSlashedPageAsync(ct).ConfigureAwait(false);
            await RefreshProgressAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task LoadNotebookAsync(CancellationToken ct = default)
    {
        if (getNotebookEntries is null) { NotebookStatus = "生词本服务尚未连接"; return; }
        var result = await getNotebookEntries.HandleAsync(ct).ConfigureAwait(false);
        Publish(() =>
        {
            NotebookItems.Clear();
            if (result is Success<IReadOnlyList<NotebookItem>> success)
            {
                foreach (var item in success.Value) NotebookItems.Add(item);
                NotebookStatus = success.Value.Count == 0 ? "生词本还是空的" : $"共 {success.Value.Count} 个生词";
            }
            else NotebookStatus = result switch
            {
                StorageFailure<IReadOnlyList<NotebookItem>> failure => failure.Message,
                _ => "无法读取生词本",
            };
        });
    }

    public async Task RemoveNotebookEntryAsync(Guid wordId, CancellationToken ct = default)
    {
        if (removeFromNotebook is null) { NotebookStatus = "生词本服务尚未连接"; return; }
        var result = await removeFromNotebook.HandleAsync(new RemoveFromNotebookRequest(wordId), ct).ConfigureAwait(false);
        if (result is Success<bool>)
        {
            await LoadNotebookAsync(ct).ConfigureAwait(false);
            NotebookStatus = "已从生词本移除";
        }
        else NotebookStatus = result switch
        {
            StorageFailure<bool> failure => $"移除失败：{failure.Message}",
            _ => "移除失败，请重试",
        };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (dataChanges is not null) dataChanges.CommittedDataChanged -= OnCommittedDataChanged;
        var cancellation = Interlocked.Exchange(ref dataChangeRefreshCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
        if (dailyPlanSubscribed) DailyPlan.PropertyChanged -= OnDailyPlanChanged;
        Shortcuts.Dispose();
        Pronunciation.Dispose();
    }

    private async Task<IReadOnlyList<VocabularyWord>> GetVocabularyAsync(CancellationToken ct)
    {
        if (vocabularyCache is not null) return vocabularyCache;
        if (vocabulary is null) return [];
        var words = new List<VocabularyWord>();
        int offset = 0;
        while (true)
        {
            var page = await vocabulary.GetWordsAsync(new PageRequest(offset, 500), ct).ConfigureAwait(false);
            words.AddRange(page.Items.Where(item => item.IsLearningHeadword));
            offset += page.Items.Count;
            if (page.HasMore != true || page.Items.Count == 0) break;
        }
        vocabularyCache = words;
        return words;
    }

    private void OnDailyPlanChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DailyPlanSettingsViewModel.NewLimit) or nameof(DailyPlanSettingsViewModel.SoftReviewLimit) or nameof(DailyPlanSettingsViewModel.Plan))
        { Raise(nameof(DailyTarget)); Raise(nameof(RemainingToday)); Raise(nameof(ProgressRatio)); Raise(nameof(EstimatedMinutes)); }
    }

    private void OnCommittedDataChanged(object? sender, EventArgs args)
    {
        if (disposed) return;
        if (context is not null && context != SynchronizationContext.Current)
        {
            context.Post(_ => ScheduleDataChangeRefresh(), null);
            return;
        }
        ScheduleDataChangeRefresh();
    }

    private void ScheduleDataChangeRefresh()
    {
        if (disposed) return;
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref dataChangeRefreshCancellation, next);
        previous?.Cancel();
        _ = RefreshAfterDataChangeAsync(next);
    }

    private async Task RefreshAfterDataChangeAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(75), cancellation.Token);
            await RefreshProgressAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch { }
        finally
        {
            Interlocked.CompareExchange(ref dataChangeRefreshCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    internal static DateOnly GetDashboardDay(TimeProvider clock) =>
        DateOnly.FromDateTime((clock ?? throw new ArgumentNullException(nameof(clock))).GetLocalNow().DateTime);

    private void Publish(Action action)
    {
        if (disposed) return;
        if (context is null || context == SynchronizationContext.Current) action();
        else context.Post(_ => { if (!disposed) action(); }, null);
    }
    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    private void Raise(string? name) => PropertyChanged?.Invoke(this, new(name));
}
