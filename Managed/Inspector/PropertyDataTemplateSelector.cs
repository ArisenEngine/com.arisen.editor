using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Templates;
using ArisenEditorFramework.Inspector;

namespace ArisenEditorFramework.Inspector;

/// <summary>
/// Dynamically selects an Avalonia Control template based on the underlying PropertyType
/// of a PropertyItemViewModel instance.
/// </summary>
public class PropertyDataTemplateSelector : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is not PropertyItemViewModel prop)
            return new TextBlock { Text = "Unsupported Type" };

        var control = PropertyEditorRegistry.CreateEditor(prop);
        if (control != null)
            return control;

        // Fallback to a plain TextBox if no editor is found
        var textBox = new TextBox 
        { 
            HorizontalAlignment = HorizontalAlignment.Stretch,
            AcceptsReturn = false
        };
        textBox.Bind(TextBox.TextProperty, 
                     new Avalonia.Data.Binding(nameof(PropertyItemViewModel.Value)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        return textBox;
    }

    public bool Match(object? data)
    {
        return data is PropertyItemViewModel;
    }
}

public class DecimalObjectConverter : IValueConverter
{
    private Type _targetBaseType;
    public DecimalObjectConverter(Type targetBaseType) { _targetBaseType = targetBaseType; }

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value == null) return null;
        try { return System.Convert.ToDecimal(value); } catch { return 0m; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value == null) return null;
        try { return System.Convert.ChangeType(value, _targetBaseType); } catch { return null; }
    }
}
