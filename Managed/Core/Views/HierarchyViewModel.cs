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

    public string Name
    {
        get => m_Name;
        set => this.RaiseAndSetIfChanged(ref m_Name, value);
    }

    private bool m_IsExpanded = true;

    public bool IsExpanded
    {
        get => m_IsExpanded;
        set => this.RaiseAndSetIfChanged(ref m_IsExpanded, value);
    }

    public ObservableCollection<ReactiveObject> Entities { get; } = new();

    public SceneNodeViewModel(string name)
    {
        m_Name = string.IsNullOrWhiteSpace(name) ? "Unnamed Scene" : name;
    }
}

public sealed class SceneAssetEntityNodeViewModel : ReactiveObject
{
    public SceneInspectionResult Scene { get; private set; }
    public int EntityIndex { get; }
    public string SourcePath => Scene.SourcePath;

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
        int entityIndex)
    {
        Scene = scene;
        m_Entity = entity;
        EntityIndex = entityIndex;
        m_Name = ResolveName(entity, entityIndex);
        m_ComponentSummary = BuildComponentSummary(entity);
    }

    public void UpdateInspection(SceneInspectionResult scene, SceneEntityInspection entity)
    {
        Scene = scene;
        Entity = entity;
        RaiseTransformPropertiesChanged();
        Name = ResolveName(entity, EntityIndex);
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

    private static string ResolveName(SceneEntityInspection entity, int entityIndex)
    {
        return string.IsNullOrWhiteSpace(entity.Name) ? $"Entity {entityIndex}" : entity.Name;
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
    private EditorSceneDocumentState? m_CurrentDocument;
    private SelectionService? m_SelectionService;
    private bool m_IsApplyingSelection;

    private string m_SearchText = string.Empty;

    public string SearchText
    {
        get => m_SearchText;
        set
        {
            this.RaiseAndSetIfChanged(ref m_SearchText, value);
            ApplyFilter();
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
    }

    private void OnDocumentStateChanged(EditorSceneDocumentState? state)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowDocument(state));
    }

    private void ShowDocument(EditorSceneDocumentState? state)
    {
        bool preserveState = IsSameDocument(m_CurrentDocument, state);
        var selectedEntity = m_SelectionService != null
            ? m_SelectionService.CurrentSelection as SceneAssetEntityNodeViewModel
            : SelectedItem as SceneAssetEntityNodeViewModel;
        int selectedEntityIndex = preserveState && selectedEntity != null
            ? selectedEntity.EntityIndex
            : -1;
        bool rootExpanded = preserveState && RootNodes.Count == 1
            ? RootNodes[0].IsExpanded
            : true;
        var reusableNodes = preserveState
            ? m_AllEntities.ToDictionary(node => node.EntityIndex)
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
            SceneAssetEntityNodeViewModel node;
            if (reusableNodes != null && reusableNodes.TryGetValue(i, out var reusableNode))
            {
                node = reusableNode;
                node.UpdateInspection(state.Inspection, state.Inspection.Entities[i]);
            }
            else
            {
                node = new SceneAssetEntityNodeViewModel(
                    state.Inspection,
                    state.Inspection.Entities[i],
                    i);
            }

            refreshedNodes.Add(node);
        }

        SynchronizeByIdentity(m_AllEntities, refreshedNodes);
        ApplyFilter(rootExpanded, preserveState);

        if (preserveState && selectedEntityIndex >= refreshedNodes.Count)
        {
            ClearSceneSelection();
        }
        else if (!preserveState && selectedEntity != null)
        {
            ClearSceneSelection();
        }
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
            ? m_AllEntities.Cast<ReactiveObject>().ToList()
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
