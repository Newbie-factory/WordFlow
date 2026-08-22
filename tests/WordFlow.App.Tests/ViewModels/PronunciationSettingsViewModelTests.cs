using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Data;
using WordFlow.Infrastructure.Audio;
using System.Collections.Concurrent;

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
        Assert.Equal(1, settings.AutoplayRepeatCount);
        settings.SelectedVoiceId = "gb";
        settings.AccentPreference = PronunciationAccent.British;
        settings.Rate = -2;
        settings.Volume = 72;
        settings.Autoplay = true;
        settings.AutoplayRepeatCount = 10;
        await settings.SaveAsync();

        var restored = new PronunciationSettingsViewModel(service, store);
        await restored.RestoreAsync();

        Assert.Equal("gb", restored.SelectedVoiceId);
        Assert.Equal(PronunciationAccent.British, restored.AccentPreference);
        Assert.Equal(-2, restored.Rate);
        Assert.Equal(72, restored.Volume);
        Assert.True(restored.Autoplay);
        Assert.Equal(10, restored.AutoplayRepeatCount);
        Assert.Null(restored.SettingsIssue);
    }

    [Theory]
    [InlineData("0", 1)]
    [InlineData("11", 1)]
    [InlineData("not-a-number", 1)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    [InlineData("5", 5)]
    [InlineData("6", 6)]
    [InlineData("7", 7)]
    [InlineData("8", 8)]
    [InlineData("9", 9)]
    [InlineData("10", 10)]
    public async Task Restore_sanitizes_repeat_count_to_one_through_ten(string storedValue, int expected)
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.AutoplayRepeatCountKey] = storedValue,
        }, default);
        var settings = new PronunciationSettingsViewModel(new FakePronunciationService(), store);

        await settings.RestoreAsync();

        Assert.Equal(expected, settings.AutoplayRepeatCount);
        var persisted = await store.GetManyAsync([PronunciationSettingsViewModel.AutoplayRepeatCountKey], default);
        Assert.Equal(expected.ToString(), persisted[PronunciationSettingsViewModel.AutoplayRepeatCountKey]);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Throwing_factory_or_enumerator_preserves_persisted_voice_until_later_authoritative_recovery(bool factoryThrows)
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "us",
            [PronunciationSettingsViewModel.RateKey] = "broken",
        }, default);
        using var faultedService = new WindowsSpeechPronunciationService(
            factoryThrows ? new ThrowingEngineFactory() : new TestEngineFactory(new TestSpeechEngine(throwOnInventory: true)));
        var faulted = new PronunciationSettingsViewModel(faultedService, store);

        await faulted.RestoreAsync();
        var afterFault = await store.GetManyAsync([PronunciationSettingsViewModel.VoiceKey], default);

        Assert.Equal("us", faulted.SelectedVoiceId);
        Assert.Equal("us", afterFault[PronunciationSettingsViewModel.VoiceKey]);
        Assert.DoesNotContain("语音", faulted.SettingsIssue);

        using var recoveredService = new WindowsSpeechPronunciationService(
            new TestEngineFactory(new TestSpeechEngine(throwOnInventory: false)));
        var recovered = new PronunciationSettingsViewModel(recoveredService, store);
        await recovered.RestoreAsync();
        Assert.Equal("us", recovered.SelectedVoiceId);
    }

    [Fact]
    public async Task Authoritative_empty_inventory_sanitizes_a_removed_persisted_voice()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "removed",
        }, default);
        var service = new FakePronunciationService
        {
            Voices = [],
            Availability = new(false, WindowsSpeechPronunciationService.NoEnglishVoiceMessage, PronunciationInventoryState.AuthoritativeEmpty),
        };
        var settings = new PronunciationSettingsViewModel(service, store);

        await settings.RestoreAsync();
        var persisted = await store.GetManyAsync([PronunciationSettingsViewModel.VoiceKey], default);

        Assert.Null(settings.SelectedVoiceId);
        Assert.Equal("", persisted[PronunciationSettingsViewModel.VoiceKey]);
        Assert.Contains("不可用", settings.SettingsIssue);
    }

    [Fact]
    public async Task Persisted_voice_id_uses_canonical_identity_without_rewriting_its_original_spelling()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "voice-id",
        }, default);
        using var service = new WindowsSpeechPronunciationService(new TestEngineFactory(
            new TestSpeechEngine(false, [new("Voice-ID", "Voice", "en-US", true)])));
        var settings = new PronunciationSettingsViewModel(service, store);

        await settings.RestoreAsync();
        await settings.SaveAsync();
        var persisted = await store.GetManyAsync([PronunciationSettingsViewModel.VoiceKey], default);

        Assert.Equal("voice-id", settings.SelectedVoiceId);
        Assert.Equal("voice-id", persisted[PronunciationSettingsViewModel.VoiceKey]);
        Assert.Null(settings.SettingsIssue);
        Assert.Equal("Voice-ID", service.SelectVoice(settings.SelectedVoiceId, PronunciationAccent.Automatic)!.Id);
    }

    [Fact]
    public async Task Case_variant_quarantined_voice_id_keeps_its_persisted_spelling_across_unrelated_save()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "ID-A",
        }, default);
        using var service = new WindowsSpeechPronunciationService(new TestEngineFactory(
            new TestSpeechEngine(false,
            [
                new("id-a", "First", "en-US", true),
                new("id-a", "Second", "en-GB", true),
            ])));
        var settings = new PronunciationSettingsViewModel(service, store);

        await settings.RestoreAsync();
        settings.Rate = -4;
        await settings.SaveAsync();
        var persisted = await store.GetManyAsync(
            [PronunciationSettingsViewModel.VoiceKey, PronunciationSettingsViewModel.RateKey], default);

        Assert.Equal("ID-A", settings.SelectedVoiceId);
        Assert.Equal("ID-A", persisted[PronunciationSettingsViewModel.VoiceKey]);
        Assert.Equal("-4", persisted[PronunciationSettingsViewModel.RateKey]);
        Assert.Contains("冲突", settings.SettingsIssue);
    }

    [Fact]
    public async Task Partial_conflict_preserves_a_quarantined_voice_id_and_later_clean_inventory_recovers_it()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "dup",
            [PronunciationSettingsViewModel.RateKey] = "broken",
        }, default);
        using var conflictedService = new WindowsSpeechPronunciationService(new TestEngineFactory(
            new TestSpeechEngine(false,
            [
                new("safe", "Safe", "en-US", true),
                new("dup", "First", "en-US", true),
                new("dup", "Second", "en-GB", true),
            ])));
        var conflicted = new PronunciationSettingsViewModel(conflictedService, store);

        await conflicted.RestoreAsync();
        var afterConflict = await store.GetManyAsync([PronunciationSettingsViewModel.VoiceKey], default);

        Assert.Equal("dup", conflicted.SelectedVoiceId);
        Assert.Equal("dup", afterConflict[PronunciationSettingsViewModel.VoiceKey]);
        Assert.Contains("冲突", conflicted.SettingsIssue);

        using var recoveredService = new WindowsSpeechPronunciationService(new TestEngineFactory(
            new TestSpeechEngine(false, [new("dup", "Recovered", "en-GB", true)])));
        var recovered = new PronunciationSettingsViewModel(recoveredService, store);
        await recovered.RestoreAsync();
        Assert.Equal("dup", recovered.SelectedVoiceId);
    }

    [Fact]
    public async Task Partial_conflict_still_sanitizes_an_absent_nonquarantined_voice_id()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "removed",
        }, default);
        using var service = new WindowsSpeechPronunciationService(new TestEngineFactory(
            new TestSpeechEngine(false,
            [
                new("safe", "Safe", "en-US", true),
                new("dup", "First", "en-US", true),
                new("dup", "Second", "en-GB", true),
            ])));
        var settings = new PronunciationSettingsViewModel(service, store);

        await settings.RestoreAsync();
        var persisted = await store.GetManyAsync([PronunciationSettingsViewModel.VoiceKey], default);

        Assert.Null(settings.SelectedVoiceId);
        Assert.Equal("", persisted[PronunciationSettingsViewModel.VoiceKey]);
    }

    [Fact]
    public async Task Quarantined_restored_voice_allows_each_unrelated_setting_save_and_later_clean_resolution()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VoiceKey] = "dup",
        }, default);
        using var conflictedService = new WindowsSpeechPronunciationService(new TestEngineFactory(
            new TestSpeechEngine(false,
            [
                new("dup", "First", "en-US", true),
                new("dup", "Second", "en-GB", true),
            ])));
        var conflicted = new PronunciationSettingsViewModel(conflictedService, store);
        await conflicted.RestoreAsync();

        conflicted.Rate = -3;
        await conflicted.SaveAsync();
        conflicted.Volume = 61;
        await conflicted.SaveAsync();
        conflicted.AccentPreference = PronunciationAccent.British;
        await conflicted.SaveAsync();
        conflicted.Autoplay = true;
        await conflicted.SaveAsync();

        var persisted = await store.GetManyAsync(
            [
                PronunciationSettingsViewModel.VoiceKey,
                PronunciationSettingsViewModel.RateKey,
                PronunciationSettingsViewModel.VolumeKey,
                PronunciationSettingsViewModel.AccentKey,
                PronunciationSettingsViewModel.AutoplayKey,
            ], default);
        Assert.Equal("dup", persisted[PronunciationSettingsViewModel.VoiceKey]);
        Assert.Equal("-3", persisted[PronunciationSettingsViewModel.RateKey]);
        Assert.Equal("61", persisted[PronunciationSettingsViewModel.VolumeKey]);
        Assert.Equal(nameof(PronunciationAccent.British), persisted[PronunciationSettingsViewModel.AccentKey]);
        Assert.Equal(bool.TrueString, persisted[PronunciationSettingsViewModel.AutoplayKey]);

        using var recoveredService = new WindowsSpeechPronunciationService(new TestEngineFactory(
            new TestSpeechEngine(false, [new("dup", "Recovered", "en-GB", true)])));
        var recovered = new PronunciationSettingsViewModel(recoveredService, store);
        await recovered.RestoreAsync();

        Assert.Equal("dup", recovered.SelectedVoiceId);
        Assert.Equal(-3, recovered.Rate);
        Assert.Equal(61, recovered.Volume);
        Assert.Equal(PronunciationAccent.British, recovered.AccentPreference);
        Assert.True(recovered.Autoplay);
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
    public async Task Autoplay_repetitions_are_serial_and_card_change_cancels_the_remainder()
    {
        var service = new FakePronunciationService { DelayCompletion = true };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync())
        {
            Autoplay = true,
            AutoplayRepeatCount = 10,
        };

        settings.OnCardChanged(Guid.NewGuid(), "alpha", isPaused: false);
        await service.WaitForCallsAsync(1);
        Assert.Equal(1, service.SelectVoiceCalls);
        service.Complete(0, PronunciationPlaybackResult.Completed("alpha"));
        await service.WaitForCallsAsync(2);

        settings.OnCardChanged(Guid.NewGuid(), "beta", isPaused: false);

        Assert.True(service.Pending[1].CancellationToken.IsCancellationRequested);
        Assert.Equal(["alpha", "alpha"], service.SpokenWords.Take(2));
    }

    [Fact]
    public async Task Autoplay_count_ten_completes_exactly_ten_calls_in_order_with_one_active_and_one_final_publish()
    {
        var service = new FakePronunciationService { DelayCompletion = true };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync())
        {
            Autoplay = true,
            AutoplayRepeatCount = 10,
        };
        var feedback = new ConcurrentQueue<PronunciationPlaybackResult>();
        settings.PlaybackFeedback += (_, result) => feedback.Enqueue(result);

        settings.OnCardChanged(Guid.NewGuid(), "alpha", isPaused: false);
        for (int index = 0; index < 10; index++)
        {
            await service.WaitForCallsAsync(index + 1);
            Assert.Equal(1, service.ActiveCalls);
            service.Complete(index, PronunciationPlaybackResult.Completed($"alpha-{index + 1}"));
        }
        await WaitUntilAsync(() => feedback.Count == 1);

        Assert.Equal(10, service.SpokenWords.Count);
        Assert.Equal(Enumerable.Range(1, 10), service.PlaybackStartOrder);
        Assert.Equal(1, service.MaxConcurrentCalls);
        var published = Assert.Single(feedback);
        Assert.Equal(PronunciationPlaybackStatus.Completed, published.Status);
        Assert.Equal("已播放 alpha 的离线发音", published.Message);
    }

    [Fact]
    public async Task Autoplay_count_one_completes_once_and_publishes_completed()
    {
        var service = new FakePronunciationService { DelayCompletion = true };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync())
        {
            Autoplay = true,
            AutoplayRepeatCount = 1,
        };
        var feedback = new TaskCompletionSource<PronunciationPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.PlaybackFeedback += (_, result) => feedback.TrySetResult(result);

        settings.OnCardChanged(Guid.NewGuid(), "solo", isPaused: false);
        await service.WaitForCallsAsync(1);
        service.Complete(0, PronunciationPlaybackResult.Completed("solo-1"));

        var published = await feedback.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(service.SpokenWords);
        Assert.Equal(1, service.MaxConcurrentCalls);
        Assert.Equal(PronunciationPlaybackStatus.Completed, published.Status);
    }

    [Fact]
    public async Task Autoplay_failure_short_circuits_remaining_repetitions()
    {
        var service = new FakePronunciationService { DelayCompletion = true };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync())
        {
            Autoplay = true,
            AutoplayRepeatCount = 4,
        };
        var feedback = new TaskCompletionSource<PronunciationPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.PlaybackFeedback += (_, result) => feedback.TrySetResult(result);

        settings.OnCardChanged(Guid.NewGuid(), "alpha", isPaused: false);
        await service.WaitForCallsAsync(1);
        service.Complete(0, PronunciationPlaybackResult.Failed("device fault"));

        var result = await feedback.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PronunciationPlaybackStatus.Failed, result.Status);
        Assert.Single(service.SpokenWords);
    }

    [Fact]
    public async Task Manual_playback_stays_single_when_autoplay_repeat_count_is_ten()
    {
        var service = new FakePronunciationService { DelayCompletion = true };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync()) { AutoplayRepeatCount = 10 };
        var feedback = new TaskCompletionSource<PronunciationPlaybackResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.PlaybackFeedback += (_, result) => feedback.TrySetResult(result);

        settings.Execute(Guid.NewGuid(), "manual");
        await service.WaitForCallsAsync(1);
        service.Complete(0, PronunciationPlaybackResult.Completed("manual"));

        await feedback.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(service.SpokenWords);
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
    public async Task Enabling_autoplay_while_a_disabled_card_change_is_cancelling_does_not_replay_that_card()
    {
        var service = new FakePronunciationService { DelayNextCancel = true };
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync()) { Autoplay = false };

        settings.OnCardChanged(Guid.NewGuid(), "must-not-play", isPaused: false);
        await WaitUntilAsync(() => service.CancelCalls == 1);
        settings.Autoplay = true;
        service.ReleaseDelayedCancel();
        await Task.Delay(100);

        Assert.Empty(service.SpokenWords);
    }

    [Theory]
    [InlineData("click")]
    [InlineData("pause")]
    [InlineData("disable")]
    [InlineData("switch")]
    [InlineData("dispose")]
    public async Task Newer_transition_prevents_autoplay_blocked_at_the_publication_boundary(string transition)
    {
        var service = new FakePronunciationService();
        service.BlockNextPublication();
        var settings = new PronunciationSettingsViewModel(service, await StoreAsync()) { Autoplay = true };

        settings.OnCardChanged(Guid.NewGuid(), "automatic", isPaused: false);
        await service.WaitForBlockedPublicationAsync();

        switch (transition)
        {
            case "click": settings.Execute(Guid.NewGuid(), "clicked"); break;
            case "pause": settings.SetPaused(true); break;
            case "disable": settings.Autoplay = false; break;
            case "switch": settings.OnCardChanged(Guid.NewGuid(), "switched", isPaused: false); break;
            case "dispose": settings.Dispose(); break;
        }

        service.ReleaseBlockedPublication();
        await service.WaitForBlockedPublicationCompletionAsync();

        Assert.DoesNotContain("automatic", service.SpokenWords);
    }

    [Fact]
    public async Task Restore_applies_all_notifications_on_owner_context_and_contains_observers()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.RateKey] = "invalid",
        }, default);
        using var owner = new PumpSynchronizationContext();
        var settings = new PronunciationSettingsViewModel(new FakePronunciationService(), store, owner);
        var notificationThreads = new ConcurrentBag<int>();
        settings.PropertyChanged += (_, _) => throw new InvalidOperationException("observer fault");
        settings.PropertyChanged += (_, _) => notificationThreads.Add(Environment.CurrentManagedThreadId);

        var exception = await Record.ExceptionAsync(() => settings.RestoreAsync());

        Assert.Null(exception);
        Assert.NotEmpty(notificationThreads);
        Assert.All(notificationThreads, id => Assert.Equal(owner.ThreadId, id));
        Assert.Contains("已修复", settings.SettingsIssue);
    }

    [Fact]
    public async Task Cancellation_after_owner_post_prevents_late_restore_apply_and_notifications()
    {
        var store = await StoreAsync();
        await store.SetManyAsync(new Dictionary<string, string>
        {
            [PronunciationSettingsViewModel.VolumeKey] = "55",
        }, default);
        var owner = new ControlledSynchronizationContext();
        var settings = new PronunciationSettingsViewModel(new FakePronunciationService(), store, owner);
        var notifications = new ConcurrentQueue<string?>();
        settings.PropertyChanged += (_, args) => notifications.Enqueue(args.PropertyName);
        using var cancellation = new CancellationTokenSource();

        var restore = settings.RestoreAsync(cancellation.Token);
        await owner.WaitForPostAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        owner.RunPostedCallbacks();

        Assert.Equal(100, settings.Volume);
        Assert.Empty(notifications);
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
            Availability = new(false, WindowsSpeechPronunciationService.NoEnglishVoiceMessage, PronunciationInventoryState.AuthoritativeEmpty),
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
        private readonly List<PendingPlayback> pending = [];
        private readonly SemaphoreSlim calls = new(0);
        public IReadOnlyList<PronunciationVoice> Voices { get; set; } =
        [
            new("gb", "GB", "en-GB", PronunciationAccent.British),
            new("us", "US", "en-US", PronunciationAccent.American),
        ];
        public PronunciationAvailability Availability { get; set; } =
            new(true, "可用", PronunciationInventoryState.AuthoritativeAvailable);
        public ConcurrentQueue<string> SpokenWords { get; } = [];
        public ConcurrentQueue<int> PlaybackStartOrder { get; } = [];
        public IReadOnlyList<PendingPlayback> Pending => pending;
        public int ActiveCalls => Volatile.Read(ref activeCalls);
        public int MaxConcurrentCalls => Volatile.Read(ref maxConcurrentCalls);
        public int CancelCalls { get; private set; }
        public int SelectVoiceCalls { get; private set; }
        public bool DelayCompletion { get; set; }
        public bool DelayNextCancel { get; set; }
        private TaskCompletionSource<bool>? delayedCancel;
        private int blockNextPublication;
        private TaskCompletionSource<bool>? publicationEntered;
        private TaskCompletionSource<bool>? publicationRelease;
        private TaskCompletionSource<bool>? publicationCompleted;
        private int activeCalls;
        private int maxConcurrentCalls;
        private int playbackSequence;
        public PronunciationVoice? SelectVoice(string? voiceId, PronunciationAccent preference)
        {
            SelectVoiceCalls++;
            return Voices.FirstOrDefault(voice => voice.Id == voiceId) ?? Voices[0];
        }
        public async Task<PronunciationPlaybackResult> SpeakAsync(string text, string voiceId, int rate, int volume, CancellationToken ct)
        {
            bool wasBlocked = Interlocked.Exchange(ref blockNextPublication, 0) == 1;
            if (wasBlocked)
            {
                publicationEntered!.TrySetResult(true);
                await publicationRelease!.Task;
                if (ct.IsCancellationRequested)
                {
                    publicationCompleted!.TrySetResult(true);
                    return PronunciationPlaybackResult.Cancelled();
                }
            }
            TaskCompletionSource<PronunciationPlaybackResult>? completion = null;
            if (DelayCompletion)
            {
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(new(completion, ct));
            }
            SpokenWords.Enqueue(text);
            PlaybackStartOrder.Enqueue(Interlocked.Increment(ref playbackSequence));
            int active = Interlocked.Increment(ref activeCalls);
            int observedMaximum;
            while (active > (observedMaximum = Volatile.Read(ref maxConcurrentCalls)) &&
                   Interlocked.CompareExchange(ref maxConcurrentCalls, active, observedMaximum) != observedMaximum) { }
            calls.Release();
            if (wasBlocked) publicationCompleted!.TrySetResult(true);
            try
            {
                if (completion is null) return PronunciationPlaybackResult.Completed(text);
                return await completion.Task;
            }
            finally { Interlocked.Decrement(ref activeCalls); }
        }
        public Task CancelAsync()
        {
            CancelCalls++;
            if (!DelayNextCancel) return Task.CompletedTask;
            DelayNextCancel = false;
            delayedCancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return delayedCancel.Task;
        }
        public void ReleaseDelayedCancel() => delayedCancel?.TrySetResult(true);
        public void BlockNextPublication()
        {
            publicationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            publicationRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            publicationCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref blockNextPublication, 1);
        }
        public Task WaitForBlockedPublicationAsync() => publicationEntered!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public void ReleaseBlockedPublication() => publicationRelease!.TrySetResult(true);
        public Task WaitForBlockedPublicationCompletionAsync() => publicationCompleted!.Task.WaitAsync(TimeSpan.FromSeconds(2));
        public async Task WaitForCallsAsync(int count)
        {
            while (SpokenWords.Count < count) await calls.WaitAsync(TimeSpan.FromSeconds(2));
        }
        public void Complete(int index, PronunciationPlaybackResult result) => pending[index].Completion.TrySetResult(result);
        public void Dispose() { }

        public sealed record PendingPlayback(
            TaskCompletionSource<PronunciationPlaybackResult> Completion,
            CancellationToken CancellationToken);
    }

    private sealed class ThrowingEngineFactory : IOfflineSpeechEngineFactory
    {
        public IOfflineSpeechEngine Create() => throw new InvalidOperationException("factory fault");
    }

    private sealed class TestEngineFactory(TestSpeechEngine engine) : IOfflineSpeechEngineFactory
    {
        public IOfflineSpeechEngine Create() => engine;
    }

    private sealed class TestSpeechEngine(
        bool throwOnInventory,
        IReadOnlyList<SpeechEngineVoice>? installedVoices = null) : IOfflineSpeechEngine
    {
        public IReadOnlyList<SpeechEngineVoice> GetInstalledVoices() => throwOnInventory
            ? throw new InvalidOperationException("enumeration fault")
            : installedVoices ?? [new("us", "US", "en-US", true)];
        public event EventHandler<SpeechEngineCompletedEventArgs>? SpeakCompleted { add { } remove { } }
        public void SpeakAsync(Guid requestId, string text, string voiceId, int rate, int volume) { }
        public void CancelAll() { }
        public void Dispose() { }
    }

    private sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = [];
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new();

        public PumpSynchronizationContext()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Pronunciation settings owner context" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
        }

        public int ThreadId { get; private set; }
        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));
        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join();
            ready.Dispose();
            queue.Dispose();
        }
        private void Run()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            SetSynchronizationContext(this);
            ready.Set();
            foreach (var work in queue.GetConsumingEnumerable()) work.Callback(work.State);
        }
    }

    private sealed class ControlledSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = [];
        private readonly SemaphoreSlim posted = new(0);

        public override void Post(SendOrPostCallback d, object? state)
        {
            callbacks.Enqueue((d, state));
            posted.Release();
        }

        public Task WaitForPostAsync() => posted.WaitAsync(TimeSpan.FromSeconds(2));

        public void RunPostedCallbacks()
        {
            while (callbacks.TryDequeue(out var work)) work.Callback(work.State);
        }
    }
}
