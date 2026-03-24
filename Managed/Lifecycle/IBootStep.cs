using System.Threading;
using System.Threading.Tasks;

namespace ArisenEditorFramework.Lifecycle;

public interface IBootStep
{
    string Name { get; }
    string Description { get; }
    Task ExecuteAsync(BootContext context, CancellationToken cancellationToken = default);
}

public class BootContext
{
    public string ProjectPath { get; set; }
    public object ProjectMetadata { get; set; }
    public bool Success { get; set; } = true;
    public string ErrorMessage { get; set; }
}
