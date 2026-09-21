using System.Numerics;
using ArisenEngine.Core.Math;
using ArisenEngine.Resources.Serialization;

namespace ArisenEditor.Core.Services;

/// <summary>
/// Scene-view camera motion. Kept free of editor, render and ECS state so the look/move/dolly math
/// can be verified without a GPU, and so both navigation sources (user input and focus framing)
/// share one definition of "camera pose".
/// </summary>
/// <remarks>
/// Angles follow the engine camera convention: euler degrees with X = pitch, Y = yaw, Z = roll,
/// and the basis built by <see cref="Matrix4x4.CreateFromYawPitchRoll"/> over
/// <see cref="MathExtensions.Forward"/>. Strafe uses <see cref="MathExtensions.RightVector(Matrix4x4)"/>
/// because the rendered image's right is forward x up, not world +X. Movement stays in double-precision
/// world space so a rebased world origin cannot accumulate error in the editor camera.
internal static class EditorSceneViewCameraMotion
{
    public const float DefaultMoveSpeed = 12.0f;
    public const float FastMoveMultiplier = 4.0f;
    public const float LookDegreesPerPixel = 0.14f;
    public const float MaxPitchDegrees = 89.0f;
    public const float DollyMetersPerNotch = 2.0f;

    private const float RadiansPerDegree = MathF.PI / 180.0f;
    private const float MinimumDirectionLength = 1.0e-6f;

    public static Vector3 ApplyLook(Vector3 eulerDegrees, double lookDeltaX, double lookDeltaY)
    {
        if (!IsFinite(eulerDegrees) ||
            !double.IsFinite(lookDeltaX) ||
            !double.IsFinite(lookDeltaY))
        {
            return eulerDegrees;
        }

        // Look is expressed in the rendered image's frame: the view basis is right-handed over the
        // +Z forward basis, so the image's right is forward x up (world -X for an unrotated camera)
        // and pitch grows while the view tilts down. Dragging right turns right by decreasing yaw;
        // dragging down looks down by increasing pitch.
        float yaw = MathExtensions.WrapDegrees(eulerDegrees.Y - (float)(lookDeltaX * LookDegreesPerPixel));
        float pitch = Math.Clamp(
            eulerDegrees.X + (float)(lookDeltaY * LookDegreesPerPixel),
            -MaxPitchDegrees,
            MaxPitchDegrees);
        return new Vector3(pitch, yaw, eulerDegrees.Z);
    }

    public static bool TryApplyMove(
        WorldPosition position,
        Vector3 eulerDegrees,
        EditorSceneViewNavigationKeys keys,
        float deltaSeconds,
        out WorldPosition moved)
    {
        moved = position;
        if (!position.IsFinite ||
            !float.IsFinite(deltaSeconds) ||
            deltaSeconds <= 0.0f ||
            !TryResolveDirection(eulerDegrees, keys, out Vector3 direction))
        {
            return false;
        }

        float speed = DefaultMoveSpeed;
        if ((keys & EditorSceneViewNavigationKeys.FastMove) != 0)
        {
            speed *= FastMoveMultiplier;
        }

        double distance = speed * deltaSeconds;
        var candidate = new WorldPosition(
            position.X + direction.X * distance,
            position.Y + direction.Y * distance,
            position.Z + direction.Z * distance);
        if (!candidate.IsFinite)
        {
            return false;
        }

        moved = candidate;
        return true;
    }

    public static bool TryApplyDolly(
        WorldPosition position,
        Vector3 eulerDegrees,
        double notches,
        out WorldPosition moved)
    {
        moved = position;
        if (!position.IsFinite ||
            !double.IsFinite(notches) ||
            notches == 0.0 ||
            !IsFinite(eulerDegrees))
        {
            return false;
        }

        Vector3 forward = Vector3.Transform(MathExtensions.Forward, CreateRotation(eulerDegrees));
        double distance = notches * DollyMetersPerNotch;
        var candidate = new WorldPosition(
            position.X + forward.X * distance,
            position.Y + forward.Y * distance,
            position.Z + forward.Z * distance);
        if (!candidate.IsFinite)
        {
            return false;
        }

        moved = candidate;
        return true;
    }

    public static bool TryResolveDirection(
        Vector3 eulerDegrees,
        EditorSceneViewNavigationKeys keys,
        out Vector3 direction)
    {
        direction = default;
        if (keys == EditorSceneViewNavigationKeys.None || !IsFinite(eulerDegrees))
        {
            return false;
        }

        Matrix4x4 rotation = CreateRotation(eulerDegrees);
        Vector3 combined =
            Vector3.Transform(MathExtensions.Forward, rotation) *
            ReadAxis(keys, EditorSceneViewNavigationKeys.Forward, EditorSceneViewNavigationKeys.Backward) +
            rotation.RightVector() *
            ReadAxis(keys, EditorSceneViewNavigationKeys.StrafeRight, EditorSceneViewNavigationKeys.StrafeLeft) +
            MathExtensions.Up *
            ReadAxis(keys, EditorSceneViewNavigationKeys.Ascend, EditorSceneViewNavigationKeys.Descend);
        float lengthSquared = combined.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= MinimumDirectionLength)
        {
            return false;
        }

        direction = combined / MathF.Sqrt(lengthSquared);
        return true;
    }

    private static Matrix4x4 CreateRotation(Vector3 eulerDegrees) =>
        Matrix4x4.CreateFromYawPitchRoll(
            eulerDegrees.Y * RadiansPerDegree,
            eulerDegrees.X * RadiansPerDegree,
            eulerDegrees.Z * RadiansPerDegree);

    private static float ReadAxis(
        EditorSceneViewNavigationKeys keys,
        EditorSceneViewNavigationKeys positive,
        EditorSceneViewNavigationKeys negative)
    {
        float value = 0.0f;
        if ((keys & positive) != 0)
        {
            value += 1.0f;
        }

        if ((keys & negative) != 0)
        {
            value -= 1.0f;
        }

        return value;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
