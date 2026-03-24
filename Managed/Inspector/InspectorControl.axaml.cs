using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ArisenEditorFramework.Inspector;

public partial class InspectorControl : UserControl
{
    public InspectorControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
