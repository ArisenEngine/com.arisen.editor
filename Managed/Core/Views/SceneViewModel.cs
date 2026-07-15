using ArisenEditorFramework.Core;
using ArisenEditor.Core.Services;
using ArisenEditor.Views;

namespace ArisenEditor.ViewModels;

/// <summary>
/// Scene View Model
/// </summary>
internal class SceneViewModel : EditorPanelBase
{
    public SelectionService? SelectionService { get; }

    public override string Title => "Scene";
    public override string Id => "Scene";
    public override object Content => new SceneView { DataContext = this };

    internal SceneViewModel(SelectionService? selectionService = null)
    {
        SelectionService = selectionService;
    }
}
