using Avalonia.Controls;

namespace ArisenEditorFramework.Extensions;

/// <summary>
/// Immutable setup descriptor for a package-provided SceneView overlay.
/// The returned control owns its visibility UI and any package-specific state.
/// </summary>
public sealed class EditorSceneViewOverlayRegistration
{
    public string Id { get; }
    public int Order { get; }
    public Func<Control> Factory { get; }

    public EditorSceneViewOverlayRegistration(
        string id,
        Func<Control> factory,
        int order = 0)
    {
        if (string.IsNullOrWhiteSpace(id) ||
            !string.Equals(id, id.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "[Editor.Extensions] SceneView overlay ID must be non-empty and cannot have leading or trailing whitespace.",
                nameof(id));
        }

        Id = id;
        Order = order;
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }
}
