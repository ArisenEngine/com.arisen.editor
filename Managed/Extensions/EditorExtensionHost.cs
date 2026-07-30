using ArisenEditor.Core.Factory;
using ArisenEditorFramework.Inspector;
using ArisenEditorFramework.Services;
using ArisenEditorFramework.UI.Menus;

namespace ArisenEditorFramework.Extensions;

internal sealed class EditorExtensionHost : IEditorExtensionContext, IDisposable
{
    private sealed class PanelComparer : IComparer<EditorPanelRegistration>
    {
        public static PanelComparer Instance { get; } = new();

        public int Compare(EditorPanelRegistration? left, EditorPanelRegistration? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            int region = left.DockRegion.CompareTo(right.DockRegion);
            if (region != 0)
            {
                return region;
            }

            int order = left.Order.CompareTo(right.Order);
            return order != 0
                ? order
                : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }
    }

    private readonly ArisenPanelFactory m_PanelFactory;
    private readonly HashSet<string> m_PanelIds;
    private readonly HashSet<string> m_SceneViewOverlayIds = new(StringComparer.Ordinal);
    private readonly List<EditorPanelRegistration> m_Panels = new();
    private readonly List<EditorSceneViewOverlayRegistration> m_SceneViewOverlays = new();
    private readonly List<IMenuProvider> m_MenuProviders = new();
    private readonly List<IPropertyEditor> m_PropertyEditors = new();
    private bool m_IsConfiguring;
    private bool m_IsCommitted;
    private bool m_IsDisposed;

    public IReadOnlyList<EditorPanelRegistration> Panels => m_Panels;

    public EditorExtensionHost(
        ArisenPanelFactory panelFactory,
        IReadOnlyList<IEditorExtension> extensions)
    {
        m_PanelFactory = panelFactory ?? throw new ArgumentNullException(nameof(panelFactory));
        ArgumentNullException.ThrowIfNull(extensions);
        m_PanelIds = new HashSet<string>(panelFactory.GetAvailablePanelIds(), StringComparer.Ordinal);

        try
        {
            foreach (var extension in extensions)
            {
                ArgumentNullException.ThrowIfNull(extension);
                m_IsConfiguring = true;
                extension.Configure(this);
                m_IsConfiguring = false;
            }

            m_Panels.Sort(PanelComparer.Instance);
            m_SceneViewOverlays.Sort(static (left, right) =>
            {
                int order = left.Order.CompareTo(right.Order);
                return order != 0
                    ? order
                    : StringComparer.Ordinal.Compare(left.Id, right.Id);
            });
            foreach (var panel in m_Panels)
            {
                m_PanelFactory.RegisterPanel(panel.Id, panel.Factory);
            }
            foreach (var overlay in m_SceneViewOverlays)
            {
                m_PanelFactory.RegisterSceneViewOverlay(overlay);
            }

            foreach (var provider in m_MenuProviders)
            {
                MenuRegistry.Instance.RegisterProvider(provider);
            }

            foreach (var editor in m_PropertyEditors)
            {
                PropertyEditorRegistry.RegisterEditor(editor);
            }

            m_IsCommitted = true;
        }
        catch
        {
            m_IsConfiguring = false;
            Dispose();
            throw;
        }
    }

    public void RegisterPanel(EditorPanelRegistration panel)
    {
        EnsureConfiguring();
        ArgumentNullException.ThrowIfNull(panel);
        if (!m_PanelIds.Add(panel.Id))
        {
            throw new InvalidOperationException(
                $"[Editor.Extensions] Panel ID '{panel.Id}' is already registered.");
        }

        m_Panels.Add(panel);
    }

    public void RegisterMenuProvider(IMenuProvider provider)
    {
        EnsureConfiguring();
        ArgumentNullException.ThrowIfNull(provider);
        m_MenuProviders.Add(provider);
    }

    public void RegisterSceneViewOverlay(EditorSceneViewOverlayRegistration overlay)
    {
        EnsureConfiguring();
        ArgumentNullException.ThrowIfNull(overlay);
        if (!m_SceneViewOverlayIds.Add(overlay.Id))
        {
            throw new InvalidOperationException(
                $"[Editor.Extensions] SceneView overlay ID '{overlay.Id}' is already registered.");
        }

        m_SceneViewOverlays.Add(overlay);
    }

    public void RegisterPropertyEditor(IPropertyEditor editor)
    {
        EnsureConfiguring();
        ArgumentNullException.ThrowIfNull(editor);
        m_PropertyEditors.Add(editor);
    }

    public void Dispose()
    {
        if (m_IsDisposed)
        {
            return;
        }

        m_IsDisposed = true;
        if (m_IsCommitted)
        {
            for (int i = m_SceneViewOverlays.Count - 1; i >= 0; i--)
            {
                m_PanelFactory.UnregisterSceneViewOverlay(m_SceneViewOverlays[i]);
            }
            for (int i = m_PropertyEditors.Count - 1; i >= 0; i--)
            {
                PropertyEditorRegistry.UnregisterEditor(m_PropertyEditors[i]);
            }

            for (int i = m_MenuProviders.Count - 1; i >= 0; i--)
            {
                MenuRegistry.Instance.UnregisterProvider(m_MenuProviders[i]);
            }

            for (int i = m_Panels.Count - 1; i >= 0; i--)
            {
                m_PanelFactory.UnregisterPanel(m_Panels[i].Id);
            }
        }

        m_PropertyEditors.Clear();
        m_MenuProviders.Clear();
        m_Panels.Clear();
        m_SceneViewOverlays.Clear();
        m_SceneViewOverlayIds.Clear();
        m_PanelIds.Clear();
    }

    private void EnsureConfiguring()
    {
        if (!m_IsConfiguring || m_IsDisposed)
        {
            throw new InvalidOperationException(
                "[Editor.Extensions] Contributions may only be registered during IEditorExtension.Configure().");
        }
    }
}
