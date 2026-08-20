namespace WordFlow.Application.Shortcuts;

public enum ShortcutAction
{
    Again,
    Hard,
    Good,
    Slash,
    ToggleSynonyms,
    ToggleConfusables,
    Undo,
    Pronounce,
}

public enum ShortcutScope
{
    Global,
    Focused,
}

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public static class ShortcutDefaults
{
    private static readonly IReadOnlyDictionary<ShortcutAction, ShortcutBinding> Bindings =
        new Dictionary<ShortcutAction, ShortcutBinding>
        {
            [ShortcutAction.Again] = Global("F1"),
            [ShortcutAction.Hard] = Global("F2"),
            [ShortcutAction.Good] = Global("F3"),
            [ShortcutAction.Slash] = Global("Shift+F3"),
            [ShortcutAction.ToggleSynonyms] = Global("F4"),
            [ShortcutAction.ToggleConfusables] = Global("F5"),
            [ShortcutAction.Undo] = new(ShortcutChord.Parse("Ctrl+Z"), ShortcutScope.Focused, true),
            [ShortcutAction.Pronounce] = new(ShortcutChord.Unassigned, ShortcutScope.Focused, false),
        };

    public static IReadOnlyDictionary<ShortcutAction, ShortcutBinding> All => Bindings;

    public static ShortcutBinding For(ShortcutAction action) =>
        Bindings.TryGetValue(action, out var binding)
            ? binding
            : throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown shortcut action.");

    private static ShortcutBinding Global(string chord) =>
        new(ShortcutChord.Parse(chord), ShortcutScope.Global, true);
}
