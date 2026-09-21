using System.Numerics;
using ArisenEngine.Rendering;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Diagnostics;

namespace ArisenEditor.Core.Services;

/// <summary>
/// Owns the scene-view camera override for the whole editor. Both camera sources go through this
/// type: the world-document focus framing and interactive navigation. Keeping a single owner is
/// what makes "re-frame this cell" and "I moved the camera myself" deterministic instead of two
/// writers racing for the override slot.
/// </summary>
/// <remarks>
/// The override slot lives in <see cref="RenderSubsystem"/> and is only consulted for
/// <c>SurfaceType.SceneView</c> surfaces. Without an override the scene view falls back to the
/// scene cameras in the ECS, so interactive navigation is seeded from the pose the user is already
/// looking at and stays continuous with the authored camera.
/// The same owner publishes the world streaming source once the user frames or flies the camera,
/// mirroring the runtime rule that the composition host which owns an interactive camera selects
/// the finite source that makes cells stream. Until then the editor keeps its deterministic
/// pin/preview activation model untouched, so automated sessions and the startup state never
/// depend on a camera source.
/// </remarks>
internal sealed class EditorSceneViewCameraController : IDisposable
{
    private readonly RenderSubsystem m_Rendering;
    private readonly IRuntimeWorldStreamingService m_WorldStreaming;
    private readonly EditorSceneViewNavigationInput m_Input;
    private readonly object m_Gate = new();
    private SceneViewCameraOverride? m_Camera;
    private bool m_NavigationOwned;
    private bool m_StreamingSourceOwned;
    private bool m_HasLoggedNavigation;
    private bool m_HasLoggedUnavailableSeed;
    private bool m_Disposed;

    public EditorSceneViewCameraController(
        RenderSubsystem rendering,
        IRuntimeWorldStreamingService worldStreaming,
        EditorSceneViewNavigationInput input)
    {
        m_Rendering = rendering ?? throw new ArgumentNullException(nameof(rendering));
        m_WorldStreaming = worldStreaming ?? throw new ArgumentNullException(nameof(worldStreaming));
        m_Input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <summary>
    /// Adopts a framing camera produced by the focus controller. An explicit re-frame request
    /// replaces the navigation pose.
    /// </summary>
    public void ApplyFocusFrame(SceneViewCameraOverride camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (!camera.IsValid)
        {
            throw new ArgumentException(
                "Scene-view focus frame must contain finite camera data.",
                nameof(camera));
        }

        lock (m_Gate)
        {
            m_Camera = camera;
            m_NavigationOwned = false;
        }

        m_Rendering.SetSceneViewCameraOverride(camera);
        PublishStreamingSource(camera.Position);
    }

    /// <summary>
    /// Releases a focus frame. A pose the user navigated to is preserved: document state changes
    /// must not pull the camera away from an inspection angle the user chose.
    /// </summary>
    public void ClearFocusFrame()
    {
        bool cleared = false;
        lock (m_Gate)
        {
            if (m_Camera != null && !m_NavigationOwned)
            {
                m_Camera = null;
                cleared = true;
            }
        }

        if (cleared)
        {
            m_Rendering.ClearSceneViewCameraOverride();
        }
    }

    /// <summary>
    /// Drops the scene-view override entirely, falling back to the scene cameras. Used at editor
    /// teardown, not by document transitions.
    /// </summary>
    public void Clear()
    {
        bool ownedStreamingSource;
        lock (m_Gate)
        {
            m_Camera = null;
            m_NavigationOwned = false;
            ownedStreamingSource = m_StreamingSourceOwned;
            m_StreamingSourceOwned = false;
        }

        m_Rendering.ClearSceneViewCameraOverride();
        if (ownedStreamingSource)
        {
            m_WorldStreaming.ClearStreamingSource();
        }
    }

    /// <summary>
    /// Engine-frame update. Consumes the accumulated pointer/key state and publishes the resulting
    /// pose as the scene-view override.
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        if (m_Disposed ||
            !float.IsFinite(deltaSeconds) ||
            deltaSeconds <= 0.0f ||
            !m_Input.TryConsume(out EditorSceneViewNavigationSnapshot input) ||
            !input.HasInput)
        {
            return;
        }

        SceneViewCameraOverride? existing;
        lock (m_Gate)
        {
            existing = m_Camera;
        }

        SceneViewCameraOverride camera;
        bool seeded = false;
        if (existing == null)
        {
            if (!EditorSceneViewCameraSeed.TryReadSceneCamera(out SceneViewCameraOverride sceneCamera))
            {
                LogUnavailableSeedOnce();
                return;
            }

            camera = sceneCamera;
            seeded = true;
        }
        else
        {
            camera = existing;
        }

        Vector3 eulerDegrees = camera.Rotation;
        WorldPosition position = camera.Position;
        bool changed = seeded;

        if (input.HasLookInput)
        {
            Vector3 looked = EditorSceneViewCameraMotion.ApplyLook(
                eulerDegrees,
                input.LookDeltaX,
                input.LookDeltaY);
            if (looked != eulerDegrees)
            {
                eulerDegrees = looked;
                changed = true;
            }
        }

        if (input.HasMoveInput &&
            EditorSceneViewCameraMotion.TryApplyMove(
                position,
                eulerDegrees,
                input.Keys,
                deltaSeconds,
                out WorldPosition moved))
        {
            position = moved;
            changed = true;
        }

        if (input.HasDollyInput &&
            EditorSceneViewCameraMotion.TryApplyDolly(
                position,
                eulerDegrees,
                input.DollyDelta,
                out WorldPosition dollied))
        {
            position = dollied;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var updated = new SceneViewCameraOverride(
            position,
            eulerDegrees,
            camera.VerticalFieldOfView,
            camera.NearClip,
            camera.FarClip);
        if (!updated.IsValid)
        {
            return;
        }

        lock (m_Gate)
        {
            m_Camera = updated;
            m_NavigationOwned = true;
        }

        m_Rendering.SetSceneViewCameraOverride(updated);
        PublishStreamingSource(updated.Position);
        LogNavigationOnce(updated);
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        m_Disposed = true;
        Clear();
    }

    private void LogNavigationOnce(SceneViewCameraOverride camera)
    {
        if (m_HasLoggedNavigation)
        {
            return;
        }

        m_HasLoggedNavigation = true;
        KernelLog.InfoFormat(
            "[EditorSceneView] Scene-view navigation engaged at ({0:F2}, {1:F2}, {2:F2}) aimed at yaw {3:F1}°, pitch {4:F1}°.",
            camera.Position.X,
            camera.Position.Y,
            camera.Position.Z,
            camera.Rotation.Y,
            camera.Rotation.X);
    }

    private void PublishStreamingSource(WorldPosition position)
    {
        if (!position.IsFinite)
        {
            return;
        }

        if (!m_StreamingSourceOwned)
        {
            m_StreamingSourceOwned = true;
            KernelLog.InfoFormat(
                "[EditorSceneView] World streaming now follows the scene-view camera at ({0:F2}, {1:F2}, {2:F2}).",
                position.X,
                position.Y,
                position.Z);
        }

        m_WorldStreaming.SetStreamingSource(position);
    }

    private void LogUnavailableSeedOnce()
    {
        if (m_HasLoggedUnavailableSeed)
        {
            return;
        }

        m_HasLoggedUnavailableSeed = true;
        KernelLog.Warning(
            "[EditorSceneView] Scene-view navigation is idle: no perspective scene camera with a valid transform is active to start from.");
    }
}
