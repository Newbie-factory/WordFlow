using System.Collections.Concurrent;
using System.Globalization;
using System.Speech.Synthesis;
using WordFlow.Application.Ports;

namespace WordFlow.Infrastructure.Audio;

internal sealed record SpeechEngineVoice(string Id, string Name, string CultureName, bool IsEnabled);

internal sealed class SpeechEngineCompletedEventArgs(Guid requestId, bool cancelled, Exception? error) : EventArgs
{
    public Guid RequestId { get; } = requestId;
    public bool Cancelled { get; } = cancelled;
    public Exception? Error { get; } = error;
}

internal interface IOfflineSpeechEngine : IDisposable
{
    IReadOnlyList<SpeechEngineVoice> GetInstalledVoices();
    event EventHandler<SpeechEngineCompletedEventArgs>? SpeakCompleted;
    void SpeakAsync(Guid requestId, string text, string voiceId, int rate, int volume);
    void CancelAll();
}

internal interface IOfflineSpeechEngineFactory
{
    IOfflineSpeechEngine Create();
}

public sealed class WindowsSpeechPronunciationService : IPronunciationService
{
    public const string NoEnglishVoiceMessage =
        "未检测到可用的英文语音。请在 Windows“语言和区域”的“语音”选项中安装离线英语语音包。";

    private readonly BlockingCollection<Action> queue = [];
    private readonly IOfflineSpeechEngineFactory factory;
    private readonly Thread worker;
    private readonly TaskCompletionSource<bool> startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object lifecycleGate = new();
    private IOfflineSpeechEngine? engine;
    private PendingRequest? current;
    private IReadOnlyList<PronunciationVoice> voices = [];
    private PronunciationAvailability availability = new(false, NoEnglishVoiceMessage);
    private long requestSequence;
    private long latestRequest;
    private bool accepting = true;
    private bool disposed;

    public WindowsSpeechPronunciationService() : this(new SystemSpeechEngineFactory()) { }

    internal WindowsSpeechPronunciationService(IOfflineSpeechEngineFactory factory)
    {
        this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        worker = new Thread(Run)
        {
            IsBackground = true,
            Name = "WordFlow offline pronunciation",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        startup.Task.GetAwaiter().GetResult();
    }

    public IReadOnlyList<PronunciationVoice> Voices => voices;
    public PronunciationAvailability Availability => availability;

    public PronunciationVoice? SelectVoice(string? voiceId, PronunciationAccent preference)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var exact = voices.FirstOrDefault(voice => string.Equals(voice.Id, voiceId, StringComparison.Ordinal));
            if (exact is not null) return exact;
        }

        return voices.OrderBy(voice => PreferenceRank(voice, preference))
            .ThenBy(voice => voice.CultureName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public Task<PronunciationPlaybackResult> SpeakAsync(
        string text,
        string voiceId,
        int rate,
        int volume,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Text is required.", nameof(text));
        if (rate is < -10 or > 10) throw new ArgumentOutOfRangeException(nameof(rate), "Rate must be between -10 and 10.");
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be between 0 and 100.");
        if (!availability.IsAvailable)
            return Task.FromResult(PronunciationPlaybackResult.Unavailable(availability.Message));
        if (voices.All(voice => !string.Equals(voice.Id, voiceId, StringComparison.Ordinal)))
            throw new ArgumentException("The selected installed English voice is unavailable.", nameof(voiceId));
        if (ct.IsCancellationRequested)
            return Task.FromResult(PronunciationPlaybackResult.Cancelled());

        long sequence = Interlocked.Increment(ref requestSequence);
        var pending = new PendingRequest(sequence, Guid.NewGuid(), text, ct);
        if (ct.CanBeCanceled)
            pending.Cancellation = ct.Register(() => QueueCancellation(sequence));
        if (!TryPostLatest(sequence, () => StartCore(pending, voiceId, rate, volume)))
        {
            pending.Cancellation.Dispose();
            pending.Completion.TrySetResult(PronunciationPlaybackResult.Cancelled());
        }
        return pending.Completion.Task;
    }

    public Task CancelAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        long sequence = Interlocked.Increment(ref requestSequence);
        if (!TryPostLatest(sequence, () =>
        {
            CancelCurrentCore();
            completion.TrySetResult(true);
        })) completion.TrySetResult(true);
        return completion.Task;
    }

    public void Dispose()
    {
        lock (lifecycleGate)
        {
            if (disposed) return;
            disposed = true;
            accepting = false;
            Volatile.Write(ref latestRequest, Interlocked.Increment(ref requestSequence));
            queue.Add(CleanupCore);
            queue.CompleteAdding();
        }
        if (Environment.CurrentManagedThreadId != worker.ManagedThreadId) worker.Join();
        queue.Dispose();
    }

    private void Run()
    {
        try
        {
            try
            {
                engine = factory.Create();
                engine.SpeakCompleted += OnSpeakCompleted;
                voices = engine.GetInstalledVoices()
                    .Where(voice => voice.IsEnabled && IsEnglish(voice.CultureName))
                    .Select(voice => new PronunciationVoice(
                        voice.Id,
                        voice.Name,
                        voice.CultureName,
                        AccentFor(voice.CultureName)))
                    .OrderBy(voice => voice.CultureName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(voice => voice.Id, StringComparer.Ordinal)
                    .ToArray();
                availability = voices.Count > 0
                    ? new(true, "Windows 本机离线英语语音可用")
                    : new(false, NoEnglishVoiceMessage);
            }
            catch
            {
                availability = new(false, NoEnglishVoiceMessage);
                voices = [];
            }
            finally { startup.TrySetResult(true); }

            foreach (var action in queue.GetConsumingEnumerable()) action();
        }
        finally { startup.TrySetResult(true); }
    }

    private void StartCore(PendingRequest request, string voiceId, int rate, int volume)
    {
        if (request.Sequence != Volatile.Read(ref latestRequest) || request.CancellationToken.IsCancellationRequested || disposed)
        {
            Complete(request, PronunciationPlaybackResult.Cancelled());
            return;
        }
        CancelCurrentCore();
        current = request;
        try
        {
            if (engine is null)
            {
                CompleteCurrent(PronunciationPlaybackResult.Unavailable(availability.Message));
                return;
            }
            engine.SpeakAsync(request.RequestId, request.Text, voiceId, rate, volume);
        }
        catch (Exception exception)
        {
            CompleteCurrent(PronunciationPlaybackResult.Failed(exception.Message));
        }
    }

    private void QueueCancellation(long sequence)
    {
        TryPost(() =>
        {
            if (current?.Sequence == sequence) CancelCurrentCore();
        });
    }

    private void OnSpeakCompleted(object? sender, SpeechEngineCompletedEventArgs args) =>
        TryPost(() => CompleteFromEngine(args));

    private void CompleteFromEngine(SpeechEngineCompletedEventArgs args)
    {
        if (current is not { } pending || pending.RequestId != args.RequestId) return;
        if (args.Error is not null) CompleteCurrent(PronunciationPlaybackResult.Failed(args.Error.Message));
        else if (args.Cancelled) CompleteCurrent(PronunciationPlaybackResult.Cancelled());
        else CompleteCurrent(PronunciationPlaybackResult.Completed(pending.Text));
    }

    private void CancelCurrentCore()
    {
        if (current is null) return;
        try { engine?.CancelAll(); }
        catch { }
        CompleteCurrent(PronunciationPlaybackResult.Cancelled());
    }

    private void CompleteCurrent(PronunciationPlaybackResult result)
    {
        if (current is not { } pending) return;
        current = null;
        Complete(pending, result);
    }

    private static void Complete(PendingRequest pending, PronunciationPlaybackResult result)
    {
        pending.Cancellation.Dispose();
        pending.Completion.TrySetResult(result);
    }

    private void CleanupCore()
    {
        CancelCurrentCore();
        if (engine is null) return;
        try { engine.SpeakCompleted -= OnSpeakCompleted; }
        catch { }
        try { engine.Dispose(); }
        catch { }
        finally { engine = null; }
    }

    private bool TryPost(Action action)
    {
        lock (lifecycleGate)
        {
            if (!accepting) return false;
            queue.Add(action);
            return true;
        }
    }

    private bool TryPostLatest(long sequence, Action action)
    {
        lock (lifecycleGate)
        {
            if (!accepting) return false;
            Volatile.Write(ref latestRequest, sequence);
            queue.Add(action);
            return true;
        }
    }

    private static bool IsEnglish(string cultureName)
    {
        try { return string.Equals(CultureInfo.GetCultureInfo(cultureName).TwoLetterISOLanguageName, "en", StringComparison.OrdinalIgnoreCase); }
        catch (CultureNotFoundException) { return false; }
    }

    private static PronunciationAccent AccentFor(string cultureName) => cultureName.ToUpperInvariant() switch
    {
        "EN-GB" => PronunciationAccent.British,
        "EN-US" => PronunciationAccent.American,
        _ => PronunciationAccent.OtherEnglish,
    };

    private static int PreferenceRank(PronunciationVoice voice, PronunciationAccent preference) => preference switch
    {
        PronunciationAccent.British when string.Equals(voice.CultureName, "en-GB", StringComparison.OrdinalIgnoreCase) => 0,
        PronunciationAccent.British when voice.Accent == PronunciationAccent.British => 1,
        PronunciationAccent.American when string.Equals(voice.CultureName, "en-US", StringComparison.OrdinalIgnoreCase) => 0,
        PronunciationAccent.American when voice.Accent == PronunciationAccent.American => 1,
        PronunciationAccent.OtherEnglish when voice.Accent == PronunciationAccent.OtherEnglish => 0,
        _ => 2,
    };

    private sealed class PendingRequest(long sequence, Guid requestId, string text, CancellationToken cancellationToken)
    {
        public long Sequence { get; } = sequence;
        public Guid RequestId { get; } = requestId;
        public string Text { get; } = text;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<PronunciationPlaybackResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Cancellation { get; set; }
    }
}

internal sealed class SystemSpeechEngineFactory : IOfflineSpeechEngineFactory
{
    public IOfflineSpeechEngine Create() => new SystemSpeechEngine();
}

internal sealed class SystemSpeechEngine : IOfflineSpeechEngine
{
    private readonly SpeechSynthesizer synthesizer = new();
    private readonly Dictionary<Prompt, Guid> requests = [];
    private readonly Dictionary<string, string> voiceNames = new(StringComparer.Ordinal);

    public SystemSpeechEngine() => synthesizer.SpeakCompleted += OnSpeakCompleted;

    public event EventHandler<SpeechEngineCompletedEventArgs>? SpeakCompleted;

    public IReadOnlyList<SpeechEngineVoice> GetInstalledVoices()
    {
        var installed = synthesizer.GetInstalledVoices()
            .Select(voice => new SpeechEngineVoice(
                voice.VoiceInfo.Id,
                voice.VoiceInfo.Name,
                voice.VoiceInfo.Culture.Name,
                voice.Enabled))
            .ToArray();
        voiceNames.Clear();
        foreach (var voice in installed) voiceNames[voice.Id] = voice.Name;
        return installed;
    }

    public void SpeakAsync(Guid requestId, string text, string voiceId, int rate, int volume)
    {
        if (!voiceNames.TryGetValue(voiceId, out string? voiceName))
            throw new InvalidOperationException("The selected installed voice is no longer available.");
        synthesizer.SelectVoice(voiceName);
        synthesizer.Rate = rate;
        synthesizer.Volume = volume;
        var prompt = new Prompt(text);
        lock (requests) requests[prompt] = requestId;
        try { synthesizer.SpeakAsync(prompt); }
        catch
        {
            lock (requests) requests.Remove(prompt);
            throw;
        }
    }

    public void CancelAll() => synthesizer.SpeakAsyncCancelAll();

    public void Dispose()
    {
        synthesizer.SpeakCompleted -= OnSpeakCompleted;
        synthesizer.Dispose();
        lock (requests) requests.Clear();
        voiceNames.Clear();
    }

    private void OnSpeakCompleted(object? sender, SpeakCompletedEventArgs args)
    {
        Guid requestId;
        lock (requests)
        {
            if (!requests.Remove(args.Prompt, out requestId)) return;
        }
        SpeakCompleted?.Invoke(this, new(requestId, args.Cancelled, args.Error));
    }
}
