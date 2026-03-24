namespace ArisenEditorFramework.Docking;

/// <summary>
/// Represents a custom window or tool inside the ArisenEngine editor that can be docked, floated, and hot-reloaded.
/// </summary>
public interface IEditorWindow
{
    /// <summary>
    /// A unique identifier for this window instance, crucial for layout serialization and hot reloading.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// The display title of the window.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Gets the UI content of the window. Can be an Avalonia Control or a ViewModel mapped by a DataTemplate.
    /// </summary>
    object GetContent();

    /// <summary>
    /// Called before the AssemblyLoadContext is unloaded. Implementations should serialize their state to a string.
    /// </summary>
    string SerializeState();

    /// <summary>
    /// Called after the AssemblyLoadContext is reloaded. Implementations should restore their state from the string.
    /// </summary>
    void DeserializeState(string state);
}
