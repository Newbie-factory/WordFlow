using System.ComponentModel;
using WordFlow.App.ViewModels;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.Tests.ViewModels;

public sealed class ShortcutSettingsViewModelTests
{
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

    private sealed class FakeShortcutService : IShortcutService
    {
        private readonly Dictionary<ShortcutAction, ShortcutBinding> bindings =
            ShortcutDefaults.All.ToDictionary(pair => pair.Key, pair => pair.Value);

        public IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings => bindings;
        public IReadOnlyList<ShortcutRestoreIssue> RestoreIssues => [];
        public List<(ShortcutAction Action, ShortcutBinding Candidate)> Replacements { get; } = [];
        public ShortcutRegistrationResult? NextFailure { get; set; }
        public event EventHandler<ShortcutAction>? ActionInvoked { add { } remove { } }
        public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted { add { } remove { } }
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
            foreach (var pair in ShortcutDefaults.All)
            {
                bindings[pair.Key] = pair.Value;
                BindingChanged?.Invoke(this, new(pair.Key, pair.Value));
            }
            return ShortcutRegistrationResult.Success();
        }

        public ShortcutRestoreResult RestorePersisted() => new([]);
        public void AttachWindowHandle(nint handle) { }
        public bool ProcessWindowMessage(int message, nint id) => false;
        public void Dispose() { }
    }
}
