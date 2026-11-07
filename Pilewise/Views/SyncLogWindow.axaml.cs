using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Pilewise.Views;

public partial class SyncLogWindow : Window
{
    public SyncLogWindow()
    {
        // Loaded directly (no named controls), so it doesn't depend on the IDE's source generator.
        AvaloniaXamlLoader.Load(this);
    }
}
