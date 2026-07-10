using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace ArisenEditorFramework.Inspector;

public sealed class ActionPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property)
    {
        return property is ActionPropertyItemViewModel;
    }

    public Control CreateControl(PropertyItemViewModel property)
    {
        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Avalonia.Thickness(8, 4)
        };

        button.Bind(ContentControl.ContentProperty, new Binding(nameof(ActionPropertyItemViewModel.ButtonText)));
        button.Bind(Button.CommandProperty, new Binding(nameof(ActionPropertyItemViewModel.Command)));
        ToolTip.SetTip(button, property.Description);
        return button;
    }
}
