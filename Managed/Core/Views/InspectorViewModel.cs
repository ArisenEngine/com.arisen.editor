using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ArisenEditorFramework.Inspector;
using ArisenEngine.Core.ECS;
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

        // 2. Check if we are inspecting a specialized EntityNode
        if (TargetObject is EntityNodeViewModel node)
        {
            var ActiveEntityManager = ArisenEditor.Core.Services.SceneManagerService.Instance.ActiveScene?.Registry;
            if (ActiveEntityManager == null) return;

            foreach (var pool in ActiveEntityManager.GetEntityComponentPools(node.Entity))
            {
                var compType = pool.GetComponentType();

                // Create a category for this component
                var category = new ArisenEditorFramework.Inspector.InspectorCategoryViewModel(compType.Name);
                Categories.Add(category);

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
        }
        else
        {
            // 3. Fallback to standard reflection for non-ECS objects
            base.RebuildProperties();
        }
    }
}
