using System.ComponentModel;
using System.Runtime.CompilerServices;
using WordFlow.Application;

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
    internal RelationItemViewModel(RelationItemData data, Action<RelationActionRequestedEventArgs> publish) { Data = data; this.publish = publish; }
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
    public string AutomationName => $"{Word}；音标 {Phonetic}；中文 {Chinese}；{Badge}；辨析 {Contrast}；搭配 {Collocation}";
    public string SpeakAutomationName => $"播放 {Word} 离线发音";
    public string DetailsAutomationName => $"打开 {Word} 完整词条";
    public string AddAutomationName => $"将 {Word} 加入学习";
    public void RequestSpeak() => Publish(RelationActionKind.Speak);
    public void RequestDetails() => Publish(RelationActionKind.OpenDetails);
    public void RequestAddToLearning() => Publish(RelationActionKind.AddToLearning);
    private void Publish(RelationActionKind action)
    {
        if (!CanUseWordActions || WordId is not { } id) return;
        publish(new(action, id, Word));
    }
}

public sealed class RelationGroupViewModel : INotifyPropertyChanged
{
    private bool isExpanded;
    private IReadOnlyList<RelationItemViewModel> visibleItems;
    internal RelationGroupViewModel(string senseId, string partOfSpeech, string definition, IReadOnlyList<RelationItemViewModel> items, bool expanded)
    { SenseId = senseId; PartOfSpeech = partOfSpeech; Definition = definition; Items = items; visibleItems = items; isExpanded = expanded; }
    public string SenseId { get; }
    public string PartOfSpeech { get; }
    public string Definition { get; }
    public IReadOnlyList<RelationItemViewModel> Items { get; }
    public IReadOnlyList<RelationItemViewModel> VisibleItems => visibleItems;
    public bool HasMatches => visibleItems.Count > 0;
    public string Header => string.IsNullOrWhiteSpace(Definition) ? PartOfSpeech : $"{PartOfSpeech} · {Definition}".Trim(' ', '·');
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
    private IReadOnlyList<RelationItemViewModel> allItems = [];
    private IReadOnlyList<RelationGroupViewModel> groups = [];
    private string searchText = "";
    private string? errorMessage;
    private bool isOpen;
    private bool isLoading;
    private Guid? wordId;
    private Guid? loadingWordId;
    private CancellationTokenSource? requestCancellation;
    private long generation;
    private bool disposed;

    public RelationDrawerViewModel(string title, string noResultsMessage, Func<Guid, CancellationToken, Task<UseCaseResult<IReadOnlyList<RelationItemData>>>> load)
    {
        Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("A drawer title is required.", nameof(title)) : title;
        NoResultsMessage = string.IsNullOrWhiteSpace(noResultsMessage) ? throw new ArgumentException("No-result copy is required.", nameof(noResultsMessage)) : noResultsMessage;
        this.load = load ?? throw new ArgumentNullException(nameof(load));
    }

    public string Title { get; }
    public string NoResultsMessage { get; }
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

    public async Task OpenAsync(Guid currentWordId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (currentWordId == Guid.Empty) throw new ArgumentException("A current word is required.", nameof(currentWordId));
        IsOpen = true;
        if (wordId == currentWordId && AllItems.Count > 0) return;
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
                    allItems = success.Value.Select(data => new RelationItemViewModel(data, Publish)).ToArray();
                    groups = allItems.GroupBy(item => (item.Data.SourceSenseId, item.Data.PartOfSpeech, item.Data.SourceDefinition))
                        .Select((group, index) => new RelationGroupViewModel(group.Key.SourceSenseId, group.Key.PartOfSpeech, group.Key.SourceDefinition, group.ToArray(), index == 0)).ToArray();
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

    public void Close() { InvalidateRequest(); IsOpen = false; IsLoading = false; }
    public void Reset()
    {
        InvalidateRequest(); IsOpen = false; IsLoading = false; searchText = ""; wordId = null; loadingWordId = null; allItems = []; groups = []; ErrorMessage = null;
        OnPropertyChanged(nameof(SearchText)); NotifyItemsChanged();
    }
    public void Dispose() { if (disposed) return; disposed = true; InvalidateRequest(); IsOpen = false; IsLoading = false; }
    private long InvalidateRequest() { generation++; requestCancellation?.Cancel(); requestCancellation?.Dispose(); requestCancellation = null; return generation; }
    private bool IsCurrent(long requestGeneration, Guid requestedWordId) => !disposed && IsOpen && generation == requestGeneration && loadingWordId == requestedWordId;
    private bool MatchesSearch(RelationItemViewModel item)
    {
        var query = SearchText.Trim();
        return new[] { item.Word, item.Phonetic, item.Chinese, item.Contrast, item.Collocation, item.Badge, item.Data.SourceDefinition, item.Data.PartOfSpeech, item.TargetSenseId }
            .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
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
