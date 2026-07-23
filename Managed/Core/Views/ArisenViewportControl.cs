using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
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
using ArisenEditor.Core.Validation;
using ArisenEditor.ViewModels;

namespace ArisenEditor.Views;

/// <summary>
/// A high-performance viewport control that displays the Arisen Engine's output.
/// Refactored to follow the official Avalonia GPU Interop pattern for maximum stability and performance.
/// </summary>
public partial class ArisenViewportControl : Control
{
    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private RenderSubsystem? _renderSubsystem;
    
    private bool _isInitialized;
    private bool _isUpdating;
    private bool _updateQueued;
    private string? _handleType;
    private string? _semaphoreType;
    private CompositionGpuImportedImageSynchronizationCapabilities _syncCapabilities;
    private bool _syncCapabilitiesLogged;
    private bool _firstImportLogged;
    private DateTime _lastRecoveryTime = DateTime.MinValue;
    private DateTime _lastPresentationSkipLogTime = DateTime.MinValue;
    private RenderOutputPresentationSkipReason _lastPresentationSkipReason;
    private RenderOutputPresentationState _presentationState;
    private int _lastRequestedSurfaceWidth;
    private int _lastRequestedSurfaceHeight;
    private int _outputReadyDispatchQueued;
    private EditorViewportKind _viewportKind = EditorViewportKind.SceneView;
    
    private readonly Action _updateAction;
    private readonly Action _outputReadyDispatchAction;
    private readonly Dictionary<IntPtr, ICompositionImportedGpuImage> _imageCache = new();

    public ArisenViewportControl()
    {
        _updateAction = OnCompositionUpdate;
        _outputReadyDispatchAction = DispatchOutputReady;
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
            
            _compositor = selfVisual.Compositor;
            _interop = await _compositor.TryGetCompositionGpuInterop();
            
            if (_interop == null)
            {
                KernelLog.Error("[ArisenViewportControl] GPU Interop is not supported on this platform.");
                return;
            }

            // Detect the correct handle type for the resource exported by Arisen's Vulkan backend.
            // Native code creates memory with VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_BIT and exports
            // it with vkGetMemoryWin32HandleKHR using that same handle type, so the Avalonia import type
            // must be VulkanOpaqueNtHandle. Importing that handle as D3D11TextureNtHandle leaves the
            // compositor without a valid image and the black XAML background remains visible.
            _handleType = null;
            var supported = _interop.SupportedImageHandleTypes;
            KernelLog.Info($"[ArisenViewportControl] Compositor supported handle types: [{string.Join(", ", supported)}]");
            
            if (supported.Contains(KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle))
                _handleType = KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle;
            else if (supported.Contains(KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureNtHandle))
            {
                KernelLog.Error("[ArisenViewportControl] The compositor supports D3D11TextureNtHandle, but Arisen currently exports VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_BIT. " +
                    "A D3D11TextureNtHandle import would not match the native resource. Native D3D-backed export or VulkanOpaqueNtHandle compositor support is required.");
                return;
            }
            
            if (_handleType == null)
            {
                KernelLog.Error($"[ArisenViewportControl] No compatible Vulkan opaque handle type found. Supported: [{string.Join(", ", supported)}]");
                return;
            }
            KernelLog.Info($"[ArisenViewportControl] Selected handle type: {_handleType}");

            _syncCapabilities = _interop.GetSynchronizationCapabilities(_handleType);
            _syncCapabilitiesLogged = true;
            KernelLog.Info($"[ArisenViewportControl] Synchronization capabilities for {_handleType}: {_syncCapabilities}; semaphore types: [{string.Join(", ", _interop.SupportedSemaphoreTypes)}]");

            if ((_syncCapabilities & CompositionGpuImportedImageSynchronizationCapabilities.Semaphores) != 0)
            {
                if (_interop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle))
                {
                    _semaphoreType = KnownPlatformGraphicsExternalSemaphoreHandleTypes.VulkanOpaqueNtHandle;
                    KernelLog.Info($"[ArisenViewportControl] Selected semaphore type: {_semaphoreType}");
                }
                else
                {
                    KernelLog.Error($"[ArisenViewportControl] Compositor requires semaphore synchronization, but VulkanOpaqueNtHandle semaphores are unsupported. Supported: [{string.Join(", ", _interop.SupportedSemaphoreTypes)}]");
                    return;
                }
            }

            if ((_syncCapabilities & CompositionGpuImportedImageSynchronizationCapabilities.Automatic) == 0 &&
                (_syncCapabilities & CompositionGpuImportedImageSynchronizationCapabilities.Semaphores) == 0)
            {
                KernelLog.Error($"[ArisenViewportControl] Unsupported imported image synchronization mode: {_syncCapabilities}");
                return;
            }

            if ((_syncCapabilities & CompositionGpuImportedImageSynchronizationCapabilities.Automatic) == 0)
            {
                KernelLog.Info("[ArisenViewportControl] Automatic imported-image synchronization is unavailable; " +
                    "using explicit exported Vulkan semaphores for viewport updates.");
            }

            // 2. Setup Composition Visuals
            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            UpdatePresentationVisualGeometry();
            _visual.Surface = _surface;
            
            ElementComposition.SetElementChildVisual(this, _visual);

            // 3. Connect to Arisen RenderSubsystem
            _renderSubsystem = EngineKernel.Instance.Services.GetService<RenderSubsystem>();
            if (_renderSubsystem != null)
            {
                _viewportKind = DataContext is EditorViewportViewModel { IsSceneView: false }
                    ? EditorViewportKind.GameView
                    : EditorViewportKind.SceneView;
                var pixelSize = GetPhysicalPixelSize();
                var surfaceType = _viewportKind == EditorViewportKind.SceneView
                    ? SurfaceType.SceneView
                    : SurfaceType.GameView;
                _renderSubsystem.RegisterSurface(
                    this.Handle.Handle,
                    _viewportKind.ToString(),
                    surfaceType,
                    pixelSize.Width,
                    pixelSize.Height);
                _lastRequestedSurfaceWidth = pixelSize.Width;
                _lastRequestedSurfaceHeight = pixelSize.Height;
                KernelLog.Info($"[ArisenViewportControl] Registered surface 0x{this.Handle.Handle:X} ({pixelSize.Width}x{pixelSize.Height})");
            }

            _isInitialized = true;
            if (_renderSubsystem != null)
            {
                _renderSubsystem.OutputFrameReady += OnOutputFrameReady;
            }

            RequestSurfaceResize(force: true);
            Dispatcher.UIThread.Post(() =>
            {
                RequestSurfaceResize(force: true);
                QueueNextFrame();
            }, DispatcherPriority.Loaded);
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
            _renderSubsystem.OutputFrameReady -= OnOutputFrameReady;
            _renderSubsystem.UnregisterSurface(this.Handle.Handle);
        }

        lock (_imageCache)
        {
            foreach (var img in _imageCache.Values) DisposeImportedImage(img);
            _imageCache.Clear();
        }

        _surface?.Dispose();
        _surface = null;
        _visual = null;
        _compositor = null;
        _interop = null;
        _handleType = null;
        _semaphoreType = null;
        _presentationState.Reset();
        _firstImportLogged = false;
        _syncCapabilities = default;
        _syncCapabilitiesLogged = false;
        _lastPresentationSkipReason = RenderOutputPresentationSkipReason.None;
        _lastPresentationSkipLogTime = DateTime.MinValue;
        _lastRequestedSurfaceWidth = 0;
        _lastRequestedSurfaceHeight = 0;
        Interlocked.Exchange(ref _outputReadyDispatchQueued, 0);
        _updateQueued = false;
    }

    private void OnOutputFrameReady(IntPtr host)
    {
        if (host != Handle.Handle || !Volatile.Read(ref _isInitialized) ||
            Interlocked.CompareExchange(ref _outputReadyDispatchQueued, 1, 0) != 0)
        {
            return;
        }

        try
        {
            Dispatcher.UIThread.Post(_outputReadyDispatchAction, DispatcherPriority.Render);
        }
        catch
        {
            Interlocked.Exchange(ref _outputReadyDispatchQueued, 0);
            throw;
        }
    }

    private void DispatchOutputReady()
    {
        Interlocked.Exchange(ref _outputReadyDispatchQueued, 0);
        QueueNextFrame();
    }

    private void OnCompositionUpdate()
    {
        _updateQueued = false;
        if (!_isInitialized || _isUpdating) return;

        // Sync visual size
        if (_visual != null)
        {
            UpdatePresentationVisualGeometry();
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

        var requiresSemaphores = (_syncCapabilities & CompositionGpuImportedImageSynchronizationCapabilities.Automatic) == 0;
        var decision = _presentationState.Evaluate(info, requiresSemaphores);

        // Skip if nothing new or engine is still warming up
        if (!decision.ShouldPresent)
        {
            LogPresentationSkipIfUseful(info, decision.SkipReason);

            if (decision.ShouldReleaseSignalSemaphore)
            {
                ReleaseConsumedSemaphore(info.SignalSemaphoreHandle);
            }

            QueueNextFrame();
            return;
        }

        RenderFrameAsync(info);
    }

    private void LogPresentationSkipIfUseful(in RenderOutputInfo info, RenderOutputPresentationSkipReason reason)
    {
        if (reason is RenderOutputPresentationSkipReason.None or
            RenderOutputPresentationSkipReason.WarmingUp or
            RenderOutputPresentationSkipReason.DuplicateTicket)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (reason == _lastPresentationSkipReason &&
            (now - _lastPresentationSkipLogTime).TotalSeconds < 1.0)
        {
            return;
        }

        _lastPresentationSkipReason = reason;
        _lastPresentationSkipLogTime = now;
        KernelLog.Warning($"[ArisenViewportControl] Skipped viewport output: reason={reason}, ticket={info.Ticket}, " +
            $"frame={info.FrameIndex}, generation={info.ResizeGeneration}, size={info.Width}x{info.Height}, " +
            $"handle=0x{info.SharedHandle.ToInt64():X}, memory={info.MemorySize}, " +
            $"wait=0x{info.WaitSemaphoreHandle.ToInt64():X}, signal=0x{info.SignalSemaphoreHandle.ToInt64():X}");
    }

    private async void RenderFrameAsync(RenderOutputInfo info)
    {
        _isUpdating = true;
        
        try
        {
            // CRITICAL: Await GPU completion before attempting to import or update.
            // This prevents race conditions where Avalonia reads an image Vulkan is still writing.
            await _renderSubsystem!.WaitForRenderTicketAsync(this.Handle.Handle, info.Ticket);

            var interop = _interop;
            var surface = _surface;
            if (!_isInitialized || interop == null || surface == null) return;

            // Handle Resize
            if (_presentationState.ShouldResetImportedImageCache(info))
            {
                ClearImageCache();
                KernelLog.Info($"[ArisenViewportControl] Viewport output resized: generation={info.ResizeGeneration}, size={info.Width}x{info.Height}");
                _presentationState.MarkImportedImageCacheCurrent(info);
            }

            // Get or Import Image
            if (!_imageCache.TryGetValue(info.SharedHandle, out var importedImage))
            {
                var handle = new PlatformHandle(info.SharedHandle, _handleType!);
                var properties = new PlatformGraphicsExternalImageProperties
                {
                    Width = (int)info.Width,
                    Height = (int)info.Height,
                    // Virtual Vulkan editor surfaces are created as VK_FORMAT_R8G8B8A8_UNORM
                    // in RHIVkSurface::InitSwapChain. The Avalonia import format must
                    // match the exported image exactly; importing it as BGRA can make
                    // the compositor reject the image/update and report context loss.
                    Format = PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
                    MemorySize = info.MemorySize
                };

                importedImage = interop.ImportImage(handle, properties);
                lock (_imageCache)
                {
                    _imageCache[info.SharedHandle] = importedImage;
                }
                
                if (!_firstImportLogged)
                {
                    _firstImportLogged = true;
                    KernelLog.Info($"[ArisenViewportControl] First image imported: handle=0x{info.SharedHandle.ToInt64():X}, " +
                        $"type={_handleType}, size={info.Width}x{info.Height}, generation={info.ResizeGeneration}, memory={info.MemorySize}, format={properties.Format}");
                }
            }

            if (!_syncCapabilitiesLogged && _handleType != null)
            {
                _syncCapabilities = interop.GetSynchronizationCapabilities(_handleType);
                _syncCapabilitiesLogged = true;
                KernelLog.Info($"[ArisenViewportControl] Synchronization capabilities for {_handleType}: {_syncCapabilities}; semaphore types: [{string.Join(", ", interop.SupportedSemaphoreTypes)}]");
            }

            if ((_syncCapabilities & CompositionGpuImportedImageSynchronizationCapabilities.Semaphores) != 0)
            {
                var semaphoreType = _semaphoreType;
                if (semaphoreType == null || info.WaitSemaphoreHandle == IntPtr.Zero || info.SignalSemaphoreHandle == IntPtr.Zero)
                {
                    KernelLog.Error($"[ArisenViewportControl] Missing explicit Vulkan semaphore sync for frame {info.FrameIndex}. " +
                        $"wait=0x{info.WaitSemaphoreHandle.ToInt64():X}, signal=0x{info.SignalSemaphoreHandle.ToInt64():X}, type={semaphoreType}");
                    return;
                }

                var waitSemaphore = interop.ImportSemaphore(new PlatformHandle(info.WaitSemaphoreHandle, semaphoreType));
                var signalSemaphore = interop.ImportSemaphore(new PlatformHandle(info.SignalSemaphoreHandle, semaphoreType));
                try
                {
                    await surface.UpdateWithSemaphoresAsync(importedImage, waitSemaphore, signalSemaphore);
                }
                finally
                {
                    DisposeImportedSemaphore(waitSemaphore);
                    DisposeImportedSemaphore(signalSemaphore);
                    ReleaseConsumedSemaphore(info.SignalSemaphoreHandle);
                    info.SignalSemaphoreHandle = IntPtr.Zero;
                }
            }
            else
            {
                // Push to Avalonia Compositor
                await surface.UpdateAsync(importedImage);
            }

            // Mark the ticket as presented only after Avalonia accepted the image. If import/update fails,
            // the next composition tick can retry the same valid engine frame instead of skipping it forever.
            _presentationState.MarkPresented(info);

            // Report consumption back to engine for back-pressure
            var consumptionReported = _renderSubsystem?.ReportConsumedFrameIndex(
                this.Handle.Handle,
                info.FrameIndex) == true;
            var lastConsumedFrameIndex = _renderSubsystem?.GetLastConsumedFrameIndex(
                this.Handle.Handle) ?? 0;
            var visual = _visual;
            if (visual != null)
            {
                var requiresVerticalFlip = string.Equals(
                    _handleType,
                    KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle,
                    StringComparison.Ordinal);
                EditorViewportPresentationDiagnostics.Report(new EditorViewportPresentationObservation(
                    _viewportKind,
                    info.Ticket,
                    info.FrameIndex,
                    info.ResizeGeneration,
                    info.Width,
                    info.Height,
                    lastConsumedFrameIndex,
                    consumptionReported,
                    requiresVerticalFlip,
                    (float)visual.Scale.X,
                    (float)visual.Scale.Y,
                    (float)visual.CenterPoint.X,
                    (float)visual.CenterPoint.Y,
                    (float)visual.Size.X,
                    (float)visual.Size.Y));
            }
        }
        catch (PlatformGraphicsContextLostException)
        {
            KernelLog.Warning("[ArisenViewportControl] Graphics context lost. Triggering recovery...");
            RecoverContext();
        }
        catch (Exception ex)
        {
            KernelLog.Error($"[ArisenViewportControl] Frame update failed: ticket={info.Ticket}, frame={info.FrameIndex}, " +
                $"handle=0x{info.SharedHandle.ToInt64():X}, size={info.Width}x{info.Height}, generation={info.ResizeGeneration}, memory={info.MemorySize}, type={_handleType}, " +
                $"wait=0x{info.WaitSemaphoreHandle.ToInt64():X}, signal=0x{info.SignalSemaphoreHandle.ToInt64():X}, sync={_syncCapabilities}, error={ex.Message}");
        }
        finally
        {
            ReleaseConsumedSemaphore(info.SignalSemaphoreHandle);
            _isUpdating = false;
            QueueNextFrame();
        }
    }

    private void ReleaseConsumedSemaphore(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _renderSubsystem?.ReleaseConsumedSemaphoreHandle(this.Handle.Handle, handle);
        }
    }

    private static void DisposeImportedSemaphore(ICompositionImportedGpuSemaphore semaphore)
    {
        try
        {
            if (semaphore is IAsyncDisposable asyncDisposable)
            {
                _ = asyncDisposable.DisposeAsync().AsTask();
            }
            else if (semaphore is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            KernelLog.Warning($"[ArisenViewportControl] Failed to dispose imported GPU semaphore: {ex.Message}");
        }
    }

    private void QueueNextFrame()
    {
        if (_isInitialized && !_updateQueued && _compositor != null)
        {
            _updateQueued = true;
            _compositor.RequestCompositionUpdate(_updateAction);
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
            foreach (var img in _imageCache.Values) DisposeImportedImage(img);
            _imageCache.Clear();
        }
    }

    private static void DisposeImportedImage(ICompositionImportedGpuImage image)
    {
        try
        {
            if (image is IAsyncDisposable asyncDisposable)
            {
                _ = asyncDisposable.DisposeAsync().AsTask();
            }
            else if (image is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            KernelLog.Warning($"[ArisenViewportControl] Failed to dispose imported GPU image: {ex.Message}");
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _isInitialized)
        {
            RequestSurfaceResize(force: false);
            QueueNextFrame();
        }
    }

    private void RequestSurfaceResize(bool force)
    {
        var renderSubsystem = _renderSubsystem;
        if (!_isInitialized || renderSubsystem == null)
        {
            return;
        }

        var size = GetPhysicalPixelSize();
        if (!force &&
            size.Width == _lastRequestedSurfaceWidth &&
            size.Height == _lastRequestedSurfaceHeight)
        {
            return;
        }

        _lastRequestedSurfaceWidth = size.Width;
        _lastRequestedSurfaceHeight = size.Height;
        renderSubsystem.ResizeSurface(this.Handle.Handle, size.Width, size.Height);
    }

    private PixelSize GetPhysicalPixelSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return new PixelSize((int)Math.Max(1, Bounds.Width * scaling), (int)Math.Max(1, Bounds.Height * scaling));
    }

    private void UpdatePresentationVisualGeometry()
    {
        if (_visual == null)
        {
            return;
        }

        var width = (float)Math.Max(0.0, Bounds.Width);
        var height = (float)Math.Max(0.0, Bounds.Height);
        _visual.Size = new Avalonia.Vector(width, height);
        _visual.CenterPoint = new Vector3(width * 0.5f, height * 0.5f, 0.0f);

        // Avalonia's Vulkan opaque-image import presents external image rows in
        // the opposite vertical direction. Compensate only at that interop boundary;
        // engine clip space and RHI viewports retain their top-left convention.
        _visual.Scale = string.Equals(
            _handleType,
            KnownPlatformGraphicsExternalImageHandleTypes.VulkanOpaqueNtHandle,
            StringComparison.Ordinal)
            ? new Vector3(1.0f, -1.0f, 1.0f)
            : Vector3.One;
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
