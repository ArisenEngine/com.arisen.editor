using System;
using ArisenEditor.Core.Services;
using ArisenEngine.Core.Automation;
using ArisenEngine.Resources.Serialization;

namespace ArisenEditor.Core.Commands;

internal sealed class ModifySceneAssetTransformCommand : ICommand
{
    private readonly IEditorSceneDocumentService m_DocumentService;
    private readonly Guid m_EntityGuid;
    private readonly string m_EntityName;
    private readonly SceneTransformInspection m_OldTransform;
    private readonly SceneTransformInspection m_NewTransform;
    private readonly Action<SceneTransformInspection>? m_OnApplied;
    private string? m_OldSource;
    private string? m_NewSource;
    public string Description => $"Modify scene transform '{m_EntityName}'";

    public ModifySceneAssetTransformCommand(
        IEditorSceneDocumentService documentService,
        Guid entityGuid,
        string entityName,
        SceneTransformInspection oldTransform,
        SceneTransformInspection newTransform,
        Action<SceneTransformInspection>? onApplied = null)
    {
        m_DocumentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
        if (entityGuid == Guid.Empty)
        {
            throw new ArgumentException("Scene transform commands require a stable entity GUID.", nameof(entityGuid));
        }

        m_EntityGuid = entityGuid;
        m_EntityName = string.IsNullOrWhiteSpace(entityName) ? $"Entity {entityGuid:D}" : entityName;
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
        var result = m_DocumentService.ApplyEntityTransform(m_EntityGuid, transform);
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

internal sealed class ModifyWorldCellEntityTransformCommand : ICommand
{
    private readonly IEditorWorldDocumentService m_DocumentService;
    private readonly WorldCellId m_CellId;
    private readonly Guid m_EntityGuid;
    private readonly string m_EntityName;
    private readonly SceneTransformInspection m_OldTransform;
    private readonly SceneTransformInspection m_NewTransform;
    private readonly Action<SceneTransformInspection>? m_OnApplied;
    private string? m_OldSource;
    private string? m_NewSource;

    public ModifyWorldCellEntityTransformCommand(
        IEditorWorldDocumentService documentService,
        WorldCellId cellId,
        Guid entityGuid,
        string entityName,
        SceneTransformInspection oldTransform,
        SceneTransformInspection newTransform,
        Action<SceneTransformInspection>? onApplied = null)
    {
        m_DocumentService = documentService ?? throw new ArgumentNullException(nameof(documentService));
        m_CellId = cellId;
        m_EntityGuid = entityGuid;
        m_EntityName = entityName;
        m_OldTransform = oldTransform;
        m_NewTransform = newTransform;
        m_OnApplied = onApplied;
    }

    public string Description => $"Modify world-cell transform '{m_EntityName}'";

    public void Execute()
    {
        if (m_NewSource != null)
        {
            ApplySource(m_NewSource, m_NewTransform);
            return;
        }

        EditorWorldCellDocumentState document = GetDocument();
        m_OldSource = document.SceneDocument.WorkingSource;
        ApplyTransform(m_NewTransform);
        m_NewSource = GetDocument().SceneDocument.WorkingSource;
    }

    public void Undo()
    {
        if (m_OldSource == null)
        {
            throw new InvalidOperationException("The world-cell transform command has not been executed.");
        }
        ApplySource(m_OldSource, m_OldTransform);
    }

    private EditorWorldCellDocumentState GetDocument()
    {
        return m_DocumentService.Current?.Cells.SingleOrDefault(cell => cell.CellId == m_CellId)
            ?? throw new InvalidOperationException($"World cell '{m_CellId}' is no longer open.");
    }

    private void ApplyTransform(SceneTransformInspection transform)
    {
        EditorWorldDocumentResult result = m_DocumentService.ApplyCellEntityTransform(
            m_CellId,
            m_EntityGuid,
            transform);
        if (!result.Success) throw new InvalidOperationException(result.Diagnostic);
        m_OnApplied?.Invoke(transform);
    }

    private void ApplySource(string source, SceneTransformInspection transform)
    {
        EditorWorldDocumentResult result = m_DocumentService.ApplyCellWorkingSource(m_CellId, source);
        if (!result.Success) throw new InvalidOperationException(result.Diagnostic);
        m_OnApplied?.Invoke(transform);
    }
}
