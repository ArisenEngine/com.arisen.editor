using System;
using System.Diagnostics;
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
    private Thread m_EngineThread;
    private CancellationTokenSource m_CancellationTokenSource;
    private Stopwatch m_Stopwatch;

    private float m_TargetFrameTime = 1.0f / 60.0f; // 60 FPS Target for Editor

    public bool IsRunning => m_EngineThread != null && m_EngineThread.IsAlive;

    public void Start()
    {
        if (IsRunning) return;

        m_CancellationTokenSource = new CancellationTokenSource();
        m_Stopwatch = new Stopwatch();

        m_EngineThread = new Thread(EngineLoop)
        {
            Name = "Arisen_Engine_MainThread",
            IsBackground = false, // Changed to false to ensure graceful cleanup before process exit
            Priority = ThreadPriority.AboveNormal
        };

        m_EngineThread.Start();
    }

    public void Stop()
    {
        if (!IsRunning) return;

        EditorLog.Log("[EditorEngineRunner] Stop requested. Waiting for engine thread to exit...");
        m_CancellationTokenSource.Cancel();
        m_EngineThread.Join(5000); // Wait up to 5 seconds for graceful exit (Vulkan/TaskGraph teardown)
        
        m_CancellationTokenSource.Dispose();
        m_CancellationTokenSource = null;
        m_EngineThread = null;
    }

    private void EngineLoop()
    {
        EditorLog.Log("Engine Background Thread Started.");
        m_Stopwatch.Start();

        double lastTime = m_Stopwatch.Elapsed.TotalSeconds;

        var token = m_CancellationTokenSource.Token;

        // The Hot Loop - ZERO Allocations allowed here
        while (!token.IsCancellationRequested)
        {
            using (Profiler.Zone("EditorEngine_Frame"))
            {
                double currentTime = m_Stopwatch.Elapsed.TotalSeconds;
                float deltaTime = (float)(currentTime - lastTime);
                lastTime = currentTime;

                // 1. Tick the Engine (ECS, Renderer, Physics)
                EngineKernel.Instance?.Tick(deltaTime);

                // 2. Cap Frame Rate to prevent Editor from burning 100% CPU when idle
                // In a true shipped game this might be vsync bound, but in Editor we throttle.
                float elapsedThisFrame = (float)(m_Stopwatch.Elapsed.TotalSeconds - currentTime);
                if (elapsedThisFrame < m_TargetFrameTime)
                {
                    int sleepMs = (int)((m_TargetFrameTime - elapsedThisFrame) * 1000.0f);
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

        // CRITICAL: Trigger engine kernel shutdown once the loop exits.
        // This ensures all subsystems (RHI, TaskGraph, ECS) perform their cleanup.
        if (EngineKernel.IsCreated)
        {
            EditorLog.Log("[EditorEngineRunner] Shutting down Engine Kernel...");
            EngineKernel.Instance.Shutdown();
        }

        m_Stopwatch.Stop();
        EditorLog.Log("Engine Background Thread Stopped.");
    }

    public void Dispose()
    {
        Stop();
    }
}
