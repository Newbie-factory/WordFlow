using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using WordFlow.Application.Ports;
using WordFlow.Application.Shortcuts;

namespace WordFlow.App.ViewModels;

public sealed class ShortcutSettingsViewModel : IDisposable
{
    private readonly IShortcutService service;
    private bool disposed;

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
    public string? ResetConflictReason { get; private set; }

    public bool ResetAll()
    {
        var result = service.ResetAll();
        ResetConflictReason = result.ConflictReason;
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
            Apply(Current with { Scope = value });
        }
    }

    public bool IsEnabled
    {
        get => Current.IsEnabled;
        set
        {
            if (value == Current.IsEnabled) return;
            Apply(Current with { IsEnabled = value });
        }
    }

    public bool Record(string chord)
    {
        if (!ShortcutChord.TryParse(chord, out var parsed))
        {
            ConflictReason = "Record one non-modifier key with optional Ctrl, Alt, or Shift modifiers.";
            return false;
        }
        return Apply(Current with { Chord = parsed });
    }

    public bool Record(ShortcutChord chord) => Apply(Current with { Chord = chord });

    public bool RestoreDefault() => Apply(ShortcutDefaults.For(Action));

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        if (disposed) return;
        service.BindingChanged -= OnBindingChanged;
        disposed = true;
    }

    private ShortcutBinding Current => service.Bindings[Action];

    private bool Apply(ShortcutBinding candidate)
    {
        var result = service.TryReplace(Action, candidate);
        ConflictReason = result.ConflictReason;
        return result.Succeeded;
    }

    private void OnBindingChanged(object? sender, ShortcutBindingChangedEventArgs args)
    {
        if (args.Action != Action) return;
        ConflictReason = null;
        OnPropertyChanged(nameof(ChordDisplay));
        OnPropertyChanged(nameof(Scope));
        OnPropertyChanged(nameof(IsEnabled));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}

public sealed class ShortcutLabelMap : INotifyPropertyChanged, IDisposable
{
    private readonly IShortcutService service;
    private readonly Dictionary<ShortcutAction, string> labels;
    private bool disposed;

    internal ShortcutLabelMap(IShortcutService service)
    {
        this.service = service;
        labels = service.Bindings.ToDictionary(pair => pair.Key, pair => Format(pair.Value));
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

    public bool TryInvoke(ShortcutChord chord)
    {
        ShortcutAction action;
        lock (gate)
        {
            if (disposed || !focused.TryGetValue(chord, out action)) return false;
        }
        ActionInvoked?.Invoke(this, action);
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
