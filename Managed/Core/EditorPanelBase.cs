using System;
using Avalonia.Controls;
using ReactiveUI;

namespace ArisenEditorFramework.Core;

/// <summary>
/// A base class for editor panels that provides standard windowing behavior.
/// Implements IDisposable for resource cleanup when panels are closed.
/// </summary>
public abstract class EditorPanelBase : ReactiveObject, IEditorPanel, IDisposable
{
    public abstract string Title { get; }
    public abstract string Id { get; }
    public abstract object Content { get; }

    /// <summary>
    /// Called when the panel is being closed or disposed. Override to add cleanup logic.
    /// </summary>
    public virtual void Dispose()
    {
        // Override in derived classes to clean up resources
    }
}
