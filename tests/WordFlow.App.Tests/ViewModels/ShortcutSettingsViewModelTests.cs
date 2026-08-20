using System.ComponentModel;
using System.Windows.Input;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.Tests.ViewModels;

public sealed class ShortcutSettingsViewModelTests
{
    [Fact]
    public void Pronunciation_is_configurable_but_has_no_invented_default_key()
    {
        var binding = ShortcutDefaults.For(ShortcutAction.Pronounce);

        Assert.False(binding.IsEnabled);
        Assert.False(binding.Chord.IsAssigned);
        Assert.Equal("未分配", binding.DisplayText);
        Assert.Equal("disabled|focused|Unassigned", binding.ToString());
        Assert.True(ShortcutBinding.TryParse(binding.ToString(), out var restored));
        Assert.Equal(binding, restored);
    }

    [Fact]
    public void Recorder_rows_show_normalized_chord_scope_and_enable_state()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);

        var slash = viewModel.Items.Single(item => item.Action == ShortcutAction.Slash);

        Assert.Equal("Shift+F3", slash.ChordDisplay);
        Assert.Equal(ShortcutScope.Global, slash.Scope);
        Assert.True(slash.IsEnabled);
        Assert.Null(slash.ConflictReason);
    }

    [Fact]
    public void Recording_a_chord_and_changing_scope_or_enable_uses_atomic_replacement()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);
        var undo = viewModel.Items.Single(item => item.Action == ShortcutAction.Undo);

        Assert.True(undo.Record("ctrl+shift+u"));
        undo.Scope = ShortcutScope.Global;
        undo.IsEnabled = false;

        Assert.Equal(3, service.Replacements.Count);
        Assert.Equal("Ctrl+Shift+U", undo.ChordDisplay);
        Assert.Equal(ShortcutScope.Global, undo.Scope);
        Assert.False(undo.IsEnabled);
    }

    [Fact]
    public void Failed_edit_exposes_conflict_and_preserves_displayed_binding()
    {
        var service = new FakeShortcutService { NextFailure = ShortcutRegistrationResult.Conflict(
            ShortcutConflictKind.ApplicationDuplicate, "Already assigned to Again.") };
        using var viewModel = new ShortcutSettingsViewModel(service);
        var hard = viewModel.Items.Single(item => item.Action == ShortcutAction.Hard);

        var succeeded = hard.Record("F1");

        Assert.False(succeeded);
        Assert.Equal("F2", hard.ChordDisplay);
        Assert.Equal("Already assigned to Again.", hard.ConflictReason);
    }

    [Fact]
    public void Restore_default_and_reset_all_update_every_live_button_label_immediately()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);
        var changed = new List<string?>();
        viewModel.Labels.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var again = viewModel.Items.Single(item => item.Action == ShortcutAction.Again);

        Assert.True(again.Record("Ctrl+Alt+F8"));
        Assert.Equal("Ctrl+Alt+F8", viewModel.Labels[ShortcutAction.Again]);
        Assert.True(again.RestoreDefault());
        Assert.Equal("F1", viewModel.Labels[ShortcutAction.Again]);
        Assert.True(viewModel.ResetAll());
        Assert.All(ShortcutDefaults.All, pair => Assert.Equal(pair.Value.DisplayText, viewModel.Labels[pair.Key]));
        Assert.Contains("Item[]", changed);
    }

    [Fact]
    public void Focused_bridge_invokes_only_enabled_focused_bindings_and_tracks_changes()
    {
        var service = new FakeShortcutService();
        using var bridge = new FocusedShortcutBindingBridge(service);
        ShortcutAction? invoked = null;
        bridge.ActionInvoked += (_, action) => invoked = action;

        Assert.True(bridge.TryInvoke(ShortcutChord.Parse("Ctrl+Z")));
        Assert.Equal(ShortcutAction.Undo, invoked);
        invoked = null;
        Assert.False(bridge.TryInvoke(ShortcutChord.Parse("F1")));

        service.TryReplace(ShortcutAction.Undo,
            new ShortcutBinding(ShortcutChord.Parse("Ctrl+Shift+U"), ShortcutScope.Focused, true));

        Assert.False(bridge.TryInvoke(ShortcutChord.Parse("Ctrl+Z")));
        Assert.True(bridge.TryInvoke(ShortcutChord.Parse("Ctrl+Shift+U")));
        Assert.Equal(ShortcutAction.Undo, invoked);
    }

    [Fact]
    public void All_item_properties_notify_when_external_binding_change_arrives()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);
        var again = viewModel.Items.Single(item => item.Action == ShortcutAction.Again);
        var notifications = new List<string?>();
        again.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        service.TryReplace(ShortcutAction.Again,
            new ShortcutBinding(ShortcutChord.Parse("Ctrl+Alt+F8"), ShortcutScope.Focused, false));

        Assert.Contains(nameof(ShortcutBindingItemViewModel.ChordDisplay), notifications);
        Assert.Contains(nameof(ShortcutBindingItemViewModel.Scope), notifications);
        Assert.Contains(nameof(ShortcutBindingItemViewModel.IsEnabled), notifications);
        Assert.Equal("Ctrl+Alt+F8", again.ChordDisplay);
    }

    [Fact]
    public void Versioned_labels_ignore_stale_same_action_events_without_suppressing_other_actions()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);
        var newest = new ShortcutBinding(ShortcutChord.Parse("Ctrl+Alt+F8"), ShortcutScope.Global, true);
        var other = new ShortcutBinding(ShortcutChord.Parse("Ctrl+Alt+F9"), ShortcutScope.Global, true);

        service.Publish(ShortcutAction.Again, newest, version: 2, commit: true);
        service.Publish(ShortcutAction.Again, ShortcutDefaults.For(ShortcutAction.Again), version: 1, commit: false);
        service.Publish(ShortcutAction.Hard, other, version: 1, commit: true);

        Assert.Equal("Ctrl+Alt+F8", viewModel.Labels[ShortcutAction.Again]);
        Assert.Equal("Ctrl+Alt+F9", viewModel.Labels[ShortcutAction.Hard]);
        Assert.All(viewModel.Items, item => Assert.Equal(service.Bindings[item.Action],
            new ShortcutBinding(ShortcutChord.Parse(item.ChordDisplay), item.Scope, item.IsEnabled)));
    }

    [Fact]
    public void Focused_bridge_contains_each_action_and_fault_observer_exception()
    {
        var service = new FakeShortcutService();
        using var bridge = new FocusedShortcutBindingBridge(service);
        ShortcutAction? later = null;
        Exception? reported = null;
        bridge.ActionInvoked += (_, _) => throw new InvalidOperationException("action failed");
        bridge.ActionInvoked += (_, action) => later = action;
        bridge.CallbackFaulted += (_, args) => { reported = args.Exception; throw new InvalidOperationException("observer failed"); };

        var exception = Record.Exception(() => bridge.TryInvoke(ShortcutChord.Parse("Ctrl+Z")));

        Assert.Null(exception);
        Assert.IsType<InvalidOperationException>(reported);
        Assert.Equal(ShortcutAction.Undo, later);
    }

    [Theory]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.RightShift)]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightCtrl)]
    [InlineData(Key.LeftAlt)]
    [InlineData(Key.RightAlt)]
    public void Wpf_capture_preserves_extended_modifier_vk_for_validation(Key key)
    {
        var chord = WpfShortcutChordCapture.FromKey(key, ModifierKeys.None);

        Assert.NotNull(chord.Validate());
    }

    [Fact]
    public void Rejected_scope_and_enable_edits_notify_the_attempted_property_and_restore_visible_truth()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);
        var undo = viewModel.Items.Single(item => item.Action == ShortcutAction.Undo);
        var notifications = new List<string?>();
        undo.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        service.NextFailure = ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem, "scope conflict");
        undo.Scope = ShortcutScope.Global;
        service.NextFailure = ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem, "enable conflict");
        undo.IsEnabled = false;

        Assert.Equal(ShortcutScope.Focused, undo.Scope);
        Assert.True(undo.IsEnabled);
        Assert.Contains(nameof(ShortcutBindingItemViewModel.Scope), notifications);
        Assert.Contains(nameof(ShortcutBindingItemViewModel.IsEnabled), notifications);
        Assert.Equal("enable conflict", undo.ConflictReason);
    }

    [Fact]
    public void Failed_restore_default_refreshes_all_binding_properties()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);
        var again = viewModel.Items.Single(item => item.Action == ShortcutAction.Again);
        Assert.True(again.Record("Ctrl+Alt+F8"));
        var notifications = new List<string?>();
        again.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        service.NextFailure = ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem, "restore conflict");

        Assert.False(again.RestoreDefault());

        Assert.Equal("Ctrl+Alt+F8", again.ChordDisplay);
        Assert.Contains(nameof(ShortcutBindingItemViewModel.ChordDisplay), notifications);
        Assert.Contains(nameof(ShortcutBindingItemViewModel.Scope), notifications);
        Assert.Contains(nameof(ShortcutBindingItemViewModel.IsEnabled), notifications);
    }

    [Fact]
    public void Failed_reset_notifies_conflict_and_refreshes_every_row()
    {
        var service = new FakeShortcutService();
        using var viewModel = new ShortcutSettingsViewModel(service);
        var resetNotifications = new List<string?>();
        ((INotifyPropertyChanged)viewModel).PropertyChanged += (_, args) => resetNotifications.Add(args.PropertyName);
        var rowNotifications = viewModel.Items.ToDictionary(item => item.Action, _ => new List<string?>());
        foreach (var item in viewModel.Items)
            item.PropertyChanged += (_, args) => rowNotifications[item.Action].Add(args.PropertyName);
        service.NextFailure = ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem, "reset conflict");

        Assert.False(viewModel.ResetAll());

        Assert.Equal("reset conflict", viewModel.ResetConflictReason);
        Assert.Contains(nameof(ShortcutSettingsViewModel.ResetConflictReason), resetNotifications);
        Assert.All(rowNotifications.Values, notifications => Assert.Contains(nameof(ShortcutBindingItemViewModel.ChordDisplay), notifications));

        resetNotifications.Clear();
        service.NextFailure = ShortcutRegistrationResult.Conflict(ShortcutConflictKind.OperatingSystem, "reset conflict");
        Assert.False(viewModel.ResetAll());
        Assert.Contains(nameof(ShortcutSettingsViewModel.ResetConflictReason), resetNotifications);
    }

    [Fact]
    public void Shared_fault_hub_reports_global_and_focused_faults_contains_observers_and_continues()
    {
        var service = new FakeShortcutService();
        using var bridge = new FocusedShortcutBindingBridge(service);
        var hub = new ShortcutCallbackFaultHub();
        using var connection = hub.Connect(service, bridge);
        var reported = new List<string>();
        hub.CallbackFaulted += (_, _) => throw new InvalidOperationException("fault observer failed");
        hub.CallbackFaulted += (_, args) => reported.Add(args.Exception.Message);
        bridge.ActionInvoked += (_, _) => throw new InvalidOperationException("focused consumer failed");

        service.PublishFault(new InvalidOperationException("global consumer failed"));
        Assert.True(bridge.TryInvoke(ShortcutDefaults.For(ShortcutAction.Undo).Chord));

        Assert.Equal(["global consumer failed", "focused consumer failed"], reported);
    }

    private sealed class FakeShortcutService : IShortcutService
    {
        private readonly Dictionary<ShortcutAction, ShortcutBinding> bindings =
            ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);

        public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings => bindings;
        public IReadOnlyList<ShortcutRestoreIssue> RestoreIssues => [];
        public ShortcutLifecycleSnapshot Lifecycle => new(ShortcutLifecycleState.Ready);
        public List<(ShortcutAction Action, ShortcutBinding Candidate)> Replacements { get; } = [];
        public ShortcutRegistrationResult? NextFailure { get; set; }
        public event EventHandler<ShortcutAction>? ActionInvoked { add { } remove { } }
        public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted;
        public event EventHandler<ShortcutBindingChangedEventArgs>? BindingChanged;

        public ShortcutRegistrationResult TryReplace(ShortcutAction action, ShortcutBinding candidate)
        {
            Replacements.Add((action, candidate));
            if (NextFailure is { } failure)
            {
                NextFailure = null;
                return failure;
            }
            bindings[action] = candidate;
            BindingChanged?.Invoke(this, new(action, candidate));
            return ShortcutRegistrationResult.Success(candidate);
        }

        public ShortcutRegistrationResult ResetAll()
        {
            if (NextFailure is { } failure)
            {
                NextFailure = null;
                return failure;
            }
            foreach (var pair in ShortcutDefaults.All)
            {
                bindings[pair.Key] = pair.Value;
                BindingChanged?.Invoke(this, new(pair.Key, pair.Value));
            }
            return ShortcutRegistrationResult.Success();
        }

        public ShortcutRestoreResult RestorePersisted() => new([]);
        public ShortcutRegistrationResult AttachWindowHandle(nint handle) => ShortcutRegistrationResult.Success();
        public bool ProcessWindowMessage(int message, nint id, nint chordData = default) => false;
        public void Dispose() { }

        public void Publish(ShortcutAction action, ShortcutBinding binding, long version, bool commit)
        {
            if (commit) bindings[action] = binding;
            BindingChanged?.Invoke(this, new(action, binding, version));
        }
        public void PublishFault(Exception exception) => CallbackFaulted?.Invoke(this, new(exception));
    }
}
