using System;
using System.Collections.Generic;
using ReactiveUI;

namespace ArisenEditorFramework.Commands;

/// <summary>
/// Manages the execution, undo, and redo of all Editor commands.
/// Central service for Editor Automation — both human UI actions and AI agents
/// interact with the Editor through this single entry point.
///
/// This lives in the Framework so that third-party packages can execute commands
/// without depending on the main Editor assembly.
/// </summary>
public class CommandHistory : ReactiveObject
{
    private static readonly Lazy<CommandHistory> s_Instance = new(() => new CommandHistory());
    public static CommandHistory Instance => s_Instance.Value;

    private readonly Stack<IEditorCommand> m_UndoStack = new();
    private readonly Stack<IEditorCommand> m_RedoStack = new();

    /// <summary>
    /// Fired after any command is executed, undone, or redone.
    /// Allows external listeners (e.g., Editor UI) to react to command state changes.
    /// </summary>
    public event Action<IEditorCommand>? CommandExecuted;
    public event Action<IEditorCommand>? CommandUndone;
    public event Action<IEditorCommand>? CommandRedone;

    /// <summary>
    /// Maximum number of commands to keep in the undo history.
    /// </summary>
    public int MaxHistorySize { get; set; } = 256;

    private bool m_CanUndo;
    public bool CanUndo
    {
        get => m_CanUndo;
        private set => this.RaiseAndSetIfChanged(ref m_CanUndo, value);
    }

    private bool m_CanRedo;
    public bool CanRedo
    {
        get => m_CanRedo;
        private set => this.RaiseAndSetIfChanged(ref m_CanRedo, value);
    }

    private CommandHistory() { }

    /// <summary>
    /// Executes a command and pushes it onto the undo stack.
    /// Clears the redo stack (new action breaks the redo chain).
    /// </summary>
    public void Execute(IEditorCommand command)
    {
        command.Execute();
        m_UndoStack.Push(command);
        m_RedoStack.Clear();

        UpdateState();
        CommandExecuted?.Invoke(command);
        System.Diagnostics.Debug.WriteLine($"[Command] Executed: {command.Description}");
    }

    /// <summary>
    /// Undoes the most recent command.
    /// </summary>
    public void Undo()
    {
        if (m_UndoStack.Count == 0) return;

        var command = m_UndoStack.Pop();
        command.Undo();
        m_RedoStack.Push(command);

        UpdateState();
        CommandUndone?.Invoke(command);
        System.Diagnostics.Debug.WriteLine($"[Command] Undo: {command.Description}");
    }

    /// <summary>
    /// Redoes the most recently undone command.
    /// </summary>
    public void Redo()
    {
        if (m_RedoStack.Count == 0) return;

        var command = m_RedoStack.Pop();
        command.Execute();
        m_UndoStack.Push(command);

        UpdateState();
        CommandRedone?.Invoke(command);
        System.Diagnostics.Debug.WriteLine($"[Command] Redo: {command.Description}");
    }

    /// <summary>
    /// Clears all undo/redo history (e.g., when loading a new scene).
    /// </summary>
    public void Clear()
    {
        m_UndoStack.Clear();
        m_RedoStack.Clear();
        UpdateState();
    }

    private void UpdateState()
    {
        CanUndo = m_UndoStack.Count > 0;
        CanRedo = m_RedoStack.Count > 0;
    }
}
