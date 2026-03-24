using System.Collections.Generic;
using System.Windows.Input;

namespace ArisenEditorFramework.Core.Models;

public class MenuItemModel
{
    public string Header { get; set; } = string.Empty;
    public ICommand? Command { get; set; }
    public object? CommandParameter { get; set; }
    public List<MenuItemModel> Items { get; set; } = new();
    public bool IsSeparator { get; set; }
    public string? Icon { get; set; }
    public string? Shortcut { get; set; }
    public int Priority { get; set; }
}
