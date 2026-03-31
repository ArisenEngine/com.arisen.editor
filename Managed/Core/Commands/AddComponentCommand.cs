using ArisenEngine.Core.ECS;
using ArisenEditor.Core.Services;
using ArisenKernel.Contracts;
using ArisenEngine.Core.Automation;
using System;
using System.Reflection;

namespace ArisenEditor.Core.Commands;

/// <summary>
/// Command to add a new component to an entity by its Type.
/// </summary>
public class AddComponentCommand : ICommand
{
    private readonly Entity m_Entity;
    private readonly Type m_ComponentType;

    public string Description => $"Add {m_ComponentType.Name} to Entity {m_Entity.Id}";

    public AddComponentCommand(Entity entity, Type componentType)
    {
        m_Entity = entity;
        m_ComponentType = componentType;
    }

    public void Execute()
    {
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene == null) return;

        var method = typeof(IEntityManager).GetMethod(nameof(IEntityManager.AddComponent));
        var genericMethod = method?.MakeGenericMethod(m_ComponentType);
        
        // Use Activator to create default struct
        genericMethod?.Invoke(scene.Registry, new object[] { m_Entity, Activator.CreateInstance(m_ComponentType)! });

        SceneManagerService.Instance.NotifyEntityComponentChanged(m_Entity, m_ComponentType);
    }

    public void Undo()
    {
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene == null) return;

        var method = typeof(IEntityManager).GetMethod(nameof(IEntityManager.RemoveComponent));
        var genericMethod = method?.MakeGenericMethod(m_ComponentType);
        genericMethod?.Invoke(scene.Registry, new object[] { m_Entity });

        SceneManagerService.Instance.NotifyEntityComponentChanged(m_Entity, m_ComponentType);
    }
}
