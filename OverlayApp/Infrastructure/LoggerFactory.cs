using System;
using System.IO;

namespace OverlayApp.Infrastructure;

internal static class LoggerFactory
{
    public static ILogger CreateDefaultLogger()
    {
        var logPath = GetLogFilePath();
        return new FileLogger(logPath);
    }

    public static string GetLogFilePath()
    {
        // DEBUG: Log to workspace for easier access
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        // var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArcRaidersHelper", "logs");
        Directory.CreateDirectory(logDirectory);
        return Path.Combine(logDirectory, "overlay.log");
    }
}