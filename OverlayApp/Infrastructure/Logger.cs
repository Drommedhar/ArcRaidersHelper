using System;
using System.IO;
using System.Text;

namespace OverlayApp.Infrastructure;

internal interface ILogger
{
    void Log(string category, string message);
}

internal sealed class FileLogger : ILogger
{
    private readonly string _logFilePath;
    private readonly object _gate = new();

    public FileLogger(string logFilePath)
    {
        _logFilePath = logFilePath ?? throw new ArgumentNullException(nameof(logFilePath));
        var directory = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void Log(string category, string message)
    {
        var line = $"{DateTimeOffset.Now:O}\t{category}\t{message}";
        lock (_gate)
        {
            File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}