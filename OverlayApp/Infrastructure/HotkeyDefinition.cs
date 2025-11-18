using System;
using System.Globalization;
using System.Linq;
using System.Windows.Input;

namespace OverlayApp.Infrastructure;

internal sealed record HotkeyDefinition(ModifierKeys Modifiers, Key Key)
{

    public override string ToString()
    {
        var modifierParts = Enum.GetValues(typeof(ModifierKeys))
            .Cast<ModifierKeys>()
            .Where(mod => mod != ModifierKeys.None && Modifiers.HasFlag(mod))
            .Select(mod => mod switch
            {
                ModifierKeys.Control => "Ctrl",
                ModifierKeys.Alt => "Alt",
                ModifierKeys.Shift => "Shift",
                ModifierKeys.Windows => "Win",
                _ => mod.ToString()
            });

        return string.Join('+', modifierParts.Concat(new[] { Key.ToString() }));
    }

    public static bool TryParse(string? value, out HotkeyDefinition definition)
    {
        definition = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        ModifierKeys modifiers = ModifierKeys.None;
        Key? key = null;

        foreach (var part in parts)
        {
            switch (part.ToLower(CultureInfo.InvariantCulture))
            {
                case "ctrl":
                case "control":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "alt":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "shift":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (key is not null)
                    {
                        return false;
                    }

                    if (!Enum.TryParse(part, true, out Key parsedKey))
                    {
                        return false;
                    }

                    key = parsedKey;
                    break;
            }
        }

        if (key is null)
        {
            return false;
        }

        definition = new HotkeyDefinition(modifiers, key.Value);
        return true;
    }

    public static HotkeyDefinition FromStringOrDefault(string? value, HotkeyDefinition fallback)
    {
        return TryParse(value, out var parsed) ? parsed : fallback;
    }
}
