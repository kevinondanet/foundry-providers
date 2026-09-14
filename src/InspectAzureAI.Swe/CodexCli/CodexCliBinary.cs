using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.CodexCli;

/// <summary>The Codex release asset chosen for a sandbox platform (port of <c>AgentBinaryVersion</c>).</summary>
/// <param name="Version">The concrete release version (no <c>rust-v</c> prefix).</param>
/// <param name="Name">The asset file name, such as <c>codex-package-aarch64-unknown-linux-musl.tar.gz</c>.</param>
/// <param name="Sha256">The expected SHA-256 of the asset, from its <c>digest</c> (lower-case hex, no <c>sha256:</c> prefix).</param>
/// <param name="Url">The asset's <c>browser_download_url</c>.</param>
/// <param name="Package">True for the package archive (with companion executables), false for the single-binary archive.</param>
public sealed record CodexReleaseAsset(string Version, string Name, string Sha256, string Url, bool Package);

/// <summary>
/// Port of inspect_swe <c>_codex_cli/agentbinary.py</c> over <c>_util/agentbinary.py</c>: resolves a Codex CLI
/// release on GitHub, caches the release archive on the host and installs it in the sandbox.
/// </summary>
/// <remarks>
/// <para>
/// Version targets: <c>auto</c> and <c>sandbox</c> first look for <c>codex</c> in the sandbox (<c>sandbox</c> requires
/// it; <c>auto</c> otherwise continues as <c>stable</c>); <c>stable</c> and <c>latest</c> resolve through
/// <c>GET {ReleaseApiBaseUrl}/releases/latest</c>; anything else must be a concrete version.
/// </para>
/// <para>
/// Release lookups are memoized per process (unauthenticated GitHub allows 60 requests an hour): one
/// <c>releases/latest</c> call per API base and one <c>releases/tags/rust-v{v}</c> call per (API base, version,
/// platform). A lookup that faults or is cancelled is evicted, so a transient failure is retried rather than cached.
/// </para>
/// <para>
/// Archives are cached verbatim under <see cref="CacheDir"/> as <c>codex-package-{v}-{p}.tar.gz</c> or
/// <c>codex-{v}-{p}.tar.gz</c> beside a <c>.sha256</c> companion. The cache is checked for every concrete version (a
/// pinned one, or the version <c>stable</c>/<c>latest</c> just resolved to), preferring a package; a hit whose digest
/// matches needs no further network (deviation D-X4). Both kinds are extracted inside the sandbox as root. Installs
/// are serialized process-wide (<c>concurrency("codex-install", 1)</c>).
/// </para>
/// </remarks>
public sealed partial class CodexCliBinary
{
    public const string BinaryName = "codex";

    public const string AgentName = "codex cli";

    public const string DefaultReleaseApiBaseUrl = "https://api.github.com/repos/openai/codex";

    public const string DefaultCatalogBaseUrl = "https://raw.githubusercontent.com/openai/codex";

    /// <summary>The prefix of Codex release tags (<c>rust-v0.154.0</c>).</summary>
    public const string TagPrefix = "rust-v";

    /// <summary>The Codex executable inside an extracted package archive.</summary>
    public const string PackageEntrypoint = "bin/codex";

    /// <summary>Archives kept in the host cache (the most recently accessed).</summary>
    public const int KeepCached = 3;

    public const string ArchiveSuffix = ".tar.gz";

    public const string DigestSuffix = ".sha256";

    public const string CatalogSuffix = "-models.json";

    public const string TempSuffix = ".tmp";

    /// <summary>Sent on every request (the GitHub API rejects requests without a User-Agent).</summary>
    public const string UserAgent = "inspect-azureai";

    /// <summary>Largest download accepted; a bigger body is rejected while streaming rather than buffered whole.</summary>
    public const long MaxDownloadBytes = 1024L * 1024 * 1024;

    /// <summary>Delay before each retry; attempts = count + 1.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    /// <summary>Generous: the package archive is about 118 MB.</summary>
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    private const string GitHubApiHost = "api.github.com";

    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> LatestVersions = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<(string ApiBase, string Version, string Platform), Lazy<Task<CodexReleaseAsset>>> ReleaseAssets = new();

    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private static readonly SemaphoreSlim CatalogGate = new(1, 1);

    private static readonly Func<TimeSpan, CancellationToken, Task> SleepDelay = (delay, cancellationToken) => Task.Delay(delay, cancellationToken);

    private readonly HttpMessageHandler? _httpHandler;

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private readonly Lazy<HttpClient> _client;

    public CodexCliBinary(
        string? cacheDir = null,
        string? releaseApiBaseUrl = null,
        string? catalogBaseUrl = null,
        HttpMessageHandler? httpHandler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        CacheDir = cacheDir ?? DefaultCacheDir;
        ReleaseApiBaseUrl = (releaseApiBaseUrl ?? DefaultReleaseApiBaseUrl).TrimEnd('/');
        CatalogBaseUrl = (catalogBaseUrl ?? DefaultCatalogBaseUrl).TrimEnd('/');
        _httpHandler = httpHandler;
        _delay = delay ?? SleepDelay;
        _client = new Lazy<HttpClient>(CreateClient);
    }

    /// <summary>The sibling of the Claude Code and Copilot CLI caches: <c>~/.cache/inspect-azureai/codex-cli-downloads</c>.</summary>
    public static string DefaultCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "inspect-azureai", "codex-cli-downloads");

    public string CacheDir { get; }

    /// <summary>The GitHub repository API base, without a trailing slash (default <see cref="DefaultReleaseApiBaseUrl"/>).</summary>
    public string ReleaseApiBaseUrl { get; }

    /// <summary>The raw-content base the version-matched catalog is fetched from (default <see cref="DefaultCatalogBaseUrl"/>).</summary>
    public string CatalogBaseUrl { get; }

    /// <summary>A concrete release version: <c>x.y.z</c> with an optional <c>-prerelease</c> suffix of <c>[A-Za-z0-9.+-]</c>.</summary>
    public static bool IsValidVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return VersionRegex().IsMatch(version);
    }

    /// <summary>Throws <see cref="ArgumentException"/> unless <see cref="IsValidVersion"/>.</summary>
    public static void ValidateVersion(string version)
    {
        if (!IsValidVersion(version))
        {
            throw new ArgumentException($"Invalid codex cli version '{version}' (must be 'auto', 'sandbox', 'stable', 'latest', or a semver version number such as '0.154.0')");
        }
    }

    /// <summary>
    /// Port of <c>_platform_to_codex_arch</c>: the musl target triple for a <see cref="SandboxUtil.DetectPlatformAsync"/>
    /// platform, whatever the sandbox libc (musl builds are statically linked).
    /// </summary>
    public static string CodexArch(string platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        return platform switch
        {
            "linux-x64" or "linux-x64-musl" => "x86_64-unknown-linux-musl",
            "linux-arm64" or "linux-arm64-musl" => "aarch64-unknown-linux-musl",
            _ => throw new PlatformNotSupportedException($"Unsupported platform: {platform}"),
        };
    }

    /// <summary>The release assets to look for, in preference order: the package archive, then the single-binary archive.</summary>
    public static IReadOnlyList<(string Name, bool Package)> AssetCandidates(string platform)
    {
        var arch = CodexArch(platform);
        return [($"codex-package-{arch}{ArchiveSuffix}", true), ($"codex-{arch}{ArchiveSuffix}", false)];
    }

    /// <summary>
    /// Port of the asset selection in <c>resolve_version</c>: the first <see cref="AssetCandidates"/> entry present in
    /// the release's <c>assets</c>. None present throws <c>"No asset found for platform {p} in version {v}"</c>; a
    /// <c>digest</c> not starting with <c>sha256:</c> throws <c>"Invalid digest format: {digest}"</c>.
    /// </summary>
    public static CodexReleaseAsset SelectAsset(JsonObject release, string version, string platform)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(version);
        var assets = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        if (release["assets"] is JsonArray array)
        {
            foreach (var asset in array.OfType<JsonObject>())
            {
                if (StringOf(asset["name"]) is { } name)
                {
                    assets[name] = asset;
                }
            }
        }

        foreach (var (name, package) in AssetCandidates(platform))
        {
            if (!assets.TryGetValue(name, out var asset))
            {
                continue;
            }

            var digest = StringOf(asset["digest"]) ?? "";
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Invalid digest format: {digest}");
            }

            var url = StringOf(asset["browser_download_url"])
                ?? throw new InvalidOperationException($"Release asset {name} in version {version} has no browser_download_url");
            return new CodexReleaseAsset(version, name, digest["sha256:".Length..].Trim().ToLowerInvariant(), url, package);
        }

        throw new InvalidOperationException($"No asset found for platform {platform} in version {version}");
    }

    /// <summary>
    /// A concrete version: <c>stable</c> and <c>latest</c> resolve (memoized per API base) through
    /// <c>releases/latest</c>, stripping <c>rust-v</c> from <c>tag_name</c>; anything else is validated and returned.
    /// </summary>
    public async Task<string> ResolveVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version is "stable" or "latest")
        {
            return await MemoizedAsync(LatestVersions, ReleaseApiBaseUrl, FetchLatestVersionAsync, cancellationToken).ConfigureAwait(false);
        }

        ValidateVersion(version);
        return version;
    }

    /// <summary>
    /// Port of <c>ensure_agent_binary_installed</c> for Codex: returns the path of a runnable <c>codex</c> in the
    /// sandbox, installing the resolved release when needed (see the class remarks).
    /// </summary>
    public async Task<string> EnsureInstalledAsync(ISandboxEnvironment sandbox, string version = "auto", string? user = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(version);
        if (version is "auto" or "sandbox")
        {
            var which = await sandbox.ExecAsync(SandboxUtil.BashCommand($"which {BinaryName}"), user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
            var found = which.Stdout.Trim();
            if (which.Success && found.Length > 0)
            {
                ProviderLogger.Info($"Using {AgentName} installed in sandbox: {found}");
                return found;
            }

            if (version == "sandbox")
            {
                throw new InvalidOperationException($"unable to locate {AgentName} in sandbox");
            }

            version = "stable";
        }

        var pinned = version is not ("stable" or "latest");
        var resolved = await ResolveVersionAsync(version, cancellationToken).ConfigureAwait(false);
        var platform = await SandboxUtil.DetectPlatformAsync(sandbox, cancellationToken).ConfigureAwait(false);
        CodexArch(platform);

        await InstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (data, package) = await ResolveArchiveAsync(resolved, platform, pinned, cancellationToken).ConfigureAwait(false);
            return await InstallAsync(sandbox, data, resolved, platform, package, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    /// <summary>
    /// The verified archive of a concrete version: a cache hit (package preferred) needs no network; otherwise the
    /// memoized release asset is downloaded, verified (a mismatch is a <see cref="ChecksumMismatchException"/>) and
    /// cached with its digest. When that fails for a <paramref name="pinned"/> version, a verified cached archive of
    /// the version is used instead if one has appeared.
    /// </summary>
    public async Task<(byte[] Data, bool Package)> ResolveArchiveAsync(string version, string platform, bool pinned = true, CancellationToken cancellationToken = default)
    {
        ValidateVersion(version);
        CodexArch(platform);
        if (ReadCachedArchive(version, platform) is { } cached)
        {
            ProviderLogger.Info($"Used {AgentName} archive from cache: {version} ({platform})");
            return cached;
        }

        try
        {
            var asset = await MemoizedAsync(ReleaseAssets, (ReleaseApiBaseUrl, version, platform), () => FetchReleaseAssetAsync(version, platform), cancellationToken).ConfigureAwait(false);
            var data = await DownloadFileAsync(asset.Url, cancellationToken).ConfigureAwait(false);
            if (!VerifyChecksum(data, asset.Sha256))
            {
                throw new ChecksumMismatchException("Checksum verification failed");
            }

            WriteCachedArchive(data, version, platform, asset.Package, asset.Sha256);
            ProviderLogger.Info($"Downloaded {AgentName} archive: {version} ({platform})");
            return (data, asset.Package);
        }
        catch (Exception) when (pinned && !cancellationToken.IsCancellationRequested)
        {
            if (ReadCachedArchive(version, platform) is { } fallback)
            {
                ProviderLogger.Info($"Unable to resolve {AgentName} {version}; using cached archive ({platform})");
                return fallback;
            }

            throw;
        }
    }

    /// <summary>
    /// Port of <c>codex_binary_version</c>: runs <c>[binary, "--version"]</c> as <paramref name="user"/> and returns the
    /// first <c>x.y.z</c> in its output, or null when the command fails or prints none.
    /// </summary>
    public static async Task<string?> InstalledVersionAsync(ISandboxEnvironment sandbox, string binary, string? user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(binary);
        var result = await sandbox.ExecAsync([binary, "--version"], user: user, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return null;
        }

        var match = InstalledVersionRegex().Match(result.Stdout);
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Port of <c>codex_models_catalog</c>: the version-matched Codex model catalog. A null version gives
    /// <see cref="CodexCliModelCatalog.Bundled"/>; a cached <c>codex-{v}-models.json</c> is used when present;
    /// otherwise, under a process-wide gate with a cache re-check once held,
    /// <c>GET {CatalogBaseUrl}/rust-v{v}/codex-rs/models-manager/models.json</c> must return an object with a
    /// <c>models</c> array, which is cached. Any failure warns once and returns the bundled catalog.
    /// </summary>
    public async Task<JsonObject> CatalogAsync(string? version, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(version))
        {
            return CodexCliModelCatalog.Bundled;
        }

        if (ReadCachedCatalog(version) is { } cached)
        {
            return cached;
        }

        try
        {
            ValidateVersion(version);
            await CatalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ReadCachedCatalog(version) is { } raced)
                {
                    return raced;
                }

                var url = $"{CatalogBaseUrl}/{TagPrefix}{version}/codex-rs/models-manager/models.json";
                var text = Encoding.UTF8.GetString(await DownloadFileAsync(url, cancellationToken).ConfigureAwait(false));
                var catalog = JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException($"{url} is not a JSON object");
                if (catalog["models"] is not JsonArray)
                {
                    throw new InvalidDataException($"{url} has no models array");
                }

                TryWriteCatalog(version, text);
                return catalog;
            }
            finally
            {
                CatalogGate.Release();
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            ProviderLogger.WarnOnce($"Unable to fetch codex model catalog for {version} ({ex.Message}); using the bundled catalog.");
            return CodexCliModelCatalog.Bundled;
        }
    }

    /// <summary>The host cache path of a version's archive.</summary>
    public string CachedArchivePath(string version, string platform, bool package) =>
        Path.Combine(CacheDir, package ? $"{BinaryName}-package-{version}-{platform}{ArchiveSuffix}" : $"{BinaryName}-{version}-{platform}{ArchiveSuffix}");

    /// <summary>The host cache path of a version's model catalog (<c>codex-{v}-models.json</c>).</summary>
    public string CachedCatalogPath(string version) => Path.Combine(CacheDir, $"{BinaryName}-{version}{CatalogSuffix}");

    /// <summary>The cached archives (not digests, catalogs or in-progress temp files).</summary>
    public IReadOnlyList<string> ListCachedArchives() =>
        Directory.Exists(CacheDir)
            ? Directory.GetFiles(CacheDir, $"{BinaryName}-*{ArchiveSuffix}")
                .Where(file => file.EndsWith(ArchiveSuffix, StringComparison.Ordinal) && !file.EndsWith(CatalogSuffix, StringComparison.Ordinal))
                .ToArray()
            : [];

    /// <summary>
    /// A cached archive of the version whose <c>.sha256</c> companion matches its content, package first; its access
    /// time is refreshed. An archive that does not match is deleted with its companion. Null when nothing usable is cached.
    /// </summary>
    public (byte[] Data, bool Package)? ReadCachedArchive(string version, string platform)
    {
        foreach (var package in (bool[])[true, false])
        {
            if (ReadVerified(CachedArchivePath(version, platform, package)) is { } data)
            {
                return (data, package);
            }
        }

        return null;
    }

    /// <summary>
    /// Writes the digest companion and then the archive, each through a temp file and an atomic move, removes a
    /// single-binary entry the package supersedes, and prunes to the <see cref="KeepCached"/> most recently accessed archives.
    /// </summary>
    public void WriteCachedArchive(byte[] data, string version, string platform, bool package, string sha256)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(sha256);
        ValidateVersion(version);
        Directory.CreateDirectory(CacheDir);
        var archivePath = CachedArchivePath(version, platform, package);
        WriteAtomic(archivePath + DigestSuffix, Encoding.ASCII.GetBytes(sha256.Trim().ToLowerInvariant() + "\n"));
        WriteAtomic(archivePath, data);
        if (package)
        {
            var superseded = CachedArchivePath(version, platform, package: false);
            TryDelete(superseded);
            TryDelete(superseded + DigestSuffix);
        }

        PruneCache();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static bool VerifyChecksum(ReadOnlySpan<byte> data, string expectedChecksum)
    {
        ArgumentNullException.ThrowIfNull(expectedChecksum);
        return string.Equals(Sha256Hex(data), expectedChecksum.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The install directory of a version inside the sandbox.</summary>
    public static string SandboxInstallPath(string version, string platform) => $"{SandboxUtil.SandboxInstallDir}/{BinaryName}-{version}-{platform}";

    /// <summary>The executable an install provides: <c>bin/codex</c> in a package, <c>codex-{arch}</c> from a single-binary archive.</summary>
    public static string SandboxEntrypoint(string version, string platform, bool package) =>
        package ? $"{SandboxInstallPath(version, platform)}/{PackageEntrypoint}" : $"{SandboxInstallPath(version, platform)}/{BinaryName}-{CodexArch(platform)}";

    /// <summary>
    /// Port of <c>download_file</c>: 4 attempts with 1, 2 and 4 s delays on transport errors and 5xx; a 4xx is
    /// permanent; the body is read in chunks against <see cref="MaxDownloadBytes"/>. Requests to the GitHub API
    /// (<c>api.github.com</c>) carry <c>Authorization: Bearer $GITHUB_TOKEN</c> when that variable is set.
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var attempts = RetryDelays.Count + 1;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                if (request.RequestUri is { Host: GitHubApiHost } && Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                using var response = await _client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Clears the process-wide release memos. Tests call it in their constructor.</summary>
    internal static void ResetForTests()
    {
        LatestVersions.Clear();
        ReleaseAssets.Clear();
    }

    /// <summary>
    /// Awaits the memoized lookup for <paramref name="key"/>, starting it on a miss. The shared lookup runs without the
    /// caller's token (each caller stops waiting on its own); a lookup that faults or is cancelled is evicted before
    /// the exception propagates.
    /// </summary>
    private static async Task<T> MemoizedAsync<TKey, T>(ConcurrentDictionary<TKey, Lazy<Task<T>>> memo, TKey key, Func<Task<T>> lookup, CancellationToken cancellationToken)
        where TKey : notnull
    {
        var lazy = memo.GetOrAdd(key, _ => new Lazy<Task<T>>(() => Task.Run(lookup), LazyThreadSafetyMode.ExecutionAndPublication));
        var task = lazy.Value;
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch when (task.IsFaulted || task.IsCanceled)
        {
            memo.TryRemove(KeyValuePair.Create(key, lazy));
            throw;
        }
    }

    private async Task<string> FetchLatestVersionAsync()
    {
        var latest = await GetJsonObjectAsync($"{ReleaseApiBaseUrl}/releases/latest").ConfigureAwait(false);
        var tag = StringOf(latest["tag_name"]) ?? throw new InvalidOperationException($"{ReleaseApiBaseUrl}/releases/latest has no tag_name");
        if (!tag.StartsWith(TagPrefix, StringComparison.Ordinal) || !IsValidVersion(tag[TagPrefix.Length..]))
        {
            throw new InvalidOperationException($"Unexpected tag format: {tag}");
        }

        return tag[TagPrefix.Length..];
    }

    private async Task<CodexReleaseAsset> FetchReleaseAssetAsync(string version, string platform)
    {
        var release = await GetJsonObjectAsync($"{ReleaseApiBaseUrl}/releases/tags/{TagPrefix}{version}").ConfigureAwait(false);
        return SelectAsset(release, version, platform);
    }

    private async Task<JsonObject> GetJsonObjectAsync(string url)
    {
        var bytes = await DownloadFileAsync(url, CancellationToken.None).ConfigureAwait(false);
        return JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidOperationException($"GET {url} did not return a JSON object.");
    }

    private static async Task<string> InstallAsync(ISandboxEnvironment sandbox, byte[] data, string version, string platform, bool package, CancellationToken cancellationToken)
    {
        var installDir = SandboxInstallPath(version, platform);
        var entrypoint = SandboxEntrypoint(version, platform, package);
        var probe = await sandbox.ExecAsync(["test", "-x", entrypoint], user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (probe.Success)
        {
            ProviderLogger.Info($"Using {AgentName} already installed in sandbox: {entrypoint}");
            return entrypoint;
        }

        // argv, not shell strings (deviation D-X5): the paths embed the validated version and a platform from our own table
        var archive = $"{installDir}/.codex-archive{ArchiveSuffix}";
        await RootExecAsync(sandbox, ["mkdir", "-p", installDir], cancellationToken).ConfigureAwait(false);
        await sandbox.WriteFileAsync(archive, data, cancellationToken).ConfigureAwait(false);
        await RootExecAsync(sandbox, ["tar", "-xzf", archive, "-C", installDir], cancellationToken).ConfigureAwait(false);
        await RootExecAsync(sandbox, ["rm", "-f", archive], cancellationToken).ConfigureAwait(false);
        await RootExecAsync(sandbox, ["chmod", "+x", entrypoint], cancellationToken).ConfigureAwait(false);
        return entrypoint;
    }

    private static async Task RootExecAsync(ISandboxEnvironment sandbox, IReadOnlyList<string> cmd, CancellationToken cancellationToken)
    {
        var result = await sandbox.ExecAsync(cmd, user: "root", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Error executing sandbox command {string.Join(" ", cmd)}: {result.Stderr}");
        }
    }

    private static byte[]? ReadVerified(string archivePath)
    {
        var digestPath = archivePath + DigestSuffix;
        try
        {
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
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        ProviderLogger.Warning($"Cached {AgentName} archive {archivePath} does not match its digest file; discarding it.");
        TryDelete(archivePath);
        TryDelete(digestPath);
        return null;
    }

    private JsonObject? ReadCachedCatalog(string version)
    {
        if (!IsValidVersion(version))
        {
            return null;
        }

        var path = CachedCatalogPath(version);
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void TryWriteCatalog(string version, string text)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            WriteAtomic(CachedCatalogPath(version), Encoding.UTF8.GetBytes(text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the catalog is still returned; the next sample fetches it again
        }
    }

    private void PruneCache()
    {
        foreach (var orphan in Directory.GetFiles(CacheDir, $"{BinaryName}-*{TempSuffix}"))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(orphan) > StaleTempAge)
            {
                TryDelete(orphan);
            }
        }

        var archives = ListCachedArchives();
        if (archives.Count <= KeepCached)
        {
            return;
        }

        foreach (var stale in archives.OrderBy(File.GetLastAccessTimeUtc).Take(archives.Count - KeepCached))
        {
            TryDelete(stale);
            TryDelete(stale + DigestSuffix);
        }
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + TempSuffix;
        File.WriteAllBytes(temp, data);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // another process holds or already removed it
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

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.+-]+)?\z")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"[0-9]+\.[0-9]+\.[0-9]+")]
    private static partial Regex InstalledVersionRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}\z")]
    private static partial Regex HexDigestRegex();
}
