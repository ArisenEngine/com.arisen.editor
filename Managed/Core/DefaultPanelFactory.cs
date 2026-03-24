using System;
using System.Collections.Generic;
using System.Linq;

namespace ArisenEditorFramework.Core;

public class DefaultPanelFactory : IPanelFactory
{
    private readonly Dictionary<string, Func<IEditorPanel>> _registry = new();

    public void RegisterPanel(string id, Func<IEditorPanel> factory)
    {
        _registry[id] = factory;
    }

    public virtual IEditorPanel CreatePanel(string panelId)
    {
        if (_registry.TryGetValue(panelId, out var factory))
        {
            return factory();
        }
        throw new ArgumentException($"Panel with ID '{panelId}' is not registered.");
    }

    public IEnumerable<string> GetAvailablePanelIds() => _registry.Keys;
}
