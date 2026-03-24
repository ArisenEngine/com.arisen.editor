using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArisenEditorFramework.Lifecycle;

public class Bootstrapper
{
    private readonly List<IBootStep> _steps = new();
    public event Action<string, string, double>? ProgressChanged;

    public void AddStep(IBootStep step) => _steps.Add(step);

    public async Task<BootContext> RunAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var context = new BootContext { ProjectPath = projectPath };
        double totalSteps = _steps.Count;
        
        for (int i = 0; i < _steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var step = _steps[i];
            double progress = (i / totalSteps) * 100.0;
            ProgressChanged?.Invoke(step.Name, step.Description, progress);

            try
            {
                await step.ExecuteAsync(context, cancellationToken);
                if (!context.Success)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                context.Success = false;
                context.ErrorMessage = "Bootstrap was cancelled.";
                break;
            }
            catch (Exception ex)
            {
                context.Success = false;
                context.ErrorMessage = ex.Message;
                break;
            }
        }

        if (context.Success)
        {
            ProgressChanged?.Invoke("Completed", "Engine is ready.", 100.0);
        }

        return context;
    }
}

