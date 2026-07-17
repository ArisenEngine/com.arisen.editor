using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ArisenEditor.Core.Assets;
using ArisenEngine.Core.Assets;
using ArisenEditor.Core.Commands;
using ArisenEditor.Core.Services;
using ArisenKernel.Contracts;
using ArisenEngine.Core.Automation;
using ArisenEditorFramework.Core;
using ArisenEditorFramework.Services;
using ArisenEditorFramework.UI.Menus;
using ArisenEngine.Core.ECS;
using ArisenEngine.Resources.Serialization;
using ReactiveUI;

namespace ArisenEditor.ViewModels;

public class SceneNodeViewModel : ReactiveObject
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
    public bool IsReadOnly { get; }

    public SceneNodeViewModel(string name, bool isReadOnly = false)
    {
        m_Name = string.IsNullOrWhiteSpace(name) ? "Unnamed Scene" : name;
        IsReadOnly = isReadOnly;
    }
}

public class EntityNodeViewModel : ReactiveObject
{
    public Entity Entity { get; }
    
    private string m_Name = "New Entity";
    public string Name
    {
        get => m_Name;
        set 
        {
            if (m_Name != value)
            {
                var oldName = m_Name;
                this.RaiseAndSetIfChanged(ref m_Name, value);
                ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ICommandManager>()!.Execute(new RenameEntityCommand(Entity, oldName, value));
            }
        }
    }

    public void SetNameWithoutNotifying(string newName)
    {
        this.RaiseAndSetIfChanged(ref m_Name, newName, nameof(Name));
    }
    
    private string m_DraftName = "";
    public string DraftName
    {
        get => m_DraftName;
        set => this.RaiseAndSetIfChanged(ref m_DraftName, value);
    }
    
    private bool m_IsRenaming;
    public bool IsRenaming
    {
        get => m_IsRenaming;
        set 
        {
            if (m_IsRenaming != value)
            {
                if (value)
                {
                    DraftName = Name; // Initialize draft
                }
                else
                {
                    // Commit draft to actual name if changed
                    if (!string.IsNullOrWhiteSpace(DraftName) && DraftName != Name)
                    {
                        Name = DraftName;
                    }
                }
                this.RaiseAndSetIfChanged(ref m_IsRenaming, value);
            }
        }
    }
    
    private bool m_IsExpanded = true;
    public bool IsExpanded
    {
        get => m_IsExpanded;
        set => this.RaiseAndSetIfChanged(ref m_IsExpanded, value);
    }

    public ObservableCollection<EntityNodeViewModel> Children { get; } = new();
    public EntityNodeViewModel? ParentNode { get; set; }
    
    public EntityNodeViewModel(Entity entity, string name)
    {
        Entity = entity;
        // Set the backing field directly to avoid triggering the Name setter,
        // which fires a RenameEntityCommand and causes an infinite RefreshHierarchy loop.
        m_Name = string.IsNullOrWhiteSpace(name) ? $"Entity {entity.Id}" : name;
    }
}

public sealed class SceneAssetEntityNodeViewModel : ReactiveObject
{
    public SceneInspectionResult Scene { get; }
    public int EntityIndex { get; }
    public string SourcePath => Scene.SourcePath;
    public string ComponentSummary { get; }

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

    public SceneAssetEntityNodeViewModel(SceneInspectionResult scene, SceneEntityInspection entity, int entityIndex)
    {
        Scene = scene;
        m_Entity = entity;
        EntityIndex = entityIndex;
        m_Name = string.IsNullOrWhiteSpace(entity.Name) ? $"Entity {entityIndex}" : entity.Name;
        ComponentSummary = BuildComponentSummary(entity);
    }

    public void SetTransform(SceneTransformInspection transform)
    {
        Entity = Entity with { Transform = transform };
        this.RaisePropertyChanged("Position");
        this.RaisePropertyChanged("Rotation");
        this.RaisePropertyChanged("Scale");
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

internal class HierarchyViewModel : EditorPanelBase
{
    private ObservableCollection<EntityNodeViewModel> m_AllEntities = new();
    private ObservableCollection<SceneAssetEntityNodeViewModel> m_AllSceneAssetEntities = new();
    private ObservableCollection<SceneNodeViewModel> m_RootNodes = new();
    private readonly System.Collections.Generic.Dictionary<Entity, EntityNodeViewModel> m_EntityMap = new();
    private readonly CompositeDisposable m_Disposables = new();
    private readonly IRuntimeSceneService? m_RuntimeSceneService;
    private readonly IAssetDatabase? m_RuntimeAssetDatabase;
    private SelectionService? m_SelectionService;
    private SceneInspectionResult? m_InspectedSceneAsset;
    private bool m_IsShowingSceneAssetInspection;
    private bool m_IsApplyingExternalSelection;

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

    public ObservableCollection<MenuAction> CreateActions { get; } = new();
    public ObservableCollection<MenuAction> ContextActions { get; } = new();

    public EntityManager? ActiveEntityManager { get; set; }
    public ArisenEditor.Core.Services.SelectionService? SelectionService
    {
        get => m_SelectionService;
        set
        {
            if (ReferenceEquals(m_SelectionService, value))
            {
                return;
            }

            if (m_SelectionService != null)
            {
                m_SelectionService.SelectionChanged -= OnExternalSelectionChanged;
            }

            m_SelectionService = value;
            if (m_SelectionService != null)
            {
                m_SelectionService.SelectionChanged += OnExternalSelectionChanged;
            }
        }
    }

    public override string Title => "Hierarchy";
    public override string Id => "Hierarchy";

    public override object Content => new Views.HierarchyView { DataContext = this };

    public ObservableCollection<SceneNodeViewModel> RootNodes
    {
        get => m_RootNodes;
        set => this.RaiseAndSetIfChanged(ref m_RootNodes, value);
    }

    private ReactiveObject? m_SelectedItem;
    public ReactiveObject? SelectedItem
    {
        get => m_SelectedItem;
        set => this.RaiseAndSetIfChanged(ref m_SelectedItem, value);
    }

    internal HierarchyViewModel()
    {
        // Register default provider (In a real app, this happens in bootstrapper)
        MenuRegistry.Instance.RegisterProvider(new ArisenEditor.Core.Services.HierarchyMenuProvider());
        
        // Populate menus
        RefreshMenus();

        this.WhenAnyValue(x => x.SelectedItem)
            .Subscribe(item =>
            {
                RefreshMenus(item);
                if (!m_IsApplyingExternalSelection && m_SelectionService != null)
                {
                    m_SelectionService.CurrentSelection = item;
                }
            });
            
        // Subscribe to SceneManagerService to auto-refresh when the active scene changes
        ArisenEditor.Core.Services.SceneManagerService.Instance
            .WhenAnyValue(x => x.ActiveScene)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(scene => 
            {
                m_IsShowingSceneAssetInspection = false;
                m_InspectedSceneAsset = null;
                m_AllSceneAssetEntities.Clear();

                if (scene != null)
                {
                    RefreshHierarchy(scene.Registry);
                }
                else
                {
                    m_AllEntities.Clear();
                    m_EntityMap.Clear();
                    RootNodes.Clear();
                    ActiveEntityManager = null;
                }
            })
            .DisposeWith(m_Disposables);

        var svc = ArisenEditor.Core.Services.SceneManagerService.Instance;

        svc.EntityNameChanged += (entity, newName) =>
        {
            if (m_IsShowingSceneAssetInspection) return;

            if (m_EntityMap.TryGetValue(entity, out var node))
            {
                node.SetNameWithoutNotifying(newName);
            }
        };

        svc.EntityCreated += (entity) =>
        {
            if (m_IsShowingSceneAssetInspection) return;
            if (ActiveEntityManager == null) return;
            string name = $"Entity {entity.Id}";
            if (ActiveEntityManager.HasComponent<NameComponent>(entity))
                name = ActiveEntityManager.GetComponent<NameComponent>(entity).Name;

            var node = new EntityNodeViewModel(entity, name);
            m_EntityMap[entity] = node;

            if (ActiveEntityManager.HasComponent<ParentComponent>(entity))
            {
                var p = ActiveEntityManager.GetComponent<ParentComponent>(entity).Parent;
                if (p != Entity.Null && m_EntityMap.TryGetValue(p, out var pNode))
                {
                    node.ParentNode = pNode;
                    pNode.Children.Add(node);
                    return;
                }
            }
            m_AllEntities.Add(node);
            if (RootNodes.Count > 0) RootNodes[0].Entities.Add(node);
        };

        svc.EntityDeleted += (entity) =>
        {
            if (m_IsShowingSceneAssetInspection) return;

            if (m_EntityMap.TryGetValue(entity, out var node))
            {
                if (node.ParentNode != null)
                {
                    node.ParentNode.Children.Remove(node);
                }
                else
                {
                    m_AllEntities.Remove(node);
                    if (RootNodes.Count > 0) RootNodes[0].Entities.Remove(node);
                }
                m_EntityMap.Remove(entity);
            }
        };

        svc.EntityParentChanged += (entity, newParent) =>
        {
            if (m_IsShowingSceneAssetInspection) return;

            if (m_EntityMap.TryGetValue(entity, out var node))
            {
                if (node.ParentNode != null)
                    node.ParentNode.Children.Remove(node);
                else
                {
                    m_AllEntities.Remove(node);
                    if (RootNodes.Count > 0) RootNodes[0].Entities.Remove(node);
                }

                if (newParent != Entity.Null && m_EntityMap.TryGetValue(newParent, out var pNode))
                {
                    node.ParentNode = pNode;
                    pNode.Children.Add(node);
                }
                else
                {
                    node.ParentNode = null;
                    m_AllEntities.Add(node);
                    if (RootNodes.Count > 0) RootNodes[0].Entities.Add(node);
                }
            }
        };

        ArisenEditor.Core.Services.SceneManagerService.Instance.PropertyChanged += (sender, args) =>
        {
            if (m_IsShowingSceneAssetInspection) return;

            if (args.PropertyName == nameof(ArisenEditor.Core.Services.SceneManagerService.IsDirty) ||
                args.PropertyName == nameof(ArisenEditor.Core.Services.SceneManagerService.ActiveScene))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    if (RootNodes.Count > 0)
                    {
                        var svc = ArisenEditor.Core.Services.SceneManagerService.Instance;
                        var sceneName = svc.ActiveScene?.Name ?? "Unnamed Scene";
                        if (svc.IsDirty) sceneName += "*";
                        RootNodes[0].Name = sceneName;
                    }
                });
            }
        };

        var services = ArisenKernel.Lifecycle.EngineKernel.Instance.Services;
        if (services.TryGetService<IRuntimeSceneService>(out var runtimeSceneService) &&
            services.TryGetService<IAssetDatabase>(out var runtimeAssetDatabase))
        {
            m_RuntimeSceneService = runtimeSceneService;
            m_RuntimeAssetDatabase = runtimeAssetDatabase;
            m_RuntimeSceneService.ActiveSceneChanged += OnRuntimeSceneChanged;

            if (m_RuntimeSceneService.ActiveScene is { } activeScene)
            {
                ShowRuntimeScene(activeScene);
            }
        }
    }

    private void OnRuntimeSceneChanged(RuntimeSceneState scene)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowRuntimeScene(scene));
    }

    private void ShowRuntimeScene(RuntimeSceneState scene)
    {
        if (m_RuntimeAssetDatabase == null)
        {
            return;
        }

        var inspection = SceneAssetLoader.InspectScene(m_RuntimeAssetDatabase, scene.Scene);
        SetSceneAssetInspection(inspection);
    }

    public void RefreshMenus(object? context = null)
    {
        CreateActions.Clear();
        foreach (var item in MenuRegistry.Instance.BuildMenu("Hierarchy.CreateMenu", context))
            CreateActions.Add(item);

        ContextActions.Clear();
        foreach (var item in MenuRegistry.Instance.BuildMenu("Hierarchy.ContextMenu", context))
            ContextActions.Add(item);
    }

    private void ApplyFilter(bool rootExpanded = true)
    {
        if (m_IsShowingSceneAssetInspection)
        {
            ApplySceneAssetFilter(rootExpanded);
            return;
        }

        var svc = ArisenEditor.Core.Services.SceneManagerService.Instance;
        var sceneName = svc.ActiveScene?.Name ?? "Unnamed Scene";
        if (svc.IsDirty) sceneName += "*";
        
        var sceneNode = new SceneNodeViewModel(sceneName) { IsExpanded = rootExpanded };

        if (string.IsNullOrWhiteSpace(m_SearchText))
        {
            foreach (var e in m_AllEntities)
                sceneNode.Entities.Add(e);
        }
        else
        {
            foreach (var e in m_AllEntities.Where(en => en.Name.Contains(m_SearchText, StringComparison.OrdinalIgnoreCase)))
                sceneNode.Entities.Add(e);
            sceneNode.IsExpanded = true;
        }

        RootNodes = new ObservableCollection<SceneNodeViewModel> { sceneNode };
    }

    private void ApplySceneAssetFilter(bool rootExpanded = true)
    {
        if (m_InspectedSceneAsset == null)
        {
            RootNodes.Clear();
            return;
        }

        var inspection = m_InspectedSceneAsset.Value;
        var sceneName = string.IsNullOrWhiteSpace(inspection.SceneName)
            ? Path.GetFileNameWithoutExtension(inspection.SourcePath)
            : inspection.SceneName;
        var sceneNode = new SceneNodeViewModel(sceneName, isReadOnly: true) { IsExpanded = rootExpanded };

        if (string.IsNullOrWhiteSpace(m_SearchText))
        {
            foreach (var e in m_AllSceneAssetEntities)
                sceneNode.Entities.Add(e);
        }
        else
        {
            foreach (var e in m_AllSceneAssetEntities.Where(en => en.Name.Contains(m_SearchText, StringComparison.OrdinalIgnoreCase)))
                sceneNode.Entities.Add(e);
            sceneNode.IsExpanded = true;
        }

        RootNodes = new ObservableCollection<SceneNodeViewModel> { sceneNode };
    }

    private void OnExternalSelectionChanged(object? selection)
    {
        if (selection is not FileTreeNode node)
        {
            return;
        }

        if (!TryShowSceneAssetHierarchy(node))
        {
            return;
        }

        m_IsApplyingExternalSelection = true;
        try
        {
            SelectedItem = null;
        }
        finally
        {
            m_IsApplyingExternalSelection = false;
        }
    }

    private bool TryShowSceneAssetHierarchy(FileTreeNode node)
    {
        if (node.IsBranch)
        {
            return false;
        }

        var extension = Path.GetExtension(node.Path);
        bool looksLikeScene = IsKnownSceneExtension(extension);

        if (!ArisenKernel.Lifecycle.EngineKernel.Instance.Services.TryGetService<IAssetDatabase>(out var assetDatabase) || assetDatabase == null)
        {
            if (!looksLikeScene)
            {
                return false;
            }

            SetSceneAssetInspection(new SceneInspectionResult(
                false,
                Path.GetFileNameWithoutExtension(node.Path),
                node.Path,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<SceneEntityInspection>(),
                new[] { "Runtime asset database service is not available." }));
            return true;
        }

        var guid = node.AssetGuid;
        if (guid == Guid.Empty)
        {
            guid = ArisenEditor.Core.Services.AssetDatabaseService.Instance.GetGuidFromPath(node.Path);
        }

        if (guid == Guid.Empty)
        {
            if (!looksLikeScene)
            {
                return false;
            }

            SetSceneAssetInspection(new SceneInspectionResult(
                false,
                Path.GetFileNameWithoutExtension(node.Path),
                node.Path,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<SceneEntityInspection>(),
                new[] { "Scene source is not indexed yet." }));
            return true;
        }

        if (!assetDatabase.TryGetAsset(guid, out var sourceAsset))
        {
            if (!looksLikeScene)
            {
                return false;
            }

            SetSceneAssetInspection(new SceneInspectionResult(
                false,
                Path.GetFileNameWithoutExtension(node.Path),
                node.Path,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<SceneEntityInspection>(),
                new[] { $"Runtime asset database has no record for scene GUID {guid}." }));
            return true;
        }

        if (!string.Equals(sourceAsset.AssetType, "Scene", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var inspection = SceneAssetLoader.InspectScene(
                assetDatabase,
                new AssetRef<SceneSourceAsset>(guid, "Scene", sourceAsset.PackageId));
            SetSceneAssetInspection(inspection);
        }
        catch (Exception ex)
        {
            SetSceneAssetInspection(new SceneInspectionResult(
                false,
                Path.GetFileNameWithoutExtension(sourceAsset.SourcePath),
                sourceAsset.SourcePath,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                Array.Empty<SceneEntityInspection>(),
                new[] { ex.Message }));
        }

        return true;
    }

    private void SetSceneAssetInspection(SceneInspectionResult inspection)
    {
        var selectedSceneEntity =
            m_SelectionService?.CurrentSelection as SceneAssetEntityNodeViewModel ??
            SelectedItem as SceneAssetEntityNodeViewModel;
        bool restoreSceneEntitySelection = selectedSceneEntity != null &&
            IsSameSceneSource(selectedSceneEntity.SourcePath, inspection.SourcePath);
        int selectedEntityIndex = restoreSceneEntitySelection
            ? selectedSceneEntity!.EntityIndex
            : -1;
        var rootExpanded = RootNodes.Count > 0 ? RootNodes[0].IsExpanded : true;
        m_IsShowingSceneAssetInspection = true;
        ActiveEntityManager = null;
        m_AllEntities.Clear();
        m_EntityMap.Clear();
        m_AllSceneAssetEntities.Clear();
        m_InspectedSceneAsset = inspection;

        SceneAssetEntityNodeViewModel? replacementSelection = null;
        for (int i = 0; i < inspection.Entities.Count; i++)
        {
            var entityNode = new SceneAssetEntityNodeViewModel(inspection, inspection.Entities[i], i);
            m_AllSceneAssetEntities.Add(entityNode);
            if (i == selectedEntityIndex)
            {
                replacementSelection = entityNode;
            }
        }

        ApplySceneAssetFilter(rootExpanded);

        if (restoreSceneEntitySelection)
        {
            m_IsApplyingExternalSelection = true;
            try
            {
                SelectedItem = replacementSelection;
            }
            finally
            {
                m_IsApplyingExternalSelection = false;
            }

            if (m_SelectionService != null)
            {
                m_SelectionService.CurrentSelection = replacementSelection;
            }
        }
    }

    public void RefreshHierarchy(EntityManager entityManager)
    {
        m_IsShowingSceneAssetInspection = false;
        m_InspectedSceneAsset = null;
        m_AllSceneAssetEntities.Clear();
        ActiveEntityManager = entityManager;
        
        var collapsedEntityIds = new System.Collections.Generic.HashSet<Entity>();
        foreach (var oldNode in m_AllEntities)
        {
            if (!oldNode.IsExpanded) collapsedEntityIds.Add(oldNode.Entity);
        }
        bool rootExpanded = RootNodes.Count > 0 ? RootNodes[0].IsExpanded : true;
        
        m_AllEntities.Clear();
        m_EntityMap.Clear();
        
        if (!entityManager.HasPool<NameComponent>())
        {
            ApplyFilter(rootExpanded);
            return;
        }

        var namePool = entityManager.GetPool<NameComponent>();
        var components = namePool.GetRawComponentArray();
        var entities = namePool.GetRawEntityArray();
        int count = namePool.Count;

        for (int i = 0; i < count; i++)
        {
            ref NameComponent nameComp = ref components[i];
            var node = new EntityNodeViewModel(entities[i], nameComp.Name);
            
            if (collapsedEntityIds.Contains(entities[i]))
            {
                node.IsExpanded = false;
            }
            
            m_AllEntities.Add(node);
            m_EntityMap[entities[i]] = node;
        }
        
        // Build hierarchy
        var rootEntities = new System.Collections.Generic.List<EntityNodeViewModel>();
        
        // Step 1: Discover Roots and assign ParentNode refs (skip Children.Add for now)
        foreach (var node in m_AllEntities)
        {
            if (entityManager.HasComponent<ParentComponent>(node.Entity))
            {
                var parentComp = entityManager.GetComponent<ParentComponent>(node.Entity);
                if (parentComp.Parent != Entity.Null && m_EntityMap.TryGetValue(parentComp.Parent, out var parentNode))
                {
                    node.ParentNode = parentNode;
                    continue; // Do NOT add to rootEntities!
                }
            }
            rootEntities.Add(node);
        }

        // Step 2: Traverse Intrusive Linked Lists to establish exact child ordering
        foreach (var node in m_AllEntities)
        {
            if (entityManager.HasComponent<ChildComponent>(node.Entity))
            {
                var childComp = entityManager.GetComponent<ChildComponent>(node.Entity);
                var currentChild = childComp.FirstChild;
                
                // Prevent infinite cycle in corrupted files
                int safetyLimit = 0; 
                while (currentChild != Entity.Null && safetyLimit < 10000)
                {
                    if (m_EntityMap.TryGetValue(currentChild, out var childNode))
                    {
                        node.Children.Add(childNode);
                    }
                    
                    if (entityManager.HasComponent<SiblingComponent>(currentChild))
                    {
                        currentChild = entityManager.GetComponent<SiblingComponent>(currentChild).NextSibling;
                    }
                    else
                    {
                        break;
                    }
                    safetyLimit++;
                }

                // Fallback: If any children somehow weren't part of the linked list but claimed this parent,
                // we should append them anyway to avoid losing objects in the UI.
            }
        }

        // Safety pass: Catch any orphaned children missing from the Sibling chain
        foreach (var node in m_AllEntities)
        {
            if (node.ParentNode != null && !node.ParentNode.Children.Contains(node))
            {
                node.ParentNode.Children.Add(node);
            }
        }

        m_AllEntities = new ObservableCollection<EntityNodeViewModel>(rootEntities);
        
        ApplyFilter(rootExpanded);
    }

    public void MoveEntity(EntityNodeViewModel source, EntityNodeViewModel? targetParent)
    {
        var em = ActiveEntityManager;
        if (em == null) return;
        
        var srcEntity = source.Entity;
        var newParentEntity = targetParent?.Entity ?? Entity.Null;

        if (srcEntity == newParentEntity) return;

        // Check if newParentEntity is a child of srcEntity to prevent cycles
        var current = newParentEntity;
        while (current != Entity.Null && em.HasComponent<ParentComponent>(current))
        {
            if (current == srcEntity) return; // Cycle detected
            current = em.GetComponent<ParentComponent>(current).Parent;
        }

        // Check if already at the target parent
        if (em.HasComponent<ParentComponent>(srcEntity))
        {
            var oldParent = em.GetComponent<ParentComponent>(srcEntity).Parent;
            if (oldParent == newParentEntity) return;
        }

        ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ICommandManager>()!.Execute(new MoveEntityCommand(srcEntity, newParentEntity, em));
    }

    internal void OnUnloaded()
    {
        if (m_SelectionService != null)
        {
            m_SelectionService.SelectionChanged -= OnExternalSelectionChanged;
        }

        if (m_RuntimeSceneService != null)
        {
            m_RuntimeSceneService.ActiveSceneChanged -= OnRuntimeSceneChanged;
        }

        m_Disposables.Dispose();
    }

    private static bool IsKnownSceneExtension(string extension)
    {
        return string.Equals(extension, ".arisenscene", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".scene", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameSceneSource(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(
                   AssetPathPolicy.NormalizeFullPath(left),
                   AssetPathPolicy.NormalizeFullPath(right),
                   StringComparison.OrdinalIgnoreCase);
    }
}



