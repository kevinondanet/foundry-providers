using System.Text.Json;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Model.Cache;

/// <summary>
/// Port of the <c>cache_store</c> / <c>cache_fetch</c> half of <c>model/_cache.py</c>: the on-disk prompt cache
/// <see cref="Model"/> consults before a generate call and writes after one. Entries live under
/// <c>{CacheOps.CachePath()}/{model}/{key}</c>; see <see cref="CacheOps"/> for where the root is and for maintenance.
/// A cache failure never fails a generate: store and fetch problems are logged and reported as false / a miss.
/// </summary>
public static class PromptCache
{
    /// <summary>Suffix of the temporary file an entry is written to before being renamed into place.</summary>
    internal const string TempSuffix = ".tmp";

    /// <summary>The file an entry is (or would be) stored in.</summary>
    public static string EntryPath(CacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Path.Combine(CacheOps.CachePath(entry.Model), entry.Key);
    }

    /// <summary>
    /// Port of <c>cache_store</c>: writes <paramref name="output"/> under <paramref name="entry"/>'s key with the
    /// policy's expiry. Returns false without writing when any choice stopped for <c>content_filter</c> (refusal
    /// retry loops re-call generate with identical inputs, and a cached refusal would be replayed on every retry),
    /// and false with a logged warning when the write fails. The entry is written to a temporary file and renamed
    /// into place so a concurrent reader never sees a partial entry.
    /// </summary>
    public static async Task<bool> StoreAsync(CacheEntry entry, ModelOutput output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(output);
        if (output.Choices.Any(choice => choice.StopReason == StopReason.ContentFilter))
        {
            return false;
        }

        var path = EntryPath(entry);
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{entry.Key}.{Guid.NewGuid():N}{TempSuffix}");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(temp, CacheFile.Serialize(CacheExpiry(entry.Policy), output), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
        {
            TryDelete(temp);
            ProviderLogger.Warning($"Failed to write prompt cache entry {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Port of <c>cache_fetch</c>: the cached output for <paramref name="entry"/>, or null on a miss. An expired
    /// entry is deleted (it can never be served again) and reported as a miss. An entry that exists but cannot be
    /// read — corrupt JSON, the wrong shape, an unreadable file — is a miss with a logged warning; the next
    /// successful generate overwrites it.
    /// </summary>
    public static async Task<ModelOutput?> FetchAsync(CacheEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var path = EntryPath(entry);
        if (!File.Exists(path))
        {
            return null;
        }

        CacheFile file;
        try
        {
            file = CacheFile.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ProviderLogger.Warning($"Ignoring unreadable prompt cache entry {path}: {ex.Message}");
            return null;
        }

        if (IsExpired(file.Expiry))
        {
            TryDelete(path);
            return null;
        }

        return file.Output;
    }

    /// <summary>Port of <c>_cache_expiry</c>: when an entry stored now under <paramref name="policy"/> expires (null: never).</summary>
    public static DateTimeOffset? CacheExpiry(CachePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.ExpirySeconds is { } seconds ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;
    }

    /// <summary>Port of <c>_is_expired</c>: a null expiry never expires.</summary>
    public static bool IsExpired(DateTimeOffset? expiry) => expiry is { } e && DateTimeOffset.UtcNow > e;

    /// <summary>Reads an entry for maintenance; a missing file is null, an unreadable one is null with a logged warning.</summary>
    internal static CacheFile? TryRead(string path)
    {
        try
        {
            return CacheFile.Parse(File.ReadAllText(path));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ProviderLogger.Warning($"Skipping unreadable prompt cache entry {path}: {ex.Message}");
            return null;
        }
    }

    internal static void TryDelete(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderLogger.Warning($"Failed to delete prompt cache entry {path}: {ex.Message}");
        }
    }
}
