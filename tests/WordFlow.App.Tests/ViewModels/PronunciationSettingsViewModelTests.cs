using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;
using WordFlow.Infrastructure.Audio;

namespace WordFlow.App.Tests.ViewModels;

public sealed class PronunciationSettingsViewModelTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wordflow-pronunciation-settings-{Guid.NewGuid():N}");

    public PronunciationSettingsViewModelTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Preferences_restore_and_save_as_one_app_setting_transaction()
    {
        var store = await StoreAsync();
        var service = new FakePronunciationService();
        var settings = new PronunciationSettingsViewModel(service, store);
        await settings.RestoreAsync();
        settings.SelectedVoiceId = "gb";
        settings.AccentPreference = PronunciationAccent.British;
        settings.Rate = -2;
        settings.Volume = 72;
        settings.Autoplay = true;
        await settings.SaveAsync();

        var restored = new PronunciationSettingsViewModel(service, store);
        await restored.RestoreAsync();

        Assert.Equal("gb", restored.SelectedVoiceId);
        Assert.Equal(PronunciationAccent.British, restored.AccentPreference);
        Assert.Equal(-2, restored.Rate);
        Assert.Equal(72, restored.Volume);
        Assert.True(restored.Autoplay);
        Assert.Null(restored.SettingsIssue);
    }

    [Fact]
    public async Task Corrupt_preferences_are_sanitized_expose_an_issue_and_do_not_crash_restore()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "removed",
            [PronunciationSettingsViewModel.AccentKey] = "Mars",
            [PronunciationSettingsViewModel.RateKey] = "99",
            [PronunciationSettingsViewModel.VolumeKey] = "loud",
            [PronunciationSettingsViewModel.AutoplayKey] = "sometimes",
        }, default);
        var settings = new PronunciationSettingsViewModel(new FakePronunciationService(), store);

        var exception = await Record.ExceptionAsync(() => settings.RestoreAsync());

        Assert.Null(exception);
        Assert.Null(settings.SelectedVoiceId);
        Assert.Equal(PronunciationAccent.Automatic, settings.AccentPreference);
        Assert.Equal(0, settings.Rate);
        Assert.Equal(100, settings.Volume);
        Assert.False(settings.Autoplay);
        Assert.Contains("已修复", settings.SettingsIssue);
    }

    [Fact]
    public async Task Click_supersedes_pending_autoplay_and_failures_are_observed_as_feedback()
    {
        var store = await StoreAsync();
        var service = new FakePronunciationService { DelayCompletion = true };
        var settings = new PronunciationSettingsViewModel(service, store) { Autoplay = true };
        var feedback = new List<PronunciationPlaybackResult>();
        settings.PlaybackFeedback += (_, result) => feedback.Add(result);

        settings.OnCardChanged(Guid.NewGuid(), "automatic", isPaused: false);
        await service.WaitForCallsAsync(1);
        var clickResult = settings.Execute(Guid.NewGuid(), "clicked");
        await service.WaitForCallsAsync(2);
        service.Complete(0, PronunciationPlaybackResult.Cancelled());
        service.Complete(1, PronunciationPlaybackResult.Failed("device fault"));
        await WaitUntilAsync(() => feedback.Count == 1);

        Assert.Equal(FloatingCardActionStatus.Completed, clickResult.Status);
        Assert.Equal(["automatic", "clicked"], service.SpokenWords);
        Assert.DoesNotContain(feedback, item => item.Message.Contains("automatic"));
        Assert.Contains(feedback, item => item.Status == PronunciationPlaybackStatus.Failed && item.Message.Contains("device fault"));
    }

    [Fact]
    public async Task Card_change_disable_pause_and_dispose_cancel_without_stale_autoplay()
    {
        var service = new FakePronunciationService();
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync()) { Autoplay = true };

        settings.OnCardChanged(Guid.NewGuid(), "first", isPaused: false);
        await service.WaitForCallsAsync(1);
        settings.SetPaused(true);
        settings.OnCardChanged(Guid.NewGuid(), "paused", isPaused: true);
        settings.Autoplay = false;
        settings.OnCardChanged(Guid.NewGuid(), "disabled", isPaused: false);
        settings.Dispose();
        await WaitUntilAsync(() => service.CancelCalls >= 4);

        Assert.Equal(["first"], service.SpokenWords);
        Assert.True(service.CancelCalls >= 4);
    }

    [Fact]
    public async Task Playback_feedback_observer_fault_is_contained_and_does_not_fault_background_work()
    {
        var service = new FakePronunciationService { DelayCompletion = true };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync());
        var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.PlaybackFeedback += (_, _) => throw new InvalidOperationException("observer fault");
        settings.PlaybackFeedback += (_, _) => delivered.TrySetResult(true);

        settings.Execute(Guid.NewGuid(), "safe");
        await service.WaitForCallsAsync(1);
        service.Complete(0, PronunciationPlaybackResult.Completed("safe"));

        Assert.True(await delivered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task Action_host_truthfully_disables_current_and_related_speech_when_no_voice_exists()
    {
        var service = new FakePronunciationService
        {
            Voices = [],
            Availability = new(false, WindowsSpeechPronunciationService.NoEnglishVoiceMessage),
        };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync());
        IFloatingCardActionHost host = new FloatingCardActionHost(offlineSpeech: settings);

        var capability = host.Capability(RelationActionKind.Speak);
        var result = host.Dispatch(new(RelationActionKind.Speak, Guid.NewGuid(), "word"));

        Assert.False(capability.IsAvailable);
        Assert.Contains("Windows", capability.HelpText);
        Assert.Equal(FloatingCardActionStatus.Unavailable, result.Status);
        Assert.Empty(service.SpokenWords);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private async Task<SqliteAppSettingStore> StoreAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
        await new MigrationRunner(factory).MigrateAsync(default);
        return new(factory);
    }

    private sealed class FakePronunciationService : IPronunciationService
    {
        private readonly List<TaskCompletionSource<PronunciationPlaybackResult>> pending = [];
        private readonly SemaphoreSlim calls = new(0);
        public IReadOnlyList<PronunciationVoice> Voices { get; set; } =
        [
            new("gb", "GB", "en-GB", PronunciationAccent.British),
            new("us", "US", "en-US", PronunciationAccent.American),
        ];
        public PronunciationAvailability Availability { get; set; } = new(true, "可用");
        public List<string> SpokenWords { get; } = [];
        public int CancelCalls { get; private set; }
        public bool DelayCompletion { get; set; }
        public PronunciationVoice? SelectVoice(string? voiceId, PronunciationAccent preference) =>
            Voices.FirstOrDefault(voice => voice.Id == voiceId) ?? Voices[0];
        public Task<PronunciationPlaybackResult> SpeakAsync(string text, string voiceId, int rate, int volume, CancellationToken ct)
        {
            SpokenWords.Add(text);
            calls.Release();
            if (!DelayCompletion) return Task.FromResult(PronunciationPlaybackResult.Completed(text));
            var completion = new TaskCompletionSource<PronunciationPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending.Add(completion);
            return completion.Task;
        }
        public Task CancelAsync() { CancelCalls++; return Task.CompletedTask; }
        public async Task WaitForCallsAsync(int count)
        {
            while (SpokenWords.Count < count) await calls.WaitAsync(TimeSpan.FromSeconds(2));
        }
        public void Complete(int index, PronunciationPlaybackResult result) => pending[index].TrySetResult(result);
        public void Dispose() { }
    }
}
