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

internal enum AssetImportSchedulerState
{
    Accepting,
    StopRequested,
    Completed,
    Disposed
}

internal sealed record AssetImportFailure(
    AssetImportRequest Request,
    int Attempts,
    string Diagnostic,
    Exception? Exception);

internal sealed class AssetImportScheduler : IDisposable
{
    private sealed class PendingImport
    {
        public required AssetImportRequest Request;
        public required DateTime DueTimeUtc;
    }

    private readonly object m_Lock = new();
    private readonly Dictionary<string, PendingImport> m_Pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AssetImportFailure> m_TerminalFailures = new();
    private readonly Func<AssetImportRequest, bool> m_Process;
    private readonly TimeSpan m_DebounceDelay;
    private readonly TimeSpan m_RetryDelay;
    private readonly int m_MaxAttempts;
    private readonly CancellationTokenSource m_Cancellation = new();
    private readonly SemaphoreSlim m_Signal = new(0);
    private readonly Task m_Worker;
    private TaskCompletionSource<bool> m_IdleCompletion = CreateCompletedSignal();
    private AssetImportSchedulerState m_State = AssetImportSchedulerState.Accepting;
    private bool m_IsProcessing;

    public AssetImportScheduler(
        Func<AssetImportRequest, bool> process,
        TimeSpan? debounceDelay = null,
        TimeSpan? retryDelay = null,
        int maxAttempts = 5)
    {
        m_Process = process ?? throw new ArgumentNullException(nameof(process));
        m_DebounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(150);
        m_RetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(100);
        m_MaxAttempts = System.Math.Max(1, maxAttempts);
        m_Worker = Task.Run(ProcessLoopAsync);
    }

    internal event Action<AssetImportFailure>? ImportFailed;

    internal Action? BeforeWorkerCompletion { get; set; }

    internal AssetImportSchedulerState State
    {
        get
        {
            lock (m_Lock)
            {
                return m_State;
            }
        }
    }

    internal Task Completion => m_Worker;

    internal IReadOnlyList<AssetImportFailure> TerminalFailures
    {
        get
        {
            lock (m_Lock)
            {
                return m_TerminalFailures.ToArray();
            }
        }
    }

    public bool Enqueue(AssetImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FullPath))
        {
            return false;
        }

        AssetImportRequest normalizedRequest = Normalize(request);
        string key = CreateKey(normalizedRequest);
        lock (m_Lock)
        {
            if (m_State != AssetImportSchedulerState.Accepting)
            {
                return false;
            }

            if (m_Pending.TryGetValue(key, out PendingImport? pending))
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

            if (m_IdleCompletion.Task.IsCompleted)
            {
                m_IdleCompletion = CreateSignal();
            }

            m_Signal.Release();
            return true;
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (m_Lock)
        {
            return m_IdleCompletion.Task;
        }
    }

    internal void RequestStop()
    {
        lock (m_Lock)
        {
            if (m_State != AssetImportSchedulerState.Accepting)
            {
                return;
            }

            m_State = AssetImportSchedulerState.StopRequested;
            m_Pending.Clear();
            m_Cancellation.Cancel();
            m_Signal.Release();
        }
    }

    private async Task ProcessLoopAsync()
    {
        CancellationToken token = m_Cancellation.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan delay = GetDelayUntilNextDue();
                if (delay > TimeSpan.Zero)
                {
                    await m_Signal.WaitAsync(delay, token);
                }

                while (TryDequeueDue(out AssetImportRequest request))
                {
                    try
                    {
                        await ProcessWithRetryAsync(request, token);
                    }
                    finally
                    {
                        CompleteActiveRequest();
                    }
                }

                if (GetPendingCount() == 0)
                {
                    await m_Signal.WaitAsync(token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                BeforeWorkerCompletion?.Invoke();
            }
            finally
            {
                lock (m_Lock)
                {
                    m_Pending.Clear();
                    m_IsProcessing = false;
                    m_IdleCompletion.TrySetResult(true);
                    if (m_State != AssetImportSchedulerState.Disposed)
                    {
                        m_State = AssetImportSchedulerState.Completed;
                    }
                }
            }
        }
    }

    private async Task ProcessWithRetryAsync(AssetImportRequest request, CancellationToken token)
    {
        Exception? lastError = null;
        for (int attempt = 1; attempt <= m_MaxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (m_Process(request))
                {
                    return;
                }

                lastError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                RecordTerminalFailure(request, attempt, ex.Message, ex);
                return;
            }

            if (attempt < m_MaxAttempts)
            {
                await Task.Delay(m_RetryDelay * attempt, token);
            }
        }

        string diagnostic = lastError?.Message ??
            $"Processor reported a retryable failure for {m_MaxAttempts} attempt(s).";
        RecordTerminalFailure(request, m_MaxAttempts, diagnostic, lastError);
    }

    private void RecordTerminalFailure(
        AssetImportRequest request,
        int attempts,
        string diagnostic,
        Exception? exception)
    {
        var failure = new AssetImportFailure(request, attempts, diagnostic, exception);
        Action<AssetImportFailure>? handlers;
        lock (m_Lock)
        {
            m_TerminalFailures.Add(failure);
            handlers = ImportFailed;
        }

        ArisenEngine.Core.Diagnostics.Logger.Error(
            $"[AssetImportScheduler] Terminal import failure after {attempts} attempt(s): " +
            $"{request.Kind} {request.FullPath} | {diagnostic}");
        if (handlers == null)
        {
            return;
        }

        try
        {
            handlers(failure);
        }
        catch (Exception callbackError)
        {
            ArisenEngine.Core.Diagnostics.Logger.Error(
                $"[AssetImportScheduler] Import-failure observer threw: {callbackError}");
        }
    }

    private bool TryDequeueDue(out AssetImportRequest request)
    {
        lock (m_Lock)
        {
            DateTime now = DateTime.UtcNow;
            foreach ((string key, PendingImport pending) in m_Pending)
            {
                if (pending.DueTimeUtc > now)
                {
                    continue;
                }

                request = pending.Request;
                m_Pending.Remove(key);
                m_IsProcessing = true;
                return true;
            }
        }

        request = default;
        return false;
    }

    private void CompleteActiveRequest()
    {
        lock (m_Lock)
        {
            m_IsProcessing = false;
            CompleteIdleIfReady();
        }
    }

    private TimeSpan GetDelayUntilNextDue()
    {
        lock (m_Lock)
        {
            if (m_Pending.Count == 0)
            {
                return TimeSpan.Zero;
            }

            DateTime now = DateTime.UtcNow;
            DateTime? earliest = null;
            foreach (PendingImport pending in m_Pending.Values)
            {
                if (earliest == null || pending.DueTimeUtc < earliest.Value)
                {
                    earliest = pending.DueTimeUtc;
                }
            }

            return earliest == null || earliest.Value <= now
                ? TimeSpan.Zero
                : earliest.Value - now;
        }
    }

    private int GetPendingCount()
    {
        lock (m_Lock)
        {
            CompleteIdleIfReady();
            return m_Pending.Count;
        }
    }

    private void CompleteIdleIfReady()
    {
        if (!m_IsProcessing && m_Pending.Count == 0)
        {
            m_IdleCompletion.TrySetResult(true);
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

    private static TaskCompletionSource<bool> CreateSignal()
    {
        return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource<bool> CreateCompletedSignal()
    {
        TaskCompletionSource<bool> signal = CreateSignal();
        signal.SetResult(true);
        return signal;
    }

    public void Dispose()
    {
        lock (m_Lock)
        {
            if (m_State == AssetImportSchedulerState.Disposed)
            {
                return;
            }
        }

        var errors = new List<Exception>();
        try
        {
            RequestStop();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }

        try
        {
            m_Worker.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            errors.Add(ex);
        }

        lock (m_Lock)
        {
            if (m_State == AssetImportSchedulerState.Disposed)
            {
                return;
            }

            m_State = AssetImportSchedulerState.Disposed;
            try
            {
                m_Cancellation.Dispose();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }

            try
            {
                m_Signal.Dispose();
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException(
                "[AssetImportScheduler] Shutdown failed after worker completion.",
                errors);
        }
    }
}
