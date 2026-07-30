namespace ArisenEditorFramework.Extensions;

/// <summary>
/// Registration surface for optional packages that extend the Editor UI.
/// </summary>
public interface IEditorExtensionRegistry
{
    int Count { get; }
    bool IsEditorActive { get; }

    void Register(IEditorExtension extension);

    /// <summary>
    /// Removes the exact registered extension instance. Returns false when it was already absent.
    /// </summary>
    bool Unregister(IEditorExtension extension);
}
