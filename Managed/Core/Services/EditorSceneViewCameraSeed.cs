using System.Numerics;
using ArisenEngine.Core.ECS;
using ArisenEngine.Core.Math;
using ArisenEngine.ECS.Lifecycle;
using ArisenEngine.Rendering;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Lifecycle;

namespace ArisenEditor.Core.Services;

/// <summary>
/// Reads the first usable scene camera from the active ECS scene. This is the pose the scene view
/// already presents when no override is installed, so interactive navigation starts exactly where
/// the user is looking instead of snapping to an invented default.
/// </summary>
internal static class EditorSceneViewCameraSeed
{
    public static bool TryReadSceneCamera(out SceneViewCameraOverride camera)
    {
        camera = null!;
        if (!EngineKernel.IsCreated)
        {
            return false;
        }

        EntityManager? entityManager = EngineKernel.Instance
            .GetSubsystem<SceneSubsystem>()?
            .ActiveEntityManager;
        if (entityManager == null)
        {
            return false;
        }

        ComponentPool<CameraComponent> cameras = entityManager.GetPool<CameraComponent>();
        ComponentPool<TransformComponent> transforms = entityManager.GetPool<TransformComponent>();
        if (cameras.Count == 0 || transforms.Count == 0)
        {
            return false;
        }

        Entity[] cameraEntities = cameras.GetRawEntityArray();
        for (int index = 0; index < cameras.Count; index++)
        {
            Entity entity = cameraEntities[index];
            if (!transforms.Has(entity))
            {
                continue;
            }

            ref CameraComponent cameraComponent = ref cameras.GetRef(entity);
            if (cameraComponent.IsPerspective == 0)
            {
                continue;
            }

            ref TransformComponent transform = ref transforms.GetRef(entity);
            if (!IsFinite(transform.Position) || !IsFinite(transform.Rotation))
            {
                continue;
            }

            var candidate = new SceneViewCameraOverride(
                ResolveWorldPosition(transform.Position),
                transform.Rotation.QuaternionToEulerDegrees(),
                cameraComponent.VerticalFov,
                cameraComponent.NearPlane,
                cameraComponent.FarPlane);
            if (!candidate.IsValid)
            {
                continue;
            }

            camera = candidate;
            return true;
        }

        return false;
    }

    private static WorldPosition ResolveWorldPosition(Vector3 originRelativePosition)
    {
        if (EngineKernel.Instance.Services.TryGetService<IWorldOriginService>(
                out IWorldOriginService? origin) &&
            origin != null)
        {
            WorldPosition worldPosition = origin.ToWorld(originRelativePosition);
            if (worldPosition.IsFinite)
            {
                return worldPosition;
            }
        }

        return new WorldPosition(
            originRelativePosition.X,
            originRelativePosition.Y,
            originRelativePosition.Z);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
