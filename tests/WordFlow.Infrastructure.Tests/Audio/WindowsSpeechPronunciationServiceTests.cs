using System.Collections.Concurrent;
using WordFlow.Application.Ports;
using WordFlow.Infrastructure.Audio;

namespace WordFlow.Infrastructure.Tests.Audio;

public sealed class WindowsSpeechPronunciationServiceTests
{
    [Fact]
    public void Keeps_only_enabled_English_voices_and_selects_preferences_deterministically()
    {
        var engine = new FakeSpeechEngine(
        [
            new("us-z", "US Z", "en-US", true),
            new("disabled-gb", "Disabled", "en-GB", false),
            new("fr", "French", "fr-FR", true),
            new("gb-a", "GB A", "en-GB", true),
            new("au", "Australian", "en-AU", true),
            new("us-a", "US A", "en-US", true),
        ]);
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));

        Assert.Equal(["au", "gb-a", "us-a", "us-z"], service.Voices.Select(voice => voice.Id));
        Assert.Equal(PronunciationAccent.OtherEnglish, service.Voices[0].Accent);
        Assert.Equal("gb-a", service.SelectVoice(null, PronunciationAccent.British)!.Id);
        Assert.Equal("us-a", service.SelectVoice(null, PronunciationAccent.American)!.Id);
        Assert.Equal("au", service.SelectVoice("missing", PronunciationAccent.Automatic)!.Id);
        Assert.Equal("us-z", service.SelectVoice("us-z", PronunciationAccent.British)!.Id);
    }

    [Fact]
    public void No_English_voice_disables_only_pronunciation_with_offline_Windows_guidance()
    {
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(
            new FakeSpeechEngine([new("fr", "French", "fr-FR", true)])));

        Assert.False(service.Availability.IsAvailable);
        Assert.Equal(PronunciationInventoryState.AuthoritativeEmpty, service.Availability.InventoryState);
        Assert.Null(service.Availability.FaultInfo);
        Assert.Empty(service.Voices);
        Assert.Contains("Windows", service.Availability.Message);
        Assert.Contains("离线", service.Availability.Message);
        Assert.DoesNotContain("http", service.Availability.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("factory")]
    [InlineData("handler")]
    [InlineData("enumeration")]
    public void Speech_subsystem_fault_is_not_reported_as_an_authoritative_empty_inventory(string failurePoint)
    {
        var engine = EnglishEngine();
        engine.ThrowOnHandlerAdd = failurePoint == "handler";
        engine.ThrowOnInventory = failurePoint == "enumeration";
        IOfflineSpeechEngineFactory factory = failurePoint == "factory"
            ? new ThrowingSpeechEngineFactory()
            : new FakeSpeechEngineFactory(engine);

        using var service = new WindowsSpeechPronunciationService(factory);

        Assert.False(service.Availability.IsAvailable);
        Assert.Equal(PronunciationInventoryState.UnavailableFault, service.Availability.InventoryState);
        Assert.Contains("暂时不可用", service.Availability.Message);
        Assert.DoesNotContain("安装", service.Availability.Message);
        Assert.Equal("InvalidOperationException", service.Availability.FaultInfo);
    }

    [Fact]
    public void Conflicting_duplicate_voice_id_or_selectable_name_is_quarantined_and_identical_entries_are_canonicalized()
    {
        var engine = new FakeSpeechEngine(
        [
            new("safe", "Canonical", "EN-us", true),
            new("safe", "Canonical", "en-US", true),
            new("dup-id", "First", "en-US", true),
            new("dup-id", "Second", "en-GB", true),
            new("name-a", "Shared", "en-US", true),
            new("name-b", "shared", "en-GB", true),
        ]);
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));

        var voice = Assert.Single(service.Voices);
        Assert.Equal("safe", voice.Id);
        Assert.Equal("Canonical", voice.Name);
        Assert.Equal("en-US", voice.CultureName);
        Assert.Equal(PronunciationInventoryState.AuthoritativeAvailable, service.Availability.InventoryState);
        Assert.Equal(["dup-id", "name-a", "name-b"], service.Availability.QuarantinedVoiceIds);
    }

    [Fact]
    public void All_conflicting_enabled_English_voices_are_an_unavailable_conflict_not_an_empty_inventory()
    {
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(
            new FakeSpeechEngine(
            [
                new("dup", "First", "en-US", true),
                new("dup", "Second", "en-GB", true),
            ])));

        Assert.Empty(service.Voices);
        Assert.False(service.Availability.IsAvailable);
        Assert.Equal(PronunciationInventoryState.UnavailableConflict, service.Availability.InventoryState);
        Assert.Equal(["dup"], service.Availability.QuarantinedVoiceIds);
        Assert.Contains("冲突", service.Availability.Message);
        Assert.DoesNotContain("安装", service.Availability.Message);
    }

    [Theory]
    [InlineData("", 0, 50)]
    [InlineData("word", -11, 50)]
    [InlineData("word", 11, 50)]
    [InlineData("word", 0, -1)]
    [InlineData("word", 0, 101)]
    public async Task Rejects_invalid_text_rate_and_volume_before_touching_the_engine(string text, int rate, int volume)
    {
        var engine = EnglishEngine();
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.SpeakAsync(text, "us", rate, volume, default));

        Assert.Empty(engine.Started);
    }

    [Fact]
    public async Task Latest_request_wins_and_stale_completion_cannot_complete_the_new_request()
    {
        var engine = EnglishEngine();
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));

        var first = service.SpeakAsync("first", "us", 0, 80, default);
        await engine.WaitForStartsAsync(1);
        var firstRequest = engine.Started[0].RequestId;
        var second = service.SpeakAsync("second", "us", 1, 90, default);
        await engine.WaitForStartsAsync(2);
        var secondRequest = engine.Started[1].RequestId;
        engine.Complete(firstRequest);

        Assert.Equal(PronunciationPlaybackStatus.Cancelled, (await first).Status);
        Assert.False(second.IsCompleted);

        engine.Complete(secondRequest);
        Assert.Equal(PronunciationPlaybackStatus.Completed, (await second).Status);
        Assert.True(engine.CancelCalls >= 1);
        Assert.Equal("second", engine.Started[1].Text);
        Assert.Equal(["speak:first", "cancel", "speak:second"], engine.Operations.Take(3));
    }

    [Fact]
    public async Task Cancellation_and_engine_faults_complete_without_blocking_or_escaping()
    {
        var engine = EnglishEngine();
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));
        using var cancellation = new CancellationTokenSource();

        var cancelled = service.SpeakAsync("cancel", "us", 0, 100, cancellation.Token);
        await engine.WaitForStartsAsync(1);
        cancellation.Cancel();
        Assert.Equal(PronunciationPlaybackStatus.Cancelled, (await cancelled.WaitAsync(TimeSpan.FromSeconds(2))).Status);

        engine.ThrowOnSpeak = new InvalidOperationException("engine fault");
        var failed = await service.SpeakAsync("fault", "us", 0, 100, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PronunciationPlaybackStatus.Failed, failed.Status);
        Assert.Contains("engine fault", failed.Message);
    }

    [Fact]
    public async Task Unknown_voice_is_rejected_and_engine_startup_failure_degrades_without_crashing()
    {
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(EnglishEngine()));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SpeakAsync("word", "missing", 0, 100, default));

        using var unavailable = new WindowsSpeechPronunciationService(new ThrowingSpeechEngineFactory());
        var result = await unavailable.SpeakAsync("word", "anything", 0, 100, default);

        Assert.False(unavailable.Availability.IsAvailable);
        Assert.Equal(PronunciationPlaybackStatus.Unavailable, result.Status);
        Assert.Contains("Windows", result.Message);
    }

    [Fact]
    public async Task Engine_lives_on_one_long_running_STA_thread_and_is_disposed_there()
    {
        var engine = EnglishEngine();
        var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));
        int startupThread = engine.CreatedThreadId;
        var speech = service.SpeakAsync("short", "us", 0, 50, default);
        await engine.WaitForStartsAsync(1);
        engine.Complete(engine.Started[0].RequestId);
        await speech;

        service.Dispose();

        Assert.Equal(ApartmentState.STA, engine.CreatedApartment);
        Assert.Equal(startupThread, engine.SpeakThreadIds.Single());
        Assert.Equal(startupThread, engine.DisposeThreadId);
        Assert.False(engine.EventHandlerAttachedAfterDispose);
    }

    [Fact]
    public async Task Real_installed_English_voice_null_output_smoke_is_guarded_and_stops_cleanly()
    {
        using var service = new WindowsSpeechPronunciationService(
            new SystemSpeechEngineFactory(SpeechOutputPolicy.Null));
        var voice = service.SelectVoice(null, PronunciationAccent.Automatic);
        if (voice is null) return;

        var speech = service.SpeakAsync("test", voice.Id, 0, 1, default);
        await Task.Delay(75);
        await service.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2));
        var result = await speech.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(result.Status, new[] { PronunciationPlaybackStatus.Completed, PronunciationPlaybackStatus.Cancelled });
    }

    [Fact]
    public async Task Disposal_cancels_active_speech_and_completes_its_task()
    {
        var engine = EnglishEngine();
        var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));
        var speech = service.SpeakAsync("active", "us", 0, 100, default);
        await engine.WaitForStartsAsync(1);

        service.Dispose();

        Assert.Equal(PronunciationPlaybackStatus.Cancelled, (await speech.WaitAsync(TimeSpan.FromSeconds(2))).Status);
        Assert.Equal(engine.CreatedThreadId, engine.DisposeThreadId);
    }

    [Fact]
    public async Task Synchronous_engine_completion_is_correlated_after_current_request_is_installed()
    {
        var engine = EnglishEngine();
        engine.CompleteSynchronously = true;
        using var service = new WindowsSpeechPronunciationService(new FakeSpeechEngineFactory(engine));

        var result = await service.SpeakAsync("sync", "us", 0, 50, default).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(PronunciationPlaybackStatus.Completed, result.Status);
    }

    private static FakeSpeechEngine EnglishEngine() =>
        new([new("us", "US", "en-US", true)]);

    private sealed class FakeSpeechEngineFactory(FakeSpeechEngine engine) : IOfflineSpeechEngineFactory
    {
        public IOfflineSpeechEngine Create()
        {
            engine.CreatedThreadId = Environment.CurrentManagedThreadId;
            engine.CreatedApartment = Thread.CurrentThread.GetApartmentState();
            return engine;
        }
    }

    private sealed class ThrowingSpeechEngineFactory : IOfflineSpeechEngineFactory
    {
        public IOfflineSpeechEngine Create() => throw new InvalidOperationException("SAPI unavailable");
    }

    private sealed class FakeSpeechEngine(IReadOnlyList<SpeechEngineVoice> voices) : IOfflineSpeechEngine
    {
        private EventHandler<SpeechEngineCompletedEventArgs>? completed;
        private readonly SemaphoreSlim starts = new(0);
        public int CreatedThreadId { get; set; }
        public ApartmentState CreatedApartment { get; set; }
        public int? DisposeThreadId { get; private set; }
        public List<int> SpeakThreadIds { get; } = [];
        public List<(Guid RequestId, string Text, string VoiceId, int Rate, int Volume)> Started { get; } = [];
        public int CancelCalls { get; private set; }
        public List<string> Operations { get; } = [];
        public Exception? ThrowOnSpeak { get; set; }
        public bool ThrowOnHandlerAdd { get; set; }
        public bool ThrowOnInventory { get; set; }
        public bool CompleteSynchronously { get; set; }
        public bool EventHandlerAttachedAfterDispose => completed is not null;
        public IReadOnlyList<SpeechEngineVoice> GetInstalledVoices() => ThrowOnInventory
            ? throw new InvalidOperationException("enumeration fault")
            : voices;
        public event EventHandler<SpeechEngineCompletedEventArgs>? SpeakCompleted
        {
            add
            {
                if (ThrowOnHandlerAdd) throw new InvalidOperationException("handler fault");
                completed += value;
            }
            remove => completed -= value;
        }
        public void CancelAll() { CancelCalls++; Operations.Add("cancel"); }
        public void SpeakAsync(Guid requestId, string text, string voiceId, int rate, int volume)
        {
            SpeakThreadIds.Add(Environment.CurrentManagedThreadId);
            if (ThrowOnSpeak is { } failure) throw failure;
            Started.Add((requestId, text, voiceId, rate, volume));
            Operations.Add($"speak:{text}");
            starts.Release();
            if (CompleteSynchronously) Complete(requestId);
        }
        public void Complete(Guid requestId, bool cancelled = false, Exception? error = null) =>
            completed?.Invoke(this, new(requestId, cancelled, error));
        public async Task WaitForStartsAsync(int count)
        {
            while (Started.Count < count) await starts.WaitAsync(TimeSpan.FromSeconds(2));
        }
        public void Dispose() => DisposeThreadId = Environment.CurrentManagedThreadId;
    }
}
