using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive;
using ArisenEditor.Core.Services;
using ArisenEditor.Views;
using ArisenEditorFramework.Core;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Lifecycle;
using ArisenEngine.Resources.Serialization;
using ReactiveUI;

namespace ArisenEditor.ViewModels;

internal sealed class ProjectAssetOption
{
    public AssetRecord Asset { get; }
    public string Name { get; }
    public string RelativePath { get; }
    public string PackageId => Asset.PackageId;
    public string GuidText => Asset.Guid.ToString("D");

    public ProjectAssetOption(AssetRecord asset, string workspaceRoot)
    {
        Asset = asset;
        Name = Path.GetFileNameWithoutExtension(asset.SourcePath);
        RelativePath = string.IsNullOrWhiteSpace(workspaceRoot)
            ? asset.SourcePath
            : Path.GetRelativePath(workspaceRoot, asset.SourcePath).Replace('\\', '/');
    }
}

internal sealed class ProjectSettingsViewModel : EditorPanelBase
{
    private readonly EditorProjectService m_ProjectService = EditorProjectService.Instance;
    private readonly IAssetDatabase? m_AssetDatabase;
    private readonly IRuntimeSceneService? m_RuntimeSceneService;
    private Guid m_AppliedSceneGuid;
    private string m_AppliedScenePackageId = string.Empty;
    private Guid m_AppliedRenderPipelineGuid;
    private string m_AppliedRenderPipelinePackageId = string.Empty;
    private bool m_IsReloading;

    public override string Title => "Project Settings";
    public override string Id => "ProjectSettings";
    public override object Content => new ProjectSettingsView { DataContext = this };

    public string ProjectName { get; }
    public string EngineVersion { get; }
    public string ManifestPath => m_ProjectService.ManifestPath;

    public ObservableCollection<ProjectAssetOption> StartupScenes { get; } = new();
    public ObservableCollection<ProjectAssetOption> RenderPipelines { get; } = new();

    private ProjectAssetOption? m_SelectedStartupScene;
    public ProjectAssetOption? SelectedStartupScene
    {
        get => m_SelectedStartupScene;
        set
        {
            if (ReferenceEquals(m_SelectedStartupScene, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref m_SelectedStartupScene, value);
            RaiseSelectedSceneProperties();
            if (!m_IsReloading)
            {
                ClearError();
                UpdatePendingState();
            }
        }
    }

    public string SelectedSceneGuid => SelectedStartupScene?.GuidText ?? "None";
    public string SelectedScenePackage => SelectedStartupScene?.PackageId ?? "None";
    public string SelectedSceneSource => SelectedStartupScene?.RelativePath ?? "None";

    private ProjectAssetOption? m_SelectedRenderPipeline;
    public ProjectAssetOption? SelectedRenderPipeline
    {
        get => m_SelectedRenderPipeline;
        set
        {
            if (ReferenceEquals(m_SelectedRenderPipeline, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref m_SelectedRenderPipeline, value);
            RaiseSelectedRenderPipelineProperties();
            if (!m_IsReloading)
            {
                ClearError();
                UpdatePendingState();
            }
        }
    }

    public string SelectedRenderPipelineGuid => SelectedRenderPipeline?.GuidText ?? "None";
    public string SelectedRenderPipelinePackage => SelectedRenderPipeline?.PackageId ?? "None";
    public string SelectedRenderPipelineSource => SelectedRenderPipeline?.RelativePath ?? "None";

    private bool m_HasPendingChanges;
    public bool HasPendingChanges
    {
        get => m_HasPendingChanges;
        private set
        {
            this.RaiseAndSetIfChanged(ref m_HasPendingChanges, value);
            this.RaisePropertyChanged(nameof(CanApply));
            this.RaisePropertyChanged(nameof(CanRevert));
        }
    }

    public bool CanApply => HasPendingChanges &&
                            SelectedStartupScene != null &&
                            SelectedRenderPipeline != null;
    public bool CanRevert => HasPendingChanges;

    private string m_StatusText = string.Empty;
    public string StatusText
    {
        get => m_StatusText;
        private set => this.RaiseAndSetIfChanged(ref m_StatusText, value);
    }

    private string m_ErrorText = string.Empty;
    public string ErrorText
    {
        get => m_ErrorText;
        private set => this.RaiseAndSetIfChanged(ref m_ErrorText, value);
    }

    private bool m_HasError;
    public bool HasError
    {
        get => m_HasError;
        private set => this.RaiseAndSetIfChanged(ref m_HasError, value);
    }

    public ReactiveCommand<Unit, Unit> UseActiveSceneCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> RevertCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    internal ProjectSettingsViewModel()
    {
        var manifest = m_ProjectService.ActiveProject;
        ProjectName = manifest?.Name ?? "Unavailable";
        EngineVersion = manifest?.EngineVersion ?? "Unavailable";

        var services = EngineKernel.Instance.Services;
        services.TryGetService(out m_AssetDatabase);
        services.TryGetService(out m_RuntimeSceneService);

        UseActiveSceneCommand = ReactiveCommand.Create(UseActiveScene);
        ApplyCommand = ReactiveCommand.Create(Apply);
        RevertCommand = ReactiveCommand.Create(Revert);
        RefreshCommand = ReactiveCommand.Create(Reload);

        Reload();
    }

    private void Reload()
    {
        ClearError();
        var manifest = m_ProjectService.ActiveProject;
        if (manifest == null)
        {
            StartupScenes.Clear();
            RenderPipelines.Clear();
            SetError("No active workspace manifest is loaded.");
            return;
        }

        if (m_AssetDatabase == null)
        {
            StartupScenes.Clear();
            RenderPipelines.Clear();
            SetError("Runtime asset database service is unavailable.");
            return;
        }

        var eligiblePackages = new HashSet<string>(
            manifest.Packages.Select(package => package.Id),
            StringComparer.OrdinalIgnoreCase);
        string workspaceRoot = m_ProjectService.ProjectDirectory;
        var sceneOptions = m_AssetDatabase.Assets
            .Where(asset =>
                asset.Guid != Guid.Empty &&
                string.Equals(asset.AssetType, "Scene", StringComparison.OrdinalIgnoreCase) &&
                eligiblePackages.Contains(asset.PackageId))
            .OrderBy(asset => asset.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(asset => new ProjectAssetOption(asset, workspaceRoot))
            .ToArray();
        var pipelineOptions = m_AssetDatabase.Assets
            .Where(asset =>
                asset.Guid != Guid.Empty &&
                string.Equals(
                    asset.AssetType,
                    "RenderPipelineSettings",
                    StringComparison.OrdinalIgnoreCase) &&
                eligiblePackages.Contains(asset.PackageId))
            .OrderBy(asset => asset.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(asset => asset.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(asset => new ProjectAssetOption(asset, workspaceRoot))
            .ToArray();

        StartupScenes.Clear();
        foreach (var option in sceneOptions)
        {
            StartupScenes.Add(option);
        }
        RenderPipelines.Clear();
        foreach (var option in pipelineOptions)
        {
            RenderPipelines.Add(option);
        }

        m_AppliedSceneGuid = manifest.StartupScene?.Guid ?? Guid.Empty;
        m_AppliedScenePackageId = manifest.StartupScene?.PackageId ?? string.Empty;
        m_AppliedRenderPipelineGuid = manifest.RenderPipeline?.Guid ?? Guid.Empty;
        m_AppliedRenderPipelinePackageId = manifest.RenderPipeline?.PackageId ?? string.Empty;
        var appliedScene = FindOption(
            StartupScenes,
            m_AppliedSceneGuid,
            m_AppliedScenePackageId);
        var appliedPipeline = FindOption(
            RenderPipelines,
            m_AppliedRenderPipelineGuid,
            m_AppliedRenderPipelinePackageId);

        m_IsReloading = true;
        try
        {
            SelectedStartupScene = appliedScene;
            SelectedRenderPipeline = appliedPipeline;
        }
        finally
        {
            m_IsReloading = false;
        }

        HasPendingChanges = false;
        if (manifest.StartupScene is { IsValid: true } && appliedScene == null)
        {
            SetError(
                $"Configured startup scene {m_AppliedSceneGuid:D} from package " +
                $"'{m_AppliedScenePackageId}' is not an eligible indexed Scene asset.");
        }
        else if (manifest.RenderPipeline is { IsValid: true } && appliedPipeline == null)
        {
            SetError(
                $"Configured render pipeline {m_AppliedRenderPipelineGuid:D} from package " +
                $"'{m_AppliedRenderPipelinePackageId}' is not an eligible indexed RenderPipelineSettings asset.");
        }
        else if (StartupScenes.Count == 0)
        {
            SetError("No eligible package-owned Scene assets are indexed.");
        }
        else if (RenderPipelines.Count == 0)
        {
            SetError("No eligible package-owned RenderPipelineSettings assets are indexed.");
        }
        else
        {
            StatusText = appliedScene == null || appliedPipeline == null
                ? "Project asset selection is incomplete."
                : "Project asset selections match manifest.json.";
        }
    }

    private void UseActiveScene()
    {
        var activeScene = m_RuntimeSceneService?.ActiveScene;
        if (activeScene == null)
        {
            SetError("There is no active runtime scene.");
            return;
        }

        var option = FindOption(
            StartupScenes,
            activeScene.Scene.Guid,
            activeScene.Scene.PackageId);
        if (option == null)
        {
            SetError(
                $"Active scene {activeScene.Scene.Guid:D} from package " +
                $"'{activeScene.Scene.PackageId}' is not eligible as a workspace startup scene.");
            return;
        }

        SelectedStartupScene = option;
    }

    private void Apply()
    {
        if (!CanApply || SelectedStartupScene == null || SelectedRenderPipeline == null)
        {
            return;
        }

        var result = m_ProjectService.SetProjectAssets(
            SelectedStartupScene.Asset,
            SelectedRenderPipeline.Asset);
        if (!result.Success)
        {
            SetError(result.Diagnostic);
            return;
        }

        m_AppliedSceneGuid = SelectedStartupScene.Asset.Guid;
        m_AppliedScenePackageId = SelectedStartupScene.Asset.PackageId;
        m_AppliedRenderPipelineGuid = SelectedRenderPipeline.Asset.Guid;
        m_AppliedRenderPipelinePackageId = SelectedRenderPipeline.Asset.PackageId;
        HasPendingChanges = false;
        ClearError();
        StatusText = "Project asset selections saved to manifest.json for the next launch.";
    }

    private void Revert()
    {
        m_IsReloading = true;
        try
        {
            SelectedStartupScene = FindOption(
                StartupScenes,
                m_AppliedSceneGuid,
                m_AppliedScenePackageId);
            SelectedRenderPipeline = FindOption(
                RenderPipelines,
                m_AppliedRenderPipelineGuid,
                m_AppliedRenderPipelinePackageId);
        }
        finally
        {
            m_IsReloading = false;
        }

        HasPendingChanges = false;
        ClearError();
        StatusText = "Pending project asset changes reverted.";
    }

    private void UpdatePendingState()
    {
        bool sceneChanged = SelectedStartupScene == null
            ? m_AppliedSceneGuid != Guid.Empty
            : SelectedStartupScene.Asset.Guid != m_AppliedSceneGuid ||
              !string.Equals(
                  SelectedStartupScene.Asset.PackageId,
                  m_AppliedScenePackageId,
                  StringComparison.OrdinalIgnoreCase);
        bool pipelineChanged = SelectedRenderPipeline == null
            ? m_AppliedRenderPipelineGuid != Guid.Empty
            : SelectedRenderPipeline.Asset.Guid != m_AppliedRenderPipelineGuid ||
              !string.Equals(
                  SelectedRenderPipeline.Asset.PackageId,
                  m_AppliedRenderPipelinePackageId,
                  StringComparison.OrdinalIgnoreCase);
        HasPendingChanges = sceneChanged || pipelineChanged;
        StatusText = HasPendingChanges
            ? "Project asset changes are pending."
            : "Project asset selections match manifest.json.";
    }

    private static ProjectAssetOption? FindOption(
        IEnumerable<ProjectAssetOption> options,
        Guid guid,
        string packageId)
    {
        if (guid == Guid.Empty || string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        return options.FirstOrDefault(option =>
            option.Asset.Guid == guid &&
            string.Equals(option.Asset.PackageId, packageId, StringComparison.OrdinalIgnoreCase));
    }

    private void RaiseSelectedSceneProperties()
    {
        this.RaisePropertyChanged(nameof(SelectedSceneGuid));
        this.RaisePropertyChanged(nameof(SelectedScenePackage));
        this.RaisePropertyChanged(nameof(SelectedSceneSource));
    }

    private void RaiseSelectedRenderPipelineProperties()
    {
        this.RaisePropertyChanged(nameof(SelectedRenderPipelineGuid));
        this.RaisePropertyChanged(nameof(SelectedRenderPipelinePackage));
        this.RaisePropertyChanged(nameof(SelectedRenderPipelineSource));
    }

    private void ClearError()
    {
        HasError = false;
        ErrorText = string.Empty;
    }

    private void SetError(string error)
    {
        HasError = true;
        ErrorText = error;
        StatusText = string.Empty;
    }
}
