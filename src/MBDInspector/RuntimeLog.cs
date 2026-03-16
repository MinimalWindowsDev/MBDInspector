using System;
using System.IO;

namespace MBDInspector;

internal static class RuntimeLog
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MBDInspector");

    private static readonly string LogPath = Path.Combine(LogDirectory, "runtime.log");

    public static string PathOnDisk => LogPath;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null)
    {
        string payload = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", payload);
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {level} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
