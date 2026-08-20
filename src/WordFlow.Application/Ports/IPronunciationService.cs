namespace WordFlow.Application.Ports;

public enum PronunciationAccent
{
    Automatic,
    British,
    American,
    OtherEnglish,
}

public sealed record PronunciationVoice(string Id, string Name, string CultureName, PronunciationAccent Accent);

public enum PronunciationInventoryState
{
    AuthoritativeAvailable,
    AuthoritativeEmpty,
    UnavailableFault,
}

public sealed record PronunciationAvailability(
    bool IsAvailable,
    string Message,
    PronunciationInventoryState InventoryState,
    string? FaultInfo = null);

public enum PronunciationPlaybackStatus
{
    Completed,
    Cancelled,
    Failed,
    Unavailable,
}

public sealed record PronunciationPlaybackResult(PronunciationPlaybackStatus Status, string Message)
{
    public static PronunciationPlaybackResult Completed(string text) =>
        new(PronunciationPlaybackStatus.Completed, $"已播放 {text} 的离线发音");

    public static PronunciationPlaybackResult Cancelled() =>
        new(PronunciationPlaybackStatus.Cancelled, "发音已取消");

    public static PronunciationPlaybackResult Failed(string message) =>
        new(PronunciationPlaybackStatus.Failed, $"离线发音失败：{message}");

    public static PronunciationPlaybackResult Unavailable(string message) =>
        new(PronunciationPlaybackStatus.Unavailable, message);
}

public interface IPronunciationService : IDisposable
{
    IReadOnlyList<PronunciationVoice> Voices { get; }
    PronunciationAvailability Availability { get; }
    PronunciationVoice? SelectVoice(string? voiceId, PronunciationAccent preference);
    Task<PronunciationPlaybackResult> SpeakAsync(
        string text,
        string voiceId,
        int rate,
        int volume,
        CancellationToken ct);
    Task CancelAsync();
}
