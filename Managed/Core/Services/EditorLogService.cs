using System;
using ArisenEditorFramework.Services;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEditor.Core.Services;

public class EditorLogService : LogService
{
    public static event Action<Logger.LogMessage>? MessageAdded;

    public EditorLogService(string logFileName) : base(logFileName)
    {
    }

    public override void Info(string message)
    {
        base.Info(message);
        Notify(Logger.LogLevel.Info, message);
    }

    public void Log(string message)
    {
        base.Info(message);
        Notify(Logger.LogLevel.Log, message);
    }

    public override void Warning(string message)
    {
        string fullMessage = $"{message}\n{Environment.StackTrace}";
        base.Warning(fullMessage);
        Notify(Logger.LogLevel.Warning, message);
    }

    public override void Error(string message, Exception? ex = null)
    {
        string fullMessage = $"{message}{(ex != null ? $"\nException: {ex}" : "")}\n{Environment.StackTrace}";
        base.Error(fullMessage);
        Notify(Logger.LogLevel.Error, $"{message}{(ex != null ? $"\n{ex}" : "")}");
    }

    public override void Critical(string message, Exception? ex = null)
    {
        string fullMessage = $"{message}{(ex != null ? $"\nException: {ex}" : "")}\n{Environment.StackTrace}";
        base.Critical(fullMessage);
        Notify(Logger.LogLevel.Fatal, $"{message}{(ex != null ? $"\n{ex}" : "")}");
    }

    private void Notify(Logger.LogLevel level, string message)
    {
        var logMessage = new Logger.LogMessage(
            level, 
            message, 
            System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(), 
            System.Threading.Thread.CurrentThread.Name ?? "MainThread", 
            DateTime.Now, 
            Environment.StackTrace);

        MessageAdded?.Invoke(logMessage);
    }
}
