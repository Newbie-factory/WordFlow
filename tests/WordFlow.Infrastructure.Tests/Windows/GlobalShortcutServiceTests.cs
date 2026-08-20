using System.ComponentModel;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Infrastructure.Windows;
using WordFlow.Infrastructure.Data;

namespace WordFlow.Infrastructure.Tests.Windows;

public sealed class GlobalShortcutServiceTests
{
    [Fact]
    public void Defaults_match_the_approved_product_mapping()
    {
        Assert.Collection(ShortcutDefaults.All.OrderBy(pair => pair.Key),
            pair => AssertDefault(pair, ShortcutAction.Again, "F1", ShortcutScope.Global),
            pair => AssertDefault(pair, ShortcutAction.Hard, "F2", ShortcutScope.Global),
            pair => AssertDefault(pair, ShortcutAction.Good, "F3", ShortcutScope.Global),
            pair => AssertDefault(pair, ShortcutAction.Slash, "Shift+F3", ShortcutScope.Global),
            pair => AssertDefault(pair, ShortcutAction.ToggleSynonyms, "F4", ShortcutScope.Global),
            pair => AssertDefault(pair, ShortcutAction.ToggleConfusables, "F5", ShortcutScope.Global),
            pair => AssertDefault(pair, ShortcutAction.Undo, "Ctrl+Z", ShortcutScope.Focused),
            pair =>
            {
                Assert.Equal(ShortcutAction.Pronounce, pair.Key);
                Assert.False(pair.Value.Chord.IsAssigned);
                Assert.False(pair.Value.IsEnabled);
            });
    }

    [Fact]
    public void Duplicate_is_rejected_across_global_and_focused_delivery_overlap()
    {
        var fixture = Fixture.Create();

        var result = fixture.Service.TryReplace(ShortcutAction.Undo,
            Binding("F1", ShortcutScope.Focused));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutConflictKind.ApplicationDuplicate, result.ConflictKind);
        Assert.Contains("Again", result.ConflictReason, StringComparison.Ordinal);
        Assert.Equal("Ctrl+Z", fixture.Service.Bindings[ShortcutAction.Undo].DisplayText);
        Assert.Equal("Ctrl+Z", fixture.Store.Persisted[ShortcutAction.Undo].DisplayText);
    }

    [Fact]
    public void Os_registration_conflict_keeps_old_active_and_persisted_binding()
    {
        var fixture = Fixture.Create();
        fixture.Native.ConflictingChords.Add(ShortcutChord.Parse("Ctrl+Alt+F8"));

        var result = fixture.Service.TryReplace(ShortcutAction.Again,
            Binding("Ctrl+Alt+F8", ShortcutScope.Global));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutConflictKind.OperatingSystem, result.ConflictKind);
        Assert.Equal("F1", fixture.Service.Bindings[ShortcutAction.Again].DisplayText);
        Assert.Equal("F1", fixture.Store.Persisted[ShortcutAction.Again].DisplayText);
        Assert.True(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("F1")));
        Assert.False(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("Ctrl+Alt+F8")));
    }

    [Fact]
    public void Focused_binding_never_calls_RegisterHotKey_or_UnregisterHotKey()
    {
        var fixture = Fixture.Create();
        fixture.Native.Calls.Clear();

        var result = fixture.Service.TryReplace(ShortcutAction.Undo,
            Binding("Ctrl+Shift+U", ShortcutScope.Focused));

        Assert.True(result.Succeeded);
        Assert.Empty(fixture.Native.Calls);
        Assert.Equal("Ctrl+Shift+U", fixture.Store.Persisted[ShortcutAction.Undo].DisplayText);
    }

    [Fact]
    public void Disabling_a_global_binding_unregisters_it_and_keeps_its_editable_chord()
    {
        var fixture = Fixture.Create();

        var result = fixture.Service.TryReplace(ShortcutAction.Again,
            Binding("F1", ShortcutScope.Global, enabled: false));

        Assert.True(result.Succeeded);
        Assert.False(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("F1")));
        Assert.False(fixture.Store.Persisted[ShortcutAction.Again].IsEnabled);
        Assert.Equal("F1", fixture.Store.Persisted[ShortcutAction.Again].DisplayText);
    }

    [Fact]
    public void Candidate_registration_is_rolled_back_when_persistence_staging_fails()
    {
        var fixture = Fixture.Create();
        fixture.Store.FailNextStage = true;

        var result = fixture.Service.TryReplace(ShortcutAction.Again,
            Binding("Ctrl+Alt+F8", ShortcutScope.Global));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutConflictKind.Persistence, result.ConflictKind);
        Assert.True(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("F1")));
        Assert.False(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("Ctrl+Alt+F8")));
        Assert.Equal("F1", fixture.Store.Persisted[ShortcutAction.Again].DisplayText);
    }

    [Fact]
    public void Candidate_and_staged_write_are_rolled_back_when_old_unregister_fails()
    {
        var fixture = Fixture.Create();
        fixture.Native.FailNextUnregister = true;

        var result = fixture.Service.TryReplace(ShortcutAction.Again,
            Binding("Ctrl+Alt+F8", ShortcutScope.Global));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutConflictKind.OperatingSystem, result.ConflictKind);
        Assert.True(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("F1")));
        Assert.False(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("Ctrl+Alt+F8")));
        Assert.Equal("F1", fixture.Store.Persisted[ShortcutAction.Again].DisplayText);
    }

    [Fact]
    public void Commit_failure_reinstates_old_registration_and_persisted_state()
    {
        var fixture = Fixture.Create();
        fixture.Store.FailNextCommit = true;

        var result = fixture.Service.TryReplace(ShortcutAction.Again,
            Binding("Ctrl+Alt+F8", ShortcutScope.Global));

        Assert.False(result.Succeeded);
        Assert.Equal(ShortcutConflictKind.Persistence, result.ConflictKind);
        Assert.True(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("F1")));
        Assert.False(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("Ctrl+Alt+F8")));
        Assert.Equal("F1", fixture.Store.Persisted[ShortcutAction.Again].DisplayText);
    }

    [Fact]
    public void Restore_disables_invalid_duplicate_and_os_conflicting_rows_explicitly()
    {
        var raw = ShortcutDefaults.All.Select(pair => StoredShortcutBinding.From(pair.Key, pair.Value)).ToList();
        raw.RemoveAll(row => row.Command is "Again" or "Hard" or "Good");
        raw.Add(new("Again", "not-a-binding"));
        raw.Add(new("Hard", "enabled|focused|F5"));
        raw.Add(new("Good", "enabled|global|Ctrl+Alt+F8"));
        var store = new FakeStore(raw);
        var native = new FakeNative();
        native.ConflictingChords.Add(ShortcutChord.Parse("Ctrl+Alt+F8"));
        using var service = new GlobalShortcutService(native, store);
        service.AttachWindowHandle((nint)42);

        var result = service.RestorePersisted();

        Assert.Equal(3, result.Issues.Count);
        Assert.Contains(result.Issues, issue => issue.Action == ShortcutAction.Again && issue.Kind == ShortcutConflictKind.InvalidBinding);
        Assert.Contains(result.Issues, issue => issue.Action == ShortcutAction.Hard && issue.Kind == ShortcutConflictKind.ApplicationDuplicate);
        Assert.Contains(result.Issues, issue => issue.Action == ShortcutAction.Good && issue.Kind == ShortcutConflictKind.OperatingSystem);
        Assert.False(service.Bindings[ShortcutAction.Again].IsEnabled);
        Assert.False(service.Bindings[ShortcutAction.Hard].IsEnabled);
        Assert.False(service.Bindings[ShortcutAction.Good].IsEnabled);
        Assert.All(store.Persisted.Keys, action => Assert.True(Enum.IsDefined(action)));
    }

    [Fact]
    public void Reset_all_rolls_back_the_entire_batch_when_a_default_cannot_register()
    {
        var custom = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        custom[ShortcutAction.Again] = Binding("Ctrl+Alt+F8", ShortcutScope.Global);
        custom[ShortcutAction.Hard] = Binding("Ctrl+Alt+F9", ShortcutScope.Global);
        var fixture = Fixture.Create(custom);
        fixture.Native.ConflictingChords.Add(ShortcutChord.Parse("F2"));

        var result = fixture.Service.ResetAll();

        Assert.False(result.Succeeded);
        Assert.Equal("Ctrl+Alt+F8", fixture.Service.Bindings[ShortcutAction.Again].DisplayText);
        Assert.Equal("Ctrl+Alt+F9", fixture.Service.Bindings[ShortcutAction.Hard].DisplayText);
        Assert.Equal("Ctrl+Alt+F8", fixture.Store.Persisted[ShortcutAction.Again].DisplayText);
        Assert.True(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("Ctrl+Alt+F8")));
        Assert.True(fixture.Native.IsRegistered(fixture.Handle, ShortcutChord.Parse("Ctrl+Alt+F9")));
    }

    [Fact]
    public void Window_handle_recreation_moves_registrations_and_routes_WM_HOTKEY_by_id()
    {
        var fixture = Fixture.Create();
        ShortcutAction? invoked = null;
        fixture.Service.ActionInvoked += (_, action) => invoked = action;
        var originalId = fixture.Native.IdFor(fixture.Handle, ShortcutChord.Parse("F1"));

        fixture.Service.AttachWindowHandle((nint)84);
        Assert.False(fixture.Native.HasAnyRegistration(fixture.Handle));
        var recreatedId = fixture.Native.IdFor((nint)84, ShortcutChord.Parse("F1"));
        Assert.False(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey, (nint)originalId,
            (nint)((0x72 << 16) | 0)));
        Assert.True(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey, (nint)recreatedId,
            (nint)(0x70 << 16)));
        Assert.Equal(ShortcutAction.Again, invoked);
    }

    [Fact]
    public void Partial_handle_recreation_failure_restores_every_old_registration()
    {
        var fixture = Fixture.Create();
        fixture.Native.FailUnregisterCallNumber = 2;

        var result = fixture.Service.AttachWindowHandle((nint)84);

        Assert.False(result.Succeeded);
        Assert.Equal(6, ShortcutDefaults.All.Count(pair => pair.Value.Scope == ShortcutScope.Global));
        Assert.All(ShortcutDefaults.All.Where(pair => pair.Value.Scope == ShortcutScope.Global),
            pair => Assert.True(fixture.Native.IsRegistered(fixture.Handle, pair.Value.Chord)));
        Assert.False(fixture.Native.HasAnyRegistration((nint)84));
    }

    [Fact]
    public void Partial_reset_unregistration_failure_restores_the_complete_custom_set()
    {
        var custom = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        custom[ShortcutAction.Again] = Binding("Ctrl+Alt+F8", ShortcutScope.Global);
        custom[ShortcutAction.Hard] = Binding("Ctrl+Alt+F9", ShortcutScope.Global);
        var fixture = Fixture.Create(custom);
        fixture.Native.FailUnregisterCallNumber = 2;

        var result = fixture.Service.ResetAll();

        Assert.False(result.Succeeded);
        Assert.All(custom.Where(pair => pair.Value.Scope == ShortcutScope.Global),
            pair => Assert.True(fixture.Native.IsRegistered(fixture.Handle, pair.Value.Chord)));
        Assert.Equal("Ctrl+Alt+F8", fixture.Store.Persisted[ShortcutAction.Again].DisplayText);
    }

    [Fact]
    public void Bare_modifiers_Win_combinations_and_F12_are_rejected_without_native_calls()
    {
        var fixture = Fixture.Create();
        fixture.Native.Calls.Clear();

        var modifier = fixture.Service.TryReplace(ShortcutAction.Again,
            new ShortcutBinding(new ShortcutChord(0x10, ShortcutModifiers.None), ShortcutScope.Global, true));
        var windows = fixture.Service.TryReplace(ShortcutAction.Again,
            new ShortcutBinding(new ShortcutChord('L', ShortcutModifiers.Windows), ShortcutScope.Global, true));
        var f12 = fixture.Service.TryReplace(ShortcutAction.Again, Binding("F12", ShortcutScope.Global));

        Assert.All([modifier, windows, f12], result => Assert.Equal(ShortcutConflictKind.Reserved, result.ConflictKind));
        Assert.Empty(fixture.Native.Calls);
    }

    [Fact]
    public void Dispose_unregisters_every_global_binding_exactly_once()
    {
        var fixture = Fixture.Create();

        fixture.Service.Dispose();

        Assert.False(fixture.Native.HasAnyRegistration(fixture.Handle));
        Assert.Equal(6, fixture.Native.Calls.Count(call => call.StartsWith("unregister", StringComparison.Ordinal)));
    }

    [Fact]
    public void Concurrent_replacements_are_serialized_and_callbacks_observe_committed_state()
    {
        var fixture = Fixture.Create();
        var observed = new List<string>();
        fixture.Service.BindingChanged += (_, args) =>
        {
            lock (observed) observed.Add(fixture.Service.Bindings[args.Action].DisplayText);
        };

        Assert.True(fixture.Service.TryReplace(ShortcutAction.Again, Binding("Ctrl+Alt+F8", ShortcutScope.Global)).Succeeded);
        Assert.True(fixture.Service.TryReplace(ShortcutAction.Hard, Binding("Ctrl+Alt+F9", ShortcutScope.Global)).Succeeded);

        Assert.Equal("Ctrl+Alt+F8", fixture.Service.Bindings[ShortcutAction.Again].DisplayText);
        Assert.Equal("Ctrl+Alt+F9", fixture.Service.Bindings[ShortcutAction.Hard].DisplayText);
        Assert.Equal(2, observed.Count);
    }

    [Fact]
    public async Task Sqlite_store_round_trips_disabled_duplicate_chords_without_violating_schema_uniqueness()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"wordflow-shortcuts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
            await new MigrationRunner(factory).MigrateAsync(default);
            var store = new SqliteShortcutBindingStore(factory);
            var sameChord = new ShortcutBinding(ShortcutChord.Parse("Ctrl+Alt+F8"), ShortcutScope.Focused, false);
            var replacement = new[]
            {
                StoredShortcutBinding.From(ShortcutAction.Again, sameChord),
                StoredShortcutBinding.From(ShortcutAction.Hard, sameChord),
            };

            using (var transaction = store.BeginReplace(replacement)) transaction.Commit();

            var loaded = store.Load();
            Assert.Equal(replacement, loaded);
            Assert.All(loaded, row => Assert.True(ShortcutBinding.TryParse(row.Value, out _)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Callback_fault_is_reported_without_escaping_WM_HOTKEY_or_blocking_later_subscribers()
    {
        var fixture = Fixture.Create();
        Exception? reported = null;
        ShortcutAction? later = null;
        fixture.Service.CallbackFaulted += (_, args) => reported = args.Exception;
        fixture.Service.ActionInvoked += (_, _) => throw new InvalidOperationException("consumer failed");
        fixture.Service.ActionInvoked += (_, action) => later = action;
        int id = fixture.Native.IdFor(fixture.Handle, ShortcutChord.Parse("F1"));

        Assert.True(fixture.Service.ProcessWindowMessage(GlobalShortcutService.WmHotKey, (nint)id,
            (nint)(0x70 << 16)));

        Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal(ShortcutAction.Again, later);
    }

    [Fact]
    public void Failed_candidate_cleanup_is_reported_tracked_and_retried_before_the_next_edit()
    {
        var fixture = Fixture.Create();
        var candidate = ShortcutChord.Parse("Ctrl+Alt+F8");
        fixture.Store.FailNextStage = true;
        fixture.Native.PersistentUnregisterFailures.Add(candidate);

        var failed = fixture.Service.TryReplace(ShortcutAction.Again,
            new ShortcutBinding(candidate, ShortcutScope.Global, true));

        Assert.False(failed.Succeeded);
        Assert.Contains("cleanup", failed.ConflictReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.Native.IsRegistered(fixture.Handle, candidate));

        fixture.Native.PersistentUnregisterFailures.Clear();
        var recovered = fixture.Service.TryReplace(ShortcutAction.Again,
            new ShortcutBinding(candidate, ShortcutScope.Global, true));

        Assert.True(recovered.Succeeded);
        Assert.Equal(1, fixture.Native.CountRegistrations(fixture.Handle, candidate));
        Assert.Equal(ShortcutLifecycleState.Ready, fixture.Service.Lifecycle.State);
    }

    private static ShortcutBinding Binding(string chord, ShortcutScope scope, bool enabled = true) =>
        new(ShortcutChord.Parse(chord), scope, enabled);

    private static void AssertDefault(
        KeyValuePair<ShortcutAction, ShortcutBinding> pair,
        ShortcutAction action,
        string display,
        ShortcutScope scope)
    {
        Assert.Equal(action, pair.Key);
        Assert.Equal(display, pair.Value.DisplayText);
        Assert.Equal(scope, pair.Value.Scope);
        Assert.True(pair.Value.IsEnabled);
    }

    private sealed record Fixture(GlobalShortcutService Service, FakeNative Native, FakeStore Store, nint Handle)
    {
        public static Fixture Create(IReadOnlyDictionary<ShortcutAction, ShortcutBinding>? initial = null)
        {
            var native = new FakeNative();
            var store = new FakeStore((initial ?? ShortcutDefaults.All)
                .Select(pair => StoredShortcutBinding.From(pair.Key, pair.Value)));
            var service = new GlobalShortcutService(native, store);
            nint handle = (nint)42;
            service.AttachWindowHandle(handle);
            var restored = service.RestorePersisted();
            Assert.Empty(restored.Issues);
            native.Calls.Clear();
            return new(service, native, store, handle);
        }
    }

    private sealed class FakeNative : IHotKeyNative
    {
        private readonly Dictionary<(nint Handle, int Id), ShortcutChord> registrations = [];
        public HashSet<ShortcutChord> ConflictingChords { get; } = [];
        public HashSet<ShortcutChord> PersistentUnregisterFailures { get; } = [];
        public List<string> Calls { get; } = [];
        public bool FailNextUnregister { get; set; }
        public int? FailUnregisterCallNumber { get; set; }
        private int unregisterCallCount;

        public bool TryRegister(nint handle, int id, ShortcutChord chord, out int errorCode)
        {
            Calls.Add($"register:{handle}:{id}:{chord}");
            if (ConflictingChords.Contains(chord) || registrations.Any(pair => pair.Value == chord))
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
            Calls.Add($"unregister:{handle}:{id}");
            unregisterCallCount++;
            if ((registrations.TryGetValue((handle, id), out var chord) && PersistentUnregisterFailures.Contains(chord)) ||
                FailNextUnregister || unregisterCallCount == FailUnregisterCallNumber)
            {
                FailNextUnregister = false;
                errorCode = 5;
                return false;
            }
            errorCode = registrations.Remove((handle, id)) ? 0 : 1419;
            return errorCode == 0;
        }

        public bool IsRegistered(nint handle, ShortcutChord chord) =>
            registrations.Any(pair => pair.Key.Handle == handle && pair.Value == chord);

        public bool HasAnyRegistration(nint handle) => registrations.Keys.Any(key => key.Handle == handle);

        public int CountRegistrations(nint handle, ShortcutChord chord) =>
            registrations.Count(pair => pair.Key.Handle == handle && pair.Value == chord);

        public int IdFor(nint handle, ShortcutChord chord) =>
            registrations.Single(pair => pair.Key.Handle == handle && pair.Value == chord).Key.Id;
    }

    private sealed class FakeStore : IShortcutBindingStore
    {
        private List<StoredShortcutBinding> persisted;
        public FakeStore(IEnumerable<StoredShortcutBinding> initial) => persisted = initial.ToList();
        public bool FailNextStage { get; set; }
        public bool FailNextCommit { get; set; }
        public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Persisted => persisted
            .Select(row => (Row: row, ParsedAction: Enum.TryParse<ShortcutAction>(row.Command, out var action) ? action : (ShortcutAction?)null,
                ParsedBinding: ShortcutBinding.TryParse(row.Value, out var binding) ? binding : null))
            .Where(item => item.ParsedAction.HasValue && item.ParsedBinding is not null)
            .ToDictionary(item => item.ParsedAction!.Value, item => item.ParsedBinding!);

        public IReadOnlyList<StoredShortcutBinding> Load() => persisted.ToArray();

        public IShortcutBindingStoreTransaction BeginReplace(IReadOnlyCollection<StoredShortcutBinding> replacement)
        {
            if (FailNextStage)
            {
                FailNextStage = false;
                throw new IOException("stage failed");
            }
            return new Transaction(this, replacement.ToList());
        }

        private sealed class Transaction(FakeStore owner, List<StoredShortcutBinding> replacement) : IShortcutBindingStoreTransaction
        {
            public void Commit()
            {
                if (owner.FailNextCommit)
                {
                    owner.FailNextCommit = false;
                    throw new IOException("commit failed");
                }
                owner.persisted = replacement;
            }

            public void Dispose() { }
        }
    }
}
