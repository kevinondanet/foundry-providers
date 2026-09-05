using System.Runtime.InteropServices;

namespace InspectAzureAI.Eval.Model.Cache;

/// <summary>
/// Port of <c>_util/appdirs.py</c> over <c>platformdirs</c>: the per-user cache directory the prompt cache lives in
/// when <c>INSPECT_CACHE_DIR</c> is unset. The locations match <c>platformdirs.user_cache_path</c> so the .NET port
/// and Python share one cache root (the entry formats differ; see <c>docs/ports/prompt-cache.md</c>).
/// </summary>
public static class AppDirs
{
    /// <summary>The Python package name the cache directory is keyed by.</summary>
    public const string PackageName = "inspect_ai";

    /// <summary>
    /// Port of <c>platformdirs.user_cache_path(appName)</c>: <c>~/Library/Caches/{app}</c> on macOS,
    /// <c>$XDG_CACHE_HOME/{app}</c> (default <c>~/.cache/{app}</c>) on Linux and other Unixes, and
    /// <c>%LOCALAPPDATA%\{app}\{app}\Cache</c> on Windows (platformdirs repeats the app name as the author).
    /// </summary>
    public static string UserCachePath(string appName)
    {
        ArgumentException.ThrowIfNullOrEmpty(appName);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return Path.Combine(Home(), "Library", "Caches", appName);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName, appName, "Cache");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        var root = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Home(), ".cache") : xdg;
        return Path.Combine(root, appName);
    }

    /// <summary>Port of <c>inspect_cache_dir(subdir)</c>: the inspect_ai user cache directory (plus <paramref name="subdir"/>), created when missing.</summary>
    public static string InspectCacheDir(string? subdir)
    {
        var dir = UserCachePath(PackageName);
        if (!string.IsNullOrEmpty(subdir))
        {
            dir = Path.Combine(dir, subdir);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
