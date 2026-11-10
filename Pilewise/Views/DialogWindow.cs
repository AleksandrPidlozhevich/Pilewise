using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Pilewise.Views;

/// <summary>
/// Base for the app's dialogs. A button with the <c>confirm</c> class closes the dialog with
/// <c>true</c>, one with <c>cancel</c> (and Esc) with <c>false</c>, so <c>ShowDialog&lt;bool&gt;</c>
/// gets the answer without per-window click handlers.
/// </summary>
public class DialogWindow : Window
{
    public const string ConfirmClass = "confirm";
    public const string CancelClass = "cancel";

    public DialogWindow()
    {
        AddHandler(Button.ClickEvent, OnButtonClick);
    }

    private void OnButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button) return;

        if (button.Classes.Contains(ConfirmClass)) Close(true);
        else if (button.Classes.Contains(CancelClass)) Close(false);
        else return;

        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close(false);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}