using System.Diagnostics;

namespace ClaudeLog.Core;

/// <summary>
/// Dead-simple diagnostics. Nothing in this app should ever crash over a log line, a settings
/// file or a transcript it couldn't parse, so failures land here instead of propagating.
/// </summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;

    public static readonly List<string> Recent = [];

    public static string File => Path.Combine(Paths.AppDataDir, "claudelog.log");

    public static void Warn(string message) => Write("WARN", message);

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level} {message}";
        Debug.WriteLine(line);

        lock (Recent)
        {
            Recent.Add(line);
            if (Recent.Count > 200) Recent.RemoveAt(0);

            // A GUI app that swallows failures needs somewhere to have swallowed them.
            try
            {
                Paths.EnsureAppDataDir();
                var info = new FileInfo(File);
                if (info.Exists && info.Length > MaxBytes) info.Delete();
                System.IO.File.AppendAllText(File, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never be the thing that breaks.
            }
        }
    }
}
