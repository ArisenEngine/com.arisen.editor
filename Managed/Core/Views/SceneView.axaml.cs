using System;
using ArisenEditor.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ArisenEditor.Views;

public partial class SceneView : UserControl
{
    private EditorViewportView? m_Viewport;

    internal bool HasWorldPartitionOverlayVisual =>
        WorldPartitionOverlayHost.Parent != null &&
        WorldPartitionOverlayToggle.Parent != null &&
        WorldPartitionOverlayPanel.Parent != null;

    public SceneView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        LoadViewport();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (m_Viewport?.DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        if (m_Viewport != null)
        {
            SceneViewContainer.Children.Remove(m_Viewport);
            m_Viewport = null;
        }
    }

    private void LoadViewport()
    {
        if (m_Viewport != null)
        {
            return;
        }

        var sceneViewModel = DataContext as SceneViewModel;
        m_Viewport = new EditorViewportView
        {
            DataContext = new EditorViewportViewModel(
                isSceneView: true,
                sceneViewModel?.SelectionService)
        };
        SceneViewContainer.Children.Insert(0, m_Viewport);
    }
}
