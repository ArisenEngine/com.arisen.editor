using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ArisenEditorFramework.Assets;

public partial class AssetBrowserControl : UserControl
{
    private Avalonia.Point _dragStartPoint;
    private bool _isPointerPressed = false;

    public AssetBrowserControl()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is AssetItemViewModel item)
        {
            if (item.IsDirectory)
            {
                if (DataContext is AssetBrowserViewModel vm)
                {
                    vm.CurrentPath = item.FullPath;
                }
            }
        }
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsLeftButtonPressed && sender is Control control && control.DataContext is AssetItemViewModel item && !item.IsDirectory)
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

        // Start drag only if moved beyond threshold (3 pixels)
        if (System.Math.Abs(diff.X) > 3 || System.Math.Abs(diff.Y) > 3)
        {
            _isPointerPressed = false;

            if (sender is Control control && control.DataContext is AssetItemViewModel item)
            {
                var dragData = new DataObject();
                dragData.Set("AssetItem", item);
                dragData.Set("HierarchyItem", item);
                dragData.Set(DataFormats.Files, new[] { item.FullPath });

                await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy | DragDropEffects.Move);
            }
        }
    }
}

