using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Controls;
using Avalonia.VisualTree;
using ArisenEditorFramework.Commands;

namespace ArisenEditorFramework.Services;

public class EditorShortcut
{
    public Key Key { get; }
    public KeyModifiers Modifiers { get; }
    
    // Support either a generic ICommand or our dedicated EditorCommand (undoable)
    public System.Windows.Input.ICommand? Command { get; }
    public Func<IEditorCommand>? EditorCommandFactory { get; }
    
    public bool BypassTextInput { get; }
    public Func<bool>? ContextEvaluator { get; }

    public EditorShortcut(Key key, KeyModifiers modifiers, Func<IEditorCommand> editorCommandFactory, bool bypassTextInput = false, Func<bool>? contextEvaluator = null)
    {
        Key = key;
        Modifiers = modifiers;
        EditorCommandFactory = editorCommandFactory;
        BypassTextInput = bypassTextInput;
        ContextEvaluator = contextEvaluator;
    }

    public EditorShortcut(Key key, KeyModifiers modifiers, System.Windows.Input.ICommand command, bool bypassTextInput = false, Func<bool>? contextEvaluator = null)
    {
        Key = key;
        Modifiers = modifiers;
        Command = command;
        BypassTextInput = bypassTextInput;
        ContextEvaluator = contextEvaluator;
    }
}

public class EditorInputManager
{
    public static EditorInputManager Instance { get; } = new EditorInputManager();

    private readonly Dictionary<(Key, KeyModifiers), List<EditorShortcut>> m_Shortcuts = new();

    private EditorInputManager() { }

    public void RegisterShortcut(EditorShortcut shortcut)
    {
        var tuple = (shortcut.Key, shortcut.Modifiers);
        if (!m_Shortcuts.TryGetValue(tuple, out var list))
        {
            list = new List<EditorShortcut>();
            m_Shortcuts[tuple] = list;
        }
        list.Add(shortcut);
    }

    public void UnregisterShortcut(Key key, KeyModifiers modifiers)
    {
        if (m_Shortcuts.TryGetValue((key, modifiers), out var list))
        {
            list.Clear();
        }
    }

    public void OnGlobalPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;

        bool isTextInput = false;
        var currentVisual = e.Source as Avalonia.Visual;
        
        while (currentVisual != null)
        {
            if (currentVisual is TextBox || currentVisual is NumericUpDown)
            {
                isTextInput = true;
                break;
            }
            currentVisual = currentVisual.GetVisualParent();
        }

        if (m_Shortcuts.TryGetValue((e.Key, e.KeyModifiers), out var list))
        {
            foreach (var shortcut in list)
            {
                if (shortcut.ContextEvaluator != null && !shortcut.ContextEvaluator())
                {
                    continue; // Skip if contextual constraints fail
                }

                if (isTextInput && !shortcut.BypassTextInput)
                {
                    // Let the text box naturally consume keys like Delete, Backspace, or arrows without executing an Editor hotkey over it.
                    continue; 
                }

                if (shortcut.EditorCommandFactory != null)
                {
                    var cmd = shortcut.EditorCommandFactory();
                    if (cmd != null)
                    {
                        CommandHistory.Instance.Execute(cmd);
                        e.Handled = true;
                        return;
                    }
                }
                else if (shortcut.Command != null && shortcut.Command.CanExecute(null))
                {
                    shortcut.Command.Execute(null);
                    e.Handled = true;
                    return;
                }
            }
        }
    }
}
