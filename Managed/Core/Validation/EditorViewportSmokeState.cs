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
    float VisualHeight);

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
    public bool SceneFirstFramePresented { get; init; }
    public bool ScenePresentedBeforeGameViewActivation { get; init; }
    public bool SceneResizeGenerationAdvanced { get; init; }
    public bool SceneOutputSizeChanged { get; init; }
    public bool SceneFrameConsumptionReported { get; init; }
    public bool SceneOrientationCorrect { get; init; }
    public bool GameFirstFramePresented { get; init; }
    public bool GameFrameConsumptionReported { get; init; }
    public bool GameOrientationCorrect { get; init; }
    public bool WorldVisibleOnFirstOpen { get; init; }
    public bool WorldCellLoadObserved { get; init; }
    public bool WorldCellUnloadObserved { get; init; }

    public bool Passed =>
        SceneFirstFramePresented &&
        ScenePresentedBeforeGameViewActivation &&
        SceneResizeGenerationAdvanced &&
        SceneOutputSizeChanged &&
        SceneFrameConsumptionReported &&
        SceneOrientationCorrect &&
        GameFirstFramePresented &&
        GameFrameConsumptionReported &&
        GameOrientationCorrect &&
        WorldVisibleOnFirstOpen &&
        WorldCellLoadObserved &&
        WorldCellUnloadObserved;
}

public sealed class EditorWorldPartitionSmokeObservation
{
    public Guid WorldGuid { get; init; }
    public int CellCount { get; init; }
    public Guid CellId { get; init; }
    public bool LoadRequested { get; init; }
    public bool ActiveObserved { get; init; }
    public bool UnloadRequested { get; init; }
    public bool UnloadedObserved { get; init; }
}

public sealed class EditorViewportSmokeArtifact
{
    public int SchemaVersion { get; init; } = 2;
    public string CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow.ToString("O");
    public required string Profile { get; init; }
    public int TimeoutSeconds { get; init; }
    public int GameViewActivationCount { get; init; }
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
    private const float TransformEpsilon = 0.01f;

    private EditorViewportSmokeStage m_Stage = EditorViewportSmokeStage.WaitingForSceneFirstFrame;
    private bool m_ScenePresentedBeforeGameViewActivation;
    private int m_GameViewActivationCount;
    private Guid m_WorldGuid;
    private int m_WorldCellCount;
    private Guid m_WorldCellId;
    private bool m_WorldCellLoadRequested;
    private bool m_WorldCellActiveObserved;
    private bool m_WorldCellUnloadRequested;
    private bool m_WorldCellUnloadedObserved;

    public EditorViewportPresentationObservation? SceneFirstFrame { get; private set; }
    public EditorViewportPresentationObservation? SceneResizedFrame { get; private set; }
    public EditorViewportPresentationObservation? GameFirstFrame { get; private set; }
    public string? FailureMessage { get; private set; }
    public bool IsComplete =>
        m_Stage == EditorViewportSmokeStage.Failed ||
        (m_Stage == EditorViewportSmokeStage.Complete && m_WorldCellUnloadedObserved);
    public bool Succeeded =>
        m_Stage == EditorViewportSmokeStage.Complete &&
        m_WorldCellUnloadedObserved;

    public EditorViewportSmokeAction Observe(in EditorViewportPresentationObservation observation)
    {
        if (IsComplete)
        {
            return EditorViewportSmokeAction.None;
        }

        var validationFailure = ValidateObservation(observation);
        if (validationFailure != null)
        {
            return Fail(validationFailure);
        }

        switch (m_Stage)
        {
            case EditorViewportSmokeStage.WaitingForSceneFirstFrame:
                if (observation.ViewportKind != EditorViewportKind.SceneView)
                {
                    return Fail("GameView presented before the initial SceneView frame.");
                }

                SceneFirstFrame = observation;
                m_ScenePresentedBeforeGameViewActivation = m_GameViewActivationCount == 0;
                m_Stage = EditorViewportSmokeStage.WaitingForSceneResize;
                return EditorViewportSmokeAction.ResizeSceneView;

            case EditorViewportSmokeStage.WaitingForSceneResize:
                if (observation.ViewportKind != EditorViewportKind.SceneView)
                {
                    return Fail("GameView presented before the SceneView resize completed.");
                }

                var firstSceneFrame = SceneFirstFrame!.Value;
                if (observation.ResizeGeneration <= firstSceneFrame.ResizeGeneration ||
                    (observation.Width == firstSceneFrame.Width && observation.Height == firstSceneFrame.Height))
                {
                    return EditorViewportSmokeAction.None;
                }

                SceneResizedFrame = observation;
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
                m_Stage = EditorViewportSmokeStage.Complete;
                return IsComplete
                    ? EditorViewportSmokeAction.Complete
                    : EditorViewportSmokeAction.None;

            default:
                return EditorViewportSmokeAction.None;
        }
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

    public void ObserveWorldFirstOpen(Guid worldGuid, int cellCount, Guid cellId)
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
            GameViewActivationCount = m_GameViewActivationCount,
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
            WorldVisibleOnFirstOpen =
                m_WorldGuid != Guid.Empty &&
                m_WorldCellCount > 0 &&
                m_WorldCellId != Guid.Empty,
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

        return null;
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

    private static bool NearlyEqual(float left, float right)
    {
        return MathF.Abs(left - right) <= TransformEpsilon;
    }

    private enum EditorViewportSmokeStage
    {
        WaitingForSceneFirstFrame,
        WaitingForSceneResize,
        WaitingForGameFirstFrame,
        Complete,
        Failed
    }
}
