namespace ArisenEditorFramework.Commands;

/// <summary>
/// Interface for all undoable editor commands.
/// Every user action in the Editor MUST be implemented as an IEditorCommand
/// to support undo/redo and headless automation (AI-First Architecture).
///
/// Third-party packages can implement this interface to add custom commands
/// that integrate with the Editor's undo/redo system.
/// </summary>
public interface IEditorCommand
{
    /// <summary>
    /// A human-readable description of what this command does (for undo/redo UI and logging).
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Executes the command, applying its changes.
    /// </summary>
    void Execute();

    /// <summary>
    /// Reverts the changes made by Execute().
    /// </summary>
    void Undo();
}
