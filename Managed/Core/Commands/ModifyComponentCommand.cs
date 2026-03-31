using ArisenEngine.Core.ECS;
using ArisenEditor.Core.Services;
using ArisenKernel.Contracts;
using ArisenEngine.Core.Automation;
using System;

namespace ArisenEditor.Core.Commands;

/// <summary>
/// Command to modify any ECS component via its IComponentPool.
/// Stores boxed versions of the old and new component data for undo/redo.
/// </summary>
public class ModifyComponentCommand : ICommand
{
    private readonly Entity m_Entity;
    private readonly IComponentPool m_Pool;
    private readonly object m_OldComponent;
    private readonly object m_NewComponent;
    private readonly Type m_ComponentType;

    public string Description => $"Modify {m_ComponentType.Name} on Entity {m_Entity.Id}";

    public ModifyComponentCommand(Entity entity, IComponentPool pool, object oldComponent, object newComponent)
    {
        m_Entity = entity;
        m_Pool = pool;
        m_OldComponent = oldComponent;
        m_NewComponent = newComponent;
        m_ComponentType = pool.GetComponentType();
    }

    public void Execute()
    {
        SetComponent(m_NewComponent);
    }

    public void Undo()
    {
        SetComponent(m_OldComponent);
    }

    private void SetComponent(object componentData)
    {
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene == null) return;

        // Apply to the ECS pool
        m_Pool.SetBoxed(m_Entity, componentData);

        // Notify systems
        if (m_ComponentType == typeof(NameComponent))
        {
            var nameComp = (NameComponent)componentData;
            SceneManagerService.Instance.NotifyEntityNameChanged(m_Entity, nameComp.Name);
        }
        else
        {
            SceneManagerService.Instance.NotifyEntityComponentChanged(m_Entity, m_ComponentType);
        }
    }
}
