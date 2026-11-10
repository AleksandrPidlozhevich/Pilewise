using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Pilewise.Services;

/// <summary>A screen's working area in physical pixels (the part not covered by the taskbar / dock).</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height);

/// <summary>
/// The main window's position (physical pixels), size (DIPs) and maximized state, remembered between
/// runs (state-store key <c>window</c>). Pure, so it's unit-tested; <c>MainWindow</c> applies it.
/// </summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized)
{
    public const string StateKey = "window";

    public string Serialize()
    {
        return string.Join(",",
            X.ToString(CultureInfo.InvariantCulture), Y.ToString(CultureInfo.InvariantCulture),
            Width.ToString("0", CultureInfo.InvariantCulture), Height.ToString("0", CultureInfo.InvariantCulture),
            Maximized ? "max" : "normal");
    }

    public static WindowPlacement? Parse(string? value)
    {
        var parts = value?.Split(',');
        if (parts is not { Length: 5 }) return null;

        var culture = CultureInfo.InvariantCulture;
        if (!int.TryParse(parts[0], NumberStyles.Integer, culture, out var x) ||
            !int.TryParse(parts[1], NumberStyles.Integer, culture, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, culture, out var width) ||
            !double.TryParse(parts[3], NumberStyles.Float, culture, out var height) ||
            width <= 0 || height <= 0)
            return null;

        return new WindowPlacement(x, y, width, height, parts[4] == "max");
    }

    /// <summary>
    /// The placement to restore on the current screens: the size is kept within
    /// [<paramref name="minWidth"/>×<paramref name="minHeight"/>, the largest screen], and the saved
    /// position only when the window's title bar would land on a screen (e.g. not after a monitor was
    /// unplugged); otherwise <c>KeepPosition</c> is false and the window should be centred.
    /// </summary>
    public (double Width, double Height, bool KeepPosition) Fit(
        IReadOnlyList<ScreenArea> screens, double scaling, double minWidth, double minHeight)
    {
        var maxWidth = screens.Count > 0 ? screens.Max(s => s.Width) / scaling : double.MaxValue;
        var maxHeight = screens.Count > 0 ? screens.Max(s => s.Height) / scaling : double.MaxValue;
        var width = Math.Clamp(Width, minWidth, Math.Max(minWidth, maxWidth));
        var height = Math.Clamp(Height, minHeight, Math.Max(minHeight, maxHeight));

        // A grab point on the title bar must be on some screen so the window can be moved back.
        var grabX = X + (int)(Math.Min(width, 200) * scaling / 2);
        var grabY = Y + (int)(16 * scaling);
        var visible = screens.Any(s =>
            grabX >= s.X && grabX < s.X + s.Width && grabY >= s.Y && grabY < s.Y + s.Height);

        return (width, height, visible);
    }
}