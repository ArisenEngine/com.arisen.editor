using ArisenEditor.ViewModels;
using Avalonia.Controls;
using Avalonia.Data;

namespace ArisenEditorFramework.Inspector;

public sealed class MaterialTexturePropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property)
    {
        return property is MaterialTexturePropertyViewModel;
    }

    public Control CreateControl(PropertyItemViewModel property)
    {
        var materialProperty = (MaterialTexturePropertyViewModel)property;
        var comboBox = new ComboBox
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemsSource = materialProperty.Options,
            MaxDropDownHeight = 360
        };
        comboBox.Bind(
            ComboBox.SelectedItemProperty,
            new Binding(nameof(PropertyItemViewModel.Value)) { Mode = BindingMode.TwoWay });
        return comboBox;
    }
}
