using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArisenEditor.Core.Factory;
using ArisenEditor.Core.Services;
using ArisenEditor.Views;
using ArisenEditor.ViewModels;
using ArisenEditorFramework.Core;
using ArisenEditorFramework.Extensions;
using ArisenEngine.Rendering;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ArisenEditor.Core.Validation;

internal readonly record struct EditorViewportSmokeOptions(
    string Profile,
    string OutputPath,
    int TimeoutSeconds,
    bool RestartRenderDoc,
    bool CaptureRenderDoc)
{
    private const int DefaultTimeoutSeconds = 90;
    private const int MinimumTimeoutSeconds = 5;
    private const int MaximumTimeoutSeconds = 120;

    public static bool IsRequested(string[] args)
    {
        return Array.Exists(
            args,
            argument => string.Equals(
                argument,
                "--editor-viewport-smoke",
                StringComparison.OrdinalIgnoreCase));
    }

    public static EditorViewportSmokeOptions Parse(string[] args, string workspacePath)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        var profile = "Editor";
        var timeoutSeconds = DefaultTimeoutSeconds;
        var restartRenderDoc = false;
        var captureRenderDoc = false;
        for (var index = 0; index < args.Length; index++)
        {
            if (string.Equals(args[index], "--profile", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length)
            {
                profile = args[++index];
            }
            else if (string.Equals(
                         args[index],
                         "--editor-viewport-smoke-timeout",
                         StringComparison.OrdinalIgnoreCase) &&
                     index + 1 < args.Length)
            {
                if (!int.TryParse(args[++index], out timeoutSeconds) ||
                    timeoutSeconds < MinimumTimeoutSeconds ||
                    timeoutSeconds > MaximumTimeoutSeconds)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(args),
                        $"Editor viewport smoke timeout must be between {MinimumTimeoutSeconds} and " +
                        $"{MaximumTimeoutSeconds} seconds.");
                }
            }
            else if (string.Equals(
                         args[index],
                         "--editor-viewport-smoke-restart-renderdoc",
                         StringComparison.OrdinalIgnoreCase))
            {
                restartRenderDoc = true;
            }
            else if (string.Equals(
                         args[index],
                         "--editor-viewport-smoke-capture-renderdoc",
                         StringComparison.OrdinalIgnoreCase))
            {
                captureRenderDoc = true;
            }
        }

        if (captureRenderDoc && !restartRenderDoc)
        {
            throw new ArgumentException(
                "Editor viewport RenderDoc capture requires --editor-viewport-smoke-restart-renderdoc.",
                nameof(args));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        var safeProfile = profile;
        foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
        {
            safeProfile = safeProfile.Replace(invalidCharacter, '_');
        }

        var outputPath = Path.GetFullPath(Path.Combine(
            workspacePath,
            ".arisen",
            "Logs",
            $"editor-viewport-summary-{safeProfile}-latest.json"));
        return new EditorViewportSmokeOptions(
            profile,
            outputPath,
            timeoutSeconds,
            restartRenderDoc,
            captureRenderDoc);
    }
}

internal sealed class EditorViewportSmokeSession : IDisposable
{
    private const string TerrainBrushPanelId = "TerrainBrush";
    private static readonly (int Width, int Height)[] s_SceneResizeDeltas =
    {
        (64, 36),
        (96, 54),
        (-80, -45),
        (80, 45)
    };

    private readonly IClassicDesktopStyleApplicationLifetime m_Desktop;
    private readonly EditorViewportSmokeOptions m_Options;
    private readonly EditorViewportSmokeState m_State;
    private readonly CancellationTokenSource m_TimeoutCancellation = new();
    private readonly SceneView m_SceneView;
    private EditorViewportView m_GameView;
    private readonly Grid m_ViewportLayout;
    private readonly ContentControl m_TerrainBrushHost;
    private readonly Control? m_TerrainBrushView;
    private readonly Window m_Window;
    private IEditorWorldDocumentService? m_WorldDocuments;
    private WorldPartitionViewModel? m_WorldPartitionViewModel;
    private WorldCellId m_WorldCellId;
    private bool m_WorldValidationStarted;
    private bool m_WorldCellActive;
    private bool m_WorldUnloadRequested;
    private bool m_ConcurrentViewportsRequested;
    private bool m_ConcurrentViewportsShown;
    private bool m_ConcurrentPresentationFinished;
    private bool m_TerrainPaintActivated;
    private bool m_GameViewportReplacementRequested;
    private bool m_GameViewportReplacementPresented;
    private long m_MinimumReplacementGameOwnershipGeneration;
    private int m_NextSceneResizeStep;
    private int m_GraphicsRestartActive;
    private int m_RenderDocRestartStarted;
    private int m_Finished;
    private bool m_Disposed;

    public EditorViewportSmokeSession(
        IClassicDesktopStyleApplicationLifetime desktop,
        EditorViewportSmokeOptions options,
        ArisenPanelFactory panelFactory,
        IReadOnlyList<EditorPanelRegistration> extensionPanels)
    {
        m_Desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        m_Options = options;
        var renderDocOptIn = Environment.GetEnvironmentVariable("ARISEN_ENABLE_RENDERDOC");
        m_State = new EditorViewportSmokeState(
            string.Equals(renderDocOptIn, "1", StringComparison.Ordinal) ||
            string.Equals(renderDocOptIn, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(renderDocOptIn, "yes", StringComparison.OrdinalIgnoreCase),
            options.RestartRenderDoc,
            options.CaptureRenderDoc);
        ArgumentNullException.ThrowIfNull(panelFactory);
        ArgumentNullException.ThrowIfNull(extensionPanels);

        IEditorPanel scenePanel = panelFactory.CreatePanel("Scene");
        m_SceneView = scenePanel.Content as SceneView ??
            throw new InvalidOperationException(
                "The Editor Scene panel did not create a SceneView control.");
        m_GameView = new EditorViewportView
        {
            DataContext = new EditorViewportViewModel(isSceneView: false),
            IsVisible = false
        };

        EditorPanelRegistration? terrainBrushRegistration = extensionPanels.FirstOrDefault(
            panel => string.Equals(panel.Id, TerrainBrushPanelId, StringComparison.Ordinal));
        if (terrainBrushRegistration != null)
        {
            IEditorPanel terrainBrushPanel = panelFactory.CreatePanel(terrainBrushRegistration.Id);
            m_TerrainBrushView = terrainBrushPanel.Content as Control ??
                throw new InvalidOperationException(
                    "The Terrain Brush panel did not create an Avalonia control.");
        }
        m_State.NotifyTerrainPaintAvailability(m_TerrainBrushView != null);

        m_TerrainBrushHost = new ContentControl
        {
            Content = m_TerrainBrushView,
            IsVisible = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        m_ViewportLayout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,0,0")
        };
        Grid.SetColumn(m_SceneView, 0);
        Grid.SetColumn(m_GameView, 1);
        Grid.SetColumn(m_TerrainBrushHost, 2);
        m_ViewportLayout.Children.Add(m_SceneView);
        m_ViewportLayout.Children.Add(m_GameView);
        m_ViewportLayout.Children.Add(m_TerrainBrushHost);

        m_Window = new Window
        {
            Title = "Arisen Editor",
            Width = 800,
            Height = 500,
            MinWidth = 640,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = m_ViewportLayout
        };
    }

    public void Start(Action startEngine)
    {
        ArgumentNullException.ThrowIfNull(startEngine);
        ThrowIfDisposed();

        EditorViewportPresentationDiagnostics.Presented += OnPresented;
        m_Window.Closed += OnWindowClosed;
        m_Desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        m_Desktop.MainWindow = m_Window;
        m_Window.Show();
        startEngine();

        m_State.ObserveRenderDocAvailability(RenderDocService.Instance.IsAvailable);
        if (m_State.IsComplete)
        {
            Complete(m_State.FailureMessage);
            return;
        }

        _ = WatchTimeoutAsync(m_TimeoutCancellation.Token);

        KernelLog.InfoFormat(
            "[EditorViewportSmoke] Started. Timeout={0}s, RestartRenderDoc={1}, CaptureRenderDoc={2}, Output={3}",
            m_Options.TimeoutSeconds,
            m_Options.RestartRenderDoc,
            m_Options.CaptureRenderDoc,
            m_Options.OutputPath);
    }

    public void ReportFailure(string message)
    {
        Complete(message);
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        m_Disposed = true;
        m_TimeoutCancellation.Cancel();
        m_TimeoutCancellation.Dispose();
        EditorViewportPresentationDiagnostics.Presented -= OnPresented;
        m_Window.Closed -= OnWindowClosed;
        DetachWorldDocuments();
        m_WorldPartitionViewModel?.Dispose();
        m_WorldPartitionViewModel = null;
    }

    private void OnPresented(EditorViewportPresentationObservation observation)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnPresented(observation), DispatcherPriority.Render);
            return;
        }

        if (Volatile.Read(ref m_Finished) != 0)
        {
            return;
        }
        if (Volatile.Read(ref m_GraphicsRestartActive) != 0)
        {
            return;
        }

        try
        {
            if (m_GameViewportReplacementRequested &&
                observation.ViewportKind == EditorViewportKind.GameView &&
                !m_GameViewportReplacementPresented)
            {
                if (observation.SurfaceOwnershipGeneration <
                    m_MinimumReplacementGameOwnershipGeneration)
                {
                    return;
                }

                m_GameViewportReplacementPresented = true;
                KernelLog.InfoFormat(
                    "[EditorViewportSmoke] Replacement GameView presented with ownership generation {0} ({1}).",
                    observation.SurfaceOwnershipGeneration,
                    observation.SurfaceOwnershipOwnerId);
            }

            var action = m_State.Observe(observation);
            switch (action)
            {
                case EditorViewportSmokeAction.ResizeSceneView:
                    BeginWorldPartitionValidation();
                    RequestNextSceneResize(observation);
                    break;

                case EditorViewportSmokeAction.ShowGameView:
                    m_ConcurrentViewportsRequested = true;
                    TryShowConcurrentViewports();
                    break;

                case EditorViewportSmokeAction.RestartRenderDoc:
                    if (Interlocked.CompareExchange(
                            ref m_RenderDocRestartStarted,
                            1,
                            0) != 0)
                    {
                        Complete("Editor viewport smoke requested RenderDoc restart more than once.");
                        break;
                    }
                    Interlocked.Exchange(ref m_GraphicsRestartActive, 1);
                    _ = RestartWithRenderDocAsync();
                    break;

                case EditorViewportSmokeAction.FinishConcurrentPresentation:
                    m_ConcurrentPresentationFinished = true;
                    TryRequestWorldCellUnload();
                    break;

                case EditorViewportSmokeAction.Complete:
                    if (m_GameViewportReplacementRequested &&
                        !m_GameViewportReplacementPresented)
                    {
                        Complete(
                            "The replacement GameView never acquired logical surface ownership and presented.");
                        break;
                    }
                    if (!m_SceneView.HasWorldPartitionOverlayVisual)
                    {
                        Complete(
                            "SceneView removed its world-partition overlay controls during GameView activation.");
                        break;
                    }
                    Complete(null);
                    break;

                case EditorViewportSmokeAction.Failed:
                    Complete(m_State.FailureMessage);
                    break;
            }
        }
        catch (Exception ex)
        {
            Complete($"Editor viewport observation failed: {ex.Message}");
        }
    }

    private async Task RestartWithRenderDocAsync()
    {
        try
        {
            var services = EngineKernel.Instance.Services;
            var backend = services.GetService<IRHIBackend>();
            var lifecycle = services.GetService<IGraphicsDeviceLifecycleService>();
            ulong previousGeneration = backend.Generation;
            m_State.NotifyRenderDocRestartRequested(previousGeneration);
            if (m_State.IsComplete && !m_State.Succeeded)
            {
                Complete(m_State.FailureMessage);
                return;
            }

            KernelLog.InfoFormat(
                "[EditorViewportSmoke] Requesting in-process RenderDoc graphics restart from generation {0}.",
                previousGeneration);
            GraphicsDeviceRestartResult result = await lifecycle.RestartAsync(
                new RHIBackendRestartOptions(RHIBackendDiagnosticMode.RenderDoc),
                m_TimeoutCancellation.Token);
            bool renderDocAvailable = RenderDocService.Instance.IsAvailable;
            m_State.ObserveRenderDocRestartCompleted(
                result.Succeeded,
                result.PreviousGeneration,
                result.CurrentGeneration,
                renderDocAvailable,
                result.Diagnostic);
            if (m_State.IsComplete && !m_State.Succeeded)
            {
                Complete(m_State.FailureMessage);
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(
                ReplaceGameViewportForOwnershipValidation,
                DispatcherPriority.Send);

            if (m_Options.CaptureRenderDoc)
            {
                await CaptureRenderDocFrameAsync();
                if (m_State.IsComplete && !m_State.Succeeded)
                {
                    Complete(m_State.FailureMessage);
                    return;
                }
            }

            KernelLog.InfoFormat(
                "[EditorViewportSmoke] RenderDoc graphics restart completed. PreviousGeneration={0}, CurrentGeneration={1}, RenderDocAvailable={2}. Resuming dual-viewport presentation.",
                result.PreviousGeneration,
                result.CurrentGeneration,
                renderDocAvailable);
        }
        catch (OperationCanceledException) when (m_TimeoutCancellation.IsCancellationRequested)
        {
            Complete("Editor viewport RenderDoc restart was cancelled by the smoke timeout.");
        }
        catch (Exception exception)
        {
            Complete($"Editor viewport RenderDoc restart failed: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref m_GraphicsRestartActive, 0);
        }
    }

    private async Task CaptureRenderDocFrameAsync()
    {
        RenderSurfaceRegistration registration = await Dispatcher.UIThread.InvokeAsync(
            () => m_SceneView.CurrentRenderSurfaceRegistration,
            DispatcherPriority.Send);
        if (!registration.IsValid)
        {
            throw new InvalidOperationException(
                "The restored SceneView has no valid render-surface registration for RenderDoc capture.");
        }

        RenderDocService renderDoc = RenderDocService.Instance;
        var completion = new TaskCompletionSource<RenderDocCaptureRequestSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        ulong requestId = 0;
        void OnCaptureStateChanged(RenderDocCaptureRequestSnapshot snapshot)
        {
            if (snapshot.Target == registration &&
                snapshot.IsTerminal &&
                (requestId == 0 || snapshot.RequestId == requestId))
            {
                completion.TrySetResult(snapshot);
            }
        }

        renderDoc.CaptureStateChanged += OnCaptureStateChanged;
        try
        {
            if (!renderDoc.TryTriggerCapture(registration))
            {
                RenderDocCaptureRequestSnapshot rejected = renderDoc.CaptureRequest;
                throw new InvalidOperationException(
                    $"RenderDoc rejected the SceneView capture request: {rejected.Status} {rejected.Diagnostic}");
            }

            requestId = renderDoc.CaptureRequest.RequestId;
            m_State.NotifyRenderDocCaptureRequested(requestId);
            if (m_State.IsComplete && !m_State.Succeeded)
            {
                return;
            }

            RenderDocCaptureRequestSnapshot terminal = await completion.Task.WaitAsync(
                m_TimeoutCancellation.Token);
            bool succeeded = terminal.Status == RenderDocCaptureRequestStatus.Succeeded;
            m_State.ObserveRenderDocCaptureCompleted(
                terminal.RequestId,
                succeeded,
                terminal.Diagnostic,
                terminal.CapturePath);
            if (!succeeded)
            {
                throw new InvalidOperationException(
                    $"RenderDoc capture {terminal.RequestId} failed at {terminal.FailureStage}: {terminal.Diagnostic}");
            }

            KernelLog.InfoFormat(
                "[EditorViewportSmoke] RenderDoc capture {0} completed for SceneView host 0x{1:X}, generation {2}. Artifact='{3}'.",
                terminal.RequestId,
                terminal.Target.Host.ToInt64(),
                terminal.Target.Generation,
                terminal.CapturePath);
        }
        finally
        {
            renderDoc.CaptureStateChanged -= OnCaptureStateChanged;
        }
    }

    private void ReplaceGameViewportForOwnershipValidation()
    {
        if (m_GameViewportReplacementRequested || Volatile.Read(ref m_Finished) != 0)
        {
            return;
        }

        EditorViewportSurfaceOwnershipSnapshot currentOwnership =
            EditorViewportSurfaceOwnership.Shared.GetSnapshot(
                EditorViewportKind.GameView);
        if (!currentOwnership.IsOwned)
        {
            Complete(
                "The active GameView had no logical surface ownership before replacement.");
            return;
        }

        m_GameViewportReplacementRequested = true;
        m_MinimumReplacementGameOwnershipGeneration = checked(
            currentOwnership.Generation + 1);

        EditorViewportView previous = m_GameView;
        var replacement = new EditorViewportView
        {
            DataContext = new EditorViewportViewModel(isSceneView: false),
            IsVisible = true
        };
        Grid.SetColumn(replacement, 1);
        m_ViewportLayout.Children.Add(replacement);
        m_ViewportLayout.UpdateLayout();
        m_ViewportLayout.Children.Remove(previous);
        if (previous.DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
        m_GameView = replacement;
        m_ViewportLayout.UpdateLayout();

        KernelLog.InfoFormat(
            "[EditorViewportSmoke] Replaced live GameView after RenderDoc restart. PriorOwner={0}, PriorGeneration={1}, RequiredGeneration={2}.",
            currentOwnership.OwnerId,
            currentOwnership.Generation,
            m_MinimumReplacementGameOwnershipGeneration);
    }

    private void RequestNextSceneResize(in EditorViewportPresentationObservation observation)
    {
        if (s_SceneResizeDeltas.Length != EditorViewportSmokeState.RequiredSceneResizeTransitions)
        {
            Complete(
                $"Editor viewport smoke defines {s_SceneResizeDeltas.Length} resize steps, " +
                $"expected {EditorViewportSmokeState.RequiredSceneResizeTransitions}.");
            return;
        }

        if ((uint)m_NextSceneResizeStep >= (uint)s_SceneResizeDeltas.Length)
        {
            Complete("Editor viewport smoke exhausted its resize steps before the state contract completed.");
            return;
        }

        var delta = s_SceneResizeDeltas[m_NextSceneResizeStep];
        float targetVisualWidth = observation.VisualWidth + delta.Width;
        float targetVisualHeight = observation.VisualHeight + delta.Height;
        double targetWindowWidth = m_Window.Width + delta.Width;
        double targetWindowHeight = m_Window.Height + delta.Height;
        double renderScaling = TopLevel.GetTopLevel(m_SceneView)?.RenderScaling ?? 1.0;
        m_State.NotifySceneResizeRequested(
            checked((uint)Math.Max(1, (int)(targetVisualWidth * renderScaling))),
            checked((uint)Math.Max(1, (int)(targetVisualHeight * renderScaling))),
            targetVisualWidth,
            targetVisualHeight);
        if (m_State.IsComplete && !m_State.Succeeded)
        {
            Complete(m_State.FailureMessage);
            return;
        }

        int step = ++m_NextSceneResizeStep;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (Volatile.Read(ref m_Finished) != 0)
                {
                    return;
                }

                m_Window.Width = targetWindowWidth;
                m_Window.Height = targetWindowHeight;
                KernelLog.InfoFormat(
                    "[EditorViewportSmoke] Requested observable SceneView resize {0}/{1}: {2}x{3}.",
                    step,
                    s_SceneResizeDeltas.Length,
                    targetVisualWidth,
                    targetVisualHeight);
            },
            DispatcherPriority.Loaded);
    }

    private async Task WatchTimeoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(m_Options.TimeoutSeconds), cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(
                () => Complete(
                    $"Editor viewport smoke timed out after {m_Options.TimeoutSeconds} seconds."),
                DispatcherPriority.Send);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void BeginWorldPartitionValidation()
    {
        if (m_WorldValidationStarted)
        {
            return;
        }

        m_WorldValidationStarted = true;
        var services = EngineKernel.Instance.Services;
        if (!services.TryGetService<IEditorWorldDocumentService>(out var documents) ||
            documents?.Current is not { } world ||
            world.Cells.Count == 0)
        {
            m_State.Fail("The real Editor host did not expose an active world document on its first SceneView frame.");
            return;
        }

        EditorWorldCellDocumentState? originCell = world.Cells.FirstOrDefault(
            candidate => candidate.Descriptor.Key.Coordinate == new WorldCellCoordinate(0, 0, 0));
        if (originCell == null)
        {
            m_State.Fail("The canonical Editor world did not expose cell (0,0,0).");
            return;
        }
        EditorWorldCellDocumentState cell = originCell;
        m_WorldDocuments = documents;
        m_WorldCellId = cell.CellId;
        m_WorldPartitionViewModel = new WorldPartitionViewModel();
        WorldPartitionCellViewModel? panelCell = m_WorldPartitionViewModel.Cells
            .FirstOrDefault(candidate => candidate.CellId == cell.CellId);
        if (panelCell == null)
        {
            m_State.Fail($"The World Partition panel did not expose smoke cell '{cell.CellId}'.");
            return;
        }
        m_WorldPartitionViewModel.SelectedCell = panelCell;
        if (documents.Current?.SelectedCellId != cell.CellId)
        {
            m_State.Fail($"The World Partition panel did not publish selection for cell '{cell.CellId}'.");
            return;
        }
        m_State.ObserveWorldFirstOpen(
            world.World.Guid,
            world.Cells.Count,
            cell.CellId.Value,
            cell.Descriptor.Key.Coordinate.X,
            cell.Descriptor.Key.Coordinate.Y,
            cell.Descriptor.Key.Coordinate.Z);
        if (m_State.IsComplete && !m_State.Succeeded)
        {
            return;
        }

        documents.StateChanged += OnWorldDocumentStateChanged;
        if (services.TryGetService<IRuntimeWorldStreamingService>(out var streaming) && streaming != null)
        {
            streaming.ClearStreamingSource();
        }

        m_State.NotifyWorldCellLoadRequested(cell.CellId.Value);
        if (!documents.LoadCellForEditing(cell.CellId))
        {
            m_State.Fail($"The real Editor host rejected the explicit load request for cell '{cell.CellId}'.");
            return;
        }

        ObserveWorldDocument(documents.Current);
    }

    private void OnWorldDocumentStateChanged(EditorWorldDocumentState? state)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ObserveWorldDocument(state), DispatcherPriority.Loaded);
            return;
        }

        ObserveWorldDocument(state);
    }

    private void ObserveWorldDocument(EditorWorldDocumentState? state)
    {
        if (Volatile.Read(ref m_Finished) != 0 || state == null || !m_WorldCellId.IsValid)
        {
            return;
        }

        EditorWorldCellDocumentState? cell = state.Cells.FirstOrDefault(
            candidate => candidate.CellId == m_WorldCellId);
        if (cell == null)
        {
            Complete($"The active Editor world lost smoke cell '{m_WorldCellId}' during validation.");
            return;
        }
        if (cell.Streaming.State == WorldCellStreamingState.Failed)
        {
            Complete($"Editor world cell '{m_WorldCellId}' failed during real-host validation: {cell.Streaming.Diagnostic}");
            return;
        }

        if (!m_WorldCellActive &&
            cell.IsEditPinned &&
            cell.Streaming.State == WorldCellStreamingState.Active)
        {
            m_State.ObserveWorldCellActive(m_WorldCellId.Value);
            m_WorldCellActive = true;
            TryShowConcurrentViewports();
            TryRequestWorldCellUnload();
            return;
        }

        if (m_WorldUnloadRequested &&
            !cell.IsEditPinned &&
            cell.Streaming.State == WorldCellStreamingState.Unloaded)
        {
            bool complete = m_State.ObserveWorldCellUnloaded(m_WorldCellId.Value);
            DetachWorldDocuments();
            if (complete)
            {
                Complete(null);
            }
        }
    }

    private void TryShowConcurrentViewports()
    {
        if (!m_ConcurrentViewportsRequested || !m_WorldCellActive ||
            m_ConcurrentViewportsShown || Volatile.Read(ref m_Finished) != 0)
        {
            return;
        }

        m_ConcurrentViewportsShown = true;
        m_State.NotifyGameViewActivated();
        if (m_State.IsComplete && !m_State.Succeeded)
        {
            Complete(m_State.FailureMessage);
            return;
        }

        m_GameView.IsVisible = true;
        m_TerrainBrushHost.IsVisible = m_TerrainBrushView != null;
        m_ViewportLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        m_ViewportLayout.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        m_ViewportLayout.ColumnDefinitions[2].Width = m_TerrainBrushView != null
            ? new GridLength(280)
            : new GridLength(0);
        m_Window.MinWidth = m_TerrainBrushView != null ? 960 : 640;
        m_Window.Width = Math.Max(m_Window.Width, m_TerrainBrushView != null ? 1200 : 960);
        m_ViewportLayout.UpdateLayout();

        double renderScaling = TopLevel.GetTopLevel(m_ViewportLayout)?.RenderScaling ?? 1.0;
        m_State.NotifyConcurrentViewportLayout(
            checked((uint)Math.Max(1, (int)(m_SceneView.Bounds.Width * renderScaling))),
            checked((uint)Math.Max(1, (int)(m_SceneView.Bounds.Height * renderScaling))),
            (float)m_SceneView.Bounds.Width,
            (float)m_SceneView.Bounds.Height,
            checked((uint)Math.Max(1, (int)(m_GameView.Bounds.Width * renderScaling))),
            checked((uint)Math.Max(1, (int)(m_GameView.Bounds.Height * renderScaling))),
            (float)m_GameView.Bounds.Width,
            (float)m_GameView.Bounds.Height);
        if (m_State.IsComplete && !m_State.Succeeded)
        {
            Complete(m_State.FailureMessage);
            return;
        }

        if (!m_SceneView.HasWorldPartitionOverlayVisual)
        {
            Complete(
                "SceneView removed its world-partition overlay controls while showing concurrent viewports.");
            return;
        }

        if (m_TerrainBrushView != null)
        {
            ActivateTerrainPaint();
        }

        KernelLog.InfoFormat(
            "[EditorViewportSmoke] Concurrent SceneView/GameView presentation started with active cell '{0}'. TerrainPaint={1}.",
            m_WorldCellId,
            m_TerrainPaintActivated);
    }

    private void ActivateTerrainPaint()
    {
        ToggleButton[] toggles = m_TerrainBrushView!
            .GetVisualDescendants()
            .OfType<ToggleButton>()
            .ToArray();
        ToggleButton? brush = toggles.SingleOrDefault(
            toggle => string.Equals(toggle.Content as string, "Brush", StringComparison.Ordinal));
        ToggleButton? paint = toggles.SingleOrDefault(
            toggle => string.Equals(toggle.Content as string, "Paint", StringComparison.Ordinal));
        if (brush == null || paint == null)
        {
            Complete(
                "The real Terrain Brush panel did not expose its Brush and Paint toggle controls.");
            return;
        }

        brush.IsChecked = false;
        paint.IsChecked = true;
        if (brush.IsChecked == true || paint.IsChecked != true)
        {
            Complete("The real Terrain Brush panel rejected Paint-only activation.");
            return;
        }

        m_TerrainPaintActivated = true;
        m_State.NotifyTerrainPaintActivated();
        TryRequestWorldCellUnload();
    }

    private void TryRequestWorldCellUnload()
    {
        if (!m_ConcurrentPresentationFinished || !m_WorldCellActive ||
            m_WorldUnloadRequested || Volatile.Read(ref m_Finished) != 0 ||
            (m_TerrainBrushView != null && !m_TerrainPaintActivated))
        {
            return;
        }

        m_State.NotifyWorldCellUnloadRequested(m_WorldCellId.Value);
        m_WorldUnloadRequested = true;
        if (m_WorldDocuments?.UnloadCellForEditing(m_WorldCellId) != true)
        {
            Complete(
                $"The real Editor host rejected the explicit unload request for cell '{m_WorldCellId}'.");
        }
    }

    private void DetachWorldDocuments()
    {
        if (m_WorldDocuments != null)
        {
            m_WorldDocuments.StateChanged -= OnWorldDocumentStateChanged;
            m_WorldDocuments = null;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref m_Finished) == 0)
        {
            Complete("Editor viewport smoke window closed before validation completed.", requestShutdown: false);
        }

        m_Desktop.Shutdown(Environment.ExitCode);
    }

    private void Complete(string? failureMessage, bool requestShutdown = true)
    {
        if (Interlocked.CompareExchange(ref m_Finished, 1, 0) != 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(failureMessage))
        {
            m_State.Fail(failureMessage);
        }

        m_TimeoutCancellation.Cancel();
        DetachWorldDocuments();
        var artifact = m_State.CreateArtifact(m_Options.Profile, m_Options.TimeoutSeconds);
        var exitCode = artifact.Passed ? 0 : 1;
        try
        {
            EditorViewportSmokeArtifactWriter.WriteAtomic(m_Options.OutputPath, artifact);
            if (artifact.Passed)
            {
                KernelLog.InfoFormat(
                    "[EditorViewportSmoke] Passed. Scene={0}x{1}, Resized={2}x{3}, Game={4}x{5}, WorldCells={6}, Output={7}",
                    artifact.SceneFirstFrame!.Value.Width,
                    artifact.SceneFirstFrame.Value.Height,
                    artifact.SceneResizedFrame!.Value.Width,
                    artifact.SceneResizedFrame.Value.Height,
                    artifact.GameFirstFrame!.Value.Width,
                    artifact.GameFirstFrame.Value.Height,
                    artifact.WorldPartition!.CellCount,
                    m_Options.OutputPath);
            }
            else
            {
                KernelLog.ErrorFormat(
                    "[EditorViewportSmoke] Failed: {0}. Output={1}",
                    artifact.FailureMessage ?? "viewport checks did not pass",
                    m_Options.OutputPath);
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            KernelLog.ErrorFormat(
                "[EditorViewportSmoke] Failed to write artifact '{0}': {1}",
                m_Options.OutputPath,
                ex.Message);
        }

        Environment.ExitCode = exitCode;
        if (requestShutdown)
        {
            Dispatcher.UIThread.Post(
                m_Window.Close,
                DispatcherPriority.Background);
        }
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(EditorViewportSmokeSession));
        }
    }
}

internal static class EditorViewportSmokeArtifactWriter
{
    private static readonly JsonSerializerOptions s_JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void WriteAtomic(string outputPath, EditorViewportSmokeArtifact artifact)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(artifact);

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException($"Viewport-smoke path '{fullPath}' has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = fullPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            var json = JsonSerializer.Serialize(artifact, s_JsonOptions);
            File.WriteAllText(temporaryPath, json + Environment.NewLine, new UTF8Encoding(false));
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
