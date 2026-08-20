using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;
using WordFlow.Infrastructure.Data;
using WordFlow.Infrastructure.Windows;

namespace WordFlow.Infrastructure.Tests.Windows;

public sealed class ShortcutPersistenceIntegrationTests
{
    [Fact]
    public async Task Real_sqlite_transaction_dispose_rolls_back_the_complete_replacement()
    {
        await using var database = await ShortcutDatabase.CreateAsync();
        var store = new SqliteShortcutBindingStore(database.Factory);
        var original = Rows(ShortcutDefaults.All);
        using (var commit = store.BeginReplace(original)) commit.Commit();
        var changed = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        changed[ShortcutAction.Again] = Binding("Ctrl+Alt+F8", ShortcutScope.Focused);

        using (store.BeginReplace(Rows(changed))) { }

        Assert.Equal(original.OrderBy(row => row.Command, StringComparer.Ordinal), store.Load());
    }

    [Fact]
    public async Task Real_sqlite_contention_fails_bounded_and_recovers_after_owner_rollback()
    {
        await using var database = await ShortcutDatabase.CreateAsync();
        var first = new SqliteShortcutBindingStore(database.Factory, busyTimeoutMilliseconds: 50);
        var second = new SqliteShortcutBindingStore(database.Factory, busyTimeoutMilliseconds: 50);
        using (var seed = first.BeginReplace(Rows(ShortcutDefaults.All))) seed.Commit();
        var changed = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);
        changed[ShortcutAction.Again] = Binding("Ctrl+Alt+F8", ShortcutScope.Focused);

        using (first.BeginReplace(Rows(changed)))
            Assert.Throws<SqliteException>(() => second.BeginReplace(Rows(ShortcutDefaults.All)));

        using var recovered = second.BeginReplace(Rows(changed));
        recovered.Commit();
        Assert.Equal("Ctrl+Alt+F8", Parse(second.Load(), ShortcutAction.Again).DisplayText);
    }

    [Fact]
    public async Task Legacy_three_field_rows_restore_and_are_rewritten_to_action_identified_format()
    {
        await using var database = await ShortcutDatabase.CreateAsync();
        var store = new SqliteShortcutBindingStore(database.Factory);
        var legacy = Rows(ShortcutDefaults.All).ToList();
        legacy.RemoveAll(row => row.Command == nameof(ShortcutAction.Again));
        legacy.Add(new(nameof(ShortcutAction.Again), "enabled|focused|Ctrl+Alt+F8"));
        using (var seed = store.BeginReplace(legacy)) seed.Commit();
        var native = new MemoryNative();
        using var service = new GlobalShortcutService(native, store);
        Assert.True(service.AttachWindowHandle((nint)42).Succeeded);

        var restored = service.RestorePersisted();

        Assert.Empty(restored.Issues);
        Assert.Equal("Ctrl+Alt+F8", service.Bindings[ShortcutAction.Again].DisplayText);
        Assert.All(store.Load(), row => Assert.Contains("|action=", row.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Guarded_real_hwnd_service_registers_and_releases_an_uncommon_hotkey_in_finally()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var database = await ShortcutDatabase.CreateAsync();
        var configured = ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value with { IsEnabled = false });
        var chord = ShortcutChord.Parse("Ctrl+Alt+Shift+F23");
        configured[ShortcutAction.Again] = new(chord, ShortcutScope.Global, true);
        var store = new SqliteShortcutBindingStore(database.Factory);
        using (var seed = store.BeginReplace(Rows(configured))) seed.Commit();
        using var dispatcher = new MessageWindowDispatcher();
        var native = new Win32HotKeyNative();
        var service = new GlobalShortcutService(native, store, dispatcher);
        bool probeRegistered = false;
        try
        {
            Assert.True(service.AttachWindowHandle(dispatcher.Handle).Succeeded);
            Assert.Empty(service.RestorePersisted().Issues);
            bool duplicate = dispatcher.Invoke(() => native.TryRegister(dispatcher.Handle, 0x6000, chord, out _));
            Assert.False(duplicate);
            service.Dispose();
            probeRegistered = dispatcher.Invoke(() => native.TryRegister(dispatcher.Handle, 0x6001, chord, out _));
            Assert.True(probeRegistered);
        }
        finally
        {
            if (probeRegistered)
                dispatcher.Invoke(() => native.TryUnregister(dispatcher.Handle, 0x6001, out _));
            if (service.Lifecycle.State != ShortcutLifecycleState.Disposed)
                service.Dispose();
        }
    }

    private static ShortcutBinding Binding(string chord, ShortcutScope scope) => new(ShortcutChord.Parse(chord), scope, true);
    private static StoredShortcutBinding[] Rows(IReadOnlyDictionary<ShortcutAction, ShortcutBinding> bindings) =>
        bindings.OrderBy(pair => pair.Key).Select(pair => StoredShortcutBinding.From(pair.Key, pair.Value)).ToArray();
    private static ShortcutBinding Parse(IReadOnlyList<StoredShortcutBinding> rows, ShortcutAction action)
    {
        var row = rows.Single(item => item.Command == action.ToString());
        Assert.True(row.TryParse(out _, out var binding));
        return binding!;
    }

    private sealed class MemoryNative : IHotKeyNative
    {
        private readonly HashSet<(nint Handle, int Id)> registrations = [];
        public bool TryRegister(nint handle, int id, ShortcutChord chord, out int errorCode)
        { errorCode = registrations.Add((handle, id)) ? 0 : 1409; return errorCode == 0; }
        public bool TryUnregister(nint handle, int id, out int errorCode)
        { errorCode = registrations.Remove((handle, id)) ? 0 : 1419; return errorCode == 0; }
    }

    private sealed class ShortcutDatabase : IAsyncDisposable
    {
        private ShortcutDatabase(string directory, SqliteConnectionFactory factory) { Directory = directory; Factory = factory; }
        private string Directory { get; }
        public SqliteConnectionFactory Factory { get; }
        public static async Task<ShortcutDatabase> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), $"wordflow-shortcut-integration-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var factory = new SqliteConnectionFactory(Path.Combine(directory, "user.db"));
            await new MigrationRunner(factory).MigrateAsync(default);
            return new(directory, factory);
        }
        public ValueTask DisposeAsync()
        { System.IO.Directory.Delete(Directory, recursive: true); return ValueTask.CompletedTask; }
    }

    private sealed class MessageWindowDispatcher : IShortcutDispatcher, IDisposable
    {
        private readonly BlockingCollection<Action> queue = [];
        private readonly Thread thread;
        private readonly ManualResetEventSlim ready = new();
        public MessageWindowDispatcher()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "WordFlow real HWND shortcut smoke" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            if (Handle == 0) throw new InvalidOperationException("Could not create a message-only HWND.");
        }
        public nint Handle { get; private set; }
        private int ThreadId { get; set; }
        public bool CheckAccess() => Environment.CurrentManagedThreadId == ThreadId;
        public T Invoke<T>(Func<T> action)
        {
            if (CheckAccess()) return action();
            T? value = default;
            Exception? failure = null;
            using var done = new ManualResetEventSlim();
            queue.Add(() => { try { value = action(); } catch (Exception error) { failure = error; } finally { done.Set(); } });
            done.Wait();
            if (failure is not null) throw failure;
            return value!;
        }
        public void Invoke(Action action) => Invoke(() => { action(); return true; });
        public void Dispose() { queue.CompleteAdding(); thread.Join(); ready.Dispose(); queue.Dispose(); }
        private void Run()
        {
            ThreadId = Environment.CurrentManagedThreadId;
            Handle = CreateWindowExW(0, "STATIC", "", 0, 0, 0, 0, 0, (nint)(-3), 0, 0, 0);
            ready.Set();
            foreach (var action in queue.GetConsumingEnumerable()) action();
            if (Handle != 0) DestroyWindow(Handle);
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint handle);
    }
}
