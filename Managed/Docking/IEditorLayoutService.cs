namespace ArisenEditorFramework.Docking;

/// <summary>
/// Provides services for managing the layout of editor windows.
/// </summary>
public interface IEditorLayoutService
{
    /// <summary>
    /// Adds or focuses a registered custom window in the layout.
    /// </summary>
    void OpenWindow(IEditorWindow window);

    /// <summary>
    /// Closes a window and removes it from the layout.
    /// </summary>
    void CloseWindow(IEditorWindow window);

    /// <summary>
    /// Serializes the entire docking layout to a JSON string.
    /// </summary>
    string SaveLayout();

    /// <summary>
    /// Restores the docking layout from a JSON string.
    /// </summary>
    void LoadLayout(string layoutData);
}
