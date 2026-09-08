using System.ComponentModel;
using System.Windows;
using WordFlow.App.ViewModels;

namespace WordFlow.App.Views;

public partial class WordEntryWindow : Window
{
    private readonly WordEntryViewModel? viewModel;
    private readonly CancellationTokenSource lifetime = new();

    public WordEntryWindow() => InitializeComponent();

    public WordEntryWindow(WordEntryViewModel viewModel) : this()
    {
        this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (viewModel is null) return;
        try { await viewModel.LoadAsync(lifetime.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) { }
    }

    private void Close_Click(object sender, RoutedEventArgs args) => Close();

    private void OnClosed(object? sender, EventArgs args)
    {
        lifetime.Cancel();
        lifetime.Dispose();
        if (viewModel is not null)
        {
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            viewModel.Dispose();
        }
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WordEntryViewModel.Word) || args.PropertyName is nameof(WordEntryViewModel.HasEntry))
            Title = viewModel?.Word is { Length: > 0 } word ? $"词条 · {word}" : "词条";
    }
}