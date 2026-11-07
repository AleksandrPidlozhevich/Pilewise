using Avalonia.Markup.Xaml;

namespace Pilewise.Views;

public partial class DuplicatesWindow : DialogWindow
{
    public DuplicatesWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
