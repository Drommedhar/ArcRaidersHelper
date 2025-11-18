using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OverlayApp.Infrastructure;

internal static class UpdateInstaller
{
    public static string LaunchUpdateScript(UpdateScriptOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var scriptPath = CreateScriptFile(options);
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(startInfo);
        return scriptPath;
    }

    private static string CreateScriptFile(UpdateScriptOptions options)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"ArcRaidersHelper_Update_{Guid.NewGuid():N}.cmd");
        var builder = new StringBuilder();

        builder.AppendLine("@echo off");
        builder.AppendLine("setlocal enabledelayedexpansion");
        builder.AppendLine($"set \"SOURCE={options.SourceDirectory}\"");
        builder.AppendLine($"set \"TARGET={options.TargetDirectory}\"");
        builder.AppendLine($"set \"LOG={options.LogFilePath}\"");
        builder.AppendLine($"set \"PARENT={options.ParentProcessId}\"");
        builder.AppendLine();
        builder.AppendLine("echo [%date% %time%] Update script started. >> \"%LOG%\"");
        builder.AppendLine("if not \"%PARENT%\"==\"\" call :waitForParent");
        builder.AppendLine();
        builder.AppendLine("robocopy \"%SOURCE%\" \"%TARGET%\" /E /R:2 /W:2 /NFL /NDL /NP >nul");
        builder.AppendLine("set RC=%ERRORLEVEL%");
        builder.AppendLine("echo [%date% %time%] robocopy exit code %RC%. >> \"%LOG%\"");
        builder.AppendLine("if %RC% GEQ 8 goto failed");
        builder.AppendLine();
        builder.AppendLine("rmdir /s /q \"%SOURCE%\" >nul 2>&1");
        builder.AppendLine();
        if (!string.IsNullOrWhiteSpace(options.LauncherPath))
        {
            builder.Append(BuildRestartCommand(options.LauncherPath, options.LauncherArguments));
        }
        builder.AppendLine("goto end");
        builder.AppendLine();
        builder.AppendLine(":failed");
        builder.AppendLine("echo [%date% %time%] Update copy failed. >> \"%LOG%\"");
        builder.AppendLine("goto end");
        builder.AppendLine();
        builder.AppendLine(":waitForParent");
        builder.AppendLine("tasklist /FI \"PID eq %PARENT%\" 2>nul | find \"%PARENT%\" >nul");
        builder.AppendLine("if not errorlevel 1 (");
        builder.AppendLine("  timeout /t 1 /nobreak >nul");
        builder.AppendLine("  goto waitForParent");
        builder.AppendLine(")");
        builder.AppendLine("goto :eof");
        builder.AppendLine();
        builder.AppendLine(":end");
        builder.AppendLine("endlocal");
        builder.AppendLine("del \"%~f0\" >nul 2>&1");
        builder.AppendLine("exit /b 0");

        File.WriteAllText(scriptPath, builder.ToString(), Encoding.UTF8);
        return scriptPath;
    }

    private static string BuildRestartCommand(string launcherPath, string? launcherArguments)
    {
        var argumentSuffix = string.IsNullOrWhiteSpace(launcherArguments)
            ? string.Empty
            : $" {launcherArguments}";

        var builder = new StringBuilder();
        builder.AppendLine("REM Restart ArcRaidersHelper");
        builder.AppendLine($"start \"\" \"{launcherPath}\"{argumentSuffix}");
        builder.AppendLine("if errorlevel 1 (");
        builder.AppendLine("  echo [%date% %time%] Failed to restart ArcRaidersHelper. >> \"%LOG%\"");
        builder.AppendLine(") else (");
        builder.AppendLine("  echo [%date% %time%] Restarted ArcRaidersHelper. >> \"%LOG%\"");
        builder.AppendLine(")");
        return builder.ToString();
    }
}

internal sealed class UpdateScriptOptions
{
    public required string SourceDirectory { get; init; }
    public required string TargetDirectory { get; init; }
    public required int ParentProcessId { get; init; }
    public required string LauncherPath { get; init; }
    public string LauncherArguments { get; init; } = string.Empty;
    public required string LogFilePath { get; init; }
}