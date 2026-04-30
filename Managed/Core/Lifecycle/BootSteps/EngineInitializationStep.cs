using System.Threading;
using System.Threading.Tasks;
using ArisenEditorFramework.Lifecycle;
using ArisenEngine.Core.Lifecycle;

namespace ArisenEditor.Core.Lifecycle.BootSteps;

public class EngineInitializationStep : IBootStep
{
    public string Name => "Engine Initialization";
    public string Description => "Initializing core engine subsystems...";

    public async Task ExecuteAsync(BootContext context, CancellationToken cancellationToken = default)
    {
        var projectRoot = System.IO.Path.GetDirectoryName(context.ProjectPath);
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var projectName = System.IO.Path.GetFileNameWithoutExtension(context.ProjectPath);
            
            // Sync project context to the core engine EnvironmentSubsystem
            var kernel = ArisenKernel.Lifecycle.EngineKernel.Instance;
            
            // B11: Skip initialization if the kernel is already Running or Initializing
            // This prevents overwriting subsystems if the Host already started the engine.
            if (kernel.CurrentPhase != ArisenKernel.Lifecycle.EnginePhase.None)
            {
                ArisenKernel.Diagnostics.KernelLog.Info($"[EngineInitializationStep] Engine already in phase {kernel.CurrentPhase}. Skipping redundant initialization.");
            }
            else
            {
                var env = kernel.GetSubsystem<ArisenEngine.Core.Lifecycle.EnvironmentSubsystem>();
                if (env != null)
                {
                    env.SetProject(projectRoot, projectName);

                    // Initialize the kernel to trigger subsystem discovery and transition to Running phase.
                    // This is required for ITickableSubsystems like RenderSubsystem to start working.
                    kernel.Initialize(new ArisenKernel.Lifecycle.EngineConfig
                    {
                        ProjectRoot = projectRoot,
                        ProjectName = projectName,
                        AppName = "ArisenEditor"
                    });
                }
            }
        }

        await Task.CompletedTask;
    }
}

