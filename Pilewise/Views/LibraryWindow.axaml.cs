using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Pilewise.ViewModels;

namespace Pilewise.Views;

public partial class LibraryWindow : Window
{
    private readonly TextBox _search;

    public LibraryWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _search = this.Named<TextBox>("Search");
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        if (Shortcuts.IsCommand(e, this, Key.F))
        {
            _search.Focus();
            _search.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            if (!string.IsNullOrEmpty(_search.Text)) _search.Text = "";
            else Close();
            e.Handled = true;
        }
    }

    // Double-clicking a game opens its store page (not when the double-click hit the Hide button).
    private void OnGameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && source.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        if (DataContext is LibraryViewModel library && sender is ListBox { SelectedItem: LibraryGameItem game })
            library.OpenStorePageCommand.Execute(game);
    }
}
