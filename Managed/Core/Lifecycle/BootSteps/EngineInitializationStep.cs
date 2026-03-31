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
            var env = kernel.GetSubsystem<ArisenEngine.Core.Lifecycle.EnvironmentSubsystem>();
            if (env != null)
            {
                env.SetProject(projectRoot, projectName);
            }
        }

        await Task.CompletedTask;
    }
}

