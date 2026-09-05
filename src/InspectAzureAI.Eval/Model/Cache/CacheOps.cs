using System.Globalization;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Cache;

/// <summary>The size of one model's cache directory (port of the <c>(model, bytes)</c> tuples of <c>cache_size</c>).</summary>
public sealed record ModelCacheSize(string Model, long Bytes);

/// <summary>
/// Port of the maintenance half of <c>model/_cache.py</c> (<c>cache_path</c>, <c>cache_clear</c>, <c>cache_size</c>,
/// <c>cache_list_expired</c>, <c>cache_prune</c>) plus the size formatting of <c>_cli/cache.py</c>. The cache root is
/// <c>$INSPECT_CACHE_DIR/generate</c> when the variable is set, otherwise the user cache directory of
/// <see cref="AppDirs"/> (<c>inspect_ai/generate</c>); model names become subdirectories, so <c>openai/gpt-4</c> nests.
/// </summary>
public static class CacheOps
{
    /// <summary>The environment variable overriding the cache root.</summary>
    public const string CacheDirVar = "INSPECT_CACHE_DIR";

    /// <summary>
    /// Port of <c>cache_path</c>: the cache root (created when missing), or the directory for <paramref name="model"/>
    /// beneath it (not created; the model directory appears on the first store).
    /// </summary>
    public static string CachePath(string model = "")
    {
        ArgumentNullException.ThrowIfNull(model);
        var env = Environment.GetEnvironmentVariable(CacheDirVar);
        string root;
        if (!string.IsNullOrEmpty(env))
        {
            root = Path.Combine(env, "generate");
            Directory.CreateDirectory(root);
        }
        else
        {
            root = AppDirs.InspectCacheDir("generate");
        }

        return model.Length == 0 ? root : Path.Combine(root, model);
    }

    /// <summary>
    /// Port of <c>_path_is_in_cache</c>: true when <paramref name="path"/> lies strictly below the cache root, so a
    /// model such as <c>../../home/user</c> can never direct a clear or a listing outside the cache.
    /// </summary>
    public static bool IsInCache(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = FullPath(CachePath());
        var full = FullPath(path);
        return full.Length > root.Length && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    /// <summary>
    /// Port of <c>cache_clear</c>: deletes the whole cache (<paramref name="model"/> empty) or one model's directory.
    /// Returns true when something was deleted; false when the directory does not exist, when the model resolves
    /// outside the cache, or (with a logged warning) when deletion fails.
    /// </summary>
    public static bool CacheClear(string model = "")
    {
        ArgumentNullException.ThrowIfNull(model);
        try
        {
            var path = CachePath(model);
            if ((model.Length == 0 || IsInCache(path)) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderLogger.Warning($"Failed to clear cache: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Port of <c>cache_size</c>: the size of the cache by model directory. With neither argument the whole cache is
    /// measured. <paramref name="subdirs"/> keeps only directories whose path contains one of the given strings
    /// (generally model names); <paramref name="files"/> measures those files, grouped by their parent directory,
    /// and on its own suppresses the directory scan. Directories without files are ignored; results are sorted by name.
    /// </summary>
    public static IReadOnlyList<ModelCacheSize> CacheSize(IReadOnlyList<string>? subdirs = null, IReadOnlyList<string>? files = null)
    {
        subdirs ??= [];
        files ??= [];
        var sizes = files.Count > 0 && subdirs.Count == 0 ? [] : SizeDirectoriesOnly(subdirs);
        sizes.AddRange(SizeFilesOnly(files));
        return sizes.OrderBy(size => size.Model, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Port of <c>cache_list_expired</c>: every entry past its expiry, optionally only those directly inside the
    /// directories of the given models (a model that resolves outside the cache is ignored; if none remain, nothing
    /// is listed). Entries that cannot be read are skipped with a logged warning.
    /// </summary>
    public static IReadOnlyList<string> CacheListExpired(IReadOnlyList<string>? filterBy = null)
    {
        filterBy ??= [];
        var filterPaths = filterBy
            .Select(model => CachePath(model))
            .Where(IsInCache)
            .Select(FullPath)
            .ToHashSet(StringComparer.Ordinal);
        var expired = new List<string>();
        if (filterBy.Count > 0 && filterPaths.Count == 0)
        {
            return expired;
        }

        foreach (var directory in Walk(CachePath()))
        {
            if (filterPaths.Count > 0 && !filterPaths.Contains(FullPath(directory)))
            {
                continue;
            }

            foreach (var file in EntryFiles(directory))
            {
                if (PromptCache.TryRead(file) is { } entry && PromptCache.IsExpired(entry.Expiry))
                {
                    expired.Add(file);
                }
            }
        }

        return expired;
    }

    /// <summary>
    /// Port of <c>cache_prune</c>: deletes expired entries — the given <paramref name="files"/>, or every expired entry
    /// in the cache when none are given. Each file is re-read and only deleted if it is actually expired.
    /// </summary>
    public static void CachePrune(IReadOnlyList<string>? files = null)
    {
        var targets = files is { Count: > 0 } ? files : CacheListExpired();
        foreach (var file in targets)
        {
            if (PromptCache.TryRead(file) is { } entry && PromptCache.IsExpired(entry.Expiry))
            {
                PromptCache.TryDelete(file);
            }
        }
    }

    /// <summary>Port of the CLI's <c>_readable_size</c>: <c>512  B</c>, <c>1.50 KB</c>, <c>2.00 MB</c>.</summary>
    public static string ReadableSize(long bytes)
    {
        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes}  B");
        }

        if (bytes < 1024 * 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F2} KB");
        }

        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F2} MB");
    }

    private static List<ModelCacheSize> SizeDirectoriesOnly(IReadOnlyList<string> filterBy)
    {
        var root = CachePath();
        var sizes = new List<ModelCacheSize>();
        foreach (var directory in Walk(root))
        {
            if (!EntryFiles(directory).Any())
            {
                continue;
            }

            var path = Slashes(FullPath(directory));
            if (filterBy.Count > 0 && !filterBy.Any(model => path.Contains(model, StringComparison.Ordinal)))
            {
                continue;
            }

            var bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(IsEntryFile)
                .Sum(file => new FileInfo(file).Length);
            sizes.Add(new ModelCacheSize(ModelName(root, directory), bytes));
        }

        return sizes;
    }

    private static List<ModelCacheSize> SizeFilesOnly(IReadOnlyList<string> files)
    {
        var sizes = new List<ModelCacheSize>();
        if (files.Count == 0)
        {
            return sizes;
        }

        var root = CachePath();
        var rootFull = FullPath(root);
        var bytesByModel = new Dictionary<string, long>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var file in files)
        {
            var full = FullPath(file);
            if (!full.Contains(rootFull, StringComparison.Ordinal) || !File.Exists(full))
            {
                continue;
            }

            var model = ModelName(root, Path.GetDirectoryName(full)!);
            if (!bytesByModel.ContainsKey(model))
            {
                order.Add(model);
            }

            bytesByModel[model] = bytesByModel.GetValueOrDefault(model) + new FileInfo(full).Length;
        }

        sizes.AddRange(order.Select(model => new ModelCacheSize(model, bytesByModel[model])));
        return sizes;
    }

    /// <summary>The root and every directory beneath it, top-down (the port of <c>os.walk</c>).</summary>
    private static IEnumerable<string> Walk(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        yield return root;
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            yield return directory;
        }
    }

    private static IEnumerable<string> EntryFiles(string directory) => Directory.EnumerateFiles(directory).Where(IsEntryFile);

    /// <summary>An in-progress store's temporary file is not an entry.</summary>
    private static bool IsEntryFile(string file) => !file.EndsWith(PromptCache.TempSuffix, StringComparison.Ordinal);

    /// <summary>The directory relative to the root with forward slashes (<c>openai/gpt-4</c>), like Python's <c>replace(f"{root}/", "")</c>.</summary>
    private static string ModelName(string root, string directory)
    {
        var rootFull = FullPath(root);
        var full = FullPath(directory);
        var prefix = rootFull + Path.DirectorySeparatorChar;
        return Slashes(full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..] : full);
    }

    private static string FullPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string Slashes(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
