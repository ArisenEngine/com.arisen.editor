using System.Globalization;
using System.Text;
using ArisenEditor.Core.Assets;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Automation;
using ArisenEngine.Resources.Serialization;
using YamlDotNet.RepresentationModel;

namespace ArisenEditor.Core.Services;

internal readonly record struct EditorWorldDocumentResult(
    bool Success,
    bool RequiresUserResolution,
    string Diagnostic)
{
    public static EditorWorldDocumentResult Ok(string diagnostic) =>
        new(true, false, diagnostic);

    public static EditorWorldDocumentResult Fail(string diagnostic) =>
        new(false, false, diagnostic);

    public static EditorWorldDocumentResult RequiresResolution(string diagnostic) =>
        new(false, true, diagnostic);
}

internal readonly record struct EditorWorldSelectionId(
    Guid SceneGuid,
    WorldCellId CellId,
    Guid EntityGuid)
{
    public bool IsValid => SceneGuid != Guid.Empty && EntityGuid != Guid.Empty;
}

internal sealed record EditorWorldSceneDocumentState(
    AssetRef<SceneSourceAsset> Scene,
    WorldCellId CellId,
    bool IsPersistent,
    string Name,
    string SourcePath,
    string SavedSource,
    string WorkingSource,
    bool HasUtf8Bom,
    bool IsEditable,
    bool HasExternalChanges,
    long Revision,
    Guid TransactionId,
    SceneInspectionResult Inspection)
{
    public bool IsDirty => !string.Equals(SavedSource, WorkingSource, StringComparison.Ordinal);

    public SceneSourceSnapshot CreateSnapshot() =>
        new(Scene, SourcePath, WorkingSource, Revision);
}

internal sealed record EditorWorldCellDocumentState(
    WorldCellDescriptor Descriptor,
    EditorWorldSceneDocumentState SceneDocument,
    WorldCellStreamingSnapshot Streaming,
    bool IsEditPinned)
{
    public WorldCellId CellId => Descriptor.Id;
    public bool IsDirty => SceneDocument.IsDirty;
    public bool HasExternalChanges => SceneDocument.HasExternalChanges;
}

internal sealed record EditorWorldDocumentState(
    AssetRef<WorldSourceAsset> World,
    string Name,
    string SourcePath,
    string SavedSource,
    string WorkingSource,
    bool HasUtf8Bom,
    bool IsEditable,
    bool HasExternalChanges,
    long Revision,
    WorldDescriptor Descriptor,
    EditorWorldSceneDocumentState PersistentScene,
    IReadOnlyList<EditorWorldCellDocumentState> Cells,
    WorldCellId SelectedCellId,
    WorldCellId FocusedCellId,
    EditorWorldSelectionId? Selection,
    IReadOnlySet<string> ExpandedNodeIds,
    WorldStreamingMetrics Metrics,
    IReadOnlyList<WorldStreamingDiagnostic> Diagnostics)
{
    public bool IsDirty =>
        !string.Equals(SavedSource, WorkingSource, StringComparison.Ordinal) ||
        PersistentScene.IsDirty ||
        Cells.Any(cell => cell.IsDirty);
}

internal interface IEditorWorldDocumentService : IDisposable
{
    EditorWorldDocumentState? Current { get; }
    string LastDiagnostic { get; }

    event Action<EditorWorldDocumentState?>? StateChanged;
    event Action<string>? OperationFailed;
    event Action<WorldCellId, WorldPosition>? FocusRequested;

    EditorWorldDocumentResult RequestOpenWorld(AssetRef<WorldSourceAsset> world);
    EditorWorldDocumentResult ApplyWorldWorkingSource(string sourceText);
    EditorWorldDocumentResult ApplyCellWorkingSource(WorldCellId cellId, string sourceText);
    EditorWorldDocumentResult ApplyCellEntityTransform(
        WorldCellId cellId,
        Guid entityGuid,
        SceneTransformInspection transform);
    EditorWorldDocumentResult MoveEntityToCell(
        WorldCellId sourceCellId,
        WorldCellId targetCellId,
        Guid entityGuid);
    EditorWorldDocumentResult SaveCell(WorldCellId cellId);
    EditorWorldDocumentResult SaveAll();
    EditorWorldDocumentResult DiscardCellChanges(WorldCellId cellId);
    EditorWorldDocumentResult ReimportCell(WorldCellId cellId);
    bool LoadCellForEditing(WorldCellId cellId);
    bool UnloadCellForEditing(WorldCellId cellId);
    bool FocusCell(WorldCellId cellId);
    void SelectCell(WorldCellId cellId);
    void SetStableSelection(EditorWorldSelectionId? selection);
    void SetExpanded(string stableNodeId, bool expanded);
}

internal sealed class EditorWorldDocumentService : IEditorWorldDocumentService
{
    private readonly object m_Gate = new();
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly IRuntimeWorldStreamingService m_Streaming;
    private readonly ICommandManager m_CommandManager;
    private readonly IEditorSceneDocumentService? m_SceneDocuments;
    private readonly Dictionary<WorldCellId, EditorWorldSceneDocumentState> m_CellDocuments = new();
    private readonly Dictionary<WorldCellId, WorldCellStreamingSnapshot> m_CellStreaming = new();
    private readonly HashSet<WorldCellId> m_EditPins = new();
    private readonly HashSet<string> m_ExpandedNodeIds = new(StringComparer.Ordinal);
    private EditorWorldDocumentState? m_Current;
    private EditorWorldSelectionId? m_Selection;
    private WorldCellId m_SelectedCellId;
    private WorldCellId m_FocusedCellId;
    private string m_LastDiagnostic = string.Empty;
    private long m_NextRevision;
    private bool m_Disposed;

    public EditorWorldDocumentState? Current => Volatile.Read(ref m_Current);
    public string LastDiagnostic => Volatile.Read(ref m_LastDiagnostic);

    public event Action<EditorWorldDocumentState?>? StateChanged;
    public event Action<string>? OperationFailed;
    public event Action<WorldCellId, WorldPosition>? FocusRequested;

    public EditorWorldDocumentService(
        IAssetDatabase assetDatabase,
        IRuntimeWorldStreamingService streaming,
        ICommandManager commandManager,
        IEditorSceneDocumentService? sceneDocuments = null)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Streaming = streaming ?? throw new ArgumentNullException(nameof(streaming));
        m_CommandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));
        m_SceneDocuments = sceneDocuments;
        m_Streaming.ActiveWorldChanged += OnActiveWorldChanged;
        m_Streaming.CellStateChanged += OnCellStateChanged;
        m_AssetDatabase.AssetChanged += OnAssetChanged;
        if (m_SceneDocuments != null) m_SceneDocuments.StateChanged += OnPersistentSceneDocumentChanged;

        if (m_Streaming.ActiveWorldAsset is { } world && m_Streaming.ActiveWorld is { } descriptor)
        {
            RefreshWorldDocument(world, descriptor, preserveAuthoringState: false);
        }
    }

    public EditorWorldDocumentResult RequestOpenWorld(AssetRef<WorldSourceAsset> world)
    {
        if (!world.IsValid)
        {
            return EditorWorldDocumentResult.Fail("World activation requires a valid asset reference.");
        }

        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (m_Current is { IsDirty: true })
            {
                return EditorWorldDocumentResult.RequiresResolution(
                    $"World '{m_Current.Name}' has unsaved authoring changes.");
            }
        }

        RuntimeWorldLoadResult result = m_Streaming.LoadWorld(world);
        if (!result.Success)
        {
            SetFailure(result.Diagnostic);
            return EditorWorldDocumentResult.Fail(result.Diagnostic);
        }

        return EditorWorldDocumentResult.Ok(
            $"Opened world '{world.Guid:D}' with {result.CellCount} cell(s).");
    }

    public EditorWorldDocumentResult ApplyWorldWorkingSource(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return EditorWorldDocumentResult.Fail("World working source cannot be empty.");
        }

        EditorWorldDocumentState publish;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            EditorWorldDocumentState current = m_Current
                ?? throw new InvalidOperationException("There is no active editor world document.");
            if (!current.IsEditable)
            {
                return EditorWorldDocumentResult.Fail(
                    $"World '{current.SourcePath}' is generated or outside an editable Assets root.");
            }

            WorldDescriptorLoadResult validation = WorldDescriptorLoader.LoadSourceText(
                m_AssetDatabase,
                current.World.Guid,
                current.SourcePath,
                sourceText);
            if (!validation.Success || validation.Descriptor == null)
            {
                SetDiagnosticLocked(validation.Diagnostic);
                return EditorWorldDocumentResult.Fail(validation.Diagnostic);
            }

            publish = current with
            {
                Name = validation.Descriptor.Name,
                WorkingSource = sourceText,
                Revision = ++m_NextRevision,
                Descriptor = validation.Descriptor
            };
            Volatile.Write(ref m_Current, publish);
            SetDiagnosticLocked(string.Empty);
        }

        PublishState(publish);
        return EditorWorldDocumentResult.Ok($"Staged world '{publish.Name}'.");
    }

    public EditorWorldDocumentResult ApplyCellWorkingSource(
        WorldCellId cellId,
        string sourceText)
    {
        return ApplyCellWorkingSourceCore(cellId, sourceText, Guid.Empty, publishPreview: true);
    }

    public EditorWorldDocumentResult ApplyCellEntityTransform(
        WorldCellId cellId,
        Guid entityGuid,
        SceneTransformInspection transform)
    {
        EditorWorldSceneDocumentState document;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!m_CellDocuments.TryGetValue(cellId, out document!))
            {
                return EditorWorldDocumentResult.Fail($"World cell '{cellId}' is not part of the active document.");
            }
        }

        SceneAssetEditResult edit = SceneAssetLoader.UpdateEntityTransformSource(
            document.SourcePath,
            document.WorkingSource,
            entityGuid,
            transform);
        return edit.Success
            ? ApplyCellWorkingSource(cellId, edit.UpdatedSource)
            : EditorWorldDocumentResult.Fail(edit.Diagnostic);
    }

    public EditorWorldDocumentResult MoveEntityToCell(
        WorldCellId sourceCellId,
        WorldCellId targetCellId,
        Guid entityGuid)
    {
        if (!sourceCellId.IsValid || !targetCellId.IsValid || sourceCellId == targetCellId)
        {
            return EditorWorldDocumentResult.Fail(
                "Moving a world entity requires distinct valid source and target cells.");
        }
        if (entityGuid == Guid.Empty)
        {
            return EditorWorldDocumentResult.Fail("Moving a world entity requires a stable entity GUID.");
        }

        try
        {
            m_CommandManager.Execute(new MoveEntityBetweenCellsCommand(
                this,
                sourceCellId,
                targetCellId,
                entityGuid));
            return EditorWorldDocumentResult.Ok(
                $"Moved entity subtree '{entityGuid:D}' to cell '{targetCellId}'.");
        }
        catch (Exception ex)
        {
            SetFailure(ex.Message);
            return EditorWorldDocumentResult.Fail(ex.Message);
        }
    }

    public EditorWorldDocumentResult SaveCell(WorldCellId cellId)
    {
        EditorWorldSceneDocumentState document;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!m_CellDocuments.TryGetValue(cellId, out document!))
            {
                return EditorWorldDocumentResult.Fail($"World cell '{cellId}' is not part of the active document.");
            }

            if (document.TransactionId != Guid.Empty &&
                m_CellDocuments.Values.Count(item => item.TransactionId == document.TransactionId) > 1)
            {
                return EditorWorldDocumentResult.RequiresResolution(
                    "This cell participates in a multi-cell source transaction. Use Save All so every scene commits together.");
            }
        }

        return SaveDocuments([cellId], includeWorld: false);
    }

    public EditorWorldDocumentResult SaveAll()
    {
        WorldCellId[] dirtyCells;
        bool dirtyWorld;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            EditorWorldDocumentState current = m_Current
                ?? throw new InvalidOperationException("There is no active editor world document.");
            dirtyCells = m_CellDocuments.Values
                .Where(document => document.IsDirty)
                .Select(document => document.CellId)
                .Order()
                .ToArray();
            dirtyWorld = !string.Equals(
                current.SavedSource,
                current.WorkingSource,
                StringComparison.Ordinal);
        }

        if (m_SceneDocuments?.Current is { IsDirty: true })
        {
            EditorSceneDocumentResult persistentSave = m_SceneDocuments.Save();
            if (!persistentSave.Success)
            {
                return EditorWorldDocumentResult.Fail(persistentSave.Diagnostic);
            }
        }

        return SaveDocuments(dirtyCells, dirtyWorld);
    }

    public EditorWorldDocumentResult DiscardCellChanges(WorldCellId cellId)
    {
        EditorWorldSceneDocumentState disk;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!m_CellDocuments.TryGetValue(cellId, out EditorWorldSceneDocumentState? current))
            {
                return EditorWorldDocumentResult.Fail($"World cell '{cellId}' is not part of the active document.");
            }
            if (!TryCreateSceneDocument(
                    current.Scene,
                    cellId,
                    isPersistent: false,
                    out disk,
                    out string diagnostic))
            {
                SetDiagnosticLocked(diagnostic);
                return EditorWorldDocumentResult.Fail(diagnostic);
            }

            m_CellDocuments[cellId] = disk;
            RefreshStateLocked();
            SetDiagnosticLocked(string.Empty);
        }

        m_Streaming.SetCellPreviewSource(cellId, null);
        PublishState(Current);
        return EditorWorldDocumentResult.Ok($"Discarded changes for cell '{cellId}'.");
    }

    public EditorWorldDocumentResult ReimportCell(WorldCellId cellId)
    {
        EditorWorldSceneDocumentState document;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!m_CellDocuments.TryGetValue(cellId, out document!))
            {
                return EditorWorldDocumentResult.Fail($"World cell '{cellId}' is not part of the active document.");
            }
            if (document.IsDirty)
            {
                return EditorWorldDocumentResult.RequiresResolution(
                    $"Save or discard cell '{cellId}' before reimporting it.");
            }
        }

        int invalidated = m_AssetDatabase.InvalidateCookedAssets(document.Scene.Guid);
        m_AssetDatabase.NotifyAssetChanged(new AssetChangeEvent(
            AssetChangeKind.Changed,
            document.Scene.Guid,
            "Scene",
            document.SourcePath,
            string.Empty,
            document.Scene.PackageId));
        m_Streaming.SetCellPreviewSource(cellId, null);
        return EditorWorldDocumentResult.Ok(
            $"Reimported cell '{cellId}' and invalidated {invalidated} cooked artifact(s).");
    }

    public bool LoadCellForEditing(WorldCellId cellId)
    {
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!m_CellDocuments.ContainsKey(cellId)) return false;
            m_EditPins.Add(cellId);
        }

        bool loaded = m_Streaming.PinCell(cellId);
        lock (m_Gate) RefreshStateLocked();
        PublishState(Current);
        return loaded;
    }

    public bool UnloadCellForEditing(WorldCellId cellId)
    {
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!m_CellDocuments.ContainsKey(cellId)) return false;
            m_EditPins.Remove(cellId);
        }

        bool unloaded = m_Streaming.UnpinCell(cellId);
        lock (m_Gate) RefreshStateLocked();
        PublishState(Current);
        return unloaded;
    }

    public bool FocusCell(WorldCellId cellId)
    {
        WorldPosition center;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            EditorWorldDocumentState? current = m_Current;
            WorldCellDescriptor? descriptor = current?.Descriptor.Cells.FirstOrDefault(cell => cell.Id == cellId);
            if (descriptor == null) return false;
            center = new WorldPosition(
                (descriptor.Bounds.Min.X + descriptor.Bounds.Max.X) * 0.5,
                (descriptor.Bounds.Min.Y + descriptor.Bounds.Max.Y) * 0.5,
                (descriptor.Bounds.Min.Z + descriptor.Bounds.Max.Z) * 0.5);
            m_SelectedCellId = cellId;
            m_FocusedCellId = cellId;
            RefreshStateLocked();
        }

        FocusRequested?.Invoke(cellId, center);
        PublishState(Current);
        return true;
    }

    public void SelectCell(WorldCellId cellId)
    {
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (m_Current?.Descriptor.Cells.Any(cell => cell.Id == cellId) != true) return;
            m_SelectedCellId = cellId;
            RefreshStateLocked();
        }
        PublishState(Current);
    }

    public void SetStableSelection(EditorWorldSelectionId? selection)
    {
        lock (m_Gate)
        {
            ThrowIfDisposed();
            m_Selection = selection is { IsValid: true } ? selection : null;
            RefreshStateLocked();
        }
        PublishState(Current);
    }

    public void SetExpanded(string stableNodeId, bool expanded)
    {
        if (string.IsNullOrWhiteSpace(stableNodeId)) return;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (expanded) m_ExpandedNodeIds.Add(stableNodeId);
            else m_ExpandedNodeIds.Remove(stableNodeId);
            RefreshStateLocked();
        }
    }

    public void Dispose()
    {
        WorldCellId[] pins;
        lock (m_Gate)
        {
            if (m_Disposed) return;
            m_Disposed = true;
            m_Streaming.ActiveWorldChanged -= OnActiveWorldChanged;
            m_Streaming.CellStateChanged -= OnCellStateChanged;
            m_AssetDatabase.AssetChanged -= OnAssetChanged;
            if (m_SceneDocuments != null)
            {
                m_SceneDocuments.StateChanged -= OnPersistentSceneDocumentChanged;
            }
            pins = m_EditPins.Order().ToArray();
            m_EditPins.Clear();
        }

        foreach (WorldCellId pin in pins) m_Streaming.UnpinCell(pin);
    }

    private EditorWorldDocumentResult ApplyCellWorkingSourceCore(
        WorldCellId cellId,
        string sourceText,
        Guid transactionId,
        bool publishPreview)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return EditorWorldDocumentResult.Fail("Cell scene working source cannot be empty.");
        }

        EditorWorldSceneDocumentState updated;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!m_CellDocuments.TryGetValue(cellId, out EditorWorldSceneDocumentState? current))
            {
                return EditorWorldDocumentResult.Fail($"World cell '{cellId}' is not part of the active document.");
            }
            if (!current.IsEditable)
            {
                return EditorWorldDocumentResult.Fail(
                    $"Cell scene '{current.SourcePath}' is generated or outside an editable Assets root.");
            }

            long revision = ++m_NextRevision;
            var snapshot = new SceneSourceSnapshot(
                current.Scene,
                current.SourcePath,
                sourceText,
                revision);
            SceneInspectionResult inspection = SceneAssetLoader.InspectScene(m_AssetDatabase, snapshot);
            if (!inspection.Success || inspection.Entities.Count == 0)
            {
                string diagnostic = string.IsNullOrWhiteSpace(inspection.Diagnostic)
                    ? $"Cell scene '{current.SourcePath}' is invalid after editing."
                    : inspection.Diagnostic;
                SetDiagnosticLocked(diagnostic);
                return EditorWorldDocumentResult.Fail(diagnostic);
            }

            updated = current with
            {
                Name = ResolveSceneName(inspection, current.SourcePath),
                WorkingSource = sourceText,
                Revision = revision,
                TransactionId = transactionId,
                Inspection = inspection
            };
            m_CellDocuments[cellId] = updated;
            RefreshStateLocked();
            SetDiagnosticLocked(string.Empty);
        }

        if (publishPreview)
        {
            m_Streaming.SetCellPreviewSource(cellId, updated.CreateSnapshot());
        }
        PublishState(Current);
        return EditorWorldDocumentResult.Ok($"Staged cell '{cellId}'.");
    }

    private EditorWorldDocumentResult SaveDocuments(
        IReadOnlyList<WorldCellId> cellIds,
        bool includeWorld)
    {
        EditorWorldDocumentState current;
        EditorWorldSceneDocumentState[] documents;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            current = m_Current
                ?? throw new InvalidOperationException("There is no active editor world document.");
            documents = cellIds
                .Select(id => m_CellDocuments.TryGetValue(id, out EditorWorldSceneDocumentState? document)
                    ? document
                    : throw new InvalidOperationException($"World cell '{id}' is not part of the active document."))
                .Where(document => document.IsDirty)
                .ToArray();
        }

        if (documents.Length == 0 && !includeWorld)
        {
            return EditorWorldDocumentResult.Ok("World authoring documents have no unsaved changes.");
        }

        var writes = new List<SourceWrite>(documents.Length + (includeWorld ? 1 : 0));
        foreach (EditorWorldSceneDocumentState document in documents)
        {
            if (!TryReadUtf8(document.SourcePath, out string disk, out _, out string readError))
            {
                return FailAndPublish(readError);
            }
            if (!string.Equals(disk, document.SavedSource, StringComparison.Ordinal))
            {
                MarkCellConflict(document.CellId);
                return FailAndPublish(
                    $"Cell scene '{document.SourcePath}' changed on disk. Save All was blocked.");
            }
            writes.Add(new SourceWrite(
                document.Scene.Guid,
                "Scene",
                document.Scene.PackageId,
                document.SourcePath,
                document.WorkingSource,
                document.HasUtf8Bom));
        }

        if (includeWorld)
        {
            if (!TryReadUtf8(current.SourcePath, out string disk, out _, out string readError))
            {
                return FailAndPublish(readError);
            }
            if (!string.Equals(disk, current.SavedSource, StringComparison.Ordinal))
            {
                lock (m_Gate)
                {
                    if (m_Current != null)
                    {
                        Volatile.Write(ref m_Current, m_Current with { HasExternalChanges = true });
                    }
                }
                return FailAndPublish(
                    $"World '{current.SourcePath}' changed on disk. Save All was blocked.");
            }
            writes.Add(new SourceWrite(
                current.World.Guid,
                "World",
                current.World.PackageId,
                current.SourcePath,
                current.WorkingSource,
                current.HasUtf8Bom));
        }

        try
        {
            using var transaction = SourceFileTransaction.Stage(writes);
            transaction.Commit();
            WorldDescriptorLoadResult validation = WorldDescriptorLoader.LoadSource(
                m_AssetDatabase,
                current.World);
            if (!validation.Success)
            {
                throw new InvalidDataException(
                    $"Saved world transaction failed validation and was rolled back: {validation.Diagnostic}");
            }
            transaction.Accept();
        }
        catch (Exception ex)
        {
            return FailAndPublish(ex.Message);
        }

        lock (m_Gate)
        {
            foreach (EditorWorldSceneDocumentState document in documents)
            {
                m_CellDocuments[document.CellId] = document with
                {
                    SavedSource = document.WorkingSource,
                    HasExternalChanges = false,
                    TransactionId = Guid.Empty
                };
            }
            if (includeWorld && m_Current != null)
            {
                Volatile.Write(ref m_Current, m_Current with
                {
                    SavedSource = m_Current.WorkingSource,
                    HasExternalChanges = false
                });
            }
            RefreshStateLocked();
            SetDiagnosticLocked(string.Empty);
        }

        foreach (SourceWrite write in writes)
        {
            m_AssetDatabase.InvalidateCookedAssets(write.Guid);
            m_AssetDatabase.NotifyAssetChanged(new AssetChangeEvent(
                AssetChangeKind.Changed,
                write.Guid,
                write.AssetType,
                write.Path,
                string.Empty,
                write.PackageId));
        }
        foreach (EditorWorldSceneDocumentState document in documents)
        {
            m_Streaming.SetCellPreviewSource(document.CellId, null);
        }
        PublishState(Current);
        return EditorWorldDocumentResult.Ok(
            $"Saved {writes.Count} world authoring document(s) transactionally.");
    }

    private void ApplyMoveSources(
        WorldCellId sourceCellId,
        WorldCellId targetCellId,
        string sourceText,
        string targetText,
        Guid transactionId)
    {
        EditorWorldSceneDocumentState source;
        EditorWorldSceneDocumentState target;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            EditorWorldSceneDocumentState oldSource = m_CellDocuments[sourceCellId];
            EditorWorldSceneDocumentState oldTarget = m_CellDocuments[targetCellId];
            long sourceRevision = ++m_NextRevision;
            long targetRevision = ++m_NextRevision;
            SceneInspectionResult sourceInspection = SceneAssetLoader.InspectScene(
                m_AssetDatabase,
                new SceneSourceSnapshot(
                    oldSource.Scene,
                    oldSource.SourcePath,
                    sourceText,
                    sourceRevision));
            SceneInspectionResult targetInspection = SceneAssetLoader.InspectScene(
                m_AssetDatabase,
                new SceneSourceSnapshot(
                    oldTarget.Scene,
                    oldTarget.SourcePath,
                    targetText,
                    targetRevision));
            if (!sourceInspection.Success || sourceInspection.Entities.Count == 0)
            {
                throw new InvalidOperationException(sourceInspection.Diagnostic);
            }
            if (!targetInspection.Success || targetInspection.Entities.Count == 0)
            {
                throw new InvalidOperationException(targetInspection.Diagnostic);
            }

            source = oldSource with
            {
                Name = ResolveSceneName(sourceInspection, oldSource.SourcePath),
                WorkingSource = sourceText,
                Revision = sourceRevision,
                TransactionId = transactionId,
                Inspection = sourceInspection
            };
            target = oldTarget with
            {
                Name = ResolveSceneName(targetInspection, oldTarget.SourcePath),
                WorkingSource = targetText,
                Revision = targetRevision,
                TransactionId = transactionId,
                Inspection = targetInspection
            };
            m_CellDocuments[sourceCellId] = source;
            m_CellDocuments[targetCellId] = target;
            RefreshStateLocked();
        }
        m_Streaming.SetCellPreviewSource(sourceCellId, source.CreateSnapshot());
        m_Streaming.SetCellPreviewSource(targetCellId, target.CreateSnapshot());
        PublishState(Current);
    }

    private void OnActiveWorldChanged(AssetRef<WorldSourceAsset>? world)
    {
        if (world is not { } value || m_Streaming.ActiveWorld is not { } descriptor)
        {
            lock (m_Gate)
            {
                if (m_Disposed) return;
                m_CellDocuments.Clear();
                m_CellStreaming.Clear();
                Volatile.Write(ref m_Current, null);
            }
            PublishState(null);
            return;
        }

        RefreshWorldDocument(value, descriptor, preserveAuthoringState: true);
    }

    private void OnCellStateChanged(WorldCellStreamingSnapshot snapshot)
    {
        lock (m_Gate)
        {
            if (m_Disposed || m_Current == null || !m_CellDocuments.ContainsKey(snapshot.CellId)) return;
            m_CellStreaming[snapshot.CellId] = snapshot;
            RefreshStateLocked();
        }
        PublishState(Current);
    }

    private void OnPersistentSceneDocumentChanged(EditorSceneDocumentState? scene)
    {
        lock (m_Gate)
        {
            if (m_Disposed || m_Current == null || scene == null ||
                scene.Scene.Guid != m_Current.PersistentScene.Scene.Guid)
            {
                return;
            }

            Volatile.Write(ref m_Current, m_Current with
            {
                PersistentScene = new EditorWorldSceneDocumentState(
                    scene.Scene,
                    default,
                    true,
                    scene.Name,
                    scene.SourcePath,
                    scene.SavedSource,
                    scene.WorkingSource,
                    scene.HasUtf8Bom,
                    scene.IsEditable,
                    scene.HasExternalChanges,
                    scene.Revision,
                    Guid.Empty,
                    scene.Inspection)
            });
            RefreshStateLocked();
        }
        PublishState(Current);
    }

    private void OnAssetChanged(AssetChangeEvent change)
    {
        AssetRef<WorldSourceAsset>? refreshWorld = null;
        WorldDescriptor? refreshDescriptor = null;
        WorldCellId clearPreview = default;
        bool publish = false;
        lock (m_Gate)
        {
            if (m_Disposed || m_Current == null) return;
            if (change.Guid == m_Current.World.Guid)
            {
                if (m_Current.IsDirty)
                {
                    Volatile.Write(ref m_Current, m_Current with { HasExternalChanges = true });
                    SetDiagnosticLocked("The active world source changed externally while authoring changes are dirty.");
                    publish = true;
                }
                else
                {
                    refreshWorld = m_Current.World;
                    refreshDescriptor = m_Streaming.ActiveWorld;
                }
            }
            else
            {
                EditorWorldSceneDocumentState? document = m_CellDocuments.Values
                    .FirstOrDefault(item => item.Scene.Guid == change.Guid);
                if (document == null) return;
                if (document.IsDirty)
                {
                    m_CellDocuments[document.CellId] = document with { HasExternalChanges = true };
                    SetDiagnosticLocked(
                        $"Cell scene '{document.SourcePath}' changed externally while authoring changes are dirty.");
                    RefreshStateLocked();
                    publish = true;
                }
                else if (TryCreateSceneDocument(
                             document.Scene,
                             document.CellId,
                             isPersistent: false,
                             out EditorWorldSceneDocumentState reloaded,
                             out _))
                {
                    m_CellDocuments[document.CellId] = reloaded;
                    RefreshStateLocked();
                    clearPreview = document.CellId;
                    publish = true;
                }
            }
        }
        if (refreshWorld is { } world && refreshDescriptor != null)
        {
            RefreshWorldDocument(world, refreshDescriptor, preserveAuthoringState: true);
        }
        else
        {
            if (clearPreview.IsValid) m_Streaming.SetCellPreviewSource(clearPreview, null);
            if (publish) PublishState(Current);
        }
    }

    private void RefreshWorldDocument(
        AssetRef<WorldSourceAsset> world,
        WorldDescriptor descriptor,
        bool preserveAuthoringState)
    {
        string diagnostic = string.Empty;
        if (!m_AssetDatabase.TryGetAsset(world, out AssetRecord? worldAsset) ||
            !string.Equals(worldAsset.AssetType, "World", StringComparison.OrdinalIgnoreCase) ||
            !TryReadUtf8(worldAsset.SourcePath, out string worldSource, out bool worldBom, out diagnostic))
        {
            SetFailure(string.IsNullOrWhiteSpace(diagnostic)
                ? $"World asset '{world.Guid:D}' is not available as editable source."
                : diagnostic);
            return;
        }

        var nextDocuments = new Dictionary<WorldCellId, EditorWorldSceneDocumentState>();
        foreach (WorldCellDescriptor cell in descriptor.Cells.OrderBy(cell => cell.Id))
        {
            if (preserveAuthoringState &&
                m_CellDocuments.TryGetValue(cell.Id, out EditorWorldSceneDocumentState? existing) &&
                existing.Scene.Guid == cell.Scene.Guid &&
                existing.IsDirty)
            {
                nextDocuments.Add(cell.Id, existing);
                continue;
            }

            var sceneRef = new AssetRef<SceneSourceAsset>(cell.Scene.Guid, "Scene", cell.Scene.PackageId);
            if (!TryCreateSceneDocument(
                    sceneRef,
                    cell.Id,
                    isPersistent: false,
                    out EditorWorldSceneDocumentState sceneDocument,
                    out diagnostic))
            {
                SetFailure(diagnostic);
                return;
            }
            nextDocuments.Add(cell.Id, sceneDocument);
        }

        var persistentRef = new AssetRef<SceneSourceAsset>(
            descriptor.PersistentScene.Guid,
            "Scene",
            descriptor.PersistentScene.PackageId);
        if (!TryCreateSceneDocument(
                persistentRef,
                default,
                isPersistent: true,
                out EditorWorldSceneDocumentState persistent,
                out diagnostic))
        {
            SetFailure(diagnostic);
            return;
        }

        lock (m_Gate)
        {
            if (m_Disposed) return;
            m_CellDocuments.Clear();
            foreach ((WorldCellId id, EditorWorldSceneDocumentState document) in nextDocuments)
            {
                m_CellDocuments.Add(id, document);
            }
            m_CellStreaming.Clear();
            foreach (WorldCellStreamingSnapshot snapshot in m_Streaming.GetCells())
            {
                m_CellStreaming[snapshot.CellId] = snapshot;
            }
            m_EditPins.RemoveWhere(id => !m_CellDocuments.ContainsKey(id));
            if (m_SelectedCellId.IsValid && !m_CellDocuments.ContainsKey(m_SelectedCellId))
            {
                m_SelectedCellId = default;
            }
            if (m_FocusedCellId.IsValid && !m_CellDocuments.ContainsKey(m_FocusedCellId))
            {
                m_FocusedCellId = default;
            }
            if (m_Selection is { } selection &&
                !SelectionExists(selection, persistent, m_CellDocuments))
            {
                m_Selection = null;
            }

            m_Current = new EditorWorldDocumentState(
                world,
                descriptor.Name,
                worldAsset.SourcePath,
                worldSource,
                worldSource,
                worldBom,
                AssetPathPolicy.IsEditableAssetPath(worldAsset.SourcePath),
                false,
                ++m_NextRevision,
                descriptor,
                persistent,
                Array.Empty<EditorWorldCellDocumentState>(),
                m_SelectedCellId,
                m_FocusedCellId,
                m_Selection,
                new HashSet<string>(m_ExpandedNodeIds, StringComparer.Ordinal),
                m_Streaming.GetMetrics(),
                m_Streaming.GetDiagnostics());
            RefreshStateLocked();
            SetDiagnosticLocked(string.Empty);
        }

        PublishState(Current);
    }

    private void RefreshStateLocked()
    {
        if (m_Current == null) return;
        WorldCellStreamingSnapshot[] live = m_Streaming.GetCells().ToArray();
        foreach (WorldCellStreamingSnapshot snapshot in live)
        {
            m_CellStreaming[snapshot.CellId] = snapshot;
        }

        EditorWorldCellDocumentState[] cells = m_Current.Descriptor.Cells
            .OrderBy(cell => cell.Id)
            .Select(cell => new EditorWorldCellDocumentState(
                cell,
                m_CellDocuments[cell.Id],
                m_CellStreaming.TryGetValue(cell.Id, out WorldCellStreamingSnapshot? snapshot)
                    ? snapshot
                    : CreateUnloadedSnapshot(cell.Id),
                m_EditPins.Contains(cell.Id)))
            .ToArray();
        Volatile.Write(ref m_Current, m_Current with
        {
            Cells = cells,
            SelectedCellId = m_SelectedCellId,
            FocusedCellId = m_FocusedCellId,
            Selection = m_Selection,
            ExpandedNodeIds = new HashSet<string>(m_ExpandedNodeIds, StringComparer.Ordinal),
            Metrics = m_Streaming.GetMetrics(),
            Diagnostics = m_Streaming.GetDiagnostics()
        });
    }

    private bool TryCreateSceneDocument(
        AssetRef<SceneSourceAsset> scene,
        WorldCellId cellId,
        bool isPersistent,
        out EditorWorldSceneDocumentState document,
        out string diagnostic)
    {
        if (!m_AssetDatabase.TryGetAsset(scene, out AssetRecord? asset) ||
            !string.Equals(asset.AssetType, "Scene", StringComparison.OrdinalIgnoreCase))
        {
            document = null!;
            diagnostic = $"Scene asset '{scene.Guid:D}' is not indexed as Scene.";
            return false;
        }
        if (!TryReadUtf8(asset.SourcePath, out string source, out bool bom, out diagnostic))
        {
            document = null!;
            return false;
        }

        var snapshot = new SceneSourceSnapshot(scene, asset.SourcePath, source, 0);
        SceneInspectionResult inspection = SceneAssetLoader.InspectScene(m_AssetDatabase, snapshot);
        if (!inspection.Success || inspection.Entities.Count == 0)
        {
            document = null!;
            diagnostic = string.IsNullOrWhiteSpace(inspection.Diagnostic)
                ? $"Scene '{asset.SourcePath}' has no inspectable entities."
                : inspection.Diagnostic;
            return false;
        }

        document = new EditorWorldSceneDocumentState(
            scene,
            cellId,
            isPersistent,
            ResolveSceneName(inspection, asset.SourcePath),
            asset.SourcePath,
            source,
            source,
            bom,
            AssetPathPolicy.IsEditableAssetPath(asset.SourcePath),
            false,
            ++m_NextRevision,
            Guid.Empty,
            inspection);
        diagnostic = string.Empty;
        return true;
    }

    private void MarkCellConflict(WorldCellId cellId)
    {
        lock (m_Gate)
        {
            if (m_CellDocuments.TryGetValue(cellId, out EditorWorldSceneDocumentState? document))
            {
                m_CellDocuments[cellId] = document with { HasExternalChanges = true };
                RefreshStateLocked();
            }
        }
        PublishState(Current);
    }

    private EditorWorldDocumentResult FailAndPublish(string diagnostic)
    {
        SetFailure(diagnostic);
        return EditorWorldDocumentResult.Fail(diagnostic);
    }

    private void SetFailure(string diagnostic)
    {
        lock (m_Gate) SetDiagnosticLocked(diagnostic);
        OperationFailed?.Invoke(diagnostic);
    }

    private void SetDiagnosticLocked(string diagnostic)
    {
        Volatile.Write(ref m_LastDiagnostic, diagnostic ?? string.Empty);
    }

    private void PublishState(EditorWorldDocumentState? state)
    {
        StateChanged?.Invoke(state);
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed) throw new ObjectDisposedException(nameof(EditorWorldDocumentService));
    }

    private static WorldCellStreamingSnapshot CreateUnloadedSnapshot(WorldCellId cellId) =>
        new(
            cellId,
            WorldCellStreamingState.Unloaded,
            0,
            0,
            false,
            false,
            false,
            RuntimeSceneInstanceId.Invalid,
            0,
            0,
            0,
            string.Empty);

    private static bool SelectionExists(
        EditorWorldSelectionId selection,
        EditorWorldSceneDocumentState persistent,
        IReadOnlyDictionary<WorldCellId, EditorWorldSceneDocumentState> cells)
    {
        EditorWorldSceneDocumentState? document = selection.CellId.IsValid
            ? cells.GetValueOrDefault(selection.CellId)
            : persistent;
        return document != null &&
               document.Scene.Guid == selection.SceneGuid &&
               document.Inspection.Entities.Any(entity => entity.AuthoringGuid == selection.EntityGuid);
    }

    private static string ResolveSceneName(SceneInspectionResult inspection, string path) =>
        string.IsNullOrWhiteSpace(inspection.SceneName)
            ? Path.GetFileNameWithoutExtension(path)
            : inspection.SceneName;

    private static bool TryReadUtf8(
        string path,
        out string source,
        out bool hasBom,
        out string diagnostic)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            ReadOnlySpan<byte> payload = hasBom
                ? bytes.AsSpan(Encoding.UTF8.Preamble.Length)
                : bytes.AsSpan();
            source = new UTF8Encoding(false, true).GetString(payload);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            source = string.Empty;
            hasBom = false;
            diagnostic = $"Failed to read authoring source '{path}': {ex.Message}";
            return false;
        }
    }

    private sealed class MoveEntityBetweenCellsCommand : ICommand
    {
        private readonly EditorWorldDocumentService m_Service;
        private readonly WorldCellId m_SourceCellId;
        private readonly WorldCellId m_TargetCellId;
        private readonly Guid m_EntityGuid;
        private readonly Guid m_TransactionId = Guid.NewGuid();
        private string? m_OldSource;
        private string? m_OldTarget;
        private string? m_NewSource;
        private string? m_NewTarget;

        public MoveEntityBetweenCellsCommand(
            EditorWorldDocumentService service,
            WorldCellId sourceCellId,
            WorldCellId targetCellId,
            Guid entityGuid)
        {
            m_Service = service;
            m_SourceCellId = sourceCellId;
            m_TargetCellId = targetCellId;
            m_EntityGuid = entityGuid;
        }

        public string Description => $"Move world entity '{m_EntityGuid:D}' between cells";

        public void Execute()
        {
            if (m_NewSource == null || m_NewTarget == null)
            {
                EditorWorldDocumentState current = m_Service.Current
                    ?? throw new InvalidOperationException("There is no active editor world document.");
                EditorWorldCellDocumentState source = current.Cells.Single(cell => cell.CellId == m_SourceCellId);
                EditorWorldCellDocumentState target = current.Cells.Single(cell => cell.CellId == m_TargetCellId);
                m_OldSource = source.SceneDocument.WorkingSource;
                m_OldTarget = target.SceneDocument.WorkingSource;
                SceneMoveResult move = SceneSourceMover.MoveSubtree(
                    current.Descriptor,
                    source,
                    target,
                    m_EntityGuid);
                if (!move.Success) throw new InvalidOperationException(move.Diagnostic);
                m_NewSource = move.SourceText;
                m_NewTarget = move.TargetText;
            }

            m_Service.ApplyMoveSources(
                m_SourceCellId,
                m_TargetCellId,
                m_NewSource,
                m_NewTarget,
                m_TransactionId);
        }

        public void Undo()
        {
            if (m_OldSource == null || m_OldTarget == null)
            {
                throw new InvalidOperationException("The world entity move has not been executed.");
            }
            m_Service.ApplyMoveSources(
                m_SourceCellId,
                m_TargetCellId,
                m_OldSource,
                m_OldTarget,
                m_TransactionId);
        }
    }

    private readonly record struct SourceWrite(
        Guid Guid,
        string AssetType,
        string PackageId,
        string Path,
        string Source,
        bool IncludeBom);

    private sealed class SourceFileTransaction : IDisposable
    {
        private readonly List<Entry> m_Entries;
        private bool m_Accepted;
        private int m_CommitCount;

        private SourceFileTransaction(List<Entry> entries)
        {
            m_Entries = entries;
        }

        public static SourceFileTransaction Stage(IEnumerable<SourceWrite> writes)
        {
            var entries = new List<Entry>();
            try
            {
                foreach (SourceWrite write in writes)
                {
                    string fullPath = Path.GetFullPath(write.Path);
                    string directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException("Authoring source has no parent directory.");
                    string token = Guid.NewGuid().ToString("N");
                    string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{token}.tmp");
                    string backup = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{token}.bak");
                    byte[] content = EncodeUtf8(write.Source, write.IncludeBom);
                    File.WriteAllBytes(temporary, content);
                    entries.Add(new Entry(fullPath, temporary, backup));
                }
                return new SourceFileTransaction(entries);
            }
            catch
            {
                foreach (Entry entry in entries) DeleteIfPresent(entry.TemporaryPath);
                throw;
            }
        }

        public void Commit()
        {
            foreach (Entry entry in m_Entries)
            {
                File.Move(entry.Path, entry.BackupPath);
                try
                {
                    File.Move(entry.TemporaryPath, entry.Path);
                    m_CommitCount++;
                }
                catch
                {
                    File.Move(entry.BackupPath, entry.Path);
                    throw;
                }
            }
        }

        public void Accept()
        {
            m_Accepted = true;
            foreach (Entry entry in m_Entries) DeleteIfPresent(entry.BackupPath);
        }

        public void Dispose()
        {
            if (!m_Accepted && m_CommitCount > 0)
            {
                for (int index = m_CommitCount - 1; index >= 0; index--)
                {
                    Entry entry = m_Entries[index];
                    DeleteIfPresent(entry.Path);
                    if (File.Exists(entry.BackupPath)) File.Move(entry.BackupPath, entry.Path);
                }
            }
            foreach (Entry entry in m_Entries)
            {
                DeleteIfPresent(entry.TemporaryPath);
                if (m_Accepted) DeleteIfPresent(entry.BackupPath);
            }
        }

        private static byte[] EncodeUtf8(string source, bool includeBom)
        {
            byte[] payload = Encoding.UTF8.GetBytes(source);
            if (!includeBom) return payload;
            byte[] preamble = Encoding.UTF8.GetPreamble();
            byte[] output = new byte[preamble.Length + payload.Length];
            preamble.CopyTo(output, 0);
            payload.CopyTo(output, preamble.Length);
            return output;
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private sealed record Entry(string Path, string TemporaryPath, string BackupPath);
    }
}

internal readonly record struct SceneMoveResult(
    bool Success,
    string Diagnostic,
    string SourceText,
    string TargetText);

internal static class SceneSourceMover
{
    public static SceneMoveResult MoveSubtree(
        WorldDescriptor world,
        EditorWorldCellDocumentState source,
        EditorWorldCellDocumentState target,
        Guid rootEntityGuid)
    {
        try
        {
            YamlStream sourceStream = Parse(source.SceneDocument.SourcePath, source.SceneDocument.WorkingSource);
            YamlStream targetStream = Parse(target.SceneDocument.SourcePath, target.SceneDocument.WorkingSource);
            YamlMappingNode sourceRoot = (YamlMappingNode)sourceStream.Documents[0].RootNode;
            YamlMappingNode targetRoot = (YamlMappingNode)targetStream.Documents[0].RootNode;
            YamlSequenceNode sourceEntities = GetSequence(sourceRoot, "Entities");
            YamlSequenceNode targetEntities = GetSequence(targetRoot, "Entities");

            Dictionary<Guid, YamlMappingNode> sourceByGuid = IndexEntities(sourceEntities, source.SceneDocument.SourcePath);
            Dictionary<Guid, YamlMappingNode> targetByGuid = IndexEntities(targetEntities, target.SceneDocument.SourcePath);
            if (!sourceByGuid.ContainsKey(rootEntityGuid))
            {
                return Fail($"Source cell does not contain entity '{rootEntityGuid:D}'.");
            }

            var moved = new HashSet<Guid> { rootEntityGuid };
            bool changed;
            do
            {
                changed = false;
                foreach ((Guid guid, YamlMappingNode entity) in sourceByGuid)
                {
                    if (!moved.Contains(guid) && TryReadParentGuid(entity, out Guid parent) && moved.Contains(parent))
                    {
                        moved.Add(guid);
                        changed = true;
                    }
                }
            } while (changed);

            if (TryReadParentGuid(sourceByGuid[rootEntityGuid], out Guid rootParent) &&
                rootParent != Guid.Empty && !moved.Contains(rootParent))
            {
                return Fail(
                    $"Entity '{rootEntityGuid:D}' has parent '{rootParent:D}' outside its subtree. " +
                    "Detach it from the parent before moving it to another cell.");
            }
            if (moved.Any(targetByGuid.ContainsKey))
            {
                return Fail("The target scene already contains an entity GUID from the moved subtree.");
            }
            if (sourceByGuid.Count == moved.Count)
            {
                return Fail("Moving this subtree would leave the source scene empty.");
            }
            if (world.EntityReferences.Any(reference =>
                    moved.Contains(reference.SourceEntityGuid) ||
                    moved.Contains(reference.TargetEntityGuid)))
            {
                return Fail(
                    "The moved subtree participates in a world-level cross-cell reference. " +
                    "Detach or retarget that reference before moving the entity.");
            }

            MergeRequiredSchemas(sourceRoot, targetRoot, moved.Select(guid => sourceByGuid[guid]));
            YamlMappingNode[] movedNodes = sourceEntities.Children
                .OfType<YamlMappingNode>()
                .Where(node => TryReadGuid(node, "Guid", out Guid guid) && moved.Contains(guid))
                .ToArray();
            foreach (YamlMappingNode node in movedNodes)
            {
                sourceEntities.Children.Remove(node);
                targetEntities.Add(node);
            }

            string sourceText = Serialize(sourceStream, source.SceneDocument.WorkingSource);
            string targetText = Serialize(targetStream, target.SceneDocument.WorkingSource);
            return new SceneMoveResult(true, string.Empty, sourceText, targetText);
        }
        catch (Exception ex)
        {
            return Fail($"World-cell entity move failed: {ex.Message}");
        }
    }

    private static void MergeRequiredSchemas(
        YamlMappingNode sourceRoot,
        YamlMappingNode targetRoot,
        IEnumerable<YamlMappingNode> movedEntities)
    {
        YamlSequenceNode sourceSchemas = GetSequence(sourceRoot, "ComponentSchemas");
        YamlSequenceNode targetSchemas = GetSequence(targetRoot, "ComponentSchemas");
        var requiredNames = movedEntities
            .SelectMany(entity => entity.Children.Keys.OfType<YamlScalarNode>())
            .Select(key => key.Value ?? string.Empty)
            .Where(name => name is not ("Guid" or "Name" or "Parent"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetNames = targetSchemas.Children
            .OfType<YamlMappingNode>()
            .Select(schema => ReadScalar(schema, "Name"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (YamlMappingNode sourceSchema in sourceSchemas.Children.OfType<YamlMappingNode>())
        {
            string name = ReadScalar(sourceSchema, "Name");
            if (!requiredNames.Contains(name) || !targetNames.Add(name)) continue;
            targetSchemas.Add(new YamlMappingNode
            {
                { "TypeId", ReadScalar(sourceSchema, "TypeId") },
                { "Name", name },
                { "Version", ReadScalar(sourceSchema, "Version") },
                { "Required", ReadScalar(sourceSchema, "Required") }
            });
        }
    }

    private static YamlStream Parse(string path, string source)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(source);
        stream.Load(reader);
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode)
        {
            throw new InvalidDataException($"Scene '{path}' must contain one YAML mapping document.");
        }
        return stream;
    }

    private static Dictionary<Guid, YamlMappingNode> IndexEntities(YamlSequenceNode entities, string path)
    {
        var result = new Dictionary<Guid, YamlMappingNode>();
        foreach (YamlMappingNode entity in entities.Children.OfType<YamlMappingNode>())
        {
            if (!TryReadGuid(entity, "Guid", out Guid guid) || guid == Guid.Empty || !result.TryAdd(guid, entity))
            {
                throw new InvalidDataException($"Scene '{path}' contains an empty or duplicate entity GUID.");
            }
        }
        return result;
    }

    private static bool TryReadParentGuid(YamlMappingNode entity, out Guid guid)
    {
        guid = Guid.Empty;
        if (!TryGet(entity, "Parent", out YamlNode? parentNode) || parentNode is not YamlMappingNode parent)
        {
            return false;
        }
        return TryReadGuid(parent, "EntityGuid", out guid);
    }

    private static bool TryReadGuid(YamlMappingNode mapping, string key, out Guid guid)
    {
        guid = Guid.Empty;
        return TryGet(mapping, key, out YamlNode? node) &&
               node is YamlScalarNode scalar &&
               Guid.TryParse(scalar.Value, out guid);
    }

    private static string ReadScalar(YamlMappingNode mapping, string key)
    {
        if (TryGet(mapping, key, out YamlNode? node) && node is YamlScalarNode scalar)
        {
            return scalar.Value ?? string.Empty;
        }
        throw new InvalidDataException($"YAML mapping is missing scalar '{key}'.");
    }

    private static YamlSequenceNode GetSequence(YamlMappingNode mapping, string key)
    {
        if (TryGet(mapping, key, out YamlNode? node) && node is YamlSequenceNode sequence)
        {
            return sequence;
        }
        throw new InvalidDataException($"YAML mapping is missing sequence '{key}'.");
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        foreach ((YamlNode candidateKey, YamlNode candidateValue) in mapping.Children)
        {
            if (candidateKey is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                value = candidateValue;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static string Serialize(YamlStream stream, string original)
    {
        var output = new StringBuilder(original.Length + 256);
        using var writer = new StringWriter(output, CultureInfo.InvariantCulture)
        {
            NewLine = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n"
        };
        stream.Save(writer, assignAnchors: false);
        return output.ToString();
    }

    private static SceneMoveResult Fail(string diagnostic) =>
        new(false, diagnostic, string.Empty, string.Empty);
}
