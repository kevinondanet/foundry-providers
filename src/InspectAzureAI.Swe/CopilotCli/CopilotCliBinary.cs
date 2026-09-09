using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.CopilotCli;

/// <summary>
/// The Copilot CLI counterpart of <see cref="ClaudeCodeBinary"/> (inspect_swe <c>_util/agentbinary.py</c> /
/// <c>_util/download.py</c>) for a GitHub release: the asset is <c>copilot-linux-{x64,arm64}.tar.gz</c> (or the
/// <c>linuxmusl</c> variant) under <c>{ReleaseBaseUrl}/v{version}/</c>, verified against the release's
/// <c>SHA256SUMS.txt</c>, cached on the host as <c>copilot-{version}-{platform}.tar.gz</c> beside a <c>.sha256</c>
/// digest file (a cached tarball whose digest file matches is served without any network), written into the
/// sandbox and extracted there with <c>tar</c> into <c>{SandboxInstallDir}/copilot-{version}-{platform}/</c>. The
/// archive holds a single self-contained <c>copilot</c> executable (the 1.0.83 probe's <c>tar tzf</c>).
/// </summary>
public sealed partial class CopilotCliBinary
{
    public const string BinaryName = "copilot";

    public const string AgentName = "copilot cli";

    public const string ChecksumsFileName = "SHA256SUMS.txt";

    public const string ArchiveSuffix = ".tar.gz";

    public const string DigestSuffix = ".sha256";

    /// <summary>Archives kept in the cache (the most recently accessed), as the Claude Code cache does.</summary>
    public const int CacheKeepCount = 3;

    /// <summary>Delay before each retry; attempts = count + 1.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>Generous: the host measured 50-200 KB/s earlier and the tarball is ~96 MB.</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Largest download accepted (the tarball is ~96 MB); a bigger body is rejected while streaming rather than buffered whole.</summary>
    public const long MaxDownloadBytes = 1024L * 1024 * 1024;

    public const string TempSuffix = ".tmp";

    public static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    private static readonly Func<TimeSpan, CancellationToken, Task> SleepDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    private readonly HttpMessageHandler? _httpHandler;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly Lazy<HttpClient> _client;

    private readonly SemaphoreSlim _installGate = new(1, 1);

    public CopilotCliBinary(string? cacheDir = null, string? releaseBaseUrl = null, HttpMessageHandler? httpHandler = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        CacheDir = cacheDir ?? DefaultCacheDir;
        ReleaseBaseUrl = (releaseBaseUrl ?? CopilotCliOptions.DefaultReleaseBaseUrl).TrimEnd('/');
        _httpHandler = httpHandler;
        _delay = delay ?? SleepDelay;
        _client = new Lazy<HttpClient>(CreateClient);
    }

    /// <summary>The sibling of the Claude Code cache: <c>~/.cache/inspect-azureai/copilot-cli-downloads</c>.</summary>
    public static string DefaultCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "inspect-azureai", "copilot-cli-downloads");

    public string CacheDir { get; }

    public string ReleaseBaseUrl { get; }

    /// <summary>A concrete release version (semver with an optional pre-release suffix; no leading <c>v</c>).</summary>
    public static bool IsValidVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return VersionRegex().IsMatch(version);
    }

    public static void ValidateVersion(string version)
    {
        if (!IsValidVersion(version))
        {
            throw new InvalidOperationException("Invalid version target (must be 'auto', 'sandbox', or a semver version number such as '1.0.83')");
        }
    }

    /// <summary>The release asset for a <see cref="SandboxUtil.DetectPlatformAsync"/> platform: <c>copilot-linux-x64.tar.gz</c>, <c>copilot-linuxmusl-arm64.tar.gz</c>, ...</summary>
    public static string AssetName(string platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return platform switch
        {
            "linux-x64" => "copilot-linux-x64.tar.gz",
            "linux-arm64" => "copilot-linux-arm64.tar.gz",
            "linux-x64-musl" => "copilot-linuxmusl-x64.tar.gz",
            "linux-arm64-musl" => "copilot-linuxmusl-arm64.tar.gz",
            _ => throw new PlatformNotSupportedException($"No copilot cli release asset for platform '{platform}'."),
        };
    }

    /// <summary>The digest of <paramref name="assetName"/> in a <c>SHA256SUMS.txt</c> (<c>&lt;hex&gt;  &lt;name&gt;</c> lines).</summary>
    public static string ParseChecksums(string checksums, string assetName)
    {
        ArgumentNullException.ThrowIfNull(checksums);
        ArgumentNullException.ThrowIfNull(assetName);
        foreach (var rawLine in checksums.Split('\n'))
        {
            var parts = rawLine.Trim().Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('*') == assetName && HexDigestRegex().IsMatch(parts[0]))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InvalidOperationException($"Asset '{assetName}' not found in {ChecksumsFileName}.");
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static bool VerifyChecksum(ReadOnlySpan<byte> data, string expectedChecksum)
    {
        ArgumentNullException.ThrowIfNull(expectedChecksum);
        return string.Equals(Sha256Hex(data), expectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public string ReleaseUrl(string version, string fileName) => $"{ReleaseBaseUrl}/v{version}/{fileName}";

    public string CachedArchivePath(string version, string platform) => Path.Combine(CacheDir, $"{BinaryName}-{version}-{platform}{ArchiveSuffix}");

    public string CachedDigestPath(string version, string platform) => CachedArchivePath(version, platform) + DigestSuffix;

    public string CachedChecksumsPath(string version) => Path.Combine(CacheDir, $"SHA256SUMS-{version}.txt");

    /// <summary>The cached archives (not digests, checksum lists or in-progress temp files).</summary>
    public IReadOnlyList<string> ListCachedArchives() =>
        Directory.Exists(CacheDir)
            ? Directory.GetFiles(CacheDir, $"{BinaryName}-*{ArchiveSuffix}").Where(file => !file.EndsWith(TempSuffix, StringComparison.Ordinal)).ToArray()
            : [];

    /// <summary>
    /// The cached archive when its digest file exists and matches its content (no network); a mismatch deletes
    /// both. Null when there is nothing usable in the cache.
    /// </summary>
    public byte[]? ReadCachedArchive(string version, string platform)
    {
        var archivePath = CachedArchivePath(version, platform);
        var digestPath = CachedDigestPath(version, platform);
        if (!File.Exists(archivePath) || !File.Exists(digestPath))
        {
            return null;
        }

        var expected = File.ReadAllText(digestPath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var data = File.ReadAllBytes(archivePath);
        if (HexDigestRegex().IsMatch(expected) && VerifyChecksum(data, expected))
        {
            var now = DateTime.UtcNow;
            File.SetLastAccessTimeUtc(archivePath, now);
            File.SetLastWriteTimeUtc(archivePath, now);
            return data;
        }

        ProviderLogger.Warning($"Cached {AgentName} archive {archivePath} does not match its digest file; discarding it.");
        File.Delete(archivePath);
        File.Delete(digestPath);
        return null;
    }

    /// <summary>
    /// Writes the digest file, then the archive, each through a temp file and an atomic move, then prunes to the
    /// <see cref="CacheKeepCount"/> most recently accessed archives. Digest first: a concurrent reader (two evals
    /// sharing the cache) then never sees a verified archive paired with a stale digest and discards it for nothing.
    /// </summary>
    public void WriteCachedArchive(byte[] data, string version, string platform, string checksum)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(checksum);
        Directory.CreateDirectory(CacheDir);
        WriteDigest(CachedDigestPath(version, platform), checksum);
        var archivePath = CachedArchivePath(version, platform);
        var temp = archivePath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        File.WriteAllBytes(temp, data);
        File.Move(temp, archivePath, overwrite: true);
        PruneCache();
    }

    /// <summary>The digest file is written whole or not at all (temp file plus atomic move), never as a partial line a reader could mistake for corruption.</summary>
    private static void WriteDigest(string digestPath, string checksum)
    {
        var temp = digestPath + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        File.WriteAllText(temp, checksum.Trim().ToLowerInvariant() + "\n");
        File.Move(temp, digestPath, overwrite: true);
    }

    /// <summary>The expected digest of the platform's asset: from the cached <c>SHA256SUMS-{version}.txt</c> when present, else the release's (cached afterwards).</summary>
    public async Task<string> ResolveChecksumAsync(string version, string platform, CancellationToken cancellationToken = default)
    {
        ValidateVersion(version);
        var asset = AssetName(platform);
        var cachedList = CachedChecksumsPath(version);
        if (File.Exists(cachedList))
        {
            try
            {
                return ParseChecksums(await File.ReadAllTextAsync(cachedList, cancellationToken).ConfigureAwait(false), asset);
            }
            catch (InvalidOperationException)
            {
                // an older or partial list: fetch the release's
            }
        }

        var text = await DownloadTextAsync(ReleaseUrl(version, ChecksumsFileName), cancellationToken).ConfigureAwait(false);
        var checksum = ParseChecksums(text, asset);
        Directory.CreateDirectory(CacheDir);
        await File.WriteAllTextAsync(cachedList, text, cancellationToken).ConfigureAwait(false);
        return checksum;
    }

    /// <summary>
    /// The verified archive bytes: a cache hit whose digest file matches needs no network; otherwise the release's
    /// checksum list is fetched, a cached archive matching it is reused, else the asset is downloaded, verified
    /// (a mismatch is a <see cref="ChecksumMismatchException"/>, never masked) and cached with its digest.
    /// </summary>
    public async Task<(byte[] Data, string Checksum)> ResolveArchiveAsync(string version, string platform, CancellationToken cancellationToken = default)
    {
        ValidateVersion(version);
        var asset = AssetName(platform);
        if (ReadCachedArchive(version, platform) is { } cached)
        {
            ProviderLogger.Info($"Used {AgentName} archive from cache: {version} ({platform})");
            return (cached, Sha256Hex(cached));
        }

        var checksum = await ResolveChecksumAsync(version, platform, cancellationToken).ConfigureAwait(false);
        var archivePath = CachedArchivePath(version, platform);
        if (File.Exists(archivePath))
        {
            var stale = await File.ReadAllBytesAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (VerifyChecksum(stale, checksum))
            {
                WriteDigest(CachedDigestPath(version, platform), checksum);
                ProviderLogger.Info($"Used {AgentName} archive from cache (verified against {ChecksumsFileName}): {version} ({platform})");
                return (stale, checksum);
            }

            File.Delete(archivePath);
        }

        var data = await DownloadFileAsync(ReleaseUrl(version, asset), cancellationToken).ConfigureAwait(false);
        if (!VerifyChecksum(data, checksum))
        {
            throw new ChecksumMismatchException("Checksum verification failed");
        }

        WriteCachedArchive(data, version, platform, checksum);
        ProviderLogger.Info($"Downloaded {AgentName} archive: {version} ({platform})");
        return (data, checksum);
    }

    /// <summary>Port of <c>download_file</c>: 4 attempts with 1, 2, 4 s delays on transport errors and 5xx; a 4xx is permanent; the body is read in chunks against <see cref="MaxDownloadBytes"/>.</summary>
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

    /// <summary>The install directory of a version inside the sandbox and the executable it holds.</summary>
    public static string SandboxInstallPath(string version, string platform) => $"{SandboxUtil.SandboxInstallDir}/{BinaryName}-{version}-{platform}";

    public static string SandboxBinaryPath(string version, string platform) => $"{SandboxInstallPath(version, platform)}/{BinaryName}";

    /// <summary>
    /// The counterpart of <c>ensure_agent_binary_installed</c>: "auto"/"sandbox" first look for an installed
    /// <c>copilot</c> (<c>which</c>, as <paramref name="user"/>); "auto" then installs <see cref="CopilotCliOptions.DefaultVersion"/>;
    /// a concrete version is resolved through the cache, written into the sandbox as a tarball, extracted with
    /// <c>tar -xzf</c> into the install directory as root and the executable <c>chmod +x</c>'d; the returned
    /// path is that executable. An executable already present at the install path is reused without a write.
    /// </summary>
    public async Task<string> EnsureInstalledAsync(ISandboxEnvironment sandbox, string version = "auto", string? user = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(version);
        if (version is "auto" or "sandbox")
        {
            var which = await sandbox.ExecAsync(SandboxUtil.BashCommand($"which {BinaryName}"), user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (which.Success && which.Stdout.Trim().Length > 0)
            {
                var binaryPath = which.Stdout.Trim();
                ProviderLogger.Info($"Using {AgentName} installed in sandbox: {binaryPath}");
                return binaryPath;
            }

            if (version == "sandbox")
            {
                throw new InvalidOperationException($"unable to locate {AgentName} in sandbox");
            }

            version = CopilotCliOptions.DefaultVersion;
        }

        ValidateVersion(version);
        var platform = await SandboxUtil.DetectPlatformAsync(sandbox, cancellationToken).ConfigureAwait(false);
        var installDir = SandboxInstallPath(version, platform);
        var binary = SandboxBinaryPath(version, platform);

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var present = await sandbox.ExecAsync(["test", "-x", binary], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
            if (present.Success)
            {
                ProviderLogger.Info($"Using {AgentName} already installed in sandbox: {binary}");
                return binary;
            }

            var (data, _) = await ResolveArchiveAsync(version, platform, cancellationToken).ConfigureAwait(false);
            var archive = $"{installDir}{ArchiveSuffix}";
            // argv, not shell strings: the paths embed the version (validated) and platform (from our own table)
            await RootExecAsync(sandbox, ["mkdir", "-p", installDir], cancellationToken).ConfigureAwait(false);
            await sandbox.WriteFileAsync(archive, data, cancellationToken).ConfigureAwait(false);
            await RootExecAsync(sandbox, ["tar", "-xzf", archive, "-C", installDir], cancellationToken).ConfigureAwait(false);
            await RootExecAsync(sandbox, ["chmod", "+x", binary], cancellationToken).ConfigureAwait(false);
            await RootExecAsync(sandbox, ["rm", "-f", archive], cancellationToken).ConfigureAwait(false);
            return binary;
        }
        finally
        {
            _installGate.Release();
        }
    }

    private static async Task RootExecAsync(ISandboxEnvironment sandbox, IReadOnlyList<string> cmd, CancellationToken cancellationToken)
    {
        var result = await sandbox.ExecAsync(cmd, user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error executing sandbox command {string.Join(" ", cmd)}: {result.Stderr}");
        }
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

        var files = ListCachedArchives();
        if (files.Count <= CacheKeepCount)
        {
            return;
        }

        foreach (var stale in files.OrderBy(File.GetLastAccessTimeUtc).Take(files.Count - CacheKeepCount))
        {
            File.Delete(stale);
            var digest = stale + DigestSuffix;
            if (File.Exists(digest))
            {
                File.Delete(digest);
            }
        }
    }

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

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.+-]+)?\z")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}\z")]
    private static partial Regex HexDigestRegex();
}
