using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ArisenEngine.Core.Serialization;
using RuntimeAssetChangeEvent = ArisenEngine.Core.Assets.AssetChangeEvent;
using RuntimeAssetChangeKind = ArisenEngine.Core.Assets.AssetChangeKind;

namespace ArisenEditor.Core.Assets;

internal enum AssetImporterState
{
    Created,
    Starting,
    Running,
    Stopping,
    Faulted,
    Disposed
}

/// <summary>
/// Scans the Assets directory and ensures every asset has a .meta file and is registered in the SQLite AssetDatabase.
/// </summary>
public class AssetImporter : IDisposable
{
    private readonly string _assetsDirectory;
    private readonly string _workspaceRoot;
    private readonly string _packageId;
    private readonly TimeSpan? _debounceDelay;
    private readonly TimeSpan? _retryDelay;
    private readonly int _maxAttempts;
    private readonly object _lifecycleGate = new();
    private readonly List<AssetImportRequest> _startupRequests = new();
    private FileSystemWatcher? _watcher;
    private AssetImportScheduler? _scheduler;
    private AssetImporterState _state;

    internal Action<string>? BeforeInitialScanFile { get; set; }

    internal Action? BeforeRenameRegistrationCommit { get; set; }

    internal AssetImporterState State
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _state;
            }
        }
    }

    public event Action<RuntimeAssetChangeEvent>? AssetChanged;

    public AssetImporter(string assetsDirectory, string workspaceRoot)
        : this(assetsDirectory, workspaceRoot, "workspace")
    {
    }

    public AssetImporter(string assetsDirectory, string workspaceRoot, string packageId)
        : this(
            assetsDirectory,
            workspaceRoot,
            packageId,
            debounceDelay: null,
            retryDelay: null,
            maxAttempts: 5)
    {
    }

    internal AssetImporter(
        string assetsDirectory,
        string workspaceRoot,
        string packageId,
        TimeSpan? debounceDelay,
        TimeSpan? retryDelay,
        int maxAttempts)
    {
        _assetsDirectory = assetsDirectory;
        _workspaceRoot = workspaceRoot;
        _packageId = string.IsNullOrWhiteSpace(packageId) ? "workspace" : packageId;
        _debounceDelay = debounceDelay;
        _retryDelay = retryDelay;
        _maxAttempts = System.Math.Max(1, maxAttempts);
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

        lock (_lifecycleGate)
        {
            if (_state == AssetImporterState.Disposed)
            {
                throw new ObjectDisposedException(nameof(AssetImporter));
            }

            if (_state != AssetImporterState.Created)
            {
                throw new InvalidOperationException(
                    $"[AssetImporter] Cannot start importer while it is {_state}.");
            }

            _state = AssetImporterState.Starting;
        }

        AssetImportScheduler? scheduler = null;
        FileSystemWatcher? watcher = null;
        try
        {
            scheduler = new AssetImportScheduler(
                ProcessScheduledImport,
                _debounceDelay,
                _retryDelay,
                _maxAttempts);
            watcher = new FileSystemWatcher(_assetsDirectory)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = false,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite |
                    NotifyFilters.Size
            };

            watcher.Created += OnFileCreated;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.Changed += OnFileChanged;
            lock (_lifecycleGate)
            {
                _scheduler = scheduler;
                _watcher = watcher;
            }

            watcher.EnableRaisingEvents = true;
            ScanDirectory(_assetsDirectory);

            lock (_lifecycleGate)
            {
                foreach (AssetImportRequest request in _startupRequests)
                {
                    scheduler.Enqueue(request);
                }

                _startupRequests.Clear();
                _state = AssetImporterState.Running;
            }
        }
        catch (Exception startError)
        {
            var rollbackErrors = new List<Exception>();
            if (watcher != null)
            {
                StopAndDisposeWatcher(watcher, rollbackErrors);
            }

            if (scheduler != null)
            {
                CaptureCleanup(scheduler.Dispose, rollbackErrors);
            }

            lock (_lifecycleGate)
            {
                _watcher = null;
                _scheduler = null;
                _startupRequests.Clear();
                _state = AssetImporterState.Faulted;
            }

            if (rollbackErrors.Count == 0)
            {
                throw;
            }

            rollbackErrors.Insert(0, startError);
            throw new AggregateException(
                "[AssetImporter] Startup failed and rollback reported additional errors.",
                rollbackErrors);
        }
    }

    private void ScanDirectory(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
        {
            if (IsIgnoredPath(file)) continue;
            BeforeInitialScanFile?.Invoke(file);
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

            if (meta.HasLegacyImporterType)
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

    internal void EnqueueWatcherEvent(
        string fullPath,
        AssetImportWorkKind kind,
        string oldFullPath = "")
    {
        string sourcePath = ResolveSourcePathForWatcherEvent(fullPath);
        string oldSourcePath = string.IsNullOrWhiteSpace(oldFullPath)
            ? string.Empty
            : ResolveSourcePathForWatcherEvent(oldFullPath);
        if (IsIgnoredPath(sourcePath) || !AssetPathPolicy.IsUnderAssetsRoot(sourcePath, _assetsDirectory))
        {
            return;
        }

        var request = new AssetImportRequest(kind, sourcePath, oldSourcePath);
        lock (_lifecycleGate)
        {
            if (_state == AssetImporterState.Starting)
            {
                _startupRequests.Add(request);
                return;
            }

            if (_state == AssetImporterState.Running)
            {
                _scheduler?.Enqueue(request);
            }
        }
    }

    private bool ProcessScheduledImport(AssetImportRequest request)
    {
        switch (request.Kind)
        {
            case AssetImportWorkKind.Created:
                ProcessFile(
                    request.FullPath,
                    publishEvent: true,
                    RuntimeAssetChangeKind.Created,
                    throwOnFailure: true);
                return true;
            case AssetImportWorkKind.Changed:
                ProcessFile(
                    request.FullPath,
                    publishEvent: true,
                    RuntimeAssetChangeKind.Changed,
                    throwOnFailure: true);
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

    internal Task WaitForIdleAsync()
    {
        lock (_lifecycleGate)
        {
            return _scheduler?.WaitForIdleAsync() ?? Task.CompletedTask;
        }
    }

    internal IReadOnlyList<AssetImportFailure> GetTerminalFailures()
    {
        lock (_lifecycleGate)
        {
            return _scheduler?.TerminalFailures ?? Array.Empty<AssetImportFailure>();
        }
    }

    internal void ProcessDeletedFile(string filePath)
    {
        if (!AssetPathPolicy.IsUnderAssetsRoot(filePath, _assetsDirectory))
        {
            return;
        }

        // Atomic replacement can queue Deleted before the replacement Created/Renamed
        // event. Filesystem state at processing time is authoritative.
        if (File.Exists(filePath))
        {
            ProcessFile(
                filePath,
                publishEvent: true,
                RuntimeAssetChangeKind.Changed,
                throwOnFailure: true);
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

    internal void ProcessRenamedFile(string oldFullPath, string newFullPath)
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

        string oldRelativePath = Path.GetRelativePath(_workspaceRoot, oldFullPath).Replace('\\', '/');
        string newRelativePath = Path.GetRelativePath(_workspaceRoot, newFullPath).Replace('\\', '/');
        string oldMetaPath = oldFullPath + ".meta";
        string newMetaPath = newFullPath + ".meta";
        Guid guid = Guid.Empty;

        if (AssetDatabase.Instance.TryGetGuid(oldRelativePath, out Guid existingGuid))
        {
            guid = existingGuid;
        }

        if (!WaitUntilReadable(newFullPath))
        {
            throw new IOException($"Renamed asset is locked or unavailable: {newFullPath}");
        }

        string metadataSourcePath = File.Exists(oldMetaPath)
            ? oldMetaPath
            : newMetaPath;
        AssetMetadata metadata = File.Exists(metadataSourcePath)
            ? SerializationUtil.Deserialize<AssetMetadata>(metadataSourcePath)
            : new AssetMetadata();
        if (guid == Guid.Empty)
        {
            guid = metadata.Guid == Guid.Empty ? Guid.NewGuid() : metadata.Guid;
        }

        metadata.Guid = guid;
        string assetType = string.IsNullOrWhiteSpace(metadata.AssetType)
            ? InferAssetType(newFullPath)
            : metadata.AssetType;
        string importer = string.IsNullOrWhiteSpace(metadata.Importer)
            ? string.IsNullOrWhiteSpace(metadata.ImporterType)
                ? InferImporter(newFullPath, assetType)
                : metadata.ImporterType
            : metadata.Importer;
        metadata.AssetType = assetType;
        metadata.Importer = importer;
        if (metadata.HasLegacyImporterType)
        {
            metadata.ImporterType = null;
        }

        byte[]? previousDestinationMetadata = File.Exists(newMetaPath)
            ? File.ReadAllBytes(newMetaPath)
            : null;
        string temporaryMetaPath = newMetaPath + "." + Guid.NewGuid().ToString("N") + ".tmp.meta";
        bool destinationMetadataPublished = false;
        try
        {
            SerializationUtil.Serialize(metadata, temporaryMetaPath);
            File.Move(temporaryMetaPath, newMetaPath, overwrite: true);
            destinationMetadataPublished = true;

            long lastModified = new DateTimeOffset(
                File.GetLastWriteTimeUtc(newFullPath)).ToUnixTimeSeconds();
            BeforeRenameRegistrationCommit?.Invoke();
            AssetDatabase.Instance.MoveAssetRegistration(
                guid,
                oldRelativePath,
                newRelativePath,
                assetType,
                importer,
                _packageId,
                lastModified);
        }
        catch (Exception commitError)
        {
            var rollbackErrors = new List<Exception>();
            try
            {
                if (File.Exists(temporaryMetaPath))
                {
                    File.Delete(temporaryMetaPath);
                }
            }
            catch (Exception cleanupError)
            {
                rollbackErrors.Add(cleanupError);
            }

            if (destinationMetadataPublished)
            {
                try
                {
                    if (previousDestinationMetadata == null)
                    {
                        File.Delete(newMetaPath);
                    }
                    else
                    {
                        File.WriteAllBytes(newMetaPath, previousDestinationMetadata);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, commitError);
                throw new InvalidOperationException(
                    "[AssetImporter] Rename publication failed and sidecar rollback was incomplete.",
                    new AggregateException(rollbackErrors));
            }

            throw;
        }

        if (!AssetDatabase.Instance.TryGetGuid(newRelativePath, out Guid registeredGuid))
        {
            throw new InvalidOperationException(
                $"[AssetImporter] Renamed asset '{newFullPath}' was not registered at its destination path.");
        }

        if (registeredGuid != guid)
        {
            throw new InvalidOperationException(
                $"[AssetImporter] Renamed asset '{newFullPath}' registered GUID '{registeredGuid}', expected preserved GUID '{guid}'.");
        }

        if (!string.Equals(
                Path.GetFullPath(oldMetaPath),
                Path.GetFullPath(newMetaPath),
                StringComparison.OrdinalIgnoreCase) &&
            File.Exists(oldMetaPath))
        {
            try
            {
                File.Delete(oldMetaPath);
            }
            catch (Exception ex)
            {
                ArisenEngine.Core.Diagnostics.Logger.Warning(
                    $"[AssetImporter] Rename committed, but stale source sidecar " +
                    $"'{oldMetaPath}' could not be deleted: {ex.Message}");
            }
        }

        PublishChange(new RuntimeAssetChangeEvent(
            RuntimeAssetChangeKind.Renamed,
            guid,
            assetType,
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
            ".hdr" => "Texture2D",
            ".arienvironment" => "EnvironmentTexture",
            ".arismaterial" => "Material",
            ".material" => "Material",
            ".arismodel" => "Model",
            ".model" => "Model",
            ".arisenscene" => "Scene",
            ".scene" => "Scene",
            ".arisrenderpipeline" => "RenderPipelineSettings",
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
            ".hdr" => "HdrTextureImporter",
            ".arienvironment" => "ArisenEnvironmentTextureImporter",
            ".arismaterial" or ".material" => "ArisenMaterialImporter",
            ".arismodel" or ".model" => "ArisenModelImporter",
            ".arisenscene" or ".scene" => "ArisenSceneImporter",
            ".arisrenderpipeline" => "ArisenRenderPipelineSettingsImporter",
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
        FileSystemWatcher? watcher;
        AssetImportScheduler? scheduler;
        lock (_lifecycleGate)
        {
            if (_state == AssetImporterState.Disposed)
            {
                return;
            }

            _state = AssetImporterState.Stopping;
            watcher = _watcher;
            scheduler = _scheduler;
            _watcher = null;
            _scheduler = null;
            _startupRequests.Clear();
        }

        var errors = new List<Exception>();
        try
        {
            scheduler?.RequestStop();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }

        if (watcher != null)
        {
            StopAndDisposeWatcher(watcher, errors);
        }

        try
        {
            scheduler?.Dispose();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }

        lock (_lifecycleGate)
        {
            _state = AssetImporterState.Disposed;
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("[AssetImporter] Shutdown failed.", errors);
        }
    }

    private void StopAndDisposeWatcher(
        FileSystemWatcher watcher,
        List<Exception> errors)
    {
        CaptureCleanup(() => watcher.EnableRaisingEvents = false, errors);
        CaptureCleanup(() => watcher.Created -= OnFileCreated, errors);
        CaptureCleanup(() => watcher.Deleted -= OnFileDeleted, errors);
        CaptureCleanup(() => watcher.Renamed -= OnFileRenamed, errors);
        CaptureCleanup(() => watcher.Changed -= OnFileChanged, errors);
        CaptureCleanup(watcher.Dispose, errors);
    }

    private static void CaptureCleanup(Action cleanup, List<Exception> errors)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }
    }
}
