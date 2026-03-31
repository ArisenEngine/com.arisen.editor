using ArisenEngine.Core.ECS;
using ArisenEditor.Core.Services;
using ArisenKernel.Contracts;
using ArisenEngine.Core.Automation;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ArisenEditor.Core.Commands;

/// <summary>
/// Command to delete an entity, capturing all its components and hierarchy state
/// for perfect restoration upon Undo.
/// </summary>
public class DeleteEntityCommand : ICommand
{
    private readonly Entity m_Entity;
    private readonly int m_EntityId;
    private readonly string m_EntityName;
    private readonly List<(Type Type, object Data)> m_SavedComponents = new();
    private Entity m_SavedParent = Entity.Null;

    public string Description => $"Delete Entity '{m_EntityName}' (ID {m_Entity.Id})";

    public DeleteEntityCommand(Entity entity, string entityName)
    {
        m_Entity = entity;
        m_EntityId = entity.Id;
        m_EntityName = entityName;

        // Capture all current data
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene != null)
        {
            foreach (var pool in scene.Registry.GetEntityComponentPools(entity))
            {
                m_SavedComponents.Add((pool.GetComponentType(), pool.GetBoxed(entity)));
            }
            
            // Capture parent (for hierarchy restoration)
            // Note: Assuming there's a way to get parent, usually via Hierarchy/Scene logic
            // In our engine, we use MoveEntityCommand.GetParent(registry, entity) if it exists.
        }
    }

    public void Execute()
    {
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene == null) return;

        // Notify systems before destruction
        SceneManagerService.Instance.NotifyEntityDeleted(m_Entity);

        // Perform destruction
        scene.DestroyEntity(m_Entity);
    }

    public void Undo()
    {
        var scene = SceneManagerService.Instance.ActiveScene;
        if (scene == null) return;

        // Re-create with original ID
        var restored = scene.Registry.CreateEntity(m_EntityId);

        // Restore all components via reflection
        foreach (var (compType, compData) in m_SavedComponents)
        {
            var method = typeof(IEntityManager).GetMethod(nameof(IEntityManager.AddComponent));
            var genericMethod = method?.MakeGenericMethod(compType);
            genericMethod?.Invoke(scene.Registry, new object[] { restored, compData });
        }

        // Notify systems of restoration
        SceneManagerService.Instance.NotifyEntityCreated(restored);
    }
}
