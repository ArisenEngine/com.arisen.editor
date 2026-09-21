using System;
using Avalonia;
using Avalonia.Input;
using ArisenEditor.Core.Services;
using ArisenEditor.Core.Validation;
using ArisenKernel.Lifecycle;

namespace ArisenEditor.Views;

/// <summary>
/// Interactive scene-view navigation for the docked viewport: right mouse button to look, W/A/S/D
/// to fly, E/Q for up and down, Shift for speed, wheel to dolly.
/// </summary>
/// <remarks>
/// The viewport only collects input. Turning it into a camera pose happens on the engine frame in
/// <see cref="EditorSceneViewCameraController"/>, so look speed and movement speed are frame-rate
/// independent and the camera state has exactly one owner. Look is a right-button drag: the cursor
/// is confined to the viewport and re-centred between move events, which is why the drag keeps
/// working past a screen edge. Only scene views navigate; the game view stays a pure presentation.
/// </remarks>
public partial class ArisenViewportControl
{
    private static readonly Cursor s_HiddenCursor = new(StandardCursorType.None);

    private EditorSceneViewNavigationInput? _sceneViewNavigation;
    private bool _isLooking;
    private Point _lookReferencePoint;

    private bool IsSceneViewport => _viewportKind == EditorViewportKind.SceneView;

    private void AttachSceneViewNavigation()
    {
        if (_sceneViewNavigation != null || !IsSceneViewport || !EngineKernel.IsCreated)
        {
            return;
        }

        if (!EngineKernel.Instance.Services.TryGetService<EditorSceneViewNavigationInput>(
                out EditorSceneViewNavigationInput? input) ||
            input == null)
        {
            return;
        }

        _sceneViewNavigation = input;
        Focusable = true;
        input.SetActive(true);
    }

    private void DetachSceneViewNavigation()
    {
        EditorSceneViewNavigationInput? input = _sceneViewNavigation;
        _sceneViewNavigation = null;
        EndLook();
        input?.SetActive(false);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        EditorSceneViewNavigationInput? input = _sceneViewNavigation;
        if (input == null || e.Handled)
        {
            return;
        }

        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        _isLooking = true;
        input.SetLooking(true);
        e.Pointer.Capture(this);
        EditorCursorCapture.TryClip(this);
        _lookReferencePoint = EditorCursorCapture.TryRecenter(this)
            ? GetLookCenterPoint()
            : e.GetPosition(this);
        Cursor = s_HiddenCursor;
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        EditorSceneViewNavigationInput? input = _sceneViewNavigation;
        if (input == null || !_isLooking)
        {
            return;
        }

        Point position = e.GetPosition(this);
        double deltaX = position.X - _lookReferencePoint.X;
        double deltaY = position.Y - _lookReferencePoint.Y;
        if (deltaX != 0.0 || deltaY != 0.0)
        {
            input.AddLookDelta(deltaX, deltaY);
        }

        _lookReferencePoint = EditorCursorCapture.TryRecenter(this)
            ? GetLookCenterPoint()
            : position;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isLooking)
        {
            return;
        }

        if (e.InitialPressMouseButton != MouseButton.Right)
        {
            return;
        }

        e.Pointer.Capture(null);
        EndLook();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_isLooking)
        {
            EndLook();
        }
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        _sceneViewNavigation?.Reset();
        if (_isLooking)
        {
            EndLook();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!TryApplyNavigationKey(e.Key, down: true))
        {
            return;
        }

        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!TryApplyNavigationKey(e.Key, down: false))
        {
            return;
        }

        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        EditorSceneViewNavigationInput? input = _sceneViewNavigation;
        if (input == null || e.Delta.Y == 0.0)
        {
            return;
        }

        input.AddDollyDelta(e.Delta.Y);
        e.Handled = true;
    }

    private void EndLook()
    {
        _isLooking = false;
        _sceneViewNavigation?.SetLooking(false);
        EditorCursorCapture.Release();
        Cursor = null;
    }

    private Point GetLookCenterPoint() => new(Bounds.Width * 0.5, Bounds.Height * 0.5);

    private bool TryApplyNavigationKey(Key key, bool down)
    {
        EditorSceneViewNavigationInput? input = _sceneViewNavigation;
        if (input == null || !TryMapNavigationKey(key, out EditorSceneViewNavigationKeys navigationKey))
        {
            return false;
        }

        input.SetKey(navigationKey, down);
        return true;
    }

    private static bool TryMapNavigationKey(
        Key key,
        out EditorSceneViewNavigationKeys navigationKey)
    {
        switch (key)
        {
            case Key.W:
                navigationKey = EditorSceneViewNavigationKeys.Forward;
                return true;
            case Key.S:
                navigationKey = EditorSceneViewNavigationKeys.Backward;
                return true;
            case Key.A:
                navigationKey = EditorSceneViewNavigationKeys.StrafeLeft;
                return true;
            case Key.D:
                navigationKey = EditorSceneViewNavigationKeys.StrafeRight;
                return true;
            case Key.E:
                navigationKey = EditorSceneViewNavigationKeys.Ascend;
                return true;
            case Key.Q:
                navigationKey = EditorSceneViewNavigationKeys.Descend;
                return true;
            case Key.LeftShift:
            case Key.RightShift:
                navigationKey = EditorSceneViewNavigationKeys.FastMove;
                return true;
            default:
                navigationKey = EditorSceneViewNavigationKeys.None;
                return false;
        }
    }
}
