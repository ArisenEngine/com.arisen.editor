using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenKernel.Services;

namespace ArisenEditor.Core.Services;

/// <summary>
/// Drives editor scene-view navigation from the engine frame. Input is collected by the viewport
/// on the UI thread and published through <see cref="EditorSceneViewNavigationInput"/>; this
/// subsystem turns it into one camera update per frame so movement speed follows real frame time
/// instead of the UI thread's event cadence.
/// </summary>
public sealed class EditorSceneViewNavigationSubsystem : ITickableSubsystem
{
    private EditorSceneViewCameraController? m_Controller;

    public int Priority => 70;

    public EnginePhase InitPhase => EnginePhase.Running;

    public void Initialize()
    {
        if (!EngineKernel.IsCreated)
        {
            return;
        }

        IServiceRegistry services = EngineKernel.Instance.Services;
        if (!services.TryGetService<EditorSceneViewCameraController>(
                out EditorSceneViewCameraController? controller) ||
            controller == null)
        {
            KernelLog.Warning(
                "[EditorSceneView] Scene-view camera controller is unavailable; interactive scene-view navigation is disabled.");
            return;
        }

        m_Controller = controller;
        KernelLog.Info(
            "[EditorSceneView] Scene-view navigation ready: hold the right mouse button to look, W/A/S/D to move, E/Q for up/down, Shift to move faster, mouse wheel to dolly.");
    }

    public void Tick(float deltaTime)
    {
        m_Controller?.Tick(deltaTime);
    }

    public void Shutdown()
    {
        m_Controller = null;
    }

    public void Dispose()
    {
    }
}
