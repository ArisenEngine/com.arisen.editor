using System;
using System.Threading;
using System.Threading.Tasks;
using ArisenEditorFramework.Lifecycle;
using ArisenKernel.Contracts;
using ArisenKernel.Lifecycle;

namespace ArisenEditor.Core.Lifecycle.BootSteps;

public class HardwareWarmupStep : IBootStep
{
    public string Name => "Hardware Warmup";
    public string Description => "Initializing GPU compute buffers and shader caches...";

    public Task ExecuteAsync(BootContext context, CancellationToken cancellationToken = default)
    {
        InitializeBackend();
        return Task.CompletedTask;
    }

    public static void InitializeBackend()
    {
        // LoadingWindow is already on screen, so Avalonia's WinUI compositor has created its
        // ANGLE D3D11 device. It is now safe to preload RenderDoc (installs global
        // D3D11CreateDevice hooks) and bring up the selected RHI without corrupting the compositor.
        var services = EngineKernel.Instance.Services;
        var backend = services.GetService<IRHIBackend>();
        if (!backend.Initialize(services))
        {
            throw new InvalidOperationException(
                $"Graphics subsystem failed to initialize ({backend.Name} / RenderDoc). See log.");
        }
    }
}
