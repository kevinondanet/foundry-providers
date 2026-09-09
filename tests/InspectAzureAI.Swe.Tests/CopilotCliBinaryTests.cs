using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.CopilotCli;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// The GitHub-release resolver of the Copilot CLI binary against a fake release server: <c>SHA256SUMS.txt</c>
/// parsing, the cache hit that needs no network, the digest-file contract, checksum refusal, retries and the
/// sandbox install (write the tarball, <c>tar</c>, <c>chmod</c>).
/// </summary>
[Collection("ClaudeCode")]
public class CopilotCliBinaryTests
{
    private const string BaseUrl = "https://releases.test/download";

    private const string Version = "1.0.83";

    private const string Platform = "linux-arm64";

    private const string Asset = "copilot-linux-arm64.tar.gz";

    private static readonly byte[] Executable = Encoding.ASCII.GetBytes("#!/bin/sh\necho copilot\n");

    private static readonly byte[] Archive = TarGz("copilot", Executable);

    private static readonly string Checksum = CopilotCliBinary.Sha256Hex(Archive);

    private static readonly string Checksums =
        $"ffbe1c429664b8a05efed67ecdb467123e40fcaa3c6c14ef9a98ba74da4687b7  copilot-linux-x64.tar.gz\n{Checksum}  {Asset}\n"
        + "83e462836ad2461a2c1a27392e78ecae3d3774ec86974520dd2ebd52b1e425dd  copilot-linuxmusl-arm64.tar.gz\n";

    /// <summary>Answers requests by URL and records them; unknown URLs are 404.</summary>
    private sealed class FakeRelease : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public FakeRelease Text(string url, string body) => Route(url, () => Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(body)));

        public FakeRelease Bytes(string url, byte[] body) => Route(url, () => Response(HttpStatusCode.OK, body));

        public FakeRelease Status(string url, HttpStatusCode status) => Route(url, () => Response(status, []));

        public FakeRelease Route(string url, Func<HttpResponseMessage> respond)
        {
            Routes[url] = respond;
            return this;
        }

        public int Count(string suffix) => Requests.Count(r => r.EndsWith(suffix, StringComparison.Ordinal));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var response = Routes.TryGetValue(url, out var respond) ? respond() : Response(HttpStatusCode.NotFound, []);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Response(HttpStatusCode status, byte[] body) => new(status) { Content = new ByteArrayContent(body) };
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inspect-swe-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    /// <summary>A gzip'd tar with one regular file, built with the framework's tar and gzip writers (no package).</summary>
    private static byte[] TarGz(string name, byte[] content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(content) };
            entry.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            tar.WriteEntry(entry);
        }

        return output.ToArray();
    }

    private static FakeRelease Release(byte[]? archive = null, string? checksums = null) =>
        new FakeRelease()
            .Text($"{BaseUrl}/v{Version}/SHA256SUMS.txt", checksums ?? Checksums)
            .Bytes($"{BaseUrl}/v{Version}/{Asset}", archive ?? Archive);

    private static CopilotCliBinary NewBinary(TempDir cache, FakeRelease release, List<TimeSpan>? delays = null) =>
        new(cache.Path, BaseUrl, release, (delay, _) =>
        {
            delays?.Add(delay);
            return Task.CompletedTask;
        });

    private static FakeSandboxEnvironment LinuxSandbox(string? copilotPath = null, bool installed = false) =>
        new(cmd => cmd[0] switch
        {
            "test" => installed ? FakeSandboxEnvironment.Ok() : FakeSandboxEnvironment.Fail(1),
            "mkdir" or "tar" or "chmod" or "rm" => FakeSandboxEnvironment.Ok(),
            _ => cmd[2] switch
            {
                "which copilot" => copilotPath is null ? FakeSandboxEnvironment.Fail(1) : FakeSandboxEnvironment.Ok(copilotPath + "\n"),
                "uname -s" => FakeSandboxEnvironment.Ok("Linux\n"),
                "uname -m" => FakeSandboxEnvironment.Ok("aarch64\n"),
                var c when c.StartsWith("if [ -f /lib/libc.musl", StringComparison.Ordinal) => FakeSandboxEnvironment.Ok("glibc\n"),
                _ => FakeSandboxEnvironment.Fail(1, "unexpected command " + cmd[2]),
            },
        });

    [Theory]
    [InlineData("1.0.83", true)]
    [InlineData("10.0.205", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("v1.0.83", false)]
    [InlineData("1.0", false)]
    [InlineData("1.0.83\n", false)]
    [InlineData("auto", false)]
    [InlineData("", false)]
    public void versions_are_validated_as_bare_semver(string version, bool valid)
    {
        Assert.Equal(valid, CopilotCliBinary.IsValidVersion(version));
        if (!valid)
        {
            Assert.Contains("Invalid version target", Assert.Throws<InvalidOperationException>(() => CopilotCliBinary.ValidateVersion(version)).Message);
        }
    }

    [Theory]
    [InlineData("linux-x64", "copilot-linux-x64.tar.gz")]
    [InlineData("linux-arm64", "copilot-linux-arm64.tar.gz")]
    [InlineData("linux-x64-musl", "copilot-linuxmusl-x64.tar.gz")]
    [InlineData("linux-arm64-musl", "copilot-linuxmusl-arm64.tar.gz")]
    public void platforms_map_to_the_release_asset_names(string platform, string asset)
    {
        Assert.Equal(asset, CopilotCliBinary.AssetName(platform));
        Assert.Throws<PlatformNotSupportedException>(() => CopilotCliBinary.AssetName("darwin-arm64"));
    }

    [Fact]
    public void checksums_are_parsed_by_asset_name()
    {
        Assert.Equal(Checksum, CopilotCliBinary.ParseChecksums(Checksums, Asset));
        Assert.Equal("ffbe1c429664b8a05efed67ecdb467123e40fcaa3c6c14ef9a98ba74da4687b7", CopilotCliBinary.ParseChecksums(Checksums, "copilot-linux-x64.tar.gz"));
        Assert.Equal("abcd".PadRight(64, '0'), CopilotCliBinary.ParseChecksums("ABCD".PadRight(64, '0') + " *copilot-linux-x64.tar.gz\r\n", "copilot-linux-x64.tar.gz"));
        var missing = Assert.Throws<InvalidOperationException>(() => CopilotCliBinary.ParseChecksums(Checksums, "copilot-win32-x64.zip"));
        Assert.Equal("Asset 'copilot-win32-x64.zip' not found in SHA256SUMS.txt.", missing.Message);
        Assert.Throws<InvalidOperationException>(() => CopilotCliBinary.ParseChecksums("nothex  " + Asset, Asset));
    }

    [Fact]
    public async Task download_verifies_against_the_checksum_list_and_caches_the_archive_with_its_digest()
    {
        using var cache = new TempDir();
        var release = Release();
        var binary = NewBinary(cache, release);

        var (data, checksum) = await binary.ResolveArchiveAsync(Version, Platform);

        Assert.Equal(Archive, data);
        Assert.Equal(Checksum, checksum);
        Assert.Equal([$"{BaseUrl}/v{Version}/SHA256SUMS.txt", $"{BaseUrl}/v{Version}/{Asset}"], release.Requests);
        Assert.Equal(Archive, File.ReadAllBytes(Path.Combine(cache.Path, $"copilot-{Version}-{Platform}.tar.gz")));
        Assert.Equal(Checksum, File.ReadAllText(Path.Combine(cache.Path, $"copilot-{Version}-{Platform}.tar.gz.sha256")).Trim());
        Assert.Equal(Checksums, File.ReadAllText(Path.Combine(cache.Path, $"SHA256SUMS-{Version}.txt")));
        Assert.True(CopilotCliBinary.VerifyChecksum(Archive, Checksum.ToUpperInvariant()));
    }

    [Fact]
    public async Task a_cached_archive_with_a_matching_digest_file_needs_no_network()
    {
        using var cache = new TempDir();
        var offline = new FakeRelease();
        Directory.CreateDirectory(cache.Path);
        File.WriteAllBytes(Path.Combine(cache.Path, $"copilot-{Version}-{Platform}.tar.gz"), Archive);
        File.WriteAllText(Path.Combine(cache.Path, $"copilot-{Version}-{Platform}.tar.gz.sha256"), Checksum);
        var binary = NewBinary(cache, offline);
        var sandbox = LinuxSandbox();

        var (data, _) = await binary.ResolveArchiveAsync(Version, Platform);
        var path = await binary.EnsureInstalledAsync(sandbox, Version, user: "agent");

        Assert.Equal(Archive, data);
        Assert.Empty(offline.Requests);
        Assert.Equal("/var/tmp/.5c95f967ca830048/copilot-1.0.83-linux-arm64/copilot", path);
    }

    [Fact]
    public async Task a_cached_archive_whose_digest_file_does_not_match_is_discarded_and_re_downloaded()
    {
        using var cache = new TempDir();
        var release = Release();
        Directory.CreateDirectory(cache.Path);
        var archivePath = Path.Combine(cache.Path, $"copilot-{Version}-{Platform}.tar.gz");
        File.WriteAllBytes(archivePath, Encoding.ASCII.GetBytes("corrupt"));
        File.WriteAllText(archivePath + ".sha256", Checksum);

        var (data, _) = await NewBinary(cache, release).ResolveArchiveAsync(Version, Platform);

        Assert.Equal(Archive, data);
        Assert.Equal(1, release.Count($"/{Asset}"));
        Assert.Equal(Archive, File.ReadAllBytes(archivePath));
        Assert.Contains(ProviderLogger.Warnings, w => w.Contains("does not match its digest file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task a_cached_archive_without_a_digest_file_is_verified_against_the_release_list_and_reused()
    {
        using var cache = new TempDir();
        var release = Release();
        Directory.CreateDirectory(cache.Path);
        var archivePath = Path.Combine(cache.Path, $"copilot-{Version}-{Platform}.tar.gz");
        File.WriteAllBytes(archivePath, Archive);

        var (data, _) = await NewBinary(cache, release).ResolveArchiveAsync(Version, Platform);

        Assert.Equal(Archive, data);
        Assert.Equal([$"{BaseUrl}/v{Version}/SHA256SUMS.txt"], release.Requests);
        Assert.Equal(Checksum, File.ReadAllText(archivePath + ".sha256").Trim());
    }

    [Fact]
    public async Task a_download_that_does_not_match_the_release_list_is_refused_and_not_cached()
    {
        using var cache = new TempDir();
        var release = Release(archive: TarGz("copilot", Encoding.ASCII.GetBytes("tampered")));
        var sandbox = LinuxSandbox();

        var direct = await Assert.ThrowsAsync<ChecksumMismatchException>(() => NewBinary(cache, release).ResolveArchiveAsync(Version, Platform));
        var install = await Assert.ThrowsAsync<ChecksumMismatchException>(() => NewBinary(cache, release).EnsureInstalledAsync(sandbox, Version));

        Assert.Equal("Checksum verification failed", direct.Message);
        Assert.Equal("Checksum verification failed", install.Message);
        Assert.False(File.Exists(Path.Combine(cache.Path, $"copilot-{Version}-{Platform}.tar.gz")));
        Assert.Empty(sandbox.Files);
    }

    [Fact]
    public async Task an_asset_missing_from_the_list_and_a_release_that_is_not_there_fail_without_a_write()
    {
        using var cache = new TempDir();
        var noAsset = new FakeRelease().Text($"{BaseUrl}/v{Version}/SHA256SUMS.txt", "0000000000000000000000000000000000000000000000000000000000000000  other.zip\n");
        var missing = new FakeRelease();
        var sandbox = LinuxSandbox();

        var notListed = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, noAsset).EnsureInstalledAsync(sandbox, Version));
        var notFound = await Assert.ThrowsAsync<HttpRequestException>(() => NewBinary(cache, missing).EnsureInstalledAsync(sandbox, Version));

        Assert.Contains(Asset, notListed.Message);
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Empty(sandbox.Files);
        Assert.Empty(Directory.Exists(cache.Path) ? Directory.GetFiles(cache.Path, "copilot-*") : []);
    }

    [Fact]
    public async Task transient_failures_retry_with_backoff_and_four_xx_fails_immediately()
    {
        using var cache = new TempDir();
        var delays = new List<TimeSpan>();
        var flaky = 0;
        var release = new FakeRelease()
            .Status("https://releases.test/missing", HttpStatusCode.NotFound)
            .Route("https://releases.test/flaky", () => ++flaky < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Archive) })
            .Route("https://releases.test/broken", () => throw new HttpRequestException("connection reset"));
        var binary = NewBinary(cache, release, delays);

        var notFound = await Assert.ThrowsAsync<HttpRequestException>(() => binary.DownloadFileAsync("https://releases.test/missing"));
        var data = await binary.DownloadFileAsync("https://releases.test/flaky");
        var broken = await Assert.ThrowsAsync<HttpRequestException>(() => binary.DownloadFileAsync("https://releases.test/broken"));

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(1, release.Count("/missing"));
        Assert.Equal(Archive, data);
        Assert.Equal(3, release.Count("/flaky"));
        Assert.Equal("connection reset", broken.Message);
        Assert.Equal(4, release.Count("/broken"));
        Assert.Equal([1, 2, 1, 2, 4], delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task install_writes_the_tarball_extracts_it_as_root_and_returns_the_executable()
    {
        using var cache = new TempDir();
        var sandbox = LinuxSandbox();

        var path = await NewBinary(cache, Release()).EnsureInstalledAsync(sandbox, Version, user: "agent");

        const string installDir = "/var/tmp/.5c95f967ca830048/copilot-1.0.83-linux-arm64";
        Assert.Equal(installDir + "/copilot", path);
        var archive = Assert.Single(sandbox.Files);
        Assert.Equal(installDir + ".tar.gz", archive.Key);
        Assert.Equal(Archive, archive.Value);
        using var reader = new TarReader(new GZipStream(new MemoryStream(archive.Value), CompressionMode.Decompress));
        var entry = reader.GetNextEntry()!;
        Assert.Equal("copilot", entry.Name);
        using var content = new MemoryStream();
        entry.DataStream!.CopyTo(content);
        Assert.Equal(Executable, content.ToArray());

        var rootCalls = sandbox.Calls.Where(c => c.User == "root").Select(c => c.Cmd).ToArray();
        Assert.Equal(
            [
                ["test", "-x", installDir + "/copilot"],
                ["mkdir", "-p", installDir],
                ["tar", "-xzf", installDir + ".tar.gz", "-C", installDir],
                ["chmod", "+x", installDir + "/copilot"],
                ["rm", "-f", installDir + ".tar.gz"],
            ],
            rootCalls);
        Assert.Equal("uname -s", sandbox.Calls[0].Cmd[2]);
        Assert.DoesNotContain(sandbox.Calls, c => c.Cmd.Count > 2 && c.Cmd[2] == "which copilot");
    }

    [Fact]
    public async Task an_executable_already_in_the_install_dir_is_reused_without_a_write()
    {
        using var cache = new TempDir();
        var offline = new FakeRelease();
        var sandbox = LinuxSandbox(installed: true);

        var path = await NewBinary(cache, offline).EnsureInstalledAsync(sandbox, Version);

        Assert.Equal("/var/tmp/.5c95f967ca830048/copilot-1.0.83-linux-arm64/copilot", path);
        Assert.Empty(sandbox.Files);
        Assert.Empty(offline.Requests);
        Assert.Single(sandbox.Calls, c => c.Cmd[0] == "test");
    }

    [Fact]
    public async Task auto_uses_a_sandbox_copilot_else_installs_the_default_version_and_sandbox_requires_one()
    {
        using var cache = new TempDir();
        var release = Release();
        var present = LinuxSandbox("/usr/local/bin/copilot");
        var absent = LinuxSandbox();

        var found = await NewBinary(cache, release).EnsureInstalledAsync(present, "auto", user: "agent");
        var installed = await NewBinary(cache, release).EnsureInstalledAsync(absent, "auto");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, new FakeRelease()).EnsureInstalledAsync(LinuxSandbox(), "sandbox"));

        Assert.Equal("/usr/local/bin/copilot", found);
        Assert.Single(present.Calls);
        Assert.Equal("agent", present.Calls[0].User);
        Assert.Equal($"{SandboxUtil.SandboxInstallDir}/copilot-{CopilotCliOptions.DefaultVersion}-linux-arm64/copilot", installed);
        Assert.Equal("unable to locate copilot cli in sandbox", ex.Message);
        Assert.Equal(1, release.Count($"/{Asset}"));
    }

    [Fact]
    public async Task invalid_versions_are_rejected_before_any_network_or_sandbox_write()
    {
        using var cache = new TempDir();
        var release = Release();
        var sandbox = LinuxSandbox();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, release).EnsureInstalledAsync(sandbox, "v1.0.83"));

        Assert.Contains("Invalid version target", ex.Message);
        Assert.Empty(release.Requests);
        Assert.Empty(sandbox.Files);
    }

    [Fact]
    public void cache_is_pruned_to_the_three_most_recently_accessed_archives_with_their_digests()
    {
        using var cache = new TempDir();
        Directory.CreateDirectory(cache.Path);
        var now = DateTime.UtcNow;
        foreach (var (name, age) in new[] { ("copilot-1.0.80-linux-x64", 4), ("copilot-1.0.81-linux-x64", 1), ("copilot-1.0.82-linux-x64", 3), ("copilot-1.0.83-linux-arm64", 2) })
        {
            var path = Path.Combine(cache.Path, name + ".tar.gz");
            File.WriteAllBytes(path, [1]);
            File.WriteAllText(path + ".sha256", "x");
            File.SetLastAccessTimeUtc(path, now.AddHours(-age));
        }

        File.WriteAllText(Path.Combine(cache.Path, "SHA256SUMS-1.0.83.txt"), "list");
        var binary = new CopilotCliBinary(cache.Path);

        binary.WriteCachedArchive(Archive, "2.0.0", Platform, Checksum);

        var remaining = new DirectoryInfo(cache.Path).GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            [
                "SHA256SUMS-1.0.83.txt",
                "copilot-1.0.81-linux-x64.tar.gz", "copilot-1.0.81-linux-x64.tar.gz.sha256",
                "copilot-1.0.83-linux-arm64.tar.gz", "copilot-1.0.83-linux-arm64.tar.gz.sha256",
                "copilot-2.0.0-linux-arm64.tar.gz", "copilot-2.0.0-linux-arm64.tar.gz.sha256",
            ],
            remaining);
        Assert.Equal(3, binary.ListCachedArchives().Count);
    }

    [Fact]
    public async Task a_cached_checksum_list_is_used_before_the_release_is_asked()
    {
        using var cache = new TempDir();
        Directory.CreateDirectory(cache.Path);
        File.WriteAllText(Path.Combine(cache.Path, $"SHA256SUMS-{Version}.txt"), Checksums);
        var release = new FakeRelease().Bytes($"{BaseUrl}/v{Version}/{Asset}", Archive);

        var (data, _) = await NewBinary(cache, release).ResolveArchiveAsync(Version, Platform);

        Assert.Equal(Archive, data);
        Assert.Equal([$"{BaseUrl}/v{Version}/{Asset}"], release.Requests);
    }

    [Fact]
    public async Task a_cached_checksum_list_without_the_asset_is_refetched_from_the_release_once()
    {
        using var cache = new TempDir();
        Directory.CreateDirectory(cache.Path);
        var listPath = Path.Combine(cache.Path, $"SHA256SUMS-{Version}.txt");
        File.WriteAllText(listPath, "0000000000000000000000000000000000000000000000000000000000000000  copilot-linux-x64.tar.gz\n");
        var release = Release();

        var (data, checksum) = await NewBinary(cache, release).ResolveArchiveAsync(Version, Platform);

        Assert.Equal(Archive, data);
        Assert.Equal(Checksum, checksum);
        Assert.Equal([$"{BaseUrl}/v{Version}/SHA256SUMS.txt", $"{BaseUrl}/v{Version}/{Asset}"], release.Requests);
        Assert.Equal(Checksums, File.ReadAllText(listPath));
        Assert.Empty(Directory.GetFiles(cache.Path, "*.tmp"));
    }

    [Fact]
    public async Task a_download_larger_than_the_limit_is_rejected_from_its_content_length()
    {
        using var cache = new TempDir();
        var release = Release().Route($"{BaseUrl}/v{Version}/{Asset}", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentLength = CopilotCliBinary.MaxDownloadBytes + 1;
            return response;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, release).ResolveArchiveAsync(Version, Platform));

        Assert.Contains("download limit", ex.Message);
        Assert.Equal(1, release.Count($"/{Asset}"));
    }
}
