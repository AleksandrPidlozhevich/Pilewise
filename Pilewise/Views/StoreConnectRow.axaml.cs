using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Pilewise.Views;

/// <summary>
/// One store's connect row (icon + Connect button / code field / "Connected" check). The icon is
/// supplied per-store via <see cref="Icon"/> so the theme-aware <c>DynamicResource</c> stays at the
/// call site; everything else binds to a <c>StoreConnectionViewModel</c> set as the DataContext.
/// </summary>
public partial class StoreConnectRow : UserControl
{
    public static readonly StyledProperty<IImage?> IconProperty =
        AvaloniaProperty.Register<StoreConnectRow, IImage?>(nameof(Icon));

    public IImage? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public StoreConnectRow()
    {
        AvaloniaXamlLoader.Load(this);
    }
}