using WordFlow.Application.Shortcuts;

namespace WordFlow.Application.Ports;

public enum ShortcutConflictKind
{
    None,
    InvalidBinding,
    Reserved,
    ApplicationDuplicate,
    OperatingSystem,
    Persistence,
    Lifecycle,
}

public enum ShortcutLifecycleState
{
    Ready,
    Degraded,
    Terminal,
    Disposed,
}

public sealed record ShortcutLifecycleSnapshot(ShortcutLifecycleState State, string? Reason = null);

public sealed record ShortcutRegistrationResult(
    bool Succeeded,
    ShortcutConflictKind ConflictKind,
    string? ConflictReason,
    ShortcutBinding? Binding)
{
    public static ShortcutRegistrationResult Success(ShortcutBinding? binding = null) =>
        new(true, ShortcutConflictKind.None, null, binding);

    public static ShortcutRegistrationResult Conflict(ShortcutConflictKind kind, string reason, ShortcutBinding? binding = null) =>
        new(false, kind, reason, binding);
}

public sealed record ShortcutRestoreIssue(ShortcutAction? Action, ShortcutConflictKind Kind, string Reason);

public sealed record ShortcutRestoreResult(IReadOnlyList<ShortcutRestoreIssue> Issues);

public sealed class ShortcutBindingChangedEventArgs(ShortcutAction action, ShortcutBinding binding, long version = 0) : EventArgs
{
    public ShortcutAction Action { get; } = action;
    public ShortcutBinding Binding { get; } = binding;
    public long Version { get; } = version;
}

public sealed class ShortcutCallbackFaultedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception ?? throw new ArgumentNullException(nameof(exception));
}

public interface IShortcutService : IDisposable
{
    IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings { get; }
    IReadOnlyList<ShortcutRestoreIssue> RestoreIssues { get; }
    ShortcutLifecycleSnapshot Lifecycle { get; }
    event EventHandler<ShortcutAction>? ActionInvoked;
    event EventHandler<ShortcutBindingChangedEventArgs>? BindingChanged;
    event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted;
    ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate);
    ShortcutRegistrationResult ResetAll();
    ShortcutRestoreResult RestorePersisted();
    ShortcutRegistrationResult AttachWindowHandle(nint handle);
    bool ProcessWindowMessage(int message, nint id, nint chordData);
}

public interface IShortcutDispatcher
{
    bool CheckAccess();
    T Invoke<T>(Func<T> action);
    void Invoke(Action action);
}

public sealed record StoredShortcutBinding(string Command, string Value)
{
    public static StoredShortcutBinding From(ShortcutAction action, ShortcutBinding binding) =>
        new(action.ToString(), $"{binding}|action={action}");

    public bool TryParse(out ShortcutAction action, out ShortcutBinding? binding)
    {
        binding = null;
        if (!Enum.TryParse(Command, true, out action) || !Enum.IsDefined(action) ||
            !ShortcutBinding.TryParse(Value, out binding)) return false;
        var parts = Value.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length == 4)
        {
            if (!Enum.TryParse<ShortcutAction>(parts[3].AsSpan("action=".Length), true, out var encodedAction) ||
                encodedAction != action) return false;
        }
        return true;
    }
}

public interface IShortcutBindingStore
{
    IReadOnlyList<StoredShortcutBinding> Load();
    IShortcutBindingStoreTransaction BeginReplace(IReadOnlyCollection<StoredShortcutBinding> replacement);
}

public interface IShortcutBindingStoreTransaction : IDisposable
{
    void Commit();
}
