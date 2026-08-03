using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using ArisenEditor.Core.Services;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Lifecycle;

namespace ArisenEditor.Core.Lifecycle;

/// <summary>
/// A dedicated background runner that executes the Arisen Engine Kernel loop.
/// Decoupled from the Avalonia UI thread to prevent blocking and airspace issues.
/// </summary>
public class EditorEngineRunner : IDisposable
{
    private const float TargetFrameTime = 1.0f / 60.0f;
    private readonly EditorEngineThreadOwner m_ThreadOwner;

    public EditorEngineRunner()
    {
        m_ThreadOwner = new EditorEngineThreadOwner(
            EngineLoop,
            "Arisen_Engine_MainThread",
            ThreadPriority.AboveNormal);
    }

    public bool IsRunning => m_ThreadOwner.IsRunning;
    public Task Completion => m_ThreadOwner.Completion;

    public void Start()
    {
        m_ThreadOwner.Start();
    }

    public void Stop()
    {
        if (!m_ThreadOwner.HasThreadOwnership) return;

        EditorLog.Log("[EditorEngineRunner] Stop requested. Waiting for engine thread to exit...");
        m_ThreadOwner.Stop();
    }

    private void EngineLoop(CancellationToken token)
    {
        EditorLog.Log("Engine Background Thread Started.");
        var stopwatch = Stopwatch.StartNew();
        double lastTime = stopwatch.Elapsed.TotalSeconds;
        Exception? failure = null;

        try
        {
            // The Hot Loop - ZERO Allocations allowed here
            while (!token.IsCancellationRequested)
            {
                using (Profiler.Zone("EditorEngine_Frame"))
                {
                    double currentTime = stopwatch.Elapsed.TotalSeconds;
                    float deltaTime = (float)(currentTime - lastTime);
                    lastTime = currentTime;

                    EngineKernel.Instance.Tick(deltaTime);

                    // Editor frame pacing is a performance policy; it does not participate in
                    // shutdown correctness, which waits on explicit thread completion.
                    float elapsedThisFrame = (float)(stopwatch.Elapsed.TotalSeconds - currentTime);
                    if (elapsedThisFrame < TargetFrameTime)
                    {
                        int sleepMs = (int)((TargetFrameTime - elapsedThisFrame) * 1000.0f);
                        if (sleepMs > 0)
                        {
                            using (Profiler.Zone("Editor_IdleSleep"))
                            {
                                Thread.Sleep(sleepMs);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception error)
        {
            failure = new InvalidOperationException(
                "Editor engine frame loop failed.",
                error);
        }

        try
        {
            if (EngineKernel.IsCreated)
            {
                EditorLog.Log("[EditorEngineRunner] Shutting down Engine Kernel...");
                EngineKernel.Instance.Shutdown();
            }
        }
        catch (Exception error)
        {
            var shutdownFailure = new InvalidOperationException(
                "Editor engine kernel shutdown failed.",
                error);
            failure = failure == null
                ? shutdownFailure
                : new AggregateException(
                    "Editor engine frame loop and kernel shutdown both failed.",
                    failure,
                    shutdownFailure);
        }

        stopwatch.Stop();
        EditorLog.Log("Engine Background Thread Stopped.");
        if (failure != null)
        {
            EditorLog.Critical("[EditorEngineRunner] Engine thread failed.", failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
