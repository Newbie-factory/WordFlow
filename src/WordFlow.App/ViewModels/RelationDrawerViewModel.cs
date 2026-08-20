using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using WordFlow.Application;
using WordFlow.Application.Learning;

namespace WordFlow.App.ViewModels;

public enum RelationActionKind { Speak, OpenDetails, AddToLearning }

public sealed record RelationItemData(Guid? WordId, string Word, string Phonetic, string Chinese,
    string Contrast, string Collocation, string Badge, bool IsCorrectionOnly,
    string SourceSenseId = "", string PartOfSpeech = "", string SourceDefinition = "", string TargetSenseId = "");

public sealed class RelationActionRequestedEventArgs(RelationActionKind action, Guid wordId, string word) : EventArgs
{
    public RelationActionKind Action { get; } = action;
    public Guid WordId { get; } = wordId;
    public string Word { get; } = word;
}

public sealed class RelationItemViewModel
{
    private readonly Action<RelationActionRequestedEventArgs> publish;
    private readonly IFloatingCardActionHost actionHost;
    internal RelationItemViewModel(RelationItemData data, Action<RelationActionRequestedEventArgs> publish, IFloatingCardActionHost actionHost)
    { Data = data; this.publish = publish; this.actionHost = actionHost; }
    internal RelationItemData Data { get; }
    public Guid? WordId => Data.WordId;
    public string Word => Data.Word;
    public string Phonetic => Data.Phonetic;
    public string Chinese => Data.Chinese;
    public string Contrast => string.IsNullOrWhiteSpace(Data.Contrast) ? "暂无离线辨析说明" : Data.Contrast;
    public string Collocation => string.IsNullOrWhiteSpace(Data.Collocation) ? "暂无离线搭配" : Data.Collocation;
    public string Badge => Data.Badge;
    public string TargetSenseId => Data.TargetSenseId;
    public bool IsCorrectionOnly => Data.IsCorrectionOnly;
    public bool CanUseWordActions => !IsCorrectionOnly && WordId.HasValue;
    public bool CanSpeak => CanUseWordActions && actionHost.Capability(RelationActionKind.Speak).IsAvailable;
    public bool CanOpenDetails => CanUseWordActions && actionHost.Capability(RelationActionKind.OpenDetails).IsAvailable;
    public bool CanAddToLearning => CanUseWordActions && actionHost.Capability(RelationActionKind.AddToLearning).IsAvailable;
    public string SpeakHelpText => IsCorrectionOnly ? "误拼仅用于纠正" : actionHost.Capability(RelationActionKind.Speak).HelpText;
    public string DetailsHelpText => IsCorrectionOnly ? "误拼仅用于纠正" : actionHost.Capability(RelationActionKind.OpenDetails).HelpText;
    public string AddHelpText => IsCorrectionOnly ? "误拼仅用于纠正" : actionHost.Capability(RelationActionKind.AddToLearning).HelpText;
    public string AutomationName => $"{Word}；音标 {Phonetic}；中文 {Chinese}；{Badge}；辨析 {Contrast}；搭配 {Collocation}";
    public string SpeakAutomationName => $"播放 {Word} 离线发音";
    public string DetailsAutomationName => $"打开 {Word} 完整词条";
    public string AddAutomationName => $"将 {Word} 加入学习";
    public void RequestSpeak() => Publish(RelationActionKind.Speak);
    public void RequestDetails() => Publish(RelationActionKind.OpenDetails);
    public void RequestAddToLearning() => Publish(RelationActionKind.AddToLearning);
    private void Publish(RelationActionKind action)
    {
        if (!CanUseWordActions || !actionHost.Capability(action).IsAvailable || WordId is not { } id) return;
        publish(new(action, id, Word));
    }
}

public sealed class RelationGroupViewModel : INotifyPropertyChanged
{
    private bool isExpanded;
    private IReadOnlyList<RelationItemViewModel> visibleItems;
    internal RelationGroupViewModel(string senseId, string partOfSpeech, string definition, IReadOnlyList<RelationItemViewModel> items, bool expanded, bool isFallbackPrimary)
    { SenseId = senseId; PartOfSpeech = partOfSpeech; Definition = definition; Items = items; visibleItems = items; isExpanded = expanded; IsFallbackPrimary = isFallbackPrimary; }
    public string SenseId { get; }
    public string PartOfSpeech { get; }
    public string Definition { get; }
    public IReadOnlyList<RelationItemViewModel> Items { get; }
    public IReadOnlyList<RelationItemViewModel> VisibleItems => visibleItems;
    public bool HasMatches => visibleItems.Count > 0;
    public bool IsFallbackPrimary { get; }
    public string Header
    {
        get
        {
            var core = string.IsNullOrWhiteSpace(Definition) ? PartOfSpeech : $"{PartOfSpeech} · {Definition}".Trim(' ', '·');
            return IsFallbackPrimary ? $"{core} · 默认" : core;
        }
    }
    public bool IsExpanded { get => isExpanded; set { if (isExpanded == value) return; isExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void ApplyFilter(Func<RelationItemViewModel, bool>? predicate)
    {
        visibleItems = predicate is null ? Items : Items.Where(predicate).ToArray();
        PropertyChanged?.Invoke(this, new(nameof(VisibleItems)));
        PropertyChanged?.Invoke(this, new(nameof(HasMatches)));
        if (predicate is not null && HasMatches) IsExpanded = true;
    }
}

public sealed class RelationDrawerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly Func<Guid, CancellationToken, Task<UseCaseResult<IReadOnlyList<RelationItemData>>>> load;
    private readonly IFloatingCardActionHost actionHost;
    private readonly bool usesSenseGroups;
    private IReadOnlyList<RelationItemViewModel> allItems = [];
    private IReadOnlyList<RelationGroupViewModel> groups = [];
    private string searchText = "";
    private string? errorMessage;
    private bool isOpen;
    private bool isLoading;
    private Guid? wordId;
    private CurrentPrimarySense? loadedPrimarySense;
    private Guid? loadingWordId;
    private CancellationTokenSource? requestCancellation;
    private long generation;
    private bool disposed;

    public RelationDrawerViewModel(string title, string noResultsMessage, Func<Guid, CancellationToken, Task<UseCaseResult<IReadOnlyList<RelationItemData>>>> load,
        IFloatingCardActionHost? actionHost = null, bool usesSenseGroups = false)
    {
        Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("A drawer title is required.", nameof(title)) : title;
        NoResultsMessage = string.IsNullOrWhiteSpace(noResultsMessage) ? throw new ArgumentException("No-result copy is required.", nameof(noResultsMessage)) : noResultsMessage;
        this.load = load ?? throw new ArgumentNullException(nameof(load));
        this.actionHost = actionHost ?? FloatingCardActionHost.Unavailable;
        this.usesSenseGroups = usesSenseGroups;
    }

    public string Title { get; }
    public string NoResultsMessage { get; }
    public bool UsesSenseGroups => usesSenseGroups;
    public Guid? CurrentWordId => wordId;
    public IReadOnlyList<RelationItemViewModel> AllItems => allItems;
    public IReadOnlyList<RelationGroupViewModel> Groups => groups;
    public IReadOnlyList<RelationItemViewModel> FilteredItems => string.IsNullOrWhiteSpace(SearchText) ? AllItems : AllItems.Where(MatchesSearch).ToArray();
    public bool HasMoreThanFiveItems => AllItems.Count > 5;
    public bool IsOpen { get => isOpen; private set => SetField(ref isOpen, value); }
    public bool IsLoading { get => isLoading; private set => SetField(ref isLoading, value); }
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetField(ref searchText, value ?? "")) return;
            foreach (var group in groups) group.ApplyFilter(string.IsNullOrWhiteSpace(searchText) ? null : MatchesSearch);
            OnPropertyChanged(nameof(FilteredItems));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }
    public string? ErrorMessage { get => errorMessage; private set => SetField(ref errorMessage, value); }
    public string? EmptyMessage => !IsLoading && FilteredItems.Count == 0 ? string.IsNullOrWhiteSpace(SearchText) ? NoResultsMessage : "没有匹配的关系词" : null;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<RelationActionRequestedEventArgs>? ActionRequested;

    public async Task OpenAsync(Guid currentWordId, CurrentPrimarySense? primarySense = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (currentWordId == Guid.Empty) throw new ArgumentException("A current word is required.", nameof(currentWordId));
        IsOpen = true;
        if (wordId == currentWordId && Equals(loadedPrimarySense, primarySense) && AllItems.Count > 0) return;
        var requestGeneration = InvalidateRequest();
        loadingWordId = currentWordId;
        requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var requestToken = requestCancellation.Token;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await load(currentWordId, requestToken).ConfigureAwait(true);
            if (!IsCurrent(requestGeneration, currentWordId)) return;
            switch (result)
            {
                case Success<IReadOnlyList<RelationItemData>> success:
                    wordId = currentWordId;
                    loadedPrimarySense = primarySense;
                    allItems = success.Value.Select(data => new RelationItemViewModel(data, Publish, actionHost)).ToArray();
                    groups = usesSenseGroups ? BuildGroups(allItems, primarySense) : [];
                    searchText = "";
                    OnPropertyChanged(nameof(SearchText));
                    NotifyItemsChanged();
                    break;
                case StorageFailure<IReadOnlyList<RelationItemData>> failure: ErrorMessage = $"无法加载关系词：{failure.Message}"; break;
                case NotFound<IReadOnlyList<RelationItemData>> failure: ErrorMessage = $"无法加载关系词：{failure.Message}"; break;
                case Conflict<IReadOnlyList<RelationItemData>> failure: ErrorMessage = $"无法加载关系词：{failure.Message}"; break;
            }
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested) { }
        catch (Exception exception) when (IsCurrent(requestGeneration, currentWordId)) { ErrorMessage = $"无法加载关系词：{exception.Message}"; }
        finally { if (IsCurrent(requestGeneration, currentWordId)) { IsLoading = false; OnPropertyChanged(nameof(EmptyMessage)); } }
    }

    public Task OpenAsync(Guid currentWordId, CancellationToken ct) => OpenAsync(currentWordId, null, ct);

    public void Close() { InvalidateRequest(); IsOpen = false; IsLoading = false; }
    public void Reset()
    {
        InvalidateRequest(); IsOpen = false; IsLoading = false; searchText = ""; wordId = null; loadedPrimarySense = null; loadingWordId = null; allItems = []; groups = []; ErrorMessage = null;
        OnPropertyChanged(nameof(SearchText)); NotifyItemsChanged();
    }
    public void Dispose() { if (disposed) return; disposed = true; InvalidateRequest(); IsOpen = false; IsLoading = false; }
    private long InvalidateRequest() { generation++; requestCancellation?.Cancel(); requestCancellation?.Dispose(); requestCancellation = null; return generation; }
    private bool IsCurrent(long requestGeneration, Guid requestedWordId) => !disposed && IsOpen && generation == requestGeneration && loadingWordId == requestedWordId;
    private bool MatchesSearch(RelationItemViewModel item)
    {
        var query = Normalize(SearchText.Trim());
        return new[] { item.Word, item.Phonetic, item.Chinese, item.Contrast, item.Collocation, item.Badge, item.Data.SourceDefinition, item.Data.PartOfSpeech, item.TargetSenseId }
            .Any(value => Normalize(value).Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
    private IReadOnlyList<RelationGroupViewModel> BuildGroups(IReadOnlyList<RelationItemViewModel> items, CurrentPrimarySense? primarySense)
    {
        var grouped = items.GroupBy(item => (item.Data.SourceSenseId, item.Data.PartOfSpeech, item.Data.SourceDefinition))
            .Select(group => new { group.Key, Items = (IReadOnlyList<RelationItemViewModel>)group.ToArray() }).ToArray();
        if (grouped.Length == 0) return [];
        var matching = primarySense is null ? null : grouped.FirstOrDefault(group =>
            (!string.IsNullOrWhiteSpace(primarySense.SenseId) && string.Equals(group.Key.SourceSenseId, primarySense.SenseId, StringComparison.Ordinal)) ||
            (Normalize(group.Key.SourceDefinition) == Normalize(primarySense.Definition) && Normalize(group.Key.PartOfSpeech) == Normalize(primarySense.PartOfSpeech)));
        bool fallback = primarySense is not null && (primarySense.IsDeterministicFallback || matching is null);
        var primary = matching ?? grouped.OrderBy(group => group.Key.SourceSenseId, StringComparer.Ordinal)
            .ThenBy(group => group.Key.PartOfSpeech, StringComparer.Ordinal).ThenBy(group => group.Key.SourceDefinition, StringComparer.Ordinal).First();
        return grouped.OrderByDescending(group => ReferenceEquals(group, primary)).ThenBy(group => group.Key.SourceSenseId, StringComparer.Ordinal)
            .Select(group => new RelationGroupViewModel(group.Key.SourceSenseId, group.Key.PartOfSpeech, group.Key.SourceDefinition, group.Items,
                ReferenceEquals(group, primary), ReferenceEquals(group, primary) && fallback)).ToArray();
    }
    private static string Normalize(string? value) => (value ?? "").Normalize(NormalizationForm.FormC);
    private void Publish(RelationActionRequestedEventArgs args) => ActionRequested?.Invoke(this, args);
    private void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(AllItems)); OnPropertyChanged(nameof(FilteredItems)); OnPropertyChanged(nameof(HasMoreThanFiveItems)); OnPropertyChanged(nameof(EmptyMessage)); OnPropertyChanged(nameof(Groups));
    }
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(propertyName); if (propertyName is nameof(IsLoading)) OnPropertyChanged(nameof(EmptyMessage)); return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}
