using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Port of inspect_swe <c>tests/test_claude_code_agentbinary.py</c> plus the download, checksum, cache and sandbox-install rules of <c>_util/agentbinary.py</c>, against a fake CDN.</summary>
[Collection("ClaudeCode")]
public class ClaudeCodeBinaryTests
{
    private const string BaseUrl = "https://cdn.test/releases";

    private const string Platform = "linux-x64";

    private static readonly byte[] Binary = Encoding.ASCII.GetBytes("#!/bin/sh\necho claude\n");

    private static readonly string Checksum = ClaudeCodeBinary.Sha256Hex(Binary);

    /// <summary>Answers requests by URL and records them; unknown URLs are 404.</summary>
    private sealed class FakeCdn : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public FakeCdn Text(string url, string body) => Route(url, () => Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(body)));

        public FakeCdn Bytes(string url, byte[] body) => Route(url, () => Response(HttpStatusCode.OK, body));

        public FakeCdn Status(string url, HttpStatusCode status) => Route(url, () => Response(status, []));

        public FakeCdn Route(string url, Func<HttpResponseMessage> respond)
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

    private static FakeCdn Cdn(string version = "1.2.3", byte[]? binary = null, string? checksum = null)
    {
        binary ??= Binary;
        checksum ??= ClaudeCodeBinary.Sha256Hex(binary);
        return new FakeCdn()
            .Text(ClaudeCodeBinary.InstallScriptUrl, $"#!/bin/bash\nset -e\nDOWNLOAD_BASE_URL=\"{BaseUrl}\"\n")
            .Text($"{BaseUrl}/stable", version)
            .Text($"{BaseUrl}/latest", version)
            .Text($"{BaseUrl}/{version}/manifest.json", $"{{\"version\":\"{version}\",\"platforms\":{{\"{Platform}\":{{\"checksum\":\"{checksum}\",\"size\":{binary.Length}}}}}}}")
            .Bytes($"{BaseUrl}/{version}/{Platform}/claude", binary);
    }

    private static ClaudeCodeBinary NewBinary(TempDir cache, FakeCdn cdn, List<TimeSpan>? delays = null, string? baseUrl = null) =>
        new(cache.Path, baseUrl, cdn, (delay, _) =>
        {
            delays?.Add(delay);
            return Task.CompletedTask;
        });

    private static FakeSandboxEnvironment LinuxSandbox(string? claudePath = null) =>
        new(cmd => cmd is ["chmod", "+x", _] ? FakeSandboxEnvironment.Ok() : cmd[2] switch
        {
            "which claude" => claudePath is null ? FakeSandboxEnvironment.Fail(1) : FakeSandboxEnvironment.Ok(claudePath + "\n"),
            "uname -s" => FakeSandboxEnvironment.Ok("Linux\n"),
            "uname -m" => FakeSandboxEnvironment.Ok("x86_64\n"),
            var c when c.StartsWith("if [ -f /lib/libc.musl", StringComparison.Ordinal) => FakeSandboxEnvironment.Ok("glibc\n"),
            _ => FakeSandboxEnvironment.Fail(1, "unexpected command " + cmd[2]),
        });

    [Theory]
    [InlineData("stable", true)]
    [InlineData("latest", true)]
    [InlineData("1.2.3", true)]
    [InlineData("10.0.205", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("1.2.3-rc1+build", true)]
    [InlineData("v1.2.3", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3-", false)]
    [InlineData("1.2.3 ", false)]
    [InlineData("1.2.3-a b", false)]
    [InlineData("auto", false)]
    [InlineData("sandbox", false)]
    [InlineData("", false)]
    public void version_targets_are_validated_as_pointer_or_semver(string target, bool valid)
    {
        Assert.Equal(valid, ClaudeCodeBinary.IsValidVersionTarget(target));
        if (!valid)
        {
            var ex = Assert.Throws<InvalidOperationException>(() => ClaudeCodeBinary.ValidateVersionTarget(target));
            Assert.Equal("Invalid version target (must be 'stable', 'latest', or a semver version number)", ex.Message);
        }
    }

    [Theory]
    [InlineData("export DOWNLOAD_BASE_URL='https://downloads.claude.ai/claude-code-releases'", "https://downloads.claude.ai/claude-code-releases")]
    [InlineData("GCS_BUCKET=\"https://storage.googleapis.com/legacy-claude-code\"", "https://storage.googleapis.com/legacy-claude-code")]
    [InlineData("  DOWNLOAD_BASE_URL = \"https://a.test/x\"\nGCS_BUCKET=\"https://b.test/y\"", "https://a.test/x")]
    public void download_base_url_supports_current_and_legacy_install_scripts(string assignment, string expected)
    {
        Assert.Equal(expected, ClaudeCodeBinary.ParseDownloadBaseUrl($"#!/bin/bash\n{assignment}\n"));
    }

    [Fact]
    public async Task install_script_without_an_assignment_is_an_error_and_a_failed_fetch_falls_back()
    {
        using var cache = new TempDir();
        var noMatch = new FakeCdn().Text(ClaudeCodeBinary.InstallScriptUrl, "#!/bin/bash\necho hi\nDOWNLOAD_BASE_URL=http://unquoted\n");
        var down = new FakeCdn().Status(ClaudeCodeBinary.InstallScriptUrl, HttpStatusCode.ServiceUnavailable);
        var delays = new List<TimeSpan>();

        Assert.Null(ClaudeCodeBinary.ParseDownloadBaseUrl("nothing here"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, noMatch).ResolveDownloadBaseUrlAsync());
        var fallback = await NewBinary(cache, down, delays).ResolveDownloadBaseUrlAsync();

        Assert.Equal("Unable to determine download base URL for claude code.", ex.Message);
        Assert.Equal(ClaudeCodeBinary.FallbackDownloadBaseUrl, fallback);
        Assert.Equal(4, down.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.Equal("https://downloads.claude.ai/claude-code-releases", ClaudeCodeBinary.FallbackDownloadBaseUrl);
    }

    [Fact]
    public async Task stable_pointer_resolves_version_checksum_and_download_url()
    {
        using var cache = new TempDir();
        var cdn = Cdn(version: "2.1.205");

        var resolved = await NewBinary(cache, cdn).ResolveAsync("stable", Platform);

        Assert.Equal("2.1.205", resolved.Version);
        Assert.Equal(Checksum, resolved.ExpectedChecksum);
        Assert.Equal($"{BaseUrl}/2.1.205/{Platform}/claude", resolved.DownloadUrl);
        Assert.Equal([ClaudeCodeBinary.InstallScriptUrl, $"{BaseUrl}/stable", $"{BaseUrl}/2.1.205/manifest.json"], cdn.Requests);
    }

    [Fact]
    public async Task a_concrete_version_skips_the_pointer_and_a_configured_base_url_skips_discovery()
    {
        using var cache = new TempDir();
        var cdn = Cdn();

        var resolved = await NewBinary(cache, cdn, baseUrl: BaseUrl).ResolveAsync("1.2.3", Platform);

        Assert.Equal("1.2.3", resolved.Version);
        Assert.Equal([$"{BaseUrl}/1.2.3/manifest.json"], cdn.Requests);
    }

    [Fact]
    public async Task manifest_without_the_platform_is_an_error()
    {
        using var cache = new TempDir();
        var manifest = JsonNode.Parse("{\"version\":\"1.2.3\",\"platforms\":{\"linux-x64\":{\"checksum\":\"abc\",\"size\":1}}}")!;

        Assert.Equal("abc", ClaudeCodeBinary.ChecksumForPlatform(manifest, "linux-x64"));
        var ex = Assert.Throws<InvalidOperationException>(() => ClaudeCodeBinary.ChecksumForPlatform(manifest, "linux-arm64-musl"));
        var viaCdn = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, Cdn()).ResolveAsync("1.2.3", "linux-arm64"));

        Assert.Equal("Platform 'linux-arm64-musl' not found in manifest.", ex.Message);
        Assert.Equal("Platform 'linux-arm64' not found in manifest.", viaCdn.Message);
    }

    [Fact]
    public async Task download_verifies_the_checksum_and_caches_the_binary()
    {
        using var cache = new TempDir();
        var cdn = Cdn();

        var (data, resolved) = await NewBinary(cache, cdn).DownloadAsync("stable", Platform);
        var (again, _) = await NewBinary(cache, cdn).DownloadAsync("1.2.3", Platform);

        Assert.Equal(Binary, data);
        Assert.Equal(Binary, again);
        Assert.Equal("1.2.3", resolved.Version);
        Assert.Equal(Binary, File.ReadAllBytes(Path.Combine(cache.Path, "claude-1.2.3-linux-x64")));
        Assert.Equal(1, cdn.Count("/claude"));
        Assert.True(ClaudeCodeBinary.VerifyChecksum(Binary, Checksum));
        Assert.False(ClaudeCodeBinary.VerifyChecksum(Binary, Checksum.ToUpperInvariant()));
    }

    [Fact]
    public async Task checksum_mismatch_is_never_masked_by_the_cache()
    {
        using var cache = new TempDir();
        var cdn = Cdn(binary: Encoding.ASCII.GetBytes("tampered"), checksum: Checksum);
        Directory.CreateDirectory(cache.Path);
        var cached = Path.Combine(cache.Path, "claude-1.2.3-linux-x64");
        File.WriteAllBytes(cached, Encoding.ASCII.GetBytes("stale and wrong"));

        var direct = await Assert.ThrowsAsync<ChecksumMismatchException>(() => NewBinary(cache, cdn).DownloadAsync("stable", Platform));
        File.WriteAllBytes(cached, Encoding.ASCII.GetBytes("stale and wrong"));
        var sandbox = LinuxSandbox();
        var install = await Assert.ThrowsAsync<ChecksumMismatchException>(() => NewBinary(cache, cdn).EnsureInstalledAsync(sandbox, "stable"));

        Assert.Equal("Checksum verification failed", direct.Message);
        Assert.Equal("Checksum verification failed", install.Message);
        Assert.False(File.Exists(cached));
        Assert.Empty(sandbox.Files);
    }

    [Fact]
    public void cache_is_pruned_to_the_three_most_recently_accessed()
    {
        using var cache = new TempDir();
        Directory.CreateDirectory(cache.Path);
        var now = DateTime.UtcNow;
        foreach (var (name, age) in new[] { ("claude-1.0.0-linux-x64", 4), ("claude-1.0.1-linux-x64", 1), ("claude-1.0.2-linux-x64", 3), ("claude-1.0.3-linux-arm64", 2) })
        {
            var path = Path.Combine(cache.Path, name);
            File.WriteAllBytes(path, [1]);
            File.SetLastAccessTimeUtc(path, now.AddHours(-age));
        }

        File.WriteAllBytes(Path.Combine(cache.Path, "other-file"), [1]);
        var binary = new ClaudeCodeBinary(cache.Path);

        binary.WriteCachedFile(Binary, binary.CachedBinaryPath("2.0.0", Platform));

        var remaining = new DirectoryInfo(cache.Path).GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["claude-1.0.1-linux-x64", "claude-1.0.3-linux-arm64", "claude-2.0.0-linux-x64", "other-file"], remaining);
        Assert.Equal(3, binary.ListCachedBinaries().Count);
    }

    [Fact]
    public void cache_read_touches_a_hit_and_deletes_a_mismatch()
    {
        using var cache = new TempDir();
        var binary = new ClaudeCodeBinary(cache.Path);
        binary.WriteCachedFile(Binary, binary.CachedBinaryPath("1.2.3", Platform));
        var path = binary.CachedBinaryPath("1.2.3", Platform);
        File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddDays(-2));

        var hit = binary.ReadCachedBinary("1.2.3", Platform, Checksum);
        var touched = File.GetLastAccessTimeUtc(path);
        var unverified = binary.ReadCachedBinary("1.2.3", Platform, null);
        var miss = binary.ReadCachedBinary("1.2.3", Platform, new string('0', 64));

        Assert.Equal(Binary, hit);
        Assert.Equal(Binary, unverified);
        Assert.True(touched > DateTime.UtcNow.AddMinutes(-5));
        Assert.Null(miss);
        Assert.False(File.Exists(path));
        Assert.Null(binary.ReadCachedBinary("9.9.9", Platform, null));
    }

    [Fact]
    public async Task concrete_version_installs_from_the_cache_without_the_network()
    {
        using var cache = new TempDir();
        var cdn = new FakeCdn();
        var binary = NewBinary(cache, cdn);
        binary.WriteCachedFile(Binary, binary.CachedBinaryPath("1.2.3", Platform));
        var sandbox = LinuxSandbox();

        var path = await binary.EnsureInstalledAsync(sandbox, "1.2.3", user: "agent");

        Assert.Equal("/var/tmp/.5c95f967ca830048/claude-1.2.3-linux-x64", path);
        Assert.Equal(Binary, sandbox.Files[path]);
        Assert.Empty(cdn.Requests);
        var chmod = sandbox.Calls.Single(c => c.Cmd[0] == "chmod");
        Assert.Equal(["chmod", "+x", path], chmod.Cmd);
        Assert.Equal("root", chmod.User);
        Assert.DoesNotContain(sandbox.Calls, c => c.Cmd[2] == "which claude");
        Assert.Equal("uname -s", sandbox.Calls[0].Cmd[2]);
    }

    [Fact]
    public async Task pointer_with_the_network_down_fails_after_retries_with_no_fallback()
    {
        using var cache = new TempDir();
        var cdn = new FakeCdn().Status($"{BaseUrl}/stable", HttpStatusCode.BadGateway);
        var delays = new List<TimeSpan>();
        var sandbox = LinuxSandbox();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => NewBinary(cache, cdn, delays, BaseUrl).EnsureInstalledAsync(sandbox, "stable"));

        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
        Assert.Equal(4, cdn.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.Empty(sandbox.Files);
    }

    [Fact]
    public async Task four_xx_fails_immediately_and_transient_failures_retry_with_backoff()
    {
        using var cache = new TempDir();
        var delays = new List<TimeSpan>();
        var flaky = 0;
        var cdn = new FakeCdn()
            .Status("https://cdn.test/missing", HttpStatusCode.NotFound)
            .Route("https://cdn.test/flaky", () => ++flaky < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new ByteArrayContent([]) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Binary) })
            .Route("https://cdn.test/broken", () => throw new HttpRequestException("connection reset"));
        var binary = NewBinary(cache, cdn, delays);

        var notFound = await Assert.ThrowsAsync<HttpRequestException>(() => binary.DownloadFileAsync("https://cdn.test/missing"));
        var data = await binary.DownloadFileAsync("https://cdn.test/flaky");
        var broken = await Assert.ThrowsAsync<HttpRequestException>(() => binary.DownloadFileAsync("https://cdn.test/broken"));

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal(1, cdn.Count("/missing"));
        Assert.Equal(Binary, data);
        Assert.Equal(3, cdn.Count("/flaky"));
        Assert.Equal("connection reset", broken.Message);
        Assert.Equal(4, cdn.Count("/broken"));
        Assert.Equal([1, 2, 1, 2, 4], delays.Select(d => (int)d.TotalSeconds));
    }

    [Fact]
    public async Task sandbox_binary_is_used_when_present_and_required_by_the_sandbox_version()
    {
        using var cache = new TempDir();
        var cdn = new FakeCdn();
        var present = LinuxSandbox("/usr/local/bin/claude");
        var absent = LinuxSandbox();

        var path = await NewBinary(cache, cdn).EnsureInstalledAsync(present, "auto", user: "agent");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, cdn).EnsureInstalledAsync(absent, "sandbox"));

        Assert.Equal("/usr/local/bin/claude", path);
        Assert.Single(present.Calls);
        Assert.Equal("agent", present.Calls[0].User);
        Assert.Equal("unable to locate claude code in sandbox", ex.Message);
        Assert.Empty(cdn.Requests);
    }

    [Fact]
    public async Task auto_falls_back_to_stable_and_installs_into_the_sandbox()
    {
        using var cache = new TempDir();
        var cdn = Cdn(version: "3.0.0");
        var sandbox = LinuxSandbox();

        var path = await NewBinary(cache, cdn).EnsureInstalledAsync(sandbox, "auto");

        Assert.Equal($"{SandboxUtil.SandboxInstallDir}/claude-3.0.0-linux-x64", path);
        Assert.Equal(Binary, sandbox.Files[path]);
        Assert.True(File.Exists(Path.Combine(cache.Path, "claude-3.0.0-linux-x64")));
        Assert.Equal(1, cdn.Count("/stable"));
        Assert.Equal("root", sandbox.Calls.Last().User);
        Assert.Equal(["chmod", "+x", path], sandbox.Calls.Last().Cmd);
    }

    [Fact]
    public async Task invalid_version_targets_are_rejected_before_any_network_or_sandbox_write()
    {
        using var cache = new TempDir();
        var cdn = Cdn();
        var sandbox = LinuxSandbox();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, cdn).EnsureInstalledAsync(sandbox, "v1.2.3"));

        Assert.Equal("Invalid version target (must be 'stable', 'latest', or a semver version number)", ex.Message);
        Assert.Empty(cdn.Requests);
        Assert.Empty(sandbox.Files);
    }

    [Fact]
    public async Task version_resolution_is_memoized_per_instance_and_failures_are_not_cached()
    {
        using var cache = new TempDir();
        var cdn = Cdn();
        var binary = NewBinary(cache, cdn, baseUrl: BaseUrl);
        var failing = new FakeCdn().Status($"{BaseUrl}/latest", HttpStatusCode.NotFound);
        var retrying = NewBinary(cache, failing, baseUrl: BaseUrl);

        var first = await binary.ResolveAsync("stable", Platform);
        var second = await binary.ResolveAsync("stable", Platform);
        var other = await binary.ResolveAsync("1.2.3", Platform);
        await Assert.ThrowsAsync<HttpRequestException>(() => retrying.ResolveAsync("latest", Platform));
        failing.Text($"{BaseUrl}/latest", "1.2.3").Text($"{BaseUrl}/1.2.3/manifest.json", $"{{\"version\":\"1.2.3\",\"platforms\":{{\"{Platform}\":{{\"checksum\":\"{Checksum}\",\"size\":1}}}}}}");
        var recovered = await retrying.ResolveAsync("latest", Platform);

        Assert.Same(first, second);
        Assert.Equal(first, other);
        Assert.Equal(1, cdn.Count("/stable"));
        Assert.Equal(2, cdn.Count("/manifest.json"));
        Assert.Equal("1.2.3", recovered.Version);
    }

    [Fact]
    public async Task concrete_version_with_no_cache_and_a_failed_download_raises_without_an_offline_warning()
    {
        using var cache = new TempDir();
        var cdn = new FakeCdn().Text($"{BaseUrl}/1.2.3/manifest.json", $"{{\"version\":\"1.2.3\",\"platforms\":{{\"{Platform}\":{{\"checksum\":\"{Checksum}\",\"size\":1}}}}}}");
        var binary = NewBinary(cache, cdn, baseUrl: BaseUrl);
        var sandbox = LinuxSandbox();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => binary.EnsureInstalledAsync(sandbox, "1.2.3"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Empty(sandbox.Files);
        Assert.DoesNotContain(ProviderLogger.Warnings, w => w.Contains("WITHOUT checksum verification", StringComparison.Ordinal) && w.Contains(cache.Path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2.1.236\n")]
    [InlineData("2.1.236\n-linux-x64; rm -rf /")]
    [InlineData("$(curl evil)")]
    [InlineData("")]
    public async Task a_pointer_that_is_not_a_version_number_is_rejected_before_the_manifest_is_fetched(string pointer)
    {
        using var cache = new TempDir();
        var cdn = Cdn().Text($"{BaseUrl}/stable", pointer);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, cdn).ResolveAsync("stable", Platform));

        Assert.Contains("did not resolve to a version number", ex.Message);
        Assert.Equal([ClaudeCodeBinary.InstallScriptUrl, $"{BaseUrl}/stable"], cdn.Requests);
    }

    [Fact]
    public void temp_files_are_neither_listed_nor_pruned_and_stale_ones_are_removed()
    {
        using var cache = new TempDir();
        Directory.CreateDirectory(cache.Path);
        var now = DateTime.UtcNow;
        foreach (var (name, age) in new[] { ("claude-1.0.0-linux-x64", 3), ("claude-1.0.1-linux-x64", 2), ("claude-1.0.2-linux-x64", 1) })
        {
            var path = Path.Combine(cache.Path, name);
            File.WriteAllBytes(path, [1]);
            File.SetLastAccessTimeUtc(path, now.AddHours(-age));
        }

        var inProgress = Path.Combine(cache.Path, "claude-9.9.9-linux-x64.abc.tmp");
        File.WriteAllBytes(inProgress, [1]);
        File.SetLastAccessTimeUtc(inProgress, now.AddDays(-1));
        var stale = Path.Combine(cache.Path, "claude-8.8.8-linux-x64.def.tmp");
        File.WriteAllBytes(stale, [1]);
        File.SetLastWriteTimeUtc(stale, now.AddHours(-2));
        var binary = new ClaudeCodeBinary(cache.Path);

        Assert.Equal(3, binary.ListCachedBinaries().Count);
        binary.WriteCachedFile(Binary, binary.CachedBinaryPath("2.0.0", Platform));

        var remaining = new DirectoryInfo(cache.Path).GetFiles().Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["claude-1.0.1-linux-x64", "claude-1.0.2-linux-x64", "claude-2.0.0-linux-x64", "claude-9.9.9-linux-x64.abc.tmp"], remaining);
        Assert.DoesNotContain(binary.ListCachedBinaries(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task a_download_larger_than_the_limit_is_rejected_from_its_content_length()
    {
        using var cache = new TempDir();
        var cdn = Cdn().Route($"{BaseUrl}/1.2.3/{Platform}/claude", () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentLength = ClaudeCodeBinary.MaxDownloadBytes + 1;
            return response;
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary(cache, cdn, baseUrl: BaseUrl).DownloadAsync("1.2.3", Platform));

        Assert.Contains("download limit", ex.Message);
        Assert.Equal(1, cdn.Count("/claude"));
    }
}
