using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WordFlow.Domain.Learning;
using WordFlow.Infrastructure.Data;

namespace WordFlow.App.ViewModels;

/// <summary>Durable daily queue limits shared by the card and the control center.</summary>
public sealed class DailyPlanSettingsViewModel : INotifyPropertyChanged
{
    public const string NewLimitKey = "learning.daily_new_limit";
    public const string SoftReviewLimitKey = "learning.daily_review_limit";
    private readonly SqliteAppSettingStore store;
    private int newLimit = DailyPlan.Default.NewLimit;
    private int softReviewLimit = DailyPlan.Default.SoftReviewLimit;

    public DailyPlanSettingsViewModel(SqliteAppSettingStore store) => this.store = store ?? throw new ArgumentNullException(nameof(store));

    public int NewLimit
    {
        get => newLimit;
        set { Validate(value, nameof(value)); if (Set(ref newLimit, value)) OnPropertyChanged(nameof(Plan)); }
    }

    public int SoftReviewLimit
    {
        get => softReviewLimit;
        set { Validate(value, nameof(value)); if (Set(ref softReviewLimit, value)) OnPropertyChanged(nameof(Plan)); }
    }

    public DailyPlan Plan => new(NewLimit, SoftReviewLimit);
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task RestoreAsync(CancellationToken ct = default)
    {
        var values = await store.GetManyAsync([NewLimitKey, SoftReviewLimitKey], ct).ConfigureAwait(false);
        NewLimit = Parse(values[NewLimitKey], DailyPlan.Default.NewLimit);
        SoftReviewLimit = Parse(values[SoftReviewLimitKey], DailyPlan.Default.SoftReviewLimit);
    }

    public Task SaveAsync(CancellationToken ct = default) => store.SetManyAsync(new Dictionary<string, string>
    {
        [NewLimitKey] = NewLimit.ToString(CultureInfo.InvariantCulture),
        [SoftReviewLimitKey] = SoftReviewLimit.ToString(CultureInfo.InvariantCulture),
    }, ct);

    private static int Parse(string? raw, int fallback) => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 && value <= 10000 ? value : fallback;
    private static void Validate(int value, string name) { if (value is < 0 or > 10000) throw new ArgumentOutOfRangeException(name, "每日目标必须在 0 到 10000 之间。"); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; OnPropertyChanged(name); return true; }
    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new(name));
}
