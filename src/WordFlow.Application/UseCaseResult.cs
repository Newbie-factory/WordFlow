namespace WordFlow.Application;

public abstract record UseCaseResult<T>;

public sealed record Success<T>(T Value) : UseCaseResult<T>;

public sealed record Conflict<T>(string Message) : UseCaseResult<T>;

public sealed record StorageFailure<T>(string Message) : UseCaseResult<T>;

public sealed record NotFound<T>(string Message) : UseCaseResult<T>;
