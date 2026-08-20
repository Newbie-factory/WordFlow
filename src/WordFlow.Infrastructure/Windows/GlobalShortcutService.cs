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
    private const string PendingCleanupCause = "native-cleanup";
    private const string TransactionDisposeCause = "persistence-transaction-dispose";
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
    private readonly Queue<ShortcutBindingChangedEventArgs> queuedChanges = [];
    private readonly Dictionary<string, string> degradationCauses = [];
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

    public ShortcutRegistrationResult AttachWindowHandle(nint handle) =>
        dispatcher.Invoke(() => ExecuteWithEventDrain(() => AttachCore(handle)));
    public ShortcutRestoreResult RestorePersisted() => dispatcher.Invoke(() => ExecuteWithEventDrain(RestoreCore));
    public ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return dispatcher.Invoke(() => ExecuteWithEventDrain(() => ReplaceCore(action, candidate)));
    }
    public ShortcutRegistrationResult ResetAll() => dispatcher.Invoke(() => ExecuteWithEventDrain(ResetCore));
    public bool ProcessWindowMessage(int message, nint id, nint chordData) =>
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
                    return ResolveRollback(RollBack(previous, removed, staged,
                        $"Windows could not release {pair.Value.Chord} from the previous window (error {error}).",
                        ShortcutConflictKind.OperatingSystem, oldHandle));
                removed.Add(pair.Key, registration);
            }
            foreach (var pair in removed)
            {
                if (!TryRegisterSpecificStaged(handle, pair.Value.Id, pair.Key, pair.Value.Chord, out var candidate, out var error))
                    return ResolveRollback(RollBack(previous, removed, staged,
                        $"Windows could not register {pair.Value.Chord} on the recreated window (error {error}).",
                        ShortcutConflictKind.OperatingSystem, oldHandle));
                staged.Add(candidate);
            }
            windowHandle = handle;
            foreach (var registration in staged) registrations[registration.Action] = registration;
            retiredIds.Clear();
            RecomputeLifecycle();
            return ShortcutRegistrationResult.Success();
        }
    }

    private ShortcutRestoreResult RestoreCore()
    {
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
                    if (rollback.FatalFailure is { } fatal) throw fatal;
                    throw new InvalidOperationException(rollback.Result.ConflictReason);
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
            var persistence = Persist(target);
            if (!persistence.Committed)
            {
                var rollback = RollBack(previous, removed, staged,
                    $"Could not persist restored shortcuts: {persistence.OperationFailure?.Message ?? "unknown persistence failure"}",
                    ShortcutConflictKind.Persistence, windowHandle);
                rollback = PreserveTransactionDisposeFailure(rollback, persistence.DisposeFailure,
                    "The failed restore transaction could not be closed");
                var fatal = FirstUnexpected(persistence.OperationFailure, persistence.DisposeFailure) ?? rollback.FatalFailure;
                if (fatal is not null) throw fatal;
                throw new InvalidOperationException(rollback.Result.ConflictReason, persistence.OperationFailure);
            }
            foreach (var registration in staged) registrations[registration.Action] = registration;
            QueueChanges(ApplyBindings(target));
            restoreIssues.Clear();
            restoreIssues.AddRange(issues);
            restored = true;
            RecomputeLifecycle();
            if (persistence.DisposeFailure is not null)
                SetDegradation(TransactionDisposeCause,
                    $"Restored shortcuts were committed, but closing their persistence transaction failed: {persistence.DisposeFailure.Message}");
            else ClearDegradation(TransactionDisposeCause);
            if (FirstUnexpected(persistence.DisposeFailure) is { } fatalDispose) throw fatalDispose;
        }
        return new(issues);
    }

    private ShortcutRegistrationResult ReplaceCore(ShortcutAction action, ShortcutBinding candidate)
    {
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
            catch (Exception exception)
            {
                return ResolveRollback(RollBack(previous, new Dictionary<ShortcutAction, Registration>(), staged,
                    $"Could not stage setting: {exception.Message}", ShortcutConflictKind.Persistence, windowHandle), exception);
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
                    catch (Exception exception) { commitFailure = exception; }
                }
            }
            finally
            {
                try { transaction.Dispose(); }
                catch (Exception exception) { disposeFailure = exception; }
            }
            if (!committed)
            {
                string failure = nativeFailure ?? $"Could not commit setting: {commitFailure?.Message ?? disposeFailure?.Message ?? "unknown persistence failure"}";
                if (disposeFailure is not null) failure += $" Transaction disposal also failed: {disposeFailure.Message}";
                var rollback = PreserveTransactionDisposeFailure(
                    RollBack(previous, removed, staged, failure,
                        nativeFailure is null ? ShortcutConflictKind.Persistence : ShortcutConflictKind.OperatingSystem, windowHandle),
                    disposeFailure, "The failed replacement transaction could not be closed");
                return ResolveRollback(rollback,
                    commitFailure, disposeFailure);
            }
            foreach (var registration in staged) registrations[action] = registration;
            bindings[action] = candidate;
            restoreIssues.RemoveAll(issue => issue.Action == action);
            var change = new ShortcutBindingChangedEventArgs(action, candidate, ++bindingVersion);
            bindingVersions[action] = change.Version;
            QueueChanges([change]);
            ClearDegradation(BindingDegradationCause(action));
            if (disposeFailure is not null)
                SetDegradation(TransactionDisposeCause,
                    $"The shortcut was committed, but closing its persistence transaction failed: {disposeFailure.Message}");
            else ClearDegradation(TransactionDisposeCause);
            result = ShortcutRegistrationResult.Success(candidate);
            if (FirstUnexpected(disposeFailure) is { } fatalDispose) throw fatalDispose;
        }
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
            catch (Exception exception)
            {
                return ResolveRollback(RollBack(previous, new Dictionary<ShortcutAction, Registration>(), [],
                    $"Could not stage reset: {exception.Message}", ShortcutConflictKind.Persistence, windowHandle), exception);
            }
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
                    catch (Exception exception) { commitFailure = exception; }
                }
            }
            finally
            {
                try { transaction.Dispose(); }
                catch (Exception exception) { disposeFailure = exception; }
            }
            if (!committed)
            {
                string failure = nativeFailure ?? $"Could not commit reset: {commitFailure?.Message ?? disposeFailure?.Message ?? "unknown persistence failure"}";
                if (disposeFailure is not null) failure += $" Transaction disposal also failed: {disposeFailure.Message}";
                var rollback = PreserveTransactionDisposeFailure(
                    RollBack(previous, removed, staged, failure,
                        nativeFailure is null ? ShortcutConflictKind.Persistence : ShortcutConflictKind.OperatingSystem, windowHandle),
                    disposeFailure, "The failed reset transaction could not be closed");
                return ResolveRollback(rollback,
                    commitFailure, disposeFailure);
            }
            foreach (var registration in staged) registrations[registration.Action] = registration;
            changes = ApplyBindings(target);
            QueueChanges(changes);
            restoreIssues.Clear();
            ClearBindingDegradations();
            RecomputeLifecycle();
            if (disposeFailure is not null)
                SetDegradation(TransactionDisposeCause,
                    $"The reset was committed, but closing its persistence transaction failed: {disposeFailure.Message}");
            else ClearDegradation(TransactionDisposeCause);
            result = ShortcutRegistrationResult.Success();
            if (FirstUnexpected(disposeFailure) is { } fatalDispose) throw fatalDispose;
        }
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
            if (chordData == 0) return false;
            long encoded = chordData.ToInt64();
            if ((int)(encoded & 0xFFFF) != (int)match.Value.Chord.Modifiers ||
                (int)((encoded >> 16) & 0xFFFF) != match.Value.Chord.VirtualKey) return false;
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
            if (failures.Count > 0) SetTerminal(
                "Disposal could not release every Windows shortcut; remaining IDs are non-routable and tracked.");
            else { disposed = true; degradationCauses.Clear(); lifecycle = new(ShortcutLifecycleState.Disposed, "All Windows shortcuts were released."); }
        }
        if (failures.Count > 0) throw new AggregateException("One or more Windows shortcut registrations could not be released.", failures);
    }

    private RollbackOutcome RollBack(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> previous,
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
        var reconciliation = Persist(reconciled);
        if (!reconciliation.Committed)
        {
            var terminalChanges = ApplyBindings(reconciled);
            SetTerminal(
                $"{failure} SQLite reconciliation failed after Windows rollback: {reconciliation.OperationFailure?.Message ?? "unknown persistence failure"}");
            QueueChanges(terminalChanges);
            return new(ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, lifecycle.Reason!,
                    removed.Count == 1 ? reconciled[removed.Keys.Single()] : null),
                FirstUnexpected(reconciliation.OperationFailure, reconciliation.DisposeFailure));
        }
        if (reconstructionFailures.Count > 0)
        {
            var changes = ApplyBindings(reconciled);
            foreach (var pair in removed.Where(pair => !registrations.ContainsKey(pair.Key)))
                SetDegradation(BindingDegradationCause(pair.Key),
                    $"{failure} Disabled {pair.Key}; Windows could not reconstruct {pair.Value.Chord}.");
            QueueChanges(changes);
        }
        else if (pendingCleanup.Count > 0)
            SetDegradation(PendingCleanupCause, $"{failure} Candidate cleanup remains tracked and non-routable.");
        if (reconciliation.DisposeFailure is not null)
            SetDegradation(TransactionDisposeCause,
                $"{failure} The reconciled snapshot was committed, but closing its persistence transaction failed: {reconciliation.DisposeFailure.Message}");
        else ClearDegradation(TransactionDisposeCause);

        var result = reconstructionFailures.Count > 0 || pendingCleanup.Count > 0 || reconciliation.DisposeFailure is not null
            ? ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, lifecycle.Reason!,
                removed.Count == 1 ? reconciled[removed.Keys.Single()] : null)
            : ShortcutRegistrationResult.Conflict(originalKind, failure,
                removed.Count == 1 ? previous[removed.Keys.Single()] : null);
        return new(result, FirstUnexpected(reconciliation.DisposeFailure));
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
        SetDegradation(PendingCleanupCause, $"Windows cleanup is pending for {registration.Chord} (error {error}).");
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
        if (pendingCleanup.Count == 0)
        {
            restoreIssues.RemoveAll(issue => issue.Action is null && issue.Kind == ShortcutConflictKind.Lifecycle);
            ClearDegradation(PendingCleanupCause);
        }
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
    private PersistenceAttempt Persist(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> snapshot)
    {
        IShortcutBindingStoreTransaction transaction;
        try { transaction = store.BeginReplace(ToStored(snapshot)); }
        catch (Exception exception) { return new(false, exception, null); }
        Exception? commitFailure = null;
        Exception? disposeFailure = null;
        bool committed = false;
        try { transaction.Commit(); committed = true; }
        catch (Exception exception) { commitFailure = exception; }
        try { transaction.Dispose(); }
        catch (Exception exception) { disposeFailure = exception; }
        return new(committed, commitFailure, disposeFailure);
    }
    private Dictionary<ShortcutAction, ShortcutBinding> CopyBindings() => bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
    private static string BindingDegradationCause(ShortcutAction action) => $"binding-reconstruction:{action}";
    private void SetDegradation(string cause, string reason)
    {
        degradationCauses[cause] = reason;
        RecomputeLifecycle();
    }
    private void ClearDegradation(string cause)
    {
        degradationCauses.Remove(cause);
        RecomputeLifecycle();
    }
    private void ClearBindingDegradations()
    {
        foreach (string cause in degradationCauses.Keys.Where(key => key.StartsWith("binding-reconstruction:", StringComparison.Ordinal)).ToArray())
            degradationCauses.Remove(cause);
        RecomputeLifecycle();
    }
    private void RecomputeLifecycle()
    {
        if (lifecycle.State is ShortcutLifecycleState.Terminal or ShortcutLifecycleState.Disposed) return;
        lifecycle = degradationCauses.Count == 0
            ? new(ShortcutLifecycleState.Ready)
            : new(ShortcutLifecycleState.Degraded, string.Join(" ", degradationCauses.OrderBy(pair => pair.Key).Select(pair => $"[{pair.Key}] {pair.Value}")));
    }
    private void SetTerminal(string reason) => lifecycle = new(ShortcutLifecycleState.Terminal, reason);
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
    private T ExecuteWithEventDrain<T>(Func<T> action)
    {
        try { return action(); }
        finally { DrainQueuedChanges(); }
    }
    private void QueueChanges(IEnumerable<ShortcutBindingChangedEventArgs>? changes)
    {
        if (changes is null) return;
        foreach (var change in changes) queuedChanges.Enqueue(change);
    }
    private void DrainQueuedChanges()
    {
        List<ShortcutBindingChangedEventArgs> changes;
        lock (gate)
        {
            changes = queuedChanges.ToList();
            queuedChanges.Clear();
        }
        PublishChanges(changes);
    }
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
    private static bool IsExpectedPersistenceException(Exception exception) =>
        exception is IOException or SqliteException or UnauthorizedAccessException;
    private static Exception? FirstUnexpected(params Exception?[] failures) =>
        failures.FirstOrDefault(failure => failure is not null && !IsExpectedPersistenceException(failure));
    private static ShortcutRegistrationResult ResolveRollback(RollbackOutcome outcome, params Exception?[] failures)
    {
        var fatal = FirstUnexpected(failures) ?? outcome.FatalFailure;
        if (fatal is not null) throw fatal;
        return outcome.Result;
    }
    private RollbackOutcome PreserveTransactionDisposeFailure(RollbackOutcome outcome, Exception? disposeFailure, string context)
    {
        if (disposeFailure is null) return outcome;
        SetDegradation(TransactionDisposeCause, $"{context}: {disposeFailure.Message}.");
        if (lifecycle.State == ShortcutLifecycleState.Terminal) return outcome;
        return outcome with
        {
            Result = ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle, lifecycle.Reason!, outcome.Result.Binding),
        };
    }
    private readonly record struct Registration(nint Handle, int Id, ShortcutAction Action, ShortcutChord Chord);
    private readonly record struct PersistenceAttempt(bool Committed, Exception? OperationFailure, Exception? DisposeFailure);
    private sealed record RollbackOutcome(ShortcutRegistrationResult Result, Exception? FatalFailure = null);
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
