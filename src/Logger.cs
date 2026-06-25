using System.IO;
using System.Text;

namespace MsTeamsLocal;

/// <summary>Minimal thread-safe file logger written next to the plugin executable.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string LogPath = ResolvePath();

    private static string ResolvePath()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "logs");
        try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        return Path.Combine(dir, "plugin.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                // Keep the log from growing unbounded across long sessions.
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 512 * 1024)
                    File.WriteAllText(LogPath, string.Empty);
                File.AppendAllText(LogPath, line, Encoding.UTF8);
            }
            catch { /* logging must never throw */ }
        }
    }
}
