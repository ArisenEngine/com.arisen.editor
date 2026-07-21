using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ArisenEditor.Core.Services;
using ArisenEditorFramework.Core;
using ArisenEngine.Resources.Serialization;
using ReactiveUI;

namespace ArisenEditor.ViewModels;

public sealed class SceneNodeViewModel : ReactiveObject
{
    private string m_Name;
    private string m_Status = string.Empty;
    private readonly Action<bool>? m_OnExpandedChanged;

    public string StableId { get; }

    public string Status
    {
        get => m_Status;
        set => this.RaiseAndSetIfChanged(ref m_Status, value);
    }

    public string Name
    {
        get => m_Name;
        set => this.RaiseAndSetIfChanged(ref m_Name, value);
    }

    private bool m_IsExpanded = true;

    public bool IsExpanded
    {
        get => m_IsExpanded;
        set
        {
            if (m_IsExpanded == value) return;
            this.RaiseAndSetIfChanged(ref m_IsExpanded, value);
            m_OnExpandedChanged?.Invoke(value);
        }
    }

    public ObservableCollection<ReactiveObject> Entities { get; } = new();

    public SceneNodeViewModel(
        string name,
        string stableId = "",
        bool isExpanded = true,
        Action<bool>? onExpandedChanged = null)
    {
        m_Name = string.IsNullOrWhiteSpace(name) ? "Unnamed Scene" : name;
        StableId = stableId;
        m_IsExpanded = isExpanded;
        m_OnExpandedChanged = onExpandedChanged;
    }
}

public sealed class SceneAssetEntityNodeViewModel : ReactiveObject
{
    public SceneInspectionResult Scene { get; private set; }
    public Guid AuthoringGuid => Entity.AuthoringGuid;
    public Guid ParentGuid => Entity.ParentGuid;
    public Guid SceneGuid { get; }
    public string SourcePath => Scene.SourcePath;
    public WorldCellId CellId { get; }
    public bool IsWorldScene { get; }

    private string m_ComponentSummary;

    public string ComponentSummary
    {
        get => m_ComponentSummary;
        private set => this.RaiseAndSetIfChanged(ref m_ComponentSummary, value);
    }

    private SceneEntityInspection m_Entity;

    public SceneEntityInspection Entity
    {
        get => m_Entity;
        private set => this.RaiseAndSetIfChanged(ref m_Entity, value);
    }

    private string m_Name;

    public string Name
    {
        get => m_Name;
        private set => this.RaiseAndSetIfChanged(ref m_Name, value);
    }

    private bool m_IsExpanded = true;

    public bool IsExpanded
    {
        get => m_IsExpanded;
        set => this.RaiseAndSetIfChanged(ref m_IsExpanded, value);
    }

    public ObservableCollection<SceneAssetEntityNodeViewModel> Children { get; } = new();

    public SceneAssetEntityNodeViewModel(
        SceneInspectionResult scene,
        SceneEntityInspection entity,
        WorldCellId cellId = default,
        Guid sceneGuid = default,
        bool isWorldScene = false)
    {
        Scene = scene;
        CellId = cellId;
        SceneGuid = sceneGuid;
        IsWorldScene = isWorldScene;
        m_Entity = entity;
        m_Name = ResolveName(entity);
        m_ComponentSummary = BuildComponentSummary(entity);
    }

    public void UpdateInspection(SceneInspectionResult scene, SceneEntityInspection entity)
    {
        Scene = scene;
        Entity = entity;
        RaiseTransformPropertiesChanged();
        Name = ResolveName(entity);
        ComponentSummary = BuildComponentSummary(entity);
    }

    public void SetTransform(SceneTransformInspection transform)
    {
        Entity = Entity with { Transform = transform };
        RaiseTransformPropertiesChanged();
    }

    private void RaiseTransformPropertiesChanged()
    {
        this.RaisePropertyChanged("Position");
        this.RaisePropertyChanged("Rotation");
        this.RaisePropertyChanged("Scale");
    }

    private static string ResolveName(SceneEntityInspection entity)
    {
        return string.IsNullOrWhiteSpace(entity.Name)
            ? $"Entity {entity.AuthoringGuid:D}"
            : entity.Name;
    }

    private static string BuildComponentSummary(SceneEntityInspection entity)
    {
        var components = new List<string> { "Transform" };
        if (entity.Camera != null) components.Add("Camera");
        if (entity.MeshRenderer != null) components.Add("MeshRenderer");
        if (entity.DirectionalLight != null) components.Add("DirectionalLight");
        if (entity.PointLight != null) components.Add("PointLight");
        if (entity.SpotLight != null) components.Add("SpotLight");
        if (entity.Environment != null) components.Add("Environment");
        return string.Join(", ", components);
    }
}

internal sealed class HierarchyViewModel : EditorPanelBase
{
    private readonly ObservableCollection<SceneAssetEntityNodeViewModel> m_AllEntities = new();
    private readonly CompositeDisposable m_Disposables = new();
    private readonly IEditorSceneDocumentService? m_DocumentService;
    private readonly IEditorWorldDocumentService? m_WorldDocumentService;
    private EditorSceneDocumentState? m_CurrentDocument;
    private EditorWorldDocumentState? m_CurrentWorldDocument;
    private SelectionService? m_SelectionService;
    private bool m_IsApplyingSelection;

    private string m_SearchText = string.Empty;

    public string SearchText
    {
        get => m_SearchText;
        set
        {
            this.RaiseAndSetIfChanged(ref m_SearchText, value);
            RefreshVisibleTree();
        }
    }

    public SelectionService? SelectionService
    {
        get => m_SelectionService;
        set => m_SelectionService = value;
    }

    public override string Title => "Hierarchy";
    public override string Id => "Hierarchy";
    public override object Content => new Views.HierarchyView { DataContext = this };

    private ObservableCollection<SceneNodeViewModel> m_RootNodes = new();

    public ObservableCollection<SceneNodeViewModel> RootNodes
    {
        get => m_RootNodes;
        private set => this.RaiseAndSetIfChanged(ref m_RootNodes, value);
    }

    private ReactiveObject? m_SelectedItem;

    public ReactiveObject? SelectedItem
    {
        get => m_SelectedItem;
        set => this.RaiseAndSetIfChanged(ref m_SelectedItem, value);
    }

    internal HierarchyViewModel()
    {
        this.WhenAnyValue(x => x.SelectedItem)
            .Subscribe(item =>
            {
                if (!m_IsApplyingSelection && m_SelectionService != null)
                {
                    m_SelectionService.CurrentSelection = item;
                }
                if (!m_IsApplyingSelection && item is SceneAssetEntityNodeViewModel entity &&
                    m_WorldDocumentService != null && m_CurrentWorldDocument != null)
                {
                    m_WorldDocumentService.SetStableSelection(new EditorWorldSelectionId(
                        entity.SceneGuid,
                        entity.CellId,
                        entity.AuthoringGuid));
                }
            })
            .DisposeWith(m_Disposables);

        var services = ArisenKernel.Lifecycle.EngineKernel.Instance.Services;
        if (services.TryGetService<IEditorSceneDocumentService>(out var documentService) &&
            documentService != null)
        {
            m_DocumentService = documentService;
            m_DocumentService.StateChanged += OnDocumentStateChanged;
            ShowDocument(m_DocumentService.Current);
        }
        if (services.TryGetService<IEditorWorldDocumentService>(out var worldDocumentService) &&
            worldDocumentService != null)
        {
            m_WorldDocumentService = worldDocumentService;
            m_WorldDocumentService.StateChanged += OnWorldDocumentStateChanged;
            ShowWorldDocument(m_WorldDocumentService.Current);
        }
    }

    private void OnDocumentStateChanged(EditorSceneDocumentState? state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (m_CurrentWorldDocument == null) ShowDocument(state);
        });
    }

    private void OnWorldDocumentStateChanged(EditorWorldDocumentState? state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowWorldDocument(state));
    }

    private void ShowDocument(EditorSceneDocumentState? state)
    {
        if (m_CurrentWorldDocument != null) return;
        bool preserveState = IsSameDocument(m_CurrentDocument, state);
        var selectedEntity = m_SelectionService != null
            ? m_SelectionService.CurrentSelection as SceneAssetEntityNodeViewModel
            : SelectedItem as SceneAssetEntityNodeViewModel;
        Guid selectedEntityGuid = preserveState && selectedEntity != null
            ? selectedEntity.AuthoringGuid
            : Guid.Empty;
        bool rootExpanded = preserveState && RootNodes.Count == 1
            ? RootNodes[0].IsExpanded
            : true;
        var reusableNodes = preserveState
            ? m_AllEntities.ToDictionary(node => node.AuthoringGuid)
            : null;

        m_CurrentDocument = state;
        if (state == null)
        {
            m_AllEntities.Clear();
            RootNodes.Clear();
            ClearSceneSelection();
            return;
        }

        var refreshedNodes = new List<SceneAssetEntityNodeViewModel>(state.Inspection.Entities.Count);
        for (int i = 0; i < state.Inspection.Entities.Count; i++)
        {
            SceneEntityInspection entity = state.Inspection.Entities[i];
            SceneAssetEntityNodeViewModel node;
            if (reusableNodes != null &&
                reusableNodes.TryGetValue(entity.AuthoringGuid, out var reusableNode))
            {
                node = reusableNode;
                node.UpdateInspection(state.Inspection, entity);
            }
            else
            {
                node = new SceneAssetEntityNodeViewModel(
                    state.Inspection,
                    entity,
                    default,
                    state.Scene.Guid);
            }

            node.Children.Clear();
            refreshedNodes.Add(node);
        }

        var nodesByGuid = refreshedNodes.ToDictionary(node => node.AuthoringGuid);
        for (int i = 0; i < refreshedNodes.Count; i++)
        {
            var node = refreshedNodes[i];
            if (node.ParentGuid != Guid.Empty &&
                nodesByGuid.TryGetValue(node.ParentGuid, out var parent))
            {
                parent.Children.Add(node);
            }
        }

        SynchronizeByIdentity(m_AllEntities, refreshedNodes);
        ApplyFilter(rootExpanded, preserveState);

        if (preserveState &&
            selectedEntityGuid != Guid.Empty &&
            !nodesByGuid.ContainsKey(selectedEntityGuid))
        {
            ClearSceneSelection();
        }
        else if (!preserveState && selectedEntity != null)
        {
            ClearSceneSelection();
        }
    }

    private void ShowWorldDocument(EditorWorldDocumentState? state)
    {
        m_CurrentWorldDocument = state;
        if (state == null)
        {
            if (m_DocumentService?.Current != null)
            {
                ShowDocument(m_DocumentService.Current);
            }
            return;
        }

        var reusableEntities = m_AllEntities.ToDictionary(
            node => (node.SceneGuid, node.CellId, node.AuthoringGuid));
        var reusableGroups = EnumerateSceneNodes(RootNodes)
            .Where(node => !string.IsNullOrWhiteSpace(node.StableId))
            .ToDictionary(node => node.StableId, StringComparer.Ordinal);
        var refreshedEntities = new List<SceneAssetEntityNodeViewModel>();
        var groups = new List<ReactiveObject>(state.Cells.Count + 1);

        groups.Add(BuildWorldSceneGroup(
            state,
            state.PersistentScene,
            default,
            "Persistent",
            "persistent",
            reusableEntities,
            reusableGroups,
            refreshedEntities));
        foreach (EditorWorldCellDocumentState cell in state.Cells)
        {
            string status = BuildCellStatus(cell);
            string title = $"Cell {cell.Descriptor.Key.Coordinate.X}, {cell.Descriptor.Key.Coordinate.Y}, {cell.Descriptor.Key.Coordinate.Z}";
            groups.Add(BuildWorldSceneGroup(
                state,
                cell.SceneDocument,
                cell.CellId,
                title,
                status,
                reusableEntities,
                reusableGroups,
                refreshedEntities));
        }

        string rootId = $"world:{state.World.Guid:D}";
        SceneNodeViewModel worldNode = reusableGroups.TryGetValue(rootId, out SceneNodeViewModel? existingRoot)
            ? existingRoot
            : CreateGroupNode(state, rootId, state.Name);
        worldNode.Name = state.Name + (state.IsDirty ? " *" : string.Empty);
        worldNode.Status = $"{state.Metrics.ActiveCells}/{state.Cells.Count} active";
        SynchronizeByIdentity(worldNode.Entities, groups);
        SynchronizeByIdentity(m_AllEntities, refreshedEntities);
        if (RootNodes.Count != 1 || !ReferenceEquals(RootNodes[0], worldNode))
        {
            RootNodes = new ObservableCollection<SceneNodeViewModel> { worldNode };
        }

        SceneAssetEntityNodeViewModel? selected = state.Selection is { } selection
            ? refreshedEntities.FirstOrDefault(node =>
                node.SceneGuid == selection.SceneGuid &&
                node.CellId == selection.CellId &&
                node.AuthoringGuid == selection.EntityGuid)
            : null;
        m_IsApplyingSelection = true;
        try
        {
            SelectedItem = selected;
            if (m_SelectionService != null) m_SelectionService.CurrentSelection = selected;
        }
        finally
        {
            m_IsApplyingSelection = false;
        }
    }

    private SceneNodeViewModel BuildWorldSceneGroup(
        EditorWorldDocumentState world,
        EditorWorldSceneDocumentState document,
        WorldCellId cellId,
        string title,
        string status,
        IReadOnlyDictionary<(Guid SceneGuid, WorldCellId CellId, Guid EntityGuid), SceneAssetEntityNodeViewModel> reusable,
        IReadOnlyDictionary<string, SceneNodeViewModel> reusableGroups,
        ICollection<SceneAssetEntityNodeViewModel> refreshed)
    {
        string stableId = cellId.IsValid ? $"cell:{cellId}" : $"persistent:{document.Scene.Guid:D}";
        SceneNodeViewModel group = reusableGroups.TryGetValue(stableId, out SceneNodeViewModel? existing)
            ? existing
            : CreateGroupNode(world, stableId, title);
        group.Name = title + (document.IsDirty ? " *" : string.Empty);
        group.Status = status;

        var nodes = new List<SceneAssetEntityNodeViewModel>(document.Inspection.Entities.Count);
        foreach (SceneEntityInspection entity in document.Inspection.Entities)
        {
            var key = (document.Scene.Guid, cellId, entity.AuthoringGuid);
            SceneAssetEntityNodeViewModel node;
            if (reusable.TryGetValue(key, out SceneAssetEntityNodeViewModel? old))
            {
                node = old;
                node.UpdateInspection(document.Inspection, entity);
            }
            else
            {
                node = new SceneAssetEntityNodeViewModel(
                    document.Inspection,
                    entity,
                    cellId,
                    document.Scene.Guid,
                    isWorldScene: true);
            }
            node.Children.Clear();
            nodes.Add(node);
            refreshed.Add(node);
        }

        var byGuid = nodes.ToDictionary(node => node.AuthoringGuid);
        foreach (SceneAssetEntityNodeViewModel node in nodes)
        {
            if (node.ParentGuid != Guid.Empty && byGuid.TryGetValue(node.ParentGuid, out var parent))
            {
                parent.Children.Add(node);
            }
        }
        IReadOnlyList<ReactiveObject> roots = nodes
            .Where(node => node.ParentGuid == Guid.Empty)
            .Where(node => string.IsNullOrWhiteSpace(m_SearchText) ||
                           node.Name.Contains(m_SearchText, StringComparison.OrdinalIgnoreCase) ||
                           DescendantMatches(node, m_SearchText))
            .Cast<ReactiveObject>()
            .ToArray();
        SynchronizeByIdentity(group.Entities, roots);
        return group;
    }

    private SceneNodeViewModel CreateGroupNode(
        EditorWorldDocumentState world,
        string stableId,
        string title)
    {
        bool expanded = world.ExpandedNodeIds.Contains(stableId) ||
                        stableId.StartsWith("world:", StringComparison.Ordinal);
        return new SceneNodeViewModel(
            title,
            stableId,
            expanded,
            value => m_WorldDocumentService?.SetExpanded(stableId, value));
    }

    private static bool DescendantMatches(SceneAssetEntityNodeViewModel node, string search)
    {
        foreach (SceneAssetEntityNodeViewModel child in node.Children)
        {
            if (child.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                DescendantMatches(child, search)) return true;
        }
        return false;
    }

    private static string BuildCellStatus(EditorWorldCellDocumentState cell)
    {
        string owner = cell.IsEditPinned ? "edit pin" : cell.Streaming.Desired ? "game" : string.Empty;
        return string.IsNullOrEmpty(owner)
            ? cell.Streaming.State.ToString()
            : $"{cell.Streaming.State} | {owner}";
    }

    private static IEnumerable<SceneNodeViewModel> EnumerateSceneNodes(
        IEnumerable<SceneNodeViewModel> roots)
    {
        var pending = new Stack<SceneNodeViewModel>(roots.Reverse());
        while (pending.Count > 0)
        {
            SceneNodeViewModel node = pending.Pop();
            yield return node;
            foreach (SceneNodeViewModel child in node.Entities.OfType<SceneNodeViewModel>().Reverse())
            {
                pending.Push(child);
            }
        }
    }

    private void RefreshVisibleTree()
    {
        if (m_CurrentWorldDocument != null) ShowWorldDocument(m_CurrentWorldDocument);
        else ApplyFilter();
    }

    private void ApplyFilter(bool? rootExpanded = null, bool preserveRootIdentity = true)
    {
        if (m_CurrentDocument == null)
        {
            RootNodes.Clear();
            return;
        }

        var sceneNode = preserveRootIdentity && RootNodes.Count == 1
            ? RootNodes[0]
            : new SceneNodeViewModel(FormatSceneName(m_CurrentDocument));
        sceneNode.Name = FormatSceneName(m_CurrentDocument);
        sceneNode.IsExpanded = rootExpanded ?? sceneNode.IsExpanded;

        var visibleEntities = string.IsNullOrWhiteSpace(m_SearchText)
            ? m_AllEntities
                .Where(entity => entity.ParentGuid == Guid.Empty)
                .Cast<ReactiveObject>()
                .ToList()
            : m_AllEntities
                .Where(entity => entity.Name.Contains(m_SearchText, StringComparison.OrdinalIgnoreCase))
                .Cast<ReactiveObject>()
                .ToList();
        if (!string.IsNullOrWhiteSpace(m_SearchText))
        {
            sceneNode.IsExpanded = true;
        }

        SynchronizeByIdentity(sceneNode.Entities, visibleEntities);
        if (RootNodes.Count != 1 || !ReferenceEquals(RootNodes[0], sceneNode))
        {
            RootNodes = new ObservableCollection<SceneNodeViewModel> { sceneNode };
        }
    }

    private void ClearSceneSelection()
    {
        if (SelectedItem is SceneAssetEntityNodeViewModel)
        {
            m_IsApplyingSelection = true;
            try
            {
                SelectedItem = null;
            }
            finally
            {
                m_IsApplyingSelection = false;
            }
        }

        if (m_SelectionService?.CurrentSelection is SceneAssetEntityNodeViewModel)
        {
            m_SelectionService.CurrentSelection = null;
        }
    }

    internal void OnUnloaded()
    {
        if (m_DocumentService != null)
        {
            m_DocumentService.StateChanged -= OnDocumentStateChanged;
        }
        if (m_WorldDocumentService != null)
        {
            m_WorldDocumentService.StateChanged -= OnWorldDocumentStateChanged;
        }

        m_Disposables.Dispose();
    }

    private static string FormatSceneName(EditorSceneDocumentState state)
    {
        string dirty = state.IsDirty ? "*" : string.Empty;
        string conflict = state.HasExternalChanges ? " [external change]" : string.Empty;
        return $"{state.Name}{dirty}{conflict}";
    }

    private static bool IsSameDocument(
        EditorSceneDocumentState? left,
        EditorSceneDocumentState? right)
    {
        return left != null &&
               right != null &&
               left.Scene.Guid == right.Scene.Guid &&
               string.Equals(left.Scene.PackageId, right.Scene.PackageId, StringComparison.OrdinalIgnoreCase);
    }

    private static void SynchronizeByIdentity<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> desired)
        where T : class
    {
        if (target.Count == desired.Count)
        {
            bool matches = true;
            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(target[i], desired[i]))
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return;
            }
        }

        target.Clear();
        for (int i = 0; i < desired.Count; i++)
        {
            target.Add(desired[i]);
        }
    }
}
