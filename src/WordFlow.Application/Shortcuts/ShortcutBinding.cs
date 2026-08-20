using System.Globalization;

namespace WordFlow.Application.Shortcuts;

public readonly record struct ShortcutChord(int VirtualKey, ShortcutModifiers Modifiers)
{
    private const int VkF1 = 0x70;
    private const int VkF24 = 0x87;
    private static readonly IReadOnlyDictionary<string, int> NamedKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Shift"] = 0x10,
        ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Alt"] = 0x12, ["Pause"] = 0x13,
        ["CapsLock"] = 0x14, ["Escape"] = 0x1B, ["Space"] = 0x20, ["PageUp"] = 0x21,
        ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25,
        ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D,
        ["Delete"] = 0x2E, ["Win"] = 0x5B, ["NumLock"] = 0x90, ["ScrollLock"] = 0x91,
    };

    public static ShortcutChord Parse(string value)
    {
        if (!TryParse(value, out var chord))
            throw new FormatException($"'{value}' is not a supported shortcut chord.");
        return chord;
    }

    public static bool TryParse(string? value, out ShortcutChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var modifiers = ShortcutModifiers.None;
        int? key = null;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Control;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ShortcutModifiers.Alt;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ShortcutModifiers.Shift;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                modifiers |= ShortcutModifiers.Windows;
            else if (key is null && TryParseKey(part, out var parsed)) key = parsed;
            else return false;
        }
        if (key is null) return false;
        chord = new(key.Value, modifiers);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ShortcutModifiers.Windows)) parts.Add("Win");
        parts.Add(FormatKey(VirtualKey));
        return string.Join('+', parts);
    }

    public string? Validate()
    {
        if (VirtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
            return "A modifier key cannot be recorded by itself.";
        if (VirtualKey <= 0 || VirtualKey > 0xFE)
            return "The key is outside the supported Windows virtual-key range.";
        if ((Modifiers & ~(ShortcutModifiers.Alt | ShortcutModifiers.Control | ShortcutModifiers.Shift | ShortcutModifiers.Windows)) != 0)
            return "The shortcut contains an unsupported modifier.";
        if (Modifiers.HasFlag(ShortcutModifiers.Windows))
            return "Windows-key combinations are reserved by the operating system.";
        if (VirtualKey == 0x7B)
            return "F12 is reserved by the Windows debugger.";
        if (VirtualKey == 0x09 && Modifiers == ShortcutModifiers.Alt)
            return "Alt+Tab is reserved by Windows.";
        if (VirtualKey == 0x1B && Modifiers is ShortcutModifiers.Alt or ShortcutModifiers.Control)
            return "This combination is reserved by Windows.";
        if (VirtualKey == 0x2E && Modifiers == (ShortcutModifiers.Control | ShortcutModifiers.Alt))
            return "Ctrl+Alt+Delete is reserved by Windows.";
        return null;
    }

    private static bool TryParseKey(string value, out int key)
    {
        if (NamedKeys.TryGetValue(value, out key)) return true;
        if (value.Length == 1)
        {
            char candidate = char.ToUpperInvariant(value[0]);
            if (candidate is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = candidate;
                return true;
            }
        }
        if (value.Length is 2 or 3 && value[0] is 'F' or 'f' &&
            int.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) &&
            function is >= 1 and <= 24)
        {
            key = VkF1 + function - 1;
            return true;
        }
        if (value.StartsWith("VK", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out key) && key is > 0 and <= 0xFE)
            return true;
        key = 0;
        return false;
    }

    private static string FormatKey(int key)
    {
        if (key is >= VkF1 and <= VkF24) return $"F{key - VkF1 + 1}";
        if (key is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)key).ToString(CultureInfo.InvariantCulture);
        var named = NamedKeys.FirstOrDefault(pair => pair.Value == key && pair.Key is not "Control");
        return named.Key ?? $"VK{key:X2}";
    }
}

public sealed record ShortcutBinding(ShortcutChord Chord, ShortcutScope Scope, bool IsEnabled)
{
    public string DisplayText => Chord.ToString();

    public override string ToString() =>
        $"{(IsEnabled ? "enabled" : "disabled")}|{Scope.ToString().ToLowerInvariant()}|{Chord}";

    public static bool TryParse(string? value, out ShortcutBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length is not (3 or 4) || !ShortcutChord.TryParse(parts[2], out var chord)) return false;
        if (parts.Length == 4 &&
            (!parts[3].StartsWith("action=", StringComparison.OrdinalIgnoreCase) ||
             !Enum.TryParse<ShortcutAction>(parts[3].AsSpan("action=".Length), true, out var action) ||
             !Enum.IsDefined(action)))
            return false;
        bool enabled;
        if (parts[0].Equals("enabled", StringComparison.OrdinalIgnoreCase)) enabled = true;
        else if (parts[0].Equals("disabled", StringComparison.OrdinalIgnoreCase)) enabled = false;
        else return false;
        if (!Enum.TryParse<ShortcutScope>(parts[1], true, out var scope) || !Enum.IsDefined(scope)) return false;
        binding = new(chord, scope, enabled);
        return true;
    }
}
