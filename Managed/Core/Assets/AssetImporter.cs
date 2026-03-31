using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ArisenEngine.Core.Serialization;

namespace ArisenEditor.Core.Assets;

/// <summary>
/// Scans the Assets directory and ensures every asset has a .meta file and is registered in the SQLite AssetDatabase.
/// </summary>
public class AssetImporter : IDisposable
{
    private readonly string _assetsDirectory;
    private readonly string _workspaceRoot;
    private FileSystemWatcher? _watcher;

    public AssetImporter(string assetsDirectory, string workspaceRoot)
    {
        _assetsDirectory = assetsDirectory;
        _workspaceRoot = workspaceRoot;
    }

    public void Start()
    {
        if (!Directory.Exists(_assetsDirectory))
        {
            Directory.CreateDirectory(_assetsDirectory);
        }

        // 1. Initial Scan
        ScanDirectory(_assetsDirectory);

        // 2. Setup Watcher
        _watcher = new FileSystemWatcher(_assetsDirectory)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };

        _watcher.Created += OnFileCreated;
        _watcher.Deleted += OnFileDeleted;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Changed += OnFileChanged;
    }

    private void ScanDirectory(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            if (IsIgnoredPath(file)) continue;
            ProcessFile(file);
        }
    }

    private void ProcessFile(string filePath)
    {
        if (Directory.Exists(filePath)) return;

        try
        {
            var metaPath = filePath + ".meta";
            AssetMetadata meta;

            if (File.Exists(metaPath))
            {
                // Read existing meta
                meta = SerializationUtil.Deserialize<AssetMetadata>(metaPath);
                if (meta.Guid == Guid.Empty)
                {
                    meta.Guid = Guid.NewGuid();
                }
            }
            else
            {
                // Generate new meta
                meta = new AssetMetadata
                {
                    Guid = Guid.NewGuid(),
                    ImporterType = "Default" // Could be inferred from file extension
                };
                SerializationUtil.Serialize(meta, metaPath);
            }

            var relativePath = Path.GetRelativePath(_workspaceRoot, filePath).Replace('\\', '/');
            var ext = Path.GetExtension(filePath).TrimStart('.');
            var lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds();

            AssetDatabase.Instance.RegisterAsset(meta.Guid, relativePath, ext, lastModified);
        }
        catch (Exception ex)
        {
            // If the file is locked, we just skip it for now. The Watcher will fire again if it changes.
            // In a real editor, route this to LogService.Error
            ArisenEngine.Core.Diagnostics.Logger.Log($"[AssetImporter] Error processing {filePath}: {ex.Message}");
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        if (IsIgnoredPath(e.FullPath)) return;
        
        // Add a small delay because FileSystemWatcher often fires before the file is fully written/unlocked
        Task.Delay(100).ContinueWith(_ => ProcessFile(e.FullPath));
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.EndsWith(".meta"))
        {
            // If the meta file was deleted, the asset is now "untracked". 
            // We should ideally generate a new one, but often it means the asset itself is being deleted next.
            return;
        }

        var relativePath = Path.GetRelativePath(_workspaceRoot, e.FullPath).Replace('\\', '/');
        
        // Remove from DB
        AssetDatabase.Instance.RemoveAssetByPath(relativePath);

        // Cleanup orphaned .meta
        var metaPath = e.FullPath + ".meta";
        if (File.Exists(metaPath))
        {
            try { File.Delete(metaPath); } catch { /* Ignore locked errors during delete */ }
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsIgnoredPath(e.FullPath)) return;

        var oldRelativePath = Path.GetRelativePath(_workspaceRoot, e.OldFullPath).Replace('\\', '/');
        var newRelativePath = Path.GetRelativePath(_workspaceRoot, e.FullPath).Replace('\\', '/');

        // Note: Renaming requires reading the Guid to update the DB, or updating by old path.
        if (AssetDatabase.Instance.TryGetGuid(oldRelativePath, out var guid))
        {
            var ext = Path.GetExtension(e.FullPath).TrimStart('.');
            var lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(e.FullPath)).ToUnixTimeSeconds();
            
            // Update DB with new path
            AssetDatabase.Instance.RegisterAsset(guid, newRelativePath, ext, lastModified);
        }

        // Rename the .meta file to match
        var oldMetaPath = e.OldFullPath + ".meta";
        var newMetaPath = e.FullPath + ".meta";
        if (File.Exists(oldMetaPath))
        {
            try { File.Move(oldMetaPath, newMetaPath); } catch { }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsIgnoredPath(e.FullPath)) return;
        Task.Delay(100).ContinueWith(_ => ProcessFile(e.FullPath));
    }

    private bool IsIgnoredPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;

        var name = Path.GetFileName(path);
        if (name.StartsWith(".")) return true;

        var dir = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(dir))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName.EndsWith(".arisen")) break; // Don't check parents above the project root if known
            if (dirName.StartsWith(".")) return true;
            dir = Path.GetDirectoryName(dir);
        }

        try 
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0) return true;
        }
        catch { }

        return false;
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
