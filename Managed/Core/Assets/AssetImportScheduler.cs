using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArisenEditor.Core.Assets;

internal enum AssetImportWorkKind
{
    Created,
    Changed,
    Deleted,
    Renamed
}

internal readonly record struct AssetImportRequest(
    AssetImportWorkKind Kind,
    string FullPath,
    string OldFullPath = "");

internal sealed class AssetImportScheduler : IDisposable
{
    private sealed class PendingImport
    {
        public required AssetImportRequest Request;
        public required DateTime DueTimeUtc;
    }

    private readonly object m_Lock = new();
    private readonly Dictionary<string, PendingImport> m_Pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<AssetImportRequest, bool> m_Process;
    private readonly TimeSpan m_DebounceDelay;
    private readonly TimeSpan m_RetryDelay;
    private readonly int m_MaxAttempts;
    private readonly CancellationTokenSource m_Cancellation = new();
    private readonly SemaphoreSlim m_Signal = new(0);
    private readonly Task m_Worker;

    public AssetImportScheduler(
        Func<AssetImportRequest, bool> process,
        TimeSpan? debounceDelay = null,
        TimeSpan? retryDelay = null,
        int maxAttempts = 5)
    {
        m_Process = process ?? throw new ArgumentNullException(nameof(process));
        m_DebounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(150);
        m_RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
        m_MaxAttempts = Math.Max(1, maxAttempts);
        m_Worker = Task.Run(ProcessLoopAsync);
    }

    public void Enqueue(AssetImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullPath))
        {
            return;
        }

        var normalizedRequest = Normalize(request);
        var key = CreateKey(normalizedRequest);
        lock (m_Lock)
        {
            if (m_Pending.TryGetValue(key, out var pending))
            {
                pending.Request = Coalesce(pending.Request, normalizedRequest);
                pending.DueTimeUtc = DateTime.UtcNow + m_DebounceDelay;
            }
            else
            {
                m_Pending[key] = new PendingImport
                {
                    Request = normalizedRequest,
                    DueTimeUtc = DateTime.UtcNow + m_DebounceDelay
                };
            }
        }

        Signal();
    }

    private async Task ProcessLoopAsync()
    {
        var token = m_Cancellation.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextDue();
                if (delay > TimeSpan.Zero)
                {
                    await Task.WhenAny(m_Signal.WaitAsync(token), Task.Delay(delay, token));
                }

                while (TryDequeueDue(out var request))
                {
                    await ProcessWithRetryAsync(request, token);
                }

                if (GetPendingCount() == 0)
                {
                    await m_Signal.WaitAsync(token);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessWithRetryAsync(AssetImportRequest request, CancellationToken token)
    {
        for (var attempt = 1; attempt <= m_MaxAttempts && !token.IsCancellationRequested; attempt++)
        {
            if (m_Process(request))
            {
                return;
            }

            if (attempt < m_MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(m_RetryDelay.TotalMilliseconds * attempt), token);
            }
        }

        ArisenEngine.Core.Diagnostics.Logger.Log(
            $"[AssetImportScheduler] Import failed after {m_MaxAttempts} attempts: {request.Kind} {request.FullPath}");
    }

    private bool TryDequeueDue(out AssetImportRequest request)
    {
        lock (m_Lock)
        {
            var now = DateTime.UtcNow;
            foreach (var (key, pending) in m_Pending)
            {
                if (pending.DueTimeUtc > now)
                {
                    continue;
                }

                request = pending.Request;
                m_Pending.Remove(key);
                return true;
            }
        }

        request = default;
        return false;
    }

    private TimeSpan GetDelayUntilNextDue()
    {
        lock (m_Lock)
        {
            if (m_Pending.Count == 0)
            {
                return TimeSpan.Zero;
            }

            var now = DateTime.UtcNow;
            DateTime? earliest = null;
            foreach (var pending in m_Pending.Values)
            {
                if (earliest == null || pending.DueTimeUtc < earliest.Value)
                {
                    earliest = pending.DueTimeUtc;
                }
            }

            if (earliest == null || earliest.Value <= now)
            {
                return TimeSpan.Zero;
            }

            return earliest.Value - now;
        }
    }

    private int GetPendingCount()
    {
        lock (m_Lock)
        {
            return m_Pending.Count;
        }
    }

    private void Signal()
    {
        try
        {
            m_Signal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static AssetImportRequest Coalesce(AssetImportRequest existing, AssetImportRequest incoming)
    {
        if (incoming.Kind == AssetImportWorkKind.Deleted)
        {
            return incoming;
        }

        if (existing.Kind == AssetImportWorkKind.Deleted)
        {
            return incoming.Kind == AssetImportWorkKind.Created
                ? incoming
                : existing;
        }

        if (incoming.Kind == AssetImportWorkKind.Renamed)
        {
            return incoming with
            {
                OldFullPath = string.IsNullOrWhiteSpace(existing.OldFullPath)
                    ? incoming.OldFullPath
                    : existing.OldFullPath
            };
        }

        if (existing.Kind == AssetImportWorkKind.Renamed)
        {
            return existing with { Kind = AssetImportWorkKind.Renamed };
        }

        if (existing.Kind == AssetImportWorkKind.Created)
        {
            return existing;
        }

        return incoming;
    }

    private static AssetImportRequest Normalize(AssetImportRequest request)
    {
        return request with
        {
            FullPath = Path.GetFullPath(request.FullPath),
            OldFullPath = string.IsNullOrWhiteSpace(request.OldFullPath)
                ? string.Empty
                : Path.GetFullPath(request.OldFullPath)
        };
    }

    private static string CreateKey(AssetImportRequest request)
    {
        return request.Kind == AssetImportWorkKind.Renamed && !string.IsNullOrWhiteSpace(request.OldFullPath)
            ? request.OldFullPath
            : request.FullPath;
    }

    public void Dispose()
    {
        m_Cancellation.Cancel();
        Signal();

        try
        {
            m_Worker.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }

        m_Cancellation.Dispose();
        m_Signal.Dispose();
    }
}
