using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using ArisenEditor.Core.Assets;
using ArisenEditor.Core.Commands;
using ArisenEditor.Core.Services;
using ArisenEngine.Core.Assets;
using ArisenEditorFramework.Inspector;
using ArisenEngine.Core.ECS;
using ArisenEngine.Rendering;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Resources.Serialization;
using ReactiveUI;
using ICommandManager = ArisenEngine.Core.Automation.ICommandManager;

namespace ArisenEditor.ViewModels;

/// <summary>
/// A specialized ECS-aware property item that knows how to read/write fields directly 
/// back to the ComponentPool memory using offsets, avoiding boxing/unboxing.
/// </summary>
public unsafe class ECSFieldPropertyViewModel : PropertyItemViewModel
{
    private readonly Entity _entity;
    private readonly IComponentPool _pool;
    private readonly int _fieldOffset;

    public override object? Value
    {
        get
        {
            // For reading to the UI, some boxing is inevitable as Avalonia expects objects,
            // but we minimize it by avoiding PropertyInfo.GetValue when possible.
            var ptr = _pool.GetAddress(_entity);
            if (ptr == IntPtr.Zero) return null;

            // Use reflection-based fallback for the GET (UI-bound) to handle all types easily.
            // Boxing on GET for the UI is acceptable; boxing on SET/HOT-PATH is not.
            var component = _pool.GetBoxed(_entity);
            return _propertyInfo?.GetValue(component) ?? _fieldInfo?.GetValue(component);
        }
        set
        {
            if (IsReadOnly) return;

            var ptr = _pool.GetAddress(_entity);
            if (ptr == IntPtr.Zero) return;

            object oldComponent = _pool.GetBoxed(_entity);
            object newComponent = _pool.GetBoxed(_entity);

            object? converted = TryConvert(value, PropertyType);
            
            if (converted is string strValue)
            {
                if (PropertyType == typeof(System.Numerics.Vector3))
                {
                    converted = TryParseVector3(strValue);
                    if (converted == null) return;
                }
                else if (PropertyType == typeof(System.Numerics.Quaternion))
                {
                    converted = TryParseQuaternion(strValue);
                    if (converted == null) return;
                }
            }
            
            if (converted == null && value != null) return;

            _fieldInfo?.SetValue(newComponent, converted);

            var cmdMgr = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ArisenEngine.Core.Automation.ICommandManager>();
            var cmd = new ArisenEditor.Core.Commands.ModifyComponentCommand(_entity, _pool, oldComponent, newComponent);
            cmdMgr?.Execute(cmd);
            
            this.RaisePropertyChanged(nameof(Value));
        }
    }

    public void Refresh() => this.RaisePropertyChanged(nameof(Value));

    private readonly FieldInfo? _fieldInfo;

    public ECSFieldPropertyViewModel(Entity entity, IComponentPool pool, FieldInfo fieldInfo) 
        : base(pool.GetBoxed(entity), fieldInfo.Name, fieldInfo.FieldType, false, pool.GetComponentType().Name)
    {
        _entity = entity;
        _pool = pool;
        _fieldInfo = fieldInfo;
        
        // Calculate the native offset of the field within the struct
        _fieldOffset = (int)Marshal.OffsetOf(pool.GetComponentType(), fieldInfo.Name);

        ApplyAttributes(fieldInfo);
    }
    
    private static object? TryParseVector3(string input)
    {
        // Vector3.ToString() format: "<1, 2, 3>"
        var clean = input.Trim('<', '>', ' ', '\t');
        var parts = clean.Split(',');
        if (parts.Length == 3 && 
            float.TryParse(parts[0], out float x) &&
            float.TryParse(parts[1], out float y) &&
            float.TryParse(parts[2], out float z))
        {
            return new System.Numerics.Vector3(x, y, z);
        }
        return null;
    }

    private static object? TryParseQuaternion(string input)
    {
        // Quaternion depends on standard ToString output, usually "{X:1 Y:2 Z:3 W:4}" or "<1, 2, 3, 4>"
        var clean = input.Replace("{", "").Replace("}", "").Replace("<", "").Replace(">", "").Trim();
        var parts = clean.Split(new[] { ' ', ',', ':' }, StringSplitOptions.RemoveEmptyEntries);
        
        // Extract 4 floats from whatever tokens are found
        var values = new System.Collections.Generic.List<float>();
        foreach (var p in parts)
        {
            if (float.TryParse(p, out float v))
                values.Add(v);
        }
        
        if (values.Count >= 4)
        {
            return new System.Numerics.Quaternion(values[0], values[1], values[2], values[3]);
        }
        return null;
    }
}

/// <summary>
/// A specialized ECS-aware property item that knows how to read/write properties
/// back to the ComponentPool memory. Properties require boxing/unboxing since they invoke method calls.
/// </summary>
public class ECSPropertyViewModel : PropertyItemViewModel
{
    private readonly Entity _entity;
    private readonly IComponentPool _pool;
    private readonly PropertyInfo _propInfo;

    public ECSPropertyViewModel(Entity entity, IComponentPool pool, PropertyInfo propInfo) 
        : base(pool.GetBoxed(entity), propInfo.Name, propInfo.PropertyType, !propInfo.CanWrite, pool.GetComponentType().Name)
    {
        _entity = entity;
        _pool = pool;
        _propInfo = propInfo;
        
        ApplyAttributes(propInfo);
    }
    
    public override object? Value
    {
        get
        {
            var ptr = _pool.GetAddress(_entity);
            if (ptr == IntPtr.Zero) return null;

            var component = _pool.GetBoxed(_entity);
            return _propInfo.GetValue(component);
        }
        set
        {
            if (IsReadOnly) return;

            var ptr = _pool.GetAddress(_entity);
            if (ptr == IntPtr.Zero) return;

            object oldComponent = _pool.GetBoxed(_entity);
            object newComponent = _pool.GetBoxed(_entity);

            object? converted = TryConvert(value, PropertyType);
            
            if (converted is string strValue)
            {
                if (PropertyType == typeof(System.Numerics.Vector3))
                {
                    converted = TryParseVector3(strValue);
                    if (converted == null) return;
                }
                else if (PropertyType == typeof(System.Numerics.Quaternion))
                {
                    converted = TryParseQuaternion(strValue);
                    if (converted == null) return;
                }
            }
            
            if (converted == null && value != null) return;

            _propInfo.SetValue(newComponent, converted);
            
            var cmdMgr = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ArisenEngine.Core.Automation.ICommandManager>();
            var cmd = new ArisenEditor.Core.Commands.ModifyComponentCommand(_entity, _pool, oldComponent, newComponent);
            cmdMgr?.Execute(cmd);
            
            this.RaisePropertyChanged(nameof(Value));
        }
    }
    
    public void Refresh() => this.RaisePropertyChanged(nameof(Value));
    
    private static object? TryParseVector3(string input)
    {
        var clean = input.Trim('<', '>', ' ', '\t');
        var parts = clean.Split(',');
        if (parts.Length == 3 && 
            float.TryParse(parts[0], out float x) &&
            float.TryParse(parts[1], out float y) &&
            float.TryParse(parts[2], out float z))
        {
            return new System.Numerics.Vector3(x, y, z);
        }
        return null;
    }

    private static object? TryParseQuaternion(string input)
    {
        var clean = input.Replace("{", "").Replace("}", "").Replace("<", "").Replace(">", "").Trim();
        var parts = clean.Split(new[] { ' ', ',', ':' }, StringSplitOptions.RemoveEmptyEntries);
        
        var values = new System.Collections.Generic.List<float>();
        foreach (var p in parts)
        {
            if (float.TryParse(p, out float v))
                values.Add(v);
        }
        
        if (values.Count >= 4)
        {
            return new System.Numerics.Quaternion(values[0], values[1], values[2], values[3]);
        }
        return null;
    }
}

public enum SceneTransformProperty
{
    Position,
    Rotation,
    Scale
}

public sealed class SceneTransformPropertyViewModel : PropertyItemViewModel
{
    private readonly SceneAssetEntityNodeViewModel m_Node;
    private readonly SceneTransformProperty m_Property;

    public SceneTransformPropertyViewModel(
        SceneAssetEntityNodeViewModel node,
        SceneTransformProperty property,
        bool isReadOnly)
        : base(
            node,
            property.ToString(),
            property == SceneTransformProperty.Rotation ? typeof(Quaternion) : typeof(Vector3),
            isReadOnly,
            "Transform")
    {
        m_Node = node;
        m_Property = property;
    }

    public override object? Value
    {
        get
        {
            var transform = m_Node.Entity.Transform;
            return m_Property switch
            {
                SceneTransformProperty.Position => transform.Position,
                SceneTransformProperty.Rotation => transform.Rotation,
                SceneTransformProperty.Scale => transform.Scale,
                _ => null
            };
        }
        set
        {
            if (IsReadOnly || value == null)
            {
                return;
            }

            var oldTransform = m_Node.Entity.Transform;
            var newTransform = m_Property switch
            {
                SceneTransformProperty.Position when TryGetVector3(value, out var position) =>
                    oldTransform with { Position = position },
                SceneTransformProperty.Rotation when TryGetQuaternion(value, out var rotation) =>
                    oldTransform with { Rotation = rotation },
                SceneTransformProperty.Scale when TryGetVector3(value, out var scale) =>
                    oldTransform with { Scale = scale },
                _ => oldTransform
            };

            if (newTransform.Equals(oldTransform))
            {
                return;
            }

            var command = new ModifySceneAssetTransformCommand(
                m_Node.SourcePath,
                m_Node.EntityIndex,
                m_Node.Name,
                oldTransform,
                newTransform,
                m_Node.SetTransform);

            try
            {
                ArisenKernel.Lifecycle.EngineKernel.Instance.Services
                    .GetService<ICommandManager>()
                    ?.Execute(command);
            }
            catch (Exception ex)
            {
                EditorLog.Error($"[SceneAssetInspector] Failed to edit transform for '{m_Node.Name}'.", ex);
            }
        }
    }

    private static bool TryGetVector3(object value, out Vector3 result)
    {
        if (value is Vector3 vector)
        {
            result = vector;
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryGetQuaternion(object value, out Quaternion result)
    {
        if (value is Quaternion quaternion)
        {
            result = quaternion;
            return true;
        }

        result = default;
        return false;
    }
}

/// <summary>
/// Overrides the standard Inspector to detect when an ECS Entity is selected.
/// It dynamically builds categories based on the components attached to the entity.
/// </summary>
internal class InspectorViewModel : ArisenEditorFramework.Inspector.InspectorViewModel
{
    public ArisenEditor.Core.Services.SelectionService? SelectionService { get; set; }

    private readonly System.Collections.Generic.List<Type> _allComponentTypes;
    private Guid m_LastModelReimportGuid;
    private string m_LastModelReimportStatus = string.Empty;

    public InspectorViewModel()
    {
        _allComponentTypes = System.AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .Where(t => typeof(ArisenEngine.Core.ECS.IComponent).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
            .OrderBy(t => t.Name)
            .ToList();

        AddComponentCommand = ReactiveUI.ReactiveCommand.Create<Type>(t => 
        {
            if (TargetObject is EntityNodeViewModel node && t != null)
            {
                var cmdMgr = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ArisenEngine.Core.Automation.ICommandManager>();
                cmdMgr?.Execute(new ArisenEditor.Core.Commands.AddComponentCommand(node.Entity, t));
                
                // Defer the UI rebuild to the next tick. 
                // This prevents Avalonia's ComboBox from crashing when its ItemsSource vanishes 
                // while it's still processing the selection event.
                Avalonia.Threading.Dispatcher.UIThread.Post(() => RebuildProperties(), Avalonia.Threading.DispatcherPriority.Background);
            }
        });

        var svc = ArisenEditor.Core.Services.SceneManagerService.Instance;
        
        svc.EntityNameChanged += (entity, name) =>
        {
            if (TargetObject is EntityNodeViewModel node && node.Entity == entity)
            {
                foreach (var category in Categories)
                {
                    if (category.CategoryName == typeof(NameComponent).Name)
                    {
                        foreach (var prop in category.Properties)
                        {
                            if (prop is ECSPropertyViewModel ecsProp) ecsProp.Refresh();
                            else if (prop is ECSFieldPropertyViewModel ecsField) ecsField.Refresh();
                        }
                    }
                }
            }
        };

        svc.EntityComponentChanged += (entity, compType) =>
        {
            if (TargetObject is EntityNodeViewModel node && node.Entity == entity)
            {
                foreach (var category in Categories)
                {
                    if (category.CategoryName == compType.Name)
                    {
                        foreach (var prop in category.Properties)
                        {
                            if (prop is ECSPropertyViewModel ecsProp) ecsProp.Refresh();
                            else if (prop is ECSFieldPropertyViewModel ecsField) ecsField.Refresh();
                        }
                    }
                }
            }
        };
    }

    protected override void RebuildProperties()
    {
        // 1. Standard Cleanup
        foreach (var category in Categories)
        {
            foreach (var prop in category.Properties)
            {
                prop.Dispose();
            }
        }
        Categories.Clear();

        CanAddComponent = false;
        AvailableComponentTypes.Clear();

        if (TargetObject == null)
            return;

        if (TargetObject is SceneAssetEntityNodeViewModel sceneAssetEntityNode)
        {
            RebuildSceneAssetEntityProperties(sceneAssetEntityNode);
            return;
        }

        if (TargetObject is FileTreeNode sceneFileNode && TryRebuildSceneAssetProperties(sceneFileNode))
        {
            return;
        }

        if (TargetObject is FileTreeNode modelFileNode && TryRebuildModelAssetProperties(modelFileNode))
        {
            return;
        }

        if (TargetObject is FileTreeNode fileNode && TryRebuildMaterialAssetProperties(fileNode))
        {
            return;
        }

        if (TargetObject is FileTreeNode meshFileNode && TryRebuildMeshAssetProperties(meshFileNode))
        {
            return;
        }

        if (TargetObject is FileTreeNode shaderFileNode && TryRebuildShaderAssetProperties(shaderFileNode))
        {
            return;
        }

        // 2. Check if we are inspecting a specialized EntityNode
        if (TargetObject is EntityNodeViewModel node)
        {
            CanAddComponent = true;
            AvailableComponentTypes.Clear();

            var ActiveEntityManager = ArisenEditor.Core.Services.SceneManagerService.Instance.ActiveScene?.Registry;
            if (ActiveEntityManager == null) return;

            var currentComponents = new System.Collections.Generic.HashSet<Type>();

            foreach (var pool in ActiveEntityManager.GetEntityComponentPools(node.Entity))
            {
                var compType = pool.GetComponentType();
                currentComponents.Add(compType);

                // Create a category for this component
                var category = new ArisenEditorFramework.Inspector.InspectorCategoryViewModel(compType.Name);
                Categories.Add(category);

                // Wire Remove button (Undo/Redo supported)
                category.CanRemove = (compType != typeof(NameComponent)); // Don't allow removing Name component
                category.RemoveCommand = ReactiveUI.ReactiveCommand.Create(() => 
                {
                    var cmdMgr = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<ArisenEngine.Core.Automation.ICommandManager>();
                    cmdMgr?.Execute(new ArisenEditor.Core.Commands.RemoveComponentCommand(node.Entity, compType));
                    
                    // Defer refresh to let UI close expander smoothly
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => RebuildProperties(), Avalonia.Threading.DispatcherPriority.Background);
                });

                // Discover fields (ECS components use fields for data per Rules.md)
                var fields = compType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                foreach (var field in fields)
                {
                    var propVm = new ECSFieldPropertyViewModel(node.Entity, pool, field);
                    category.Properties.Add(propVm);
                }
                
                // Also support properties if any (though spec says use fields)
                var props = compType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (var prop in props)
                {
                    // Filter out any properties we don't want to show
                    if (prop.Name == "TypeId") continue; // Avoid internal properties if any
                    
                    var propVm = new ECSPropertyViewModel(node.Entity, pool, prop);
                    category.Properties.Add(propVm);
                }
            }

            // Populate AvailableComponentTypes
            foreach(var t in _allComponentTypes)
            {
                if (!currentComponents.Contains(t))
                {
                    AvailableComponentTypes.Add(t);
                }
            }
        }
        else
        {
            // 3. Fallback to standard reflection for non-ECS objects
            base.RebuildProperties();
        }
    }

    private void RebuildSceneAssetEntityProperties(SceneAssetEntityNodeViewModel node)
    {
        var entity = node.Entity;
        var category = AddCategory("Scene Entity");
        AddReadOnly(category, "Name", entity.Name);
        AddReadOnly(category, "Index", node.EntityIndex);
        AddReadOnly(category, "Components", FormatSceneComponents(entity));
        AddReadOnly(category, "Source", node.SourcePath);

        AddSceneAssetEntityTransformCategory(node);
        AddSceneAssetEntityCameraCategory(entity);
        AddSceneAssetEntityMeshRendererCategory(entity);
        AddSceneAssetEntityLightCategory(entity);
        AddSceneAssetEntityEnvironmentCategory(entity);
        AddSceneAssetEntityDiagnosticsCategory(entity);
    }

    private bool TryRebuildSceneAssetProperties(FileTreeNode node)
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

            AddDiagnosticsCategory("Runtime asset database service is not available.");
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

            AddAssetHeader(node, guid, "Scene", string.Empty, string.Empty);
            AddDiagnosticsCategory("Scene source is not indexed yet. Save or reimport the asset so a .meta GUID is registered.");
            return true;
        }

        if (!assetDatabase.TryGetAsset(guid, out var sourceAsset))
        {
            if (!looksLikeScene)
            {
                return false;
            }

            AddAssetHeader(node, guid, "Scene", string.Empty, string.Empty);
            AddDiagnosticsCategory($"Runtime asset database has no record for scene GUID {guid}.");
            return true;
        }

        if (!string.Equals(sourceAsset.AssetType, "Scene", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AddAssetHeader(node, guid, sourceAsset.AssetType, sourceAsset.PackageId, sourceAsset.SourcePath);

        try
        {
            var inspection = SceneAssetLoader.InspectScene(
                assetDatabase,
                new AssetRef<SceneSourceAsset>(guid, "Scene", sourceAsset.PackageId));
            AddSceneSummaryCategory(inspection);
            AddSceneEntitiesCategory(inspection);
            AddSceneCameraCategory(inspection);
            AddSceneMeshRendererCategory(inspection);
            AddSceneLightCategory(inspection);
            AddSceneEnvironmentCategory(inspection);
            AddSceneDiagnosticsCategory(inspection);
        }
        catch (Exception ex)
        {
            AddDiagnosticsCategory(ex.Message);
        }

        return true;
    }

    private void AddSceneAssetEntityTransformCategory(SceneAssetEntityNodeViewModel node)
    {
        var category = AddCategory("Transform");
        var isReadOnly = !AssetPathPolicy.IsEditableAssetPath(node.SourcePath);
        category.Properties.Add(new SceneTransformPropertyViewModel(node, SceneTransformProperty.Position, isReadOnly));
        category.Properties.Add(new SceneTransformPropertyViewModel(node, SceneTransformProperty.Rotation, isReadOnly));
        category.Properties.Add(new SceneTransformPropertyViewModel(node, SceneTransformProperty.Scale, isReadOnly));

        if (isReadOnly)
        {
            AddReadOnly(category, "Edit Policy", "Only source scene assets under workspace/package Assets roots can be edited.");
        }
    }

    private void AddSceneAssetEntityCameraCategory(SceneEntityInspection entity)
    {
        if (entity.Camera == null)
        {
            return;
        }

        var camera = entity.Camera;
        var category = AddCategory("Camera");
        AddReadOnly(category, "Projection", camera.IsPerspective ? "Perspective" : "Orthographic");
        AddReadOnly(category, "Vertical FOV", camera.VerticalFov.ToString("0.###"));
        AddReadOnly(category, "Near Plane", camera.NearPlane.ToString("0.###"));
        AddReadOnly(category, "Far Plane", camera.FarPlane.ToString("0.###"));
    }

    private void AddSceneAssetEntityMeshRendererCategory(SceneEntityInspection entity)
    {
        if (entity.MeshRenderer == null)
        {
            return;
        }

        var renderer = entity.MeshRenderer;
        var category = AddCategory("Mesh Renderer");
        AddReadOnly(category, "Mesh", FormatSceneAssetRef(renderer.Mesh));
        AddReadOnly(category, "Material", FormatSceneAssetRef(renderer.Material));
        AddReadOnly(category, "First Submesh", renderer.FirstSubmeshIndex);
        AddReadOnly(category, "Submesh Count", renderer.SubmeshCount);
        AddReadOnly(category, "Visible", renderer.Visible);
        AddReadOnly(category, "Bounds Center", FormatVector3(renderer.BoundsCenter));
        AddReadOnly(category, "Bounds Extents", FormatVector3(renderer.BoundsExtents));
    }

    private void AddSceneAssetEntityLightCategory(SceneEntityInspection entity)
    {
        if (entity.DirectionalLight != null)
        {
            var light = entity.DirectionalLight;
            var category = AddCategory("Directional Light");
            AddReadOnly(category, "Direction", FormatVector3(light.Direction));
            AddReadOnly(category, "Color", FormatVector3(light.Color));
            AddReadOnly(category, "Intensity", light.Intensity.ToString("0.###"));
            AddReadOnly(category, "Ambient Intensity", light.AmbientIntensity.ToString("0.###"));
            AddReadOnly(category, "Enabled", light.Enabled);
        }

        if (entity.PointLight != null)
        {
            var light = entity.PointLight;
            var category = AddCategory("Point Light");
            AddReadOnly(category, "Color", FormatVector3(light.Color));
            AddReadOnly(category, "Intensity", light.Intensity.ToString("0.###"));
            AddReadOnly(category, "Range", light.Range.ToString("0.###"));
            AddReadOnly(category, "Enabled", light.Enabled);
        }

        if (entity.SpotLight != null)
        {
            var light = entity.SpotLight;
            var category = AddCategory("Spot Light");
            AddReadOnly(category, "Color", FormatVector3(light.Color));
            AddReadOnly(category, "Intensity", light.Intensity.ToString("0.###"));
            AddReadOnly(category, "Range", light.Range.ToString("0.###"));
            AddReadOnly(category, "Inner Cone", light.InnerConeAngleDegrees.ToString("0.###"));
            AddReadOnly(category, "Outer Cone", light.OuterConeAngleDegrees.ToString("0.###"));
            AddReadOnly(category, "Enabled", light.Enabled);
        }
    }

    private void AddSceneAssetEntityEnvironmentCategory(SceneEntityInspection entity)
    {
        if (entity.Environment == null)
        {
            return;
        }

        var environment = entity.Environment;
        var category = AddCategory("Environment");
        AddReadOnly(category, "Environment Texture", FormatSceneAssetRef(environment.EnvironmentTexture));
        AddReadOnly(category, "Sky Color", FormatVector3(environment.SkyColor));
        AddReadOnly(category, "Horizon Color", FormatVector3(environment.HorizonColor));
        AddReadOnly(category, "Ground Color", FormatVector3(environment.GroundColor));
        AddReadOnly(category, "Ambient Color", FormatVector3(environment.AmbientColor));
        AddReadOnly(category, "Sky Intensity", environment.SkyIntensity.ToString("0.###"));
        AddReadOnly(category, "Ambient Intensity", environment.AmbientIntensity.ToString("0.###"));
        AddReadOnly(category, "Exposure", environment.Exposure.ToString("0.###"));
        AddReadOnly(category, "Enabled", environment.Enabled);
    }

    private void AddSceneAssetEntityDiagnosticsCategory(SceneEntityInspection entity)
    {
        if (entity.MeshRenderer == null && entity.Environment == null)
        {
            return;
        }

        var category = AddCategory("Scene Diagnostics");
        var count = 0;
        if (entity.MeshRenderer != null)
        {
            AddSceneAssetRefDiagnostic(category, "Mesh", entity.MeshRenderer.Mesh, ref count);
            AddSceneAssetRefDiagnostic(category, "Material", entity.MeshRenderer.Material, ref count);
        }

        if (entity.Environment != null)
        {
            AddSceneAssetRefDiagnostic(
                category,
                "Environment Texture",
                entity.Environment.EnvironmentTexture,
                ref count);
        }

        if (count == 0)
        {
            AddReadOnly(category, "Status", "Scene entity references are valid.");
        }
    }

    private static void AddSceneAssetRefDiagnostic(
        InspectorCategoryViewModel category,
        string name,
        SceneAssetReferenceInspection assetRef,
        ref int count)
    {
        if (!assetRef.HasValue || assetRef.IsResolved)
        {
            return;
        }

        count++;
        AddReadOnly(category, name, assetRef.Diagnostic);
    }

    private bool TryRebuildModelAssetProperties(FileTreeNode node)
    {
        if (node.IsBranch)
        {
            return false;
        }

        var extension = Path.GetExtension(node.Path);
        bool looksLikeModel = IsKnownModelExtension(extension);

        if (!ArisenKernel.Lifecycle.EngineKernel.Instance.Services.TryGetService<IAssetDatabase>(out var assetDatabase) || assetDatabase == null)
        {
            if (!looksLikeModel)
            {
                return false;
            }

            AddDiagnosticsCategory("Runtime asset database service is not available.");
            return true;
        }

        var guid = node.AssetGuid;
        if (guid == Guid.Empty)
        {
            guid = ArisenEditor.Core.Services.AssetDatabaseService.Instance.GetGuidFromPath(node.Path);
        }

        if (guid == Guid.Empty)
        {
            if (!looksLikeModel)
            {
                return false;
            }

            AddAssetHeader(node, guid, ModelSourceAssetLoader.ModelAssetType, string.Empty, string.Empty);
            AddDiagnosticsCategory("Model source is not indexed yet. Save or reimport the asset so a .meta GUID is registered.");
            return true;
        }

        if (!assetDatabase.TryGetAsset(guid, out var sourceAsset))
        {
            if (!looksLikeModel)
            {
                return false;
            }

            AddAssetHeader(node, guid, ModelSourceAssetLoader.ModelAssetType, string.Empty, string.Empty);
            AddDiagnosticsCategory($"Runtime asset database has no record for model GUID {guid}.");
            return true;
        }

        if (!string.Equals(sourceAsset.AssetType, ModelSourceAssetLoader.ModelAssetType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AddAssetHeader(node, guid, sourceAsset.AssetType, sourceAsset.PackageId, sourceAsset.SourcePath);

        try
        {
            var model = ModelSourceAssetLoader.LoadSource(sourceAsset);
            var plan = ModelSourceAssetLoader.CreateGltfPlan(sourceAsset, model);
            AddModelActions(assetDatabase, sourceAsset);
            AddModelReimportStatusCategory(guid);
            AddModelSourceCategory(sourceAsset, model);
            AddModelImportSettingsCategory(sourceAsset, model);
            AddModelShaderCategory(model);
            AddGltfModelSummaryCategory(plan);
            AddGltfGeneratedChildrenCategory(plan);
            AddModelGeneratedOutputCategory(sourceAsset, model, plan);
            AddGltfMaterialPreviewCategory(plan);
            AddGltfTextureRefCategory(plan);
            AddGltfWarningsCategory(plan);
        }
        catch (Exception ex)
        {
            AddDiagnosticsCategory(ex.Message);
        }

        return true;
    }

    private bool TryRebuildMaterialAssetProperties(FileTreeNode node)
    {
        if (node.IsBranch)
        {
            return false;
        }

        var extension = Path.GetExtension(node.Path);
        bool looksLikeMaterial = string.Equals(extension, ".arismaterial", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(extension, ".material", StringComparison.OrdinalIgnoreCase);

        if (!ArisenKernel.Lifecycle.EngineKernel.Instance.Services.TryGetService<IAssetDatabase>(out var assetDatabase) || assetDatabase == null)
        {
            if (!looksLikeMaterial)
            {
                return false;
            }

            AddDiagnosticsCategory("Runtime asset database service is not available.");
            return true;
        }

        var guid = node.AssetGuid;
        if (guid == Guid.Empty)
        {
            guid = ArisenEditor.Core.Services.AssetDatabaseService.Instance.GetGuidFromPath(node.Path);
        }

        if (guid == Guid.Empty)
        {
            if (!looksLikeMaterial)
            {
                return false;
            }

            AddAssetHeader(node, guid, "Material", string.Empty, string.Empty);
            AddDiagnosticsCategory("Material source is not indexed yet. Save or reimport the asset so a .meta GUID is registered.");
            return true;
        }

        if (!assetDatabase.TryGetAsset(guid, out var sourceAsset))
        {
            if (!looksLikeMaterial)
            {
                return false;
            }

            AddAssetHeader(node, guid, "Material", string.Empty, string.Empty);
            AddDiagnosticsCategory($"Runtime asset database has no record for material GUID {guid}.");
            return true;
        }

        if (!string.Equals(sourceAsset.AssetType, "Material", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AddAssetHeader(node, guid, sourceAsset.AssetType, sourceAsset.PackageId, sourceAsset.SourcePath);
        var canEdit = MaterialAssetEditPolicy.CanEdit(sourceAsset, out var editDiagnostic);
        AddMaterialActions(assetDatabase, sourceAsset, editDiagnostic);

        try
        {
            var inspection = MaterialAssetLoader.InspectSource(assetDatabase, guid);
            var material = inspection.Asset;
            AddShaderCategory(material.Shader, assetDatabase);
            var textureOptions = BuildMaterialTextureOptions(assetDatabase);
            AddTextureCategory(material, assetDatabase, sourceAsset, textureOptions, !canEdit);
            AddScalarCategory(material, assetDatabase, sourceAsset, !canEdit);
            AddVectorCategory(material, assetDatabase, sourceAsset, !canEdit);
            AddRenderStateCategory(material.RenderState);
            AddMaterialDiagnostics(inspection);
        }
        catch (Exception ex)
        {
            AddDiagnosticsCategory(ex.Message);
        }

        return true;
    }

    private bool TryRebuildMeshAssetProperties(FileTreeNode node)
    {
        if (node.IsBranch)
        {
            return false;
        }

        var extension = Path.GetExtension(node.Path);
        bool looksLikeMesh = IsKnownMeshExtension(extension);

        if (!ArisenKernel.Lifecycle.EngineKernel.Instance.Services.TryGetService<IAssetDatabase>(out var assetDatabase) || assetDatabase == null)
        {
            if (!looksLikeMesh)
            {
                return false;
            }

            AddDiagnosticsCategory("Runtime asset database service is not available.");
            return true;
        }

        var guid = node.AssetGuid;
        if (guid == Guid.Empty)
        {
            guid = ArisenEditor.Core.Services.AssetDatabaseService.Instance.GetGuidFromPath(node.Path);
        }

        if (guid == Guid.Empty)
        {
            if (!looksLikeMesh)
            {
                return false;
            }

            AddAssetHeader(node, guid, "Mesh", string.Empty, string.Empty);
            AddDiagnosticsCategory("Mesh source is not indexed yet. Save or reimport the asset so a .meta GUID is registered.");
            return true;
        }

        if (!assetDatabase.TryGetAsset(guid, out var sourceAsset))
        {
            if (!looksLikeMesh)
            {
                return false;
            }

            AddAssetHeader(node, guid, "Mesh", string.Empty, string.Empty);
            AddDiagnosticsCategory($"Runtime asset database has no record for mesh GUID {guid}.");
            return true;
        }

        if (!string.Equals(sourceAsset.AssetType, "Mesh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AddAssetHeader(node, guid, sourceAsset.AssetType, sourceAsset.PackageId, sourceAsset.SourcePath);

        if (!TryResolveMeshSourceFormat(sourceAsset.SourcePath, out var sourceFormat, out var formatDiagnostic))
        {
            AddReadOnly(AddCategory("Mesh"), "Source Format", formatDiagnostic);
            AddDiagnosticsCategory(formatDiagnostic);
            return true;
        }

        AddMeshActions(assetDatabase, sourceAsset);
        AddGltfModelImportDiagnostics(sourceAsset);

        var mesh = new MeshAsset(
            guid,
            Path.GetFileNameWithoutExtension(sourceAsset.SourcePath),
            MeshVariantKey.Default,
            sourceFormat);

        CookedMesh cooked = default;
        try
        {
            cooked = MeshAssetCooker.LoadOrCook(assetDatabase, mesh);
            var cookedBytes = assetDatabase.GetCookedAssetBytes(cooked.Handle);
            var submeshes = new MeshSubmesh[checked((int)cooked.SubmeshCount)];
            MeshAssetCooker.ReadSubmeshes(cookedBytes.Span, cooked, submeshes);

            AddMeshSummaryCategory(cooked);
            AddMeshBoundsCategory(cooked.Bounds);
            AddMeshSubmeshCategory(submeshes);
            AddDiagnosticsCategory("Mesh importer and cooked payload validation passed.");
        }
        catch (Exception ex)
        {
            AddDiagnosticsCategory(ex.Message);
        }
        finally
        {
            if (cooked.Handle.IsValid)
            {
                assetDatabase.Release(cooked.Handle);
            }
        }

        return true;
    }

    private bool TryRebuildShaderAssetProperties(FileTreeNode node)
    {
        if (node.IsBranch)
        {
            return false;
        }

        var extension = Path.GetExtension(node.Path);
        bool looksLikeShader = IsKnownShaderExtension(extension);

        if (!ArisenKernel.Lifecycle.EngineKernel.Instance.Services.TryGetService<IAssetDatabase>(out var assetDatabase) || assetDatabase == null)
        {
            if (!looksLikeShader)
            {
                return false;
            }

            AddDiagnosticsCategory("Runtime asset database service is not available.");
            return true;
        }

        var guid = node.AssetGuid;
        if (guid == Guid.Empty)
        {
            guid = ArisenEditor.Core.Services.AssetDatabaseService.Instance.GetGuidFromPath(node.Path);
        }

        if (guid == Guid.Empty)
        {
            if (!looksLikeShader)
            {
                return false;
            }

            AddAssetHeader(node, guid, "ShaderSource", string.Empty, string.Empty);
            AddDiagnosticsCategory("Shader source is not indexed yet. Save or reimport the asset so a .meta GUID is registered.");
            return true;
        }

        if (!assetDatabase.TryGetAsset(guid, out var sourceAsset))
        {
            if (!looksLikeShader)
            {
                return false;
            }

            AddAssetHeader(node, guid, "ShaderSource", string.Empty, string.Empty);
            AddDiagnosticsCategory($"Runtime asset database has no record for shader GUID {guid}.");
            return true;
        }

        if (!string.Equals(sourceAsset.AssetType, ShaderAssetCooker.ShaderSourceAssetType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        AddAssetHeader(node, guid, sourceAsset.AssetType, sourceAsset.PackageId, sourceAsset.SourcePath);
        AddShaderActions(assetDatabase, sourceAsset);

        try
        {
            AddStandaloneShaderInspection(assetDatabase, sourceAsset);
            AddReferencingMaterialShaderInspection(assetDatabase, sourceAsset);
            AddShaderLogPathsCategory(sourceAsset);
        }
        catch (Exception ex)
        {
            AddDiagnosticsCategory(ex.Message);
        }

        return true;
    }

    private void AddAssetHeader(FileTreeNode node, Guid guid, string assetType, string packageId, string sourcePath)
    {
        var category = AddCategory("Asset");
        AddReadOnly(category, "Name", node.Name);
        AddReadOnly(category, "Guid", guid == Guid.Empty ? "<unindexed>" : guid.ToString());
        AddReadOnly(category, "Type", string.IsNullOrWhiteSpace(assetType) ? "<unknown>" : assetType);
        AddReadOnly(category, "Package", string.IsNullOrWhiteSpace(packageId) ? "<unknown>" : packageId);
        AddReadOnly(category, "Source", string.IsNullOrWhiteSpace(sourcePath) ? node.Path : sourcePath);
    }

    private void AddModelActions(IAssetDatabase assetDatabase, AssetRecord sourceAsset)
    {
        var category = AddCategory("Workflow");
        ICommand reimportCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            try
            {
                var result = await System.Threading.Tasks.Task.Run(() => ModelSourceReimporter.Reimport(sourceAsset));
                NotifyModelReimportChanges(assetDatabase, sourceAsset, result);
                m_LastModelReimportGuid = sourceAsset.Guid;
                m_LastModelReimportStatus =
                    $"Reimported {result.GeneratedChildGuids.Count} generated child asset(s). Orphans: {result.OrphanedGeneratedChildren.Count}. Output: {result.OutputRoot}";
                EditorLog.Info($"[ModelReimport] {m_LastModelReimportStatus}");
            }
            catch (Exception ex)
            {
                m_LastModelReimportGuid = sourceAsset.Guid;
                m_LastModelReimportStatus = $"Reimport failed: {ex.Message}";
                EditorLog.Error($"[ModelReimport] Failed to reimport '{sourceAsset.SourcePath}'.", ex);
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    RebuildProperties,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        });

        category.Properties.Add(new ActionPropertyItemViewModel(
            "Reimport",
            "Reimport",
            reimportCommand,
            category.CategoryName,
            "Regenerate model scene, mesh, material, and texture children under the configured package/workspace Assets output root."));
    }

    private void AddMaterialActions(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        string editDiagnostic)
    {
        var category = AddCategory("Workflow");
        AddReadOnly(category, "Source Editing", editDiagnostic);
        ICommand reloadCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                assetDatabase.InvalidateCookedAssets(sourceAsset.Guid);
                var cooked = MaterialAssetCooker.LoadOrCook(assetDatabase, sourceAsset.Guid);
                if (cooked.Handle.IsValid)
                {
                    assetDatabase.Release(cooked.Handle);
                }

                assetDatabase.NotifyAssetChanged(new AssetChangeEvent(
                    AssetChangeKind.Changed,
                    sourceAsset.Guid,
                    sourceAsset.AssetType,
                    sourceAsset.SourcePath,
                    string.Empty,
                    sourceAsset.PackageId));
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    RebuildProperties,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        });

        category.Properties.Add(new ActionPropertyItemViewModel(
            "Reload",
            "Reload / Recook",
            reloadCommand,
            category.CategoryName,
            "Invalidate cooked material data, recook the material asset, and notify runtime systems to reload it."));
    }

    private void AddShaderActions(IAssetDatabase assetDatabase, AssetRecord sourceAsset)
    {
        var category = AddCategory("Workflow");
        ICommand reloadCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                assetDatabase.InvalidateCookedAssets(sourceAsset.Guid);

                var cookTargets = BuildShaderCookTargets(assetDatabase, sourceAsset);
                for (int targetIndex = 0; targetIndex < cookTargets.Count; targetIndex++)
                {
                    var shader = cookTargets[targetIndex];
                    for (int stageIndex = 0; stageIndex < shader.Stages.Count; stageIndex++)
                    {
                        var cooked = ShaderAssetCooker.LoadOrCookStage(assetDatabase, shader, shader.Stages[stageIndex].Name);
                        if (cooked.Handle.IsValid)
                        {
                            assetDatabase.Release(cooked.Handle);
                        }
                    }
                }

                assetDatabase.NotifyAssetChanged(new AssetChangeEvent(
                    AssetChangeKind.Changed,
                    sourceAsset.Guid,
                    sourceAsset.AssetType,
                    sourceAsset.SourcePath,
                    string.Empty,
                    sourceAsset.PackageId));
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    RebuildProperties,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        });

        category.Properties.Add(new ActionPropertyItemViewModel(
            "Reload",
            "Reload / Recook",
            reloadCommand,
            category.CategoryName,
            "Invalidate cooked shader bytecode, recook inspectable variants, and notify runtime systems to reload it."));
    }

    private void AddMeshActions(IAssetDatabase assetDatabase, AssetRecord sourceAsset)
    {
        var category = AddCategory("Workflow");
        ICommand reloadCommand = ReactiveCommand.Create(() =>
        {
            try
            {
                if (!TryResolveMeshSourceFormat(sourceAsset.SourcePath, out var sourceFormat, out var formatDiagnostic))
                {
                    throw new NotSupportedException(formatDiagnostic);
                }

                assetDatabase.InvalidateCookedAssets(sourceAsset.Guid);
                var mesh = new MeshAsset(
                    sourceAsset.Guid,
                    Path.GetFileNameWithoutExtension(sourceAsset.SourcePath),
                    MeshVariantKey.Default,
                    sourceFormat);
                var cooked = MeshAssetCooker.LoadOrCook(assetDatabase, mesh);
                if (cooked.Handle.IsValid)
                {
                    assetDatabase.Release(cooked.Handle);
                }

                assetDatabase.NotifyAssetChanged(new AssetChangeEvent(
                    AssetChangeKind.Changed,
                    sourceAsset.Guid,
                    sourceAsset.AssetType,
                    sourceAsset.SourcePath,
                    string.Empty,
                    sourceAsset.PackageId));
            }
            finally
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    RebuildProperties,
                    Avalonia.Threading.DispatcherPriority.Background);
            }
        });

        category.Properties.Add(new ActionPropertyItemViewModel(
            "Reload",
            "Reload / Recook",
            reloadCommand,
            category.CategoryName,
            "Invalidate cooked mesh data, recook the mesh asset, and notify runtime systems to reload it."));
    }

    private static void NotifyModelReimportChanges(
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        ModelSourceReimportResult result)
    {
        if (assetDatabase is ArisenEngine.Core.Assets.AssetDatabase runtimeDatabase)
        {
            runtimeDatabase.RefreshDirectory(result.OutputRoot, sourceAsset.PackageId);
        }

        ModelSourceReimporter.InvalidateCookedOutputs(assetDatabase, sourceAsset, result);
    }

    private void AddStandaloneShaderInspection(IAssetDatabase assetDatabase, AssetRecord sourceAsset)
    {
        if (ShaderLabSource.IsShaderLabPath(sourceAsset.SourcePath))
        {
            var shaderLab = ShaderLabSource.Load(sourceAsset.SourcePath);
            var shader = BuildDefaultShaderLabAsset(sourceAsset, shaderLab);
            AddShaderSourceCategory("ShaderLab Source", shader, sourceAsset.SourcePath, "ShaderLab", shaderLab.CompileTimeKeywords);
            AddShaderStagesCategory(shader);
            AddMaterialContractCategory(shaderLab.MaterialContract);
            AddRenderStateCategory(shaderLab.RenderState);
            AddShaderCookDiagnosticsCategory(assetDatabase, shader, "Standalone");
            return;
        }

        var contract = ShaderMaterialContractAnnotations.ParseFile(sourceAsset.SourcePath);
        var category = AddCategory("Shader Source");
        AddReadOnly(category, "Name", Path.GetFileNameWithoutExtension(sourceAsset.SourcePath));
        AddReadOnly(category, "Guid", sourceAsset.Guid.ToString());
        AddReadOnly(category, "Source Format", "HLSL");
        AddReadOnly(category, "Stage Source", "Material asset metadata");
        AddReadOnly(category, "Cook Target", "Inspect a referencing material or add shader stage metadata to a material.");
        AddMaterialContractCategory(contract);
    }

    private void AddReferencingMaterialShaderInspection(IAssetDatabase assetDatabase, AssetRecord sourceAsset)
    {
        var materials = FindReferencingMaterials(assetDatabase, sourceAsset.Guid);
        var category = AddCategory("Material Users");
        if (materials.Count == 0)
        {
            AddReadOnly(category, "Materials", "<none>");
            return;
        }

        for (int i = 0; i < materials.Count; i++)
        {
            var material = materials[i];
            AddReadOnly(category, material.Name, $"{material.Guid} | {material.SourcePath}");
        }

        for (int i = 0; i < materials.Count; i++)
        {
            var material = materials[i];
            AddShaderSourceCategory($"Material Variant: {material.Name}", material.Shader, sourceAsset.SourcePath, "Material ShaderRef", null);
            AddShaderStagesCategory(material.Shader);
            AddShaderCookDiagnosticsCategory(assetDatabase, material.Shader, material.Name);
        }
    }

    private void AddMeshSummaryCategory(CookedMesh cooked)
    {
        var category = AddCategory("Mesh");
        AddReadOnly(category, "Name", cooked.Asset.Name);
        AddReadOnly(category, "Source Format", cooked.Asset.SourceFormat.ToString());
        AddReadOnly(category, "Variant", cooked.Variant);
        AddReadOnly(category, "Vertex Count", cooked.VertexCount);
        AddReadOnly(category, "Vertex Stride", cooked.VertexStride);
        AddReadOnly(category, "Index Count", cooked.IndexCount);
        AddReadOnly(category, "Index Format", cooked.IndexFormat.ToString());
        AddReadOnly(category, "Submesh Count", cooked.SubmeshCount);
        AddReadOnly(category, "Vertex Bytes", cooked.VertexDataSize);
        AddReadOnly(category, "Index Bytes", cooked.IndexDataSize);
    }

    private void AddMeshBoundsCategory(MeshBounds bounds)
    {
        var category = AddCategory("Bounds");
        AddReadOnly(category, "Min", FormatVector3(bounds.Min));
        AddReadOnly(category, "Max", FormatVector3(bounds.Max));
    }

    private void AddMeshSubmeshCategory(IReadOnlyList<MeshSubmesh> submeshes)
    {
        var category = AddCategory("Submeshes");
        if (submeshes.Count == 0)
        {
            AddReadOnly(category, "Submeshes", "<none>");
            return;
        }

        for (int i = 0; i < submeshes.Count; i++)
        {
            var submesh = submeshes[i];
            AddReadOnly(
                category,
                $"Submesh {i}",
                $"FirstIndex {submesh.FirstIndex} | IndexCount {submesh.IndexCount} | VertexOffset {submesh.VertexOffset} | MaterialSlot {submesh.MaterialSlot}");
        }
    }

    private void AddModelSourceCategory(AssetRecord sourceAsset, ModelSourceDescriptor model)
    {
        var category = AddCategory("Model Source");
        AddReadOnly(category, "Name", model.Name);
        AddReadOnly(category, "Model Guid", sourceAsset.Guid);
        AddReadOnly(category, "Package", sourceAsset.PackageId);
        AddReadOnly(category, "Source Path", model.SourcePath);
        AddReadOnly(category, "Resolved Source", model.ResolvedSourcePath);
        AddReadOnly(category, "Source Format", model.SourceFormat);
    }

    private void AddModelImportSettingsCategory(AssetRecord sourceAsset, ModelSourceDescriptor model)
    {
        var category = AddCategory("Model Import Settings");
        AddReadOnly(category, "Output Root", model.Import.OutputRoot);
        AddReadOnly(category, "Resolved Output Root", ModelSourceAssetLoader.ResolveOutputRoot(sourceAsset.SourcePath, model.Import.OutputRoot));
        AddReadOnly(category, "Scene Index", model.Import.SceneIndex);
        AddReadOnly(category, "Unit Scale", model.Import.UnitScale.ToString("0.###"));
        AddReadOnly(category, "Root Position", FormatVector3(model.Import.RootTransform.Position));
        AddReadOnly(category, "Root Rotation", FormatQuaternion(model.Import.RootTransform.Rotation));
        AddReadOnly(category, "Root Scale", FormatVector3(model.Import.RootTransform.Scale));
        AddReadOnly(category, "Emit Textures", model.Import.EmitTextures);
    }

    private void AddModelShaderCategory(ModelSourceDescriptor model)
    {
        var category = AddCategory("Generated Material Shader");
        AddReadOnly(category, "Shader", string.IsNullOrWhiteSpace(model.Shader.Name) ? "<unnamed>" : model.Shader.Name);
        AddReadOnly(category, "Guid", model.Shader.Guid);
    }

    private void AddModelReimportStatusCategory(Guid modelGuid)
    {
        if (m_LastModelReimportGuid != modelGuid || string.IsNullOrWhiteSpace(m_LastModelReimportStatus))
        {
            return;
        }

        var category = AddCategory("Model Reimport Status");
        AddReadOnly(category, "Last Result", m_LastModelReimportStatus);
    }

    private void AddModelGeneratedOutputCategory(
        AssetRecord sourceAsset,
        ModelSourceDescriptor model,
        GltfModelImportPlan plan)
    {
        var category = AddCategory("Generated Output");
        try
        {
            var inspection = ModelSourceReimporter.InspectGeneratedOutput(sourceAsset, model, plan);
            AddReadOnly(category, "Output Root", inspection.OutputRoot);
            AddReadOnly(category, "Orphans", inspection.OrphanedGeneratedChildren.Count);
            AddReadOnly(category, "Foreign", inspection.ForeignGeneratedChildren.Count);
            if (inspection.OrphanedGeneratedChildren.Count == 0 &&
                inspection.ForeignGeneratedChildren.Count == 0)
            {
                AddReadOnly(category, "Status", "Generated metadata matches the current import plan.");
                return;
            }

            for (int i = 0; i < inspection.OrphanedGeneratedChildren.Count; i++)
            {
                AddReadOnly(
                    category,
                    $"Orphan {i + 1}",
                    FormatGeneratedOutputDiagnostic(inspection.OrphanedGeneratedChildren[i]));
            }

            for (int i = 0; i < inspection.ForeignGeneratedChildren.Count; i++)
            {
                AddReadOnly(
                    category,
                    $"Foreign {i + 1}",
                    FormatGeneratedOutputDiagnostic(inspection.ForeignGeneratedChildren[i]));
            }
        }
        catch (Exception ex)
        {
            AddReadOnly(category, "Status", ex.Message);
        }
    }

    private void AddSceneSummaryCategory(SceneInspectionResult inspection)
    {
        var category = AddCategory("Scene");
        AddReadOnly(category, "Name", string.IsNullOrWhiteSpace(inspection.SceneName) ? Path.GetFileNameWithoutExtension(inspection.SourcePath) : inspection.SceneName);
        AddReadOnly(category, "Status", inspection.Success ? "Valid" : "Has diagnostics");
        AddReadOnly(category, "Entities", inspection.EntityCount);
        AddReadOnly(category, "Cameras", inspection.CameraCount);
        AddReadOnly(category, "Mesh Renderers", inspection.MeshRendererCount);
        AddReadOnly(category, "Directional Lights", inspection.DirectionalLightCount);
        AddReadOnly(category, "Point Lights", inspection.PointLightCount);
        AddReadOnly(category, "Spot Lights", inspection.SpotLightCount);
        AddReadOnly(category, "Environments", inspection.EnvironmentCount);
    }

    private void AddSceneEntitiesCategory(SceneInspectionResult inspection)
    {
        var category = AddCategory("Scene Entities");
        if (inspection.Entities.Count == 0)
        {
            AddReadOnly(category, "Entities", "<none>");
            return;
        }

        for (int i = 0; i < inspection.Entities.Count; i++)
        {
            var entity = inspection.Entities[i];
            AddReadOnly(
                category,
                entity.Name,
                $"{FormatSceneComponents(entity)} | Position {FormatVector3(entity.Transform.Position)} | Rotation {FormatQuaternion(entity.Transform.Rotation)} | Scale {FormatVector3(entity.Transform.Scale)}");
        }
    }

    private void AddSceneCameraCategory(SceneInspectionResult inspection)
    {
        var category = AddCategory("Scene Cameras");
        var count = 0;
        for (int i = 0; i < inspection.Entities.Count; i++)
        {
            var entity = inspection.Entities[i];
            if (entity.Camera == null)
            {
                continue;
            }

            count++;
            var camera = entity.Camera;
            AddReadOnly(
                category,
                entity.Name,
                $"{(camera.IsPerspective ? "Perspective" : "Orthographic")} | FOV {camera.VerticalFov:0.###} | Near {camera.NearPlane:0.###} | Far {camera.FarPlane:0.###}");
        }

        if (count == 0)
        {
            AddReadOnly(category, "Cameras", "<none>");
        }
    }

    private void AddSceneMeshRendererCategory(SceneInspectionResult inspection)
    {
        var category = AddCategory("Scene Mesh Renderers");
        var count = 0;
        for (int i = 0; i < inspection.Entities.Count; i++)
        {
            var entity = inspection.Entities[i];
            if (entity.MeshRenderer == null)
            {
                continue;
            }

            count++;
            var renderer = entity.MeshRenderer;
            AddReadOnly(
                category,
                entity.Name,
                $"Mesh {FormatSceneAssetRef(renderer.Mesh)} | Material {FormatSceneAssetRef(renderer.Material)} | FirstSubmesh {renderer.FirstSubmeshIndex} | SubmeshCount {renderer.SubmeshCount} | Visible {renderer.Visible} | Bounds {FormatVector3(renderer.BoundsCenter)} / {FormatVector3(renderer.BoundsExtents)}");
        }

        if (count == 0)
        {
            AddReadOnly(category, "Renderers", "<none>");
        }
    }

    private void AddSceneLightCategory(SceneInspectionResult inspection)
    {
        var category = AddCategory("Scene Lights");
        var count = 0;
        for (int i = 0; i < inspection.Entities.Count; i++)
        {
            var entity = inspection.Entities[i];
            if (entity.DirectionalLight != null)
            {
                count++;
                var light = entity.DirectionalLight;
                AddReadOnly(
                    category,
                    $"{entity.Name} Directional",
                    $"Direction {FormatVector3(light.Direction)} | Color {FormatVector3(light.Color)} | Intensity {light.Intensity:0.###} | Ambient {light.AmbientIntensity:0.###} | Enabled {light.Enabled}");
            }

            if (entity.PointLight != null)
            {
                count++;
                var light = entity.PointLight;
                AddReadOnly(
                    category,
                    $"{entity.Name} Point",
                    $"Color {FormatVector3(light.Color)} | Intensity {light.Intensity:0.###} | Range {light.Range:0.###} | Enabled {light.Enabled}");
            }

            if (entity.SpotLight != null)
            {
                count++;
                var light = entity.SpotLight;
                AddReadOnly(
                    category,
                    $"{entity.Name} Spot",
                    $"Color {FormatVector3(light.Color)} | Intensity {light.Intensity:0.###} | Range {light.Range:0.###} | Inner {light.InnerConeAngleDegrees:0.###} | Outer {light.OuterConeAngleDegrees:0.###} | Enabled {light.Enabled}");
            }
        }

        if (count == 0)
        {
            AddReadOnly(category, "Lights", "<none>");
        }
    }

    private void AddSceneEnvironmentCategory(SceneInspectionResult inspection)
    {
        var category = AddCategory("Scene Environments");
        var count = 0;
        for (int i = 0; i < inspection.Entities.Count; i++)
        {
            var entity = inspection.Entities[i];
            if (entity.Environment == null)
            {
                continue;
            }

            count++;
            var environment = entity.Environment;
            AddReadOnly(
                category,
                entity.Name,
                $"Texture {FormatSceneAssetRef(environment.EnvironmentTexture)} | Sky {FormatVector3(environment.SkyColor)} | Horizon {FormatVector3(environment.HorizonColor)} | Ground {FormatVector3(environment.GroundColor)} | Ambient {FormatVector3(environment.AmbientColor)} | SkyIntensity {environment.SkyIntensity:0.###} | AmbientIntensity {environment.AmbientIntensity:0.###} | Exposure {environment.Exposure:0.###} | Enabled {environment.Enabled}");
        }

        if (count == 0)
        {
            AddReadOnly(category, "Environments", "<none>");
        }
    }

    private void AddSceneDiagnosticsCategory(SceneInspectionResult inspection)
    {
        var category = AddCategory("Scene Diagnostics");
        if (inspection.Success)
        {
            AddReadOnly(category, "Status", "Scene inspection passed.");
            return;
        }

        if (inspection.Diagnostics.Count == 0)
        {
            AddReadOnly(category, "Status", "Scene inspection failed without a diagnostic.");
            return;
        }

        for (int i = 0; i < inspection.Diagnostics.Count; i++)
        {
            AddReadOnly(category, $"Diagnostic {i + 1}", inspection.Diagnostics[i]);
        }
    }

    private void AddShaderSourceCategory(
        string categoryName,
        ShaderAsset shader,
        string sourcePath,
        string sourceFormat,
        IReadOnlyList<string>? declaredKeywords)
    {
        var category = AddCategory(categoryName);
        AddReadOnly(category, "Name", shader.Name);
        AddReadOnly(category, "Guid", shader.Guid.ToString());
        AddReadOnly(category, "Source Format", sourceFormat);
        AddReadOnly(category, "Source", sourcePath);
        AddReadOnly(category, "Backend", shader.Variant.Backend.ToString());
        AddReadOnly(category, "Target Env", shader.Variant.TargetEnvironment);
        AddReadOnly(category, "Shader Model", shader.Variant.ShaderModel);
        AddReadOnly(category, "Optimization", shader.Variant.OptimizationLevel);
        AddReadOnly(category, "Debug Info", shader.Variant.DebugInfo ? "Enabled" : "Disabled");
        AddReadOnly(category, "Variant", shader.GetVariantIdentity());
        AddReadOnly(category, "Active Keywords", FormatList(shader.VariantKeywords));
        AddReadOnly(category, "Declared Keywords", FormatList(declaredKeywords));
        AddReadOnly(category, "Defines", FormatList(shader.Defines));
        AddReadOnly(category, "Includes", FormatList(shader.Includes));
    }

    private void AddShaderStagesCategory(ShaderAsset shader)
    {
        var category = AddCategory("Shader Stages");
        if (shader.Stages.Count == 0)
        {
            AddReadOnly(category, "Stages", "<none>");
            return;
        }

        for (int i = 0; i < shader.Stages.Count; i++)
        {
            var stage = shader.Stages[i];
            AddReadOnly(category, stage.Name, $"{stage.ProgramStage} | Entry {stage.EntryPoint} | Variant {shader.Variant.GetCookedVariant(stage.EntryPoint, shader.VariantKeywords)}");
        }
    }

    private void AddMaterialContractCategory(MaterialShaderContract contract)
    {
        var category = AddCategory("Material Contract");
        AddReadOnly(category, "Texture2D Refs", FormatList(contract.RequiredTexture2DRefs));
        AddReadOnly(category, "Scalar Properties", FormatList(contract.RequiredScalarProperties));
        AddReadOnly(category, "Vector4 Properties", FormatList(contract.RequiredVector4Properties));
    }

    private void AddShaderCookDiagnosticsCategory(IAssetDatabase assetDatabase, ShaderAsset shader, string label)
    {
        var category = AddCategory($"Cook Diagnostics: {label}");
        if (shader.Stages.Count == 0)
        {
            AddReadOnly(category, "Status", "No shader stages are available to cook.");
            return;
        }

        for (int i = 0; i < shader.Stages.Count; i++)
        {
            var stage = shader.Stages[i];
            CookedShaderStage cooked = default;
            try
            {
                cooked = ShaderAssetCooker.LoadOrCookStage(assetDatabase, shader, stage.Name);
                if (assetDatabase.TryGetCookedArtifact(shader.Guid, cooked.Variant, out var artifact))
                {
                    AddReadOnly(category, stage.Name, $"{cooked.Variant} | {artifact.SizeInBytes} bytes | {artifact.Path}");
                }
                else
                {
                    AddReadOnly(category, stage.Name, $"{cooked.Variant} | cooked artifact was not registered.");
                }
            }
            catch (Exception ex)
            {
                AddReadOnly(category, stage.Name, ex.Message);
            }
            finally
            {
                if (cooked.Handle.IsValid)
                {
                    assetDatabase.Release(cooked.Handle);
                }
            }
        }
    }

    private void AddShaderLogPathsCategory(AssetRecord sourceAsset)
    {
        var category = AddCategory("Shader Logs");
        var workspaceRoot = TryResolveWorkspaceRoot(sourceAsset.SourcePath);
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            AddReadOnly(category, "Editor Build Log", Path.Combine(workspaceRoot, ".arisen", "build_Editor.log"));
            AddReadOnly(category, "Runtime Logs", Path.Combine(workspaceRoot, ".arisen", "bin", "Editor", "Debug", "logs"));
            AddReadOnly(category, "Validation Logs", Path.Combine(workspaceRoot, ".arisen", "Logs"));
        }
        else
        {
            AddReadOnly(category, "Workspace", "Could not resolve workspace root from shader source path.");
        }

        var repositoryRoot = TryResolveRepositoryRoot(sourceAsset.SourcePath);
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            AddReadOnly(category, "Open Tracy", Path.Combine(repositoryRoot, "Arisen", "Scripts", "Windows", "open_tracy_profiler.bat"));
        }
    }

    private void AddShaderCategory(ShaderAsset shader, IAssetDatabase assetDatabase)
    {
        var category = AddCategory("Shader");
        AddReadOnly(category, "Name", shader.Name);
        AddReadOnly(category, "Guid", shader.Guid.ToString());
        AddReadOnly(category, "Variant", shader.GetVariantIdentity());
        AddReadOnly(category, "Keywords", FormatList(shader.VariantKeywords));
        AddReadOnly(category, "Defines", FormatList(shader.Defines));
        AddReadOnly(category, "Includes", FormatList(shader.Includes));

        if (assetDatabase.TryGetAsset(shader.Guid, out var shaderAsset))
        {
            AddReadOnly(category, "Source", shaderAsset.SourcePath);
        }

        for (int i = 0; i < shader.Stages.Count; i++)
        {
            var stage = shader.Stages[i];
            AddReadOnly(category, $"Stage {i}", $"{stage.Name} | {stage.ProgramStage} | {stage.EntryPoint}");
        }
    }

    private void AddTextureCategory(
        MaterialAsset material,
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        IReadOnlyList<MaterialTextureAssetOption> textureOptions,
        bool isReadOnly)
    {
        var category = AddCategory("Texture2D Refs");
        if (material.Texture2DRefs.Count == 0)
        {
            AddReadOnly(category, "Refs", "<none>");
            return;
        }

        for (int i = 0; i < material.Texture2DRefs.Count; i++)
        {
            var texture = material.Texture2DRefs[i];
            category.Properties.Add(new MaterialTexturePropertyViewModel(
                assetDatabase,
                sourceAsset,
                texture,
                textureOptions,
                isReadOnly));
        }
    }

    private void AddScalarCategory(
        MaterialAsset material,
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        bool isReadOnly)
    {
        var category = AddCategory("Scalar Properties");
        if (material.ScalarProperties.Count == 0)
        {
            AddReadOnly(category, "Properties", "<none>");
            return;
        }

        for (int i = 0; i < material.ScalarProperties.Count; i++)
        {
            var property = material.ScalarProperties[i];
            category.Properties.Add(new MaterialScalarPropertyViewModel(
                assetDatabase,
                sourceAsset,
                property,
                isReadOnly));
        }
    }

    private void AddVectorCategory(
        MaterialAsset material,
        IAssetDatabase assetDatabase,
        AssetRecord sourceAsset,
        bool isReadOnly)
    {
        var category = AddCategory("Vector4 Properties");
        if (material.Vector4Properties.Count == 0)
        {
            AddReadOnly(category, "Properties", "<none>");
            return;
        }

        for (int i = 0; i < material.Vector4Properties.Count; i++)
        {
            var property = material.Vector4Properties[i];
            category.Properties.Add(new MaterialVector4PropertyViewModel(
                assetDatabase,
                sourceAsset,
                property,
                isReadOnly));
        }
    }

    private void AddMaterialDiagnostics(MaterialAssetInspection inspection)
    {
        var category = AddCategory("Diagnostics");
        if (inspection.IsShaderContractValid)
        {
            AddReadOnly(category, "Status", "Material contract validation passed.");
            return;
        }

        AddReadOnly(
            category,
            "Status",
            $"Material shader contract has {inspection.ShaderContractDiagnostics.Count} missing binding(s).");
        for (var index = 0; index < inspection.ShaderContractDiagnostics.Count; index++)
        {
            var diagnostic = inspection.ShaderContractDiagnostics[index];
            AddReadOnly(
                category,
                $"{diagnostic.BindingKind}: {diagnostic.BindingName}",
                diagnostic.Message);
        }
    }

    private static IReadOnlyList<MaterialTextureAssetOption> BuildMaterialTextureOptions(
        IAssetDatabase assetDatabase)
    {
        var options = new List<MaterialTextureAssetOption>();
        foreach (var asset in assetDatabase.Assets)
        {
            if (!string.Equals(asset.AssetType, "Texture2D", StringComparison.OrdinalIgnoreCase) ||
                !TryResolveTextureSourceFormat(asset.SourcePath, out var sourceFormat))
            {
                continue;
            }

            var fileName = Path.GetFileNameWithoutExtension(asset.SourcePath);
            var logicalName = string.IsNullOrWhiteSpace(asset.PackageId)
                ? fileName
                : $"{asset.PackageId}/{fileName}";
            var displayName = string.IsNullOrWhiteSpace(asset.PackageId)
                ? Path.GetFileName(asset.SourcePath)
                : $"{Path.GetFileName(asset.SourcePath)} | {asset.PackageId}";
            options.Add(new MaterialTextureAssetOption(
                new MaterialTextureSourceReference(asset.Guid, logicalName, sourceFormat),
                displayName));
        }

        options.Sort((left, right) => string.Compare(
            left.DisplayName,
            right.DisplayName,
            StringComparison.OrdinalIgnoreCase));
        return options;
    }

    private static bool TryResolveTextureSourceFormat(
        string sourcePath,
        out Texture2DSourceFormat sourceFormat)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.Equals(extension, ".ppm", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = Texture2DSourceFormat.PpmP3;
            return true;
        }

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = Texture2DSourceFormat.ImageFile;
            return true;
        }

        sourceFormat = default;
        return false;
    }

    private void AddRenderStateCategory(MaterialRenderState renderState)
    {
        var category = AddCategory("Render State");
        AddReadOnly(category, "Cull Mode", renderState.CullMode.ToString());
        AddReadOnly(category, "Front Face", renderState.FrontFace.ToString());
        AddReadOnly(category, "Blend", renderState.BlendEnabled ? "Enabled" : "Disabled");
        AddReadOnly(category, "Src Color", renderState.SrcColorBlendFactor.ToString());
        AddReadOnly(category, "Dst Color", renderState.DstColorBlendFactor.ToString());
        AddReadOnly(category, "Color Op", renderState.ColorBlendOp.ToString());
    }

    private void AddGltfModelImportDiagnostics(AssetRecord sourceAsset)
    {
        if (!IsGltfSourcePath(sourceAsset.SourcePath))
        {
            return;
        }

        try
        {
            var plan = GltfModelImportPlanner.CreatePlan(sourceAsset.SourcePath, sourceAsset.Guid, sourceAsset.PackageId);
            AddGltfModelSummaryCategory(plan);
            AddGltfGeneratedChildrenCategory(plan);
            AddGltfMaterialPreviewCategory(plan);
            AddGltfTextureRefCategory(plan);
            AddGltfWarningsCategory(plan);
        }
        catch (Exception ex)
        {
            var category = AddCategory("Model Import");
            AddReadOnly(category, "Status", ex.Message);
        }
    }

    private void AddGltfModelSummaryCategory(GltfModelImportPlan plan)
    {
        var category = AddCategory("Model Import");
        AddReadOnly(category, "Source Guid", plan.SourceGuid);
        AddReadOnly(category, "Package", plan.PackageId);
        AddReadOnly(category, "Scenes", CountGeneratedChildren(plan, "scene"));
        AddReadOnly(category, "Meshes", CountGeneratedChildren(plan, "mesh"));
        AddReadOnly(category, "Materials", plan.Materials.Count);
        AddReadOnly(category, "Images", CountGeneratedChildren(plan, "texture2d"));
        AddReadOnly(category, "Texture Refs", CountGltfTextureRefs(plan));
        AddReadOnly(category, "Warnings", plan.Warnings.Count);
    }

    private void AddGltfGeneratedChildrenCategory(GltfModelImportPlan plan)
    {
        var category = AddCategory("Generated Children");
        if (plan.GeneratedChildren.Count == 0)
        {
            AddReadOnly(category, "Children", "<none>");
            return;
        }

        for (int i = 0; i < plan.GeneratedChildren.Count; i++)
        {
            var child = plan.GeneratedChildren[i];
            AddReadOnly(
                category,
                $"{child.Kind} {i}",
                FormatGeneratedChild(child));
        }
    }

    private void AddGltfMaterialPreviewCategory(GltfModelImportPlan plan)
    {
        var category = AddCategory("Imported Materials");
        if (plan.Materials.Count == 0)
        {
            AddReadOnly(category, "Materials", "<none>");
            return;
        }

        for (int i = 0; i < plan.Materials.Count; i++)
        {
            var material = plan.Materials[i];
            var name = string.IsNullOrWhiteSpace(material.Name) ? $"Material {i}" : material.Name;
            AddReadOnly(
                category,
                name,
                $"Guid {material.Guid} | BaseColor {FormatVector4(material.BaseColorFactor)} | Metallic {material.MetallicFactor:0.###} | Roughness {material.RoughnessFactor:0.###} | Occlusion {material.OcclusionStrength:0.###} | Alpha {material.AlphaMode} ({material.AlphaCutoff:0.###})");
        }
    }

    private void AddGltfTextureRefCategory(GltfModelImportPlan plan)
    {
        var category = AddCategory("Imported Texture Refs");
        var count = 0;
        for (int i = 0; i < plan.Materials.Count; i++)
        {
            var material = plan.Materials[i];
            if (material.BaseColorTexture != null)
            {
                AddReadOnly(category, $"Material {i} BaseColor", FormatGltfTextureRef(material.BaseColorTexture));
                count++;
            }

            if (material.NormalTexture != null)
            {
                AddReadOnly(category, $"Material {i} Normal", FormatGltfTextureRef(material.NormalTexture));
                count++;
            }

            if (material.EmissiveTexture != null)
            {
                AddReadOnly(category, $"Material {i} Emissive", FormatGltfTextureRef(material.EmissiveTexture));
                count++;
            }

            if (material.MetallicRoughnessTexture != null)
            {
                AddReadOnly(category, $"Material {i} Metallic/Roughness", FormatGltfTextureRef(material.MetallicRoughnessTexture));
                count++;
            }

            if (material.OcclusionTexture != null)
            {
                AddReadOnly(category, $"Material {i} Occlusion", FormatGltfTextureRef(material.OcclusionTexture));
                count++;
            }
        }

        if (count == 0)
        {
            AddReadOnly(category, "Texture Refs", "<none>");
        }
    }

    private void AddGltfWarningsCategory(GltfModelImportPlan plan)
    {
        var category = AddCategory("Model Import Warnings");
        if (plan.Warnings.Count == 0)
        {
            AddReadOnly(category, "Warnings", "<none>");
            return;
        }

        for (int i = 0; i < plan.Warnings.Count; i++)
        {
            AddReadOnly(category, $"Warning {i + 1}", plan.Warnings[i]);
        }
    }

    private void AddDiagnosticsCategory(string message)
    {
        var category = AddCategory("Diagnostics");
        AddReadOnly(category, "Status", message);
    }

    private static ShaderAsset BuildDefaultShaderLabAsset(AssetRecord sourceAsset, ShaderLabSource shaderLab)
    {
        return new ShaderAsset(
            sourceAsset.Guid,
            shaderLab.Name,
            shaderLab.BuildStages(),
            ShaderVariantKey.VulkanDebug,
            null,
            shaderLab.Includes,
            null);
    }

    private static List<ShaderAsset> BuildShaderCookTargets(IAssetDatabase assetDatabase, AssetRecord sourceAsset)
    {
        var targets = new List<ShaderAsset>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (ShaderLabSource.IsShaderLabPath(sourceAsset.SourcePath))
        {
            AddShaderCookTarget(targets, seen, BuildDefaultShaderLabAsset(sourceAsset, ShaderLabSource.Load(sourceAsset.SourcePath)));
        }

        var materials = FindReferencingMaterials(assetDatabase, sourceAsset.Guid);
        for (int i = 0; i < materials.Count; i++)
        {
            AddShaderCookTarget(targets, seen, materials[i].Shader);
        }

        return targets;
    }

    private static void AddShaderCookTarget(List<ShaderAsset> targets, HashSet<string> seen, ShaderAsset shader)
    {
        var key = $"{shader.GetVariantIdentity()}|{FormatList(shader.Defines)}|{FormatList(shader.Includes)}|{FormatStageList(shader.Stages)}";
        if (seen.Add(key))
        {
            targets.Add(shader);
        }
    }

    private static List<ReferencingMaterialShader> FindReferencingMaterials(IAssetDatabase assetDatabase, Guid shaderGuid)
    {
        var result = new List<ReferencingMaterialShader>();
        foreach (var asset in assetDatabase.Assets)
        {
            if (!string.Equals(asset.AssetType, "Material", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var material = MaterialAssetLoader.LoadSource(assetDatabase, asset.Guid);
                if (material.Shader.Guid == shaderGuid)
                {
                    result.Add(new ReferencingMaterialShader(asset.Guid, material.Name, asset.SourcePath, material.Shader));
                }
            }
            catch
            {
                // Invalid materials surface their own diagnostics when selected.
            }
        }

        return result;
    }

    private InspectorCategoryViewModel AddCategory(string name)
    {
        var category = new InspectorCategoryViewModel(name);
        Categories.Add(category);
        return category;
    }

    private static void AddReadOnly(InspectorCategoryViewModel category, string name, object? value, string description = "")
    {
        category.Properties.Add(new ReadOnlyPropertyItemViewModel(
            name,
            value?.ToString() ?? string.Empty,
            category.CategoryName,
            description));
    }

    private static string FormatList(IReadOnlyList<string>? values)
    {
        return values == null || values.Count == 0
            ? "<none>"
            : string.Join(", ", values);
    }

    private static string FormatStageList(IReadOnlyList<ShaderStageAsset> stages)
    {
        if (stages.Count == 0)
        {
            return string.Empty;
        }

        var values = new string[stages.Count];
        for (int i = 0; i < stages.Count; i++)
        {
            var stage = stages[i];
            values[i] = $"{stage.Name}:{stage.ProgramStage}:{stage.EntryPoint}";
        }

        return string.Join("|", values);
    }

    private static bool IsKnownMeshExtension(string extension)
    {
        return string.Equals(extension, ".armesh", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".obj", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownModelExtension(string extension)
    {
        return string.Equals(extension, ".arismodel", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".model", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownSceneExtension(string extension)
    {
        return string.Equals(extension, ".arisenscene", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".scene", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGltfSourcePath(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        return string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownShaderExtension(string extension)
    {
        return string.Equals(extension, ".shader", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".hlsl", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryResolveMeshSourceFormat(string sourcePath, out MeshSourceFormat sourceFormat, out string diagnostic)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.Equals(extension, ".obj", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = MeshSourceFormat.WavefrontObj;
            diagnostic = string.Empty;
            return true;
        }

        if (string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = MeshSourceFormat.GltfJson;
            diagnostic = string.Empty;
            return true;
        }

        if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = MeshSourceFormat.GltfBinary;
            diagnostic = string.Empty;
            return true;
        }

        if (string.Equals(extension, ".armesh", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = MeshSourceFormat.ArisenTextMesh;
            diagnostic = string.Empty;
            return true;
        }

        sourceFormat = MeshSourceFormat.ArisenTextMesh;
        diagnostic = $"Mesh source format '{extension}' is indexed but not supported by the runtime mesh cooker yet.";
        return false;
    }

    private static string TryResolveWorkspaceRoot(string sourcePath)
    {
        var directory = GetExistingDirectory(sourcePath);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "manifest.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Local")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return string.Empty;
    }

    private static string TryResolveRepositoryRoot(string sourcePath)
    {
        var directory = GetExistingDirectory(sourcePath);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Arisen", "Scripts", "Windows", "open_tracy_profiler.bat")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return string.Empty;
    }

    private static DirectoryInfo? GetExistingDirectory(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var path = File.Exists(sourcePath)
            ? Path.GetDirectoryName(sourcePath)
            : sourcePath;

        return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)
            ? null
            : new DirectoryInfo(path);
    }

    private static string FormatVector3(System.Numerics.Vector3 value)
    {
        return $"{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}";
    }

    private static string FormatVector4(System.Numerics.Vector4 value)
    {
        return $"{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}, {value.W:0.###}";
    }

    private static string FormatQuaternion(System.Numerics.Quaternion value)
    {
        return $"{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}, {value.W:0.###}";
    }

    private static string FormatSceneComponents(SceneEntityInspection entity)
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

    private static string FormatSceneAssetRef(SceneAssetReferenceInspection assetRef)
    {
        if (!assetRef.HasValue)
        {
            return "<none>";
        }

        var source = string.IsNullOrWhiteSpace(assetRef.SourcePath)
            ? "<unresolved>"
            : assetRef.SourcePath;
        var status = assetRef.IsResolved
            ? assetRef.ActualAssetType
            : assetRef.Diagnostic;
        return $"{assetRef.Guid} | {status} | {source}";
    }

    private static string FormatGltfTextureRef(GltfImportedTextureRef textureRef)
    {
        string source;
        if (!string.IsNullOrWhiteSpace(textureRef.Uri) &&
            textureRef.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            source = $"embedded data URI ({textureRef.MimeType ?? "unknown MIME"})";
        }
        else if (!string.IsNullOrWhiteSpace(textureRef.Uri))
        {
            source = textureRef.Uri;
        }
        else if (textureRef.BufferView >= 0)
        {
            source = $"bufferView {textureRef.BufferView} ({textureRef.MimeType ?? "unknown MIME"})";
        }
        else
        {
            source = "<unresolved>";
        }

        var sampler = textureRef.Sampler;
        var transform = textureRef.Transform;
        return $"Texture {textureRef.TextureIndex} | Image {textureRef.ImageIndex} | {source} | Filter {sampler.MinFilter}/{sampler.MagFilter}/{sampler.MipmapMode} | Wrap {sampler.WrapU}/{sampler.WrapV} | UV {transform.TexCoord} | Offset ({transform.Offset.X:0.###}, {transform.Offset.Y:0.###}) | Scale ({transform.Scale.X:0.###}, {transform.Scale.Y:0.###}) | Rotation {transform.Rotation:0.###}";
    }

    private static string FormatGeneratedChild(GltfGeneratedChildAsset child)
    {
        var generated = child.Metadata.Generated;
        if (generated == null)
        {
            return $"{child.Key} | {child.Metadata.AssetType} | {child.Metadata.Guid}";
        }

        return $"{child.Key} | {child.Metadata.AssetType} | {child.Metadata.Guid} | {generated.GeneratedByImporter} | Source {generated.SourceGuid}";
    }

    private static string FormatGeneratedOutputDiagnostic(ModelGeneratedOutputDiagnostic diagnostic)
    {
        return $"{diagnostic.ChildKind}:{diagnostic.ChildKey} | {diagnostic.AssetType} | {diagnostic.Guid} | Source {diagnostic.SourceGuid} | {diagnostic.MetaPath} | {diagnostic.Message}";
    }

    private static int CountGltfTextureRefs(GltfModelImportPlan plan)
    {
        var count = 0;
        for (int i = 0; i < plan.Materials.Count; i++)
        {
            if (plan.Materials[i].BaseColorTexture != null)
            {
                count++;
            }

            if (plan.Materials[i].NormalTexture != null)
            {
                count++;
            }

            if (plan.Materials[i].EmissiveTexture != null)
            {
                count++;
            }

            if (plan.Materials[i].MetallicRoughnessTexture != null)
            {
                count++;
            }

            if (plan.Materials[i].OcclusionTexture != null)
            {
                count++;
            }
        }

        return count;
    }

    private static int CountGeneratedChildren(GltfModelImportPlan plan, string kind)
    {
        var count = 0;
        for (int i = 0; i < plan.GeneratedChildren.Count; i++)
        {
            if (string.Equals(plan.GeneratedChildren[i].Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }

        return count;
    }

    private sealed record ReferencingMaterialShader(
        Guid Guid,
        string Name,
        string SourcePath,
        ShaderAsset Shader);
}
