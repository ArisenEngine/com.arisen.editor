using ArisenEditorFramework.Core;
using ArisenEditor.Core.Services;
using ArisenEditor.Views;
using ReactiveUI;

namespace ArisenEditor.ViewModels;

/// <summary>
/// Scene View Model
/// </summary>
internal class SceneViewModel : EditorPanelBase
{
    private readonly EditorProjectService m_ProjectService;
    private bool m_IsWorldPartitionOverlayVisible;

    public SelectionService? SelectionService { get; }

    public bool IsWorldPartitionOverlayVisible
    {
        get => m_IsWorldPartitionOverlayVisible;
        set
        {
            if (m_IsWorldPartitionOverlayVisible == value)
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref m_IsWorldPartitionOverlayVisible, value);
            m_ProjectService.UserSettings.ShowWorldPartitionOverlay = value;
            m_ProjectService.SaveUserSettings();
        }
    }

    public override string Title => "Scene";
    public override string Id => "Scene";
    public override object Content => new SceneView { DataContext = this };

    internal SceneViewModel(SelectionService? selectionService = null)
    {
        m_ProjectService = EditorProjectService.Instance;
        SelectionService = selectionService;
        m_IsWorldPartitionOverlayVisible = m_ProjectService.UserSettings.ShowWorldPartitionOverlay;
    }
}
