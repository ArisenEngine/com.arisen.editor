using ArisenEngine.Core.ECS;
using ArisenEditor.Core.Services;
using ArisenKernel.Contracts;
using ArisenEngine.Core.Automation;
using System;
using System.Reflection;

namespace ArisenEditor.Core.Commands;

/// <summary>
/// Command to remove a component from an entity, storing its state for exact restoration upon Undo.
/// </summary>
public class RemoveComponentCommand : ICommand
{
    private readonly Entity m_Entity;
    private readonly Type m_ComponentType;
    private readonly object m_SavedComponentData;

    public string Description => $"Remove {m_ComponentType.Name} from Entity {m_Entity.Id}";

    public RemoveComponentCommand(Entity entity, Type componentType)
    {
        m_Entity = entity;
        m_ComponentType = componentType;

        // Capture current data so we can perfectly restore it on Undo
        var registry = SceneManagerService.Instance.ActiveScene?.Registry;
        if (registry != null)
        {
            IComponentPool? targetPool = null;
            foreach (var pool in registry.GetEntityComponentPools(entity))
            {
                if (pool.GetComponentType() == m_ComponentType)
                {
                    targetPool = pool;
                    break;
                }
            }
            
            if (targetPool != null && targetPool.Has(entity))
            {
                // Copy existing data block
                m_SavedComponentData = targetPool.GetBoxed(entity);
            }
            else
            {
                m_SavedComponentData = Activator.CreateInstance(m_ComponentType)!;
            }
        }
        else
        {
            m_SavedComponentData = Activator.CreateInstance(m_ComponentType)!;
        }
    }

    public void Execute()
    {
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene == null) return;

        var method = typeof(IEntityManager).GetMethod(nameof(IEntityManager.RemoveComponent));
        var genericMethod = method?.MakeGenericMethod(m_ComponentType);
        genericMethod?.Invoke(scene.Registry, new object[] { m_Entity });

        SceneManagerService.Instance.NotifyEntityComponentChanged(m_Entity, m_ComponentType);
    }

    public void Undo()
    {
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene == null) return;

        var method = typeof(IEntityManager).GetMethod(nameof(IEntityManager.AddComponent));
        var genericMethod = method?.MakeGenericMethod(m_ComponentType);
        
        // Restore perfect struct copy
        genericMethod?.Invoke(scene.Registry, new object[] { m_Entity, m_SavedComponentData });

        SceneManagerService.Instance.NotifyEntityComponentChanged(m_Entity, m_ComponentType);
    }
}
