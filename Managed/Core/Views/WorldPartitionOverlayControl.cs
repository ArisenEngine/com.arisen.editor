using ArisenEditor.Core.Services;
using ArisenEngine.Resources.Serialization;
using Avalonia;
using Avalonia.Controls;
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

        double minX = state.Cells.Min(cell => cell.Descriptor.Bounds.Min.X);
        double maxX = state.Cells.Max(cell => cell.Descriptor.Bounds.Max.X);
        double minZ = state.Cells.Min(cell => cell.Descriptor.Bounds.Min.Z);
        double maxZ = state.Cells.Max(cell => cell.Descriptor.Bounds.Max.Z);
        double spanX = Math.Max(1.0, maxX - minX);
        double spanZ = Math.Max(1.0, maxZ - minZ);
        const double padding = 8.0;
        double width = Math.Max(1.0, Bounds.Width - padding * 2.0);
        double height = Math.Max(1.0, Bounds.Height - padding * 2.0);

        foreach (EditorWorldCellDocumentState cell in state.Cells.OrderBy(cell => cell.CellId))
        {
            WorldBounds bounds = cell.Descriptor.Bounds;
            double left = padding + ((bounds.Min.X - minX) / spanX) * width;
            double right = padding + ((bounds.Max.X - minX) / spanX) * width;
            double top = padding + ((bounds.Min.Z - minZ) / spanZ) * height;
            double bottom = padding + ((bounds.Max.Z - minZ) / spanZ) * height;
            var rect = new Rect(
                left,
                top,
                Math.Max(2.0, right - left),
                Math.Max(2.0, bottom - top));
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
        if (cell.Streaming.State == WorldCellStreamingState.Active) return s_ActiveBrush;
        if (cell.Streaming.Desired) return s_DesiredBrush;
        return s_UnloadedBrush;
    }
}
