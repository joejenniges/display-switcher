namespace DisplaySwitcher;

internal static class Log
{
    const long MaxBytes = 2_000_000;
    static readonly object Gate = new();
    static string? path;

    public static void Init(string logPath) => path = logPath;

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    static void Write(string level, string message)
    {
        if (path is null)
            return;
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never take down a switch.
            }
        }
    }
}
