using System;
using ArisenEditor.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ArisenEditor.Views;

public partial class SceneView : UserControl
{
    private EditorViewportView? m_Viewport;
    private readonly List<Control> m_ExtensionOverlays = new();

    internal bool HasWorldPartitionOverlayVisual =>
        WorldPartitionOverlayHost.Parent != null &&
        WorldPartitionOverlayToggle.Parent != null &&
        WorldPartitionOverlayPanel.Parent != null;

    internal ArisenKernel.Contracts.RenderSurfaceRegistration CurrentRenderSurfaceRegistration =>
        m_Viewport?.CurrentRenderSurfaceRegistration ?? default;

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

        for (int index = m_ExtensionOverlays.Count - 1; index >= 0; index--)
        {
            Control overlay = m_ExtensionOverlays[index];
            ExtensionOverlayHost.Children.Remove(overlay);
            if (overlay is IDisposable overlayDisposable)
            {
                overlayDisposable.Dispose();
            }
        }
        m_ExtensionOverlays.Clear();
    }

    private void LoadViewport()
    {
        if (m_Viewport != null)
        {
            return;
        }

        var sceneViewModel = DataContext as SceneViewModel;
        if (sceneViewModel != null && m_ExtensionOverlays.Count == 0)
        {
            foreach (var registration in sceneViewModel.SceneViewOverlays)
            {
                Control overlay = registration.Factory();
                m_ExtensionOverlays.Add(overlay);
                ExtensionOverlayHost.Children.Add(overlay);
            }
        }
        m_Viewport = new EditorViewportView
        {
            DataContext = new EditorViewportViewModel(
                isSceneView: true,
                sceneViewModel?.SelectionService)
        };
        SceneViewContainer.Children.Insert(0, m_Viewport);
    }
}
