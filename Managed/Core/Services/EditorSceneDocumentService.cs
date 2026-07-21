using System;
using System.IO;
using System.Text;
using System.Threading;
using ArisenEditor.Core.Assets;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Automation;
using ArisenEngine.Core.ECS;
using ArisenEngine.Resources.Serialization;

namespace ArisenEditor.Core.Services;

internal sealed record EditorSceneDocumentState(
    AssetRef<SceneSourceAsset> Scene,
    string Name,
    string SourcePath,
    string SavedSource,
    string WorkingSource,
    bool HasUtf8Bom,
    bool IsEditable,
    bool HasExternalChanges,
    long Revision,
    SceneInspectionResult Inspection)
{
    public bool IsDirty => !string.Equals(SavedSource, WorkingSource, StringComparison.Ordinal);

    public SceneSourceSnapshot CreateSnapshot()
    {
        return new SceneSourceSnapshot(Scene, SourcePath, WorkingSource, Revision);
    }
}

internal readonly record struct EditorSceneDocumentResult(
    bool Success,
    bool RequiresUserResolution,
    string Diagnostic)
{
    public static EditorSceneDocumentResult Ok(string diagnostic)
    {
        return new EditorSceneDocumentResult(true, false, diagnostic);
    }

    public static EditorSceneDocumentResult Fail(string diagnostic)
    {
        return new EditorSceneDocumentResult(false, false, diagnostic);
    }

    public static EditorSceneDocumentResult RequiresResolution(string diagnostic)
    {
        return new EditorSceneDocumentResult(false, true, diagnostic);
    }
}

internal interface IEditorSceneDocumentService : IDisposable
{
    EditorSceneDocumentState? Current { get; }

    string LastDiagnostic { get; }

    event Action<EditorSceneDocumentState?>? StateChanged;

    event Action<string>? OperationFailed;

    bool IsActiveScene(AssetRef<SceneSourceAsset> scene);

    EditorSceneDocumentResult RequestOpenScene(AssetRef<SceneSourceAsset> scene);

    EditorSceneDocumentResult ApplyEntityTransform(
        Guid entityGuid,
        SceneTransformInspection transform);

    EditorSceneDocumentResult ApplyWorkingSource(string sourceText);

    EditorSceneDocumentResult Save();

    EditorSceneDocumentResult DiscardChanges();
}

internal sealed class EditorSceneDocumentService : IEditorSceneDocumentService
{
    private readonly object m_Gate = new();
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly IRuntimeSceneService m_RuntimeSceneService;
    private readonly ICommandManager m_CommandManager;
    private EditorSceneDocumentState? m_Current;
    private EditorSceneDocumentState? m_Pending;
    private string m_LastDiagnostic = string.Empty;
    private long m_NextRevision;
    private bool m_Disposed;

    public EditorSceneDocumentState? Current => Volatile.Read(ref m_Current);

    public string LastDiagnostic => Volatile.Read(ref m_LastDiagnostic);

    public event Action<EditorSceneDocumentState?>? StateChanged;

    public event Action<string>? OperationFailed;

    public EditorSceneDocumentService(
        IAssetDatabase assetDatabase,
        IRuntimeSceneService runtimeSceneService,
        ICommandManager commandManager)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_RuntimeSceneService = runtimeSceneService ?? throw new ArgumentNullException(nameof(runtimeSceneService));
        m_CommandManager = commandManager ?? throw new ArgumentNullException(nameof(commandManager));

        m_RuntimeSceneService.ActiveSceneChanged += OnRuntimeSceneChanged;
        m_RuntimeSceneService.SceneLoadCompleted += OnRuntimeSceneLoadCompleted;
        m_AssetDatabase.AssetChanged += OnAssetChanged;

        if (m_RuntimeSceneService.ActiveScene is not { } activeScene)
        {
            return;
        }

        if (TryCreateDocumentFromDisk(activeScene.Scene, out var document, out var diagnostic))
        {
            Volatile.Write(ref m_Current, document);
            Volatile.Write(ref m_LastDiagnostic, string.Empty);
        }
        else
        {
            Volatile.Write(ref m_LastDiagnostic, diagnostic);
        }
    }

    public bool IsActiveScene(AssetRef<SceneSourceAsset> scene)
    {
        var current = Current;
        return current != null && IsSameScene(current.Scene, scene);
    }

    public EditorSceneDocumentResult RequestOpenScene(AssetRef<SceneSourceAsset> scene)
    {
        if (!scene.IsValid)
        {
            return EditorSceneDocumentResult.Fail("Scene activation requires a valid scene asset reference.");
        }

        EditorSceneDocumentState pending;
        lock (m_Gate)
        {
            ThrowIfDisposed();

            var current = m_Current;
            if (current != null && IsSameScene(current.Scene, scene))
            {
                return EditorSceneDocumentResult.Ok($"Scene '{current.Name}' is already active.");
            }

            if (current is { IsDirty: true })
            {
                return EditorSceneDocumentResult.RequiresResolution(
                    $"Scene '{current.Name}' has unsaved changes.");
            }

            if (!TryCreateDocumentFromDisk(scene, out pending, out var diagnostic))
            {
                SetDiagnosticLocked(diagnostic);
                return EditorSceneDocumentResult.Fail(diagnostic);
            }

            m_Pending = pending;
            SetDiagnosticLocked(string.Empty);
        }

        try
        {
            m_RuntimeSceneService.RequestSceneLoad(scene);
            return EditorSceneDocumentResult.Ok(
                $"Queued scene '{pending.Name}' for frame-boundary activation.");
        }
        catch (Exception ex)
        {
            lock (m_Gate)
            {
                if (ReferenceEquals(m_Pending, pending))
                {
                    m_Pending = null;
                }
                SetDiagnosticLocked(ex.Message);
            }

            PublishFailure(ex.Message);
            return EditorSceneDocumentResult.Fail(ex.Message);
        }
    }

    public EditorSceneDocumentResult ApplyEntityTransform(
        Guid entityGuid,
        SceneTransformInspection transform)
    {
        var current = Current;
        if (current == null)
        {
            return EditorSceneDocumentResult.Fail(
                "There is no active editor scene document to edit.");
        }

        if (!current.IsEditable)
        {
            return EditorSceneDocumentResult.Fail(
                $"Scene '{current.SourcePath}' is generated or outside an editable Assets root.");
        }

        var edit = SceneAssetLoader.UpdateEntityTransformSource(
            current.SourcePath,
            current.WorkingSource,
            entityGuid,
            transform);
        if (!edit.Success)
        {
            lock (m_Gate)
            {
                SetDiagnosticLocked(edit.Diagnostic);
            }

            return EditorSceneDocumentResult.Fail(edit.Diagnostic);
        }

        return ApplyWorkingSourceCore(
            current,
            edit.UpdatedSource,
            $"Updated entity '{entityGuid:D}' in scene '{current.Name}'.");
    }

    public EditorSceneDocumentResult ApplyWorkingSource(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return EditorSceneDocumentResult.Fail("Editor scene working source cannot be empty.");
        }

        var current = Current;
        if (current == null)
        {
            return EditorSceneDocumentResult.Fail(
                "There is no active editor scene document to edit.");
        }

        if (!current.IsEditable)
        {
            return EditorSceneDocumentResult.Fail(
                $"Scene '{current.SourcePath}' is generated or outside an editable Assets root.");
        }

        return ApplyWorkingSourceCore(
            current,
            sourceText,
            $"Restored staged source for scene '{current.Name}'.");
    }

    private EditorSceneDocumentResult ApplyWorkingSourceCore(
        EditorSceneDocumentState expected,
        string sourceText,
        string successDiagnostic)
    {
        EditorSceneDocumentState updated;
        lock (m_Gate)
        {
            ThrowIfDisposed();
            if (!ReferenceEquals(m_Current, expected))
            {
                const string changed =
                    "The active scene document changed before the edit could be staged.";
                SetDiagnosticLocked(changed);
                return EditorSceneDocumentResult.Fail(changed);
            }

            if (m_Pending != null)
            {
                const string pending =
                    "A scene activation is pending; wait for it to finish before editing the current document.";
                SetDiagnosticLocked(pending);
                return EditorSceneDocumentResult.Fail(pending);
            }

            if (string.Equals(expected.WorkingSource, sourceText, StringComparison.Ordinal))
            {
                return EditorSceneDocumentResult.Ok(successDiagnostic);
            }

            long revision = Interlocked.Increment(ref m_NextRevision);
            var snapshot = new SceneSourceSnapshot(
                expected.Scene,
                expected.SourcePath,
                sourceText,
                revision);
            var inspection = SceneAssetLoader.InspectScene(m_AssetDatabase, snapshot);
            if (!inspection.Success || inspection.Entities.Count == 0)
            {
                string diagnostic = string.IsNullOrWhiteSpace(inspection.Diagnostic)
                    ? $"Scene '{expected.SourcePath}' is invalid after editing."
                    : inspection.Diagnostic;
                SetDiagnosticLocked(diagnostic);
                return EditorSceneDocumentResult.Fail(diagnostic);
            }

            updated = expected with
            {
                Name = ResolveSceneName(inspection, expected.SourcePath),
                WorkingSource = sourceText,
                Revision = revision,
                Inspection = inspection
            };
            Volatile.Write(ref m_Current, updated);
            SetDiagnosticLocked(string.Empty);
        }

        try
        {
            m_RuntimeSceneService.RequestSceneLoad(updated.CreateSnapshot());
        }
        catch (Exception ex)
        {
            lock (m_Gate)
            {
                if (ReferenceEquals(m_Current, updated))
                {
                    Volatile.Write(ref m_Current, expected);
                }
                SetDiagnosticLocked(ex.Message);
            }

            PublishState(expected);
            PublishFailure(ex.Message);
            return EditorSceneDocumentResult.Fail(ex.Message);
        }

        PublishState(updated);
        return EditorSceneDocumentResult.Ok(successDiagnostic);
    }

    public EditorSceneDocumentResult Save()
    {
        EditorSceneDocumentState? updated = null;
        string? conflict = null;
        lock (m_Gate)
        {
            ThrowIfDisposed();

            var current = m_Current;
            if (current == null)
            {
                return EditorSceneDocumentResult.Fail("There is no active editor scene document to save.");
            }

            if (m_Pending != null)
            {
                return EditorSceneDocumentResult.Fail(
                    "A scene activation is pending; wait for it to finish before saving.");
            }

            if (!current.IsDirty)
            {
                return EditorSceneDocumentResult.Ok($"Scene '{current.Name}' has no unsaved changes.");
            }

            if (!current.IsEditable)
            {
                return EditorSceneDocumentResult.Fail(
                    $"Scene '{current.SourcePath}' is generated or outside an editable Assets root.");
            }

            if (!TryReadUtf8(current.SourcePath, out var diskSource, out _, out var readError))
            {
                SetDiagnosticLocked(readError);
                return EditorSceneDocumentResult.Fail(readError);
            }

            if (!string.Equals(diskSource, current.SavedSource, StringComparison.Ordinal))
            {
                updated = current with { HasExternalChanges = true };
                Volatile.Write(ref m_Current, updated);
                conflict =
                    "The scene changed on disk after it was opened. Save was blocked to avoid overwriting external changes.";
                SetDiagnosticLocked(conflict);
            }
            else
            {
                var validation = SceneAssetLoader.LoadScene(
                    m_AssetDatabase,
                    current.CreateSnapshot(),
                    new EntityManager());
                if (!validation.Success)
                {
                    SetDiagnosticLocked(validation.Diagnostic);
                    return EditorSceneDocumentResult.Fail(validation.Diagnostic);
                }

                try
                {
                    WriteUtf8Atomically(current.SourcePath, current.WorkingSource, current.HasUtf8Bom);
                }
                catch (Exception ex)
                {
                    SetDiagnosticLocked(ex.Message);
                    return EditorSceneDocumentResult.Fail(ex.Message);
                }

                updated = current with
                {
                    SavedSource = current.WorkingSource,
                    HasExternalChanges = false
                };
                Volatile.Write(ref m_Current, updated);
                SetDiagnosticLocked(string.Empty);
            }
        }

        if (conflict != null)
        {
            PublishState(updated);
            PublishFailure(conflict);
            return EditorSceneDocumentResult.Fail(conflict);
        }

        PublishState(updated);
        try
        {
            m_RuntimeSceneService.RequestSceneLoad(updated!.CreateSnapshot());
        }
        catch (Exception ex)
        {
            PublishFailure(
                $"Scene was saved, but its runtime preview could not be queued: {ex.Message}");
        }

        return EditorSceneDocumentResult.Ok($"Saved scene '{updated.Name}'.");
    }

    public EditorSceneDocumentResult DiscardChanges()
    {
        EditorSceneDocumentState updated;
        bool loadFromDisk;

        lock (m_Gate)
        {
            ThrowIfDisposed();

            var current = m_Current;
            if (current == null)
            {
                return EditorSceneDocumentResult.Fail("There is no active editor scene document.");
            }

            if (m_Pending != null)
            {
                return EditorSceneDocumentResult.Fail(
                    "A scene activation is pending; wait for it to finish before discarding changes.");
            }

            loadFromDisk = current.HasExternalChanges;
            if (loadFromDisk)
            {
                if (!TryCreateDocumentFromDisk(current.Scene, out updated, out var diagnostic))
                {
                    SetDiagnosticLocked(diagnostic);
                    return EditorSceneDocumentResult.Fail(diagnostic);
                }
            }
            else
            {
                long revision = Interlocked.Increment(ref m_NextRevision);
                var snapshot = new SceneSourceSnapshot(
                    current.Scene,
                    current.SourcePath,
                    current.SavedSource,
                    revision);
                var inspection = SceneAssetLoader.InspectScene(m_AssetDatabase, snapshot);
                if (inspection.Entities.Count == 0)
                {
                    string diagnostic = string.IsNullOrWhiteSpace(inspection.Diagnostic)
                        ? $"Scene '{current.SourcePath}' could not be restored."
                        : inspection.Diagnostic;
                    SetDiagnosticLocked(diagnostic);
                    return EditorSceneDocumentResult.Fail(diagnostic);
                }

                updated = current with
                {
                    WorkingSource = current.SavedSource,
                    HasExternalChanges = false,
                    Revision = revision,
                    Inspection = inspection
                };
            }

            Volatile.Write(ref m_Current, updated);
            SetDiagnosticLocked(string.Empty);
        }

        m_CommandManager.Clear();
        PublishState(updated);
        if (loadFromDisk)
        {
            m_RuntimeSceneService.RequestSceneLoad(updated.Scene);
        }
        else
        {
            m_RuntimeSceneService.RequestSceneLoad(updated.CreateSnapshot());
        }

        return EditorSceneDocumentResult.Ok($"Discarded changes to scene '{updated.Name}'.");
    }

    public void Dispose()
    {
        lock (m_Gate)
        {
            if (m_Disposed)
            {
                return;
            }

            m_Disposed = true;
            m_RuntimeSceneService.ActiveSceneChanged -= OnRuntimeSceneChanged;
            m_RuntimeSceneService.SceneLoadCompleted -= OnRuntimeSceneLoadCompleted;
            m_AssetDatabase.AssetChanged -= OnAssetChanged;
            m_Pending = null;
        }
    }

    private void OnRuntimeSceneChanged(RuntimeSceneState runtimeScene)
    {
        EditorSceneDocumentState? publish = null;
        SceneSourceSnapshot? restore = null;
        bool clearHistory = false;
        string? failure = null;

        lock (m_Gate)
        {
            if (m_Disposed)
            {
                return;
            }

            if (m_Pending is { } pending && IsSameScene(pending.Scene, runtimeScene.Scene))
            {
                m_Pending = null;
                Volatile.Write(ref m_Current, pending);
                SetDiagnosticLocked(string.Empty);
                publish = pending;
                clearHistory = true;
            }
            else if (m_Current is { } current && IsSameScene(current.Scene, runtimeScene.Scene))
            {
                if (runtimeScene.SourceRevision == current.Revision ||
                    (runtimeScene.SourceRevision == 0 && !current.IsDirty))
                {
                    return;
                }

                if (current.IsDirty && runtimeScene.SourceRevision == 0)
                {
                    restore = current.CreateSnapshot();
                }
            }
            else if (m_Current is { IsDirty: true } dirty)
            {
                failure =
                    $"Blocked external scene activation while '{dirty.Name}' has unsaved changes.";
                SetDiagnosticLocked(failure);
                restore = dirty.CreateSnapshot();
            }
            else if (TryCreateDocumentFromDisk(
                         runtimeScene.Scene,
                         out var externalDocument,
                         out var diagnostic))
            {
                Volatile.Write(ref m_Current, externalDocument);
                SetDiagnosticLocked(string.Empty);
                publish = externalDocument;
                clearHistory = true;
            }
            else
            {
                failure = diagnostic;
                SetDiagnosticLocked(diagnostic);
            }
        }

        if (clearHistory)
        {
            m_CommandManager.Clear();
        }
        if (publish != null)
        {
            PublishState(publish);
        }
        if (failure != null)
        {
            PublishFailure(failure);
        }
        if (restore != null)
        {
            m_RuntimeSceneService.RequestSceneLoad(restore);
        }
    }

    private void OnRuntimeSceneLoadCompleted(RuntimeSceneLoadReport report)
    {
        if (report.Result.Success || report.Kind != RuntimeSceneInstanceKind.Persistent)
        {
            return;
        }

        bool matchesPending = false;
        lock (m_Gate)
        {
            if (m_Disposed)
            {
                return;
            }

            if (m_Pending is { } pending && IsSameScene(pending.Scene, report.Scene))
            {
                m_Pending = null;
                matchesPending = true;
            }
            SetDiagnosticLocked(report.Result.Diagnostic);
        }

        if (matchesPending || IsActiveScene(report.Scene))
        {
            PublishFailure(report.Result.Diagnostic);
        }
    }

    private void OnAssetChanged(AssetChangeEvent change)
    {
        if (change.Kind is not (
                AssetChangeKind.Changed or
                AssetChangeKind.Deleted or
                AssetChangeKind.Renamed))
        {
            return;
        }

        EditorSceneDocumentState? publish = null;
        bool requestReload = false;
        string? failure = null;

        lock (m_Gate)
        {
            if (m_Disposed || m_Current is not { } current || current.Scene.Guid != change.Guid)
            {
                return;
            }

            if (change.Kind == AssetChangeKind.Deleted || !File.Exists(current.SourcePath))
            {
                publish = current with { HasExternalChanges = true };
                Volatile.Write(ref m_Current, publish);
                failure = $"Active scene source was removed: {current.SourcePath}";
                SetDiagnosticLocked(failure);
            }
            else if (!TryReadUtf8(current.SourcePath, out var diskSource, out _, out var readError))
            {
                failure = readError;
                SetDiagnosticLocked(readError);
            }
            else if (string.Equals(diskSource, current.SavedSource, StringComparison.Ordinal))
            {
                return;
            }
            else if (current.IsDirty)
            {
                publish = current with { HasExternalChanges = true };
                Volatile.Write(ref m_Current, publish);
                failure =
                    "The active scene changed on disk while it also has unsaved editor changes.";
                SetDiagnosticLocked(failure);
            }
            else if (TryCreateDocumentFromDisk(
                         current.Scene,
                         out var reloaded,
                         out var diagnostic))
            {
                Volatile.Write(ref m_Current, reloaded);
                publish = reloaded;
                requestReload = m_Pending == null;
                SetDiagnosticLocked(string.Empty);
            }
            else
            {
                failure = diagnostic;
                SetDiagnosticLocked(diagnostic);
            }
        }

        if (publish != null)
        {
            PublishState(publish);
        }
        if (failure != null)
        {
            PublishFailure(failure);
        }
        if (requestReload)
        {
            m_CommandManager.Clear();
            m_RuntimeSceneService.RequestSceneLoad(publish!.Scene);
        }
    }

    private bool TryCreateDocumentFromDisk(
        AssetRef<SceneSourceAsset> scene,
        out EditorSceneDocumentState document,
        out string diagnostic)
    {
        if (!scene.IsValid)
        {
            document = null!;
            diagnostic = "Editor scene document requires a valid scene asset reference.";
            return false;
        }

        if (!m_AssetDatabase.TryGetAsset(scene, out var asset) ||
            !string.Equals(asset.AssetType, "Scene", StringComparison.OrdinalIgnoreCase))
        {
            document = null!;
            diagnostic = $"Scene asset '{scene.Guid:D}' is not indexed as Scene.";
            return false;
        }

        if (!TryReadUtf8(asset.SourcePath, out var source, out var hasBom, out diagnostic))
        {
            document = null!;
            return false;
        }

        var snapshot = new SceneSourceSnapshot(scene, asset.SourcePath, source, 0);
        var inspection = SceneAssetLoader.InspectScene(m_AssetDatabase, snapshot);
        if (inspection.Entities.Count == 0)
        {
            document = null!;
            diagnostic = string.IsNullOrWhiteSpace(inspection.Diagnostic)
                ? $"Scene '{asset.SourcePath}' has no inspectable entities."
                : inspection.Diagnostic;
            return false;
        }

        document = new EditorSceneDocumentState(
            scene,
            ResolveSceneName(inspection, asset.SourcePath),
            asset.SourcePath,
            source,
            source,
            hasBom,
            AssetPathPolicy.IsEditableAssetPath(asset.SourcePath),
            false,
            0,
            inspection);
        diagnostic = string.Empty;
        return true;
    }

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
            var sourceBytes = hasBom
                ? bytes.AsSpan(Encoding.UTF8.Preamble.Length)
                : bytes.AsSpan();
            source = new UTF8Encoding(false, true).GetString(sourceBytes);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            source = string.Empty;
            hasBom = false;
            diagnostic = $"Failed to read scene source '{path}': {ex.Message}";
            return false;
        }
    }

    private static void WriteUtf8Atomically(string path, string source, bool includeBom)
    {
        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Scene source path has no parent directory.");
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        byte[] output;
        if (includeBom)
        {
            byte[] preamble = Encoding.UTF8.GetPreamble();
            output = new byte[preamble.Length + sourceBytes.Length];
            preamble.CopyTo(output, 0);
            sourceBytes.CopyTo(output, preamble.Length);
        }
        else
        {
            output = sourceBytes;
        }

        try
        {
            File.WriteAllBytes(temporaryPath, output);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string ResolveSceneName(SceneInspectionResult inspection, string sourcePath)
    {
        return string.IsNullOrWhiteSpace(inspection.SceneName)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : inspection.SceneName;
    }

    private static bool IsSameScene(
        AssetRef<SceneSourceAsset> left,
        AssetRef<SceneSourceAsset> right)
    {
        return left.Guid == right.Guid &&
               string.Equals(left.PackageId, right.PackageId, StringComparison.OrdinalIgnoreCase);
    }

    private void SetDiagnosticLocked(string diagnostic)
    {
        Volatile.Write(ref m_LastDiagnostic, diagnostic ?? string.Empty);
    }

    private void PublishState(EditorSceneDocumentState? state)
    {
        var handlers = StateChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<EditorSceneDocumentState?> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(state);
            }
            catch
            {
                // UI observers cannot invalidate document state transitions.
            }
        }
    }

    private void PublishFailure(string diagnostic)
    {
        var handlers = OperationFailed;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(diagnostic);
            }
            catch
            {
                // Diagnostics observers cannot invalidate document state transitions.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(m_Disposed, this);
    }
}
