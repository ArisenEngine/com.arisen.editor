using System;
using ArisenEditor.Core.Assets;
using ArisenEngine.Core.Automation;
using ArisenEngine.Resources.Serialization;

namespace ArisenEditor.Core.Commands;

public sealed class ModifySceneAssetTransformCommand : ICommand
{
    private readonly string m_SourcePath;
    private readonly int m_EntityIndex;
    private readonly string m_EntityName;
    private readonly SceneTransformInspection m_OldTransform;
    private readonly SceneTransformInspection m_NewTransform;
    private readonly Action<SceneTransformInspection>? m_OnApplied;

    public string Description => $"Modify scene transform '{m_EntityName}'";

    public ModifySceneAssetTransformCommand(
        string sourcePath,
        int entityIndex,
        string entityName,
        SceneTransformInspection oldTransform,
        SceneTransformInspection newTransform,
        Action<SceneTransformInspection>? onApplied = null)
    {
        m_SourcePath = sourcePath;
        m_EntityIndex = entityIndex;
        m_EntityName = string.IsNullOrWhiteSpace(entityName) ? $"Entity {entityIndex}" : entityName;
        m_OldTransform = oldTransform;
        m_NewTransform = newTransform;
        m_OnApplied = onApplied;
    }

    public void Execute()
    {
        Apply(m_NewTransform);
    }

    public void Undo()
    {
        Apply(m_OldTransform);
    }

    private void Apply(SceneTransformInspection transform)
    {
        if (!AssetPathPolicy.IsEditableAssetPath(m_SourcePath))
        {
            throw new InvalidOperationException(
                $"Only source scene assets under workspace/package Assets roots can be edited from the editor: {m_SourcePath}");
        }

        var result = SceneAssetLoader.UpdateEntityTransform(m_SourcePath, m_EntityIndex, transform);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Diagnostic);
        }

        m_OnApplied?.Invoke(transform);
    }
}
