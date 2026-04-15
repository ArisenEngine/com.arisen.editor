using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.SceneGraph;
using ArisenEngine.Rendering;
using ArisenKernel.Lifecycle;
using ArisenKernel.Contracts;
using ArisenEditor.Core.Services;
using ArisenKernel.Diagnostics;

namespace ArisenEditor.Views;

/// <summary>
/// A custom Avalonia control that hosts the Arisen RenderGraph output.
/// Uses ICompositionGpuInterop for zero-overhead texture sharing.
/// </summary>
public partial class ArisenViewportControl : Control
{
    private bool m_IsRegistered = false;
    private RenderSubsystem? m_RenderSubsystem;
    
    // Composition members
    private CompositionDrawingSurface? m_CompositionSurface;
    private ICompositionGpuInterop? m_Interop;
    private bool m_IsUpdating = false;
    private DispatcherTimer? m_ResizeTimer;
    private PixelSize m_PendingResizeSize;
    private bool m_IsContextLost = false;
    private IntPtr m_LastSharedHandle = IntPtr.Zero;
    private ICompositionImportedGpuImage? m_LastImportedImage;

    private PixelSize GetPhysicalPixelSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return new PixelSize((int)Math.Max(1, Bounds.Width * scaling), (int)Math.Max(1, Bounds.Height * scaling));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        // Resolve RenderSubsystem from Engine Kernel
        m_RenderSubsystem = EngineKernel.Instance.Services.GetService<RenderSubsystem>();
        
        if (m_RenderSubsystem != null)
        {
            var size = GetPhysicalPixelSize();
            m_RenderSubsystem.RegisterSurface(this.Handle.Handle, "EditorViewport", SurfaceType.SceneView, size.Width, size.Height);
            m_IsRegistered = true;
        }

        _ = InitializeCompositionAsync();

        // Listen for size changes
        this.GetObservable(BoundsProperty).Subscribe(_ => UpdateSurfaceSize());
    }

    private async Task InitializeCompositionAsync()
    {
        try 
        {
            var visual = ElementComposition.GetElementVisual(this);
            var compositor = visual?.Compositor;
            if (compositor == null) return;

            m_Interop = await compositor.TryGetCompositionGpuInterop();
            if (m_Interop == null)
            {
                EditorLog.Error("[ArisenViewportControl] Composition GpuInterop not available on this platform.");
                return;
            }

            m_CompositionSurface = compositor.CreateDrawingSurface();
            
            // Create a SurfaceVisual to host our drawing surface
            var surfaceVisual = compositor.CreateSurfaceVisual();
            surfaceVisual.Surface = m_CompositionSurface;
            surfaceVisual.Size = new Avalonia.Vector((float)Bounds.Width, (float)Bounds.Height);
            
            ElementComposition.SetElementChildVisual(this, surfaceVisual);
            
            m_IsContextLost = false;
            EditorLog.Info("[ArisenViewportControl] Composition background established.");

            // Trigger visual update once interop is ready
            Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            EditorLog.Error($"[ArisenViewportControl] Composition initialization failed: {ex.Message}");
        }
    }

    private void UpdateSurfaceSize()
    {
        m_PendingResizeSize = GetPhysicalPixelSize();
        
        if (m_ResizeTimer == null)
        {
            m_ResizeTimer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(100), 
                DispatcherPriority.Normal, 
                (s, e) => {
                    m_ResizeTimer?.Stop();
                    ApplyPendingResize();
                });
        }
        else
        {
            m_ResizeTimer.Stop();
            m_ResizeTimer.Start();
        }

        // Update visual size immediately (logical pixels for Avalonia positioning)
        var visual = ElementComposition.GetElementChildVisual(this);
        if (visual != null)
        {
            visual.Size = new Avalonia.Vector((float)Bounds.Width, (float)Bounds.Height);
        }
    }

    private void ApplyPendingResize()
    {
        if (m_IsRegistered && m_RenderSubsystem != null)
        {
            EditorLog.Info($"[ArisenViewportControl] Applying debounced resize: {m_PendingResizeSize.Width}x{m_PendingResizeSize.Height}");
            m_RenderSubsystem.ResizeSurface(this.Handle.Handle, m_PendingResizeSize.Width, m_PendingResizeSize.Height);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (m_IsRegistered && m_RenderSubsystem != null)
        {
            m_RenderSubsystem.UnregisterSurface(this.Handle.Handle);
            m_IsRegistered = false;
        }
        
        m_CompositionSurface?.Dispose();
        m_CompositionSurface = null;
        m_Interop = null;
        
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (m_IsContextLost)
        {
            _ = InitializeCompositionAsync();
            DrawPlaceholder(context);
            return;
        }

        if (m_CompositionSurface == null || m_Interop == null || m_RenderSubsystem == null)
        {
            DrawPlaceholder(context);
            return;
        }

        IntPtr sharedHandle = m_RenderSubsystem.GetSurfaceSharedHandle(this.Handle.Handle);
        if (sharedHandle == IntPtr.Zero)
        {
            DrawPlaceholder(context);
            return;
        }

        UpdateCompositionSurface(sharedHandle);
        
        // Trigger next frame repaint
        Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);
    }

    private void DrawPlaceholder(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, Bounds.Width, Bounds.Height));
        var typeface = new Typeface("Inter");
        var text = new FormattedText("Arisen RenderGraph Active", System.Globalization.CultureInfo.CurrentCulture, 
                                     FlowDirection.LeftToRight, typeface, 16, Brushes.DimGray);
        context.DrawText(text, new Point(Bounds.Width / 2 - text.Width / 2, Bounds.Height / 2 - text.Height / 2));
    }

    private async void UpdateCompositionSurface(IntPtr sharedHandle)
    {
        if (m_Interop == null || m_CompositionSurface == null || m_IsUpdating) return;

        m_IsUpdating = true;
        try 
        {
            var pixelSize = GetPhysicalPixelSize();
            
            // Only re-import if the handle or size has changed
            if (sharedHandle != m_LastSharedHandle || m_LastImportedImage == null)
            {
                (m_LastImportedImage as IDisposable)?.Dispose();
                m_LastImportedImage = m_Interop.ImportImage(
                    new Avalonia.Platform.PlatformHandle(sharedHandle, Avalonia.Platform.KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
                    new Avalonia.Platform.PlatformGraphicsExternalImageProperties
                    {
                        Width = pixelSize.Width,
                        Height = pixelSize.Height,
                        Format = Avalonia.Platform.PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm
                    });
                m_LastSharedHandle = sharedHandle;
            }

            if (m_LastImportedImage != null)
            {
                await m_CompositionSurface.UpdateAsync(m_LastImportedImage);
            }
        }
        catch (Avalonia.Platform.PlatformGraphicsContextLostException)
        {
            m_IsContextLost = true;
            (m_LastImportedImage as IDisposable)?.Dispose();
            m_LastImportedImage = null;
            m_LastSharedHandle = IntPtr.Zero;
            EditorLog.Warning("[ArisenViewportControl] Graphics context lost. Recovery triggered.");
        }
        catch (Exception ex)
        {
            EditorLog.Error($"[ArisenViewportControl] Surface update failed: {ex.Message}");
        }
        finally
        {
            m_IsUpdating = false;
        }
    }
}

// Conceptual handle helper for Avalonia
internal class ControlHandle
{
    public IntPtr Handle { get; set; }
}
public partial class ArisenViewportControl
{
    internal ControlHandle Handle { get; } = new ControlHandle() { Handle = new IntPtr(1001) }; // GUID-lite for testing
}
