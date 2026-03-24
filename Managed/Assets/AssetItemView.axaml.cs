using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ArisenEditorFramework.Assets;

public partial class AssetItemView : UserControl
{
    public AssetItemView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
