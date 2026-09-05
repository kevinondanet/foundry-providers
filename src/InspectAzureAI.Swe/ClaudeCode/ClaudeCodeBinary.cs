using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.ClaudeCode;

/// <summary>Port of inspect_swe <c>_util/checksum.py</c> <c>ChecksumMismatchError</c>: an integrity failure that the offline fallback must never mask.</summary>
public sealed class ChecksumMismatchException(string message) : Exception(message);

/// <summary>Port of <c>_util/agentbinary.py</c> <c>AgentBinaryVersion</c> (Claude Code never resolves a package archive).</summary>
public sealed record ClaudeCodeBinaryVersion(string Version, string ExpectedChecksum, string DownloadUrl);

/// <summary>
/// Port of inspect_swe <c>_claude_code/agentbinary.py</c> and the generic <c>_util/agentbinary.py</c> /
/// <c>_util/download.py</c>: discovers the CDN base URL from the install script, resolves <c>stable</c>/<c>latest</c>
/// pointers, verifies the manifest SHA-256, caches binaries on the host (pruned to the 3 most recently accessed)
/// and installs them into the sandbox. Version resolution is memoized per instance (one instance per agent
/// definition) instead of process-wide, so tests with different fake CDNs stay independent.
/// </summary>
public sealed partial class ClaudeCodeBinary
{
    public const string InstallScriptUrl = "https://claude.ai/install.sh";

    /// <summary>Used when the install script cannot be fetched (the value the script has carried since the CDN move).</summary>
    public const string FallbackDownloadBaseUrl = "https://downloads.claude.ai/claude-code-releases";

    public const string BinaryName = "claude";

    public const string AgentName = "claude code";

    /// <summary>Python passes <c>keep_count=3</c> (its docs say 5).</summary>
    public const int CacheKeepCount = 3;

    /// <summary>Delay before each retry; attempts = count + 1.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Largest download accepted (a real binary is ~200 MB); a bigger body is rejected while streaming rather than buffered whole.</summary>
    public const long MaxDownloadBytes = 1024L * 1024 * 1024;

    /// <summary>Suffix of the in-progress cache writes; such files are neither listed nor counted by the prune.</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>A temp file older than this was left by a crashed download and is removed by the next prune.</summary>
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    private static readonly Func<TimeSpan, CancellationToken, Task> SleepDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    private readonly string? _downloadBaseUrl;

    private readonly HttpMessageHandler? _httpHandler;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly Lazy<HttpClient> _client;

    private readonly object _sync = new();

    private readonly Dictionary<(string Version, string Platform), ClaudeCodeBinaryVersion> _resolved = [];

    private readonly Dictionary<(string Version, string Platform), (int Failures, Exception Error)> _failed = [];

    private readonly Dictionary<(string Version, string Platform), SemaphoreSlim> _resolutionGates = [];

    private readonly SemaphoreSlim _installGate = new(1, 1);

    public ClaudeCodeBinary(string? cacheDir = null, string? downloadBaseUrl = null, HttpMessageHandler? httpHandler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        CacheDir = cacheDir ?? DefaultCacheDir;
        _downloadBaseUrl = downloadBaseUrl;
        _httpHandler = httpHandler;
        _delay = delay ?? SleepDelay;
        _client = new Lazy<HttpClient>(CreateClient);
    }

    /// <summary>The port of <c>user_cache_path("inspect_swe") / "claude-code-downloads"</c>.</summary>
    public static string DefaultCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "inspect-azureai", "claude-code-downloads");

    public string CacheDir { get; }

    /// <summary>Port of the target pattern of <c>_claude_code_version</c>: a pointer name or semver with an optional pre-release suffix.</summary>
    public static bool IsValidVersionTarget(string target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return VersionTargetRegex().IsMatch(target);
    }

    public static void ValidateVersionTarget(string target)
    {
        if (!IsValidVersionTarget(target))
        {
            throw new InvalidOperationException("Invalid version target (must be 'stable', 'latest', or a semver version number)");
        }
    }

    /// <summary>Port of the regexes of <c>_claude_code_download_base_url</c>: the current <c>DOWNLOAD_BASE_URL</c> assignment, then the legacy <c>GCS_BUCKET</c>.</summary>
    public static string? ParseDownloadBaseUrl(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        foreach (var regex in new[] { DownloadBaseUrlRegex(), GcsBucketRegex() })
        {
            var match = regex.Match(script);
            if (match.Success)
            {
                return match.Groups[2].Value;
            }
        }

        return null;
    }

    /// <summary>Port of <c>_checksum_for_platform</c> over a parsed <c>manifest.json</c>.</summary>
    public static string ChecksumForPlatform(JsonNode manifest, string platform)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(platform);
        if (manifest is not JsonObject root || root["platforms"] is not JsonObject platforms)
        {
            throw new InvalidOperationException("Invalid claude code manifest: expected an object with a 'platforms' object.");
        }

        if (platforms[platform] is not JsonObject entry)
        {
            throw new InvalidOperationException($"Platform '{platform}' not found in manifest.");
        }

        return entry["checksum"]?.GetValue<string>()
            ?? throw new InvalidOperationException($"Invalid claude code manifest: platform '{platform}' has no checksum.");
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Port of <c>verify_checksum</c>.</summary>
    public static bool VerifyChecksum(ReadOnlySpan<byte> data, string expectedChecksum)
    {
        ArgumentNullException.ThrowIfNull(expectedChecksum);
        return string.Equals(Sha256Hex(data), expectedChecksum, StringComparison.Ordinal);
    }

    /// <summary>The configured base URL, else the install-script discovery; a failed fetch falls back to <see cref="FallbackDownloadBaseUrl"/>, a script without a recognizable assignment is an error.</summary>
    public async Task<string> ResolveDownloadBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        if (_downloadBaseUrl is not null)
        {
            return _downloadBaseUrl;
        }

        string script;
        try
        {
            script = await DownloadTextAsync(InstallScriptUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransportFailure(ex, cancellationToken))
        {
            ProviderLogger.Warning($"Unable to fetch {InstallScriptUrl} ({ex.Message}); using the fallback claude code download base URL {FallbackDownloadBaseUrl}.");
            return FallbackDownloadBaseUrl;
        }

        return ParseDownloadBaseUrl(script) ?? throw new InvalidOperationException("Unable to determine download base URL for claude code.");
    }

    /// <summary>
    /// Port of <c>_claude_code_version</c>: pointers are fetched as text and used verbatim (not stripped); a concrete
    /// target is the version. The pointer's value is validated as a semver string (Python trusts it): it is
    /// interpolated into URLs and an install path, so a stray newline or shell metacharacter must not get through.
    /// </summary>
    public async Task<string> ResolveVersionAsync(string baseUrl, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(target);
        ValidateVersionTarget(target);
        if (target is not ("stable" or "latest"))
        {
            return target;
        }

        var version = await DownloadTextAsync($"{baseUrl}/{target}", cancellationToken).ConfigureAwait(false);
        if (!ResolvedVersionRegex().IsMatch(version))
        {
            throw new InvalidOperationException($"The '{target}' pointer at {baseUrl} did not resolve to a version number: {PythonRepr(version)}");
        }

        return version;
    }

    /// <summary>
    /// Port of <c>_resolve_agent_binary_version</c>: memoized per (version, platform) for the instance lifetime;
    /// concurrent callers serialize per key, and callers already queued behind a failing resolution share its
    /// exception rather than each retrying a rate-limited CDN; a caller arriving after the failure retries.
    /// </summary>
    public async Task<ClaudeCodeBinaryVersion> ResolveAsync(string version, string platform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(platform);
        var key = (version, platform);
        SemaphoreSlim gate;
        int failuresAtArrival;
        lock (_sync)
        {
            if (_resolved.TryGetValue(key, out var cached))
            {
                return cached;
            }

            failuresAtArrival = _failed.TryGetValue(key, out var failure) ? failure.Failures : 0;
            if (!_resolutionGates.TryGetValue(key, out var existing))
            {
                existing = new SemaphoreSlim(1, 1);
                _resolutionGates[key] = existing;
            }

            gate = existing;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Exception? sharedFailure = null;
            lock (_sync)
            {
                if (_resolved.TryGetValue(key, out var cached))
                {
                    return cached;
                }

                if (_failed.TryGetValue(key, out var failure) && failure.Failures > failuresAtArrival)
                {
                    sharedFailure = failure.Error;
                }
            }

            if (sharedFailure is not null)
            {
                ExceptionDispatchInfo.Capture(sharedFailure).Throw();
            }

            ClaudeCodeBinaryVersion resolved;
            try
            {
                resolved = await ResolveUncachedAsync(version, platform, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (_sync)
                {
                    _failed[key] = (failuresAtArrival + 1, ex);
                }

                throw;
            }

            lock (_sync)
            {
                _resolved[key] = resolved;
            }

            return resolved;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Port of <c>download_agent_binary_async</c>: resolve, serve a verified cache hit, else download, verify and cache.</summary>
    public async Task<(byte[] Data, ClaudeCodeBinaryVersion Resolved)> DownloadAsync(string version, string platform, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveAsync(version, platform, cancellationToken).ConfigureAwait(false);
        var cachePath = CachedBinaryPath(resolved.Version, platform);
        var data = ReadCachedFile(cachePath, resolved.ExpectedChecksum);
        if (data is null)
        {
            data = await DownloadFileAsync(resolved.DownloadUrl, cancellationToken).ConfigureAwait(false);
            if (!VerifyChecksum(data, resolved.ExpectedChecksum))
            {
                throw new ChecksumMismatchException("Checksum verification failed");
            }

            WriteCachedFile(data, cachePath);
            ProviderLogger.Info($"Downloaded {AgentName} binary: {resolved.Version} ({platform})");
        }
        else
        {
            ProviderLogger.Info($"Used {AgentName} binary from cache: {resolved.Version} ({platform})");
        }

        return (data, resolved);
    }

    /// <summary>
    /// Port of <c>download_file</c>: 4 attempts with 1, 2, 4 s delays on transport errors and 5xx; a 4xx is permanent
    /// and fails immediately. The body is read in chunks against <see cref="MaxDownloadBytes"/> (the base URL is
    /// scraped from a remote script, so an unexpected multi-gigabyte body is rejected, not buffered).
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var attempts = RetryDelays.Count + 1;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await _client.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"GET {url} failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
                }

                if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
                {
                    throw new InvalidOperationException($"GET {url} announced {response.Content.Headers.ContentLength} bytes, more than the {MaxDownloadBytes} byte download limit.");
                }

                using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[256 * 1024];
                int read;
                while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxDownloadBytes)
                    {
                        throw new InvalidOperationException($"GET {url} exceeded the {MaxDownloadBytes} byte download limit.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
            catch (Exception ex) when (attempt < attempts - 1 && IsTransientFailure(ex, cancellationToken))
            {
                await _delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<string> DownloadTextAsync(string url, CancellationToken cancellationToken = default) =>
        Encoding.UTF8.GetString(await DownloadFileAsync(url, cancellationToken).ConfigureAwait(false));

    public string CachedBinaryPath(string version, string platform) => Path.Combine(CacheDir, $"{BinaryName}-{version}-{platform}");

    /// <summary>Port of <c>list_cached_binaries</c> (the <c>claude-*</c> glob), minus in-progress <see cref="TempSuffix"/> files.</summary>
    public IReadOnlyList<string> ListCachedBinaries() =>
        Directory.Exists(CacheDir)
            ? Directory.GetFiles(CacheDir, $"{BinaryName}-*").Where(file => !file.EndsWith(TempSuffix, StringComparison.Ordinal)).ToArray()
            : [];

    /// <summary>Port of <c>read_cached_binary</c>: a hit is touched; a checksum mismatch deletes the file and misses.</summary>
    public byte[]? ReadCachedBinary(string version, string platform, string? expectedChecksum) =>
        ReadCachedFile(CachedBinaryPath(version, platform), expectedChecksum);

    /// <summary>Port of <c>write_cached_file</c>: writes atomically, then prunes to the <see cref="CacheKeepCount"/> most recently accessed.</summary>
    public void WriteCachedFile(byte[] data, string cachePath)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(cachePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? CacheDir);
        var temp = cachePath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        File.WriteAllBytes(temp, data);
        File.Move(temp, cachePath, overwrite: true);
        PruneCache();
    }

    /// <summary>
    /// Port of <c>ensure_agent_binary_installed</c> for Claude Code: "auto"/"sandbox" first look for an installed
    /// <c>claude</c>; a concrete version is served from the host cache without a network round-trip; otherwise
    /// resolve and download (falling back to an unverified cached binary for a concrete version when the network
    /// fails, but never on a checksum mismatch); then write the binary into the sandbox and <c>chmod +x</c> it as root.
    /// </summary>
    public async Task<string> EnsureInstalledAsync(ISandboxEnvironment sandbox, string version = "auto", string? user = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(version);
        if (version is "auto" or "sandbox")
        {
            var which = await sandbox.ExecAsync(SandboxUtil.BashCommand($"which {BinaryName}"), user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (which.Success)
            {
                var binaryPath = which.Stdout.Trim();
                ProviderLogger.Info($"Using {AgentName} installed in sandbox: {binaryPath}");
                return binaryPath;
            }

            if (version == "sandbox")
            {
                throw new InvalidOperationException($"unable to locate {AgentName} in sandbox");
            }

            version = "stable";
        }

        ValidateVersionTarget(version);
        var platform = await SandboxUtil.DetectPlatformAsync(sandbox, cancellationToken).ConfigureAwait(false);
        var concrete = version is not ("stable" or "latest");

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[]? binaryBytes = null;
            string resolvedVersion;
            if (concrete)
            {
                binaryBytes = ReadCachedBinary(version, platform, null);
                if (binaryBytes is not null)
                {
                    ProviderLogger.Info($"Used {AgentName} binary from cache: {version} ({platform})");
                }
            }

            if (binaryBytes is null)
            {
                try
                {
                    var (data, resolved) = await DownloadAsync(version, platform, cancellationToken).ConfigureAwait(false);
                    binaryBytes = data;
                    resolvedVersion = resolved.Version;
                }
                catch (Exception ex) when (ex is not ChecksumMismatchException and not OperationCanceledException)
                {
                    binaryBytes = concrete ? ReadCachedBinary(version, platform, null) : null;
                    if (binaryBytes is null)
                    {
                        throw;
                    }

                    ProviderLogger.Warning(
                        $"{AgentName} {version} could not be resolved or downloaded over the network; installing the cached "
                        + $"binary at {CachedBinaryPath(version, platform)} ({platform}) WITHOUT checksum verification, "
                        + "because no digest can be obtained offline to verify it.");
                    resolvedVersion = version;
                }
            }
            else
            {
                resolvedVersion = version;
            }

            var installPath = $"{SandboxUtil.SandboxInstallDir}/{BinaryName}-{resolvedVersion}-{platform}";
            await sandbox.WriteFileAsync(installPath, binaryBytes, cancellationToken).ConfigureAwait(false);
            // argv, not a shell string: the path embeds a value that came from the network
            var chmod = await sandbox.ExecAsync(["chmod", "+x", installPath], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!chmod.Success)
            {
                throw new InvalidOperationException($"Error executing sandbox command chmod +x {installPath}: {chmod.Stderr}");
            }

            return installPath;
        }
        finally
        {
            _installGate.Release();
        }
    }

    private async Task<ClaudeCodeBinaryVersion> ResolveUncachedAsync(string target, string platform, CancellationToken cancellationToken)
    {
        var baseUrl = await ResolveDownloadBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        var version = await ResolveVersionAsync(baseUrl, target, cancellationToken).ConfigureAwait(false);
        var manifestText = await DownloadTextAsync($"{baseUrl}/{version}/manifest.json", cancellationToken).ConfigureAwait(false);
        JsonNode manifest;
        try
        {
            manifest = JsonNode.Parse(manifestText) ?? throw new InvalidOperationException("Invalid claude code manifest: empty document.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"Invalid claude code manifest: {ex.Message}", ex);
        }

        var checksum = ChecksumForPlatform(manifest, platform);
        return new ClaudeCodeBinaryVersion(version, checksum, $"{baseUrl}/{version}/{platform}/{BinaryName}");
    }

    private static byte[]? ReadCachedFile(string cachePath, string? expectedChecksum)
    {
        if (!File.Exists(cachePath))
        {
            return null;
        }

        var data = File.ReadAllBytes(cachePath);
        if (expectedChecksum is null || VerifyChecksum(data, expectedChecksum))
        {
            // Python touch(): the cache is pruned by access time, so a hit must count as recent use.
            var now = DateTime.UtcNow;
            File.SetLastAccessTimeUtc(cachePath, now);
            File.SetLastWriteTimeUtc(cachePath, now);
            return data;
        }

        File.Delete(cachePath);
        return null;
    }

    private void PruneCache()
    {
        foreach (var orphan in Directory.GetFiles(CacheDir, $"{BinaryName}-*{TempSuffix}"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(orphan) > StaleTempAge)
            {
                File.Delete(orphan);
            }
        }

        var files = ListCachedBinaries();
        if (files.Count <= CacheKeepCount)
        {
            return;
        }

        foreach (var stale in files.OrderBy(File.GetLastAccessTimeUtc).Take(files.Count - CacheKeepCount))
        {
            File.Delete(stale);
        }
    }

    private static string PythonRepr(string value) =>
        "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler = _httpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = ConnectTimeout };
        return new HttpClient(handler, disposeHandler: _httpHandler is null) { Timeout = HttpTimeout };
    }

    private static bool IsTransientFailure(Exception ex, CancellationToken cancellationToken) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status >= 500,
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };

    private static bool IsTransportFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is HttpRequestException or IOException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    // \z rather than $: a trailing newline (as in a pointer file) must not slip through the end anchor.
    [GeneratedRegex(@"^(stable|latest|[0-9]+\.[0-9]+\.[0-9]+(-\S+)?)\z")]
    private static partial Regex VersionTargetRegex();

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.+-]+)?\z")]
    private static partial Regex ResolvedVersionRegex();

    [GeneratedRegex("""(?:^|\n)\s*(?:export\s+)?DOWNLOAD_BASE_URL\s*=\s*(["'])(https://[^"'\s]+)\1""")]
    private static partial Regex DownloadBaseUrlRegex();

    [GeneratedRegex("""(?:^|\n)\s*(?:export\s+)?GCS_BUCKET\s*=\s*(["'])(https://[^"'\s]+)\1""")]
    private static partial Regex GcsBucketRegex();
}
