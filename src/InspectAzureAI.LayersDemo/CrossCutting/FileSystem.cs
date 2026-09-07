// ============================================================================
//  CROSS-CUTTING CONCERN 3 of 4: THE FILESYSTEM ABSTRACTION
//  Python: inspect_ai/_util/file.py  (a thin wrapper over fsspec)
//
//  Log directories and dataset paths are URIs, not OS paths. "logs/" and
//  "s3://bucket/logs" and "memory://logs" are all handled by the same
//  read/write/list calls, so the engine that writes a log and the viewer that
//  reads it back never care where the bytes live. The dataset loader (layer 4)
//  and the log writer (layer 3) and the view server (layers 1-2) all go
//  through here — another concern that cuts across every layer.
//
//  The demo registers two schemes: "file" (the local disk) and "memory" (an
//  in-process dictionary). Adding "s3" would mean adding one more class here
//  and nothing anywhere else.
// ============================================================================
using inspect_ai._util._async;
using inspect_ai._util.display;

namespace inspect_ai._util.file;

/// <summary>What every backing store must be able to do (Python: fsspec's AbstractFileSystem, trimmed).</summary>
internal interface IFileSystem
{
    string Scheme { get; }
    bool Exists(string path);
    string ReadText(string path);
    void WriteText(string path, string text);
    IEnumerable<string> List(string directory);
}

/// <summary>The local disk. Relative paths resolve against the working
/// directory first and the application directory second, so `dotnet run`
/// finds the sample dataset either way.</summary>
internal sealed class LocalFileSystem : IFileSystem
{
    public string Scheme => "file";

    private static string Full(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var cwd = Path.GetFullPath(path);
        return File.Exists(cwd) || Directory.Exists(cwd) ? cwd : Path.Combine(AppContext.BaseDirectory, path);
    }

    public bool Exists(string path) => File.Exists(Full(path)) || Directory.Exists(Full(path));
    public string ReadText(string path) => File.ReadAllText(Full(path));

    public void WriteText(string path, string text)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public IEnumerable<string> List(string directory)
        => Directory.Exists(Full(directory))
            ? Directory.EnumerateFiles(Full(directory)).OrderBy(f => f)
            : Enumerable.Empty<string>();
}

/// <summary>An in-process store. Handy for tests, and for this demo, which
/// writes its eval log here by default so nothing touches your disk.</summary>
internal sealed class MemoryFileSystem : IFileSystem
{
    public string Scheme => "memory";

    // No lock: single event loop (see EventLoop.cs).
    private readonly Dictionary<string, string> _files = new();

    public bool Exists(string path) => _files.ContainsKey(path) || _files.Keys.Any(k => k.StartsWith(path.TrimEnd('/') + "/"));
    public string ReadText(string path) => _files.TryGetValue(path, out var t) ? t : throw new FileNotFoundException(path);
    public void WriteText(string path, string text) { SingleThreadEventLoop.AssertOnLoopThread("MemoryFileSystem"); _files[path] = text; }
    public IEnumerable<string> List(string directory)
        => _files.Keys.Where(k => k.StartsWith(directory.TrimEnd('/') + "/")).OrderBy(k => k);
}

/// <summary>The façade every layer calls. It splits "scheme://path", picks
/// the backing store, and forwards. Python's file.py does the same with
/// fsspec's `filesystem(protocol)`.</summary>
internal static class FileSystems
{
    private static readonly Dictionary<string, IFileSystem> Schemes = new();   // no lock: single event loop

    public static void Register(IFileSystem fs)
    {
        Schemes[fs.Scheme] = fs;
        Display.Step("x  _util.file", $"registered filesystem scheme '{fs.Scheme}://'");
    }

    /// <summary>Split a URI into (store, path-within-store). A bare path is local.</summary>
    public static (IFileSystem Fs, string Path) Resolve(string uri)
    {
        var index = uri.IndexOf("://", StringComparison.Ordinal);
        var scheme = index < 0 ? "file" : uri[..index];
        var path = index < 0 ? uri : uri[(index + 3)..];
        return Schemes.TryGetValue(scheme, out var fs)
            ? (fs, path)
            : throw new NotSupportedException($"No filesystem registered for '{scheme}://'.");
    }

    public static bool Exists(string uri) { var (fs, p) = Resolve(uri); return fs.Exists(p); }
    public static string ReadText(string uri) { var (fs, p) = Resolve(uri); return fs.ReadText(p); }
    public static void WriteText(string uri, string text) { var (fs, p) = Resolve(uri); fs.WriteText(p, text); }

    /// <summary>List entries, returned as full URIs so callers can read them back through this façade.</summary>
    public static IEnumerable<string> List(string uri)
    {
        var (fs, p) = Resolve(uri);
        return fs.List(p).Select(entry => fs.Scheme == "file" ? entry : $"{fs.Scheme}://{entry}");
    }

    public static string Join(string directoryUri, string name)
        => directoryUri.TrimEnd('/') + "/" + name;
}
