using System;
using System.Collections.Generic;
using System.Linq;
using ArisenEditorFramework.UI.Menus;

namespace ArisenEditorFramework.Services;

/// <summary>
/// A registry for menu providers, allowing dynamic construction of menus.
/// </summary>
public class MenuRegistry
{
    private static readonly Lazy<MenuRegistry> _instance = new(() => new MenuRegistry());
    public static MenuRegistry Instance => _instance.Value;

    private readonly List<IMenuProvider> _providers = new();

    public void RegisterProvider(IMenuProvider provider)
    {
        if (!_providers.Contains(provider))
        {
            _providers.Add(provider);
        }
    }

    public void UnregisterProvider(IMenuProvider provider)
    {
        _providers.Remove(provider);
    }

    /// <summary>
    /// Builds a list of menu actions by querying all registered providers.
    /// </summary>
    public List<MenuAction> BuildMenu(string menuId, object? context = null)
    {
        var allItems = new List<MenuAction>();
        foreach (var provider in _providers)
        {
            try
            {
                var items = provider.GetMenuItems(menuId, context);
                if (items != null)
                {
                    allItems.AddRange(items);
                }
            }
            catch (Exception ex)
            {
                // In a real engine, we'd log this properly.
                System.Diagnostics.Debug.WriteLine($"[MenuRegistry] Provider {provider.GetType().Name} failed for {menuId}: {ex.Message}");
            }
        }
        return allItems;
    }
}
