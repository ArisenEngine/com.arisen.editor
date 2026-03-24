using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ArisenEditorFramework.Inspector;

public class ObjectReferencePropertyEditor : IPropertyEditor
{
    public bool CanHandle(PropertyItemViewModel property) 
    {
        var type = property.PropertyType;
        // Handle interface types or classes (excluding basic types)
        return !type.IsValueType && type != typeof(string);
    }

    public Control CreateControl(PropertyItemViewModel property)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto") };
        
        var textBlock = new TextBlock 
        { 
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Avalonia.Thickness(4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        
        void UpdateText()
        {
            var val = property.Value;
            if (val == null)
            {
                textBlock.Text = "None (" + property.PropertyType.Name + ")";
                textBlock.FontStyle = FontStyle.Italic;
                textBlock.Opacity = 0.5;
            }
            else
            {
                // In a real system, we might want to cast to something with a Name
                textBlock.Text = val.ToString();
                textBlock.FontStyle = FontStyle.Normal;
                textBlock.Opacity = 1.0;
            }
        }
        
        UpdateText();

        var clearButton = new Button 
        { 
            Content = "X", 
            Padding = new Avalonia.Thickness(4, 2),
            FontSize = 10,
            IsVisible = property.Value != null
        };
        
        clearButton.Click += (s, e) => {
            property.Value = null;
            UpdateText();
            clearButton.IsVisible = false;
        };

        // Handle Property Change
        property.PropertyChanged += (s, e) => {
            if (e.PropertyName == nameof(PropertyItemViewModel.Value))
            {
                UpdateText();
                clearButton.IsVisible = property.Value != null;
            }
        };

        grid.Children.Add(textBlock);
        Grid.SetColumn(textBlock, 0);
        
        grid.Children.Add(clearButton);
        Grid.SetColumn(clearButton, 1);

        // Support Drop
        var border = new Border 
        { 
            Background = Brushes.Transparent, 
            BorderBrush = Brushes.Gray, 
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new CornerRadius(2),
            Child = grid
        };
        
        DragDrop.SetAllowDrop(border, true);
        border.AddHandler(DragDrop.DragOverEvent, (s, e) => {
            if (e.Data.Contains("HierarchyItem"))
            {
                var item = e.Data.Get("HierarchyItem");
                if (item != null && property.PropertyType.IsAssignableFrom(item.GetType()))
                {
                    e.DragEffects = DragDropEffects.Move;
                }
                else
                {
                    e.DragEffects = DragDropEffects.None;
                }
            }
            e.Handled = true;
        });
        
        border.AddHandler(DragDrop.DropEvent, (s, e) => {
            if (e.Data.Contains("HierarchyItem"))
            {
                var item = e.Data.Get("HierarchyItem");
                if (item != null && property.PropertyType.IsAssignableFrom(item.GetType()))
                {
                    property.Value = item;
                    UpdateText();
                    clearButton.IsVisible = true;
                }
            }
            e.Handled = true;
        });

        return border;
    }
}
