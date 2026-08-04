using System.Threading;
using System.Threading.Tasks;
using ArisenEditor.Core.Services;
using ArisenEditorFramework.Lifecycle;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Lifecycle;

namespace ArisenEditor.Core.Lifecycle.BootSteps;

public class ProjectSynthesisStep : IBootStep
{
    public string Name => "Project Synthesis";
    public string Description => "Loading project settings and active scene metadata...";

    public Task ExecuteAsync(BootContext context, CancellationToken cancellationToken = default)
    {
        EditorProjectService.Instance.LoadUserSettings();

        var sceneService = EngineKernel.Instance.Services.GetService<IRuntimeSceneService>();
        var activeScene = sceneService.ActiveScene;
        if (activeScene == null)
        {
            var project = EngineKernel.Instance.Services
                .GetService<ProjectSubsystem>()
                .ActiveProject;
            if (project?.StartupWorld is { IsValid: true } startupWorld)
            {
                ArisenEngine.Core.Diagnostics.Logger.Log(
                    $"[ProjectSynthesis] Startup world '{startupWorld.Guid:D}' is waiting for " +
                    "frame-boundary residency before editor documents are reconstructed.");
                return Task.CompletedTask;
            }

            context.Success = false;
            context.ErrorMessage = "The project startup scene was not activated during engine initialization.";
            return Task.CompletedTask;
        }

        ArisenEngine.Core.Diagnostics.Logger.Log(
            $"[ProjectSynthesis] Using active runtime scene '{activeScene.Name}' ({activeScene.Scene.Guid:D}).");
        return Task.CompletedTask;
    }
}
