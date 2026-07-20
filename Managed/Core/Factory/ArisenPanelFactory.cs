using System;
using System.Collections.Generic;
using ArisenEngine.Core.Lifecycle;
using ArisenEditorFramework.Core;
using ArisenEditorFramework.Hierarchy;
using ArisenEditorFramework.Inspector;
using ArisenEditor.Core.Services;
using ArisenEditor.Core.Views;
using ArisenEditor.ViewModels;
using ArisenEditor.Views;
using ReactiveUI;

namespace ArisenEditor.Core.Factory;

public class ArisenPanelFactory : DefaultPanelFactory
{
    private readonly SelectionService _selectionService = new();
    public ISelectionService SelectionService => _selectionService;
    private readonly Dictionary<string, IEditorPanel> _panelCache = new();

    public void Initialize()
    {
        RegisterPanel("Hierarchy", () =>
        {
            var hierarchy = new ArisenEditor.ViewModels.HierarchyViewModel();
            hierarchy.SelectionService = _selectionService;
            return hierarchy;
        });
        RegisterPanel("Inspector", () =>
        {
            var inspector = new ArisenEditor.ViewModels.InspectorViewModel();
            inspector.SelectionService = _selectionService;
            // Connect selection for this specific inspector instance
            _selectionService.SelectionChanged += (obj) => inspector.TargetObject = obj;
            return inspector;
        });
        
        RegisterPanel("Scene", () => new SceneViewModel(_selectionService));
        RegisterPanel("GameView", () => new GameViewModel());
        RegisterPanel("Console", () => new ConsoleViewModel());
        RegisterPanel("Assets", () =>
        {
            var assets = new AssetsBrowserViewModel
            {
                SelectionService = _selectionService
            };
            return assets;
        });
        RegisterPanel("PackageManager", () => new PackageManagerViewModel());
        RegisterPanel("ProjectSettings", () => new ProjectSettingsViewModel());

        RegisterPanel("Viewport", () => new EditorPanelWrapper("Viewport", "Viewport", new Avalonia.Controls.TextBlock { Text = "Viewport Placeholder", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }));
        RegisterPanel("IconPreview", () => new IconPreviewViewModel());
        RegisterPanel("Header", () => new HeaderViewModel());
        RegisterPanel("Toolbar", () => new ToolbarViewModel());
        RegisterPanel("Footer", () => new FooterViewModel());
    }

    public override IEditorPanel CreatePanel(string panelId)
    {
        if (_panelCache.TryGetValue(panelId, out var cachedPanel))
        {
            return cachedPanel;
        }

        var panel = base.CreatePanel(panelId);
        _panelCache[panelId] = panel;
        return panel;
    }
}

internal class EditorPanelWrapper : EditorPanelBase
{
    public override string Title { get; }
    public override string Id { get; }
    public override object Content { get; }

    public EditorPanelWrapper(string id, string title, object content)
    {
        Id = id;
        Title = title;
        Content = content;
    }
}
