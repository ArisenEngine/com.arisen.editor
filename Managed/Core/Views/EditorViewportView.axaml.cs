using ArisenEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ArisenEditor.Views;

public partial class EditorViewportView : UserControl
{
    public EditorViewportView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        var container = this.FindControl<ContentControl>("ViewportContainer");
        if (container != null)
        {
            container.Content = new ArisenViewportControl();
        }
    }

    private void OnCaptureClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is EditorViewportViewModel vm)
        {
            vm.Capture();
        }
    }
}
