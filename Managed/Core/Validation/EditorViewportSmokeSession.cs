using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArisenEditor.Core.Services;
using ArisenEditor.Views;
using ArisenEditor.ViewModels;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace ArisenEditor.Core.Validation;

internal readonly record struct EditorViewportSmokeOptions(
    string Profile,
    string OutputPath,
    int TimeoutSeconds)
{
    private const int DefaultTimeoutSeconds = 30;
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
        return new EditorViewportSmokeOptions(profile, outputPath, timeoutSeconds);
    }
}

internal sealed class EditorViewportSmokeSession : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime m_Desktop;
    private readonly EditorViewportSmokeOptions m_Options;
    private readonly EditorViewportSmokeState m_State = new();
    private readonly CancellationTokenSource m_TimeoutCancellation = new();
    private readonly SceneView m_SceneView;
    private readonly TabControl m_Tabs;
    private readonly Window m_Window;
    private IEditorWorldDocumentService? m_WorldDocuments;
    private WorldPartitionViewModel? m_WorldPartitionViewModel;
    private WorldCellId m_WorldCellId;
    private bool m_WorldValidationStarted;
    private bool m_WorldUnloadRequested;
    private int m_Finished;
    private bool m_Disposed;

    public EditorViewportSmokeSession(
        IClassicDesktopStyleApplicationLifetime desktop,
        EditorViewportSmokeOptions options)
    {
        m_Desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        m_Options = options;

        m_SceneView = new SceneView
        {
            DataContext = new SceneViewModel()
        };
        var sceneTab = new TabItem
        {
            Header = "Scene",
            Content = m_SceneView
        };
        var gameTab = new TabItem
        {
            Header = "Game",
            Content = new EditorViewportView
            {
                DataContext = new EditorViewportViewModel(isSceneView: false)
            }
        };
        m_Tabs = new TabControl
        {
            ItemsSource = new[] { sceneTab, gameTab },
            SelectedIndex = 0
        };
        m_Window = new Window
        {
            Title = "Arisen Editor",
            Width = 800,
            Height = 500,
            MinWidth = 640,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = m_Tabs
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
        _ = WatchTimeoutAsync(m_TimeoutCancellation.Token);

        KernelLog.InfoFormat(
            "[EditorViewportSmoke] Started. Timeout={0}s, Output={1}",
            m_Options.TimeoutSeconds,
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

        try
        {
            var action = m_State.Observe(observation);
            switch (action)
            {
                case EditorViewportSmokeAction.ResizeSceneView:
                    BeginWorldPartitionValidation();
                    if (m_State.IsComplete && !m_State.Succeeded)
                    {
                        Complete(m_State.FailureMessage);
                        return;
                    }
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            m_Window.Width += 160;
                            m_Window.Height += 90;
                        },
                        DispatcherPriority.Loaded);
                    break;

                case EditorViewportSmokeAction.ShowGameView:
                    Dispatcher.UIThread.Post(
                        () =>
                        {
                            m_State.NotifyGameViewActivated();
                            if (m_State.IsComplete && !m_State.Succeeded)
                            {
                                Complete(m_State.FailureMessage);
                                return;
                            }

                            m_Tabs.SelectedIndex = 1;
                        },
                        DispatcherPriority.Loaded);
                    break;

                case EditorViewportSmokeAction.Complete:
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

        EditorWorldCellDocumentState cell = world.Cells
            .OrderBy(candidate => candidate.CellId)
            .First();
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
        m_State.ObserveWorldFirstOpen(world.World.Guid, world.Cells.Count, cell.CellId.Value);
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

        if (!m_WorldUnloadRequested &&
            cell.IsEditPinned &&
            cell.Streaming.State == WorldCellStreamingState.Active)
        {
            m_State.ObserveWorldCellActive(m_WorldCellId.Value);
            m_State.NotifyWorldCellUnloadRequested(m_WorldCellId.Value);
            m_WorldUnloadRequested = true;
            if (m_WorldDocuments?.UnloadCellForEditing(m_WorldCellId) != true)
            {
                Complete($"The real Editor host rejected the explicit unload request for cell '{m_WorldCellId}'.");
            }
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
