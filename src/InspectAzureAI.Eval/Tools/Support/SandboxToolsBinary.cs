using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Eval.Tools.Support;

/// <summary>A published sandbox tools artifact: its file name and the gzipped tar of the PyInstaller onedir tree.</summary>
public sealed record SandboxToolsArtifact(string Name, byte[] GzipBytes);

/// <summary>Port of <c>_build_config.py</c> <c>SandboxToolsBuildConfig</c>: the parts of an artifact name.</summary>
public sealed record SandboxToolsBuildConfig(string Arch, int Version, string? Suffix, bool Musl);

/// <summary>Where <see cref="SandboxToolSupport"/> gets the artifact to inject (the seam tests replace with an in-memory source).</summary>
public interface ISandboxToolsBinarySource
{
    /// <summary>Port of <c>_open_executable_for_arch</c>: the artifact for a container architecture ("amd64"/"arm64") and libc.</summary>
    Task<SandboxToolsArtifact> OpenAsync(string architecture, bool musl, CancellationToken cancellationToken = default);

    /// <summary>Port of <c>_uncompressed_tar_bytes</c>: the plain tar for a sandbox whose <c>tar</c> cannot gunzip.</summary>
    Task<byte[]> UncompressedTarAsync(SandboxToolsArtifact artifact, CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of the artifact resolution in <c>tool/_sandbox_tools_utils/sandbox.py</c>: the same version pin
/// (<c>sandbox_tools_version.txt</c>), S3 bucket and vendored <c>SHA256SUMS</c> as the Python package.
/// An artifact is served from <see cref="BinariesDir"/> when present (Python's <c>inspect_ai/binaries</c>),
/// otherwise downloaded from S3 and verified against its pinned digest before being cached there.
/// Digest verification is always strict (Python's <c>INSPECT_SANDBOX_TOOLS_STRICT_DIGESTS=1</c> behaviour):
/// unverified bytes are never injected. The Python local-build fallback (PyInstaller inside Docker) is not
/// ported; a missing artifact is a <see cref="PrerequisiteError"/> naming the build command.
/// </summary>
public sealed partial class SandboxToolsBinary : ISandboxToolsBinarySource
{
    /// <summary>The ordinal version pinned by <c>sandbox_tools_version.txt</c>.</summary>
    public const int Version = 29;

    public const string BucketBaseUrl = "https://inspect-sandbox-tools.s3.us-east-2.amazonaws.com";

    /// <summary>Overrides <see cref="DefaultBinariesDir"/> (a directory holding pre-downloaded artifacts, as a locked-down install would).</summary>
    public const string BinariesDirVar = "INSPECT_SANDBOX_TOOLS_BINARIES_DIR";

    /// <summary>Largest artifact accepted (the real ones are ~15 MB); a bigger body is rejected while streaming.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    public static readonly TimeSpan HttpTimeout = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The vendored <c>SHA256SUMS</c> for <see cref="Version"/>: one digest per published arch × libc artifact.</summary>
    public static readonly IReadOnlyDictionary<string, string> Sha256Sums = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["inspect-sandbox-tools-amd64-musl-v29"] = "fb1e9882e9a76246e9bdffb2ae28025aff6f45547adde9205bb7165badbc8f56",
        ["inspect-sandbox-tools-amd64-v29"] = "1e8e8c2238e2ce741ead68976e8bf91e69ead7eb50e935b244c88da1b0616688",
        ["inspect-sandbox-tools-arm64-musl-v29"] = "092aaa5b1505befaa35683c0c6fc6f996cbf5513822b7b26c19f18f80647c48a",
        ["inspect-sandbox-tools-arm64-v29"] = "d7e5d6cf4574f6aedf034e51efd83e78ef0db7332863c5b7b635d9372975469c",
    };

    private readonly IReadOnlyDictionary<string, string> _digests;

    private readonly string _bucketBaseUrl;

    private readonly HttpMessageHandler? _httpHandler;

    private readonly Lazy<HttpClient> _client;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <param name="binariesDir">Where artifacts are looked up and cached; defaults to <see cref="BinariesDirVar"/> then <see cref="DefaultBinariesDir"/>.</param>
    /// <param name="httpHandler">Replaces the socket handler (tests serve a fake bucket).</param>
    /// <param name="bucketBaseUrl">Replaces <see cref="BucketBaseUrl"/>.</param>
    /// <param name="digests">Replaces <see cref="Sha256Sums"/> (tests pin digests of their own fixtures).</param>
    public SandboxToolsBinary(string? binariesDir = null, HttpMessageHandler? httpHandler = null, string? bucketBaseUrl = null, IReadOnlyDictionary<string, string>? digests = null)
    {
        BinariesDir = binariesDir
            ?? (Environment.GetEnvironmentVariable(BinariesDirVar) is { Length: > 0 } configured ? configured : DefaultBinariesDir);
        _httpHandler = httpHandler;
        _bucketBaseUrl = (bucketBaseUrl ?? BucketBaseUrl).TrimEnd('/');
        _digests = digests ?? Sha256Sums;
        _client = new Lazy<HttpClient>(CreateClient);
    }

    /// <summary>The .NET stand-in for the Python package's <c>inspect_ai/binaries</c> directory.</summary>
    public static string DefaultBinariesDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "inspect-azureai", "sandbox-tools");

    public string BinariesDir { get; }

    /// <summary>Port of <c>config_to_filename</c>: <c>inspect-sandbox-tools-{arch}[-musl]-v{version}[-{suffix}]</c>.</summary>
    public static string ExecutableName(string architecture, bool musl, int version = Version, string? suffix = null)
    {
        ArgumentNullException.ThrowIfNull(architecture);
        var name = $"{SandboxToolSupport.SandboxToolsBaseName}-{architecture}";
        if (musl)
        {
            name += "-musl";
        }

        name += $"-v{version.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(suffix))
        {
            name += $"-{suffix}";
        }

        return name;
    }

    /// <summary>Port of <c>filename_to_config</c>; a name outside the pattern is an <see cref="ArgumentException"/>.</summary>
    public static SandboxToolsBuildConfig ParseExecutableName(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        var match = ExecutableNameRegex().Match(filename);
        if (!match.Success)
        {
            throw new ArgumentException($"Filename '{filename}' doesn't match expected pattern", nameof(filename));
        }

        return new SandboxToolsBuildConfig(
            match.Groups["arch"].Value,
            int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
            match.Groups["suffix"].Success ? match.Groups["suffix"].Value : null,
            match.Groups["libc"].Success);
    }

    /// <summary>Port of <c>lookup_digest</c>: the pinned digest for an artifact name; a missing entry is an <see cref="InvalidOperationException"/>, never a reason to fetch unverified bytes.</summary>
    public string LookupDigest(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);
        return _digests.TryGetValue(filename, out var digest)
            ? digest
            : throw new InvalidOperationException(
                $"No SHA256 entry for {filename} in the vendored SHA256SUMS. This indicates a corrupt installation or a "
                + "desynced commit (the sums file is rewritten by upload_to_s3.py alongside every version bump); "
                + "refusing to download unverified bytes.");
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static bool VerifyDigest(ReadOnlySpan<byte> data, string expectedSha256) =>
        string.Equals(Sha256Hex(data), expectedSha256, StringComparison.OrdinalIgnoreCase);

    public string ArtifactPath(string name) => Path.Combine(BinariesDir, name);

    public string ArtifactUrl(string name) => $"{_bucketBaseUrl}/{name}";

    /// <summary>
    /// Serves the artifact from <see cref="BinariesDir"/> when it is present and matches its pinned digest;
    /// otherwise downloads it from S3, verifies it and caches it. One resolution runs per artifact name at a time.
    /// </summary>
    public async Task<SandboxToolsArtifact> OpenAsync(string architecture, bool musl, CancellationToken cancellationToken = default)
    {
        var name = ExecutableName(architecture, musl);
        var expected = LookupDigest(name);
        var gate = _gates.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ArtifactPath(name);
            if (File.Exists(path))
            {
                var cached = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (VerifyDigest(cached, expected))
                {
                    return new SandboxToolsArtifact(name, cached);
                }

                ProviderLogger.Warning($"Sandbox tools artifact {path} does not match the digest pinned for {name}; discarding it and downloading a fresh copy.");
                File.Delete(path);
            }

            var bytes = await DownloadAsync(name, architecture, musl, cancellationToken).ConfigureAwait(false);
            if (!VerifyDigest(bytes, expected))
            {
                throw new PrerequisiteError(
                    $"Digest verification failed for {name} downloaded from S3: expected sha256 {expected}, got {Sha256Hex(bytes)}. "
                    + "The published artifact does not match the digest pinned in this inspect_ai release, which may indicate a "
                    + "compromised or corrupted artifact — please report this to the inspect_ai maintainers rather than retrying.");
            }

            await WriteAtomicAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            ProviderLogger.Info($"Downloaded sandbox tools artifact {name} from {ArtifactUrl(name)}");
            return new SandboxToolsArtifact(name, bytes);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Decompresses the artifact once and caches the tar as <c>{name}.tar</c> beside it (best effort: an
    /// unwritable directory just skips the cache, as in Python).
    /// </summary>
    public async Task<byte[]> UncompressedTarAsync(SandboxToolsArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var cachePath = ArtifactPath(artifact.Name + ".tar");
        if (File.Exists(cachePath))
        {
            return await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }

        var tar = await GunzipAsync(artifact.GzipBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(cachePath, tar, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ProviderLogger.Info($"could not cache uncompressed sandbox tools tar at {cachePath}: {ex.Message}");
        }

        return tar;
    }

    /// <summary>gunzip in memory (the artifact is a gzipped tar; nothing here inspects the tar itself).</summary>
    public static async Task<byte[]> GunzipAsync(byte[] gzipBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gzipBytes);
        using var input = new MemoryStream(gzipBytes);
        using var gunzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        await gunzip.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private async Task<byte[]> DownloadAsync(string name, string architecture, bool musl, CancellationToken cancellationToken)
    {
        var url = ArtifactUrl(name);
        using var response = await _client.Value.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            var buildCommand = $"python src/inspect_ai/tool/_sandbox_tools_utils/build_within_container.py --arch {architecture}" + (musl ? " --musl" : "");
            throw new PrerequisiteError(
                $"Executable '{name}' not found on S3 ({url}). Container tools executable {name} is required but not present. "
                + $"To build it from the inspect_ai repository, run: {buildCommand}, then copy the artifact to {BinariesDir}.");
        }

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

    /// <summary>Unique temp file then rename, so two processes racing for the same artifact never observe a partial file.</summary>
    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private HttpClient CreateClient()
    {
        HttpMessageHandler handler = _httpHandler ?? new SocketsHttpHandler { AllowAutoRedirect = true, ConnectTimeout = ConnectTimeout };
        return new HttpClient(handler, disposeHandler: _httpHandler is null) { Timeout = HttpTimeout };
    }

    [GeneratedRegex(@"^inspect-sandbox-tools-(?<arch>\w+)(?:-(?<libc>musl))?-v(?<version>\d+)(?:-(?<suffix>\w+))?$")]
    private static partial Regex ExecutableNameRegex();
}
