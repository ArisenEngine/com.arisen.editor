using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ArisenEngine;
using ArisenEditorFramework.UI.Menus;
using ArisenEditorFramework.Services;
using ArisenEditor.Core.Services;
using ReactiveUI;
using ArisenEngine.Core.Lifecycle;
using ArisenEditorFramework.Core;
using ArisenEditor.Views;
using ArisenEditor.Core.Views;

namespace ArisenEditor.ViewModels;

internal class AssetsBrowserViewModel : EditorPanelBase
{
    private string m_AssetsSearchText = String.Empty;

    public override string Title => "Assets Browser";
    public override string Id => "AssetsBrowser";
    public override object Content => new AssetsBrowserView { DataContext = this };

    public string AssetsSearchText
    {
        get => m_AssetsSearchText;
        set => this.RaiseAndSetIfChanged(ref m_AssetsSearchText, value);
    }

    private bool m_IsIconMode = true;
    public bool IsIconMode
    {
        get => m_IsIconMode;
        set => this.RaiseAndSetIfChanged(ref m_IsIconMode, value);
    }

    private double m_IconSize = 64.0;
    public double IconSize
    {
        get => m_IconSize;
        set 
        {
            this.RaiseAndSetIfChanged(ref m_IconSize, value);
            IsIconMode = value > 32.0;
        }
    }

    public ObservableCollection<MenuAction> CreateActions { get; } = new();
    public ObservableCollection<MenuAction> ContextActions { get; } = new();

    private readonly ObservableCollection<FileTreeNode> m_FolderSource = new();
    public ObservableCollection<FileTreeNode> FolderSource => m_FolderSource;
    
    private readonly ObservableCollection<FileTreeNode> m_AssetsItems = new();
    public ObservableCollection<FileTreeNode> AssetsSource => m_AssetsItems;

    public ObservableCollection<FileTreeNode> AssetsItems => m_AssetsItems;

    private FileTreeNode? m_SelectedFolder;
    public FileTreeNode? SelectedFolder
    {
        get => m_SelectedFolder;
        set 
        {
            this.RaiseAndSetIfChanged(ref m_SelectedFolder, value);
            FolderSelections = value != null ? new[] { value } : Array.Empty<FileTreeNode>();
            RefreshAssetsList();
        }
    }

    private FileTreeNode? m_SelectedAsset;
    public FileTreeNode? SelectedAsset
    {
        get => m_SelectedAsset;
        set 
        {
            this.RaiseAndSetIfChanged(ref m_SelectedAsset, value);
            AssetSelections = value != null ? new[] { value } : Array.Empty<FileTreeNode>();
        }
    }

    private FileTreeNode[] m_FolderSelections = Array.Empty<FileTreeNode>();
    public FileTreeNode[] FolderSelections
    {
        get => m_FolderSelections;
        set => this.RaiseAndSetIfChanged(ref m_FolderSelections, value);
    }

    private FileTreeNode[] m_AssetSelections = Array.Empty<FileTreeNode>();
    public FileTreeNode[] AssetSelections
    {
        get => m_AssetSelections;
        set => this.RaiseAndSetIfChanged(ref m_AssetSelections, value);
    }

    public AssetsBrowserViewModel()
    {
        // Register provider
        MenuRegistry.Instance.RegisterProvider(new AssetBrowserMenuProvider());

        InitializeFolderSource();
        InitializeAssetsSource();
        
        this.WhenAnyValue(x => x.AssetsSearchText)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RefreshAssetsList());
            
        // Select the root folder by default so the asset list is not empty
        if (FolderSource.Any())
        {
            var root = FolderSource.First();
            SelectedFolder = root;
            root.IsSelected = true;
        }
        
        this.WhenAnyValue(x => x.AssetSelections)
            .Subscribe(_ => RefreshMenus(AssetSelections?.FirstOrDefault()));

        RefreshAssetsList();
        RefreshMenus();
    }

    public void RefreshMenus(object? context = null)
    {
        CreateActions.Clear();
        foreach (var item in MenuRegistry.Instance.BuildMenu("Assets.CreateMenu", context))
            CreateActions.Add(item);

        ContextActions.Clear();
        foreach (var item in MenuRegistry.Instance.BuildMenu("Assets.ContextMenu", context))
            ContextActions.Add(item);
    }

    private void InitializeFolderSource()
    {
        EditorLog.Log("[AssetsBrowserViewModel] Initializing Folder Source...");
        m_FolderSource.Clear();
        
        var rootPath = FindAssetsRoot();
        EditorLog.Log($"[AssetsBrowserViewModel] Resolved Assets Root: {rootPath}");

        var rootNode = new FileTreeNode("Assets", rootPath, true, isRoot: true, false)
        {
            AllowDrag = false,
            AllowDrop = false
        };
        
        // Add "Packages" virtual folder - Create it FIRST
        var packagesNode = new FileTreeNode("Packages", string.Empty, true, false, true, true)
        {
            AllowDrag = false,
            AllowDrop = false
        };

        var packageSubsystem = EngineKernel.Instance.GetSubsystem<ArisenKernel.Packages.PackageSubsystem>();
        if (packageSubsystem != null)
        {
            var packages = packageSubsystem.GetAllPackages().ToList();
            EditorLog.Log($"[AssetsBrowserViewModel] Discovered {packages.Count} packages.");
            foreach (var package in packages)
            {
                // Determine if package is Local (Mutable) or Registry/Cache (Immutable)
                bool isImmutable = true;
                if (package.RootPath.Contains("Local", StringComparison.OrdinalIgnoreCase))
                {
                    isImmutable = false;
                }

                string pkgPath = Path.GetFullPath(package.RootPath);
                var pkgNode = new FileTreeNode(package.Id, pkgPath, true, false, isImmutable);
                pkgNode.Parent = packagesNode;
                
                // Add to both collections for visibility
                packagesNode.Children.Add(pkgNode);
                packagesNode.Folders.Add(pkgNode);
            }
        }

        // Add both root nodes to the source ONLY AFTER they are fully populated
        m_FolderSource.Add(rootNode);
        m_FolderSource.Add(packagesNode);
        
        // Explicitly set expanded state and refresh list
        rootNode.IsExpanded = true;
        packagesNode.IsExpanded = true;
        
        Dispatcher.UIThread.Post(() => {
             SelectedFolder = rootNode;
             RefreshAssetsList();
             EditorLog.Log("[AssetsBrowserViewModel] Folder Source Initialization Complete.");
        });
    }

    private string FindAssetsRoot()
    {
        var env = EngineKernel.Instance.GetSubsystem<EnvironmentSubsystem>();
        
        // Strategy 1: Environment Subsystem (Most Reliable)
        if (env != null && !string.IsNullOrEmpty(env.ProjectRoot))
        {
            var path = Path.GetFullPath(Path.Combine(env.ProjectRoot, "Assets"));
            if (Directory.Exists(path)) return path;
        }
        
        // Strategy 2: Current Directory (Fallthrough)
        var currentDir = Directory.GetCurrentDirectory();
        var searchDir = currentDir;
        while (!string.IsNullOrEmpty(searchDir))
        {
            var assetsPath = Path.Combine(searchDir, "Assets");
            if (Directory.Exists(assetsPath)) return Path.GetFullPath(assetsPath);
            
            var parent = Directory.GetParent(searchDir);
            if (parent == null) break;
            searchDir = parent.FullName;
        }
        
        // Strategy 3: Default relative
        return Path.GetFullPath("Assets");
    }

    private void InitializeAssetsSource()
    {
        // Now handled by ArisenListView directly against m_AssetsItems
    }

    public void FolderSelectionChanged()
    {
    }

    public void AssetsSelectionChanged()
    {
    }

    public void NavigateToFolder(string targetPath)
    {
        if (!FolderSource.Any()) return;
        var root = FolderSource.First();
        
        targetPath = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = root;
        
        while (current != null)
        {
            var currentPath = Path.GetFullPath(current.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (currentPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            {
                SelectedFolder = current;
                current.IsSelected = true;
                return;
            }

            // Expand to load children
            current.IsExpanded = true;
            
            bool foundNext = false;
            foreach (var child in current.Children.OfType<FileTreeNode>())
            {
                var childPath = Path.GetFullPath(child.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (targetPath.StartsWith(childPath, StringComparison.OrdinalIgnoreCase))
                {
                    current = child;
                    foundNext = true;
                    break;
                }
            }
            
            if (!foundNext)
                break; // Target path not found in children.
        }
    }

    private void RefreshAssetsList()
    {
        m_AssetsItems.Clear();
        
        foreach (var folder in FolderSelections)
        {
            if (!Directory.Exists(folder.Path)) continue;

            var entries = Directory.EnumerateFileSystemEntries(folder.Path, "*", SearchOption.TopDirectoryOnly);
            foreach (var entry in entries)
            {
                if (IsIgnoredPath(entry)) continue;

                bool isBranch = Directory.Exists(entry);
                var name = Path.GetFileName(entry);

                if (!string.IsNullOrEmpty(AssetsSearchText) && 
                    !name.Contains(AssetsSearchText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var node = new FileTreeNode(name, entry, isBranch)
                {
                    AssetGuid = AssetDatabaseService.Instance.GetGuidFromPath(entry)
                };
                m_AssetsItems.Add(node);
            }
        }
    }

    private bool IsIgnoredPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return true;

        var name = Path.GetFileName(path);
        if (name.StartsWith(".")) return true;

        // AssetsBrowser is usually top-level only, but let's be safe.
        // Also check OS attributes.
        try 
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0) return true;
        }
        catch { }

        return false;
    }
}
