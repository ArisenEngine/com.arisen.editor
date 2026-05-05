using System;
using System.Threading;
using System.Threading.Tasks;
using ArisenEditorFramework.Lifecycle;
using ArisenEngine.Core.Lifecycle;
using ArisenKernel.Lifecycle;

namespace ArisenEditor.Core.Lifecycle.BootSteps;

public class HardwareWarmupStep : IBootStep
{
    public string Name => "Hardware Warmup";
    public string Description => "Initializing GPU compute buffers and shader caches...";

    public Task ExecuteAsync(BootContext context, CancellationToken cancellationToken = default)
    {
        // LoadingWindow is already on screen, so Avalonia's WinUI compositor has created its
        // ANGLE D3D11 device. It is now safe to preload RenderDoc (installs global
        // D3D11CreateDevice hooks) and bring up Vulkan RHI without corrupting the compositor.
        var services = EngineKernel.Instance.Services;
        if (!NativeRuntime.InitializeGraphics(services))
        {
            throw new InvalidOperationException(
                "Graphics subsystem failed to initialize (Vulkan RHI / RenderDoc). See log.");
        }
        return Task.CompletedTask;
    }
}
