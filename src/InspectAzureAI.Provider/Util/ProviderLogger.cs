namespace InspectAzureAI.Provider.Util;

/// <summary>
/// Tiny logging shim standing in for the <c>logging</c> module: <see cref="WarnOnce"/> ports
/// <c>inspect_ai._util.logger.warn_once</c> (a message is logged at most once per process, keyed by
/// its exact text). Messages go to <see cref="Sink"/> (stderr by default) and are kept in
/// <see cref="Warnings"/> / <see cref="Infos"/> for inspection.
/// </summary>
public static class ProviderLogger
{
    private static readonly object Gate = new();
    private static readonly List<string> Warned = [];
    private static readonly List<string> WarningLog = [];
    private static readonly List<string> InfoLog = [];

    /// <summary>Where log lines are written; defaults to stderr.</summary>
    public static Action<string, string> Sink { get; set; } = (level, message) => Console.Error.WriteLine($"[{level}] {message}");

    /// <summary>All warnings emitted so far.</summary>
    public static IReadOnlyList<string> Warnings
    {
        get
        {
            lock (Gate)
            {
                return WarningLog.ToList();
            }
        }
    }

    /// <summary>All info messages emitted so far.</summary>
    public static IReadOnlyList<string> Infos
    {
        get
        {
            lock (Gate)
            {
                return InfoLog.ToList();
            }
        }
    }

    public static void Warning(string message)
    {
        lock (Gate)
        {
            WarningLog.Add(message);
        }

        Sink("WARNING", message);
    }

    public static void Info(string message)
    {
        lock (Gate)
        {
            InfoLog.Add(message);
        }

        Sink("INFO", message);
    }

    /// <summary>Port of <c>warn_once</c>.</summary>
    public static void WarnOnce(string message)
    {
        lock (Gate)
        {
            if (Warned.Contains(message))
            {
                return;
            }

            Warned.Add(message);
        }

        Warning(message);
    }

    /// <summary>Clears the once-only registry and the captured logs (tests only).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Warned.Clear();
            WarningLog.Clear();
            InfoLog.Clear();
        }
    }
}
