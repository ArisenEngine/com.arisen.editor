using System.Collections.Generic;
using System.Linq;
using ArisenEditorFramework.Docking.Internal;
using Avalonia.Collections;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Serializer;
using ArisenEditorFramework.Core;

namespace ArisenEditorFramework.Docking;

public class LayoutManager : IEditorLayoutService
{
    private readonly ArisenDockFactory _factory;
    private IRootDock? _layout;
    private readonly Dictionary<string, IEditorWindow> _customWindows = new();

    public event System.Action<IRootDock>? LayoutRefresh;

    public IFactory Factory => _factory;
    public IRootDock? Layout => _layout;

    public IPanelFactory? PanelFactory
    {
        get => _factory.PanelFactory;
        set => _factory.PanelFactory = value;
    }

    public LayoutManager()
    {
        _factory = new ArisenDockFactory(this);
    }

    public void Initialize()
    {
        _layout = _factory.CreateLayout();
        if (_layout != null)
        {
            _factory.InitLayout(_layout);
        }
    }

    public void ApplyPreset(string preset)
    {
        var newLayout = _factory.CreateLayout(preset);
        if (newLayout != null)
        {
            _layout = newLayout;
            _factory.InitLayout(_layout);
            
            // Re-bind all existing windows to the new layout structure
            RestoreCustomWindows(_customWindows.Values.ToList(), new Dictionary<string, string>());

            // Notify UI to refresh
            LayoutRefresh?.Invoke(_layout);
        }
    }

    public void OpenWindow(IEditorWindow window)
    {
        _customWindows[window.Id] = window;
        
        // Find any tool dock or root to add
        var toolDock = _layout != null ? _factory.FindDockable(_layout, v => v is IToolDock) as IToolDock : null;
        if (toolDock != null)
        {
            var tool = new EditorWindowTool(window)
            {
                Id = window.Id,
                Title = window.Title
            };
            
            _factory.AddDockable(toolDock, tool);
            _factory.SetActiveDockable(tool);
            _factory.SetFocusedDockable(toolDock, tool);
        }
    }

    public void CloseWindow(IEditorWindow window)
    {
        if (_customWindows.Remove(window.Id))
        {
            var dockable = _layout != null ? _factory.FindDockable(_layout, v => v.Id == window.Id) : null;
            if (dockable != null && dockable.Owner is IDock parentDock)
            {
                _factory.RemoveDockable(dockable, true);
            }
        }
    }

    public string SaveLayout()
    {
        if (_layout == null) return string.Empty;
        var serializer = new DockSerializer(typeof(AvaloniaList<>));
        return serializer.Serialize(_layout);
    }

    public void LoadLayout(string layoutData)
    {
        if (string.IsNullOrEmpty(layoutData)) return;
        
        var serializer = new DockSerializer(typeof(AvaloniaList<>));
        var newLayout = serializer.Deserialize<IRootDock>(layoutData);
        if (newLayout != null)
        {
            _layout = newLayout;
            _factory.InitLayout(_layout);
            
            // Re-bind existing custom windows to the new layout structure
            RestoreCustomWindows(_customWindows.Values.ToList(), new Dictionary<string, string>());
            
            // Notify UI to refresh
            LayoutRefresh?.Invoke(_layout);
        }
    }
    
    public Dictionary<string, string> SerializeCustomWindows()
    {
        return _customWindows.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.SerializeState());
    }

    public void RestoreCustomWindows(IEnumerable<IEditorWindow> newInstances, Dictionary<string, string> stateData)
    {
        _customWindows.Clear();
        foreach (var window in newInstances)
        {
            if (stateData.TryGetValue(window.Id, out var state))
            {
                window.DeserializeState(state);
            }
            _customWindows[window.Id] = window;
            
            // Re-bind to existing Ava.Dock view models built from loaded JSON
            var existingView = _layout != null ? _factory.FindDockable(_layout, v => v is EditorWindowTool d && d.Id == window.Id) as EditorWindowTool : null;
            if (existingView != null)
            {
                existingView.SetWindow(window);
            }
        }
    }

    public IEditorWindow? GetWindow(string id)
    {
        return _customWindows.TryGetValue(id, out var window) ? window : null;
    }
}

public class EditorWindowTool : Tool
{
    private IEditorWindow? _window;
    public object? WindowContent => _window?.GetContent();

    public EditorWindowTool(IEditorWindow window)
    {
        _window = window;
        CanFloat = true;
        CanClose = true;
        CanPin = true;
    }
    
    // Parameterless constructor needed for deserialization
    public EditorWindowTool() 
    {
        CanFloat = true;
        CanClose = true;
        CanPin = true;
    }
    
    public void SetWindow(IEditorWindow window)
    {
        _window = window;
    }
}
