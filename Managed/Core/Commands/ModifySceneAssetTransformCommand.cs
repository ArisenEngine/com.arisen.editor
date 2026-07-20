using System;
using ArisenEditor.Core.Services;
using ArisenEngine.Core.Automation;
using ArisenEngine.Resources.Serialization;

namespace ArisenEditor.Core.Commands;

internal sealed class ModifySceneAssetTransformCommand : ICommand
{
    private readonly IEditorSceneDocumentService m_DocumentService;
    private readonly int m_EntityIndex;
    private readonly string m_EntityName;
    private readonly SceneTransformInspection m_OldTransform;
    private readonly SceneTransformInspection m_NewTransform;
    private readonly Action<SceneTransformInspection>? m_OnApplied;
    private string? m_OldSource;
    private string? m_NewSource;

    public string Description => $"Modify scene transform '{m_EntityName}'";

    public ModifySceneAssetTransformCommand(
        IEditorSceneDocumentService documentService,
        int entityIndex,
        string entityName,
        SceneTransformInspection oldTransform,
        SceneTransformInspection newTransform,
        Action<SceneTransformInspection>? onApplied = null)
    {
        m_DocumentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
        m_EntityIndex = entityIndex;
        m_EntityName = string.IsNullOrWhiteSpace(entityName) ? $"Entity {entityIndex}" : entityName;
        m_OldTransform = oldTransform;
        m_NewTransform = newTransform;
        m_OnApplied = onApplied;
    }

    public void Execute()
    {
        if (m_NewSource != null)
        {
            ApplySource(m_NewSource, m_NewTransform);
            return;
        }

        m_OldSource = m_DocumentService.Current?.WorkingSource
            ?? throw new InvalidOperationException("There is no active editor scene document.");
        ApplyTransform(m_NewTransform);
        m_NewSource = m_DocumentService.Current?.WorkingSource
            ?? throw new InvalidOperationException("The editor scene document was lost after applying the transform.");
    }

    public void Undo()
    {
        if (m_OldSource == null)
        {
            throw new InvalidOperationException("The transform command has not been executed.");
        }

        ApplySource(m_OldSource, m_OldTransform);
    }

    private void ApplyTransform(SceneTransformInspection transform)
    {
        var result = m_DocumentService.ApplyEntityTransform(m_EntityIndex, transform);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Diagnostic);
        }

        m_OnApplied?.Invoke(transform);
    }

    private void ApplySource(string source, SceneTransformInspection transform)
    {
        var result = m_DocumentService.ApplyWorkingSource(source);
        if (!result.Success)
        {
            throw new InvalidOperationException(result.Diagnostic);
        }

        m_OnApplied?.Invoke(transform);
    }
}
