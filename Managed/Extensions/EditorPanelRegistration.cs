using ArisenEditorFramework.Core;

namespace ArisenEditorFramework.Extensions;

public enum EditorDockRegion
{
    Left,
    Center,
    Right,
    Bottom
}

/// <summary>
/// Immutable setup descriptor for a package-provided dock panel.
/// </summary>
public sealed class EditorPanelRegistration
{
    public string Id { get; }
    public string Title { get; }
    public EditorDockRegion DockRegion { get; }
    public int Order { get; }
    public Func<IEditorPanel> Factory { get; }

    public EditorPanelRegistration(
        string id,
        string title,
        EditorDockRegion dockRegion,
        Func<IEditorPanel> factory,
        int order = 0)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !string.Equals(id, id.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "[Editor.Extensions] Panel ID must be non-empty and cannot have leading or trailing whitespace.",
                nameof(id));
        }

        if (string.IsNullOrWhiteSpace(title) ||
            !string.Equals(title, title.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "[Editor.Extensions] Panel title must be non-empty and cannot have leading or trailing whitespace.",
                nameof(title));
        }

        Id = id;
        Title = title;
        DockRegion = dockRegion;
        Order = order;
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }
}
