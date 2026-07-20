using System;
using System.Linq;
using ArisenEngine.Core.Lifecycle;
using ArisenEngine.Core.Serialization;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Assets;
using System.IO;
using ArisenEditor.Core.Models;
using ArisenKernel.Packages;

namespace ArisenEditor.Core.Services;

/// <summary>
/// Service for managing the active project's settings and manifest within the editor.
/// </summary>
public class EditorProjectService
{
    private static readonly Lazy<EditorProjectService> _instance = new(() => new EditorProjectService());
    private readonly ProjectSubsystem? m_ProjectSubsystem;
    public static EditorProjectService Instance => _instance.Value;

    public ProjectManifest? ActiveProject => m_ProjectSubsystem?.ActiveProject;

    public string ProjectDirectory => m_ProjectSubsystem?.ProjectDir ?? string.Empty;

    public string ManifestPath
    {
        get
        {
            return string.IsNullOrWhiteSpace(ProjectDirectory)
                ? string.Empty
                : Path.Combine(ProjectDirectory, "manifest.json");
        }
    }

    public EditorUserSettings UserSettings { get; private set; } = new();

    private EditorProjectService() 
    {
        EngineKernel.Instance.Services.TryGetService(out m_ProjectSubsystem);
    }

    internal WorkspaceManifestEditResult SetProjectAssets(
        AssetRecord scene,
        AssetRecord renderPipeline)
    {
        var manifest = ActiveProject;
        if (manifest == null)
        {
            return new WorkspaceManifestEditResult(false, "No active workspace manifest is loaded.");
        }

        if (scene.Guid == Guid.Empty ||
            !string.Equals(scene.AssetType, "Scene", StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceManifestEditResult(false, "Startup scene selection must reference an indexed Scene asset.");
        }

        if (renderPipeline.Guid == Guid.Empty ||
            !string.Equals(
                renderPipeline.AssetType,
                "RenderPipelineSettings",
                StringComparison.OrdinalIgnoreCase))
        {
            return new WorkspaceManifestEditResult(
                false,
                "Render-pipeline selection must reference an indexed RenderPipelineSettings asset.");
        }

        if (string.IsNullOrWhiteSpace(scene.PackageId) ||
            !manifest.Packages.Any(package =>
                string.Equals(package.Id, scene.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            return new WorkspaceManifestEditResult(
                false,
                $"Scene package '{scene.PackageId}' is not selected in the workspace base Packages list.");
        }
        if (string.IsNullOrWhiteSpace(renderPipeline.PackageId) ||
            !manifest.Packages.Any(package =>
                string.Equals(
                    package.Id,
                    renderPipeline.PackageId,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return new WorkspaceManifestEditResult(
                false,
                $"Render-pipeline package '{renderPipeline.PackageId}' is not selected in the workspace base Packages list.");
        }

        if (string.IsNullOrWhiteSpace(ManifestPath))
        {
            return new WorkspaceManifestEditResult(false, "Workspace manifest path is unavailable.");
        }

        var result = WorkspaceManifestEditor.SetProjectAssets(
            ManifestPath,
            new WorkspaceProjectAssetSelection(scene.Guid, scene.PackageId),
            new WorkspaceProjectAssetSelection(renderPipeline.Guid, renderPipeline.PackageId));
        if (!result.Success)
        {
            Logger.Error($"[EditorProjectService] {result.Diagnostic}");
            return result;
        }

        manifest.StartupScene = new ProjectAssetReference
        {
            Guid = scene.Guid,
            PackageId = scene.PackageId
        };
        manifest.RenderPipeline = new ProjectAssetReference
        {
            Guid = renderPipeline.Guid,
            PackageId = renderPipeline.PackageId
        };
        Logger.Log(
            $"[EditorProjectService] Project assets updated | StartupScene: {scene.Guid:D} ({scene.PackageId}) | RenderPipeline: {renderPipeline.Guid:D} ({renderPipeline.PackageId}).");
        return result;
    }

    public void LoadUserSettings()
    {
        var env = EngineKernel.Instance.GetSubsystem<EnvironmentSubsystem>();
        string libraryPath = Path.Combine(env?.ProjectRoot ?? string.Empty, ".Cache");
        string settingsPath = Path.Combine(libraryPath, "EditorUserSettings.arisen_settings");

        if (File.Exists(settingsPath))
        {
            try
            {
                UserSettings = SerializationUtil.Deserialize<EditorUserSettings>(settingsPath);
            }
            catch (Exception ex)
            {
                Logger.Error($"[EditorProjectService] Failed to load user settings: {ex.Message}");
                UserSettings = new EditorUserSettings();
            }
        }
        else
        {
            UserSettings = new EditorUserSettings();
        }
    }

    public void SaveUserSettings()
    {
        var env = EngineKernel.Instance.GetSubsystem<EnvironmentSubsystem>();
        string libraryPath = Path.Combine(env?.ProjectRoot ?? string.Empty, ".Cache");
        
        if (!Directory.Exists(libraryPath))
        {
            Directory.CreateDirectory(libraryPath);
        }

        string settingsPath = Path.Combine(libraryPath, "EditorUserSettings.arisen_settings");
        
        try
        {
            SerializationUtil.Serialize(UserSettings, settingsPath);
        }
        catch (Exception ex)
        {
            Logger.Error($"[EditorProjectService] Failed to save user settings: {ex.Message}");
        }
    }
}
