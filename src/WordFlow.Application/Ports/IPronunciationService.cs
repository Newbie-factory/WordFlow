namespace WordFlow.Application.Ports;

public enum PronunciationAccent
{
    Automatic,
    British,
    American,
    OtherEnglish,
}

public sealed record PronunciationVoice(string Id, string Name, string CultureName, PronunciationAccent Accent);

public static class PronunciationVoiceIdentity
{
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    public static bool Equals(string? left, string? right) => Comparer.Equals(left, right);
}

public enum PronunciationInventoryState
{
    AuthoritativeAvailable,
    AuthoritativeEmpty,
    UnavailableConflict,
    UnavailableFault,
}

public sealed record PronunciationAvailability
{
    public PronunciationAvailability(
        bool isAvailable,
        string message,
        PronunciationInventoryState inventoryState,
        string? faultInfo = null,
        IReadOnlyList<string>? quarantinedVoiceIds = null)
    {
        IsAvailable = isAvailable;
        Message = message;
        InventoryState = inventoryState;
        FaultInfo = faultInfo;
        QuarantinedVoiceIds = quarantinedVoiceIds?.ToArray() ?? [];
    }

    public bool IsAvailable { get; }
    public string Message { get; }
    public PronunciationInventoryState InventoryState { get; }
    public string? FaultInfo { get; }
    public IReadOnlyList<string> QuarantinedVoiceIds { get; }
}

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
