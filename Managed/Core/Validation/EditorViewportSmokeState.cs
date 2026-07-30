using System;

namespace ArisenEditor.Core.Validation;

public enum EditorViewportKind
{
    SceneView,
    GameView
}

public enum EditorViewportSmokeAction
{
    None,
    ResizeSceneView,
    ShowGameView,
    RestartRenderDoc,
    FinishConcurrentPresentation,
    Complete,
    Failed
}

public readonly record struct EditorViewportPresentationObservation(
    EditorViewportKind ViewportKind,
    ulong Ticket,
    uint FrameIndex,
    uint ResizeGeneration,
    uint Width,
    uint Height,
    uint LastConsumedFrameIndex,
    bool ConsumptionReported,
    bool RequiresVerticalFlip,
    float PresentationScaleX,
    float PresentationScaleY,
    float PresentationCenterX,
    float PresentationCenterY,
    float VisualWidth,
    float VisualHeight,
    long SurfaceOwnershipGeneration,
    string SurfaceOwnershipOwnerId,
    int ImportedImageCount,
    int ImportedSemaphoreCount);

public static class EditorViewportPresentationDiagnostics
{
    public static event Action<EditorViewportPresentationObservation>? Presented;

    public static void Report(in EditorViewportPresentationObservation observation)
    {
        Presented?.Invoke(observation);
    }
}

public sealed class EditorViewportSmokeChecks
{
    public bool RenderDocStartupExpectationMet { get; init; }
    public bool RenderDocRestartExpectationMet { get; init; }
    public bool InteropResourceCachesBounded { get; init; }
    public bool SceneFirstFramePresented { get; init; }
    public bool ScenePresentedBeforeGameViewActivation { get; init; }
    public bool SceneResizeGenerationAdvanced { get; init; }
    public bool SceneOutputSizeChanged { get; init; }
    public bool SceneResizeStressPassed { get; init; }
    public bool SceneFrameConsumptionReported { get; init; }
    public bool SceneOrientationCorrect { get; init; }
    public bool GameFirstFramePresented { get; init; }
    public bool GameFrameConsumptionReported { get; init; }
    public bool GameOrientationCorrect { get; init; }
    public bool ConcurrentSceneFramesPresented { get; init; }
    public bool ConcurrentGameFramesPresented { get; init; }
    public bool PostRestartSceneFramesPresented { get; init; }
    public bool PostRestartGameFramesPresented { get; init; }
    public bool TerrainPaintInteractionPassed { get; init; }
    public bool WorldVisibleOnFirstOpen { get; init; }
    public bool WorldOriginCellSelected { get; init; }
    public bool WorldCellLoadObserved { get; init; }
    public bool WorldCellUnloadObserved { get; init; }

    public bool Passed =>
        RenderDocStartupExpectationMet &&
        RenderDocRestartExpectationMet &&
        InteropResourceCachesBounded &&
        SceneFirstFramePresented &&
        ScenePresentedBeforeGameViewActivation &&
        SceneResizeGenerationAdvanced &&
        SceneOutputSizeChanged &&
        SceneResizeStressPassed &&
        SceneFrameConsumptionReported &&
        SceneOrientationCorrect &&
        GameFirstFramePresented &&
        GameFrameConsumptionReported &&
        GameOrientationCorrect &&
        ConcurrentSceneFramesPresented &&
        ConcurrentGameFramesPresented &&
        PostRestartSceneFramesPresented &&
        PostRestartGameFramesPresented &&
        TerrainPaintInteractionPassed &&
        WorldVisibleOnFirstOpen &&
        WorldOriginCellSelected &&
        WorldCellLoadObserved &&
        WorldCellUnloadObserved;
}

public sealed class EditorWorldPartitionSmokeObservation
{
    public Guid WorldGuid { get; init; }
    public int CellCount { get; init; }
    public Guid CellId { get; init; }
    public int CellX { get; init; }
    public int CellY { get; init; }
    public int CellZ { get; init; }
    public bool LoadRequested { get; init; }
    public bool ActiveObserved { get; init; }
    public bool UnloadRequested { get; init; }
    public bool UnloadedObserved { get; init; }
}

public sealed class EditorViewportSmokeArtifact
{
    public int SchemaVersion { get; init; } = 6;
    public string CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public required string Profile { get; init; }
    public int TimeoutSeconds { get; init; }
    public bool RenderDocExpectedAtStartup { get; init; }
    public bool RenderDocAvailabilityObserved { get; init; }
    public bool RenderDocAvailableAtStartup { get; init; }
    public bool RenderDocRestartExpected { get; init; }
    public bool RenderDocRestartRequested { get; init; }
    public bool RenderDocRestartCompleted { get; init; }
    public bool RenderDocAvailableAfterRestart { get; init; }
    public ulong GraphicsGenerationBeforeRestart { get; init; }
    public ulong GraphicsGenerationAfterRestart { get; init; }
    public int GameViewActivationCount { get; init; }
    public int SceneResizeRequestCount { get; init; }
    public int SceneResizeTransitionCount { get; init; }
    public int ConcurrentSceneFrameCount { get; init; }
    public int ConcurrentGameFrameCount { get; init; }
    public int PostRestartConcurrentSceneFrameCount { get; init; }
    public int PostRestartConcurrentGameFrameCount { get; init; }
    public bool TerrainPaintAvailable { get; init; }
    public bool TerrainPaintActivated { get; init; }
    public int MaxSceneImportedImageCount { get; init; }
    public int MaxSceneImportedSemaphoreCount { get; init; }
    public int MaxGameImportedImageCount { get; init; }
    public int MaxGameImportedSemaphoreCount { get; init; }
    public EditorViewportPresentationObservation? SceneFirstFrame { get; init; }
    public EditorViewportPresentationObservation? SceneResizedFrame { get; init; }
    public EditorViewportPresentationObservation? GameFirstFrame { get; init; }
    public EditorWorldPartitionSmokeObservation? WorldPartition { get; init; }
    public string? FailureMessage { get; init; }
    public required EditorViewportSmokeChecks Checks { get; init; }
    public bool Passed => FailureMessage == null && Checks.Passed;
}

public sealed class EditorViewportSmokeState
{
    public const int RequiredSceneResizeTransitions = 4;
    public const int RequiredConcurrentFramesPerViewport = 320;
    public const int RequiredImportedImagesPerViewport = 3;
    public const int RequiredImportedSemaphoresPerViewport = 4;
    private const float TransformEpsilon = 0.01f;

    private readonly bool m_ExpectRenderDocAtStartup;
    private readonly bool m_ExpectRenderDocRestart;
    private EditorViewportSmokeStage m_Stage = EditorViewportSmokeStage.WaitingForSceneFirstFrame;
    private bool m_RenderDocAvailabilityObserved;
    private bool m_RenderDocAvailableAtStartup;
    private bool m_RenderDocRestartRequested;
    private bool m_RenderDocRestartCompleted;
    private bool m_RenderDocAvailableAfterRestart;
    private ulong m_GraphicsGenerationBeforeRestart;
    private ulong m_GraphicsGenerationAfterRestart;
    private bool m_ScenePresentedBeforeGameViewActivation;
    private int m_GameViewActivationCount;
    private Guid m_WorldGuid;
    private int m_WorldCellCount;
    private Guid m_WorldCellId;
    private int m_WorldCellX;
    private int m_WorldCellY;
    private int m_WorldCellZ;
    private bool m_WorldCellLoadRequested;
    private bool m_WorldCellActiveObserved;
    private bool m_WorldCellUnloadRequested;
    private bool m_WorldCellUnloadedObserved;
    private int m_ConcurrentSceneFrameCount;
    private int m_ConcurrentGameFrameCount;
    private int m_PostRestartConcurrentSceneFrameCount;
    private int m_PostRestartConcurrentGameFrameCount;
    private bool m_TerrainPaintAvailable;
    private bool m_TerrainPaintActivated;
    private int m_MaxSceneImportedImageCount;
    private int m_MaxSceneImportedSemaphoreCount;
    private int m_MaxGameImportedImageCount;
    private int m_MaxGameImportedSemaphoreCount;
    private int m_SceneResizeRequestCount;
    private int m_SceneResizeTransitionCount;
    private uint m_LastAcceptedSceneResizeGeneration;
    private bool m_HasPendingSceneResizeTarget;
    private uint m_ExpectedSceneResizeWidth;
    private uint m_ExpectedSceneResizeHeight;
    private float m_ExpectedSceneResizeVisualWidth;
    private float m_ExpectedSceneResizeVisualHeight;
    private uint m_ExpectedConcurrentSceneWidth;
    private uint m_ExpectedConcurrentSceneHeight;
    private float m_ExpectedConcurrentSceneVisualWidth;
    private float m_ExpectedConcurrentSceneVisualHeight;
    private uint m_ExpectedConcurrentGameWidth;
    private uint m_ExpectedConcurrentGameHeight;
    private float m_ExpectedConcurrentGameVisualWidth;
    private float m_ExpectedConcurrentGameVisualHeight;

    public EditorViewportPresentationObservation? SceneFirstFrame { get; private set; }
    public EditorViewportPresentationObservation? SceneResizedFrame { get; private set; }
    public EditorViewportPresentationObservation? GameFirstFrame { get; private set; }
    public string? FailureMessage { get; private set; }

    public EditorViewportSmokeState(
        bool expectRenderDocAtStartup = false,
        bool expectRenderDocRestart = false)
    {
        if (expectRenderDocAtStartup && expectRenderDocRestart)
        {
            throw new ArgumentException(
                "The viewport smoke cannot require both process-start RenderDoc and an in-process RenderDoc restart.");
        }

        m_ExpectRenderDocAtStartup = expectRenderDocAtStartup;
        m_ExpectRenderDocRestart = expectRenderDocRestart;
    }

    public bool IsComplete =>
        m_Stage == EditorViewportSmokeStage.Failed ||
        (m_Stage == EditorViewportSmokeStage.Complete && m_WorldCellUnloadedObserved);
    public bool Succeeded =>
        m_Stage == EditorViewportSmokeStage.Complete &&
        m_WorldCellUnloadedObserved &&
        m_RenderDocAvailabilityObserved &&
        m_RenderDocAvailableAtStartup == m_ExpectRenderDocAtStartup &&
        (!m_ExpectRenderDocRestart ||
         (m_RenderDocRestartRequested &&
          m_RenderDocRestartCompleted &&
          m_RenderDocAvailableAfterRestart &&
          m_GraphicsGenerationAfterRestart > m_GraphicsGenerationBeforeRestart)) &&
        (!m_TerrainPaintAvailable || m_TerrainPaintActivated);

    public EditorViewportSmokeAction Observe(in EditorViewportPresentationObservation observation)
    {
        if (IsComplete)
        {
            return EditorViewportSmokeAction.None;
        }

        if (m_Stage == EditorViewportSmokeStage.WaitingForRenderDocRestart)
        {
            return EditorViewportSmokeAction.None;
        }

        var validationFailure = ValidateObservation(observation);
        if (validationFailure != null)
        {
            return Fail(validationFailure);
        }

        ObserveImportedResourceCounts(observation);

        switch (m_Stage)
        {
            case EditorViewportSmokeStage.WaitingForSceneFirstFrame:
                if (observation.ViewportKind != EditorViewportKind.SceneView)
                {
                    return Fail("GameView presented before the initial SceneView frame.");
                }

                SceneFirstFrame = observation;
                m_LastAcceptedSceneResizeGeneration = observation.ResizeGeneration;
                m_ScenePresentedBeforeGameViewActivation = m_GameViewActivationCount == 0;
                m_Stage = EditorViewportSmokeStage.WaitingForSceneResize;
                return EditorViewportSmokeAction.ResizeSceneView;

            case EditorViewportSmokeStage.WaitingForSceneResize:
                if (observation.ViewportKind != EditorViewportKind.SceneView)
                {
                    return Fail("GameView presented before the SceneView resize completed.");
                }

                if (!m_HasPendingSceneResizeTarget ||
                    m_ExpectedSceneResizeVisualWidth <= 0.0f ||
                    m_ExpectedSceneResizeVisualHeight <= 0.0f)
                {
                    return Fail("The SceneView resize target was not registered by the smoke harness.");
                }

                if (observation.Width != m_ExpectedSceneResizeWidth ||
                    observation.Height != m_ExpectedSceneResizeHeight ||
                    !NearlyEqual(observation.VisualWidth, m_ExpectedSceneResizeVisualWidth) ||
                    !NearlyEqual(observation.VisualHeight, m_ExpectedSceneResizeVisualHeight))
                {
                    return EditorViewportSmokeAction.None;
                }

                if (observation.ResizeGeneration <= m_LastAcceptedSceneResizeGeneration)
                {
                    return EditorViewportSmokeAction.None;
                }

                m_LastAcceptedSceneResizeGeneration = observation.ResizeGeneration;
                m_HasPendingSceneResizeTarget = false;
                m_SceneResizeTransitionCount++;
                SceneResizedFrame = observation;
                if (m_SceneResizeTransitionCount < RequiredSceneResizeTransitions)
                {
                    return EditorViewportSmokeAction.ResizeSceneView;
                }

                m_Stage = EditorViewportSmokeStage.WaitingForGameFirstFrame;
                return EditorViewportSmokeAction.ShowGameView;

            case EditorViewportSmokeStage.WaitingForGameFirstFrame:
                if (observation.ViewportKind == EditorViewportKind.SceneView)
                {
                    return EditorViewportSmokeAction.None;
                }

                if (m_GameViewActivationCount == 0)
                {
                    return Fail("GameView presented before the smoke harness activated its tab.");
                }

                GameFirstFrame = observation;
                m_Stage = EditorViewportSmokeStage.WaitingForConcurrentFrames;
                return EditorViewportSmokeAction.None;

            case EditorViewportSmokeStage.WaitingForConcurrentFrames:
                if (!MatchesConcurrentLayout(observation))
                {
                    return EditorViewportSmokeAction.None;
                }

                if (observation.ViewportKind == EditorViewportKind.SceneView)
                {
                    m_ConcurrentSceneFrameCount++;
                }
                else
                {
                    m_ConcurrentGameFrameCount++;
                }

                if (m_ConcurrentSceneFrameCount < RequiredConcurrentFramesPerViewport ||
                    m_ConcurrentGameFrameCount < RequiredConcurrentFramesPerViewport)
                {
                    return EditorViewportSmokeAction.None;
                }

                if (m_ExpectRenderDocRestart)
                {
                    if (!m_TerrainPaintActivated)
                    {
                        return Fail(
                            "RenderDoc restart was reached before Terrain Paint-only activation.");
                    }

                    m_Stage = EditorViewportSmokeStage.WaitingForRenderDocRestart;
                    return EditorViewportSmokeAction.RestartRenderDoc;
                }

                m_Stage = EditorViewportSmokeStage.Complete;
                return EditorViewportSmokeAction.FinishConcurrentPresentation;

            case EditorViewportSmokeStage.WaitingForPostRestartConcurrentFrames:
                if (!MatchesConcurrentLayout(observation))
                {
                    return EditorViewportSmokeAction.None;
                }

                if (observation.ViewportKind == EditorViewportKind.SceneView)
                {
                    m_PostRestartConcurrentSceneFrameCount++;
                }
                else
                {
                    m_PostRestartConcurrentGameFrameCount++;
                }

                if (m_PostRestartConcurrentSceneFrameCount < RequiredConcurrentFramesPerViewport ||
                    m_PostRestartConcurrentGameFrameCount < RequiredConcurrentFramesPerViewport)
                {
                    return EditorViewportSmokeAction.None;
                }

                m_Stage = EditorViewportSmokeStage.Complete;
                return EditorViewportSmokeAction.FinishConcurrentPresentation;

            default:
                return EditorViewportSmokeAction.None;
        }
    }

    public void NotifySceneResizeRequested(
        uint width,
        uint height,
        float visualWidth,
        float visualHeight)
    {
        if (m_Stage != EditorViewportSmokeStage.WaitingForSceneResize)
        {
            Fail("The SceneView resize target was registered outside the resize stage.");
            return;
        }

        if (m_HasPendingSceneResizeTarget)
        {
            Fail("The SceneView smoke requested another resize before the current target was presented.");
            return;
        }

        if (m_SceneResizeRequestCount >= RequiredSceneResizeTransitions)
        {
            Fail($"The SceneView smoke requested more than {RequiredSceneResizeTransitions} resize transitions.");
            return;
        }

        var lastAccepted = SceneResizedFrame ?? SceneFirstFrame!.Value;
        if (width == 0 || height == 0 ||
            !float.IsFinite(visualWidth) || !float.IsFinite(visualHeight) ||
            visualWidth <= 0.0f || visualHeight <= 0.0f ||
            (width == lastAccepted.Width &&
             height == lastAccepted.Height &&
             NearlyEqual(visualWidth, lastAccepted.VisualWidth) &&
             NearlyEqual(visualHeight, lastAccepted.VisualHeight)))
        {
            Fail("The SceneView resize target must be non-zero and differ from the last accepted frame.");
            return;
        }

        m_SceneResizeRequestCount++;
        m_HasPendingSceneResizeTarget = true;
        m_ExpectedSceneResizeWidth = width;
        m_ExpectedSceneResizeHeight = height;
        m_ExpectedSceneResizeVisualWidth = visualWidth;
        m_ExpectedSceneResizeVisualHeight = visualHeight;
    }

    public void NotifyGameViewActivated()
    {
        if (m_Stage != EditorViewportSmokeStage.WaitingForGameFirstFrame)
        {
            Fail("GameView was activated before the resized SceneView frame was accepted.");
            return;
        }

        m_GameViewActivationCount++;
    }

    public void NotifyConcurrentViewportLayout(
        uint sceneWidth,
        uint sceneHeight,
        float sceneVisualWidth,
        float sceneVisualHeight,
        uint gameWidth,
        uint gameHeight,
        float gameVisualWidth,
        float gameVisualHeight)
    {
        if (m_Stage is not (EditorViewportSmokeStage.WaitingForGameFirstFrame or
            EditorViewportSmokeStage.WaitingForConcurrentFrames))
        {
            Fail("Concurrent viewport dimensions were registered outside the concurrent presentation stage.");
            return;
        }

        if (sceneWidth == 0 || sceneHeight == 0 || gameWidth == 0 || gameHeight == 0 ||
            !IsPositiveFinite(sceneVisualWidth) || !IsPositiveFinite(sceneVisualHeight) ||
            !IsPositiveFinite(gameVisualWidth) || !IsPositiveFinite(gameVisualHeight))
        {
            Fail("Concurrent SceneView and GameView dimensions must be finite and non-zero.");
            return;
        }

        m_ExpectedConcurrentSceneWidth = sceneWidth;
        m_ExpectedConcurrentSceneHeight = sceneHeight;
        m_ExpectedConcurrentSceneVisualWidth = sceneVisualWidth;
        m_ExpectedConcurrentSceneVisualHeight = sceneVisualHeight;
        m_ExpectedConcurrentGameWidth = gameWidth;
        m_ExpectedConcurrentGameHeight = gameHeight;
        m_ExpectedConcurrentGameVisualWidth = gameVisualWidth;
        m_ExpectedConcurrentGameVisualHeight = gameVisualHeight;
    }

    public void NotifyTerrainPaintAvailability(bool available)
    {
        if (m_Stage == EditorViewportSmokeStage.Failed)
        {
            return;
        }

        m_TerrainPaintAvailable = available;
    }

    public void ObserveRenderDocAvailability(bool available)
    {
        if (m_Stage == EditorViewportSmokeStage.Failed)
        {
            return;
        }

        m_RenderDocAvailabilityObserved = true;
        m_RenderDocAvailableAtStartup = available;
        if (available != m_ExpectRenderDocAtStartup)
        {
            Fail(m_ExpectRenderDocAtStartup
                ? "RenderDoc-enabled Editor startup did not load RenderDoc before Vulkan initialization."
                : "Ordinary Editor startup loaded RenderDoc without an explicit capture request.");
        }
    }

    public void NotifyRenderDocRestartRequested(ulong previousGeneration)
    {
        if (!m_ExpectRenderDocRestart ||
            m_Stage != EditorViewportSmokeStage.WaitingForRenderDocRestart)
        {
            Fail("RenderDoc restart was requested outside its smoke stage.");
            return;
        }
        if (m_RenderDocRestartRequested)
        {
            Fail("RenderDoc restart was requested more than once.");
            return;
        }
        if (previousGeneration == 0)
        {
            Fail("RenderDoc restart began without an active graphics generation.");
            return;
        }

        m_RenderDocRestartRequested = true;
        m_GraphicsGenerationBeforeRestart = previousGeneration;
    }

    public void ObserveRenderDocRestartCompleted(
        bool succeeded,
        ulong previousGeneration,
        ulong currentGeneration,
        bool renderDocAvailable,
        string diagnostic)
    {
        if (!m_RenderDocRestartRequested ||
            m_Stage != EditorViewportSmokeStage.WaitingForRenderDocRestart)
        {
            Fail("RenderDoc restart completed without a matching request.");
            return;
        }
        if (!succeeded)
        {
            Fail(string.IsNullOrWhiteSpace(diagnostic)
                ? "The in-process RenderDoc graphics restart failed."
                : $"The in-process RenderDoc graphics restart failed: {diagnostic}");
            return;
        }
        if (previousGeneration != m_GraphicsGenerationBeforeRestart ||
            currentGeneration <= previousGeneration)
        {
            Fail(
                $"RenderDoc restart reported invalid graphics generations. " +
                $"ExpectedPrevious={m_GraphicsGenerationBeforeRestart}, " +
                $"Previous={previousGeneration}, Current={currentGeneration}.");
            return;
        }
        if (!renderDocAvailable)
        {
            Fail("RenderDoc API was unavailable after the graphics generation restart.");
            return;
        }

        m_RenderDocRestartCompleted = true;
        m_RenderDocAvailableAfterRestart = true;
        m_GraphicsGenerationAfterRestart = currentGeneration;
        m_Stage = EditorViewportSmokeStage.WaitingForPostRestartConcurrentFrames;
    }

    public void NotifyTerrainPaintActivated()
    {
        if (!m_TerrainPaintAvailable)
        {
            Fail("Terrain Paint was activated even though the Terrain Brush panel was unavailable.");
            return;
        }

        m_TerrainPaintActivated = true;
    }

    public void ObserveWorldFirstOpen(
        Guid worldGuid,
        int cellCount,
        Guid cellId,
        int cellX,
        int cellY,
        int cellZ)
    {
        if (m_Stage == EditorViewportSmokeStage.Failed)
        {
            return;
        }
        if (worldGuid == Guid.Empty || cellCount <= 0 || cellId == Guid.Empty)
        {
            Fail("The Editor did not expose a valid active world and world cell on first open.");
            return;
        }

        m_WorldGuid = worldGuid;
        m_WorldCellCount = cellCount;
        m_WorldCellId = cellId;
        m_WorldCellX = cellX;
        m_WorldCellY = cellY;
        m_WorldCellZ = cellZ;
    }

    public void NotifyWorldCellLoadRequested(Guid cellId)
    {
        if (!ValidateWorldCell(cellId)) return;
        m_WorldCellLoadRequested = true;
    }

    public void ObserveWorldCellActive(Guid cellId)
    {
        if (!ValidateWorldCell(cellId) || !m_WorldCellLoadRequested)
        {
            Fail("The Editor world cell became active before its explicit load request was recorded.");
            return;
        }
        m_WorldCellActiveObserved = true;
    }

    public void NotifyWorldCellUnloadRequested(Guid cellId)
    {
        if (!ValidateWorldCell(cellId) || !m_WorldCellActiveObserved)
        {
            Fail("The Editor world cell unload was requested before activation was observed.");
            return;
        }
        m_WorldCellUnloadRequested = true;
    }

    public bool ObserveWorldCellUnloaded(Guid cellId)
    {
        if (!ValidateWorldCell(cellId) || !m_WorldCellUnloadRequested)
        {
            Fail("The Editor world cell unloaded before its explicit unload request was recorded.");
            return false;
        }
        m_WorldCellUnloadedObserved = true;
        return IsComplete;
    }

    public EditorViewportSmokeAction Fail(string message)
    {
        if (IsComplete)
        {
            return EditorViewportSmokeAction.None;
        }

        FailureMessage = string.IsNullOrWhiteSpace(message)
            ? "Editor viewport smoke failed without a diagnostic."
            : message;
        m_Stage = EditorViewportSmokeStage.Failed;
        return EditorViewportSmokeAction.Failed;
    }

    public EditorViewportSmokeArtifact CreateArtifact(string profile, int timeoutSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        var checks = CreateChecks();
        return new EditorViewportSmokeArtifact
        {
            Profile = profile,
            TimeoutSeconds = timeoutSeconds,
            RenderDocExpectedAtStartup = m_ExpectRenderDocAtStartup,
            RenderDocAvailabilityObserved = m_RenderDocAvailabilityObserved,
            RenderDocAvailableAtStartup = m_RenderDocAvailableAtStartup,
            RenderDocRestartExpected = m_ExpectRenderDocRestart,
            RenderDocRestartRequested = m_RenderDocRestartRequested,
            RenderDocRestartCompleted = m_RenderDocRestartCompleted,
            RenderDocAvailableAfterRestart = m_RenderDocAvailableAfterRestart,
            GraphicsGenerationBeforeRestart = m_GraphicsGenerationBeforeRestart,
            GraphicsGenerationAfterRestart = m_GraphicsGenerationAfterRestart,
            GameViewActivationCount = m_GameViewActivationCount,
            SceneResizeRequestCount = m_SceneResizeRequestCount,
            SceneResizeTransitionCount = m_SceneResizeTransitionCount,
            ConcurrentSceneFrameCount = m_ConcurrentSceneFrameCount,
            ConcurrentGameFrameCount = m_ConcurrentGameFrameCount,
            PostRestartConcurrentSceneFrameCount = m_PostRestartConcurrentSceneFrameCount,
            PostRestartConcurrentGameFrameCount = m_PostRestartConcurrentGameFrameCount,
            TerrainPaintAvailable = m_TerrainPaintAvailable,
            TerrainPaintActivated = m_TerrainPaintActivated,
            MaxSceneImportedImageCount = m_MaxSceneImportedImageCount,
            MaxSceneImportedSemaphoreCount = m_MaxSceneImportedSemaphoreCount,
            MaxGameImportedImageCount = m_MaxGameImportedImageCount,
            MaxGameImportedSemaphoreCount = m_MaxGameImportedSemaphoreCount,
            SceneFirstFrame = SceneFirstFrame,
            SceneResizedFrame = SceneResizedFrame,
            GameFirstFrame = GameFirstFrame,
            WorldPartition = CreateWorldPartitionObservation(),
            FailureMessage = FailureMessage,
            Checks = checks
        };
    }

    private EditorViewportSmokeChecks CreateChecks()
    {
        var sceneFirst = SceneFirstFrame;
        var sceneResized = SceneResizedFrame;
        var gameFirst = GameFirstFrame;
        return new EditorViewportSmokeChecks
        {
            RenderDocStartupExpectationMet =
                m_RenderDocAvailabilityObserved &&
                m_RenderDocAvailableAtStartup == m_ExpectRenderDocAtStartup,
            RenderDocRestartExpectationMet =
                !m_ExpectRenderDocRestart
                    ? !m_RenderDocRestartRequested && !m_RenderDocRestartCompleted
                    : m_RenderDocRestartRequested &&
                      m_RenderDocRestartCompleted &&
                      m_RenderDocAvailableAfterRestart &&
                      m_GraphicsGenerationBeforeRestart != 0 &&
                      m_GraphicsGenerationAfterRestart > m_GraphicsGenerationBeforeRestart,
            InteropResourceCachesBounded =
                m_MaxSceneImportedImageCount == RequiredImportedImagesPerViewport &&
                m_MaxSceneImportedSemaphoreCount == RequiredImportedSemaphoresPerViewport &&
                m_MaxGameImportedImageCount == RequiredImportedImagesPerViewport &&
                m_MaxGameImportedSemaphoreCount == RequiredImportedSemaphoresPerViewport,
            SceneFirstFramePresented = sceneFirst.HasValue,
            ScenePresentedBeforeGameViewActivation = m_ScenePresentedBeforeGameViewActivation,
            SceneResizeGenerationAdvanced =
                sceneFirst.HasValue &&
                sceneResized.HasValue &&
                sceneResized.Value.ResizeGeneration > sceneFirst.Value.ResizeGeneration,
            SceneOutputSizeChanged =
                sceneFirst.HasValue &&
                sceneResized.HasValue &&
                (sceneResized.Value.Width != sceneFirst.Value.Width ||
                 sceneResized.Value.Height != sceneFirst.Value.Height),
            SceneResizeStressPassed =
                m_SceneResizeRequestCount == RequiredSceneResizeTransitions &&
                m_SceneResizeTransitionCount == RequiredSceneResizeTransitions,
            SceneFrameConsumptionReported =
                sceneFirst.HasValue &&
                sceneResized.HasValue &&
                HasReportedConsumption(sceneFirst.Value) &&
                HasReportedConsumption(sceneResized.Value),
            SceneOrientationCorrect =
                sceneFirst.HasValue &&
                sceneResized.HasValue &&
                HasExpectedPresentationTransform(sceneFirst.Value) &&
                HasExpectedPresentationTransform(sceneResized.Value),
            GameFirstFramePresented = gameFirst.HasValue,
            GameFrameConsumptionReported = gameFirst.HasValue && HasReportedConsumption(gameFirst.Value),
            GameOrientationCorrect = gameFirst.HasValue && HasExpectedPresentationTransform(gameFirst.Value),
            ConcurrentSceneFramesPresented =
                m_ConcurrentSceneFrameCount >= RequiredConcurrentFramesPerViewport,
            ConcurrentGameFramesPresented =
                m_ConcurrentGameFrameCount >= RequiredConcurrentFramesPerViewport,
            PostRestartSceneFramesPresented =
                !m_ExpectRenderDocRestart ||
                m_PostRestartConcurrentSceneFrameCount >= RequiredConcurrentFramesPerViewport,
            PostRestartGameFramesPresented =
                !m_ExpectRenderDocRestart ||
                m_PostRestartConcurrentGameFrameCount >= RequiredConcurrentFramesPerViewport,
            TerrainPaintInteractionPassed =
                !m_TerrainPaintAvailable || m_TerrainPaintActivated,
            WorldVisibleOnFirstOpen =
                m_WorldGuid != Guid.Empty &&
                m_WorldCellCount > 0 &&
                m_WorldCellId != Guid.Empty,
            WorldOriginCellSelected =
                m_WorldCellX == 0 &&
                m_WorldCellY == 0 &&
                m_WorldCellZ == 0,
            WorldCellLoadObserved = m_WorldCellLoadRequested && m_WorldCellActiveObserved,
            WorldCellUnloadObserved = m_WorldCellUnloadRequested && m_WorldCellUnloadedObserved
        };
    }

    private EditorWorldPartitionSmokeObservation? CreateWorldPartitionObservation()
    {
        return m_WorldGuid == Guid.Empty
            ? null
            : new EditorWorldPartitionSmokeObservation
            {
                WorldGuid = m_WorldGuid,
                CellCount = m_WorldCellCount,
                CellId = m_WorldCellId,
                CellX = m_WorldCellX,
                CellY = m_WorldCellY,
                CellZ = m_WorldCellZ,
                LoadRequested = m_WorldCellLoadRequested,
                ActiveObserved = m_WorldCellActiveObserved,
                UnloadRequested = m_WorldCellUnloadRequested,
                UnloadedObserved = m_WorldCellUnloadedObserved
            };
    }

    private bool ValidateWorldCell(Guid cellId)
    {
        if (m_WorldCellId != Guid.Empty && cellId == m_WorldCellId)
        {
            return true;
        }

        Fail($"Editor world smoke observed unexpected cell '{cellId:D}'.");
        return false;
    }

    private static string? ValidateObservation(in EditorViewportPresentationObservation observation)
    {
        if (observation.SurfaceOwnershipGeneration <= 0 ||
            string.IsNullOrWhiteSpace(observation.SurfaceOwnershipOwnerId))
        {
            return $"{observation.ViewportKind} presented without logical surface ownership.";
        }

        if (observation.Ticket == 0)
        {
            return $"{observation.ViewportKind} reported a zero render ticket.";
        }

        if (observation.Width == 0 || observation.Height == 0)
        {
            return $"{observation.ViewportKind} reported an invalid output size.";
        }

        if (!HasReportedConsumption(observation))
        {
            return $"{observation.ViewportKind} did not report frame {observation.FrameIndex} as consumed.";
        }

        if (!HasExpectedPresentationTransform(observation))
        {
            var expectedScaleY = observation.RequiresVerticalFlip ? -1.0f : 1.0f;
            return $"{observation.ViewportKind} compositor transform is invalid. " +
                   $"Expected scale=(1,{expectedScaleY}) centered on the visual, received " +
                   $"scale=({observation.PresentationScaleX},{observation.PresentationScaleY}), " +
                   $"center=({observation.PresentationCenterX},{observation.PresentationCenterY}), " +
                   $"visual={observation.VisualWidth}x{observation.VisualHeight}.";
        }


        if (observation.ImportedImageCount is < 1 or > RequiredImportedImagesPerViewport ||
            observation.ImportedSemaphoreCount is < 2 or > RequiredImportedSemaphoresPerViewport ||
            (observation.ImportedSemaphoreCount & 1) != 0)
        {
            return $"{observation.ViewportKind} reported an invalid imported-resource cache size. " +
                   $"Images={observation.ImportedImageCount}, Semaphores={observation.ImportedSemaphoreCount}.";
        }

        return null;
    }

    private void ObserveImportedResourceCounts(in EditorViewportPresentationObservation observation)
    {
        if (observation.ViewportKind == EditorViewportKind.SceneView)
        {
            m_MaxSceneImportedImageCount = Math.Max(
                m_MaxSceneImportedImageCount,
                observation.ImportedImageCount);
            m_MaxSceneImportedSemaphoreCount = Math.Max(
                m_MaxSceneImportedSemaphoreCount,
                observation.ImportedSemaphoreCount);
            return;
        }

        m_MaxGameImportedImageCount = Math.Max(
            m_MaxGameImportedImageCount,
            observation.ImportedImageCount);
        m_MaxGameImportedSemaphoreCount = Math.Max(
            m_MaxGameImportedSemaphoreCount,
            observation.ImportedSemaphoreCount);
    }

    private static bool HasReportedConsumption(in EditorViewportPresentationObservation observation)
    {
        return observation.ConsumptionReported &&
               observation.LastConsumedFrameIndex >= observation.FrameIndex;
    }

    private static bool HasExpectedPresentationTransform(in EditorViewportPresentationObservation observation)
    {
        if (observation.VisualWidth <= 0.0f || observation.VisualHeight <= 0.0f)
        {
            return false;
        }

        var expectedScaleY = observation.RequiresVerticalFlip ? -1.0f : 1.0f;
        return NearlyEqual(observation.PresentationScaleX, 1.0f) &&
               NearlyEqual(observation.PresentationScaleY, expectedScaleY) &&
               NearlyEqual(observation.PresentationCenterX, observation.VisualWidth * 0.5f) &&
               NearlyEqual(observation.PresentationCenterY, observation.VisualHeight * 0.5f);
    }

    private bool MatchesConcurrentLayout(in EditorViewportPresentationObservation observation)
    {
        if (observation.ViewportKind == EditorViewportKind.SceneView)
        {
            return observation.Width == m_ExpectedConcurrentSceneWidth &&
                   observation.Height == m_ExpectedConcurrentSceneHeight &&
                   NearlyEqual(observation.VisualWidth, m_ExpectedConcurrentSceneVisualWidth) &&
                   NearlyEqual(observation.VisualHeight, m_ExpectedConcurrentSceneVisualHeight);
        }

        return observation.Width == m_ExpectedConcurrentGameWidth &&
               observation.Height == m_ExpectedConcurrentGameHeight &&
               NearlyEqual(observation.VisualWidth, m_ExpectedConcurrentGameVisualWidth) &&
               NearlyEqual(observation.VisualHeight, m_ExpectedConcurrentGameVisualHeight);
    }

    private static bool IsPositiveFinite(float value) => float.IsFinite(value) && value > 0.0f;

    private static bool NearlyEqual(float left, float right)
    {
        return MathF.Abs(left - right) <= TransformEpsilon;
    }

    private enum EditorViewportSmokeStage
    {
        WaitingForSceneFirstFrame,
        WaitingForSceneResize,
        WaitingForGameFirstFrame,
        WaitingForConcurrentFrames,
        WaitingForRenderDocRestart,
        WaitingForPostRestartConcurrentFrames,
        Complete,
        Failed
    }
}
