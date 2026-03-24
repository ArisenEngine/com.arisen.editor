using System;

namespace ArisenEditorFramework.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MenuItem : System.Attribute
{
    public static readonly string kMenuItemSeparators = "/";

    public string menuItem;
    public bool separator;
    public string? icon;
    public string? shortcut;
    public int priority;
    public MenuItem(string itemName, bool separator = false, string? icon = null, string? shortcut = null, int priority = 0)
    {
        this.menuItem = itemName;
        this.separator = separator;
        this.icon = icon;
        this.shortcut = shortcut;
        this.priority = priority;
    }
}
