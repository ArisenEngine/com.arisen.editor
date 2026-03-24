using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ArisenEditorFramework.UI.Controls;

namespace ArisenEditorFramework.Hierarchy;

public partial class HierarchyControl : UserControl
{
    public HierarchyControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
