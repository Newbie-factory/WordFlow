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
    private readonly object gate = new();
    private readonly IHotKeyNative native;
    private readonly IShortcutBindingStore store;
    private readonly Dictionary<ShortcutAction, ShortcutBinding> bindings =
        ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
    private readonly Dictionary<ShortcutAction, Registration> registrations = [];
    private readonly List<Registration> pendingCleanup = [];
    private readonly List<ShortcutRestoreIssue> restoreIssues = [];
    private int nextRegistrationId = 0x5746;
    private nint windowHandle;
    private int ownerThreadId;
    private bool restored;
    private bool disposed;

    public GlobalShortcutService(IHotKeyNative native, IShortcutBindingStore store)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public GlobalShortcutService(SqliteConnectionFactory connectionFactory)
        : this(new Win32HotKeyNative(), new SqliteShortcutBindingStore(connectionFactory)) { }

    public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings
    {
        get { lock (gate) return new Dictionary<ShortcutAction, ShortcutBinding>(bindings); }
    }

    public event EventHandler<ShortcutAction>? ActionInvoked;
    public event EventHandler<ShortcutBindingChangedEventArgs>? BindingChanged;
    public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted;

    public IReadOnlyList<ShortcutRestoreIssue> RestoreIssues
    {
        get { lock (gate) return restoreIssues.ToArray(); }
    }

    public void AttachWindowHandle(nint handle)
    {
        if (handle == 0) throw new ArgumentException("A valid window handle is required.", nameof(handle));
        lock (gate)
        {
            ThrowIfDisposed();
            RetryPendingCleanup();
            int currentThread = Environment.CurrentManagedThreadId;
            if (ownerThreadId != 0 && ownerThreadId != currentThread)
                throw new InvalidOperationException("Window handle lifecycle changes must run on the owning thread.");
            ownerThreadId = currentThread;
            if (windowHandle == handle) return;
            if (!restored)
            {
                windowHandle = handle;
                return;
            }

            var oldHandle = windowHandle;
            var oldRegistrations = registrations.ToDictionary(pair => pair.Key, pair => pair.Value);
            var removedRegistrations = new List<KeyValuePair<ShortcutAction, Registration>>();
            foreach (var pair in oldRegistrations)
            {
                if (!native.TryUnregister(oldHandle, pair.Value.Id, out var error))
                {
                    foreach (var removed in removedRegistrations)
                        registrations[removed.Key] = Register(removed.Key, removed.Value.Chord);
                    throw NativeFailure("unregister shortcuts from the previous window", error);
                }
                removedRegistrations.Add(pair);
            }
            registrations.Clear();
            windowHandle = handle;
            try
            {
                foreach (var pair in bindings.Where(pair => IsActiveGlobal(pair.Value)))
                    registrations.Add(pair.Key, Register(pair.Key, pair.Value.Chord));
            }
            catch
            {
                UnregisterAllBestEffort(handle, registrations.Values);
                registrations.Clear();
                windowHandle = oldHandle;
                foreach (var pair in oldRegistrations)
                    registrations[pair.Key] = Register(pair.Key, pair.Value.Chord);
                throw;
            }
        }
    }

    public ShortcutRestoreResult RestorePersisted()
    {
        List<ShortcutBindingChangedEventArgs> changes;
        List<ShortcutRestoreIssue> issues = [];
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureHandle();
            RetryPendingCleanup();
            if (pendingCleanup.Count > 0)
                throw new InvalidOperationException("Windows still owns a shortcut registration pending cleanup.");
            var loadedRows = store.Load();
            var restoredBindings = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var unknown in loadedRows.Where(row => !Enum.TryParse<ShortcutAction>(row.Command, true, out var parsedAction) || !Enum.IsDefined(parsedAction)))
                issues.Add(new(null, ShortcutConflictKind.InvalidBinding, $"Unknown persisted shortcut action '{unknown.Command}' was removed."));
            foreach (var action in Enum.GetValues<ShortcutAction>())
            {
                var matching = loadedRows.Where(row => row.Command.Equals(action.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length == 0) continue;
                string? validation = null;
                ShortcutBinding? parsed = null;
                bool malformed = matching.Length != 1 ||
                    !matching[0].TryParse(out var encodedAction, out parsed) || encodedAction != action ||
                    (validation = parsed!.Chord.Validate()) is not null;
                if (malformed)
                {
                    restoredBindings[action] = ShortcutDefaults.For(action) with { IsEnabled = false };
                    issues.Add(new(action, ShortcutConflictKind.InvalidBinding,
                        matching.Length != 1 ? "The persisted action has duplicate rows." : validation ?? "The persisted shortcut is malformed."));
                    continue;
                }
                restoredBindings[action] = parsed!;
            }

            foreach (var group in restoredBindings.Where(pair => pair.Value.IsEnabled).GroupBy(pair => pair.Value.Chord))
            {
                var overlaps = group.ToArray();
                if (overlaps.Length < 2) continue;
                var winner = overlaps.FirstOrDefault(pair => ShortcutDefaults.For(pair.Key).Chord == group.Key);
                if (winner.Equals(default(KeyValuePair<ShortcutAction, ShortcutBinding>))) winner = overlaps[0];
                foreach (var loser in overlaps.Where(pair => pair.Key != winner.Key))
                {
                    restoredBindings[loser.Key] = loser.Value with { IsEnabled = false };
                    issues.Add(new(loser.Key, ShortcutConflictKind.ApplicationDuplicate,
                        $"{loser.Value.DisplayText} is also assigned to {winner.Key}."));
                }
            }

            UnregisterAllBestEffort(windowHandle, registrations.Values);
            registrations.Clear();
            foreach (var pair in restoredBindings.Where(pair => IsActiveGlobal(pair.Value)))
            {
                if (TryRegister(pair.Key, pair.Value.Chord, out var registration, out var error))
                    registrations.Add(pair.Key, registration);
                else
                {
                    restoredBindings[pair.Key] = pair.Value with { IsEnabled = false };
                    issues.Add(new(pair.Key, ShortcutConflictKind.OperatingSystem,
                        $"Windows rejected {pair.Value.DisplayText} (error {error})."));
                }
            }

            try
            {
                using var transaction = store.BeginReplace(ToStored(restoredBindings));
                transaction.Commit();
            }
            catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
            {
                UnregisterAllBestEffort(windowHandle, registrations.Values);
                registrations.Clear();
                throw new InvalidOperationException("Could not persist the sanitized shortcut configuration.", exception);
            }

            changes = restoredBindings.Select(pair => new ShortcutBindingChangedEventArgs(pair.Key, pair.Value)).ToList();
            bindings.Clear();
            foreach (var pair in restoredBindings) bindings.Add(pair.Key, pair.Value);
            restored = true;
            restoreIssues.Clear();
            restoreIssues.AddRange(issues);
        }
        PublishChanges(changes);
        return new(issues);
    }

    public ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ShortcutBindingChangedEventArgs? change = null;
        ShortcutRegistrationResult result;
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureReady();
            RetryPendingCleanup();
            if (pendingCleanup.Count > 0)
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle,
                    "A previous Windows shortcut cleanup is still pending; no new binding was reserved.");
            if (!Enum.IsDefined(action)) return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.InvalidBinding, "Unknown shortcut action.");
            if (candidate.Chord.Validate() is { } validation)
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Reserved, validation);
            if (candidate.IsEnabled && bindings.Any(pair => pair.Key != action && pair.Value.IsEnabled && pair.Value.Chord == candidate.Chord))
            {
                var duplicate = bindings.First(pair => pair.Key != action && pair.Value.IsEnabled && pair.Value.Chord == candidate.Chord).Key;
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.ApplicationDuplicate,
                    $"{candidate.DisplayText} is already assigned to {duplicate}.");
            }

            var previous = bindings[action];
            if (previous == candidate) return ShortcutRegistrationResult.Success(candidate);
            Registration? stagedRegistration = null;
            bool reuseRegistration = IsActiveGlobal(previous) && IsActiveGlobal(candidate) && previous.Chord == candidate.Chord;
            if (IsActiveGlobal(candidate) && !reuseRegistration)
            {
                if (!TryRegister(action, candidate.Chord, out var registration, out var error))
                    return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem,
                        $"Windows rejected {candidate.DisplayText} (error {error}).");
                stagedRegistration = registration;
            }

            var replacement = bindings.ToDictionary(pair => pair.Key, pair => pair.Value);
            replacement[action] = candidate;
            IShortcutBindingStoreTransaction? transaction = null;
            try
            {
                transaction = store.BeginReplace(ToStored(replacement));
            }
            catch (Exception exception) when (IsPersistenceException(exception))
            {
                string cleanup = stagedRegistration is { } staged ? CleanupOrTrack(staged) : string.Empty;
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Persistence,
                    $"Could not stage the shortcut setting: {exception.Message}{cleanup}");
            }

            using (transaction)
            {
                Registration? removedOld = null;
                if (registrations.TryGetValue(action, out var oldRegistration) && !reuseRegistration)
                {
                    if (!native.TryUnregister(windowHandle, oldRegistration.Id, out var error))
                    {
                        string cleanup = stagedRegistration is { } staged ? CleanupOrTrack(staged) : string.Empty;
                        return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem,
                            $"Windows could not release the old shortcut (error {error}).{cleanup}");
                    }
                    removedOld = oldRegistration;
                }

                try
                {
                    transaction.Commit();
                }
                catch (Exception exception) when (IsPersistenceException(exception))
                {
                    if (removedOld is { } old)
                        registrations[action] = Register(action, old.Chord);
                    string cleanup = stagedRegistration is { } staged ? CleanupOrTrack(staged) : string.Empty;
                    return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Persistence,
                        $"Could not commit the shortcut setting: {exception.Message}{cleanup}");
                }

                bindings[action] = candidate;
                restoreIssues.RemoveAll(issue => issue.Action == action);
                if (reuseRegistration) { }
                else if (stagedRegistration is { } staged) registrations[action] = staged;
                else registrations.Remove(action);
                change = new(action, candidate);
                result = ShortcutRegistrationResult.Success(candidate);
            }
        }
        if (change is not null) PublishChange(change);
        return result;
    }

    public ShortcutRegistrationResult ResetAll()
    {
        List<ShortcutBindingChangedEventArgs>? changes = null;
        ShortcutRegistrationResult result;
        lock (gate)
        {
            ThrowIfDisposed();
            EnsureReady();
            RetryPendingCleanup();
            if (pendingCleanup.Count > 0)
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Lifecycle,
                    "A previous Windows shortcut cleanup is still pending; reset was not started.");
            var target = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
            if (bindings.All(pair => target[pair.Key] == pair.Value)) return ShortcutRegistrationResult.Success();

            IShortcutBindingStoreTransaction transaction;
            try { transaction = store.BeginReplace(ToStored(target)); }
            catch (Exception exception) when (IsPersistenceException(exception))
            {
                return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Persistence, exception.Message);
            }

            using (transaction)
            {
                var oldRegistrations = registrations.ToDictionary(pair => pair.Key, pair => pair.Value);
                var removedRegistrations = new List<KeyValuePair<ShortcutAction, Registration>>();
                foreach (var pair in oldRegistrations)
                {
                    if (!native.TryUnregister(windowHandle, pair.Value.Id, out var error))
                    {
                        foreach (var removed in removedRegistrations)
                            registrations[removed.Key] = Register(removed.Key, removed.Value.Chord);
                        return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem,
                            $"Windows could not stage reset (error {error}).");
                    }
                    registrations.Remove(pair.Key);
                    removedRegistrations.Add(pair);
                }

                try
                {
                    foreach (var pair in target.Where(pair => IsActiveGlobal(pair.Value)))
                        registrations.Add(pair.Key, Register(pair.Key, pair.Value.Chord));
                }
                catch (Win32Exception exception)
                {
                    UnregisterAllBestEffort(windowHandle, registrations.Values);
                    registrations.Clear();
                    RestoreRegistrationSet(oldRegistrations);
                    return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem, exception.Message);
                }

                try { transaction.Commit(); }
                catch (Exception exception) when (IsPersistenceException(exception))
                {
                    UnregisterAllBestEffort(windowHandle, registrations.Values);
                    registrations.Clear();
                    RestoreRegistrationSet(oldRegistrations);
                    return ShortcutRegistrationResult.Conflict(ShortcutConflictKind.Persistence, exception.Message);
                }

                bindings.Clear();
                foreach (var pair in target) bindings.Add(pair.Key, pair.Value);
                restoreIssues.Clear();
                changes = target.Select(pair => new ShortcutBindingChangedEventArgs(pair.Key, pair.Value)).ToList();
                result = ShortcutRegistrationResult.Success();
            }
        }
        PublishChanges(changes);
        return result;
    }

    public bool ProcessWindowMessage(int message, nint id)
    {
        ShortcutAction action;
        lock (gate)
        {
            if (disposed || message != WmHotKey) return false;
            var match = registrations.FirstOrDefault(pair => pair.Value.Id == id.ToInt32());
            if (match.Equals(default(KeyValuePair<ShortcutAction, Registration>))) return false;
            action = match.Key;
        }
        PublishAction(action);
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            if (ownerThreadId != 0 && ownerThreadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Shortcut disposal must run on the window-owning thread.");
            UnregisterAllBestEffort(windowHandle, registrations.Values);
            registrations.Clear();
            RetryPendingCleanup();
            if (pendingCleanup.Count > 0)
            {
                var errors = pendingCleanup.Select(registration =>
                    new Win32Exception($"Could not clean up {registration.Chord} registration {registration.Id}.")).ToArray();
                disposed = true;
                throw new AggregateException("One or more Windows shortcut registrations could not be released.", errors);
            }
            disposed = true;
        }
    }

    private Registration Register(ShortcutAction action, ShortcutChord chord)
    {
        if (!TryRegister(action, chord, out var registration, out var error))
            throw NativeFailure($"register {chord} for {action}", error);
        return registration;
    }

    private bool TryRegister(ShortcutAction action, ShortcutChord chord, out Registration registration, out int error)
    {
        int id = checked(nextRegistrationId++);
        if (native.TryRegister(windowHandle, id, chord, out error))
        {
            registration = new(windowHandle, id, action, chord);
            return true;
        }
        registration = default;
        return false;
    }

    private void RestoreRegistrationSet(IReadOnlyDictionary<ShortcutAction, Registration> oldRegistrations)
    {
        registrations.Clear();
        foreach (var pair in oldRegistrations)
            registrations[pair.Key] = Register(pair.Key, pair.Value.Chord);
    }

    private string CleanupOrTrack(Registration registration)
    {
        if (native.TryUnregister(registration.Handle, registration.Id, out var error)) return string.Empty;
        if (!pendingCleanup.Contains(registration)) pendingCleanup.Add(registration);
        restoreIssues.Add(new(null, ShortcutConflictKind.Lifecycle,
            $"Windows cleanup is pending for {registration.Chord} (error {error})."));
        return $" Windows also rejected candidate cleanup (error {error}); cleanup is tracked and will be retried.";
    }

    private void RetryPendingCleanup()
    {
        for (int index = pendingCleanup.Count - 1; index >= 0; index--)
        {
            var registration = pendingCleanup[index];
            if (!native.TryUnregister(registration.Handle, registration.Id, out _)) continue;
            pendingCleanup.RemoveAt(index);
        }
        if (pendingCleanup.Count == 0)
            restoreIssues.RemoveAll(issue => issue.Action is null && issue.Kind == ShortcutConflictKind.Lifecycle);
    }

    private void UnregisterAllBestEffort(nint handle, IEnumerable<Registration> values)
    {
        foreach (var registration in values.ToArray())
        {
            if (!native.TryUnregister(handle, registration.Id, out _) && !pendingCleanup.Contains(registration))
                pendingCleanup.Add(registration);
        }
    }

    private static IReadOnlyCollection<StoredShortcutBinding> ToStored(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> source) =>
        source.OrderBy(pair => pair.Key).Select(pair => StoredShortcutBinding.From(pair.Key, pair.Value)).ToArray();

    private static bool IsActiveGlobal(ShortcutBinding binding) => binding.IsEnabled && binding.Scope == ShortcutScope.Global;
    private static bool IsPersistenceException(Exception exception) => exception is IOException or SqliteException or UnauthorizedAccessException;
    private static Win32Exception NativeFailure(string operation, int error) => new(error, $"Could not {operation} (Win32 error {error}).");
    private void EnsureHandle() { if (windowHandle == 0) throw new InvalidOperationException("Attach a window handle before restoring shortcuts."); }
    private void EnsureReady() { EnsureHandle(); if (!restored) throw new InvalidOperationException("Restore persisted shortcuts before editing them."); }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(disposed, this); }

    private void PublishChanges(IEnumerable<ShortcutBindingChangedEventArgs> changes)
    {
        foreach (var change in changes) PublishChange(change);
    }

    private void PublishAction(ShortcutAction action)
    {
        var handlers = ActionInvoked;
        if (handlers is null) return;
        foreach (EventHandler<ShortcutAction> handler in handlers.GetInvocationList())
        {
            try { handler(this, action); }
            catch (Exception exception) { PublishCallbackFault(exception); }
        }
    }

    private void PublishChange(ShortcutBindingChangedEventArgs change)
    {
        var handlers = BindingChanged;
        if (handlers is null) return;
        foreach (EventHandler<ShortcutBindingChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, change); }
            catch (Exception exception) { PublishCallbackFault(exception); }
        }
    }

    private void PublishCallbackFault(Exception exception)
    {
        var handlers = CallbackFaulted;
        if (handlers is null) return;
        foreach (EventHandler<ShortcutCallbackFaultedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new(exception)); }
            catch { }
        }
    }

    private readonly record struct Registration(nint Handle, int Id, ShortcutAction Action, ShortcutChord Chord);
}

internal sealed class Win32HotKeyNative : IHotKeyNative
{
    private const uint ModNoRepeat = 0x4000;

    public bool TryRegister(nint handle, int id, ShortcutChord chord, out int errorCode)
    {
        bool registered = RegisterHotKey(handle, id, (uint)chord.Modifiers | ModNoRepeat, (uint)chord.VirtualKey);
        errorCode = registered ? 0 : Marshal.GetLastWin32Error();
        return registered;
    }

    public bool TryUnregister(nint handle, int id, out int errorCode)
    {
        bool unregistered = UnregisterHotKey(handle, id);
        errorCode = unregistered ? 0 : Marshal.GetLastWin32Error();
        return unregistered;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}

public sealed class SqliteShortcutBindingStore : IShortcutBindingStore
{
    private readonly string connectionString;

    public SqliteShortcutBindingStore(SqliteConnectionFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = factory.UserDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
    }

    public IReadOnlyList<StoredShortcutBinding> Load()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT command, gesture FROM shortcut_binding ORDER BY command";
        using var reader = command.ExecuteReader();
        var rows = new List<StoredShortcutBinding>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    public IShortcutBindingStoreTransaction BeginReplace(IReadOnlyCollection<StoredShortcutBinding> replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var connection = Open();
        try
        {
            var transaction = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM shortcut_binding";
                delete.ExecuteNonQuery();
            }
            foreach (var row in replacement)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO shortcut_binding(command, gesture) VALUES ($command, $gesture)";
                insert.Parameters.AddWithValue("$command", row.Command);
                insert.Parameters.AddWithValue("$gesture", row.Value);
                insert.ExecuteNonQuery();
            }
            return new Transaction(connection, transaction);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private sealed class Transaction(SqliteConnection connection, SqliteTransaction transaction) : IShortcutBindingStoreTransaction
    {
        private bool committed;
        public void Commit() { transaction.Commit(); committed = true; }
        public void Dispose()
        {
            if (!committed)
            {
                try { transaction.Rollback(); }
                catch (SqliteException) { }
            }
            transaction.Dispose();
            connection.Dispose();
        }
    }
}
