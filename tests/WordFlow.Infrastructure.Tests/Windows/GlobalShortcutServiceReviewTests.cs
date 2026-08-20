using System.Collections.Concurrent;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Infrastructure.Windows;

namespace WordFlow.Infrastructure.Tests.Windows;

public sealed class GlobalShortcutServiceReviewTests
{
    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    [InlineData(0xA4)]
    [InlineData(0xA5)]
    public void Every_generic_and_extended_modifier_vk_is_rejected(int virtualKey)
    {
        Assert.NotNull(new ShortcutChord(virtualKey, ShortcutModifiers.None).Validate());
    }

    [Fact]
    public void Commit_failure_old_reacquire_failure_and_candidate_cleanup_failure_reconcile_to_disabled_truth()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        int staleOldId = native.IdFor(fixture.Handle, Chord("F1"));
        var candidate = Chord("Ctrl+Alt+F8");
        store.FailNextCommit = true;
        native.PersistentUnregisterFailures.Add(candidate);
        native.AfterSuccessfulUnregister = chord =>
        {
            if (chord == Chord("F1")) native.ConflictingChords.Add(chord);
        };

        var result = fixture.Service.TryReplace(ShortcutAction.Again,
            new ShortcutBinding(candidate, ShortcutScope.Global, true));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutConflictKind.Lifecycle, result.ConflictKind);
        Assert.False(result.Binding!.IsEnabled);
        Assert.Equal(ShortcutLifecycleState.Degraded, fixture.Service.Lifecycle.State);
        Assert.False(fixture.Service.Bindings[ShortcutAction.Again].IsEnabled);
        Assert.False(store.Persisted[ShortcutAction.Again].IsEnabled);
        Assert.False(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey, (nint)staleOldId));
        int stagedId = native.IdFor(fixture.Handle, candidate);
        Assert.False(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey, (nint)stagedId));

        native.PersistentUnregisterFailures.Clear();
        native.ConflictingChords.Clear();
    }

    [Fact]
    public void Reset_failure_always_tracks_candidate_cleanup_and_disables_unreconstructable_old_binding()
    {
        using var dispatcher = new DedicatedDispatcher();
        var custom = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        custom[ShortcutAction.Again] = Binding("Ctrl+Alt+F8");
        custom[ShortcutAction.Hard] = Binding("Ctrl+Alt+F9");
        var native = new FaultNative();
        var store = new FaultStore(custom);
        using var fixture = Fixture.Create(dispatcher, native, store);
        native.ConflictingChords.Add(Chord("F2"));
        native.PersistentUnregisterFailures.Add(Chord("F1"));
        native.AfterSuccessfulUnregister = chord =>
        {
            if (chord == Chord("Ctrl+Alt+F9")) native.ConflictingChords.Add(chord);
        };
        try
        {
            var result = fixture.Service.ResetAll();
            Assert.False(result.Succeeded);
            Assert.Equal(ShortcutConflictKind.Lifecycle, result.ConflictKind);
            Assert.False(fixture.Service.Bindings[ShortcutAction.Hard].IsEnabled);
            Assert.False(store.Persisted[ShortcutAction.Hard].IsEnabled);
            int stagedF1 = native.IdFor(fixture.Handle, Chord("F1"));
            Assert.False(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey, (nint)stagedF1));
            Assert.Equal(ShortcutLifecycleState.Degraded, fixture.Service.Lifecycle.State);
        }
        finally
        {
            native.PersistentUnregisterFailures.Clear();
            native.ConflictingChords.Clear();
            native.AfterSuccessfulUnregister = null;
        }
    }

    [Fact]
    public void Handle_recreation_failure_cleans_or_tracks_new_candidates_and_reconciles_old_handle()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        native.FailRegister = (handle, chord) => handle == (nint)84 && chord == Chord("F2");
        native.FailUnregister = (handle, chord) => handle == (nint)84 && chord == Chord("F1");
        try
        {
            var result = fixture.Service.AttachWindowHandle((nint)84);
            Assert.False(result.Succeeded);
            Assert.Equal(ShortcutConflictKind.Lifecycle, result.ConflictKind);
            Assert.False(fixture.Service.Bindings[ShortcutAction.Again].IsEnabled);
            Assert.False(store.Persisted[ShortcutAction.Again].IsEnabled);
            Assert.False(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey,
                (nint)native.IdFor((nint)84, Chord("F1"))));
        }
        finally
        {
            native.FailUnregister = null;
            native.FailRegister = null;
        }
    }

    [Fact]
    public void Worker_callers_are_marshaled_and_native_and_binding_events_run_only_on_owner_dispatcher()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        native.CallThreads.Clear();
        var eventThreads = new ConcurrentBag<int>();
        fixture.Service.BindingChanged += (_, _) => eventThreads.Add(Environment.CurrentManagedThreadId);

        Parallel.Invoke(
            () => fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8")),
            () => fixture.Service.TryReplace(ShortcutAction.Hard, Binding("Ctrl+Alt+F9")));

        Assert.NotEmpty(native.CallThreads);
        Assert.All(native.CallThreads, thread => Assert.Equal(dispatcher.ThreadId, thread));
        Assert.All(eventThreads, thread => Assert.Equal(dispatcher.ThreadId, thread));
    }

    [Fact]
    public void Bounded_allocator_reports_exhaustion_and_reuses_only_after_successful_hwnd_recreation()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store, maximumRegistrationId: 5);
        int retiredId = native.IdFor(fixture.Handle, Chord("F1"));

        var exhausted = fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8"));
        Assert.False(exhausted.Succeeded);
        Assert.Equal(ShortcutConflictKind.Lifecycle, exhausted.ConflictKind);

        Assert.True(fixture.Service.TryReplace(ShortcutAction.Again,
            new ShortcutBinding(Chord("F1"), ShortcutScope.Global, false)).Succeeded);
        var stillQuarantined = fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8"));
        Assert.False(stillQuarantined.Succeeded);

        Assert.True(fixture.Service.AttachWindowHandle((nint)84).Succeeded);
        Assert.True(fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8")).Succeeded);
        int reusedId = native.IdFor((nint)84, Chord("Ctrl+Alt+F8"));
        Assert.Equal(retiredId, reusedId);
        Assert.False(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey,
            (nint)reusedId, Encode(Chord("F1"))));
        Assert.True(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey,
            (nint)reusedId, Encode(Chord("Ctrl+Alt+F8"))));
        Assert.All(native.RegisteredIds, id => Assert.InRange(id, 0, 0xBFFF));
    }

    [Fact]
    public void Disposal_failure_is_terminal_truthful_and_retryable_after_native_cleanup_recovers()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        var fixture = Fixture.Create(dispatcher, native, store);
        native.PersistentUnregisterFailures.Add(Chord("F1"));

        Assert.Throws<AggregateException>(() => fixture.Service.Dispose());
        Assert.Equal(ShortcutLifecycleState.Terminal, fixture.Service.Lifecycle.State);
        int f1Id = native.IdFor(fixture.Handle, Chord("F1"));
        Assert.False(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey, (nint)f1Id));

        native.PersistentUnregisterFailures.Clear();
        fixture.Service.Dispose();
        Assert.False(native.HasAnyRegistration);
    }

    [Fact]
    public void Same_action_concurrent_commits_and_reentrant_reset_events_end_at_service_truth()
    {
        using var dispatcher = new DedicatedDispatcher();
        var custom = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        custom[ShortcutAction.Again] = Binding("Ctrl+Alt+F8");
        custom[ShortcutAction.Hard] = Binding("Ctrl+Alt+F9");
        var native = new FaultNative();
        var store = new FaultStore(custom);
        using var fixture = Fixture.Create(dispatcher, native, store);
        var delivered = new ConcurrentQueue<ShortcutBindingChangedEventArgs>();
        bool reentered = false;
        bool allowReentry = false;
        fixture.Service.BindingChanged += (_, args) =>
        {
            delivered.Enqueue(args);
            if (allowReentry && !reentered && args.Action == ShortcutAction.Again)
            {
                reentered = true;
                fixture.Service.TryReplace(ShortcutAction.Hard, Binding("Ctrl+Shift+F9"));
            }
        };

        Parallel.Invoke(
            () => fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F10")),
            () => fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F11")));
        allowReentry = true;
        fixture.Service.ResetAll();

        var final = fixture.Service.Bindings[ShortcutAction.Again];
        Assert.Equal(final, delivered.Where(item => item.Action == ShortcutAction.Again)
            .OrderBy(item => item.Version).Last().Binding);
        Assert.True(delivered.Where(item => item.Action == ShortcutAction.Again)
            .Select(item => item.Version).SequenceEqual(delivered.Where(item => item.Action == ShortcutAction.Again)
                .Select(item => item.Version).OrderBy(version => version)));
        Assert.Equal(fixture.Service.Bindings[ShortcutAction.Hard], delivered.Last(item => item.Action == ShortcutAction.Hard).Binding);
        Assert.True(delivered.Where(item => item.Action == ShortcutAction.Hard)
            .Select(item => item.Version).SequenceEqual(delivered.Where(item => item.Action == ShortcutAction.Hard)
                .Select(item => item.Version).OrderBy(version => version)));
    }

    [Fact]
    public void Concurrent_reset_restore_and_dispose_are_serialized_without_routable_leaks()
    {
        using var dispatcher = new DedicatedDispatcher();
        var custom = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        custom[ShortcutAction.Again] = Binding("Ctrl+Alt+F8");
        var native = new FaultNative();
        var store = new FaultStore(custom);
        var fixture = Fixture.Create(dispatcher, native, store);
        var failures = new ConcurrentBag<Exception>();

        Parallel.Invoke(
            () => { if (Record.Exception(() => fixture.Service.ResetAll()) is { } error) failures.Add(error); },
            () => { if (Record.Exception(() => fixture.Service.RestorePersisted()) is { } error) failures.Add(error); },
            () => { if (Record.Exception(() => fixture.Service.Dispose()) is { } error) failures.Add(error); });

        Assert.Equal(ShortcutLifecycleState.Disposed, fixture.Service.Lifecycle.State);
        Assert.False(native.HasAnyRegistration);
        Assert.All(failures, error => Assert.True(error is ObjectDisposedException or InvalidOperationException));
    }

    [Fact]
    public void Reentrant_restore_suppresses_the_older_batch_event_for_the_replaced_action()
    {
        using var dispatcher = new DedicatedDispatcher();
        var custom = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        custom[ShortcutAction.Again] = Binding("Ctrl+Alt+F8");
        custom[ShortcutAction.Hard] = Binding("Ctrl+Alt+F9");
        var native = new FaultNative();
        var store = new FaultStore(custom);
        using var fixture = Fixture.Create(dispatcher, native, store);
        store.SetPersisted(ShortcutDefaults.All);
        var delivered = new List<ShortcutBindingChangedEventArgs>();
        bool reentered = false;
        fixture.Service.BindingChanged += (_, args) =>
        {
            delivered.Add(args);
            if (!reentered && args.Action == ShortcutAction.Again)
            {
                reentered = true;
                fixture.Service.TryReplace(ShortcutAction.Hard, Binding("Ctrl+Shift+F9"));
            }
        };

        fixture.Service.RestorePersisted();

        Assert.Equal(fixture.Service.Bindings[ShortcutAction.Hard], delivered.Last(item => item.Action == ShortcutAction.Hard).Binding);
        Assert.True(delivered.Where(item => item.Action == ShortcutAction.Hard).Select(item => item.Version)
            .SequenceEqual(delivered.Where(item => item.Action == ShortcutAction.Hard).Select(item => item.Version).OrderBy(value => value)));
    }

    [Fact]
    public void Repeated_restore_partial_unregistration_failure_reconstructs_every_removed_active_binding()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        native.FailUnregister = (_, chord) => chord == Chord("F2");
        try
        {
            Assert.Throws<InvalidOperationException>(() => fixture.Service.RestorePersisted());
            Assert.All(ShortcutDefaults.All.Where(pair => pair.Value.Scope == ShortcutScope.Global),
                pair => Assert.True(native.IsRegistered(fixture.Handle, pair.Value.Chord)));
            Assert.Equal(ShortcutDefaults.All, fixture.Service.Bindings);
        }
        finally { native.FailUnregister = null; }
    }

    [Fact]
    public void Terminal_sqlite_reconciliation_still_notifies_the_actual_disabled_binding()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        store.CommitFailuresRemaining = 2;
        native.AfterSuccessfulUnregister = chord =>
        {
            if (chord == Chord("F1")) native.ConflictingChords.Add(chord);
        };
        var changes = new List<ShortcutBindingChangedEventArgs>();
        fixture.Service.BindingChanged += (_, args) => changes.Add(args);

        var result = fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8"));

        Assert.Equal(ShortcutLifecycleState.Terminal, fixture.Service.Lifecycle.State);
        Assert.False(result.Binding!.IsEnabled);
        Assert.Equal(result.Binding, changes.Last(args => args.Action == ShortcutAction.Again).Binding);
        native.ConflictingChords.Clear();
    }

    [Fact]
    public void Replace_commit_and_transaction_dispose_failures_still_roll_back_and_clean_the_candidate()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        store.CommitFailuresRemaining = 1;
        store.DisposeFailuresRemaining = 1;
        var candidate = Chord("Ctrl+Alt+F8");

        var result = fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8"));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutConflictKind.Persistence, result.ConflictKind);
        Assert.Equal(ShortcutDefaults.All[ShortcutAction.Again], fixture.Service.Bindings[ShortcutAction.Again]);
        Assert.Equal(ShortcutDefaults.All[ShortcutAction.Again], store.Persisted[ShortcutAction.Again]);
        Assert.True(native.IsRegistered(fixture.Handle, Chord("F1")));
        Assert.False(native.IsRegistered(fixture.Handle, candidate));
    }

    [Fact]
    public void Replace_post_commit_dispose_failure_adopts_committed_candidate_and_exposes_degraded_lifecycle()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        store.DisposeFailuresRemaining = 1;
        var candidate = Binding("Ctrl+Alt+F8");

        var result = fixture.Service.TryReplace(ShortcutAction.Again, candidate);

        Assert.True(result.Succeeded);
        Assert.Equal(candidate, fixture.Service.Bindings[ShortcutAction.Again]);
        Assert.Equal(candidate, store.Persisted[ShortcutAction.Again]);
        Assert.True(native.IsRegistered(fixture.Handle, candidate.Chord));
        Assert.False(native.IsRegistered(fixture.Handle, Chord("F1")));
        Assert.Equal(ShortcutLifecycleState.Degraded, fixture.Service.Lifecycle.State);
        Assert.Contains("transaction", fixture.Service.Lifecycle.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reset_post_commit_dispose_failure_adopts_committed_defaults_and_exposes_degraded_lifecycle()
    {
        using var dispatcher = new DedicatedDispatcher();
        var custom = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        custom[ShortcutAction.Again] = Binding("Ctrl+Alt+F8");
        var native = new FaultNative();
        var store = new FaultStore(custom);
        using var fixture = Fixture.Create(dispatcher, native, store);
        store.DisposeFailuresRemaining = 1;

        var result = fixture.Service.ResetAll();

        Assert.True(result.Succeeded);
        Assert.Equal(ShortcutDefaults.All, fixture.Service.Bindings);
        Assert.Equal(ShortcutDefaults.All, store.Persisted);
        Assert.Equal(ShortcutLifecycleState.Degraded, fixture.Service.Lifecycle.State);
    }

    [Fact]
    public void Disposal_one_shot_native_failure_retries_to_disposed_without_false_terminal_error()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative { UnregisterFailuresRemaining = 1 };
        var store = new FaultStore(ShortcutDefaults.All);
        var fixture = Fixture.Create(dispatcher, native, store);

        fixture.Service.Dispose();

        Assert.False(native.HasAnyRegistration);
        Assert.Equal(ShortcutLifecycleState.Disposed, fixture.Service.Lifecycle.State);
    }

    [Fact]
    public void Restore_post_commit_transaction_dispose_failure_keeps_committed_truth_and_reports_degraded_lifecycle()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        store.DisposeFailuresRemaining = 1;

        var result = fixture.Service.RestorePersisted();

        Assert.Empty(result.Issues);
        Assert.Equal(ShortcutDefaults.All, fixture.Service.Bindings);
        Assert.Equal(ShortcutDefaults.All, store.Persisted);
        Assert.Equal(ShortcutLifecycleState.Degraded, fixture.Service.Lifecycle.State);
    }

    [Fact]
    public void Ambiguous_commit_that_persists_then_throws_is_explicitly_reconciled_back_to_old_truth()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        store.PersistBeforeCommitFailure = true;
        store.CommitFailuresRemaining = 1;

        var result = fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8"));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutDefaults.All[ShortcutAction.Again], fixture.Service.Bindings[ShortcutAction.Again]);
        Assert.Equal(ShortcutDefaults.All[ShortcutAction.Again], store.Persisted[ShortcutAction.Again]);
        Assert.True(native.IsRegistered(fixture.Handle, Chord("F1")));
        Assert.False(native.IsRegistered(fixture.Handle, Chord("Ctrl+Alt+F8")));
    }

    [Fact]
    public void Same_action_reentry_by_first_subscriber_suppresses_stale_outer_event_for_later_subscribers()
    {
        using var dispatcher = new DedicatedDispatcher();
        var native = new FaultNative();
        var store = new FaultStore(ShortcutDefaults.All);
        using var fixture = Fixture.Create(dispatcher, native, store);
        bool reentered = false;
        fixture.Service.BindingChanged += (_, args) =>
        {
            if (reentered || args.Action != ShortcutAction.Again) return;
            reentered = true;
            fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F9"));
        };
        var laterSubscriber = new List<ShortcutBindingChangedEventArgs>();
        fixture.Service.BindingChanged += (_, args) => laterSubscriber.Add(args);

        fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8"));

        Assert.Single(laterSubscriber);
        Assert.Equal(fixture.Service.Bindings[ShortcutAction.Again], laterSubscriber[0].Binding);
        Assert.Equal("Ctrl+Alt+F9", laterSubscriber[0].Binding.DisplayText);
    }

    private static ShortcutBinding Binding(string value) => new(Chord(value), ShortcutScope.Global, true);
    private static ShortcutChord Chord(string value) => ShortcutChord.Parse(value);
    private static nint Encode(ShortcutChord chord) =>
        (nint)((chord.VirtualKey << 16) | ((int)chord.Modifiers & 0xFFFF));

    private sealed record Fixture(GlobalShortcutService Service, nint Handle) : IDisposable
    {
        public static Fixture Create(
            IShortcutDispatcher dispatcher,
            FaultNative native,
            FaultStore store,
            int maximumRegistrationId = 0xBFFF)
        {
            var service = new GlobalShortcutService(native, store, dispatcher, maximumRegistrationId);
            nint handle = (nint)42;
            Assert.True(service.AttachWindowHandle(handle).Succeeded);
            Assert.Empty(service.RestorePersisted().Issues);
            return new(service, handle);
        }

        public void Dispose() => Service.Dispose();
    }

    private sealed class DedicatedDispatcher : IShortcutDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> queue = [];
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new();

        public DedicatedDispatcher()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "Shortcut test dispatcher" };
            thread.Start();
            ready.Wait();
        }

        public int ThreadId { get; private set; }
        public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;

        public T Invoke<T>(Func<T> action)
        {
            if (CheckAccess()) return action();
            T? result = default;
            Exception? failure = null;
            using var completed = new ManualResetEventSlim();
            queue.Add(() =>
            {
                try { result = action(); }
                catch (Exception exception) { failure = exception; }
                finally { completed.Set(); }
            });
            completed.Wait();
            if (failure is not null) throw failure;
            return result!;
        }

        public void Invoke(Action action) => Invoke(() => { action(); return true; });

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join();
            ready.Dispose();
            queue.Dispose();
        }

        private void Run()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            ready.Set();
            foreach (var action in queue.GetConsumingEnumerable()) action();
        }
    }

    private sealed class FaultNative : IHotKeyNative
    {
        private readonly Dictionary<(nint Handle, int Id), ShortcutChord> registrations = [];
        public HashSet<ShortcutChord> ConflictingChords { get; } = [];
        public HashSet<ShortcutChord> PersistentUnregisterFailures { get; } = [];
        public ConcurrentBag<int> CallThreads { get; } = [];
        public List<int> RegisteredIds { get; } = [];
        public Action<ShortcutChord>? AfterSuccessfulUnregister { get; set; }
        public Func<nint, ShortcutChord, bool>? FailRegister { get; set; }
        public Func<nint, ShortcutChord, bool>? FailUnregister { get; set; }
        public int UnregisterFailuresRemaining { get; set; }
        public bool HasAnyRegistration => registrations.Count > 0;

        public bool TryRegister(nint handle, int id, ShortcutChord chord, out int errorCode)
        {
            CallThreads.Add(Environment.CurrentManagedThreadId);
            RegisteredIds.Add(id);
            if (id is < 0 or > 0xBFFF || ConflictingChords.Contains(chord) ||
                FailRegister?.Invoke(handle, chord) == true || registrations.ContainsValue(chord))
            {
                errorCode = 1409;
                return false;
            }
            registrations.Add((handle, id), chord);
            errorCode = 0;
            return true;
        }

        public bool TryUnregister(nint handle, int id, out int errorCode)
        {
            CallThreads.Add(Environment.CurrentManagedThreadId);
            if (UnregisterFailuresRemaining > 0)
            {
                UnregisterFailuresRemaining--;
                errorCode = 5;
                return false;
            }
            if (registrations.TryGetValue((handle, id), out var chord) &&
                (PersistentUnregisterFailures.Contains(chord) || FailUnregister?.Invoke(handle, chord) == true))
            {
                errorCode = 5;
                return false;
            }
            if (!registrations.Remove((handle, id), out chord))
            {
                errorCode = 1419;
                return false;
            }
            errorCode = 0;
            AfterSuccessfulUnregister?.Invoke(chord);
            return true;
        }

        public int IdFor(nint handle, ShortcutChord chord) =>
            registrations.Single(pair => pair.Key.Handle == handle && pair.Value == chord).Key.Id;
        public bool IsRegistered(nint handle, ShortcutChord chord) =>
            registrations.Any(pair => pair.Key.Handle == handle && pair.Value == chord);
    }

    private sealed class FaultStore : IShortcutBindingStore
    {
        private List<StoredShortcutBinding> rows;
        public FaultStore(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> initial) =>
            rows = initial.Select(pair => StoredShortcutBinding.From(pair.Key, pair.Value)).ToList();
        public int CommitFailuresRemaining { get; set; }
        public int DisposeFailuresRemaining { get; set; }
        public bool PersistBeforeCommitFailure { get; set; }
        public bool FailNextCommit { set { if (value) CommitFailuresRemaining = 1; } }
        public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Persisted => rows
            .Where(row => row.TryParse(out _, out _))
            .ToDictionary(row => { row.TryParse(out var action, out _); return action; },
                row => { row.TryParse(out _, out var binding); return binding!; });
        public IReadOnlyList<StoredShortcutBinding> Load() => rows.ToArray();
        public void SetPersisted(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> replacement) =>
            rows = replacement.Select(pair => StoredShortcutBinding.From(pair.Key, pair.Value)).ToList();
        public IShortcutBindingStoreTransaction BeginReplace(IReadOnlyCollection<StoredShortcutBinding> replacement) =>
            new Transaction(this, replacement.ToList());

        private sealed class Transaction(FaultStore owner, List<StoredShortcutBinding> replacement) : IShortcutBindingStoreTransaction
        {
            public void Commit()
            {
                if (owner.CommitFailuresRemaining > 0)
                {
                    owner.CommitFailuresRemaining--;
                    if (owner.PersistBeforeCommitFailure) owner.rows = replacement;
                    throw new IOException("commit failed");
                }
                owner.rows = replacement;
            }
            public void Dispose()
            {
                if (owner.DisposeFailuresRemaining <= 0) return;
                owner.DisposeFailuresRemaining--;
                throw new IOException("transaction dispose failed");
            }
        }
    }
}
