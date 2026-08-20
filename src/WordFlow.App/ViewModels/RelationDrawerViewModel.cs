using System.ComponentModel;
using System.Runtime.CompilerServices;
using WordFlow.Application;

namespace WordFlow.App.ViewModels;

public enum RelationActionKind
{
    Speak,
    OpenDetails,
    AddToLearning,
}

public sealed record RelationItemData(
    Guid? WordId,
    string Word,
    string Phonetic,
    string Chinese,
    string Contrast,
    string Collocation,
    string Badge,
    bool IsCorrectionOnly);

public sealed class RelationActionRequestedEventArgs(
    RelationActionKind action,
    Guid wordId,
    string word) : EventArgs
{
    public RelationActionKind Action { get; } = action;
    public Guid WordId { get; } = wordId;
    public string Word { get; } = word;
}

public sealed class RelationItemViewModel
{
    private readonly Action<RelationActionRequestedEventArgs> publish;

    internal RelationItemViewModel(RelationItemData data, Action<RelationActionRequestedEventArgs> publish)
    {
        Data = data;
        this.publish = publish;
    }

    internal RelationItemData Data { get; }
    public Guid? WordId => Data.WordId;
    public string Word => Data.Word;
    public string Phonetic => Data.Phonetic;
    public string Chinese => Data.Chinese;
    public string Contrast => string.IsNullOrWhiteSpace(Data.Contrast) ? "暂无离线辨析说明" : Data.Contrast;
    public string Collocation => string.IsNullOrWhiteSpace(Data.Collocation) ? "暂无离线搭配" : Data.Collocation;
    public string Badge => Data.Badge;
    public bool IsCorrectionOnly => Data.IsCorrectionOnly;
    public bool CanUseWordActions => !IsCorrectionOnly && WordId.HasValue;

    public void RequestSpeak() => Publish(RelationActionKind.Speak);
    public void RequestDetails() => Publish(RelationActionKind.OpenDetails);
    public void RequestAddToLearning() => Publish(RelationActionKind.AddToLearning);

    private void Publish(RelationActionKind action)
    {
        if (!CanUseWordActions || WordId is not { } id) return;
        publish(new(action, id, Word));
    }
}

public sealed class RelationDrawerViewModel : INotifyPropertyChanged
{
    private readonly Func<Guid, CancellationToken, Task<UseCaseResult<IReadOnlyList<RelationItemData>>>> load;
    private IReadOnlyList<RelationItemViewModel> allItems = [];
    private string searchText = "";
    private string? errorMessage;
    private bool isOpen;
    private bool isLoading;
    private Guid? wordId;

    public RelationDrawerViewModel(
        string title,
        string noResultsMessage,
        Func<Guid, CancellationToken, Task<UseCaseResult<IReadOnlyList<RelationItemData>>>> load)
    {
        Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("A drawer title is required.", nameof(title)) : title;
        NoResultsMessage = string.IsNullOrWhiteSpace(noResultsMessage)
            ? throw new ArgumentException("No-result copy is required.", nameof(noResultsMessage))
            : noResultsMessage;
        this.load = load ?? throw new ArgumentNullException(nameof(load));
    }

    public string Title { get; }
    public string NoResultsMessage { get; }
    public IReadOnlyList<RelationItemViewModel> AllItems => allItems;
    public IReadOnlyList<RelationItemViewModel> FilteredItems => string.IsNullOrWhiteSpace(SearchText)
        ? AllItems
        : AllItems.Where(MatchesSearch).ToArray();
    public bool HasMoreThanFiveItems => AllItems.Count > 5;
    public bool IsOpen
    {
        get => isOpen;
        private set => SetField(ref isOpen, value);
    }
    public bool IsLoading
    {
        get => isLoading;
        private set => SetField(ref isLoading, value);
    }
    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetField(ref searchText, value ?? "")) return;
            OnPropertyChanged(nameof(FilteredItems));
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }
    public string? ErrorMessage
    {
        get => errorMessage;
        private set => SetField(ref errorMessage, value);
    }
    public string? EmptyMessage => !IsLoading && FilteredItems.Count == 0
        ? string.IsNullOrWhiteSpace(SearchText) ? NoResultsMessage : "没有匹配的关系词"
        : null;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<RelationActionRequestedEventArgs>? ActionRequested;

    public async Task OpenAsync(Guid currentWordId, CancellationToken ct = default)
    {
        if (currentWordId == Guid.Empty) throw new ArgumentException("A current word is required.", nameof(currentWordId));
        IsOpen = true;
        if (wordId == currentWordId && AllItems.Count > 0) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await load(currentWordId, ct);
            switch (result)
            {
                case Success<IReadOnlyList<RelationItemData>> success:
                    wordId = currentWordId;
                    allItems = success.Value.Select(data => new RelationItemViewModel(data, Publish)).ToArray();
                    SearchText = "";
                    NotifyItemsChanged();
                    break;
                case StorageFailure<IReadOnlyList<RelationItemData>> failure:
                    ErrorMessage = $"无法加载关系词：{failure.Message}";
                    break;
                case NotFound<IReadOnlyList<RelationItemData>> failure:
                    ErrorMessage = $"无法加载关系词：{failure.Message}";
                    break;
                case Conflict<IReadOnlyList<RelationItemData>> failure:
                    ErrorMessage = $"无法加载关系词：{failure.Message}";
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            ErrorMessage = $"无法加载关系词：{exception.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(EmptyMessage));
        }
    }

    public void Close() => IsOpen = false;

    public void Reset()
    {
        Close();
        SearchText = "";
        wordId = null;
        allItems = [];
        ErrorMessage = null;
        NotifyItemsChanged();
    }

    private bool MatchesSearch(RelationItemViewModel item)
    {
        var query = SearchText.Trim();
        return new[] { item.Word, item.Phonetic, item.Chinese, item.Contrast, item.Collocation, item.Badge }
            .Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Publish(RelationActionRequestedEventArgs args) => ActionRequested?.Invoke(this, args);

    private void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(AllItems));
        OnPropertyChanged(nameof(FilteredItems));
        OnPropertyChanged(nameof(HasMoreThanFiveItems));
        OnPropertyChanged(nameof(EmptyMessage));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(IsLoading)) OnPropertyChanged(nameof(EmptyMessage));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
