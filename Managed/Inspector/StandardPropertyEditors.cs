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
        
        var xNum = new NumericUpDown { Margin = new Avalonia.Thickness(0), FormatString = "0.00", ShowButtonSpinner = false, Watermark = "X" };
        var yNum = new NumericUpDown { Margin = new Avalonia.Thickness(4, 0, 0, 0), FormatString = "0.00", ShowButtonSpinner = false, Watermark = "Y" };
        var zNum = new NumericUpDown { Margin = new Avalonia.Thickness(4, 0, 0, 0), FormatString = "0.00", ShowButtonSpinner = false, Watermark = "Z" };

        grid.Children.Add(xNum); Grid.SetColumn(xNum, 0);
        grid.Children.Add(yNum); Grid.SetColumn(yNum, 1);
        grid.Children.Add(zNum); Grid.SetColumn(zNum, 2);

        void UpdateFromViewModel()
        {
            if (property.Value is System.Numerics.Vector3 vec)
            {
                xNum.Value = (decimal)vec.X;
                yNum.Value = (decimal)vec.Y;
                zNum.Value = (decimal)vec.Z;
            }
        }

        UpdateFromViewModel();
        
        property.PropertyChanged += (s, e) => {
            if (e.PropertyName == nameof(PropertyItemViewModel.Value))
            {
                // We need to dispatch to UI thread if we are not on it, but the property changes from UI usually
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateFromViewModel);
            }
        };

        var onValueChangedHandler = new EventHandler<NumericUpDownValueChangedEventArgs>((sender, e) =>
        {
            if (property.IsReadOnly) return;
            var newVec = new System.Numerics.Vector3(
                (float)(xNum.Value ?? 0m),
                (float)(yNum.Value ?? 0m),
                (float)(zNum.Value ?? 0m)
            );
            property.Value = newVec;
        });

        xNum.ValueChanged += onValueChangedHandler;
        yNum.ValueChanged += onValueChangedHandler;
        zNum.ValueChanged += onValueChangedHandler;

        return grid;
    }
}

public class QuaternionPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) 
    {
        var type = property.PropertyType;
        return type.Name == "Quaternion" || type.FullName == "System.Numerics.Quaternion";
    }

    public Control CreateControl(PropertyItemViewModel property)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        
        var xNum = new NumericUpDown { Margin = new Avalonia.Thickness(0), FormatString = "0.00", ShowButtonSpinner = false, Watermark = "X" };
        var yNum = new NumericUpDown { Margin = new Avalonia.Thickness(4, 0, 0, 0), FormatString = "0.00", ShowButtonSpinner = false, Watermark = "Y" };
        var zNum = new NumericUpDown { Margin = new Avalonia.Thickness(4, 0, 0, 0), FormatString = "0.00", ShowButtonSpinner = false, Watermark = "Z" };
        var wNum = new NumericUpDown { Margin = new Avalonia.Thickness(4, 0, 0, 0), FormatString = "0.00", ShowButtonSpinner = false, Watermark = "W" };

        grid.Children.Add(xNum); Grid.SetColumn(xNum, 0);
        grid.Children.Add(yNum); Grid.SetColumn(yNum, 1);
        grid.Children.Add(zNum); Grid.SetColumn(zNum, 2);
        grid.Children.Add(wNum); Grid.SetColumn(wNum, 3);

        void UpdateFromViewModel()
        {
            if (property.Value is System.Numerics.Quaternion q)
            {
                xNum.Value = (decimal)q.X;
                yNum.Value = (decimal)q.Y;
                zNum.Value = (decimal)q.Z;
                wNum.Value = (decimal)q.W;
            }
        }

        UpdateFromViewModel();
        
        property.PropertyChanged += (s, e) => {
            if (e.PropertyName == nameof(PropertyItemViewModel.Value))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateFromViewModel);
            }
        };

        var onValueChangedHandler = new EventHandler<NumericUpDownValueChangedEventArgs>((sender, e) =>
        {
            if (property.IsReadOnly) return;
            var newQ = new System.Numerics.Quaternion(
                (float)(xNum.Value ?? 0m),
                (float)(yNum.Value ?? 0m),
                (float)(zNum.Value ?? 0m),
                (float)(wNum.Value ?? 0m)
            );
            property.Value = newQ;
        });

        xNum.ValueChanged += onValueChangedHandler;
        yNum.ValueChanged += onValueChangedHandler;
        zNum.ValueChanged += onValueChangedHandler;
        wNum.ValueChanged += onValueChangedHandler;

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
