using System.Collections.ObjectModel;
using ArisenEditor.Core.Services;
using ArisenEditorFramework.Core;
using ArisenEngine.Resources.Serialization;
using Avalonia.Media;
using Avalonia.Threading;
using ReactiveUI;

namespace ArisenEditor.ViewModels;

internal sealed class WorldPartitionCellViewModel : ReactiveObject
{
    private EditorWorldCellDocumentState m_State;

    public WorldPartitionCellViewModel(EditorWorldCellDocumentState state)
    {
        m_State = state;
    }

    public WorldCellId CellId => m_State.CellId;
    public string Coordinate =>
        $"{m_State.Descriptor.Key.Coordinate.X}, {m_State.Descriptor.Key.Coordinate.Y}, {m_State.Descriptor.Key.Coordinate.Z}";
    public string Layer => m_State.Descriptor.Key.Layer;
    public string SceneName => m_State.SceneDocument.Name;
    public string State => m_State.Streaming.State.ToString();
    public string Ownership => (m_State.IsEditPinned || m_State.Streaming.Pinned, m_State.IsEditDependency, m_State.IsRuntimeDesired) switch
    {
        (true, _, true) => "Edit pin + runtime",
        (true, _, false) => "Edit pin",
        (false, true, true) => "Edit dependency + runtime",
        (false, true, false) => "Edit dependency",
        (false, false, true) => "Runtime desired",
        _ => "None"
    };
    public string Dirty => m_State.IsDirty ? "*" : string.Empty;
    public string Diagnostic => m_State.Streaming.Diagnostic;
    public bool IsEditPinned => m_State.IsEditPinned;
    public bool IsDirty => m_State.IsDirty;
    public bool IsFailed => m_State.Streaming.State == WorldCellStreamingState.Failed;
    public IBrush StateBrush => m_State.Streaming.State switch
    {
        WorldCellStreamingState.Active => Brushes.MediumSeaGreen,
        WorldCellStreamingState.Failed => Brushes.IndianRed,
        WorldCellStreamingState.Cancelled => Brushes.DarkGray,
        WorldCellStreamingState.Unloaded => Brushes.Gray,
        WorldCellStreamingState.QueuedToUnload or WorldCellStreamingState.Unloading => Brushes.Goldenrod,
        _ => Brushes.DeepSkyBlue
    };

    public void Update(EditorWorldCellDocumentState state)
    {
        m_State = state;
        this.RaisePropertyChanged(string.Empty);
    }
}

internal sealed class WorldPartitionViewModel : EditorPanelBase, IDisposable
{
    private readonly IEditorWorldDocumentService? m_Documents;
    private readonly SelectionService? m_SelectionService;
    private WorldPartitionCellViewModel? m_SelectedCell;
    private string m_WorldName = "No active world";
    private string m_StatusText = string.Empty;
    private string m_MetricsText = string.Empty;
    private bool m_IsApplyingState;

    public override string Title => "World Partition";
    public override string Id => "WorldPartition";
    public override object Content => new ArisenEditor.Views.WorldPartitionView { DataContext = this };

    public ObservableCollection<WorldPartitionCellViewModel> Cells { get; } = new();

    public WorldPartitionCellViewModel? SelectedCell
    {
        get => m_SelectedCell;
        set
        {
            if (ReferenceEquals(m_SelectedCell, value)) return;
            this.RaiseAndSetIfChanged(ref m_SelectedCell, value);
            this.RaisePropertyChanged(nameof(HasSelectedCell));
            if (!m_IsApplyingState &&
                value != null &&
                m_Documents?.Current?.SelectedCellId != value.CellId)
            {
                m_Documents?.SelectCell(value.CellId);
            }
        }
    }

    public bool HasSelectedCell => m_SelectedCell != null;

    public string WorldName
    {
        get => m_WorldName;
        private set => this.RaiseAndSetIfChanged(ref m_WorldName, value);
    }

    public string StatusText
    {
        get => m_StatusText;
        private set => this.RaiseAndSetIfChanged(ref m_StatusText, value);
    }

    public string MetricsText
    {
        get => m_MetricsText;
        private set => this.RaiseAndSetIfChanged(ref m_MetricsText, value);
    }

    public IReactiveCommand PinCommand { get; }
    public IReactiveCommand FocusCommand { get; }
    public IReactiveCommand SaveAllCommand { get; }
    public IReactiveCommand ReimportCommand { get; }
    public IReactiveCommand RetryCommand { get; }
    public IReactiveCommand MoveSelectedEntityCommand { get; }

    public WorldPartitionViewModel(SelectionService? selectionService = null)
    {
        m_SelectionService = selectionService;
        ArisenKernel.Lifecycle.EngineKernel.Instance.Services.TryGetService(out m_Documents);
        if (m_Documents != null)
        {
            m_Documents.StateChanged += OnStateChanged;
            ShowState(m_Documents.Current);
        }

        PinCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedCell == null || m_Documents == null) return;
            bool success = SelectedCell.IsEditPinned
                ? m_Documents.UnloadCellForEditing(SelectedCell.CellId)
                : m_Documents.LoadCellForEditing(SelectedCell.CellId);
            StatusText = success ? "Cell edit pin updated." : "Cell pin request was rejected.";
        });
        FocusCommand = ReactiveCommand.Create(() => RunSelected(
            cell => m_Documents?.FocusCell(cell.CellId) == true,
            "Focused SceneView on selected cell; residency is unchanged."));
        SaveAllCommand = ReactiveCommand.Create(() =>
        {
            if (m_Documents == null) return;
            EditorWorldDocumentResult result = m_Documents.SaveAll();
            StatusText = result.Diagnostic;
        });
        ReimportCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedCell == null || m_Documents == null) return;
            StatusText = m_Documents.ReimportCell(SelectedCell.CellId).Diagnostic;
        });
        RetryCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedCell == null) return;
            bool success = ArisenKernel.Lifecycle.EngineKernel.Instance.Services
                .GetService<IRuntimeWorldStreamingService>()
                .RetryCell(SelectedCell.CellId);
            StatusText = success ? "Cell retry requested." : "Selected cell is not retryable.";
        });
        MoveSelectedEntityCommand = ReactiveCommand.Create(MoveSelectedEntity);
    }

    public override void Dispose()
    {
        if (m_Documents != null) m_Documents.StateChanged -= OnStateChanged;
    }

    private void MoveSelectedEntity()
    {
        if (SelectedCell == null || m_Documents == null)
        {
            StatusText = "Select a target world cell first.";
            return;
        }
        if (m_SelectionService?.CurrentSelection is not SceneAssetEntityNodeViewModel entity ||
            !entity.IsWorldScene || !entity.CellId.IsValid)
        {
            StatusText = "Select an entity owned by a world cell in Hierarchy first.";
            return;
        }
        if (entity.CellId == SelectedCell.CellId)
        {
            StatusText = "The selected entity already belongs to the target cell.";
            return;
        }

        StatusText = m_Documents
            .MoveEntityToCell(entity.CellId, SelectedCell.CellId, entity.AuthoringGuid)
            .Diagnostic;
    }

    private void RunSelected(
        Func<WorldPartitionCellViewModel, bool> operation,
        string success)
    {
        if (SelectedCell == null)
        {
            StatusText = "Select a world cell first.";
            return;
        }
        StatusText = operation(SelectedCell) ? success : "Cell operation was rejected.";
    }

    private void OnStateChanged(EditorWorldDocumentState? state)
    {
        Dispatcher.UIThread.Post(() => ShowState(state));
    }

    private void ShowState(EditorWorldDocumentState? state)
    {
        m_IsApplyingState = true;
        try
        {
            if (state == null)
            {
                WorldName = "No active world";
                MetricsText = string.Empty;
                Cells.Clear();
                SelectedCell = null;
                return;
            }

            WorldName = state.Name + (state.IsDirty ? " *" : string.Empty);
            MetricsText =
                $"Active {state.Metrics.ActiveCells}  Queued {state.Metrics.QueuedCells}  " +
                $"I/O {FormatBytes(state.Metrics.BytesInFlight)}  " +
                $"Staging {FormatBytes(state.Metrics.DecodedStagingBytes)}  " +
                $"Failures {state.Metrics.FailedCells}";

            WorldCellId previousSelection = SelectedCell?.CellId ?? default;
            var byId = Cells.ToDictionary(cell => cell.CellId);
            var desired = new List<WorldPartitionCellViewModel>(state.Cells.Count);
            foreach (EditorWorldCellDocumentState cell in state.Cells)
            {
                if (!byId.TryGetValue(cell.CellId, out WorldPartitionCellViewModel? viewModel))
                {
                    viewModel = new WorldPartitionCellViewModel(cell);
                }
                else
                {
                    viewModel.Update(cell);
                }
                desired.Add(viewModel);
            }
            SynchronizeCells(Cells, desired);
            WorldCellId selectedId = state.SelectedCellId.IsValid
                ? state.SelectedCellId
                : previousSelection;
            SelectedCell = selectedId.IsValid
                ? Cells.FirstOrDefault(cell => cell.CellId == selectedId)
                : null;
        }
        finally
        {
            m_IsApplyingState = false;
        }
    }

    private static void SynchronizeCells(
        ObservableCollection<WorldPartitionCellViewModel> target,
        IReadOnlyList<WorldPartitionCellViewModel> desired)
    {
        for (int index = 0; index < desired.Count; index++)
        {
            WorldPartitionCellViewModel item = desired[index];
            if (index < target.Count && ReferenceEquals(target[index], item)) continue;

            int existingIndex = target.IndexOf(item);
            if (existingIndex >= 0) target.Move(existingIndex, index);
            else target.Insert(index, item);
        }

        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    private static string FormatBytes(long value)
    {
        if (value >= 1024 * 1024) return $"{value / (1024.0 * 1024.0):0.0} MiB";
        if (value >= 1024) return $"{value / 1024.0:0.0} KiB";
        return $"{value} B";
    }
}
