using System.Threading;

namespace ArisenEditor.Core.Services;

/// <summary>
/// Scene-view navigation keys. The set is published as a bit mask so the UI thread can update it
/// from pointer/key events while the engine frame reads it without allocating or locking.
/// </summary>
[Flags]
internal enum EditorSceneViewNavigationKeys
{
    None = 0,
    Forward = 1 << 0,
    Backward = 1 << 1,
    StrafeLeft = 1 << 2,
    StrafeRight = 1 << 3,
    Ascend = 1 << 4,
    Descend = 1 << 5,
    FastMove = 1 << 6
}

/// <summary>
/// One frame of scene-view navigation input. Mouse deltas are consumed by the reader; key and look
/// state are level-triggered so a dropped frame cannot leave the camera moving.
/// </summary>
internal readonly record struct EditorSceneViewNavigationSnapshot(
    EditorSceneViewNavigationKeys Keys,
    bool IsLooking,
    double LookDeltaX,
    double LookDeltaY,
    double DollyDelta)
{
    public bool HasLookInput => IsLooking && (LookDeltaX != 0.0 || LookDeltaY != 0.0);

    public bool HasMoveInput => Keys != EditorSceneViewNavigationKeys.None;

    public bool HasDollyInput => DollyDelta != 0.0;

    public bool HasInput => HasLookInput || HasMoveInput || HasDollyInput;
}

/// <summary>
/// Thread-safe carrier between the editor viewport (UI thread) and scene-view navigation (engine
/// frame). Absolute pointer deltas are accumulated here instead of polled from the presentation
/// loop so navigation keeps the exact motion the user produced.
/// </summary>
internal sealed class EditorSceneViewNavigationInput
{
    private int m_Active;
    private int m_Looking;
    private int m_Keys;
    private double m_LookDeltaX;
    private double m_LookDeltaY;
    private double m_DollyDelta;

    public bool IsActive => Volatile.Read(ref m_Active) != 0;

    public bool IsLooking => Volatile.Read(ref m_Looking) != 0;

    /// <summary>
    /// Marks whether a scene view is attached and consuming navigation. Deactivating drops every
    /// pending key and motion so a detached viewport cannot leave stale input behind.
    /// </summary>
    public void SetActive(bool active)
    {
        Volatile.Write(ref m_Active, active ? 1 : 0);
        if (!active)
        {
            Reset();
        }
    }

    public void SetLooking(bool looking)
    {
        Volatile.Write(ref m_Looking, looking ? 1 : 0);
        if (!looking)
        {
            Interlocked.Exchange(ref m_LookDeltaX, 0.0);
            Interlocked.Exchange(ref m_LookDeltaY, 0.0);
        }
    }

    public void SetKey(EditorSceneViewNavigationKeys key, bool down)
    {
        int mask = (int)key;
        if (mask == 0)
        {
            return;
        }

        while (true)
        {
            int current = Volatile.Read(ref m_Keys);
            int updated = down ? current | mask : current & ~mask;
            if (updated == current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref m_Keys, updated, current) == current)
            {
                return;
            }
        }
    }

    public void AddLookDelta(double deltaX, double deltaY)
    {
        if (!double.IsFinite(deltaX) || !double.IsFinite(deltaY))
        {
            return;
        }

        Accumulate(ref m_LookDeltaX, deltaX);
        Accumulate(ref m_LookDeltaY, deltaY);
    }

    public void AddDollyDelta(double delta)
    {
        if (!double.IsFinite(delta))
        {
            return;
        }

        Accumulate(ref m_DollyDelta, delta);
    }

    public void Reset()
    {
        Volatile.Write(ref m_Keys, 0);
        Volatile.Write(ref m_Looking, 0);
        Interlocked.Exchange(ref m_LookDeltaX, 0.0);
        Interlocked.Exchange(ref m_LookDeltaY, 0.0);
        Interlocked.Exchange(ref m_DollyDelta, 0.0);
    }

    /// <summary>
    /// Takes the motion produced since the previous frame. Returns false while no scene view owns
    /// navigation, in which case no input is consumed.
    /// </summary>
    public bool TryConsume(out EditorSceneViewNavigationSnapshot snapshot)
    {
        if (!IsActive)
        {
            snapshot = default;
            return false;
        }

        double lookDeltaX = Interlocked.Exchange(ref m_LookDeltaX, 0.0);
        double lookDeltaY = Interlocked.Exchange(ref m_LookDeltaY, 0.0);
        double dollyDelta = Interlocked.Exchange(ref m_DollyDelta, 0.0);
        snapshot = new EditorSceneViewNavigationSnapshot(
            (EditorSceneViewNavigationKeys)Volatile.Read(ref m_Keys),
            IsLooking,
            lookDeltaX,
            lookDeltaY,
            dollyDelta);
        return true;
    }

    private static void Accumulate(ref double target, double value)
    {
        if (value == 0.0)
        {
            return;
        }

        double current = Interlocked.CompareExchange(ref target, 0.0, 0.0);
        while (true)
        {
            double updated = current + value;
            double observed = Interlocked.CompareExchange(ref target, updated, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
