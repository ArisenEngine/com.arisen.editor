using ArisenEditorFramework.Inspector;
using ArisenEditorFramework.UI.Menus;

namespace ArisenEditorFramework.Extensions;

/// <summary>
/// Bounded setup surface exposed while an Editor extension is being configured.
/// </summary>
public interface IEditorExtensionContext
{
    void RegisterPanel(EditorPanelRegistration panel);
    void RegisterSceneViewOverlay(EditorSceneViewOverlayRegistration overlay);
    void RegisterMenuProvider(IMenuProvider provider);
    void RegisterPropertyEditor(IPropertyEditor editor);
}
