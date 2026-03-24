using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ArisenEditorFramework.Hierarchy;

namespace ArisenEditorFramework.UI.Controls;

public partial class ArisenListView : UserControl
{
    public static readonly StyledProperty<IEnumerable> ItemsSourceProperty =
        AvaloniaProperty.Register<ArisenListView, IEnumerable>(nameof(ItemsSource));

    public IEnumerable ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly StyledProperty<IHierarchyItem?> SelectedItemProperty =
        AvaloniaProperty.Register<ArisenListView, IHierarchyItem?>(nameof(SelectedItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public IHierarchyItem? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public static readonly StyledProperty<bool> IsIconModeProperty =
        AvaloniaProperty.Register<ArisenListView, bool>(nameof(IsIconMode), defaultValue: false);

    public bool IsIconMode
    {
        get => GetValue(IsIconModeProperty);
        set => SetValue(IsIconModeProperty, value);
    }

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<ArisenListView, double>(nameof(IconSize), defaultValue: 64.0);

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<ArisenListView, double>(nameof(ItemWidth), defaultValue: 80.0);

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public event EventHandler<TappedEventArgs>? ItemDoubleTapped;

    private Avalonia.Point _dragStartPoint;
    private bool _isPointerPressed = false;
    private ListBox? _mainListBox;

    public ArisenListView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _mainListBox = this.FindControl<ListBox>("MainListBox");
        if (_mainListBox != null)
        {
            _mainListBox.AddHandler(DragDrop.DragOverEvent, DragOver);
            _mainListBox.AddHandler(DragDrop.DropEvent, Drop);
        }

        UpdateModeTag();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsIconModeProperty)
        {
            UpdateModeTag();
        }
        else if (change.Property == IconSizeProperty)
        {
            ItemWidth = IconSize + 24; // Padding for text
        }
    }

    private void UpdateModeTag()
    {
        if (_mainListBox != null)
        {
            _mainListBox.Tag = IsIconMode ? "IconMode" : "ListMode";
        }
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is IHierarchyItem item)
        {
            ItemDoubleTapped?.Invoke(control, e);

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

        if (System.Math.Abs(diff.X) > 3 || System.Math.Abs(diff.Y) > 3)
        {
            _isPointerPressed = false; 
            
            if (sender is Control control && control.DataContext is IHierarchyItem item)
            {
                var dragData = new DataObject();
                // To maintain unity with hierarchy drag drop
                dragData.Set("HierarchyItem", item);
                if (item.GetType().Name == "FileTreeNode")
                {
                    dragData.Set("AssetItem", item);
                    var fullPath = item.GetType().GetProperty("Path")?.GetValue(item) as string;
                    if (!string.IsNullOrEmpty(fullPath))
                    {
                        dragData.Set(DataFormats.Files, new[] { fullPath });
                    }
                }

                await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move | DragDropEffects.Copy);
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
