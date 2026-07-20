using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using ArisenEditorFramework.UI.Controls;
using ArisenEditor.ViewModels;
using ArisenEditor.Core.Assets;
using ArisenEditor.Core.Services;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Lifecycle;

namespace ArisenEditor.Views;

public partial class AssetsBrowserView : UserControl
{
    public AssetsBrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        var folderGrid = this.FindControl<ArisenTreeView>("FolderGrid");
        if (folderGrid != null)
        {
            folderGrid.ItemDoubleTapped += OnFolderDoubleTapped;
        }

        var assetsGrid = this.FindControl<ArisenListView>("AssetsGrid");
        if (assetsGrid != null)
        {
            assetsGrid.ItemDoubleTapped += OnAssetDoubleTapped;
        }
    }

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AssetsBrowserViewModel vm && vm.SelectedFolder != null)
        {
            var node = vm.SelectedFolder;
            if (node.IsBranch)
            {
                node.IsExpanded = !node.IsExpanded;
            }
        }
    }

    private async void OnAssetDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AssetsBrowserViewModel vm && vm.SelectedAsset != null)
        {
            var node = vm.SelectedAsset;
            if (node.IsBranch)
            {
                vm.NavigateToFolder(node.Path);
            }
            else if (IsRuntimeScenePath(node.Path))
            {
                await RequestRuntimeSceneLoad(node);
            }
        }
    }

    private static bool IsRuntimeScenePath(string path)
    {
        var extension = Path.GetExtension(path);
        return string.Equals(extension, ".arisenscene", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".scene", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RequestRuntimeSceneLoad(FileTreeNode node)
    {
        var services = EngineKernel.Instance.Services;
        if (!services.TryGetService<IEditorSceneDocumentService>(out var documentService) || documentService == null ||
            !services.TryGetService<IAssetDatabase>(out var assetDatabase) || assetDatabase == null)
        {
            EditorLog.Error("[AssetsBrowser] Editor scene document services are unavailable.");
            return;
        }

        if (!TryResolveRuntimeSceneAsset(node, assetDatabase, out var asset))
        {
            EditorLog.Error($"[AssetsBrowser] Scene asset is not indexed: {node.Path}");
            return;
        }

        var scene = new AssetRef<SceneSourceAsset>(asset.Guid, asset.AssetType, asset.PackageId);
        if (await EditorSceneDocumentInteraction.TryOpenSceneAsync(documentService, scene))
        {
            EditorLog.Log($"[AssetsBrowser] Queued scene '{asset.SourcePath}' for frame-boundary activation.");
        }
    }

    private static bool TryResolveRuntimeSceneAsset(
        FileTreeNode node,
        IAssetDatabase assetDatabase,
        out AssetRecord asset)
    {
        var guid = node.AssetGuid;
        if (guid == Guid.Empty)
        {
            guid = AssetDatabaseService.Instance.GetGuidFromPath(node.Path);
        }

        if (guid != Guid.Empty &&
            assetDatabase.TryGetAsset(guid, out asset) &&
            IsSceneAsset(asset))
        {
            return true;
        }

        var normalizedNodePath = AssetPathPolicy.NormalizeFullPath(node.Path);
        foreach (var candidate in assetDatabase.Assets)
        {
            if (IsSceneAsset(candidate) &&
                string.Equals(
                    AssetPathPolicy.NormalizeFullPath(candidate.SourcePath),
                    normalizedNodePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                asset = candidate;
                return true;
            }
        }

        asset = null!;
        return false;
    }

    private static bool IsSceneAsset(AssetRecord asset)
    {
        return string.Equals(asset.AssetType, "Scene", StringComparison.OrdinalIgnoreCase);
    }

    private void OnAssetsPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (DataContext is AssetsBrowserViewModel vm)
            {
                double zoomDelta = e.Delta.Y * 10.0;
                double newSize = vm.IconSize + zoomDelta;
                vm.IconSize = Math.Clamp(newSize, 32.0, 128.0);
                e.Handled = true;
            }
        }
    }
}
