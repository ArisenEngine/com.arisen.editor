using System;
using Avalonia.Controls;
using Avalonia.Data;

namespace ArisenEditorFramework.Inspector;

public class BooleanPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) => property.PropertyType == typeof(bool);

    public Control CreateControl(PropertyItemViewModel property)
    {
        var checkBox = new CheckBox { Margin = new Avalonia.Thickness(0) };
        checkBox.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, 
                      new Binding(nameof(PropertyItemViewModel.Value)) { Mode = BindingMode.TwoWay });
        
        var wrapper = new StackPanel { 
            Orientation = Avalonia.Layout.Orientation.Horizontal, 
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center 
        };
        wrapper.Children.Add(checkBox);
        return wrapper;
    }
}

public class NumericPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property)
    {
        var type = property.PropertyType;
        return type == typeof(int) || type == typeof(float) || type == typeof(double) || type == typeof(long) || type == typeof(decimal);
    }

    public Control CreateControl(PropertyItemViewModel property)
    {
        var type = property.PropertyType;
        var numericUpDown = new NumericUpDown 
        { 
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            FormatString = (type == typeof(int) || type == typeof(long)) ? "0" : "0.00",
            ShowButtonSpinner = true
        };
        
        if (type == typeof(int) || type == typeof(long))
            numericUpDown.Increment = 1m;
        else
            numericUpDown.Increment = 0.1m;

        numericUpDown.Bind(NumericUpDown.ValueProperty, 
                           new Binding(nameof(PropertyItemViewModel.Value)) 
                           { 
                               Mode = BindingMode.TwoWay, 
                               Converter = new DecimalObjectConverter(type) 
                           });
                           
        return numericUpDown;
    }
}

public class EnumPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) => property.PropertyType.IsEnum && !Attribute.IsDefined(property.PropertyType, typeof(FlagsAttribute));

    public Control CreateControl(PropertyItemViewModel property)
    {
        var comboBox = new ComboBox 
        { 
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            ItemsSource = Enum.GetValues(property.PropertyType)
        };
        comboBox.Bind(ComboBox.SelectedItemProperty, 
                      new Binding(nameof(PropertyItemViewModel.Value)) { Mode = BindingMode.TwoWay });
        return comboBox;
    }
}
public class StringPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) => property.PropertyType == typeof(string);

    public Control CreateControl(PropertyItemViewModel property)
    {
        var textBox = new TextBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        textBox.Bind(TextBox.TextProperty, new Binding(nameof(PropertyItemViewModel.Value)) { Mode = BindingMode.TwoWay });
        return textBox;
    }
}

public class Vector3PropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) 
    {
        var type = property.PropertyType;
        return type.Name == "Vector3" || type.FullName == "System.Numerics.Vector3";
    }

    public Control CreateControl(PropertyItemViewModel property)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        
        for (int i = 0; i < 3; i++)
        {
            var component = new NumericUpDown 
            { 
                Margin = new Avalonia.Thickness(i == 0 ? 0 : 4, 0, 0, 0),
                ShowButtonSpinner = false,
                FormatString = "0.00"
            };
            // In a real implementation, we'd bind to X, Y, Z sub-properties
            // For now, this is a placeholder for the visual refactor
            grid.Children.Add(component);
            Grid.SetColumn(component, i);
        }
        
        return grid;
    }
}
public class ColorPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) 
    {
        var type = property.PropertyType;
        return type.Name == "Color" || type.FullName == "System.Drawing.Color" || type.FullName == "Avalonia.Media.Color";
    }

    public Control CreateControl(PropertyItemViewModel property)
    {
        var colorPicker = new ColorPicker 
        { 
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Margin = new Avalonia.Thickness(0)
        };
        colorPicker.Bind(ColorPicker.ColorProperty, new Binding(nameof(PropertyItemViewModel.Value)) { Mode = BindingMode.TwoWay });
        return colorPicker;
    }
}
