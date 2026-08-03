using System;
using System.ComponentModel;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using ArisenEditor.Core.Services;
using ReactiveUI;
using ArisenEngine.Rendering;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;

namespace ArisenEditor.ViewModels;

public class EditorViewportViewModel : ReactiveObject, IDisposable
{
    private const double MarkerDiameter = 20.0;
    private const double MarkerMargin = 8.0;
    private const float ProjectionEpsilon = 0.0001f;

    private readonly ISelectionService? m_SelectionService;
    private SceneAssetEntityNodeViewModel? m_SelectedSceneAssetEntity;
    private double m_ViewportWidth = 1.0;
    private double m_ViewportHeight = 1.0;
    private bool m_HasSceneEntitySelection;
    private bool m_IsSelectionMarkerVisible;
    private double m_SelectionMarkerLeft;
    private double m_SelectionMarkerTop;
    private string m_SelectedSceneEntityName = string.Empty;
    private string m_SelectedSceneEntitySummary = string.Empty;
    private string m_SelectedSceneEntityPosition = string.Empty;
    private bool m_IsRenderDocCaptureAvailable;
    private string m_RenderDocActionText = "Enable RenderDoc";
    private string m_RenderDocCaptureStatus = string.Empty;
    private bool m_RenderDocActionInProgress;
    private readonly IGraphicsDeviceLifecycleService? m_GraphicsDeviceLifecycle;
    private readonly RenderDocService m_RenderDoc = RenderDocService.Instance;
    private RenderSurfaceRegistration m_RenderSurfaceRegistration;
    private IImage? _viewportImage;

    /// <summary>
    /// The Image surface that the Shared GPU Texture will be bound to.
    /// Avalonia binds directly to this property.
    /// </summary>
    public IImage? ViewportImage
    {
        get => _viewportImage;
        set => this.RaiseAndSetIfChanged(ref _viewportImage, value);
    }
    
    public bool IsSceneView { get; }

    public bool HasSceneEntitySelection
    {
        get => m_HasSceneEntitySelection;
        private set => this.RaiseAndSetIfChanged(ref m_HasSceneEntitySelection, value);
    }

    public bool IsSelectionMarkerVisible
    {
        get => m_IsSelectionMarkerVisible;
        private set => this.RaiseAndSetIfChanged(ref m_IsSelectionMarkerVisible, value);
    }

    public double SelectionMarkerLeft
    {
        get => m_SelectionMarkerLeft;
        private set => this.RaiseAndSetIfChanged(ref m_SelectionMarkerLeft, value);
    }

    public double SelectionMarkerTop
    {
        get => m_SelectionMarkerTop;
        private set => this.RaiseAndSetIfChanged(ref m_SelectionMarkerTop, value);
    }

    public string SelectedSceneEntityName
    {
        get => m_SelectedSceneEntityName;
        private set => this.RaiseAndSetIfChanged(ref m_SelectedSceneEntityName, value);
    }

    public string SelectedSceneEntitySummary
    {
        get => m_SelectedSceneEntitySummary;
        private set => this.RaiseAndSetIfChanged(ref m_SelectedSceneEntitySummary, value);
    }

    public string SelectedSceneEntityPosition
    {
        get => m_SelectedSceneEntityPosition;
        private set => this.RaiseAndSetIfChanged(ref m_SelectedSceneEntityPosition, value);
    }

    private Color _clearColor = Color.FromRgb(255, 102, 178); // Pink
    public Color ClearColor
    {
        get => _clearColor;
        set => this.RaiseAndSetIfChanged(ref _clearColor, value);
    }

    public bool IsRenderDocCaptureAvailable
    {
        get => m_IsRenderDocCaptureAvailable;
        private set => this.RaiseAndSetIfChanged(ref m_IsRenderDocCaptureAvailable, value);
    }

    public string RenderDocCaptureStatus
    {
        get => m_RenderDocCaptureStatus;
        private set => this.RaiseAndSetIfChanged(ref m_RenderDocCaptureStatus, value);
    }

    public string RenderDocActionText
    {
        get => m_RenderDocActionText;
        private set => this.RaiseAndSetIfChanged(ref m_RenderDocActionText, value);
    }

    public async Task ExecuteRenderDocActionAsync()
    {
        if (!IsRenderDocCaptureAvailable || m_RenderDocActionInProgress)
        {
            return;
        }

        if (!m_RenderSurfaceRegistration.IsValid)
        {
            RefreshRenderDocCaptureState();
            return;
        }

        if (m_RenderDoc.IsAvailable)
        {
            m_RenderDoc.TryTriggerCapture(m_RenderSurfaceRegistration);
            RefreshRenderDocCaptureState();
            return;
        }

        if (m_GraphicsDeviceLifecycle == null)
        {
            RefreshRenderDocCaptureState();
            return;
        }

        m_RenderDocActionInProgress = true;
        RefreshRenderDocCaptureState();
        try
        {
            GraphicsDeviceRestartResult result = await m_GraphicsDeviceLifecycle.RestartAsync(
                new RHIBackendRestartOptions(RHIBackendDiagnosticMode.RenderDoc));
            if (!result.Succeeded)
            {
                KernelLog.Error(
                    $"[EditorViewport] RenderDoc graphics restart failed: {result.Diagnostic}");
            }
        }
        catch (Exception exception)
        {
            KernelLog.Error(
                $"[EditorViewport] RenderDoc graphics restart failed: {exception.Message}");
        }
        finally
        {
            m_RenderDocActionInProgress = false;
            RefreshRenderDocCaptureState();
        }
    }

    public EditorViewportViewModel(bool isSceneView, ISelectionService? selectionService = null)
    {
        IsSceneView = isSceneView;
        if (EngineKernel.Instance.Services.TryGetService<IGraphicsDeviceLifecycleService>(
                out var graphicsDeviceLifecycle))
        {
            m_GraphicsDeviceLifecycle = graphicsDeviceLifecycle;
        }
        if (m_GraphicsDeviceLifecycle != null)
        {
            m_GraphicsDeviceLifecycle.StateChanged += OnGraphicsDeviceLifecycleStateChanged;
        }
        m_RenderDoc.CaptureStateChanged += OnRenderDocCaptureStateChanged;
        RefreshRenderDocCaptureState();
        m_SelectionService = isSceneView ? selectionService : null;
        if (m_SelectionService != null)
        {
            m_SelectionService.SelectionChanged += OnSelectionChanged;
            OnSelectionChanged(m_SelectionService.CurrentSelection);
        }

        // In the future, this is where we will hook up `Avalonia.Platform.Interop.IExternalMemory`
        // or a `WriteableBitmap` bound to the Shared Handle exported by Arisen Engine's RHI.
    }

    private void RefreshRenderDocCaptureState()
    {
        bool renderDocAvailable = m_RenderDoc.IsAvailable;
        RenderDocCaptureRequestSnapshot capture = m_RenderDoc.CaptureRequest;
        GraphicsDeviceLifecycleSnapshot? lifecycle = m_GraphicsDeviceLifecycle?.Snapshot;
        bool lifecycleRunning = lifecycle == null ||
            lifecycle.Value.State == GraphicsDeviceLifecycleState.Running;

        RenderDocActionText = renderDocAvailable ? "Capture Frame" : "Enable RenderDoc";
        IsRenderDocCaptureAvailable =
            !m_RenderDocActionInProgress &&
            lifecycleRunning &&
            m_RenderSurfaceRegistration.IsValid &&
            !capture.IsActive &&
            (renderDocAvailable || m_GraphicsDeviceLifecycle != null);

        if (lifecycle.HasValue &&
            lifecycle.Value.State != GraphicsDeviceLifecycleState.Running)
        {
            GraphicsDeviceLifecycleSnapshot snapshot = lifecycle.Value;
            RenderDocCaptureStatus = snapshot.State == GraphicsDeviceLifecycleState.Failed
                ? $"Graphics restart failed: {snapshot.Diagnostic}"
                : $"Graphics device: {snapshot.State}.";
        }
        else if (!m_RenderSurfaceRegistration.IsValid)
        {
            RenderDocCaptureStatus = "Viewport render surface is not ready.";
        }
        else if (capture.IsActive)
        {
            RenderDocCaptureStatus = capture.Target == m_RenderSurfaceRegistration
                ? $"Capture request #{capture.RequestId} is {capture.Status.ToString().ToLowerInvariant()} for this viewport."
                : $"Capture request #{capture.RequestId} is active for another viewport.";
        }
        else if (capture.IsTerminal &&
                 capture.Target == m_RenderSurfaceRegistration)
        {
            RenderDocCaptureStatus = capture.Status == RenderDocCaptureRequestStatus.Succeeded
                ? $"Capture request #{capture.RequestId} completed."
                : $"Capture request #{capture.RequestId} failed at {capture.FailureStage}: {capture.Diagnostic}";
        }
        else if (!renderDocAvailable && m_GraphicsDeviceLifecycle != null)
        {
            RenderDocCaptureStatus =
                "Enable RenderDoc by recreating the graphics device while keeping the Editor open.";
        }
        else
        {
            RenderDocCaptureStatus = m_RenderDoc.AvailabilityDiagnostic;
        }
    }

    private void OnGraphicsDeviceLifecycleStateChanged(
        GraphicsDeviceLifecycleSnapshot snapshot)
    {
        RefreshRenderDocCaptureStateOnUIThread();
    }

    private void OnRenderDocCaptureStateChanged(
        RenderDocCaptureRequestSnapshot snapshot)
    {
        RefreshRenderDocCaptureStateOnUIThread();
    }

    private void RefreshRenderDocCaptureStateOnUIThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshRenderDocCaptureState();
            return;
        }

        Dispatcher.UIThread.Post(RefreshRenderDocCaptureState, DispatcherPriority.Loaded);
    }

    public void SetRenderSurfaceRegistration(RenderSurfaceRegistration registration)
    {
        if (m_RenderSurfaceRegistration == registration)
        {
            return;
        }

        m_RenderSurfaceRegistration = registration;
        RefreshRenderDocCaptureState();
    }

    public void SetViewportSize(double width, double height)
    {
        var safeWidth = Math.Max(1.0, width);
        var safeHeight = Math.Max(1.0, height);
        if (Math.Abs(m_ViewportWidth - safeWidth) < 0.5 &&
            Math.Abs(m_ViewportHeight - safeHeight) < 0.5)
        {
            return;
        }

        m_ViewportWidth = safeWidth;
        m_ViewportHeight = safeHeight;
        UpdateSelectionProjection();
    }

    public void Dispose()
    {
        if (m_GraphicsDeviceLifecycle != null)
        {
            m_GraphicsDeviceLifecycle.StateChanged -= OnGraphicsDeviceLifecycleStateChanged;
        }
        m_RenderDoc.CaptureStateChanged -= OnRenderDocCaptureStateChanged;
        if (m_SelectionService != null)
        {
            m_SelectionService.SelectionChanged -= OnSelectionChanged;
        }

        SetSelectedSceneEntity(null);
    }

    private void OnSelectionChanged(object? selection)
    {
        SetSelectedSceneEntity(selection as SceneAssetEntityNodeViewModel);
    }

    private void SetSelectedSceneEntity(SceneAssetEntityNodeViewModel? node)
    {
        if (ReferenceEquals(m_SelectedSceneAssetEntity, node))
        {
            RefreshSelectionOverlay();
            return;
        }

        if (m_SelectedSceneAssetEntity != null)
        {
            m_SelectedSceneAssetEntity.PropertyChanged -= OnSelectedSceneEntityPropertyChanged;
        }

        m_SelectedSceneAssetEntity = node;
        if (m_SelectedSceneAssetEntity != null)
        {
            m_SelectedSceneAssetEntity.PropertyChanged += OnSelectedSceneEntityPropertyChanged;
        }

        RefreshSelectionOverlay();
    }

    private void OnSelectedSceneEntityPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            string.Equals(e.PropertyName, nameof(SceneAssetEntityNodeViewModel.Entity), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(SceneAssetEntityNodeViewModel.Name), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "Position", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "Rotation", StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, "Scale", StringComparison.Ordinal))
        {
            RefreshSelectionOverlay();
        }
    }

    private void RefreshSelectionOverlay()
    {
        var node = m_SelectedSceneAssetEntity;
        HasSceneEntitySelection = node != null;

        if (node == null)
        {
            SelectedSceneEntityName = string.Empty;
            SelectedSceneEntitySummary = string.Empty;
            SelectedSceneEntityPosition = string.Empty;
            IsSelectionMarkerVisible = false;
            return;
        }

        SelectedSceneEntityName = node.Name;
        SelectedSceneEntitySummary = node.ComponentSummary;
        SelectedSceneEntityPosition = FormatPosition(node.Entity.Transform.Position);
        UpdateSelectionProjection();
    }

    private void UpdateSelectionProjection()
    {
        if (m_SelectedSceneAssetEntity == null ||
            !TryProjectSelectedEntity(m_SelectedSceneAssetEntity, out var markerLeft, out var markerTop))
        {
            IsSelectionMarkerVisible = false;
            return;
        }

        SelectionMarkerLeft = markerLeft;
        SelectionMarkerTop = markerTop;
        IsSelectionMarkerVisible = true;
    }

    private bool TryProjectSelectedEntity(
        SceneAssetEntityNodeViewModel node,
        out double markerLeft,
        out double markerTop)
    {
        markerLeft = 0.0;
        markerTop = 0.0;

        if (m_ViewportWidth <= 1.0 || m_ViewportHeight <= 1.0)
        {
            return false;
        }

        var cameraEntity = FindFirstCamera(node.Scene.Entities);
        if (cameraEntity?.Camera == null || !cameraEntity.Camera.IsPerspective)
        {
            return false;
        }

        var camera = cameraEntity.Camera;
        var cameraTransform = cameraEntity.Transform;
        var euler = QuaternionToEulerDegrees(cameraTransform.Rotation);
        var rotation = Matrix4x4.CreateFromYawPitchRoll(
            euler.Y * MathF.PI / 180.0f,
            euler.X * MathF.PI / 180.0f,
            euler.Z * MathF.PI / 180.0f);
        var forward = Vector3.Transform(new Vector3(0.0f, 0.0f, 1.0f), rotation);
        var up = Vector3.Transform(Vector3.UnitY, rotation);

        if (forward.LengthSquared() < ProjectionEpsilon ||
            up.LengthSquared() < ProjectionEpsilon)
        {
            return false;
        }

        var aspectRatio = (float)(m_ViewportWidth / m_ViewportHeight);
        var view = Matrix4x4.CreateLookAt(
            cameraTransform.Position,
            cameraTransform.Position + forward,
            up);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            camera.VerticalFov * MathF.PI / 180.0f,
            aspectRatio,
            Math.Max(camera.NearPlane, ProjectionEpsilon),
            Math.Max(camera.FarPlane, camera.NearPlane + ProjectionEpsilon));

        var targetPosition = GetSelectionTargetPosition(node.Entity);
        var clip = Vector4.Transform(new Vector4(targetPosition, 1.0f), view * projection);
        if (clip.W <= ProjectionEpsilon)
        {
            return false;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        if (ndcX < -1.05f || ndcX > 1.05f || ndcY < -1.05f || ndcY > 1.05f)
        {
            return false;
        }

        var screenX = (ndcX * 0.5f + 0.5f) * m_ViewportWidth;
        var screenY = (-ndcY * 0.5f + 0.5f) * m_ViewportHeight;
        markerLeft = ClampToViewport(screenX - MarkerDiameter * 0.5, MarkerMargin, m_ViewportWidth - MarkerDiameter - MarkerMargin);
        markerTop = ClampToViewport(screenY - MarkerDiameter * 0.5, MarkerMargin, m_ViewportHeight - MarkerDiameter - MarkerMargin);
        return true;
    }

    private static ArisenEngine.Resources.Serialization.SceneEntityInspection? FindFirstCamera(
        System.Collections.Generic.IReadOnlyList<ArisenEngine.Resources.Serialization.SceneEntityInspection> entities)
    {
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i].Camera != null)
            {
                return entities[i];
            }
        }

        return null;
    }

    private static Vector3 GetSelectionTargetPosition(
        ArisenEngine.Resources.Serialization.SceneEntityInspection entity)
    {
        var transform = entity.Transform;
        if (entity.MeshRenderer == null)
        {
            return transform.Position;
        }

        var localCenter = entity.MeshRenderer.BoundsCenter;
        var scaledCenter = localCenter * transform.Scale;
        return transform.Position + Vector3.Transform(scaledCenter, transform.Rotation);
    }

    private static double ClampToViewport(double value, double min, double max)
    {
        if (max < min)
        {
            return min;
        }

        return Math.Min(max, Math.Max(min, value));
    }

    private static string FormatPosition(Vector3 position)
    {
        return $"Position {position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}";
    }

    private static Vector3 QuaternionToEulerDegrees(Quaternion q)
    {
        var sinRcosP = 2.0f * (q.W * q.Z + q.X * q.Y);
        var cosRcosP = 1.0f - 2.0f * (q.Y * q.Y + q.Z * q.Z);
        var roll = MathF.Atan2(sinRcosP, cosRcosP);

        var sinP = 2.0f * (q.W * q.X - q.Z * q.Y);
        float pitch;
        if (MathF.Abs(sinP) >= 1.0f)
        {
            pitch = MathF.CopySign(MathF.PI / 2.0f, sinP);
        }
        else
        {
            pitch = MathF.Asin(sinP);
        }

        var sinYcosP = 2.0f * (q.W * q.Y + q.Z * q.X);
        var cosYcosP = 1.0f - 2.0f * (q.X * q.X + q.Y * q.Y);
        var yaw = MathF.Atan2(sinYcosP, cosYcosP);
        const float radToDeg = 180.0f / MathF.PI;
        return new Vector3(pitch * radToDeg, yaw * radToDeg, roll * radToDeg);
    }
}
