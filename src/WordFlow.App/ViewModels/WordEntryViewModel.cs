using System.ComponentModel;
using System.Runtime.CompilerServices;
using WordFlow.Application;
using WordFlow.Application.Learning;
using WordFlow.Application.Ports;

namespace WordFlow.App.ViewModels;

public sealed class WordEntryViewModel : INotifyPropertyChanged
{
    private readonly GetWordEntry getWordEntry;
    private WordEntry? entry;
    private string? errorMessage;
    private bool isLoading;
    private bool disposed;

    public WordEntryViewModel(GetWordEntry getWordEntry, Guid wordId, string word)
    {
        this.getWordEntry = getWordEntry ?? throw new ArgumentNullException(nameof(getWordEntry));
        WordId = wordId;
        Word = word;
    }

    public Guid WordId { get; }
    public string Word { get; }
    public bool HasEntry => entry is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string Phonetic => entry?.Phonetic ?? "";
    public string Chinese => entry?.Chinese ?? "";
    public string? PrimaryDefinition => entry?.PrimaryDefinition;
    public string? PartOfSpeech => entry?.PartOfSpeech;
    public string? Exchange => entry?.Exchange;
    public string? Tags => entry?.Tags;
    public string? Tier => entry?.Tier;
    public int? FrequencyRank => entry?.FrequencyRank;
    public int? Collins => entry?.Collins;
    public int? Oxford => entry?.Oxford;
    public int? BncRank => entry?.BncRank;

    public string HasExchangeVisibility => string.IsNullOrWhiteSpace(Exchange) ? "Collapsed" : "Visible";
    public string HasTagsVisibility => string.IsNullOrWhiteSpace(Tags) ? "Collapsed" : "Visible";
    public string HasTierVisibility => string.IsNullOrWhiteSpace(Tier) ? "Collapsed" : "Visible";
    public string HasDefinitionVisibility => string.IsNullOrWhiteSpace(PrimaryDefinition) ? "Collapsed" : "Visible";
    public string HasPhoneticVisibility => string.IsNullOrWhiteSpace(Phonetic) ? "Collapsed" : "Visible";
    public string HasPartOfSpeechVisibility => string.IsNullOrWhiteSpace(PartOfSpeech) ? "Collapsed" : "Visible";
    public string HasRankVisibility => FrequencyRank is null && Collins is null && Oxford is null && BncRank is null ? "Collapsed" : "Visible";
    public string HasFrequencyVisibility => FrequencyRank is null ? "Collapsed" : "Visible";
    public string HasErrorVisibility => HasError ? "Visible" : "Collapsed";

    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetField(ref errorMessage, value))
                OnPropertyChanged(nameof(HasErrorVisibility));
        }
    }

    public bool IsLoading
    {
        get => isLoading;
        private set
        {
            if (SetField(ref isLoading, value))
                OnPropertyChanged(nameof(HasLoadingVisibility));
        }
    }

    public string HasLoadingVisibility => IsLoading ? "Visible" : "Collapsed";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (disposed || IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await getWordEntry.HandleAsync(new GetWordEntryRequest(WordId), ct).ConfigureAwait(true);
            if (disposed) return;
            switch (result)
            {
                case Success<WordEntry> success:
                    entry = success.Value;
                    OnPropertyChanged(nameof(HasEntry));
                    OnPropertyChanged(nameof(Phonetic));
                    OnPropertyChanged(nameof(Chinese));
                    OnPropertyChanged(nameof(PrimaryDefinition));
                    OnPropertyChanged(nameof(PartOfSpeech));
                    OnPropertyChanged(nameof(Exchange));
                    OnPropertyChanged(nameof(Tags));
                    OnPropertyChanged(nameof(Tier));
                    OnPropertyChanged(nameof(FrequencyRank));
                    OnPropertyChanged(nameof(Collins));
                    OnPropertyChanged(nameof(Oxford));
                    OnPropertyChanged(nameof(BncRank));
                    OnPropertyChanged(nameof(HasExchangeVisibility));
                    OnPropertyChanged(nameof(HasTagsVisibility));
                    OnPropertyChanged(nameof(HasTierVisibility));
                    OnPropertyChanged(nameof(HasDefinitionVisibility));
                    OnPropertyChanged(nameof(HasPhoneticVisibility));
                    OnPropertyChanged(nameof(HasPartOfSpeechVisibility));
                    OnPropertyChanged(nameof(HasRankVisibility));
                    break;
                case StorageFailure<WordEntry> failure:
                    ErrorMessage = $"无法加载词条：{failure.Message}";
                    break;
                case NotFound<WordEntry> failure:
                    ErrorMessage = failure.Message;
                    break;
                case Conflict<WordEntry> failure:
                    ErrorMessage = $"无法加载词条：{failure.Message}";
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!disposed) ErrorMessage = $"无法加载词条：{exception.Message}";
        }
        finally { if (!disposed) IsLoading = false; }
    }

    public void Dispose()
    {
        disposed = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}