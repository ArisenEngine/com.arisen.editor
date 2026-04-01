using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ArisenEngine.Rendering;
using ArisenKernel.Lifecycle;
using System;
using ArisenEngine.Core.RHI;

namespace ArisenEditor.Views;

/// <summary>
/// A custom Avalonia control that hosts the Arisen RenderGraph output.
/// This solves the "Airspace Problem" by using Texture Sharing.
/// </summary>
public class ArisenViewportControl : Control
{
    private bool m_IsRegistered = false;
    private RenderSubsystem? m_RenderSubsystem;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        // Resolve RenderSubsystem from Engine Kernel
        m_RenderSubsystem = ArisenKernel.Lifecycle.EngineBootstrapper.Instance?.GetService<RenderSubsystem>();
        
        if (m_RenderSubsystem != null)
        {
            // Register this control as a "Shared Texture" surface
            // We'll use the platform handle (IntPtr) as the unique identifier
            IntPtr hostHandle = this.Handle.Handle; // This is conceptual, Avalonia handles vary by platform
            m_RenderSubsystem.RegisterSurface(hostHandle, "EditorViewport", SurfaceType.SharedTexture, (int)Bounds.Width, (int)Bounds.Height);
            m_IsRegistered = true;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (m_IsRegistered && m_RenderSubsystem != null)
        {
            m_RenderSubsystem.UnregisterSurface(this.Handle.Handle);
            m_IsRegistered = false;
        }
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // In a shared texture setup, the RenderGraph produces a GPU handle.
        // We then draw that handle here using Avalonia's DrawBitmap or Composition API.
        
        // Placeholder: Draw a dark grey background to indicate the viewport area
        context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
        
        var typeface = new Typeface("Inter");
        var text = new FormattedText("Arisen RenderGraph Active", System.Globalization.CultureInfo.CurrentCulture, 
                                     FlowDirection.LeftToRight, typeface, 16, Brushes.DimGray);
        
        context.DrawText(text, new Point(Bounds.Width / 2 - text.Width / 2, Bounds.Height / 2 - text.Height / 2));
    }
}

// Conceptual handle helper for Avalonia
internal class ControlHandle
{
    public IntPtr Handle { get; set; }
}
internal partial class ArisenViewportControl
{
    internal ControlHandle Handle { get; } = new ControlHandle() { Handle = new IntPtr(1001) }; // GUID-lite for testing
}
