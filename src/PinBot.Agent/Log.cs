namespace PinBot.Agent;

/// <summary>Console + daily log file under %LOCALAPPDATA%\PinBot\logs, keeping the newest 14 files of at most 5 MB each.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _file;
    const long MaxFileBytes = 5_000_000; // a cabinet's disk never fills with logs; warnings and errors are still kept
    static long _written;

    public static string LogDir => Path.Combine(LocalState.Dir, "logs");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            _file = Path.Combine(LogDir, $"pinbot-{DateTime.Now:yyyyMMdd}.log");
            _written = File.Exists(_file) ? new FileInfo(_file).Length : 0;
            foreach (var old in Directory.GetFiles(LogDir, "pinbot-*.log").OrderDescending().Skip(14))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file = null;
            Console.Error.WriteLine($"logging to file disabled: {ex.Message}");
        }
    }

    public static void Info(string msg) => Write("INFO", msg, null);
    public static void Warn(string msg) => Write("WARN", msg, ConsoleColor.Yellow);
    public static void Error(string msg) => Write("ERROR", msg, ConsoleColor.Red);
    public static void Good(string msg) => Write("INFO", msg, ConsoleColor.Green);

    /// <summary>Plain console output that is also captured in the log file.</summary>
    public static void Out(string msg = "") => Write(null, msg, null);

    static System.Collections.Concurrent.BlockingCollection<(string Text, ConsoleColor? Color)>? _console;

    /// <summary>
    /// For the watcher: the console window is written by a thread of its own. Text selected in a console window pauses
    /// every write to it until the selection ends; written directly, that froze the watcher (a whole game went unseen).
    /// The log file is still written at once; a window paused for long loses only its own lines.
    /// </summary>
    public static void ConsoleInBackground()
    {
        lock (Gate)
        {
            if (_console != null) return;
            var queue = _console = new(boundedCapacity: 10_000);
            new Thread(() => { foreach (var (text, color) in queue.GetConsumingEnumerable()) WriteConsole(text, color); })
                { IsBackground = true, Name = "console" }.Start();
        }
    }

    static void WriteConsole(string text, ConsoleColor? color)
    {
        if (color is { } c) Console.ForegroundColor = c;
        Console.WriteLine(text);
        if (color != null) Console.ResetColor();
    }

    static void Write(string? level, string msg, ConsoleColor? color)
    {
        lock (Gate)
        {
            var text = level is null || level == "INFO" ? msg : $"{level}: {msg}";
            if (_console is { } queue) queue.TryAdd((text, color)); // full: the window has been paused a long time
            else WriteConsole(text, color);
            if (_file == null || (_written += msg.Length) > MaxFileBytes && level is not ("ERROR" or "WARN")) return;
            try
            {
                var prefix = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {level ?? "OUT",-5} ";
                File.AppendAllText(_file, prefix + msg.ReplaceLineEndings(Environment.NewLine + new string(' ', prefix.Length)) + Environment.NewLine);
            }
            catch (IOException) { /* logging must never break monitoring */ }
        }
    }
}
