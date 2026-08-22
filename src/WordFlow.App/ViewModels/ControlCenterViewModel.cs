using System.ComponentModel;
using System.Reflection;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.ViewModels;

public sealed class ControlCenterViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILearningProgressReader progressReader;
    private readonly SynchronizationContext? context;
    private int reviewedToday;
    private int slashedTotal;
    private bool loading;

    public ControlCenterViewModel(
        DailyPlanSettingsViewModel dailyPlan,
        ShortcutSettingsViewModel shortcuts,
        PronunciationSettingsViewModel pronunciation,
        ThemeSettingsViewModel theme,
        ILearningProgressReader progressReader,
        SynchronizationContext? context = null)
    {
        DailyPlan = dailyPlan ?? throw new ArgumentNullException(nameof(dailyPlan));
        Shortcuts = shortcuts ?? throw new ArgumentNullException(nameof(shortcuts));
        Pronunciation = pronunciation ?? throw new ArgumentNullException(nameof(pronunciation));
        Theme = theme ?? throw new ArgumentNullException(nameof(theme));
        this.progressReader = progressReader ?? throw new ArgumentNullException(nameof(progressReader));
        this.context = context;
    }

    public DailyPlanSettingsViewModel DailyPlan { get; }
    public ShortcutSettingsViewModel Shortcuts { get; }
    public PronunciationSettingsViewModel Pronunciation { get; }
    public ThemeSettingsViewModel Theme { get; }
    public int ReviewedToday { get => reviewedToday; private set => Set(ref reviewedToday, value); }
    public int SlashedTotal { get => slashedTotal; private set => Set(ref slashedTotal, value); }
    public int DailyTarget => DailyPlan.NewLimit + DailyPlan.SoftReviewLimit;
    public int RemainingToday => Math.Max(0, DailyTarget - ReviewedToday);
    public bool IsLoading { get => loading; private set => Set(ref loading, value); }
    public string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "本地构建";
    public string OfflineStatus => "完全离线 · 数据仅保存在本机";
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await DailyPlan.RestoreAsync(ct).ConfigureAwait(false);
        await Pronunciation.RestoreAsync(ct).ConfigureAwait(false);
        await RefreshProgressAsync(ct).ConfigureAwait(false);
        DailyPlan.PropertyChanged += OnDailyPlanChanged;
    }

    public async Task RefreshProgressAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var snapshot = await progressReader.GetDailyStatisticsAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct).ConfigureAwait(false);
            Publish(() => { ReviewedToday = snapshot.ReviewedToday; SlashedTotal = snapshot.SlashedTotal; Raise(nameof(RemainingToday)); });
        }
        finally { IsLoading = false; }
    }

    public Task SaveDailyPlanAsync(CancellationToken ct = default) => DailyPlan.SaveAsync(ct);

    public void Dispose()
    {
        DailyPlan.PropertyChanged -= OnDailyPlanChanged;
        Shortcuts.Dispose();
        Pronunciation.Dispose();
    }

    private void OnDailyPlanChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DailyPlanSettingsViewModel.NewLimit) or nameof(DailyPlanSettingsViewModel.SoftReviewLimit) or nameof(DailyPlanSettingsViewModel.Plan))
        { Raise(nameof(DailyTarget)); Raise(nameof(RemainingToday)); }
    }

    private void Publish(Action action) { if (context is null || context == SynchronizationContext.Current) action(); else context.Post(_ => action(), null); }
    private bool Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    private void Raise(string? name) => PropertyChanged?.Invoke(this, new(name));
}
