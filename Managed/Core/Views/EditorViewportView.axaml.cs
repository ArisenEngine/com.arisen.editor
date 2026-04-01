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
        Content = new ArisenViewportControl();
    }
}
