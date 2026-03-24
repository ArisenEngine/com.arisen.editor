using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace ArisenEditorFramework.Inspector;

public class FlagPropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) => property.PropertyType.IsEnum && Attribute.IsDefined(property.PropertyType, typeof(FlagsAttribute));

    public Control CreateControl(PropertyItemViewModel property)
    {
        var enumType = property.PropertyType;
        var values = Enum.GetValues(enumType).Cast<Enum>().ToList();
        
        // Use a dropdown with checkboxes inside? Or just a simple text display with a popup.
        // For a generic inspector, a ComboBox with a specialized internal template or a simple multi-select button.
        // Let's implement a simple StackPanel of CheckBoxes inside a ScrollViewer for now, or better, a Button that opens a Flyout.
        
        var button = new Button 
        { 
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = "Flags..." 
        };

        // Update button text to show current selection
        void UpdateButtonText()
        {
            var val = property.Value;
            button.Content = val?.ToString() ?? "None";
        }
        
        UpdateButtonText();

        var flyout = new Flyout();
        var panel = new StackPanel { Spacing = 2, Margin = new Avalonia.Thickness(4) };
        
        foreach (var val in values)
        {
            // Skip "None" or 0 if it exists? Usually useful to keep.
            var cb = new CheckBox { Content = val.ToString() };
            
            // Initial state
            var currentVal = (Enum?)property.Value;
            cb.IsChecked = currentVal?.HasFlag(val) ?? false;
            
            cb.IsCheckedChanged += (s, e) => {
                if (cb.IsChecked == true)
                {
                    var v = (Enum)property.Value!;
                    var result = Convert.ToInt64(v) | Convert.ToInt64(val);
                    property.Value = Enum.ToObject(enumType, result);
                    UpdateButtonText();
                }
                else
                {
                    var v = (Enum)property.Value!;
                    var result = Convert.ToInt64(v) & ~Convert.ToInt64(val);
                    property.Value = Enum.ToObject(enumType, result);
                    UpdateButtonText();
                }
            };
            
            panel.Children.Add(cb);
        }

        flyout.Content = new ScrollViewer { Content = panel, MaxHeight = 300 };
        button.Flyout = flyout;

        return button;
    }
}
