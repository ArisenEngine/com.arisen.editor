using System;
using System.IO;
using ArisenEngine.Core.Serialization;
using RuntimeAssetChangeEvent = ArisenEngine.Core.Assets.AssetChangeEvent;
using RuntimeAssetChangeKind = ArisenEngine.Core.Assets.AssetChangeKind;

namespace ArisenEditor.Core.Assets;

/// <summary>
/// Scans the Assets directory and ensures every asset has a .meta file and is registered in the SQLite AssetDatabase.
/// </summary>
public class AssetImporter : IDisposable
{
    private readonly string _assetsDirectory;
    private readonly string _workspaceRoot;
    private readonly string _packageId;
    private FileSystemWatcher? _watcher;
    private AssetImportScheduler? _scheduler;

    public event Action<RuntimeAssetChangeEvent>? AssetChanged;

    public AssetImporter(string assetsDirectory, string workspaceRoot)
        : this(assetsDirectory, workspaceRoot, "workspace")
    {
    }

    public AssetImporter(string assetsDirectory, string workspaceRoot, string packageId)
    {
        _assetsDirectory = assetsDirectory;
        _workspaceRoot = workspaceRoot;
        _packageId = string.IsNullOrWhiteSpace(packageId) ? "workspace" : packageId;
    }

    public void Start()
    {
        if (!AssetPathPolicy.IsAssetsRoot(_assetsDirectory) || AssetPathPolicy.IsGeneratedPath(_assetsDirectory))
        {
            throw new InvalidOperationException(
                $"[AssetImporter] Import root must be a workspace/package Assets directory, not '{_assetsDirectory}'.");
        }

        if (!Directory.Exists(_assetsDirectory))
        {
            Directory.CreateDirectory(_assetsDirectory);
        }

        // 1. Initial Scan
        ScanDirectory(_assetsDirectory);

        // 2. Setup Watcher
        _scheduler = new AssetImportScheduler(ProcessScheduledImport);
        _watcher = new FileSystemWatcher(_assetsDirectory)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
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
            ProcessFile(file, publishEvent: false);
        }
    }

    private void ProcessFile(
        string filePath,
        bool publishEvent = false,
        RuntimeAssetChangeKind changeKind = RuntimeAssetChangeKind.Changed,
        bool throwOnFailure = false)
    {
        if (Directory.Exists(filePath)) return;
        if (!AssetPathPolicy.IsUnderAssetsRoot(filePath, _assetsDirectory))
        {
            throw new InvalidOperationException(
                $"[AssetImporter] Refusing to import outside Assets root '{_assetsDirectory}': {filePath}");
        }

        try
        {
            if (!WaitUntilReadable(filePath))
            {
                throw new IOException($"File is still locked or unavailable: {filePath}");
            }

            var metaPath = filePath + ".meta";
            AssetMetadata meta;

            if (File.Exists(metaPath))
            {
                meta = SerializationUtil.Deserialize<AssetMetadata>(metaPath);
                if (meta.Guid == Guid.Empty)
                {
                    meta.Guid = Guid.NewGuid();
                }
            }
            else
            {
                meta = new AssetMetadata
                {
                    Guid = Guid.NewGuid()
                };
            }

            var assetType = InferAssetType(filePath);
            var importer = InferImporter(filePath, assetType);
            var changed = false;
            if (string.IsNullOrWhiteSpace(meta.AssetType))
            {
                meta.AssetType = assetType;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(meta.Importer))
            {
                meta.Importer = string.IsNullOrWhiteSpace(meta.ImporterType)
                    ? importer
                    : meta.ImporterType;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(meta.ImporterType))
            {
                meta.ImporterType = null;
                changed = true;
            }

            if (!File.Exists(metaPath) || changed)
            {
                SerializationUtil.Serialize(meta, metaPath);
            }

            var relativePath = Path.GetRelativePath(_workspaceRoot, filePath).Replace('\\', '/');
            var lastModified = new DateTimeOffset(File.GetLastWriteTimeUtc(filePath)).ToUnixTimeSeconds();

            AssetDatabase.Instance.RegisterAsset(
                meta.Guid,
                relativePath,
                meta.AssetType,
                meta.Importer,
                _packageId,
                lastModified);

            if (publishEvent)
            {
                PublishChange(new RuntimeAssetChangeEvent(
                    changeKind,
                    meta.Guid,
                    meta.AssetType,
                    filePath,
                    string.Empty,
                    _packageId));
            }
        }
        catch (Exception ex)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[AssetImporter] Error processing {filePath}: {ex.Message}");
            if (throwOnFailure)
            {
                throw;
            }
        }
    }

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        EnqueueWatcherEvent(e.FullPath, AssetImportWorkKind.Created);
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        EnqueueWatcherEvent(e.FullPath, AssetImportWorkKind.Deleted);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        EnqueueWatcherEvent(e.FullPath, AssetImportWorkKind.Renamed, e.OldFullPath);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        EnqueueWatcherEvent(e.FullPath, AssetImportWorkKind.Changed);
    }

    private void EnqueueWatcherEvent(string fullPath, AssetImportWorkKind kind, string oldFullPath = "")
    {
        if (_scheduler == null)
        {
            return;
        }

        var sourcePath = ResolveSourcePathForWatcherEvent(fullPath);
        var oldSourcePath = string.IsNullOrWhiteSpace(oldFullPath)
            ? string.Empty
            : ResolveSourcePathForWatcherEvent(oldFullPath);
        if (IsIgnoredPath(sourcePath) || !AssetPathPolicy.IsUnderAssetsRoot(sourcePath, _assetsDirectory))
        {
            return;
        }

        _scheduler.Enqueue(new AssetImportRequest(kind, sourcePath, oldSourcePath));
    }

    private bool ProcessScheduledImport(AssetImportRequest request)
    {
        try
        {
            switch (request.Kind)
            {
                case AssetImportWorkKind.Created:
                    ProcessFile(request.FullPath, publishEvent: true, RuntimeAssetChangeKind.Created, throwOnFailure: true);
                    return true;
                case AssetImportWorkKind.Changed:
                    ProcessFile(request.FullPath, publishEvent: true, RuntimeAssetChangeKind.Changed, throwOnFailure: true);
                    return true;
                case AssetImportWorkKind.Deleted:
                    ProcessDeletedFile(request.FullPath);
                    return true;
                case AssetImportWorkKind.Renamed:
                    ProcessRenamedFile(request.OldFullPath, request.FullPath);
                    return true;
                default:
                    return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log(
                $"[AssetImporter] Scheduled import failed for {request.Kind} {request.FullPath}: {ex.Message}");
            return true;
        }
    }

    private void ProcessDeletedFile(string filePath)
    {
        if (!AssetPathPolicy.IsUnderAssetsRoot(filePath, _assetsDirectory))
        {
            return;
        }

        var relativePath = Path.GetRelativePath(_workspaceRoot, filePath).Replace('\\', '/');
        var guid = Guid.Empty;
        if (AssetDatabase.Instance.TryGetGuid(relativePath, out var existingGuid))
        {
            guid = existingGuid;
        }

        AssetDatabase.Instance.RemoveAssetByPath(relativePath);

        var metaPath = filePath + ".meta";
        if (File.Exists(metaPath))
        {
            try { File.Delete(metaPath); } catch { }
        }

        PublishChange(new RuntimeAssetChangeEvent(
            RuntimeAssetChangeKind.Deleted,
            guid,
            string.Empty,
            filePath,
            string.Empty,
            _packageId));
    }

    private void ProcessRenamedFile(string oldFullPath, string newFullPath)
    {
        if (!AssetPathPolicy.IsUnderAssetsRoot(newFullPath, _assetsDirectory) ||
            (!string.IsNullOrWhiteSpace(oldFullPath) && !AssetPathPolicy.IsUnderAssetsRoot(oldFullPath, _assetsDirectory)))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(oldFullPath))
        {
            ProcessFile(newFullPath, publishEvent: true, RuntimeAssetChangeKind.Changed);
            return;
        }

        var oldRelativePath = Path.GetRelativePath(_workspaceRoot, oldFullPath).Replace('\\', '/');
        var newRelativePath = Path.GetRelativePath(_workspaceRoot, newFullPath).Replace('\\', '/');
        var oldMetaPath = oldFullPath + ".meta";
        var newMetaPath = newFullPath + ".meta";
        var guid = Guid.Empty;

        if (AssetDatabase.Instance.TryGetGuid(oldRelativePath, out var existingGuid))
        {
            guid = existingGuid;
        }

        if (File.Exists(oldMetaPath) && !File.Exists(newMetaPath))
        {
            try { File.Move(oldMetaPath, newMetaPath); } catch { }
        }

        if (guid == Guid.Empty && File.Exists(newMetaPath))
        {
            var meta = SerializationUtil.Deserialize<AssetMetadata>(newMetaPath);
            guid = meta.Guid;
        }
        else if (guid != Guid.Empty && !File.Exists(newMetaPath))
        {
            SerializationUtil.Serialize(new AssetMetadata { Guid = guid }, newMetaPath);
        }

        AssetDatabase.Instance.RemoveAssetByPath(oldRelativePath);
        ProcessFile(newFullPath, publishEvent: false, RuntimeAssetChangeKind.Renamed, throwOnFailure: true);

        if (guid == Guid.Empty && AssetDatabase.Instance.TryGetGuid(newRelativePath, out var newGuid))
        {
            guid = newGuid;
        }

        PublishChange(new RuntimeAssetChangeEvent(
            RuntimeAssetChangeKind.Renamed,
            guid,
            InferAssetType(newFullPath),
            newFullPath,
            oldFullPath,
            _packageId));
    }

    private void PublishChange(RuntimeAssetChangeEvent change)
    {
        if (change.Guid == Guid.Empty)
        {
            return;
        }

        AssetChanged?.Invoke(change);
    }

    private bool IsIgnoredPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return true;
        if (AssetPathPolicy.IsGeneratedPath(path)) return true;

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

    private static string ResolveSourcePathForWatcherEvent(string path)
    {
        return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? path[..^".meta".Length]
            : path;
    }

    private static bool WaitUntilReadable(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length >= 0;
    }

    private static string InferAssetType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".hlsl" => "ShaderSource",
            ".shader" => "ShaderSource",
            ".png" => "Texture2D",
            ".jpg" => "Texture2D",
            ".jpeg" => "Texture2D",
            ".ppm" => "Texture2D",
            ".arismaterial" => "Material",
            ".material" => "Material",
            ".armesh" => "Mesh",
            ".obj" => "Mesh",
            ".gltf" => "Mesh",
            ".glb" => "Mesh",
            ".bin" => "AssetDependency",
            ".fbx" => "Mesh",
            _ => Path.GetExtension(filePath).TrimStart('.')
        };
    }

    private static string InferImporter(string filePath, string assetType)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".hlsl" => "HlslShader",
            ".shader" => "ShaderLab",
            ".ppm" => "PpmTextureImporter",
            ".png" or ".jpg" or ".jpeg" => "ImageTextureImporter",
            ".arismaterial" or ".material" => "ArisenMaterialImporter",
            ".armesh" => "ArisenTextMeshImporter",
            ".obj" => "ObjMeshImporter",
            ".gltf" or ".glb" => "GltfMeshImporter",
            ".bin" => "GltfBufferDependency",
            ".fbx" => "FbxMeshImporter",
            _ when !string.IsNullOrWhiteSpace(assetType) => assetType + "Importer",
            _ => "Default"
        };
    }

    public void Dispose()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _scheduler?.Dispose();
    }
}
