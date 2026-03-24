using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace ArisenEditorFramework.Utilities;

public static class FileSystemUtilities
{
    public static string ProjectRoot { get; set; } = string.Empty;

    public static async Task<IReadOnlyList<string>> BrowserDirectory(string title)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var options = new FolderPickerOpenOptions()
            {
                    Title = title,
                    AllowMultiple = false,
            };

            var owner = desktop.MainWindow;
            
            if (owner == null)
            {
                // To safely open a native dialog, a valid Window must exist to act as the owner.
                return new List<string>();
            }

            try 
            {
                var selected = await owner.StorageProvider.OpenFolderPickerAsync(options);
                return selected?.Select(v => v.Path.LocalPath).ToList() ?? new List<string>();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        return new List<string>();
    }
    
    public static string GetCurrentProjectRoot()
    {
        return ProjectRoot;
    }

    public static string GetProjectRelativePath(string absolutePath)
    {
        var root = GetCurrentProjectRoot();
        if (string.IsNullOrEmpty(root)) return absolutePath;
        
        return Path.GetRelativePath(root, absolutePath);
    }

    public static string GetProjectAbsolutePath(string relativePath)
    {
        var root = GetCurrentProjectRoot();
        if (string.IsNullOrEmpty(root)) return relativePath;

        return Path.Combine(root, relativePath);
    }
}
