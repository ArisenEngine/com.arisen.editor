using System.Runtime.ExceptionServices;

namespace ArisenEditor.Core.Lifecycle;

internal sealed class EditorEngineThreadOwner : IDisposable
{
    private readonly object m_Gate = new();
    private readonly Action<CancellationToken> m_Run;
    private readonly string m_ThreadName;
    private readonly ThreadPriority m_ThreadPriority;
    private RunState? m_State;

    public EditorEngineThreadOwner(
        Action<CancellationToken> run,
        string threadName,
        ThreadPriority threadPriority)
    {
        m_Run = run ?? throw new ArgumentNullException(nameof(run));
        m_ThreadName = string.IsNullOrWhiteSpace(threadName)
            ? throw new ArgumentException("Engine thread name cannot be empty.", nameof(threadName))
            : threadName;
        m_ThreadPriority = threadPriority;
    }

    public bool IsRunning
    {
        get
        {
            lock (m_Gate)
            {
                return m_State is { Completion.Task.IsCompleted: false };
            }
        }
    }

    internal bool HasThreadOwnership
    {
        get
        {
            lock (m_Gate)
            {
                return m_State != null;
            }
        }
    }

    public Task Completion
    {
        get
        {
            lock (m_Gate)
            {
                return m_State?.Completion.Task ?? Task.CompletedTask;
            }
        }
    }

    public void Start()
    {
        lock (m_Gate)
        {
            if (m_State != null)
            {
                if (!m_State.Completion.Task.IsCompleted)
                {
                    return;
                }

                throw new InvalidOperationException(
                    "The completed Editor engine thread must be stopped and released before it can be restarted.");
            }

            var cancellation = new CancellationTokenSource();
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            RunState? state = null;
            var thread = new Thread(() => Execute(state!))
            {
                Name = m_ThreadName,
                IsBackground = false,
                Priority = m_ThreadPriority
            };
            state = new RunState(thread, cancellation, completion);
            m_State = state;

            try
            {
                thread.Start();
            }
            catch
            {
                m_State = null;
                cancellation.Dispose();
                throw;
            }
        }
    }

    public void Stop()
    {
        RunState state;
        bool ownsStop;
        lock (m_Gate)
        {
            state = m_State!;
            if (state == null)
            {
                return;
            }
            if (ReferenceEquals(Thread.CurrentThread, state.Thread))
            {
                throw new InvalidOperationException(
                    "The Editor engine thread cannot synchronously stop itself.");
            }

            ownsStop = !state.StopStarted;
            state.StopStarted = true;
        }

        if (!ownsStop)
        {
            state.StopCompletion.Task.GetAwaiter().GetResult();
            return;
        }

        Exception? failure = null;
        try
        {
            try
            {
                state.Cancellation.Cancel();
            }
            catch (Exception error)
            {
                failure = AppendFailure(failure, "cancellation request", error);
            }

            try
            {
                state.Completion.Task.GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                failure = AppendFailure(failure, "engine-thread completion", error);
            }

            try
            {
                // Completion is signaled only after engine cleanup has returned. This join confirms
                // actual thread termination and never uses elapsed time as a correctness condition.
                state.Thread.Join();
            }
            catch (Exception error)
            {
                failure = AppendFailure(failure, "engine-thread termination", error);
            }
        }
        finally
        {
            try
            {
                state.Cancellation.Dispose();
            }
            catch (Exception error)
            {
                failure = AppendFailure(failure, "cancellation-owner disposal", error);
            }
            lock (m_Gate)
            {
                if (ReferenceEquals(m_State, state))
                {
                    m_State = null;
                }
            }

            if (failure == null)
            {
                state.StopCompletion.TrySetResult();
            }
            else
            {
                state.StopCompletion.TrySetException(failure);
            }
        }

        if (failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void Dispose() => Stop();

    private void Execute(RunState state)
    {
        try
        {
            m_Run(state.Cancellation.Token);
            state.Completion.TrySetResult();
        }
        catch (Exception error)
        {
            state.Completion.TrySetException(error);
        }
    }

    private static Exception AppendFailure(
        Exception? current,
        string operation,
        Exception failure)
    {
        var attributable = new InvalidOperationException(
            $"Editor engine {operation} failed.",
            failure);
        if (current == null)
        {
            return attributable;
        }

        var failures = new List<Exception>();
        AddFailure(failures, current);
        AddFailure(failures, attributable);
        return new AggregateException(
            "Editor engine stop completed with multiple failures.",
            failures);
    }

    private static void AddFailure(List<Exception> failures, Exception failure)
    {
        if (failure is AggregateException aggregate)
        {
            failures.AddRange(aggregate.Flatten().InnerExceptions);
            return;
        }

        failures.Add(failure);
    }

    private sealed class RunState
    {
        public RunState(
            Thread thread,
            CancellationTokenSource cancellation,
            TaskCompletionSource completion)
        {
            Thread = thread;
            Cancellation = cancellation;
            Completion = completion;
            StopCompletion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Thread Thread { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource Completion { get; }
        public TaskCompletionSource StopCompletion { get; }
        public bool StopStarted { get; set; }
    }
}
