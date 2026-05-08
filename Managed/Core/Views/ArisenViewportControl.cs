using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ArisenEngine.Rendering;
using ArisenKernel.Lifecycle;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;

namespace ArisenEditor.Views;

/// <summary>
/// A high-performance viewport control that displays the Arisen Engine's output.
/// Refactored to follow the official Avalonia GPU Interop pattern for maximum stability and performance.
/// </summary>
public partial class ArisenViewportControl : Control
{
    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private ICompositionGpuInterop? _interop;
    private RenderSubsystem? _renderSubsystem;
    
    private bool _isInitialized;
    private bool _isUpdating;
    private bool _updateQueued;
    private ulong _lastSeenTicket;
    private uint _lastImportedWidth;
    private uint _lastImportedHeight;
    private DateTime _lastRecoveryTime = DateTime.MinValue;
    
    private readonly Action _updateAction;
    private readonly Dictionary<IntPtr, ICompositionImportedGpuImage> _imageCache = new();

    public ArisenViewportControl()
    {
        _updateAction = OnCompositionUpdate;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Initialize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Shutdown();
        base.OnDetachedFromVisualTree(e);
    }

    private async void Initialize()
    {
        if (_isInitialized) return;

        try
        {
            // 1. Get Compositor and Interop
            var selfVisual = ElementComposition.GetElementVisual(this);
            if (selfVisual == null) return;
            
            var compositor = selfVisual.Compositor;
            _interop = await compositor.TryGetCompositionGpuInterop();
            
            if (_interop == null)
            {
                KernelLog.Error("[ArisenViewportControl] GPU Interop is not supported on this platform.");
                return;
            }

            // 2. Setup Composition Visuals
            _surface = compositor.CreateDrawingSurface();
            _visual = compositor.CreateSurfaceVisual();
            _visual.Size = new Avalonia.Vector((float)Bounds.Width, (float)Bounds.Height);
            _visual.Surface = _surface;
            
            ElementComposition.SetElementChildVisual(this, _visual);

            // 3. Connect to Arisen RenderSubsystem
            _renderSubsystem = EngineKernel.Instance.Services.GetService<RenderSubsystem>();
            if (_renderSubsystem != null)
            {
                var pixelSize = GetPhysicalPixelSize();
                _renderSubsystem.RegisterSurface(this.Handle.Handle, "EditorViewport", SurfaceType.SceneView, pixelSize.Width, pixelSize.Height);
                KernelLog.Info($"[ArisenViewportControl] Registered surface 0x{this.Handle.Handle:X} ({pixelSize.Width}x{pixelSize.Height})");
            }

            _isInitialized = true;
            QueueNextFrame();
        }
        catch (Exception ex)
        {
            KernelLog.Error($"[ArisenViewportControl] Initialization failed: {ex.Message}");
        }
    }

    private void Shutdown()
    {
        _isInitialized = false;
        
        if (_renderSubsystem != null)
        {
            _renderSubsystem.UnregisterSurface(this.Handle.Handle);
        }

        lock (_imageCache)
        {
            foreach (var img in _imageCache.Values) (img as IDisposable)?.Dispose();
            _imageCache.Clear();
        }

        _surface?.Dispose();
        _surface = null;
        _visual = null;
        _interop = null;
    }

    private void OnCompositionUpdate()
    {
        _updateQueued = false;
        if (!_isInitialized || _isUpdating) return;

        // Sync visual size
        if (_visual != null)
        {
            _visual.Size = new Avalonia.Vector((float)Bounds.Width, (float)Bounds.Height);
        }

        // Pull latest frame info from engine
        if (_renderSubsystem == null)
        {
            _renderSubsystem = EngineKernel.Instance.Services.GetService<RenderSubsystem>();
            if (_renderSubsystem == null) return;
        }

        if (!_renderSubsystem.GetOutputInfo(this.Handle.Handle, out var info))
        {
            QueueNextFrame();
            return;
        }

        // Skip if nothing new or engine is still warming up
        if (info.Ticket == 0 || info.Ticket == _lastSeenTicket || info.SharedHandle == IntPtr.Zero)
        {
            QueueNextFrame();
            return;
        }

        _lastSeenTicket = info.Ticket;
        RenderFrameAsync(info);
    }

    private async void RenderFrameAsync(RenderOutputInfo info)
    {
        _isUpdating = true;
        
        try
        {
            // CRITICAL: Await GPU completion before attempting to import or update.
            // This prevents race conditions where Avalonia reads an image Vulkan is still writing.
            await _renderSubsystem!.WaitForRenderTicketAsync(this.Handle.Handle, info.Ticket);

            if (!_isInitialized || _interop == null || _surface == null) return;

            // Handle Resize
            if (info.Width != _lastImportedWidth || info.Height != _lastImportedHeight)
            {
                ClearImageCache();
                _lastImportedWidth = info.Width;
                _lastImportedHeight = info.Height;
            }

            // Get or Import Image
            if (!_imageCache.TryGetValue(info.SharedHandle, out var importedImage))
            {
                var handle = new PlatformHandle(info.SharedHandle, KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle);
                var properties = new PlatformGraphicsExternalImageProperties
                {
                    Width = (int)info.Width,
                    Height = (int)info.Height,
                    Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm
                };

                importedImage = _interop.ImportImage(handle, properties);
                lock (_imageCache)
                {
                    _imageCache[info.SharedHandle] = importedImage;
                }
            }

            // Push to Avalonia Compositor
            await _surface.UpdateAsync(importedImage);

            // Report consumption back to engine for back-pressure
            _renderSubsystem?.ReportConsumedFrameIndex(this.Handle.Handle, info.FrameIndex);
        }
        catch (PlatformGraphicsContextLostException)
        {
            KernelLog.Warning("[ArisenViewportControl] Graphics context lost. Triggering recovery...");
            RecoverContext();
        }
        catch (Exception ex)
        {
            KernelLog.Error($"[ArisenViewportControl] Frame update failed: {ex.Message}");
        }
        finally
        {
            _isUpdating = false;
            QueueNextFrame();
        }
    }

    private void QueueNextFrame()
    {
        if (_isInitialized && !_updateQueued)
        {
            var visual = ElementComposition.GetElementVisual(this);
            if (visual != null)
            {
                _updateQueued = true;
                visual.Compositor.RequestCompositionUpdate(_updateAction);
            }
        }
    }

    private void RecoverContext()
    {
        if ((DateTime.UtcNow - _lastRecoveryTime).TotalMilliseconds < 1000) return;
        _lastRecoveryTime = DateTime.UtcNow;

        Shutdown();
        Initialize();
    }

    private void ClearImageCache()
    {
        lock (_imageCache)
        {
            foreach (var img in _imageCache.Values) (img as IDisposable)?.Dispose();
            _imageCache.Clear();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _isInitialized)
        {
            var size = GetPhysicalPixelSize();
            _renderSubsystem?.ResizeSurface(this.Handle.Handle, size.Width, size.Height);
            QueueNextFrame();
        }
    }

    private PixelSize GetPhysicalPixelSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return new PixelSize((int)Math.Max(1, Bounds.Width * scaling), (int)Math.Max(1, Bounds.Height * scaling));
    }
}

// Handle Helper for Surface Tracking
internal class ControlHandle
{
    public IntPtr Handle { get; set; }
}

public partial class ArisenViewportControl
{
    private static int s_NextViewportId = 10000;
    internal ControlHandle Handle { get; } = new ControlHandle() { Handle = new IntPtr(System.Threading.Interlocked.Increment(ref s_NextViewportId)) };
}
