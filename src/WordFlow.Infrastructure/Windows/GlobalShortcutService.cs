using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Windows;

public interface IHotKeyNative
{
    bool TryRegister(nint handle, int id, ShortcutChord chord, out int errorCode);
    bool TryUnregister(nint handle, int id, out int errorCode);
}

public sealed class GlobalShortcutService : IShortcutService
{
    public const int WmHotKey = 0x0312;
    private const int DefaultMaximumRegistrationId = 0xBFFF;
    private const int RegistrationIdExhausted = -2;
    private readonly object gate = new();
    private readonly IHotKeyNative native;
    private readonly IShortcutBindingStore store;
    private readonly IShortcutDispatcher dispatcher;
    private readonly int maximumRegistrationId;
    private readonly Dictionary<ShortcutAction, ShortcutBinding> bindings = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
    private readonly Dictionary<ShortcutAction, Registration> registrations = [];
    private readonly List<Registration> pendingCleanup = [];
    private readonly HashSet<int> reservedIds = [];
    private readonly HashSet<int> retiredIds = [];
    private readonly List<ShortcutRestoreIssue> restoreIssues = [];
    private readonly Dictionary<ShortcutAction, long> bindingVersions =
        Enum.GetValues<ShortcutAction>().ToDictionary(action => action, _ => 0L);
    private ShortcutLifecycleSnapshot lifecycle = new(ShortcutLifecycleState.Ready);
    private nint windowHandle;
    private int nextRegistrationId;
    private long bindingVersion;
    private bool restored;
    private bool disposed;

    public GlobalShortcutService(IHotKeyNative native, IShortcutBindingStore store)
        : this(native, store, new OwnerThreadShortcutDispatcher()) { }

    public GlobalShortcutService(IHotKeyNative native, IShortcutBindingStore store, IShortcutDispatcher dispatcher,
        int maximumRegistrationId = DefaultMaximumRegistrationId)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.maximumRegistrationId = maximumRegistrationId is >= 0 and <= DefaultMaximumRegistrationId
            ? maximumRegistrationId
            : throw new ArgumentOutOfRangeException(nameof(maximumRegistrationId));
    }

    public GlobalShortcutService(SqliteConnectionFactory factory, IShortcutDispatcher dispatcher)
        : this(new Win32HotKeyNative(), new SqliteShortcutBindingStore(factory), dispatcher) { }

    public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings
    {
        get { lock (gate) return new Dictionary<ShortcutAction, ShortcutBinding>(bindings); }
    }

    public IReadOnlyList<ShortcutRestoreIssue> RestoreIssues
    {
        get { lock (gate) return restoreIssues.ToArray(); }
    }

    public ShortcutLifecycleSnapshot Lifecycle
    {
        get { lock (gate) return lifecycle; }
    }

    public event EventHandler<ShortcutAction>? ActionInvoked;
    public event EventHandler<ShortcutBindingChangedEventArgs>? BindingChanged;
    public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted;

    public ShortcutRegistrationResult AttachWindowHandle(nint handle) => dispatcher.Invoke(() => AttachCore(handle));
    public ShortcutRestoreResult RestorePersisted() => dispatcher.Invoke(RestoreCore);
    public ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return dispatcher.Invoke(() => ReplaceCore(action, candidate));
    }
    public ShortcutRegistrationResult ResetAll() => dispatcher.Invoke(ResetCore);
    public bool ProcessWindowMessage(int message, nint id, nint chordData = default) =>
        dispatcher.Invoke(() => ProcessMessageCore(message, id, chordData));
    public void Dispose() => dispatcher.Invoke(DisposeCore);

    private ShortcutRegistrationResult AttachCore(nint handle)
    {
        if (handle == 0) return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, "A valid window handle is required.");
        lock (gate)
        {
            if (disposed) return DisposedResult();
            if (lifecycle.State == ShortcutLifecycleState.Terminal) return TerminalResult();
            RetryPendingCore();
            if (windowHandle == handle) return ShortcutRegistrationResult.Success();
            if (!restored)
            {
                windowHandle = handle;
                return ShortcutRegistrationResult.Success();
            }

            nint oldHandle = windowHandle;
            var previous = CopyBindings();
            var old = registrations.ToDictionary(pair => pair.Key, pair => pair.Value);
            var removed = new Dictionary<ShortcutAction, Registration>();
            var staged = new List<Registration>();
            foreach (var pair in old)
            {
                if (!TryRemoveActive(pair.Key, out var registration, out var error))
                    return RollBack(previous, removed, staged,
                        $"Windows could not release {pair.Value.Chord} from the previous window (error {error}).",
                        ShortcutConflictKind.OperatingSystem, oldHandle);
                removed.Add(pair.Key, registration);
            }
            foreach (var pair in removed)
            {
                if (!TryRegisterSpecificStaged(handle, pair.Value.Id, pair.Key, pair.Value.Chord, out var candidate, out var error))
                    return RollBack(previous, removed, staged,
                        $"Windows could not register {pair.Value.Chord} on the recreated window (error {error}).",
                        ShortcutConflictKind.OperatingSystem, oldHandle);
                staged.Add(candidate);
            }
            windowHandle = handle;
            foreach (var registration in staged) registrations[registration.Action] = registration;
            retiredIds.Clear();
            SetReadyIfRecovered();
            return ShortcutRegistrationResult.Success();
        }
    }

    private ShortcutRestoreResult RestoreCore()
    {
        List<ShortcutBindingChangedEventArgs> changes;
        List<ShortcutRestoreIssue> issues = [];
        lock (gate)
        {
            ThrowIfUnavailable();
            EnsureHandle();
            RetryPendingCore();
            if (pendingCleanup.Count > 0) throw new InvalidOperationException("Windows cleanup is still pending.");
            var target = ParseAndSanitize(store.Load(), issues);
            var previous = CopyBindings();
            var removed = new Dictionary<ShortcutAction, Registration>();
            var staged = new List<Registration>();
            foreach (var action in registrations.Keys.ToArray())
            {
                if (!TryRemoveActive(action, out var registration, out var error))
                {
                    var rollback = RollBack(previous, removed, staged,
                        $"Could not release an active shortcut during restore (error {error}).",
                        ShortcutConflictKind.OperatingSystem, windowHandle);
                    throw new InvalidOperationException(rollback.ConflictReason);
                }
                removed.Add(action, registration);
            }
            foreach (var pair in target.Where(pair => IsActiveGlobal(pair.Value)))
            {
                if (TryRegisterNewStaged(windowHandle, pair.Key, pair.Value.Chord, out var registration, out var error)) staged.Add(registration);
                else
                {
                    target[pair.Key] = pair.Value with { IsEnabled = false };
                    issues.Add(new(pair.Key, error == RegistrationIdExhausted ? ShortcutConflictKind.Lifecycle : ShortcutConflictKind.OperatingSystem,
                        error == RegistrationIdExhausted ? "No legal Windows hotkey registration ID is available."
                            : $"Windows rejected {pair.Value.DisplayText} (error {error})."));
                }
            }
            Exception? persistenceDisposeFailure;
            try { persistenceDisposeFailure = Persist(target); }
            catch (Exception exception) when (IsPersistenceException(exception))
            {
                var rollback = RollBack(previous, removed, staged, $"Could not persist restored shortcuts: {exception.Message}",
                    ShortcutConflictKind.Persistence, windowHandle);
                throw new InvalidOperationException(rollback.ConflictReason, exception);
            }
            foreach (var registration in staged) registrations[registration.Action] = registration;
            changes = ApplyBindings(target);
            restoreIssues.Clear();
            restoreIssues.AddRange(issues);
            restored = true;
            SetReadyIfRecovered();
            if (persistenceDisposeFailure is not null)
                SetLifecycle(ShortcutLifecycleState.Degraded,
                    $"Restored shortcuts were committed, but closing their persistence transaction failed: {persistenceDisposeFailure.Message}");
        }
        PublishChanges(changes);
        return new(issues);
    }

    private ShortcutRegistrationResult ReplaceCore(ShortcutAction action, ShortcutBinding candidate)
    {
        ShortcutBindingChangedEventArgs? change = null;
        ShortcutRegistrationResult result;
        lock (gate)
        {
            if (disposed) return DisposedResult();
            if (lifecycle.State == ShortcutLifecycleState.Terminal) return TerminalResult();
            EnsureReady();
            RetryPendingCore();
            if (pendingCleanup.Count > 0)
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, "A previous cleanup is still pending.", bindings.GetValueOrDefault(action));
            if (!Enum.IsDefined(action)) return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.InvalidBinding, "Unknown shortcut action.");
            var previousBinding = bindings[action];
            if (candidate.Chord.Validate() is { } validation)
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Reserved, validation, previousBinding);
            var duplicate = bindings.FirstOrDefault(pair => pair.Key != action && pair.Value.IsEnabled && candidate.IsEnabled && pair.Value.Chord == candidate.Chord);
            if (!duplicate.Equals(default(KeyValuePair<ShortcutAction, ShortcutBinding>)))
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.ApplicationDuplicate,
                    $"{candidate.DisplayText} is already assigned to {duplicate.Key}.", previousBinding);
            if (previousBinding == candidate) return ShortcutRegistrationResult.Success(candidate);

            var previous = CopyBindings();
            bool reuse = IsActiveGlobal(previousBinding) && IsActiveGlobal(candidate) && previousBinding.Chord == candidate.Chord;
            var staged = new List<Registration>();
            if (IsActiveGlobal(candidate) && !reuse)
            {
                if (!TryRegisterNewStaged(windowHandle, action, candidate.Chord, out var registration, out var error))
                    return ShortcutRegistrationResult.Conflict(error == RegistrationIdExhausted ? ShortcutConflictKind.Lifecycle : ShortcutConflictKind.OperatingSystem,
                        error == RegistrationIdExhausted ? "No legal Windows hotkey registration ID is available."
                            : $"Windows rejected {candidate.DisplayText} (error {error}).", previousBinding);
                staged.Add(registration);
            }
            var target = previous.ToDictionary(pair => pair.Key, pair => pair.Value);
            target[action] = candidate;
            IShortcutBindingStoreTransaction transaction;
            try { transaction = store.BeginReplace(ToStored(target)); }
            catch (Exception exception) when (IsPersistenceException(exception))
            {
                CleanupStaged(staged);
                return FailureAfterCleanup(ShortcutConflictKind.Persistence, $"Could not stage setting: {exception.Message}", previousBinding);
            }
            var removed = new Dictionary<ShortcutAction, Registration>();
            string? nativeFailure = null;
            Exception? commitFailure = null;
            Exception? disposeFailure = null;
            bool committed = false;
            try
            {
                if (registrations.ContainsKey(action) && !reuse)
                {
                    if (!TryRemoveActive(action, out var oldRegistration, out var error))
                        nativeFailure = $"Windows could not release the old shortcut (error {error}).";
                    else removed.Add(action, oldRegistration);
                }
                if (nativeFailure is null)
                {
                    try { transaction.Commit(); committed = true; }
                    catch (Exception exception) when (IsPersistenceException(exception)) { commitFailure = exception; }
                }
            }
            finally
            {
                try { transaction.Dispose(); }
                catch (Exception exception) when (IsPersistenceException(exception)) { disposeFailure = exception; }
            }
            if (!committed)
            {
                string failure = nativeFailure ?? $"Could not commit setting: {commitFailure?.Message ?? disposeFailure?.Message ?? "unknown persistence failure"}";
                if (disposeFailure is not null) failure += $" Transaction disposal also failed: {disposeFailure.Message}";
                return RollBack(previous, removed, staged, failure,
                    nativeFailure is null ? ShortcutConflictKind.Persistence : ShortcutConflictKind.OperatingSystem, windowHandle);
            }
            foreach (var registration in staged) registrations[action] = registration;
            bindings[action] = candidate;
            restoreIssues.RemoveAll(issue => issue.Action == action);
            change = new(action, candidate, ++bindingVersion);
            bindingVersions[action] = change.Version;
            if (disposeFailure is not null)
                SetLifecycle(ShortcutLifecycleState.Degraded,
                    $"The shortcut was committed, but closing its persistence transaction failed: {disposeFailure.Message}");
            result = ShortcutRegistrationResult.Success(candidate);
        }
        if (change is not null) PublishChange(change);
        return result;
    }

    private ShortcutRegistrationResult ResetCore()
    {
        List<ShortcutBindingChangedEventArgs>? changes = null;
        ShortcutRegistrationResult result;
        lock (gate)
        {
            if (disposed) return DisposedResult();
            if (lifecycle.State == ShortcutLifecycleState.Terminal) return TerminalResult();
            EnsureReady();
            RetryPendingCore();
            if (pendingCleanup.Count > 0) return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, "A previous cleanup is still pending.");
            var target = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (bindings.All(pair => target[pair.Key] == pair.Value)) return ShortcutRegistrationResult.Success();
            var previous = CopyBindings();
            IShortcutBindingStoreTransaction transaction;
            try { transaction = store.BeginReplace(ToStored(target)); }
            catch (Exception exception) when (IsPersistenceException(exception))
            { return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Persistence, exception.Message); }
            var removed = new Dictionary<ShortcutAction, Registration>();
            var staged = new List<Registration>();
            string? nativeFailure = null;
            Exception? commitFailure = null;
            Exception? disposeFailure = null;
            bool committed = false;
            try
            {
                foreach (var action in registrations.Keys.ToArray())
                {
                    if (!TryRemoveActive(action, out var registration, out var error))
                    { nativeFailure = $"Windows could not stage reset (error {error})."; break; }
                    removed.Add(action, registration);
                }
                if (nativeFailure is null)
                {
                    foreach (var pair in target.Where(pair => IsActiveGlobal(pair.Value)))
                    {
                        if (!TryRegisterNewStaged(windowHandle, pair.Key, pair.Value.Chord, out var registration, out var error))
                        { nativeFailure = error == RegistrationIdExhausted ? "No legal registration ID is available during reset."
                            : $"Windows could not register {pair.Value.DisplayText} during reset (error {error})."; break; }
                        staged.Add(registration);
                    }
                }
                if (nativeFailure is null)
                {
                    try { transaction.Commit(); committed = true; }
                    catch (Exception exception) when (IsPersistenceException(exception)) { commitFailure = exception; }
                }
            }
            finally
            {
                try { transaction.Dispose(); }
                catch (Exception exception) when (IsPersistenceException(exception)) { disposeFailure = exception; }
            }
            if (!committed)
            {
                string failure = nativeFailure ?? $"Could not commit reset: {commitFailure?.Message ?? disposeFailure?.Message ?? "unknown persistence failure"}";
                if (disposeFailure is not null) failure += $" Transaction disposal also failed: {disposeFailure.Message}";
                return RollBack(previous, removed, staged, failure,
                    nativeFailure is null ? ShortcutConflictKind.Persistence : ShortcutConflictKind.OperatingSystem, windowHandle);
            }
            foreach (var registration in staged) registrations[registration.Action] = registration;
            changes = ApplyBindings(target);
            restoreIssues.Clear();
            SetReadyIfRecovered();
            if (disposeFailure is not null)
                SetLifecycle(ShortcutLifecycleState.Degraded,
                    $"The reset was committed, but closing its persistence transaction failed: {disposeFailure.Message}");
            result = ShortcutRegistrationResult.Success();
        }
        PublishChanges(changes);
        return result;
    }

    private bool ProcessMessageCore(int message, nint idValue, nint chordData)
    {
        ShortcutAction action;
        lock (gate)
        {
            if (disposed || message != WmHotKey) return false;
            var match = registrations.FirstOrDefault(pair => pair.Value.Id == idValue.ToInt32());
            if (match.Equals(default(KeyValuePair<ShortcutAction, Registration>))) return false;
            if (chordData != 0)
            {
                long encoded = chordData.ToInt64();
                if ((int)(encoded & 0xFFFF) != (int)match.Value.Chord.Modifiers ||
                    (int)((encoded >> 16) & 0xFFFF) != match.Value.Chord.VirtualKey) return false;
            }
            action = match.Key;
        }
        PublishAction(action);
        return true;
    }

    private void DisposeCore()
    {
        List<Exception> failures = [];
        lock (gate)
        {
            if (disposed) return;
            foreach (var action in registrations.Keys.ToArray())
            {
                var registration = registrations[action];
                registrations.Remove(action);
                if (native.TryUnregister(registration.Handle, registration.Id, out var error)) Retire(registration.Id);
                else TrackPending(registration, error);
            }
            RetryPendingCore();
            foreach (var pending in pendingCleanup) failures.Add(new Win32Exception($"Could not clean up {pending.Chord} registration {pending.Id}."));
            if (failures.Count > 0) SetLifecycle(ShortcutLifecycleState.Terminal,
                "Disposal could not release every Windows shortcut; remaining IDs are non-routable and tracked.");
            else { disposed = true; SetLifecycle(ShortcutLifecycleState.Disposed, "All Windows shortcuts were released."); }
        }
        if (failures.Count > 0) throw new AggregateException("One or more Windows shortcut registrations could not be released.", failures);
    }

    private ShortcutRegistrationResult RollBack(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> previous,
        IReadOnlyDictionary<ShortcutAction, Registration> removed, IReadOnlyCollection<Registration> staged,
        string failure, ShortcutConflictKind originalKind, nint restoreHandle)
    {
        CleanupStaged(staged);
        var reconciled = previous.ToDictionary(pair => pair.Key, pair => pair.Value);
        List<string> reconstructionFailures = [];
        foreach (var pair in removed)
        {
            if (TryRegisterSpecificActive(restoreHandle, pair.Value.Id, pair.Key, pair.Value.Chord, out var error)) continue;
            reconciled[pair.Key] = previous[pair.Key] with { IsEnabled = false };
            reconstructionFailures.Add($"{pair.Key}/{pair.Value.Chord} (error {error})");
        }
        windowHandle = restoreHandle;
        Exception? reconciliationDisposeFailure;
        try { reconciliationDisposeFailure = Persist(reconciled); }
        catch (Exception exception) when (IsPersistenceException(exception))
        {
            var terminalChanges = ApplyBindings(reconciled);
            SetLifecycle(ShortcutLifecycleState.Terminal,
                $"{failure} SQLite reconciliation failed after Windows rollback: {exception.Message}");
            PublishChanges(terminalChanges);
            return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, lifecycle.Reason!,
                removed.Count == 1 ? reconciled[removed.Keys.Single()] : null);
        }
        if (reconstructionFailures.Count > 0)
        {
            var changes = ApplyBindings(reconciled);
            SetLifecycle(ShortcutLifecycleState.Degraded,
                $"{failure} Disabled bindings Windows could not reconstruct: {string.Join(", ", reconstructionFailures)}.");
            PublishChanges(changes);
        }
        else if (pendingCleanup.Count > 0)
            SetLifecycle(ShortcutLifecycleState.Degraded, $"{failure} Candidate cleanup remains tracked and non-routable.");
        else if (reconciliationDisposeFailure is not null)
            SetLifecycle(ShortcutLifecycleState.Degraded,
                $"{failure} The reconciled snapshot was committed, but closing its persistence transaction failed: {reconciliationDisposeFailure.Message}");

        if (reconstructionFailures.Count > 0 || pendingCleanup.Count > 0 || reconciliationDisposeFailure is not null)
            return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, lifecycle.Reason!,
                removed.Count == 1 ? reconciled[removed.Keys.Single()] : null);
        return ShortcutRegistrationResult.Conflict(originalKind, failure,
            removed.Count == 1 ? previous[removed.Keys.Single()] : null);
    }

    private ShortcutRegistrationResult FailureAfterCleanup(ShortcutConflictKind kind, string reason, ShortcutBinding actual)
    {
        if (pendingCleanup.Count == 0) return ShortcutRegistrationResult.Conflict(kind, reason, actual);
        SetLifecycle(ShortcutLifecycleState.Degraded, $"{reason} Candidate cleanup remains tracked and non-routable.");
        return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, lifecycle.Reason!, actual);
    }

    private Dictionary<ShortcutAction, ShortcutBinding> ParseAndSanitize(IReadOnlyList<StoredShortcutBinding> rows, List<ShortcutRestoreIssue> issues)
    {
        var target = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var unknown in rows.Where(row => !Enum.TryParse<ShortcutAction>(row.Command, true, out var action) || !Enum.IsDefined(action)))
            issues.Add(new(null, ShortcutConflictKind.InvalidBinding, $"Unknown persisted shortcut action '{unknown.Command}' was removed."));
        foreach (var action in Enum.GetValues<ShortcutAction>())
        {
            var matching = rows.Where(row => row.Command.Equals(action.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0) continue;
            ShortcutBinding? parsed = null;
            string? validation = null;
            bool malformed = matching.Length != 1 || !matching[0].TryParse(out var encoded, out parsed) || encoded != action ||
                (validation = parsed!.Chord.Validate()) is not null;
            if (malformed)
            {
                target[action] = ShortcutDefaults.For(action) with { IsEnabled = false };
                issues.Add(new(action, ShortcutConflictKind.InvalidBinding,
                    matching.Length != 1 ? "The persisted action has duplicate rows." : validation ?? "The persisted shortcut is malformed."));
            }
            else target[action] = parsed!;
        }
        foreach (var group in target.Where(pair => pair.Value.IsEnabled).GroupBy(pair => pair.Value.Chord))
        {
            var overlap = group.ToArray();
            if (overlap.Length < 2) continue;
            var winner = overlap.FirstOrDefault(pair => ShortcutDefaults.For(pair.Key).Chord == group.Key);
            if (winner.Equals(default(KeyValuePair<ShortcutAction, ShortcutBinding>))) winner = overlap[0];
            foreach (var loser in overlap.Where(pair => pair.Key != winner.Key))
            {
                target[loser.Key] = loser.Value with { IsEnabled = false };
                issues.Add(new(loser.Key, ShortcutConflictKind.ApplicationDuplicate, $"{loser.Value.DisplayText} is also assigned to {winner.Key}."));
            }
        }
        return target;
    }

    private List<ShortcutBindingChangedEventArgs> ApplyBindings(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> target)
    {
        long version = ++bindingVersion;
        var changes = target.OrderBy(pair => pair.Key)
            .Where(pair => !bindings.TryGetValue(pair.Key, out var old) || old != pair.Value)
            .Select(pair => new ShortcutBindingChangedEventArgs(pair.Key, pair.Value, version)).ToList();
        foreach (var change in changes) bindingVersions[change.Action] = version;
        bindings.Clear();
        foreach (var pair in target) bindings[pair.Key] = pair.Value;
        return changes;
    }

    private bool TryRegisterNewStaged(nint handle, ShortcutAction action, ShortcutChord chord, out Registration registration, out int error)
    {
        if (!TryAllocateId(out var id)) { registration = default; error = RegistrationIdExhausted; return false; }
        if (native.TryRegister(handle, id, chord, out error)) { registration = new(handle, id, action, chord); return true; }
        reservedIds.Remove(id);
        registration = default;
        return false;
    }

    private bool TryRegisterSpecificStaged(nint handle, int id, ShortcutAction action, ShortcutChord chord,
        out Registration registration, out int error)
    {
        if (reservedIds.Contains(id)) { registration = default; error = RegistrationIdExhausted; return false; }
        retiredIds.Remove(id);
        reservedIds.Add(id);
        if (native.TryRegister(handle, id, chord, out error)) { registration = new(handle, id, action, chord); return true; }
        reservedIds.Remove(id);
        retiredIds.Add(id);
        registration = default;
        return false;
    }

    private bool TryRegisterSpecificActive(nint handle, int id, ShortcutAction action, ShortcutChord chord, out int error)
    {
        if (!TryRegisterSpecificStaged(handle, id, action, chord, out var registration, out error)) return false;
        registrations[action] = registration;
        return true;
    }

    private bool TryRemoveActive(ShortcutAction action, out Registration registration, out int error)
    {
        if (!registrations.Remove(action, out registration)) { error = 0; return true; }
        if (native.TryUnregister(registration.Handle, registration.Id, out error))
        { reservedIds.Remove(registration.Id); retiredIds.Add(registration.Id); return true; }
        registrations[action] = registration;
        return false;
    }

    private void CleanupStaged(IEnumerable<Registration> staged)
    {
        foreach (var registration in staged)
        {
            if (native.TryUnregister(registration.Handle, registration.Id, out var error)) Retire(registration.Id);
            else TrackPending(registration, error);
        }
    }

    private void TrackPending(Registration registration, int error)
    {
        if (!pendingCleanup.Contains(registration)) pendingCleanup.Add(registration);
        reservedIds.Add(registration.Id);
        restoreIssues.RemoveAll(issue => issue.Action is null && issue.Kind == ShortcutConflictKind.Lifecycle);
        restoreIssues.Add(new(null, ShortcutConflictKind.Lifecycle, $"Windows cleanup is pending for {registration.Chord} (error {error})."));
    }

    private void RetryPendingCore()
    {
        for (int index = pendingCleanup.Count - 1; index >= 0; index--)
        {
            var registration = pendingCleanup[index];
            if (!native.TryUnregister(registration.Handle, registration.Id, out _)) continue;
            pendingCleanup.RemoveAt(index);
            Retire(registration.Id);
        }
        if (pendingCleanup.Count == 0) restoreIssues.RemoveAll(issue => issue.Action is null && issue.Kind == ShortcutConflictKind.Lifecycle);
    }

    private bool TryAllocateId(out int id)
    {
        int count = maximumRegistrationId + 1;
        for (int attempt = 0; attempt < count; attempt++)
        {
            int candidate = nextRegistrationId;
            nextRegistrationId = nextRegistrationId == maximumRegistrationId ? 0 : nextRegistrationId + 1;
            if (reservedIds.Contains(candidate) || retiredIds.Contains(candidate)) continue;
            reservedIds.Add(candidate);
            id = candidate;
            return true;
        }
        id = -1;
        return false;
    }

    private void Retire(int id) { reservedIds.Remove(id); retiredIds.Add(id); }
    private Exception? Persist(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> snapshot)
    {
        var transaction = store.BeginReplace(ToStored(snapshot));
        Exception? commitFailure = null;
        Exception? disposeFailure = null;
        try { transaction.Commit(); }
        catch (Exception exception) { commitFailure = exception; }
        try { transaction.Dispose(); }
        catch (Exception exception) { disposeFailure = exception; }
        if (commitFailure is not null) throw commitFailure;
        return disposeFailure;
    }
    private Dictionary<ShortcutAction, ShortcutBinding> CopyBindings() => bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
    private void SetLifecycle(ShortcutLifecycleState state, string? reason) => lifecycle = new(state, reason);
    private void SetReadyIfRecovered()
    { if (pendingCleanup.Count == 0 && lifecycle.State != ShortcutLifecycleState.Terminal) SetLifecycle(ShortcutLifecycleState.Ready, null); }
    private ShortcutRegistrationResult TerminalResult() => ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle,
        lifecycle.Reason ?? "The shortcut service is terminal.");
    private static ShortcutRegistrationResult DisposedResult() => ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle,
        "The shortcut service has been disposed.");
    private void EnsureHandle() { if (windowHandle == 0) throw new InvalidOperationException("Attach a window handle first."); }
    private void EnsureReady() { EnsureHandle(); if (!restored) throw new InvalidOperationException("Restore shortcuts first."); }
    private void ThrowIfUnavailable()
    { ObjectDisposedException.ThrowIf(disposed, this); if (lifecycle.State == ShortcutLifecycleState.Terminal) throw new InvalidOperationException(lifecycle.Reason); }

    private void PublishChanges(IEnumerable<ShortcutBindingChangedEventArgs>? changes)
    { if (changes is not null) foreach (var change in changes) PublishChange(change); }
    private void PublishAction(ShortcutAction action)
    {
        if (ActionInvoked is not { } handlers) return;
        foreach (EventHandler<ShortcutAction> handler in handlers.GetInvocationList())
        { try { handler(this, action); } catch (Exception exception) { PublishCallbackFault(exception); } }
    }
    private void PublishChange(ShortcutBindingChangedEventArgs change)
    {
        if (!IsCurrent(change)) return;
        if (BindingChanged is not { } handlers) return;
        foreach (EventHandler<ShortcutBindingChangedEventArgs> handler in handlers.GetInvocationList())
        {
            if (!IsCurrent(change)) break;
            try { handler(this, change); }
            catch (Exception exception) { PublishCallbackFault(exception); }
        }
    }
    private bool IsCurrent(ShortcutBindingChangedEventArgs change)
    { lock (gate) return bindingVersions[change.Action] == change.Version; }
    private void PublishCallbackFault(Exception exception)
    {
        if (CallbackFaulted is not { } handlers) return;
        foreach (EventHandler<ShortcutCallbackFaultedEventArgs> handler in handlers.GetInvocationList())
        { try { handler(this, new(exception)); } catch { } }
    }

    private static IReadOnlyCollection<StoredShortcutBinding> ToStored(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> source) =>
        source.OrderBy(pair => pair.Key).Select(pair => StoredShortcutBinding.From(pair.Key, pair.Value)).ToArray();
    private static bool IsActiveGlobal(ShortcutBinding binding) => binding.IsEnabled && binding.Scope == ShortcutScope.Global;
    private static bool IsPersistenceException(Exception exception) => exception is IOException or SqliteException or UnauthorizedAccessException;
    private static Win32Exception NativeFailure(string operation, int error) => new(error, $"Could not {operation} (Win32 error {error}).");
    private readonly record struct Registration(nint Handle, int Id, ShortcutAction Action, ShortcutChord Chord);
}

public sealed class OwnerThreadShortcutDispatcher : IShortcutDispatcher
{
    private readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    public bool CheckAccess() => Environment.CurrentManagedThreadId == ownerThreadId;
    public T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!CheckAccess()) throw new InvalidOperationException("Shortcut work must run on the HWND owner thread.");
        return action();
    }
    public void Invoke(Action action) => Invoke(() => { action(); return true; });
}

internal sealed class Win32HotKeyNative : IHotKeyNative
{
    private const uint ModNoRepeat = 0x4000;
    public bool TryRegister(nint handle, int id, ShortcutChord chord, out int errorCode)
    {
        bool ok = RegisterHotKey(handle, id, (uint)chord.Modifiers | ModNoRepeat, (uint)chord.VirtualKey);
        errorCode = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
    public bool TryUnregister(nint handle, int id, out int errorCode)
    {
        bool ok = UnregisterHotKey(handle, id);
        errorCode = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint hWnd, int id);
}
