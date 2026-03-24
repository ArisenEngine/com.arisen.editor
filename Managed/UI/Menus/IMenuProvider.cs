using System.Collections.Generic;

namespace ArisenEditorFramework.UI.Menus;

/// <summary>
/// Interface for objects that provide menu items for a specific menu identifier and context.
/// </summary>
public interface IMenuProvider
{
    /// <summary>
    /// Returns a list of menu actions to be added to the specified menu.
    /// </summary>
    /// <param name="menuId">The unique identifier for the menu (e.g., "Hierarchy.ContextMenu").</param>
    /// <param name="context">The contextual object (e.g., the selected Entity or Asset).</param>
    IEnumerable<MenuAction> GetMenuItems(string menuId, object? context);
}
