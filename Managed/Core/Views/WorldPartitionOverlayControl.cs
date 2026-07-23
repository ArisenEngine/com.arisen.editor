using ArisenEditor.Core.Services;
using ArisenEngine.Resources.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ArisenEditor.Views;

internal sealed class WorldPartitionOverlayControl : Control
{
    private static readonly IBrush s_UnloadedBrush = new SolidColorBrush(Color.Parse("#475569"));
    private static readonly IBrush s_DesiredBrush = new SolidColorBrush(Color.Parse("#0ea5e9"));
    private static readonly IBrush s_ActiveBrush = new SolidColorBrush(Color.Parse("#22c55e"));
    private static readonly IBrush s_PinnedBrush = new SolidColorBrush(Color.Parse("#eab308"));
    private static readonly IBrush s_EditDependencyBrush = new SolidColorBrush(Color.Parse("#d97706"));
    private static readonly IBrush s_FailedBrush = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush s_DirtyBrush = new SolidColorBrush(Color.Parse("#f97316"));
    private static readonly Pen s_BorderPen = new(new SolidColorBrush(Color.Parse("#cbd5e1")), 1.0);
    private IEditorWorldDocumentService? m_Documents;
    private EditorWorldDocumentState? m_State;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (m_Documents != null) return;
        ArisenKernel.Lifecycle.EngineKernel.Instance.Services.TryGetService(out m_Documents);
        if (m_Documents == null) return;
        m_State = m_Documents.Current;
        m_Documents.StateChanged += OnStateChanged;
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (m_Documents != null) m_Documents.StateChanged -= OnStateChanged;
        m_Documents = null;
        m_State = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        EditorWorldDocumentState? state = m_State;
        if (state == null || state.Cells.Count == 0 || Bounds.Width <= 1 || Bounds.Height <= 1)
        {
            return;
        }

        MapProjection projection = CreateProjection(state);

        foreach (EditorWorldCellDocumentState cell in state.Cells.OrderBy(cell => cell.CellId))
        {
            Rect rect = projection.Project(cell.Descriptor.Bounds);
            IBrush fill = ResolveBrush(cell);
            bool selected = state.SelectedCellId == cell.CellId || state.FocusedCellId == cell.CellId;
            context.DrawRectangle(fill, selected ? new Pen(Brushes.White, 2.0) : s_BorderPen, rect);
            if (cell.IsDirty)
            {
                context.DrawRectangle(
                    s_DirtyBrush,
                    null,
                    new Rect(rect.Right - 6.0, rect.Top + 1.0, 5.0, 5.0));
            }
        }

        DrawPersistentCamera(context, state, projection);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        EditorWorldDocumentState? state = m_State;
        if (m_Documents == null || state == null || state.Cells.Count == 0) return;

        Point position = e.GetPosition(this);
        MapProjection projection = CreateProjection(state);
        foreach (EditorWorldCellDocumentState cell in state.Cells.OrderByDescending(cell => cell.CellId))
        {
            if (!projection.Project(cell.Descriptor.Bounds).Contains(position)) continue;
            m_Documents.SelectCell(cell.CellId);
            e.Handled = true;
            return;
        }
    }

    private void OnStateChanged(EditorWorldDocumentState? state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            m_State = state;
            InvalidateVisual();
        });
    }

    private static IBrush ResolveBrush(EditorWorldCellDocumentState cell)
    {
        if (cell.Streaming.State == WorldCellStreamingState.Failed) return s_FailedBrush;
        if (cell.IsEditPinned || cell.Streaming.Pinned) return s_PinnedBrush;
        if (cell.IsEditDependency) return s_EditDependencyBrush;
        if (cell.Streaming.State == WorldCellStreamingState.Active) return s_ActiveBrush;
        if (cell.IsRuntimeDesired) return s_DesiredBrush;
        return s_UnloadedBrush;
    }

    private MapProjection CreateProjection(EditorWorldDocumentState state)
    {
        double minX = state.Cells.Min(cell => cell.Descriptor.Bounds.Min.X);
        double maxX = state.Cells.Max(cell => cell.Descriptor.Bounds.Max.X);
        double minZ = state.Cells.Min(cell => cell.Descriptor.Bounds.Min.Z);
        double maxZ = state.Cells.Max(cell => cell.Descriptor.Bounds.Max.Z);
        return new MapProjection(
            minX,
            minZ,
            Math.Max(1.0, maxX - minX),
            Math.Max(1.0, maxZ - minZ),
            Math.Max(1.0, Bounds.Width - 16.0),
            Math.Max(1.0, Bounds.Height - 16.0));
    }

    private static void DrawPersistentCamera(
        DrawingContext context,
        EditorWorldDocumentState state,
        MapProjection projection)
    {
        SceneEntityInspection? camera = state.PersistentScene.Inspection.Entities
            .FirstOrDefault(entity => entity.Camera != null);
        if (camera == null) return;

        var position = camera.Transform.Position;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Z)) return;
        Point marker = projection.Project(position.X, position.Z);
        if (!projection.ContentBounds.Contains(marker)) return;

        var outline = new Pen(Brushes.Black, 1.0);
        var cross = new Pen(Brushes.White, 1.0);
        context.DrawEllipse(Brushes.White, outline, marker, 4.0, 4.0);
        context.DrawLine(cross, new Point(marker.X - 7.0, marker.Y), new Point(marker.X + 7.0, marker.Y));
        context.DrawLine(cross, new Point(marker.X, marker.Y - 7.0), new Point(marker.X, marker.Y + 7.0));
    }

    private readonly record struct MapProjection(
        double MinX,
        double MinZ,
        double SpanX,
        double SpanZ,
        double Width,
        double Height)
    {
        private const double Padding = 8.0;

        public Rect ContentBounds => new(Padding, Padding, Width, Height);

        public Rect Project(WorldBounds bounds)
        {
            Point minimum = Project(bounds.Min.X, bounds.Min.Z);
            Point maximum = Project(bounds.Max.X, bounds.Max.Z);
            return new Rect(
                minimum.X,
                minimum.Y,
                Math.Max(2.0, maximum.X - minimum.X),
                Math.Max(2.0, maximum.Y - minimum.Y));
        }

        public Point Project(double worldX, double worldZ) => new(
            Padding + ((worldX - MinX) / SpanX) * Width,
            Padding + ((worldZ - MinZ) / SpanZ) * Height);
    }
}
