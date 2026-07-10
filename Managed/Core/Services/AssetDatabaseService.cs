using System;
using System.IO;
using System.Collections.Generic;
using ArisenEngine.Core.Lifecycle;
using ArisenKernel.Packages;
using ArisenEditor.Core.Assets;
using ArisenEngine;
using ArisenEngine.Core.Assets;
using ArisenKernel.Lifecycle;
using EditorAssetDatabase = ArisenEditor.Core.Assets.AssetDatabase;

namespace ArisenEditor.Core.Services;

/// <summary>
/// A compatibility wrapper that interfaces with the new SQLite AssetDatabase and AssetImporter.
/// This replaces the old memory-dictionary based AssetDatabaseService.
/// </summary>
public class AssetDatabaseService : IDisposable
{
    private static AssetDatabaseService? _instance;
    public static AssetDatabaseService Instance => _instance ??= new AssetDatabaseService();

    private readonly List<AssetImporter> _importers = new();
    private string m_ProjectRoot = string.Empty;

    private AssetDatabaseService() { }

    public void Initialize(string projectRoot)
    {
        m_ProjectRoot = projectRoot;
        
        // Ensure SQLite DB directory exists
        string cachePath = Path.Combine(projectRoot, ".arisen", "Cache");
        if (!Directory.Exists(cachePath)) Directory.CreateDirectory(cachePath);
        string dbPath = Path.Combine(cachePath, "AssetRegistry.db");
        
        ArisenEngine.Core.Diagnostics.Logger.Log($"[AssetDatabaseService] Initializing DB at: {dbPath}");
        EditorAssetDatabase.Initialize(dbPath);

        // 1. Collect all potential roots and filter out overlapping ones
        string assetsRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets"));
        if (!Directory.Exists(assetsRoot)) Directory.CreateDirectory(assetsRoot);
        
        var rootsToImport = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [assetsRoot] = "workspace"
        };

        // 2. Discover all loaded packages via PackageSubsystem
        var packageSubsystem = EngineKernel.Instance.GetSubsystem<PackageSubsystem>();
        if (packageSubsystem != null)
        {
            foreach (var package in packageSubsystem.GetAllPackages())
            {
                // We only scan the 'Assets' subfolder of a package to avoid indexing source code/headers
                string pkgAssets = Path.Combine(package.RootPath, "Assets");
                if (Directory.Exists(pkgAssets))
                {
                    rootsToImport[Path.GetFullPath(pkgAssets)] = package.Id;
                }
            }
        }

        // 3. Filter out redundant roots (e.g. if one is a subfolder of another)
        var sortedRoots = rootsToImport.OrderBy(r => r.Key.Length).ToList();
        var uniqueRoots = new List<(string Root, string PackageId)>();
        foreach (var entry in sortedRoots)
        {
            var root = entry.Key;
            var packageId = entry.Value;
            bool alreadyCovered = false;
            foreach (var existing in uniqueRoots)
            {
                if (IsSameOrChildPath(root, existing.Root))
                {
                    alreadyCovered = true;
                    break;
                }
            }
            if (!alreadyCovered)
            {
                uniqueRoots.Add((root, packageId));
            }
        }

        // 4. Start importers for unique roots
        foreach (var (root, packageId) in uniqueRoots)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[AssetDatabaseService] Starting importer for: {root} ({packageId})");
            var importer = new AssetImporter(root, projectRoot, packageId);
            importer.AssetChanged += OnImporterAssetChanged;
            _importers.Add(importer);
            importer.Start();
        }
    }

    public string? GetPathFromGuid(Guid guid)
    {
        return ArisenEditor.Core.Assets.AssetDatabase.Instance.GetPath(guid);
    }

    public Guid GetGuidFromPath(string path)
    {
        // Calculate path relative to the Workspace Root
        string relativePath = Path.GetRelativePath(m_ProjectRoot, path).Replace('\\', '/');
        if (ArisenEditor.Core.Assets.AssetDatabase.Instance.TryGetGuid(relativePath, out var guid))
        {
            return guid;
        }
        return Guid.Empty;
    }

    public string GetAssetsRoot() => Path.Combine(m_ProjectRoot, "Assets");

    private void OnImporterAssetChanged(AssetChangeEvent change)
    {
        if (!EngineKernel.Instance.Services.TryGetService<IAssetDatabase>(out var database) || database == null)
        {
            return;
        }

        database.NotifyAssetChanged(change);

        if (change.Kind == AssetChangeKind.Created
            || change.Kind == AssetChangeKind.Changed
            || change.Kind == AssetChangeKind.Deleted
            || change.Kind == AssetChangeKind.Renamed)
        {
            database.InvalidateCookedAssets(change.Guid);
        }
    }

    private static bool IsSameOrChildPath(string path, string potentialParent)
    {
        var normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedParent = Path.GetFullPath(potentialParent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedParent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(
                normalizedParent + Path.AltDirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        foreach (var importer in _importers)
        {
            importer.AssetChanged -= OnImporterAssetChanged;
            importer.Dispose();
        }
        _importers.Clear();
        ArisenEditor.Core.Assets.AssetDatabase.Instance?.Dispose();
    }
}
