using System;
using System.IO;
using System.Collections.Generic;
using ArisenEngine.Core.Lifecycle;
using ArisenKernel.Packages;
using ArisenEditor.Core.Assets;
using ArisenEngine;

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
        string cachePath = Path.Combine(projectRoot, ".Cache");
        if (!Directory.Exists(cachePath)) Directory.CreateDirectory(cachePath);
        string dbPath = Path.Combine(cachePath, "AssetRegistry.db");
        
        ArisenEngine.Core.Diagnostics.Logger.Log($"[AssetDatabaseService] Initializing DB at: {dbPath}");
        AssetDatabase.Initialize(dbPath);

        // 1. Collect all potential roots and filter out overlapping ones
        string assetsRoot = Path.GetFullPath(Path.Combine(projectRoot, "Assets"));
        if (!Directory.Exists(assetsRoot)) Directory.CreateDirectory(assetsRoot);
        
        var rootsToImport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        rootsToImport.Add(assetsRoot);

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
                    rootsToImport.Add(Path.GetFullPath(pkgAssets));
                }
            }
        }

        // 3. Filter out redundant roots (e.g. if one is a subfolder of another)
        var sortedRoots = rootsToImport.OrderBy(r => r.Length).ToList();
        var uniqueRoots = new List<string>();
        foreach (var root in sortedRoots)
        {
            bool alreadyCovered = false;
            foreach (var existing in uniqueRoots)
            {
                if (root.StartsWith(existing, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyCovered = true;
                    break;
                }
            }
            if (!alreadyCovered)
            {
                uniqueRoots.Add(root);
            }
        }

        // 4. Start importers for unique roots
        foreach (var root in uniqueRoots)
        {
            ArisenEngine.Core.Diagnostics.Logger.Log($"[AssetDatabaseService] Starting importer for: {root}");
            var importer = new AssetImporter(root, projectRoot);
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

    public void Dispose()
    {
        foreach (var importer in _importers)
        {
            importer.Dispose();
        }
        _importers.Clear();
        ArisenEditor.Core.Assets.AssetDatabase.Instance?.Dispose();
    }
}
