using System;

namespace ArisenEditorFramework.Core;

/// <summary>
/// Represents a tool that can be integrated into the Arisen Editor.
/// </summary>
public interface IEditorTool
{
    string DisplayName { get; }
    string Category { get; }
    void OnActivate();
    void OnDeactivate();
}

/// <summary>
/// Represents a dockable UI panel in the editor.
/// </summary>
public interface IEditorPanel
{
    string Title { get; }
    string Id { get; }
    object Content { get; }
}
