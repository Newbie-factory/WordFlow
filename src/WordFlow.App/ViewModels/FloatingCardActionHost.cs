namespace WordFlow.App.ViewModels;

public enum FloatingCardActionStatus { Completed, Unavailable, Failed }

public sealed record FloatingCardActionCapability(bool IsAvailable, string HelpText);

public sealed record FloatingCardActionResult(FloatingCardActionStatus Status, string AccessibleMessage)
{
    public bool IsError => Status == FloatingCardActionStatus.Failed;
    public static FloatingCardActionResult Completed(string message) => new(FloatingCardActionStatus.Completed, message);
    public static FloatingCardActionResult Unavailable(string message) => new(FloatingCardActionStatus.Unavailable, message);
    public static FloatingCardActionResult Failed(string message) => new(FloatingCardActionStatus.Failed, message);
}

public interface IFloatingCardActionPort
{
    FloatingCardActionResult Execute(Guid wordId, string word);
}

public interface ICapabilityAwareFloatingCardActionPort : IFloatingCardActionPort
{
    FloatingCardActionCapability Capability { get; }
}

public sealed class DelegateFloatingCardActionPort(Func<Guid, string, FloatingCardActionResult> execute) : IFloatingCardActionPort
{
    public FloatingCardActionResult Execute(Guid wordId, string word) => execute(wordId, word);
}

public interface IFloatingCardActionHost
{
    FloatingCardActionCapability Capability(RelationActionKind action);
    FloatingCardActionResult Dispatch(RelationActionRequestedEventArgs request);
}

public sealed class FloatingCardActionHost : IFloatingCardActionHost
{
    private const string UnavailableCopy = "功能将在对应离线模块就绪后可用";
    private readonly IReadOnlyDictionary<RelationActionKind, IFloatingCardActionPort> ports;

    public FloatingCardActionHost(IFloatingCardActionPort? offlineSpeech = null, IFloatingCardActionPort? details = null, IFloatingCardActionPort? addToLearning = null)
    {
        var registered = new Dictionary<RelationActionKind, IFloatingCardActionPort>();
        if (offlineSpeech is not null) registered[RelationActionKind.Speak] = offlineSpeech;
        if (details is not null) registered[RelationActionKind.OpenDetails] = details;
        if (addToLearning is not null) registered[RelationActionKind.AddToLearning] = addToLearning;
        ports = registered;
    }

    public static IFloatingCardActionHost Unavailable { get; } = new FloatingCardActionHost();

    public FloatingCardActionCapability Capability(RelationActionKind action)
    {
        if (!ports.TryGetValue(action, out var port)) return new(false, UnavailableCopy);
        if (port is ICapabilityAwareFloatingCardActionPort aware) return aware.Capability;
        return new(true, action switch
        {
            RelationActionKind.Speak => "播放离线发音",
            RelationActionKind.OpenDetails => "打开完整词条",
            RelationActionKind.AddToLearning => "加入学习队列",
            _ => "执行词条操作",
        });
    }

    public FloatingCardActionResult Dispatch(RelationActionRequestedEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ports.TryGetValue(request.Action, out var port)) return FloatingCardActionResult.Unavailable(UnavailableCopy);
        var capability = Capability(request.Action);
        if (!capability.IsAvailable) return FloatingCardActionResult.Unavailable(capability.HelpText);
        try { return port.Execute(request.WordId, request.Word) ?? FloatingCardActionResult.Failed("词条操作未返回结果"); }
        catch (Exception exception) { return FloatingCardActionResult.Failed($"词条操作失败：{exception.Message}"); }
    }
}
