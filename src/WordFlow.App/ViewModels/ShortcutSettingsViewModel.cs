using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.ViewModels;

public sealed class WpfShortcutDispatcher(Dispatcher dispatcher) : IShortcutDispatcher
{
    private readonly Dispatcher dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    public bool CheckAccess() => dispatcher.CheckAccess();
    public T Invoke<T>(Func<T> action) => CheckAccess() ? action() : dispatcher.Invoke(action);
    public void Invoke(Action action)
    {
        if (CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}

public static class WpfShortcutChordCapture
{
    public static ShortcutChord FromKey(Key key, ModifierKeys modifiers)
    {
        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        var shortcutModifiers = ShortcutModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control)) shortcutModifiers |= ShortcutModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt)) shortcutModifiers |= ShortcutModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) shortcutModifiers |= ShortcutModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) shortcutModifiers |= ShortcutModifiers.Windows;
        return new(virtualKey, shortcutModifiers);
    }
}

public sealed class ShortcutSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IShortcutService service;
    private bool disposed;
    private string? resetConflictReason;

    public ShortcutSettingsViewModel(IShortcutService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        Items = new ReadOnlyObservableCollection<ShortcutBindingItemViewModel>(
            new ObservableCollection<ShortcutBindingItemViewModel>(
                Enum.GetValues<ShortcutAction>().Select(action => new ShortcutBindingItemViewModel(service, action))));
        Labels = new ShortcutLabelMap(service);
    }

    public ReadOnlyObservableCollection<ShortcutBindingItemViewModel> Items { get; }
    public ShortcutLabelMap Labels { get; }
    public string? ResetConflictReason
    {
        get => resetConflictReason;
        private set
        {
            if (resetConflictReason == value) return;
            resetConflictReason = value;
            PropertyChanged?.Invoke(this, new(nameof(ResetConflictReason)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ResetAll()
    {
        var result = service.ResetAll();
        bool unchangedFailure = !result.Succeeded && resetConflictReason == result.ConflictReason;
        ResetConflictReason = result.ConflictReason;
        if (unchangedFailure) PropertyChanged?.Invoke(this, new(nameof(ResetConflictReason)));
        if (!result.Succeeded)
            foreach (var item in Items) item.RefreshAll();
        return result.Succeeded;
    }

    public void Dispose()
    {
        if (disposed) return;
        foreach (var item in Items) item.Dispose();
        Labels.Dispose();
        disposed = true;
    }
}

public sealed class ShortcutBindingItemViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IShortcutService service;
    private bool disposed;
    private string? conflictReason;
    private long lastVersion;

    internal ShortcutBindingItemViewModel(IShortcutService service, ShortcutAction action)
    {
        this.service = service;
        Action = action;
        conflictReason = service.RestoreIssues.FirstOrDefault(issue => issue.Action == action)?.Reason;
        service.BindingChanged += OnBindingChanged;
    }

    public ShortcutAction Action { get; }
    public string ChordDisplay => Current.DisplayText;
    public string? ConflictReason
    {
        get => conflictReason;
        private set { if (conflictReason != value) { conflictReason = value; OnPropertyChanged(); } }
    }

    public ShortcutScope Scope
    {
        get => Current.Scope;
        set
        {
            if (value == Current.Scope) return;
            Apply(Current with { Scope = value }, nameof(Scope));
        }
    }

    public bool IsEnabled
    {
        get => Current.IsEnabled;
        set
        {
            if (value == Current.IsEnabled) return;
            Apply(Current with { IsEnabled = value }, nameof(IsEnabled));
        }
    }

    public bool Record(string chord)
    {
        if (!ShortcutChord.TryParse(chord, out var parsed))
        {
            ConflictReason = "Record one non-modifier key with optional Ctrl, Alt, or Shift modifiers.";
            OnPropertyChanged(nameof(ChordDisplay));
            return false;
        }
        return Apply(Current with { Chord = parsed }, nameof(ChordDisplay));
    }

    public bool Record(ShortcutChord chord) => Apply(Current with { Chord = chord }, nameof(ChordDisplay));

    public bool RestoreDefault() => Apply(ShortcutDefaults.For(Action),
        nameof(ChordDisplay), nameof(Scope), nameof(IsEnabled));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (disposed) return;
        service.BindingChanged -= OnBindingChanged;
        disposed = true;
    }

    private ShortcutBinding Current => service.Bindings[Action];

    internal void RefreshAll()
    {
        OnPropertyChanged(nameof(ChordDisplay));
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(IsEnabled));
    }

    private bool Apply(ShortcutBinding candidate, params string[] attemptedProperties)
    {
        var result = service.TryReplace(Action, candidate);
        ConflictReason = result.ConflictReason;
        if (!result.Succeeded)
            foreach (string property in attemptedProperties) OnPropertyChanged(property);
        return result.Succeeded;
    }

    private void OnBindingChanged(object? sender, ShortcutBindingChangedEventArgs args)
    {
        if (args.Action != Action) return;
        if (args.Version < lastVersion) return;
        lastVersion = args.Version;
        ConflictReason = null;
        RefreshAll();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class ShortcutLabelMap : INotifyPropertyChanged, IDisposable
{
    private readonly IShortcutService service;
    private readonly Dictionary<ShortcutAction, string> labels;
    private readonly Dictionary<ShortcutAction, long> versions = [];
    private bool disposed;

    internal ShortcutLabelMap(IShortcutService service)
    {
        this.service = service;
        labels = service.Bindings.ToDictionary(pair => pair.Key, pair => Format(pair.Value));
        foreach (var action in labels.Keys) versions[action] = 0;
        service.BindingChanged += OnBindingChanged;
    }

    public string this[ShortcutAction action] => labels[action];
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (disposed) return;
        service.BindingChanged -= OnBindingChanged;
        disposed = true;
    }

    private void OnBindingChanged(object? sender, ShortcutBindingChangedEventArgs args)
    {
        if (args.Version < versions[args.Action]) return;
        versions[args.Action] = args.Version;
        labels[args.Action] = Format(args.Binding);
        PropertyChanged?.Invoke(this, new("Item[]"));
    }

    private static string Format(ShortcutBinding binding) =>
        binding.IsEnabled ? binding.DisplayText : $"Disabled · {binding.DisplayText}";
}

public sealed class FocusedShortcutBindingBridge : IDisposable
{
    private readonly IShortcutService service;
    private readonly object gate = new();
    private readonly Dictionary<ShortcutChord, ShortcutAction> focused = [];
    private bool disposed;

    public FocusedShortcutBindingBridge(IShortcutService service)
    {
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        Rebuild();
        service.BindingChanged += OnBindingChanged;
    }

    public event EventHandler<ShortcutAction>? ActionInvoked;
    public event EventHandler<ShortcutCallbackFaultedEventArgs>? CallbackFaulted;

    public bool TryInvoke(ShortcutChord chord)
    {
        ShortcutAction action;
        lock (gate)
        {
            if (disposed || !focused.TryGetValue(chord, out action)) return false;
        }
        if (ActionInvoked is { } handlers)
        {
            foreach (EventHandler<ShortcutAction> handler in handlers.GetInvocationList())
            {
                try { handler(this, action); }
                catch (Exception exception) { PublishFault(exception); }
            }
        }
        return true;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            focused.Clear();
        }
        service.BindingChanged -= OnBindingChanged;
    }

    private void OnBindingChanged(object? sender, ShortcutBindingChangedEventArgs args) => Rebuild();

    private void PublishFault(Exception exception)
    {
        if (CallbackFaulted is not { } handlers) return;
        foreach (EventHandler<ShortcutCallbackFaultedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, new(exception)); }
            catch { }
        }
    }

    private void Rebuild()
    {
        lock (gate)
        {
            focused.Clear();
            foreach (var pair in service.Bindings.Where(pair => pair.Value.IsEnabled && pair.Value.Scope == ShortcutScope.Focused))
                focused[pair.Value.Chord] = pair.Key;
        }
    }
}
