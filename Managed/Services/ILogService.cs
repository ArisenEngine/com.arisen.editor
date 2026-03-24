using System;

namespace ArisenEditorFramework.Services;

public enum LogLevel
{
    Info,
    Warning,
    Error,
    Critical
}

public interface ILogService
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? ex = null);
    void Critical(string message, Exception? ex = null);
    string GetLogPath();
}
