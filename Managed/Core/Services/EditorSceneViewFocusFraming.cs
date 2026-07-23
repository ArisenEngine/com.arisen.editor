using System.Numerics;
using ArisenEngine.Rendering;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Resources.Serialization;

namespace ArisenEditor.Core.Services;

internal readonly record struct EditorSceneViewFocusFrame(
    WorldBounds Bounds,
    SceneViewCameraOverride Camera,
    bool UsesMeshBounds);

internal static class EditorSceneViewFocusFraming
{
    private const float DefaultFieldOfView = 50.0f;
    private const float BoundsEpsilon = 1.0e-5f;
    private const double FramePadding = 1.15;

    private static readonly Vector3 s_ViewDirection = Vector3.Normalize(
        new Vector3(0.55f, -0.35f, 0.76f));

    public static bool TryCreate(
        WorldPartitionSettings partition,
        WorldCellDescriptor cell,
        SceneInspectionResult inspection,
        IReadOnlyDictionary<Guid, MeshBounds> authoritativeMeshBounds,
        out EditorSceneViewFocusFrame frame)
    {
        ArgumentNullException.ThrowIfNull(partition);
        ArgumentNullException.ThrowIfNull(cell);
        ArgumentNullException.ThrowIfNull(authoritativeMeshBounds);

        bool usesMeshBounds = false;
        WorldBounds bounds;
        if (cell.FocusBounds is WorldBounds focusBounds)
        {
            bounds = focusBounds;
        }
        else if (!TryGetMeshBounds(
                     partition,
                     cell,
                     inspection,
                     authoritativeMeshBounds,
                     out bounds))
        {
            bounds = cell.Bounds;
        }
        else
        {
            usesMeshBounds = true;
        }

        if (!bounds.IsValid || !TryCreateCamera(bounds, out SceneViewCameraOverride camera))
        {
            frame = default;
            return false;
        }

        frame = new EditorSceneViewFocusFrame(bounds, camera, usesMeshBounds);
        return true;
    }

    private static bool TryGetMeshBounds(
        WorldPartitionSettings partition,
        WorldCellDescriptor cell,
        SceneInspectionResult inspection,
        IReadOnlyDictionary<Guid, MeshBounds> authoritativeMeshBounds,
        out WorldBounds bounds)
    {
        Vector3 localMin = new(float.PositiveInfinity);
        Vector3 localMax = new(float.NegativeInfinity);
        bool found = false;

        IReadOnlyList<SceneEntityInspection>? entities = inspection.Entities;
        if (inspection.Success && entities != null)
        {
            for (int index = 0; index < entities.Count; index++)
            {
                SceneEntityInspection entity = entities[index];
                if (entity.MeshRenderer is not { Visible: true } mesh ||
                    !TryGetMeshBounds(
                        entity.Transform,
                        mesh,
                        authoritativeMeshBounds,
                        out Vector3 meshMin,
                        out Vector3 meshMax))
                {
                    continue;
                }

                localMin = Vector3.Min(localMin, meshMin);
                localMax = Vector3.Max(localMax, meshMax);
                found = true;
            }
        }

        if (!found)
        {
            bounds = default;
            return false;
        }

        WorldPosition cellOrigin = WorldPartitionCoordinates.GetCellOrigin(
            partition,
            cell.Key.Coordinate);
        bounds = new WorldBounds(
            new WorldPosition(
                cellOrigin.X + localMin.X,
                cellOrigin.Y + localMin.Y,
                cellOrigin.Z + localMin.Z),
            new WorldPosition(
                cellOrigin.X + localMax.X,
                cellOrigin.Y + localMax.Y,
                cellOrigin.Z + localMax.Z));
        return bounds.IsValid;
    }

    private static bool TryGetMeshBounds(
        SceneTransformInspection transform,
        SceneMeshRendererInspection mesh,
        IReadOnlyDictionary<Guid, MeshBounds> authoritativeMeshBounds,
        out Vector3 boundsMin,
        out Vector3 boundsMax)
    {
        boundsMin = default;
        boundsMax = default;
        if (!IsFinite(transform.Position) ||
            !IsFinite(transform.Scale) ||
            !IsFinite(transform.Rotation) ||
            !IsFinite(mesh.BoundsCenter) ||
            !IsFinite(mesh.BoundsExtents))
        {
            return false;
        }

        float rotationLengthSquared = transform.Rotation.LengthSquared();
        if (!float.IsFinite(rotationLengthSquared) || rotationLengthSquared <= BoundsEpsilon)
        {
            return false;
        }

        Vector3 localCenter = mesh.BoundsCenter;
        Vector3 localExtents = Vector3.Abs(mesh.BoundsExtents);
        if (!HasUsableBounds(localExtents) &&
            (!authoritativeMeshBounds.TryGetValue(mesh.Mesh.Guid, out MeshBounds meshBounds) ||
             !TryGetCenterAndExtents(meshBounds, out localCenter, out localExtents)))
        {
            return false;
        }

        Matrix4x4 localToCell =
            Matrix4x4.CreateScale(transform.Scale) *
            Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(transform.Rotation)) *
            Matrix4x4.CreateTranslation(transform.Position);
        Vector3 center = Vector3.Transform(localCenter, localToCell);
        Vector3 extents = TransformExtents(localExtents, localToCell);
        if (!IsFinite(center) || !HasUsableBounds(extents))
        {
            return false;
        }

        boundsMin = center - extents;
        boundsMax = center + extents;
        return IsFinite(boundsMin) && IsFinite(boundsMax);
    }

    internal static bool HasAuthoredBounds(SceneMeshRendererInspection mesh)
    {
        return IsFinite(mesh.BoundsCenter) &&
               HasUsableBounds(Vector3.Abs(mesh.BoundsExtents));
    }

    private static bool TryGetCenterAndExtents(
        MeshBounds bounds,
        out Vector3 center,
        out Vector3 extents)
    {
        center = (bounds.Min + bounds.Max) * 0.5f;
        extents = Vector3.Abs(bounds.Max - bounds.Min) * 0.5f;
        return IsFinite(bounds.Min) &&
               IsFinite(bounds.Max) &&
               IsFinite(center) &&
               HasUsableBounds(extents);
    }

    private static bool TryCreateCamera(
        WorldBounds bounds,
        out SceneViewCameraOverride camera)
    {
        double centerX = (bounds.Min.X + bounds.Max.X) * 0.5;
        double centerY = (bounds.Min.Y + bounds.Max.Y) * 0.5;
        double centerZ = (bounds.Min.Z + bounds.Max.Z) * 0.5;
        double extentX = (bounds.Max.X - bounds.Min.X) * 0.5;
        double extentY = (bounds.Max.Y - bounds.Min.Y) * 0.5;
        double extentZ = (bounds.Max.Z - bounds.Min.Z) * 0.5;
        double radius = Math.Sqrt(
            extentX * extentX +
            extentY * extentY +
            extentZ * extentZ);
        double halfFovRadians = DefaultFieldOfView * (Math.PI / 180.0) * 0.5;
        double distance = Math.Max(2.0, radius / Math.Sin(halfFovRadians) * FramePadding);
        double farClip = Math.Max(1000.0, distance + radius * 4.0);
        if (!double.IsFinite(distance) ||
            !double.IsFinite(farClip) ||
            distance > float.MaxValue ||
            farClip > float.MaxValue)
        {
            camera = null!;
            return false;
        }

        float pitch = -MathF.Asin(s_ViewDirection.Y) * (180.0f / MathF.PI);
        float yaw = MathF.Atan2(s_ViewDirection.X, s_ViewDirection.Z) * (180.0f / MathF.PI);
        camera = new SceneViewCameraOverride(
            new WorldPosition(
                centerX - s_ViewDirection.X * distance,
                centerY - s_ViewDirection.Y * distance,
                centerZ - s_ViewDirection.Z * distance),
            new Vector3(pitch, yaw, 0.0f),
            DefaultFieldOfView,
            0.1f,
            (float)farClip);
        return camera.IsValid;
    }

    private static Vector3 TransformExtents(Vector3 extents, Matrix4x4 transform)
    {
        return new Vector3(
            MathF.Abs(transform.M11) * extents.X +
            MathF.Abs(transform.M21) * extents.Y +
            MathF.Abs(transform.M31) * extents.Z,
            MathF.Abs(transform.M12) * extents.X +
            MathF.Abs(transform.M22) * extents.Y +
            MathF.Abs(transform.M32) * extents.Z,
            MathF.Abs(transform.M13) * extents.X +
            MathF.Abs(transform.M23) * extents.Y +
            MathF.Abs(transform.M33) * extents.Z);
    }

    private static bool HasUsableBounds(Vector3 extents) =>
        IsFinite(extents) &&
        (extents.X > BoundsEpsilon ||
         extents.Y > BoundsEpsilon ||
         extents.Z > BoundsEpsilon);

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
