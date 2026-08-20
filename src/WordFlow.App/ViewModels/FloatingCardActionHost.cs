namespace WordFlow.App.ViewModels;

public interface IFloatingCardActionHost
{
    FloatingCardActionResult Dispatch(RelationActionRequestedEventArgs request);
}

public sealed record FloatingCardActionResult(string AccessibleMessage, bool IsError = false);

public sealed class FloatingCardActionHost(
    Action<Guid, string> requestOfflineSpeech,
    Action<Guid, string> openDetails,
    Action<Guid, string> requestAddToLearning) : IFloatingCardActionHost
{
    public FloatingCardActionResult Dispatch(RelationActionRequestedEventArgs request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            return request.Action switch
            {
                RelationActionKind.Speak => Invoke(requestOfflineSpeech, request, $"已提交 {request.Word} 的离线发音请求"),
                RelationActionKind.OpenDetails => Invoke(openDetails, request, $"已打开 {request.Word} 的完整词条"),
                RelationActionKind.AddToLearning => Invoke(requestAddToLearning, request, $"已提交 {request.Word} 的加入学习请求"),
                _ => new("无法处理未知的词条操作", true),
            };
        }
        catch (Exception exception) { return new($"词条操作失败：{exception.Message}", true); }
    }

    private static FloatingCardActionResult Invoke(Action<Guid, string> owner, RelationActionRequestedEventArgs request, string message)
    {
        owner(request.WordId, request.Word);
        return new(message);
    }
}
