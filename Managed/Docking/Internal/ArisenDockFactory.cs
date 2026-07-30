using System;
using System.Collections.Generic;
using System.Linq;
using ArisenEditorFramework.Core;
using ArisenEditorFramework.Extensions;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Dock.Model.Mvvm.Core;
using Dock.Model.Mvvm;
using Avalonia.Collections;

namespace ArisenEditorFramework.Docking.Internal;

/// <summary>
/// Internal factory used by Ava.Dock to build the layout tree.
/// </summary>
internal class ArisenDockFactory : Factory
{
    private IRootDock? _rootDock;
    private readonly LayoutManager _layoutManager;
    private readonly EditorPanelRegistration[] _extensionPanels;
    private IPanelFactory? _panelFactory;

    public IPanelFactory? PanelFactory
    {
        get => _panelFactory;
        set => _panelFactory = value;
    }

    public ArisenDockFactory(
        LayoutManager layoutManager,
        IReadOnlyList<EditorPanelRegistration>? extensionPanels = null)
    {
        _layoutManager = layoutManager;
        _extensionPanels = extensionPanels?.ToArray() ?? Array.Empty<EditorPanelRegistration>();
    }

    public IRootDock CreateLayout(string preset = "Default")
    {
        var toolbar = new ToolDocument { Id = "Toolbar", Title = "Toolbar", CanFloat = false, CanClose = false, CanPin = false };
        var hierarchy = new ToolDocument { Id = "Hierarchy", Title = "Hierarchy" };
        var inspector = new ToolDocument { Id = "Inspector", Title = "Inspector" };
        var console = new ToolDocument { Id = "Console", Title = "Console" };
        var assets = new ToolDocument { Id = "Assets", Title = "Assets" };
        var worldPartition = new ToolDocument { Id = "WorldPartition", Title = "World Partition" };
        var iconPreview = new ToolDocument { Id = "IconPreview", Title = "Icon Preview" };
        var scene = new ToolDocument { Id = "Scene", Title = "Scene" };
        var gameView = new ToolDocument { Id = "GameView", Title = "Game" };
        var projectSettings = new ToolDocument
        {
            Id = "ProjectSettings",
            Title = "Project Settings",
            CanClose = false
        };

        IDockable content;

        if (preset == "Wide")
        {
            content = new ProportionalDock
            {
                Id = "WindowLayout",
                Orientation = Orientation.Vertical,
                IsCollapsable = false,
                VisibleDockables = CreateList<IDockable>
                (
                    new ToolDock { Id = "ToolbarPane", Proportion = 0.05, ActiveDockable = toolbar, VisibleDockables = CreateList<IDockable>(toolbar) },
                    new ProportionalDockSplitter(),
                    new ProportionalDock
                    {
                        Orientation = Orientation.Horizontal,
                        IsCollapsable = false,
                        VisibleDockables = CreateList<IDockable>
                        (
                            new ToolDock { Id = "LeftPane", Proportion = 0.15, ActiveDockable = hierarchy, VisibleDockables = CreatePaneDockables(EditorDockRegion.Left, hierarchy) },
                            new ProportionalDockSplitter(),
                            new ToolDock { Id = "CenterPane", Proportion = 0.6, ActiveDockable = scene, VisibleDockables = CreatePaneDockables(EditorDockRegion.Center, scene, gameView, projectSettings) },
                            new ProportionalDockSplitter(),
                            new ToolDock { Id = "BottomPane", Proportion = 0.2, ActiveDockable = worldPartition, VisibleDockables = CreatePaneDockables(EditorDockRegion.Bottom, worldPartition, console, assets) },
                            new ProportionalDockSplitter(),
                            new ToolDock { Id = "RightPane", Proportion = 0.15, ActiveDockable = inspector, VisibleDockables = CreatePaneDockables(EditorDockRegion.Right, inspector) }
                        )
                    }
                )
            };
        }
        else if (preset == "Tall")
        {
            content = new ProportionalDock
            {
                Id = "WindowLayout",
                Orientation = Orientation.Vertical,
                IsCollapsable = false,
                VisibleDockables = CreateList<IDockable>
                (
                    new ToolDock { Id = "ToolbarPane", Proportion = 0.05, ActiveDockable = toolbar, VisibleDockables = CreateList<IDockable>(toolbar) },
                    new ProportionalDockSplitter(),
                    new ToolDock { Id = "CenterPane", Proportion = 0.6, ActiveDockable = scene, VisibleDockables = CreatePaneDockables(EditorDockRegion.Center, scene, gameView, projectSettings) },
                    new ProportionalDockSplitter(),
                    new ProportionalDock
                    {
                        Orientation = Orientation.Horizontal,
                        Proportion = 0.55,
                        IsCollapsable = false,
                        VisibleDockables = CreateList<IDockable>
                        (
                            new ToolDock { Id = "LeftPane", Proportion = 0.3, ActiveDockable = hierarchy, VisibleDockables = CreatePaneDockables(EditorDockRegion.Left, hierarchy) },
                            new ProportionalDockSplitter(),
                            new ToolDock { Id = "BottomPane", Proportion = 0.4, ActiveDockable = worldPartition, VisibleDockables = CreatePaneDockables(EditorDockRegion.Bottom, worldPartition, console, assets) },
                            new ProportionalDockSplitter(),
                            new ToolDock { Id = "RightPane", Proportion = 0.3, ActiveDockable = inspector, VisibleDockables = CreatePaneDockables(EditorDockRegion.Right, inspector) }
                        )
                    }
                )
            };
        }
        else // Default
        {
            var header = new ToolDocument { Id = "Header", Title = "Header", CanFloat = false, CanClose = false, CanPin = false };
            var footer = new ToolDocument { Id = "Footer", Title = "Footer", CanFloat = false, CanClose = false, CanPin = false };

            var mainLayout = new ProportionalDock
            {
                Id = "MainLayout",
                Proportion = 0.8,
                Orientation = Orientation.Horizontal,
                IsCollapsable = false,
                VisibleDockables = CreateList<IDockable>
                (
                    new ToolDock { Id = "LeftPane", Proportion = 0.2, ActiveDockable = hierarchy, VisibleDockables = CreatePaneDockables(EditorDockRegion.Left, hierarchy) },
                    new ProportionalDockSplitter(),
                    new ToolDock { Id = "CenterPane", Proportion = 0.6, ActiveDockable = scene, VisibleDockables = CreatePaneDockables(EditorDockRegion.Center, scene, gameView, projectSettings) },
                    new ProportionalDockSplitter(),
                    new ToolDock { Id = "RightPane", Proportion = 0.2, ActiveDockable = inspector, VisibleDockables = CreatePaneDockables(EditorDockRegion.Right, inspector) }
                )
            };

            content = new ProportionalDock
            {
                Id = "WindowLayout",
                Orientation = Orientation.Vertical,
                IsCollapsable = false,
                VisibleDockables = CreateList<IDockable>
                (
                    new ToolDock { Id = "HeaderPane", Proportion = 0.08, GripMode = GripMode.Hidden, ActiveDockable = header, VisibleDockables = CreateList<IDockable>(header) },
                    new ToolDock { Id = "ToolbarPane", Proportion = 0.08, GripMode = GripMode.Hidden, ActiveDockable = toolbar, VisibleDockables = CreateList<IDockable>(toolbar) },
                    mainLayout,
                    new ProportionalDockSplitter(),
                    new ToolDock { Id = "BottomPane", Proportion = 0.17, ActiveDockable = worldPartition, VisibleDockables = CreatePaneDockables(EditorDockRegion.Bottom, worldPartition, console, assets, iconPreview) },
                    new ToolDock { Id = "FooterPane", Proportion = 0.03, GripMode = GripMode.Hidden, ActiveDockable = footer, VisibleDockables = CreateList<IDockable>(footer) }
                )
            };
        }

        var rootDock = CreateRootDock();
        rootDock.Id = "RootDock";
        rootDock.ActiveDockable = content;
        rootDock.DefaultDockable = content;
        rootDock.VisibleDockables = CreateList<IDockable>(content);
        rootDock.IsCollapsable = false;

        _rootDock = rootDock;
        
        return rootDock;
    }

    public override IRootDock CreateLayout() => CreateLayout("Default");

    private IList<IDockable> CreatePaneDockables(
        EditorDockRegion region,
        params IDockable[] builtInDockables)
    {
        var dockables = new List<IDockable>(builtInDockables.Length + _extensionPanels.Length);
        dockables.AddRange(builtInDockables);
        foreach (var panel in _extensionPanels)
        {
            if (panel.DockRegion != region)
            {
                continue;
            }

            dockables.Add(new ToolDocument
            {
                Id = panel.Id,
                Title = panel.Title
            });
        }

        return CreateList<IDockable>(dockables.ToArray());
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>();

        if (_panelFactory != null)
        {
            foreach (var id in _panelFactory.GetAvailablePanelIds())
            {
                // We map the ID to the Content of the IEditorPanel.
                // Note: In a pure MVVM setup, this might be the ViewModel, but here AEF defines 
                // IEditorPanel where .Content is typically the View or a Root ViewModel.
                ContextLocator[id] = () => _panelFactory.CreatePanel(id);
            }
        }

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow()
        };

        base.InitLayout(layout);
    }
}

internal class ToolDocument : Tool
{
    public ToolDocument()
    {
        CanFloat = true;
        CanClose = true;
        CanPin = true;
    }
}
