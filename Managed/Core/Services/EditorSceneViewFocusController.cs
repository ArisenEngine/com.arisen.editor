using ArisenEngine.Core.Assets;
using ArisenEngine.Rendering;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.Resources.Serialization;

namespace ArisenEditor.Core.Services;

internal sealed class EditorSceneViewFocusController : IDisposable
{
    private static readonly IReadOnlyDictionary<Guid, MeshBounds> s_EmptyMeshBounds =
        new Dictionary<Guid, MeshBounds>();

    private readonly IEditorWorldDocumentService m_Documents;
    private readonly RenderSubsystem m_Rendering;
    private readonly IAssetDatabase m_AssetDatabase;
    private readonly object m_MeshBoundsGate = new();
    private readonly Dictionary<Guid, MeshBounds> m_MeshBoundsByGuid = new();
    private Guid m_FocusedWorldGuid;
    private WorldCellId m_FocusedCellId;
    private bool m_Disposed;

    public EditorSceneViewFocusController(
        IEditorWorldDocumentService documents,
        RenderSubsystem rendering,
        IAssetDatabase assetDatabase)
    {
        m_Documents = documents ?? throw new ArgumentNullException(nameof(documents));
        m_Rendering = rendering ?? throw new ArgumentNullException(nameof(rendering));
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_Documents.FocusRequested += OnFocusRequested;
        m_Documents.StateChanged += OnStateChanged;
        m_AssetDatabase.AssetChanged += OnAssetChanged;
    }

    public void Dispose()
    {
        if (m_Disposed) return;
        m_Disposed = true;
        m_AssetDatabase.AssetChanged -= OnAssetChanged;
        m_Documents.FocusRequested -= OnFocusRequested;
        m_Documents.StateChanged -= OnStateChanged;
        lock (m_MeshBoundsGate)
        {
            m_MeshBoundsByGuid.Clear();
        }
        ClearFocus();
    }

    private void OnFocusRequested(WorldCellId cellId, WorldPosition _)
    {
        EditorWorldDocumentState? state = m_Documents.Current;
        EditorWorldCellDocumentState? cell = state?.Cells.FirstOrDefault(
            candidate => candidate.CellId == cellId);
        if (state == null ||
            cell == null ||
            !EditorSceneViewFocusFraming.TryCreate(
                state.Descriptor.Partition,
                cell.Descriptor,
                cell.SceneDocument.Inspection,
                cell.Descriptor.FocusBounds.HasValue
                    ? s_EmptyMeshBounds
                    : ResolveMeshBounds(cell.SceneDocument.Inspection),
                out EditorSceneViewFocusFrame frame))
        {
            ClearFocus();
            return;
        }

        m_Rendering.SetSceneViewCameraOverride(frame.Camera);
        m_FocusedWorldGuid = state.Descriptor.WorldGuid;
        m_FocusedCellId = cellId;
    }

    private void OnStateChanged(EditorWorldDocumentState? state)
    {
        if (!m_FocusedCellId.IsValid) return;
        if (state == null ||
            state.Descriptor.WorldGuid != m_FocusedWorldGuid ||
            state.FocusedCellId != m_FocusedCellId ||
            state.Cells.All(cell => cell.CellId != m_FocusedCellId))
        {
            ClearFocus();
        }
    }

    private IReadOnlyDictionary<Guid, MeshBounds> ResolveMeshBounds(
        SceneInspectionResult inspection)
    {
        var resolved = new Dictionary<Guid, MeshBounds>();
        IReadOnlyList<SceneEntityInspection>? entities = inspection.Entities;
        if (!inspection.Success || entities == null)
        {
            return resolved;
        }

        for (int index = 0; index < entities.Count; index++)
        {
            SceneMeshRendererInspection? mesh = entities[index].MeshRenderer;
            if (mesh is not { Visible: true } ||
                mesh.Mesh.Guid == Guid.Empty ||
                EditorSceneViewFocusFraming.HasAuthoredBounds(mesh) ||
                resolved.ContainsKey(mesh.Mesh.Guid))
            {
                continue;
            }

            if (TryResolveMeshBounds(mesh.Mesh.Guid, out MeshBounds bounds))
            {
                resolved.Add(mesh.Mesh.Guid, bounds);
            }
        }

        return resolved;
    }

    private bool TryResolveMeshBounds(Guid meshGuid, out MeshBounds bounds)
    {
        lock (m_MeshBoundsGate)
        {
            if (m_MeshBoundsByGuid.TryGetValue(meshGuid, out bounds))
            {
                return true;
            }

            if (!MeshAssetCooker.TryReadCookedBounds(m_AssetDatabase, meshGuid, out bounds))
            {
                return false;
            }

            m_MeshBoundsByGuid.Add(meshGuid, bounds);
            return true;
        }
    }

    private void OnAssetChanged(AssetChangeEvent change)
    {
        lock (m_MeshBoundsGate)
        {
            if (change.Guid == Guid.Empty)
            {
                m_MeshBoundsByGuid.Clear();
            }
            else
            {
                m_MeshBoundsByGuid.Remove(change.Guid);
            }
        }
    }

    private void ClearFocus()
    {
        m_Rendering.ClearSceneViewCameraOverride();
        m_FocusedWorldGuid = Guid.Empty;
        m_FocusedCellId = default;
    }
}
