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
        // LoadingWindow is already on screen, so Avalonia has established its compositor before
        // the selected RHI applies any explicitly requested process-start diagnostics.
        var services = EngineKernel.Instance.Services;
        var backend = services.GetService<IRHIBackend>();
        if (!backend.Initialize(services))
        {
            throw new InvalidOperationException(
                $"Graphics subsystem failed to initialize ({backend.Name}). See log.");
        }
    }
}
