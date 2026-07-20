using System.IO;
using System.Threading.Tasks;
using ArisenEditorFramework.Attributes;
using ArisenEditor.GameDev;
using ArisenEditor.Utilities;
using ArisenEngine;
using ArisenEngine.Core.Lifecycle;
using ArisenEditor.Core.Services;

namespace ArisenEditor.Internal.MenuItemEntries;

internal partial class HeaderMenuEntries
{
    #region Assets

    [MenuItem("Header/Content/Open C# project")]
    internal static void OpenProjectSolution()
    {
        Task.Run(()=> {

            Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            });
            var env = EngineKernel.Instance.GetSubsystem<EnvironmentSubsystem>();
            string root = env?.ProjectRoot ?? string.Empty;
            string name = env?.ProjectName ?? string.Empty;
            ProjectSolution.OpenVisualStudio(Path.Combine(root, name + @".sln"));

        });
    }

    #endregion

    #region File

    [MenuItem("Header/File/New Level")]
    internal static void NewLevel()
    {
        
    }
    
    [MenuItem("Header/File/Open Level", true)]
    internal static void OpenLevel()
    {
        
    }
    
    [MenuItem("Header/File/Save")]
    internal static async void Save()
    {
        if (ArisenKernel.Lifecycle.EngineKernel.Instance.Services
            .TryGetService<IEditorSceneDocumentService>(out var documentService) &&
            documentService != null)
        {
            await EditorSceneDocumentInteraction.SaveAsync(documentService);
        }
    }

    #endregion
    
    
    
}
