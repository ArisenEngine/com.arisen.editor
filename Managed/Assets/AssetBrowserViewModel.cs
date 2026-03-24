using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using ReactiveUI;

namespace ArisenEditorFramework.Assets;

public class AssetBrowserViewModel : ReactiveObject
{
    private string _currentPath;
    private string _searchQuery = string.Empty;
    private AssetItemViewModel? _selectedItem;

    public ObservableCollection<AssetItemViewModel> Items { get; } = new();
    public ObservableCollection<string> Breadcrumbs { get; } = new();

    public string CurrentPath
    {
        get => _currentPath;
        set
        {
            if (Directory.Exists(value))
            {
                this.RaiseAndSetIfChanged(ref _currentPath, value);
                RefreshItems();
                UpdateBreadcrumbs();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchQuery, value);
            RefreshItems();
        }
    }

    public AssetItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public ReactiveCommand<Unit, Unit> NavigateBackCommand { get; }
    public ReactiveCommand<string, Unit> NavigateToCommand { get; }

    public AssetBrowserViewModel(string initialPath)
    {
        _currentPath = initialPath;
        
        NavigateBackCommand = ReactiveCommand.Create(() => 
        {
            var parent = Directory.GetParent(CurrentPath);
            if (parent != null) CurrentPath = parent.FullName;
        });

        NavigateToCommand = ReactiveCommand.Create<string>(path => 
        {
            CurrentPath = path;
        });

        RefreshItems();
        UpdateBreadcrumbs();
    }

    public void RefreshItems()
    {
        Items.Clear();
        if (!Directory.Exists(CurrentPath)) return;

        try
        {
            var entries = Directory.GetFileSystemEntries(CurrentPath)
                .Select(e => new AssetItemViewModel(e))
                .Where(e => string.IsNullOrEmpty(SearchQuery) || e.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name);

            foreach (var entry in entries)
            {
                Items.Add(entry);
            }
        }
        catch (Exception ex) 
        { 
            System.Diagnostics.Debug.WriteLine($"[AssetBrowser] Failed to access directory '{CurrentPath}': {ex.Message}");
        }
    }

    private void UpdateBreadcrumbs()
    {
        Breadcrumbs.Clear();
        var parts = CurrentPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        
        // This is a simplified version; in reality, we'd want clickable segments
        string runningPath = Path.IsPathRooted(CurrentPath) ? Path.GetPathRoot(CurrentPath) ?? "" : "";
        foreach (var part in parts)
        {
            runningPath = Path.Combine(runningPath, part);
            Breadcrumbs.Add(part); 
            // We might store the full path for each breadcrumb later
        }
    }
}
