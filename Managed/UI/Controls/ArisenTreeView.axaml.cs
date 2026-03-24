using System;
using System.Collections;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ArisenEditorFramework.Hierarchy;

namespace ArisenEditorFramework.UI.Controls;

public partial class ArisenTreeView : UserControl
{
    public static readonly StyledProperty<IEnumerable> ItemsSourceProperty =
        AvaloniaProperty.Register<ArisenTreeView, IEnumerable>(nameof(ItemsSource));

    public IEnumerable ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly StyledProperty<IHierarchyItem?> SelectedItemProperty =
        AvaloniaProperty.Register<ArisenTreeView, IHierarchyItem?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public IHierarchyItem? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    // Tunneling event routing instead of bubbling to prevent TreeDataGrid issues
    public event EventHandler<TappedEventArgs>? ItemDoubleTapped;

    private Avalonia.Point _dragStartPoint;
    private bool _isPointerPressed = false;

    public ArisenTreeView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        var treeView = this.FindControl<TreeView>("MainTreeView");
        if (treeView != null)
        {
            treeView.AddHandler(DragDrop.DragOverEvent, DragOver);
            treeView.AddHandler(DragDrop.DropEvent, Drop);
        }
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is IHierarchyItem item)
        {
            // Optional: If we want to rename on double tap instead of firing external event
            // item.BeginRenameCommand?.Execute(null);

            // Forward the double tapped event to subscribers
            ItemDoubleTapped?.Invoke(control, e);

            // Focus the textbox asynchronously after the UI switches visibility if it's editing
            if (item.IsEditing)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                    var parent = control.Parent as Panel;
                    if (parent != null)
                    {
                        var textBox = parent.Children.OfType<TextBox>().FirstOrDefault();
                        if (textBox != null && textBox.IsVisible)
                        {
                            textBox.Focus();
                            textBox.SelectAll();
                        }
                    }
                });
            }
        }
    }

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename(sender as TextBox);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelRename(sender as TextBox);
            e.Handled = true;
        }
    }

    private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        CommitRename(sender as TextBox);
    }
    
    private void CommitRename(TextBox? textBox)
    {
        if (textBox?.DataContext is IHierarchyItem item && item.IsEditing)
        {
            item.Name = textBox.Text ?? string.Empty;
            item.EndRenameCommand?.Execute(null);
        }
    }

    private void CancelRename(TextBox? textBox)
    {
        if (textBox?.DataContext is IHierarchyItem item && item.IsEditing)
        {
            // Revert back to view model's current valid name
            textBox.Text = item.Name;
            item.EndRenameCommand?.Execute(null);
        }
    }

    // --- Drag and Drop Logic ---

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed)
        {
            _dragStartPoint = e.GetPosition(this);
            _isPointerPressed = true;
        }
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isPointerPressed = false;
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPointerPressed) return;

        var currentPoint = e.GetPosition(this);
        var diff = currentPoint - _dragStartPoint;

        // Start drag if moved beyond threshold (e.g., 3 pixels)
        if (System.Math.Abs(diff.X) > 3 || System.Math.Abs(diff.Y) > 3)
        {
            _isPointerPressed = false; // Reset to avoid multiple drag initializations
            
            if (sender is Control control && control.DataContext is IHierarchyItem item)
            {
                var dragData = new DataObject();
                dragData.Set("HierarchyItem", item);

                // Start the drag drop operation
                await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move);
            }
        }
    }
    
    private void DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains("HierarchyItem"))
        {
             var sourceItem = e.Data.Get("HierarchyItem") as IHierarchyItem;
             var targetElement = e.Source as Control;
             var targetItem = targetElement?.DataContext as IHierarchyItem;

             if (sourceItem != null && targetItem != null && targetItem.CanAcceptDrop(sourceItem))
             {
                 e.DragEffects = DragDropEffects.Move;
                 return;
             }
        }
        e.DragEffects = DragDropEffects.None;
    }

    private void Drop(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains("HierarchyItem"))
        {
             var sourceItem = e.Data.Get("HierarchyItem") as IHierarchyItem;
             var targetElement = e.Source as Control;
             var targetItem = targetElement?.DataContext as IHierarchyItem;

             if (sourceItem != null && targetItem != null && targetItem.CanAcceptDrop(sourceItem))
             {
                 targetItem.AcceptDrop(sourceItem);
                 e.Handled = true;
             }
        }
    }
}
