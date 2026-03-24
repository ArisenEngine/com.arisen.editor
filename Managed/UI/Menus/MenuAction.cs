using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using ReactiveUI;

namespace ArisenEditorFramework.UI.Menus;

/// <summary>
/// Represents a single item in a context menu or toolbar menu.
/// Supports nesting for submenus.
/// </summary>
public class MenuAction : ReactiveObject
{
    private string _header = string.Empty;
    private string _icon = string.Empty; // Store icon identifier or SVG path
    private bool _isEnabled = true;
    private bool _isVisible = true;

    public string Header
    {
        get => _header;
        set => this.RaiseAndSetIfChanged(ref _header, value);
    }

    public string Icon
    {
        get => _icon;
        set => this.RaiseAndSetIfChanged(ref _icon, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => this.RaiseAndSetIfChanged(ref _isEnabled, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => this.RaiseAndSetIfChanged(ref _isVisible, value);
    }

    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }

    public ObservableCollection<MenuAction> Children { get; } = new();

    public MenuAction(string header, ICommand? command = null, object? parameter = null)
    {
        Header = header;
        Command = command;
        CommandParameter = parameter;
    }

    public MenuAction() { }
}
