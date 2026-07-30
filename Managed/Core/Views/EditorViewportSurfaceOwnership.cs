using System;
using System.Threading;
using System.Threading.Tasks;
using ArisenEditor.Core.Validation;

namespace ArisenEditor.Views;

internal readonly record struct EditorViewportSurfaceOwnershipSnapshot(
    EditorViewportKind ViewportKind,
    bool IsOwned,
    long Generation,
    string OwnerId);

internal sealed class EditorViewportSurfaceOwnership
{
    private sealed class Slot
    {
        public readonly object Gate = new();
        public readonly SemaphoreSlim Availability = new(1, 1);
        public long LastGeneration;
        public long OwnerGeneration;
        public string OwnerId = string.Empty;
    }

    private readonly Slot m_SceneView = new();
    private readonly Slot m_GameView = new();

    public static EditorViewportSurfaceOwnership Shared { get; } = new();

    public async ValueTask<EditorViewportSurfaceLease> AcquireAsync(
        EditorViewportKind viewportKind,
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        Slot slot = GetSlot(viewportKind);
        await slot.Availability.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (slot.Gate)
            {
                if (slot.OwnerGeneration != 0)
                {
                    throw new InvalidOperationException(
                        $"Viewport ownership for '{viewportKind}' was granted while generation {slot.OwnerGeneration} is still active.");
                }

                long generation = checked(slot.LastGeneration + 1);
                slot.LastGeneration = generation;
                slot.OwnerGeneration = generation;
                slot.OwnerId = ownerId;
                return new EditorViewportSurfaceLease(
                    this,
                    viewportKind,
                    generation,
                    ownerId);
            }
        }
        catch
        {
            slot.Availability.Release();
            throw;
        }
    }

    public EditorViewportSurfaceOwnershipSnapshot GetSnapshot(
        EditorViewportKind viewportKind)
    {
        Slot slot = GetSlot(viewportKind);
        lock (slot.Gate)
        {
            return new EditorViewportSurfaceOwnershipSnapshot(
                viewportKind,
                slot.OwnerGeneration != 0,
                slot.OwnerGeneration,
                slot.OwnerId);
        }
    }

    internal void Release(
        EditorViewportKind viewportKind,
        long generation)
    {
        Slot slot = GetSlot(viewportKind);
        bool released = false;
        lock (slot.Gate)
        {
            if (slot.OwnerGeneration == generation)
            {
                slot.OwnerGeneration = 0;
                slot.OwnerId = string.Empty;
                released = true;
            }
        }

        if (released)
        {
            slot.Availability.Release();
        }
    }

    private Slot GetSlot(EditorViewportKind viewportKind)
    {
        return viewportKind switch
        {
            EditorViewportKind.SceneView => m_SceneView,
            EditorViewportKind.GameView => m_GameView,
            _ => throw new ArgumentOutOfRangeException(
                nameof(viewportKind),
                viewportKind,
                "Unknown Editor viewport kind.")
        };
    }
}

internal sealed class EditorViewportSurfaceLease : IDisposable
{
    private EditorViewportSurfaceOwnership? m_Owner;

    internal EditorViewportSurfaceLease(
        EditorViewportSurfaceOwnership owner,
        EditorViewportKind viewportKind,
        long generation,
        string ownerId)
    {
        m_Owner = owner;
        ViewportKind = viewportKind;
        Generation = generation;
        OwnerId = ownerId;
    }

    public EditorViewportKind ViewportKind { get; }

    public long Generation { get; }

    public string OwnerId { get; }

    public void Dispose()
    {
        EditorViewportSurfaceOwnership? owner = Interlocked.Exchange(
            ref m_Owner,
            null);
        owner?.Release(ViewportKind, Generation);
    }
}
