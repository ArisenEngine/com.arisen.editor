using System;
using ArisenEditor.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ArisenEditor.Views;

public partial class SceneView : UserControl
{
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
        foreach (var child in SceneViewContainer.Children)
        {
            if (child is Control { DataContext: IDisposable disposable })
            {
                disposable.Dispose();
            }
        }

        SceneViewContainer.Children.Clear();
    }
    
    private void LoadViewport()
    {
        var sceneViewModel = DataContext as SceneViewModel;
        SceneViewContainer.Children.Add(new EditorViewportView()
        {
            DataContext = new EditorViewportViewModel(
                isSceneView: true,
                sceneViewModel?.SelectionService)
        });
    }
}
