using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using System;

namespace Pilewise.Views;

internal static class Shortcuts
{
    /// <summary>True for exactly command + <paramref name="key"/> (no Shift / Alt).</summary>
    public static bool IsCommand(KeyEventArgs e, TopLevel topLevel, params Key[] keys)
    {
        return e.KeyModifiers == CommandModifier(topLevel) && Array.IndexOf(keys, e.Key) >= 0;
    }

    private static KeyModifiers CommandModifier(TopLevel topLevel)
    {
        return topLevel.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
    }
}