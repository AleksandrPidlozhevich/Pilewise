using Avalonia.Markup.Xaml;

namespace Pilewise.Views;

public partial class NotionExportPreviewWindow : DialogWindow
{
    public NotionExportPreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
