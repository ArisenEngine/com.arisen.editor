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
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;
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
public partial class ArisenViewportControl : Control, IGraphicsDeviceLifecycleParticipant
{
    private sealed class CompositorResourceReleaseException : Exception
    {
        public CompositorResourceReleaseException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private CompositionSurfaceVisual? _visual;
    private CompositionDrawingSurface? _surface;
    private Compositor? _compositor;
    private ICompositionGpuInterop? _interop;
    private RenderSubsystem? _renderSubsystem;
    private IGraphicsDeviceLifecycleService? _graphicsDeviceLifecycle;
    private IRuntimeWorldStreamingService? _worldStreaming;
    private EditorViewportSurfaceLease? _surfaceOwnershipLease;
    private RenderSurfaceRegistration _renderSurfaceRegistration;
    private CancellationTokenSource _initializationCancellation = new();
    
    private bool _isAttached;
    private bool _isParticipantRegistered;
    private bool _surfaceRegistered;
    private bool _presentationVisualAttached;
    private bool _startupWorldPresentationSubscriptionActive;
    private bool _graphicsRestartPrepared;
    private bool _isInitialized;
    private bool _isInitializing;
    private bool _isUpdating;
    private bool _isResizing;
    private bool _presentationFailed;
    private bool _resourceReleaseFailed;
    private bool _releaseSurfaceOwnershipOnShutdown;
    private bool _updateQueued;
    private string? _handleType;
    private string? _semaphoreType;
    private CompositionGpuImportedImageSynchronizationCapabilities _syncCapabilities;
    private bool _syncCapabilitiesLogged;
    private bool _firstImportLogged;
    private DateTime _lastPresentationSkipLogTime = DateTime.MinValue;
    private RenderOutputPresentationSkipReason _lastPresentationSkipReason;
    private RenderOutputPresentationState _presentationState;
    private StartupWorldPresentationBarrierState _startupWorldPresentationBarrier;
    private int _lastRequestedSurfaceWidth;
    private int _lastRequestedSurfaceHeight;
    private int _pendingSurfaceWidth;
    private int _pendingSurfaceHeight;
    private int _resizeRequestVersion;
    private int _lifecycleVersion;
    private int _outputReadyDispatchQueued;
    private ulong _preparedGraphicsGeneration;
    private EditorViewportKind _viewportKind = EditorViewportKind.SceneView;
    private Task _initializeTask = Task.CompletedTask;
    private Task _activePresentationTask = Task.CompletedTask;
    private Task _shutdownTask = Task.CompletedTask;
    private Task? _resizeTask;
    
    private readonly Action _updateAction;
    private readonly Action _outputReadyDispatchAction;
    private readonly Dictionary<IntPtr, ICompositionImportedGpuImage> _imageCache = new();
    private readonly Dictionary<IntPtr, ICompositionImportedGpuSemaphore> _semaphoreCache = new();

    public ArisenViewportControl()
    {
        _updateAction = OnCompositionUpdate;
        _outputReadyDispatchAction = DispatchOutputReady;
    }

    public string ParticipantId => $"EditorViewport:{Handle.Handle.ToInt64():X}";

    public int Order => 100;

    public RenderSurfaceRegistration CurrentRenderSurfaceRegistration =>
        _surfaceRegistered ? _renderSurfaceRegistration : default;

    public event Action<RenderSurfaceRegistration>? RenderSurfaceRegistrationChanged;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        RenewInitializationCancellation();
        RegisterGraphicsDeviceLifecycleParticipant();
        BeginInitialize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        CancelInitialization();
        Shutdown(releaseSurfaceOwnership: true);
        base.OnDetachedFromVisualTree(e);
    }

    private void BeginInitialize()
    {
        if (_isInitialized || _isInitializing || !_initializeTask.IsCompleted)
        {
            return;
        }

        RenewInitializationCancellation();
        _initializeTask = InitializeAsync(
            allowDuringGraphicsRestore: false,
            _initializationCancellation.Token);
        ObserveLifecycleTask(_initializeTask, "initialization");
    }

    private async Task InitializeAsync(
        bool allowDuringGraphicsRestore,
        CancellationToken cancellationToken)
    {
        if (_isInitialized || _isInitializing) return;

        if (!_isAttached || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var lifecycleState = _graphicsDeviceLifecycle?.Snapshot.State ??
            GraphicsDeviceLifecycleState.Running;
        if (!allowDuringGraphicsRestore &&
            lifecycleState != GraphicsDeviceLifecycleState.Running)
        {
            return;
        }

        _isInitializing = true;
        int lifecycleVersion = 0;

        try
        {
            await _shutdownTask;
            if (!_isAttached || cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (_resourceReleaseFailed)
            {
                KernelLog.Error(
                    "[ArisenViewportControl] Initialization is blocked because the prior GPU import release failed.");
                throw new InvalidOperationException(
                    "The prior viewport GPU import release failed.");
            }
            lifecycleVersion = ++_lifecycleVersion;
            _presentationFailed = false;
            InitializeStartupWorldPresentationBarrier();

            // 1. Get Compositor and Interop
            var selfVisual = ElementComposition.GetElementVisual(this);
            if (selfVisual == null) return;
            
            _compositor = selfVisual.Compositor;
            _interop = await _compositor.TryGetCompositionGpuInterop();
            if (lifecycleVersion != _lifecycleVersion || VisualRoot == null)
            {
                return;
            }
            
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

            _viewportKind = DataContext is EditorViewportViewModel { IsSceneView: false }
                ? EditorViewportKind.GameView
                : EditorViewportKind.SceneView;
            _renderSubsystem = EngineKernel.Instance.Services.GetService<RenderSubsystem>();
            if (_renderSubsystem != null && _surfaceOwnershipLease == null)
            {
                var ownership = EditorViewportSurfaceOwnership.Shared;
                EditorViewportSurfaceOwnershipSnapshot ownershipSnapshot =
                    ownership.GetSnapshot(_viewportKind);
                if (ownershipSnapshot.IsOwned)
                {
                    KernelLog.Info(
                        $"[ArisenViewportControl] Waiting for {_viewportKind} ownership held by " +
                        $"'{ownershipSnapshot.OwnerId}' generation {ownershipSnapshot.Generation}.");
                }

                EditorViewportSurfaceLease lease = await ownership.AcquireAsync(
                    _viewportKind,
                    ParticipantId,
                    cancellationToken);
                if (!_isAttached ||
                    cancellationToken.IsCancellationRequested ||
                    lifecycleVersion != _lifecycleVersion)
                {
                    lease.Dispose();
                    return;
                }

                lifecycleState = _graphicsDeviceLifecycle?.Snapshot.State ??
                    GraphicsDeviceLifecycleState.Running;
                if (!allowDuringGraphicsRestore &&
                    lifecycleState != GraphicsDeviceLifecycleState.Running)
                {
                    lease.Dispose();
                    return;
                }

                _surfaceOwnershipLease = lease;
                KernelLog.Info(
                    $"[ArisenViewportControl] Acquired {_viewportKind} ownership generation " +
                    $"{lease.Generation} for '{ParticipantId}'.");
            }

            if (_surfaceOwnershipLease != null &&
                _surfaceOwnershipLease.ViewportKind != _viewportKind)
            {
                throw new InvalidOperationException(
                    $"Viewport '{ParticipantId}' owns {_surfaceOwnershipLease.ViewportKind} but resolved as {_viewportKind}.");
            }

            // 2. Setup Composition Visuals after prior logical viewport teardown completes.
            _surface = _compositor.CreateDrawingSurface();
            _visual = _compositor.CreateSurfaceVisual();
            UpdatePresentationVisualGeometry();
            _visual.Surface = _surface;

            if (!_startupWorldPresentationBarrier.IsActive)
            {
                AttachPresentationVisual();
            }

            // 3. Connect to Arisen RenderSubsystem
            if (_renderSubsystem != null)
            {
                var pixelSize = GetPhysicalPixelSize();
                var surfaceType = _viewportKind == EditorViewportKind.SceneView
                    ? SurfaceType.SceneView
                    : SurfaceType.GameView;
                RenderSurfaceRegistration registration = await _renderSubsystem.RegisterSurfaceAsync(
                    this.Handle.Handle,
                    _viewportKind.ToString(),
                    surfaceType,
                    pixelSize.Width,
                    pixelSize.Height);
                if (!registration.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Render surface 0x{Handle.Handle.ToInt64():X} was not registered.");
                }
                SetRenderSurfaceRegistration(registration);
                if (lifecycleVersion != _lifecycleVersion || !_isAttached)
                {
                    if (!await _renderSubsystem.UnregisterSurfaceAsync(registration))
                    {
                        throw new InvalidOperationException(
                            $"Render surface 0x{Handle.Handle.ToInt64():X} could not be removed after initialization was invalidated.");
                    }
                    SetRenderSurfaceRegistration(default);
                    return;
                }
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _isInitialized = false;
            _isResizing = true;
            try
            {
                await ReleaseViewportResourcesAsync(
                    _renderSurfaceRegistration,
                    _renderSubsystem,
                    releaseSurfaceOwnership: true);
                ResetViewportState();
            }
            catch (Exception releaseException)
            {
                _resourceReleaseFailed = true;
                ResetStartupWorldPresentationBarrier();
                throw new AggregateException(
                    "Viewport initialization was cancelled and its partial GPU ownership could not be released.",
                    releaseException);
            }
        }
        catch (Exception ex)
        {
            KernelLog.Error($"[ArisenViewportControl] Initialization failed: {ex.Message}");
            _isInitialized = false;
            _isResizing = true;
            try
            {
                await ReleaseViewportResourcesAsync(
                    _renderSurfaceRegistration,
                    _renderSubsystem,
                    releaseSurfaceOwnership: true);
                ResetViewportState();
            }
            catch (Exception releaseException)
            {
                _resourceReleaseFailed = true;
                ResetStartupWorldPresentationBarrier();
                throw new AggregateException(
                    "Viewport initialization failed and its partial GPU ownership could not be released.",
                    ex,
                    releaseException);
            }
            throw;
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private void Shutdown(bool releaseSurfaceOwnership)
    {
        if (releaseSurfaceOwnership)
        {
            _releaseSurfaceOwnershipOnShutdown = true;
        }

        if (!_shutdownTask.IsCompleted)
        {
            return;
        }

        _releaseSurfaceOwnershipOnShutdown = releaseSurfaceOwnership;
        Task initializeTask = _initializeTask;
        ++_lifecycleVersion;
        _isInitialized = false;
        _isResizing = true;
        _shutdownTask = ShutdownAsync(
            _renderSurfaceRegistration,
            _renderSubsystem,
            initializeTask);
        ObserveLifecycleTask(_shutdownTask, "shutdown");
    }

    private async Task ShutdownAsync(
        RenderSurfaceRegistration registration,
        RenderSubsystem? renderSubsystem,
        Task initializeTask)
    {
        try
        {
            await initializeTask;
        }
        catch
        {
            // Initialization reports its own diagnostic; teardown still owns partial resources.
        }

        try
        {
            await ReleaseViewportResourcesAsync(
                registration,
                renderSubsystem ?? _renderSubsystem,
                releaseSurfaceOwnership: false);
        }
        catch (Exception ex)
        {
            _resourceReleaseFailed = true;
            KernelLog.Error(
                $"[ArisenViewportControl] Failed to release compositor ownership during shutdown: {ex.Message}");
            // GPU ownership remains intact for an explicit retry, but the logical
            // startup barrier must not retain a detached viewport through the
            // world-streaming event source.
            ResetStartupWorldPresentationBarrier();
            return;
        }

        ResetViewportState();

        if (!_isAttached)
        {
            UnregisterGraphicsDeviceLifecycleParticipant();
        }
    }

    private async Task ReleaseViewportResourcesAsync(
        RenderSurfaceRegistration registration,
        RenderSubsystem? renderSubsystem,
        bool releaseSurfaceOwnership)
    {
        if (renderSubsystem != null)
        {
            renderSubsystem.OutputFrameReady -= OnOutputFrameReady;
        }

        await _activePresentationTask;
        var resizeTask = _resizeTask;
        if (resizeTask != null)
        {
            await resizeTask;
        }
        await ClearImportedResourceCacheAsync();

        if (_surfaceRegistered)
        {
            if (!registration.IsValid)
            {
                throw new InvalidOperationException(
                    "Viewport marked a render surface as registered without a valid registration token.");
            }
            if (renderSubsystem == null ||
                !await renderSubsystem.UnregisterSurfaceAsync(registration))
            {
                throw new InvalidOperationException(
                    $"Render surface 0x{registration.Host.ToInt64():X}, generation {registration.Generation} " +
                    "was not removed at the render-thread boundary.");
            }
            SetRenderSurfaceRegistration(default);
        }

        ElementComposition.SetElementChildVisual(this, null);
        _presentationVisualAttached = false;
        _surface?.Dispose();

        if (releaseSurfaceOwnership || _releaseSurfaceOwnershipOnShutdown)
        {
            ReleaseSurfaceOwnership();
        }
    }

    private void ResetViewportState()
    {
        ResetStartupWorldPresentationBarrier();
        _surface = null;
        _visual = null;
        _compositor = null;
        _interop = null;
        _renderSubsystem = null;
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
        _pendingSurfaceWidth = 0;
        _pendingSurfaceHeight = 0;
        _resizeRequestVersion = 0;
        SetRenderSurfaceRegistration(default);
        Interlocked.Exchange(ref _outputReadyDispatchQueued, 0);
        _updateQueued = false;
        _isUpdating = false;
        _isResizing = false;
        _releaseSurfaceOwnershipOnShutdown = false;
        _activePresentationTask = Task.CompletedTask;
        _resizeTask = null;
    }

    private void ResetStartupWorldPresentationBarrier()
    {
        ClearStartupWorldPresentationSubscription();
        _startupWorldPresentationBarrier.Reset();
    }

    private void ClearStartupWorldPresentationSubscription()
    {
        if (_startupWorldPresentationSubscriptionActive && _worldStreaming != null)
        {
            _worldStreaming.WorldPresentationChanged -= OnWorldPresentationChanged;
        }

        _worldStreaming = null;
        _startupWorldPresentationSubscriptionActive = false;
    }

    private void AttachPresentationVisual()
    {
        if (_presentationVisualAttached || _visual == null)
        {
            return;
        }

        ElementComposition.SetElementChildVisual(this, _visual);
        _presentationVisualAttached = true;
    }

    public Task PrepareForGraphicsDeviceRestartAsync(
        GraphicsDeviceRestartContext context,
        CancellationToken cancellationToken)
    {
        return InvokeOnUIThreadAsync(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _graphicsRestartPrepared = false;
                _preparedGraphicsGeneration = context.PreviousGeneration;
                CancelInitialization();
                Shutdown(releaseSurfaceOwnership: false);
                await _shutdownTask;

                if (_resourceReleaseFailed ||
                    _surfaceRegistered ||
                    _imageCache.Count != 0 ||
                    _semaphoreCache.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"Viewport '{ParticipantId}' did not release all generation {context.PreviousGeneration} resources.");
                }

                _graphicsRestartPrepared = true;
            },
            cancellationToken);
    }

    public Task RestoreAfterGraphicsDeviceRestartAsync(
        GraphicsDeviceRestartContext context,
        CancellationToken cancellationToken)
    {
        return InvokeOnUIThreadAsync(
            async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_graphicsRestartPrepared ||
                    _preparedGraphicsGeneration != context.PreviousGeneration)
                {
                    throw new InvalidOperationException(
                        $"Viewport '{ParticipantId}' was not prepared for generation {context.PreviousGeneration}.");
                }
                if (context.CurrentGeneration <= context.PreviousGeneration)
                {
                    throw new InvalidOperationException(
                        "Viewport restoration requires an advanced graphics generation.");
                }

                if (!_isAttached)
                {
                    _graphicsRestartPrepared = false;
                    _preparedGraphicsGeneration = 0;
                    UnregisterGraphicsDeviceLifecycleParticipant();
                    return;
                }

                if (_surfaceOwnershipLease == null)
                {
                    _graphicsRestartPrepared = false;
                    _preparedGraphicsGeneration = 0;
                    return;
                }

                RenewInitializationCancellation();
                _initializeTask = InitializeAsync(
                    allowDuringGraphicsRestore: true,
                    _initializationCancellation.Token);
                await _initializeTask;
                if (!_isInitialized || !_surfaceRegistered)
                {
                    throw new InvalidOperationException(
                        $"Viewport '{ParticipantId}' did not restore its render surface for generation {context.CurrentGeneration}.");
                }

                _graphicsRestartPrepared = false;
                _preparedGraphicsGeneration = 0;
            },
            cancellationToken);
    }

    private void RegisterGraphicsDeviceLifecycleParticipant()
    {
        if (_isParticipantRegistered)
        {
            return;
        }

        if (!EngineKernel.Instance.Services.TryGetService<IGraphicsDeviceLifecycleService>(
                out var lifecycle) ||
            lifecycle == null)
        {
            KernelLog.Warning(
                $"[ArisenViewportControl] Graphics lifecycle service is unavailable for '{ParticipantId}'.");
            return;
        }

        lifecycle.RegisterParticipant(this);
        lifecycle.StateChanged += OnGraphicsDeviceLifecycleStateChanged;
        _graphicsDeviceLifecycle = lifecycle;
        _isParticipantRegistered = true;
    }

    private void UnregisterGraphicsDeviceLifecycleParticipant()
    {
        if (!_isParticipantRegistered || _graphicsDeviceLifecycle == null)
        {
            return;
        }

        _graphicsDeviceLifecycle.StateChanged -= OnGraphicsDeviceLifecycleStateChanged;
        _graphicsDeviceLifecycle.UnregisterParticipant(ParticipantId);
        _graphicsDeviceLifecycle = null;
        _isParticipantRegistered = false;
    }

    private void OnGraphicsDeviceLifecycleStateChanged(
        GraphicsDeviceLifecycleSnapshot snapshot)
    {
        if (snapshot.State != GraphicsDeviceLifecycleState.Running)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (_isAttached &&
                    !_graphicsRestartPrepared &&
                    !_isInitialized &&
                    !_resourceReleaseFailed)
                {
                    RenewInitializationCancellation();
                    BeginInitialize();
                }
            },
            DispatcherPriority.Loaded);
    }

    private static Task InvokeOnUIThreadAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return operation();
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Dispatcher.UIThread.Post(
                async () =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await operation();
                        completion.TrySetResult(true);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                },
                DispatcherPriority.Send);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }

    private static async void ObserveLifecycleTask(Task task, string operation)
    {
        try
        {
            await task;
        }
        catch (Exception exception)
        {
            KernelLog.Error(
                $"[ArisenViewportControl] Viewport {operation} entered a fail-stop state: {exception.Message}");
        }
    }

    private void CancelInitialization()
    {
        if (!_initializationCancellation.IsCancellationRequested)
        {
            _initializationCancellation.Cancel();
        }
    }

    private void RenewInitializationCancellation()
    {
        if (!_initializationCancellation.IsCancellationRequested)
        {
            return;
        }

        _initializationCancellation.Dispose();
        _initializationCancellation = new CancellationTokenSource();
    }

    private void ReleaseSurfaceOwnership()
    {
        EditorViewportSurfaceLease? lease = _surfaceOwnershipLease;
        if (lease == null)
        {
            return;
        }

        lease.Dispose();
        _surfaceOwnershipLease = null;
        KernelLog.Info(
            $"[ArisenViewportControl] Released {lease.ViewportKind} ownership generation " +
            $"{lease.Generation} for '{lease.OwnerId}'.");
    }

    private void SetRenderSurfaceRegistration(RenderSurfaceRegistration registration)
    {
        bool registered = registration.IsValid;
        if (_renderSurfaceRegistration == registration &&
            _surfaceRegistered == registered)
        {
            return;
        }

        _renderSurfaceRegistration = registration;
        _surfaceRegistered = registered;

        Action<RenderSurfaceRegistration>? handlers = RenderSurfaceRegistrationChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<RenderSurfaceRegistration> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(registration);
            }
            catch (Exception exception)
            {
                KernelLog.WarningFormat(
                    "[ArisenViewportControl] Surface-registration observer failed. Host=0x{0:X}, Generation={1}, Observer={2}.{3}, Error={4}: {5}",
                    registration.Host.ToInt64(),
                    registration.Generation,
                    handler.Method.DeclaringType?.FullName ?? "<unknown>",
                    handler.Method.Name,
                    exception.GetType().Name,
                    exception.Message);
            }
        }
    }

    private void OnOutputFrameReady(RenderSurfaceRegistration registration)
    {
        if (registration != _renderSurfaceRegistration ||
            !Volatile.Read(ref _isInitialized) ||
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
        if (!_isInitialized || _isUpdating || _isResizing || _presentationFailed) return;

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

        if (_startupWorldPresentationBarrier.IsActive &&
            (!_startupWorldPresentationBarrier.HasActivationBoundary ||
             !IsStartupWorldActivationCurrent()))
        {
            return;
        }

        RenderSurfaceRegistration registration = _renderSurfaceRegistration;
        if (!registration.IsValid ||
            !_renderSubsystem.GetOutputInfo(registration, out var info))
        {
            QueueNextFrame();
            return;
        }

        StartupWorldPresentationBarrierDecision startupBarrierDecision =
            _startupWorldPresentationBarrier.Evaluate(info.Ticket);
        if (startupBarrierDecision is
            StartupWorldPresentationBarrierDecision.WaitForActivation or
            StartupWorldPresentationBarrierDecision.WaitForOutput)
        {
            ReleaseConsumedSemaphore(registration, info.SignalSemaphoreHandle);
            QueueNextFrame();
            return;
        }

        if (startupBarrierDecision ==
            StartupWorldPresentationBarrierDecision.DiscardOutput)
        {
            _activePresentationTask = RenderFrameAsync(
                registration,
                info,
                startupBarrierDecision);
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
                ReleaseConsumedSemaphore(registration, info.SignalSemaphoreHandle);
            }

            QueueNextFrame();
            return;
        }

        _activePresentationTask = RenderFrameAsync(
            registration,
            info,
            startupBarrierDecision);
    }

    private void InitializeStartupWorldPresentationBarrier()
    {
        if (_startupWorldPresentationSubscriptionActive)
        {
            return;
        }

        var services = EngineKernel.Instance.Services;
        ProjectSubsystem? projectSubsystem = services.GetService<ProjectSubsystem>();
        if (projectSubsystem?.ActiveProject?.StartupWorld is not { IsValid: true } startupWorld)
        {
            return;
        }

        _worldStreaming = services.GetService<IRuntimeWorldStreamingService>();
        _worldStreaming.WorldPresentationChanged += OnWorldPresentationChanged;
        _startupWorldPresentationSubscriptionActive = true;
        RuntimeWorldPresentationSnapshot presentation =
            _worldStreaming.PresentationSnapshot;
        var configuredStartup = new StartupWorldPresentationTarget(
            startupWorld.Guid,
            startupWorld.PackageId);
        StartupWorldPresentationObservation observation =
            ToPresentationObservation(presentation);
        if (!_startupWorldPresentationBarrier.TryBegin(
                configuredStartup,
                observation))
        {
            ResetStartupWorldPresentationBarrier();
        }
    }

    private void OnWorldPresentationChanged(RuntimeWorldPresentationSnapshot snapshot)
    {
        int lifecycleVersion = _lifecycleVersion;
        ulong activationOutputTicket = GetCurrentOutputTicket();
        Dispatcher.UIThread.Post(
            () => ReconcileStartupWorldPresentationBarrier(
                lifecycleVersion,
                snapshot.Revision,
                activationOutputTicket),
            DispatcherPriority.Render);
    }

    private ulong GetCurrentOutputTicket()
    {
        RenderSubsystem? renderSubsystem = _renderSubsystem;
        RenderSurfaceRegistration registration = _renderSurfaceRegistration;
        return renderSubsystem != null && registration.IsValid
            ? renderSubsystem.GetLastRenderTicket(registration)
            : 0;
    }

    private void ReconcileStartupWorldPresentationBarrier(
        int lifecycleVersion,
        long notificationRevision,
        ulong activationOutputTicket)
    {
        if (lifecycleVersion != _lifecycleVersion ||
            !_isAttached ||
            !_startupWorldPresentationSubscriptionActive ||
            !_startupWorldPresentationBarrier.IsActive ||
            _worldStreaming == null)
        {
            return;
        }

        IRuntimeWorldStreamingService streaming = _worldStreaming;
        RuntimeWorldPresentationSnapshot presentation = streaming.PresentationSnapshot;
        StartupWorldPresentationReconcileDecision decision =
            _startupWorldPresentationBarrier.Reconcile(
                notificationRevision,
                ToPresentationObservation(presentation),
                activationOutputTicket);
        if (decision ==
            StartupWorldPresentationReconcileDecision.ActivationBoundaryCaptured)
        {
            QueueNextFrame();
            return;
        }

        if (decision is
            StartupWorldPresentationReconcileDecision.None or
            StartupWorldPresentationReconcileDecision.StaleNotification or
            StartupWorldPresentationReconcileDecision.WaitForActivation)
        {
            return;
        }

        ResetStartupWorldPresentationBarrier();
        AttachPresentationVisual();
        QueueNextFrame();
    }

    private bool IsStartupWorldActivationCurrent()
    {
        IRuntimeWorldStreamingService? streaming = _worldStreaming;
        if (streaming == null)
        {
            return false;
        }

        RuntimeWorldPresentationSnapshot presentation = streaming.PresentationSnapshot;
        return _startupWorldPresentationBarrier.IsCurrentActivation(
            ToPresentationObservation(presentation));
    }

    private static StartupWorldPresentationObservation ToPresentationObservation(
        in RuntimeWorldPresentationSnapshot snapshot) => new(
        snapshot.Revision,
        ToPresentationTarget(snapshot.ActiveWorldAsset),
        snapshot.ActiveWorldGuid,
        ToPresentationTarget(snapshot.PendingWorldAsset));

    private static StartupWorldPresentationTarget? ToPresentationTarget(
        AssetRef<WorldSourceAsset>? world) =>
        world is { IsValid: true } value
            ? ToPresentationTarget(value)
            : null;

    private static StartupWorldPresentationTarget ToPresentationTarget(
        AssetRef<WorldSourceAsset> world) => new(world.Guid, world.PackageId);

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

    private async Task RenderFrameAsync(
        RenderSurfaceRegistration registration,
        RenderOutputInfo info,
        StartupWorldPresentationBarrierDecision startupBarrierDecision)
    {
        _isUpdating = true;
        
        try
        {
            // CRITICAL: Await GPU completion before attempting to import or update.
            // This prevents race conditions where Avalonia reads an image Vulkan is still writing.
            await _renderSubsystem!.WaitForRenderTicketAsync(registration, info.Ticket);

            var interop = _interop;
            var surface = _surface;
            if (!_isInitialized || interop == null || surface == null) return;

            // Handle Resize
            if (_presentationState.ShouldResetImportedImageCache(info))
            {
                await ClearImportedResourceCacheAsync();
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

                var waitSemaphore = GetOrImportSemaphore(
                    interop,
                    info.WaitSemaphoreHandle,
                    semaphoreType);
                var signalSemaphore = GetOrImportSemaphore(
                    interop,
                    info.SignalSemaphoreHandle,
                    semaphoreType);
                await surface.UpdateWithSemaphoresAsync(importedImage, waitSemaphore, signalSemaphore);

                CompleteConsumedSemaphore(registration, info.SignalSemaphoreHandle);
                info.SignalSemaphoreHandle = IntPtr.Zero;
            }
            else
            {
                // Push to Avalonia Compositor
                await surface.UpdateAsync(importedImage);
            }

            // Report consumption back to engine for back-pressure
            var consumptionReported = _renderSubsystem?.ReportConsumedFrameIndex(
                registration,
                info.FrameIndex) == true;
            var lastConsumedFrameIndex = _renderSubsystem?.GetLastConsumedFrameIndex(
                registration) ?? 0;

            if (startupBarrierDecision ==
                StartupWorldPresentationBarrierDecision.DiscardOutput)
            {
                return;
            }

            // Mark the ticket as presented only after Avalonia accepted the image. If import/update fails,
            // the next composition tick can retry the same valid engine frame instead of skipping it forever.
            _presentationState.MarkPresented(info);

            if (startupBarrierDecision ==
                    StartupWorldPresentationBarrierDecision.PresentAndRelease &&
                _startupWorldPresentationBarrier.IsActive &&
                _startupWorldPresentationBarrier.Evaluate(info.Ticket) ==
                    StartupWorldPresentationBarrierDecision.PresentAndRelease &&
                IsStartupWorldActivationCurrent())
            {
                _startupWorldPresentationBarrier.CompleteAfterPresented(info.Ticket);
                ClearStartupWorldPresentationSubscription();
                AttachPresentationVisual();
            }

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
                    (float)visual.Size.Y,
                    _surfaceOwnershipLease?.Generation ?? 0,
                    _surfaceOwnershipLease?.OwnerId ?? string.Empty,
                    _imageCache.Count,
                    _semaphoreCache.Count));
            }
        }
        catch (CompositorResourceReleaseException ex)
        {
            _resourceReleaseFailed = true;
            EnterPresentationFailureState(ex);
        }
        catch (PlatformGraphicsContextLostException ex)
        {
            EnterPresentationFailureState(ex);
        }
        catch (Exception ex)
        {
            KernelLog.Error($"[ArisenViewportControl] Frame update failed: ticket={info.Ticket}, frame={info.FrameIndex}, " +
                $"handle=0x{info.SharedHandle.ToInt64():X}, size={info.Width}x{info.Height}, generation={info.ResizeGeneration}, memory={info.MemorySize}, type={_handleType}, " +
                $"wait=0x{info.WaitSemaphoreHandle.ToInt64():X}, signal=0x{info.SignalSemaphoreHandle.ToInt64():X}, sync={_syncCapabilities}, error={ex.Message}");
        }
        finally
        {
            ReleaseConsumedSemaphore(registration, info.SignalSemaphoreHandle);
            _isUpdating = false;
            QueueNextFrame();
        }
    }

    private void ReleaseConsumedSemaphore(
        RenderSurfaceRegistration registration,
        IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _renderSubsystem?.ReleaseConsumedSemaphoreHandle(registration, handle);
        }
    }

    private void CompleteConsumedSemaphore(
        RenderSurfaceRegistration registration,
        IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            _renderSubsystem?.CompleteConsumedSemaphoreHandle(registration, handle);
        }
    }

    private static async Task DisposeImportedSemaphoreAsync(ICompositionImportedGpuSemaphore semaphore)
    {
        await semaphore.ImportCompleted;
        if (semaphore is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (semaphore is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    private ICompositionImportedGpuSemaphore GetOrImportSemaphore(
        ICompositionGpuInterop interop,
        IntPtr handle,
        string semaphoreType)
    {
        if (_semaphoreCache.TryGetValue(handle, out var semaphore))
        {
            return semaphore;
        }

        semaphore = interop.ImportSemaphore(new PlatformHandle(handle, semaphoreType));
        _semaphoreCache.Add(handle, semaphore);
        return semaphore;
    }

    private void QueueNextFrame()
    {
        if (_isInitialized && !_isResizing && !_presentationFailed &&
            !_updateQueued && _compositor != null)
        {
            _updateQueued = true;
            _compositor.RequestCompositionUpdate(_updateAction);
        }
    }

    private void EnterPresentationFailureState(Exception exception)
    {
        if (_presentationFailed)
        {
            return;
        }

        _presentationFailed = true;
        ++_lifecycleVersion;
        _isInitialized = false;
        _isResizing = true;
        _updateQueued = false;
        var renderSubsystem = _renderSubsystem;
        RenderSurfaceRegistration registration = _renderSurfaceRegistration;
        if (renderSubsystem != null)
        {
            renderSubsystem.OutputFrameReady -= OnOutputFrameReady;
        }

        KernelLog.Error(
            $"[ArisenViewportControl] GPU presentation failed; the viewport is entering a fail-stop state. " +
            $"Resources will not be recreated in a timed loop. ErrorType={exception.GetType().Name}, " +
            $"Error={exception.Message}");
        if (_shutdownTask.IsCompleted)
        {
            _shutdownTask = ReleaseFailedPresentationAsync(registration, renderSubsystem);
        }
    }

    private async Task ReleaseFailedPresentationAsync(
        RenderSurfaceRegistration registration,
        RenderSubsystem? renderSubsystem)
    {
        // Defer until the presentation/resize callback that detected the failure has returned.
        await Task.Yield();
        await ShutdownAsync(registration, renderSubsystem, _initializeTask);
    }

    private async Task ClearImportedResourceCacheAsync()
    {
        List<KeyValuePair<IntPtr, ICompositionImportedGpuImage>> images;
        List<KeyValuePair<IntPtr, ICompositionImportedGpuSemaphore>> semaphores;
        lock (_imageCache)
        {
            images = new List<KeyValuePair<IntPtr, ICompositionImportedGpuImage>>(_imageCache);
        }
        lock (_semaphoreCache)
        {
            semaphores = new List<KeyValuePair<IntPtr, ICompositionImportedGpuSemaphore>>(_semaphoreCache);
        }

        try
        {
            foreach (var (handle, image) in images)
            {
                await DisposeImportedImageAsync(image);
                lock (_imageCache)
                {
                    if (_imageCache.TryGetValue(handle, out var cached) &&
                        ReferenceEquals(cached, image))
                    {
                        _imageCache.Remove(handle);
                    }
                }
            }

            foreach (var (handle, semaphore) in semaphores)
            {
                await DisposeImportedSemaphoreAsync(semaphore);
                lock (_semaphoreCache)
                {
                    if (_semaphoreCache.TryGetValue(handle, out var cached) &&
                        ReferenceEquals(cached, semaphore))
                    {
                        _semaphoreCache.Remove(handle);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new CompositorResourceReleaseException(
                "Failed to release imported compositor resources.",
                ex);
        }
    }

    private static async Task DisposeImportedImageAsync(ICompositionImportedGpuImage image)
    {
        await image.ImportCompleted;
        if (image is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (image is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty && _isInitialized)
        {
            RequestSurfaceResize(force: false);
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
        if (size.Width == _pendingSurfaceWidth &&
            size.Height == _pendingSurfaceHeight &&
            _resizeTask is { IsCompleted: false })
        {
            return;
        }
        if (!force && size.Width == _lastRequestedSurfaceWidth &&
            size.Height == _lastRequestedSurfaceHeight)
        {
            return;
        }

        _lastRequestedSurfaceWidth = size.Width;
        _lastRequestedSurfaceHeight = size.Height;
        _pendingSurfaceWidth = size.Width;
        _pendingSurfaceHeight = size.Height;
        _resizeRequestVersion++;
        if (_resizeTask is not { IsCompleted: false })
        {
            _resizeTask = ProcessSurfaceResizesAsync(
                renderSubsystem,
                _renderSurfaceRegistration);
        }
    }

    private async Task ProcessSurfaceResizesAsync(
        RenderSubsystem renderSubsystem,
        RenderSurfaceRegistration registration)
    {
        _isResizing = true;
        try
        {
            while (_isInitialized && !_presentationFailed)
            {
                await _activePresentationTask;
                if (!_isInitialized || _presentationFailed)
                {
                    return;
                }

                await ClearImportedResourceCacheAsync();
                if (!_isInitialized || _presentationFailed)
                {
                    return;
                }

                int requestVersion = _resizeRequestVersion;
                int width = _pendingSurfaceWidth;
                int height = _pendingSurfaceHeight;
                if (!await renderSubsystem.ResizeSurfaceAsync(registration, width, height))
                {
                    throw new InvalidOperationException(
                        $"Render surface 0x{registration.Host.ToInt64():X}, generation {registration.Generation} " +
                        $"disappeared before resize {width}x{height}.");
                }
                if (!_isInitialized || _presentationFailed)
                {
                    return;
                }

                _presentationState.Reset();
                if (requestVersion == _resizeRequestVersion)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            EnterPresentationFailureState(new InvalidOperationException(
                "Viewport resize ownership transition failed.",
                ex));
        }
        finally
        {
            _isResizing = false;
            QueueNextFrame();
        }
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
