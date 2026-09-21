using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace ArisenEditor.Views;

/// <summary>
/// Cursor confinement for docked scene-view look. Avalonia reports absolute pointer positions, so
/// free-look needs the OS cursor pinned inside the viewport and re-centred between move events;
/// without it a look drag stops as soon as the pointer reaches a screen edge.
/// </summary>
/// <remarks>
/// Every entry point degrades to "no confinement" instead of failing: a rejected clip or cursor
/// placement only means the look drag behaves like an absolute drag. The clip is always released
/// through <see cref="Release"/> on pointer release, focus loss, capture loss and viewport
/// teardown so a clipped cursor can never outlive the drag.
/// </remarks>
internal static class EditorCursorCapture
{
    private const int CursorCenterRounding = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ClipCursor(ref NativeRect rect);

    [DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)]
    private static extern bool ReleaseCursorClip(IntPtr rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    public static bool TryClip(Visual viewport)
    {
        if (!OperatingSystem.IsWindows() || viewport.Bounds.Width < 1.0 || viewport.Bounds.Height < 1.0)
        {
            return false;
        }

        PixelPoint topLeft = viewport.PointToScreen(default);
        PixelPoint bottomRight = viewport.PointToScreen(
            new Point(viewport.Bounds.Width, viewport.Bounds.Height));
        if (bottomRight.X - topLeft.X <= 0 || bottomRight.Y - topLeft.Y <= 0)
        {
            return false;
        }

        var rect = new NativeRect
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = bottomRight.X,
            Bottom = bottomRight.Y
        };
        return ClipCursor(ref rect);
    }

    public static void Release()
    {
        if (OperatingSystem.IsWindows())
        {
            ReleaseCursorClip(IntPtr.Zero);
        }
    }

    /// <summary>
    /// Pins the cursor to the viewport centre. Returns false when the platform rejected the move,
    /// in which case look falls back to absolute pointer deltas.
    /// </summary>
    public static bool TryRecenter(Visual viewport)
    {
        if (!OperatingSystem.IsWindows() || viewport.Bounds.Width < 1.0 || viewport.Bounds.Height < 1.0)
        {
            return false;
        }

        PixelPoint center = viewport.PointToScreen(new Point(
            Math.Max(CursorCenterRounding, viewport.Bounds.Width * 0.5),
            Math.Max(CursorCenterRounding, viewport.Bounds.Height * 0.5)));
        return SetCursorPos(center.X, center.Y);
    }
}
