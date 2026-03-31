using System;
using System.IO;

namespace ArisenEditorFramework.Services;

public class LogService : ILogService
{
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public LogService(string logFileName)
    {
        string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
        _logFilePath = Path.Combine(logDir, logFileName);
        
        // Start fresh log session
        Info($"=== {logFileName} Session Started ===");
    }

    public virtual void Info(string message) => Log(LogLevel.Info, message);
    public virtual void Warning(string message) => Log(LogLevel.Warning, message);
    public virtual void Error(string message, Exception? ex = null) => Log(LogLevel.Error, $"{message}{(ex != null ? $"\nException: {ex}" : "")}");
    public virtual void Critical(string message, Exception? ex = null) => Log(LogLevel.Critical, $"{message}{(ex != null ? $"\nException: {ex}" : "")}");

    private void Log(LogLevel level, string message)
    {
        string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level.ToString().ToUpper()}] {message}";
        
        // Output to console for debugging
        Console.WriteLine(logEntry);

        lock (_lock)
        {
            try
            {
                File.AppendAllLines(_logFilePath, new[] { logEntry });
            }
            catch
            {
                // Fallback if file writing fails
            }
        }
    }

    public string GetLogPath() => _logFilePath;
}
