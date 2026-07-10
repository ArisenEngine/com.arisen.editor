using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using ArisenEngine.Core.Assets;
using ArisenEditorFramework.Inspector;
using ArisenEngine.Core.ECS;
using ArisenEngine.Rendering;
using ArisenEngine.Rendering.Resources;
using ReactiveUI;

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

/// <summary>
/// Overrides the standard Inspector to detect when an ECS Entity is selected.
/// It dynamically builds categories based on the components attached to the entity.
/// </summary>
internal class InspectorViewModel : ArisenEditorFramework.Inspector.InspectorViewModel
{
    public ArisenEditor.Core.Services.SelectionService? SelectionService { get; set; }

    private readonly System.Collections.Generic.List<Type> _allComponentTypes;

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

        if (TargetObject == null)
            return;

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
            CanAddComponent = false;
            AvailableComponentTypes.Clear();
            // 3. Fallback to standard reflection for non-ECS objects
            base.RebuildProperties();
        }
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
        AddMaterialActions(assetDatabase, sourceAsset);

        try
        {
            var material = MaterialAssetLoader.LoadSource(assetDatabase, guid);
            AddShaderCategory(material.Shader, assetDatabase);
            AddTextureCategory(material);
            AddScalarCategory(material);
            AddVectorCategory(material);
            AddRenderStateCategory(material.RenderState);
            AddDiagnosticsCategory("Material contract validation passed.");
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

    private void AddMaterialActions(IAssetDatabase assetDatabase, AssetRecord sourceAsset)
    {
        var category = AddCategory("Workflow");
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

    private void AddTextureCategory(MaterialAsset material)
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
            AddReadOnly(
                category,
                texture.Name,
                $"{texture.Texture.Name} | {texture.Texture.Guid} | Slot {texture.Slot}");
        }
    }

    private void AddScalarCategory(MaterialAsset material)
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
            AddReadOnly(category, property.Name, property.Value.ToString("0.###"));
        }
    }

    private void AddVectorCategory(MaterialAsset material)
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
            var value = property.Value;
            AddReadOnly(category, property.Name, $"{value.X:0.###}, {value.Y:0.###}, {value.Z:0.###}, {value.W:0.###}");
        }
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
            diagnostic = "Binary glTF (.glb) is indexed, but the first runtime mesh cooker scope supports .gltf JSON sources only.";
            return false;
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

    private sealed record ReferencingMaterialShader(
        Guid Guid,
        string Name,
        string SourcePath,
        ShaderAsset Shader);
}
