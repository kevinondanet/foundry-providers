using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Util;
using InspectAzureAI.Swe.ClaudeCode;
using InspectAzureAI.Swe.CodexCli;
using InspectAzureAI.Swe.Util;

namespace InspectAzureAI.Swe.Tests;

/// <summary>
/// The Codex release pipeline against a strict fake GitHub API and CDN (every test has its own API base, calls
/// <see cref="CodexCliBinary.ResetForTests"/> and uses a temp cache): asset selection (port of
/// <c>tests/test_codex_agentbinary.py</c>), the verified host cache, the process-wide release memos, pruning, the
/// root argv install in <see cref="CliSandbox"/>, the <c>auto</c>/<c>sandbox</c> fronts and the model catalog.
/// </summary>
public sealed class CodexCliBinaryTests : IDisposable
{
    private const string Version = "0.154.0";

    private const string Platform = "linux-arm64";

    private const string Arch = "aarch64-unknown-linux-musl";

    private static readonly byte[] PackageArchive = Encoding.ASCII.GetBytes("codex package archive bytes");

    private static readonly byte[] SingleArchive = Encoding.ASCII.GetBytes("codex single binary archive bytes");

    private readonly string _apiBase = $"https://api.github.test/{Guid.NewGuid():N}/repos/openai/codex";

    private readonly string _catalogBase = $"https://raw.github.test/{Guid.NewGuid():N}/openai/codex";

    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "inspect-swe-tests", "codex-" + Guid.NewGuid().ToString("N"));

    private readonly StrictHttpHandler _http = new();

    private readonly List<TimeSpan> _delays = [];

    public CodexCliBinaryTests()
    {
        CodexCliBinary.ResetForTests();
        ProviderLogger.Reset();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheDir))
        {
            Directory.Delete(_cacheDir, recursive: true);
        }
    }

    private string LatestUrl => $"{_apiBase}/releases/latest";

    private string TagsUrl(string version = Version) => $"{_apiBase}/releases/tags/rust-v{version}";

    private string CatalogUrl(string version = Version) => $"{_catalogBase}/rust-v{version}/codex-rs/models-manager/models.json";

    private static string PackageUrl(string version = Version) => $"https://github.test/openai/codex/releases/download/rust-v{version}/codex-package-{Arch}.tar.gz";

    private static string SingleUrl(string version = Version) => $"https://github.test/openai/codex/releases/download/rust-v{version}/codex-{Arch}.tar.gz";

    private static string InstallDir(string version = Version) => $"/var/tmp/.5c95f967ca830048/codex-{version}-{Platform}";

    private string CachePath(string version, bool package) =>
        Path.Combine(_cacheDir, package ? $"codex-package-{version}-{Platform}.tar.gz" : $"codex-{version}-{Platform}.tar.gz");

    private CodexCliBinary NewBinary() => new(_cacheDir, _apiBase, _catalogBase, _http, (delay, _) =>
    {
        _delays.Add(delay);
        return Task.CompletedTask;
    });

    private static JsonObject Asset(string name, string digest, string url) =>
        new() { ["name"] = name, ["digest"] = digest, ["browser_download_url"] = url };

    private static string ReleaseJson(string version, bool package = true, bool single = true, string? packageDigest = null)
    {
        var assets = new JsonArray { Asset("codex-x86_64-unknown-linux-musl.tar.gz", "sha256:" + new string('0', 64), "https://github.test/other.tar.gz") };
        if (single)
        {
            assets.Add(Asset($"codex-{Arch}.tar.gz", "sha256:" + CodexCliBinary.Sha256Hex(SingleArchive), SingleUrl(version)));
        }

        if (package)
        {
            assets.Add(Asset($"codex-package-{Arch}.tar.gz", packageDigest ?? "sha256:" + CodexCliBinary.Sha256Hex(PackageArchive), PackageUrl(version)));
        }

        return new JsonObject { ["tag_name"] = $"rust-v{version}", ["assets"] = assets }.ToJsonString();
    }

    private void ServeLatest(string version = Version) =>
        _http.Register(LatestUrl, _ => StrictHttpHandler.Json($$"""{"tag_name":"rust-v{{version}}","name":"{{version}}"}"""));

    private void ServeRelease(string version = Version, bool package = true, bool single = true, string? packageDigest = null)
    {
        _http.Register(TagsUrl(version), _ => StrictHttpHandler.Json(ReleaseJson(version, package, single, packageDigest)));
        _http.Register(PackageUrl(version), _ => StrictHttpHandler.Bytes(PackageArchive));
        _http.Register(SingleUrl(version), _ => StrictHttpHandler.Bytes(SingleArchive));
    }

    private void SeedCache(string version, bool package, string? digest = null, DateTime? accessed = null)
    {
        Directory.CreateDirectory(_cacheDir);
        var data = package ? PackageArchive : SingleArchive;
        var path = CachePath(version, package);
        File.WriteAllBytes(path, data);
        File.WriteAllText(path + ".sha256", (digest ?? CodexCliBinary.Sha256Hex(data)) + "\n");
        if (accessed is { } when)
        {
            File.SetLastAccessTimeUtc(path, when);
        }
    }

    /// <summary>The platform probes run through <c>bash -c</c>, then the root argv install of <paramref name="entrypoint"/>.</summary>
    private static void AssertInstalled(CliSandbox sandbox, string entrypoint, byte[] archive, string version = Version)
    {
        var dir = InstallDir(version);
        var archivePath = $"{dir}/.codex-archive.tar.gz";
        Assert.Equal(["uname -s", "uname -m", SandboxUtil.MuslCheck], sandbox.Calls.Select(CliSandbox.ShellScript).OfType<string>());
        var argv = sandbox.Calls.Where(call => CliSandbox.ShellScript(call) is null).ToList();
        Assert.Equal(
            [$"test -x {entrypoint}", $"mkdir -p {dir}", $"tar -xzf {archivePath} -C {dir}", $"rm -f {archivePath}", $"chmod +x {entrypoint}"],
            argv.Select(call => string.Join(" ", call.Cmd)));
        Assert.All(argv, call => Assert.Equal("root", call.User));
        Assert.Equal(archive, sandbox.Files[archivePath]);
    }

    [Fact]
    public void select_asset_prefers_the_package_archive()
    {
        var release = JsonNode.Parse("""
            {"assets": [
              {"name": "codex-aarch64-unknown-linux-musl.tar.gz", "digest": "sha256:aaa", "browser_download_url": "https://example.com/codex.tar.gz"},
              {"name": "codex-package-aarch64-unknown-linux-musl.tar.gz", "digest": "sha256:bbb", "browser_download_url": "https://example.com/codex-package.tar.gz"}
            ]}
            """)!.AsObject();

        var asset = CodexCliBinary.SelectAsset(release, "0.147.0", "linux-arm64");

        Assert.Equal(new CodexReleaseAsset("0.147.0", "codex-package-aarch64-unknown-linux-musl.tar.gz", "bbb", "https://example.com/codex-package.tar.gz", true), asset);
    }

    [Fact]
    public void select_asset_falls_back_to_the_single_binary()
    {
        var release = JsonNode.Parse("""
            {"assets": [{"name": "codex-aarch64-unknown-linux-musl.tar.gz", "digest": "sha256:aaa", "browser_download_url": "https://example.com/codex.tar.gz"}]}
            """)!.AsObject();

        var asset = CodexCliBinary.SelectAsset(release, "0.130.0", "linux-arm64-musl");

        Assert.False(asset.Package);
        Assert.Equal("aaa", asset.Sha256);
        Assert.EndsWith("codex.tar.gz", asset.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void select_asset_rejects_a_missing_asset_and_a_bad_digest()
    {
        var release = JsonNode.Parse("""
            {"assets": [
              {"name": "codex-package-aarch64-unknown-linux-musl.tar.gz", "digest": "md5:abc", "browser_download_url": "https://example.com/p.tar.gz"},
              {"name": "codex-package-x86_64-unknown-linux-musl.tar.gz", "browser_download_url": "https://example.com/x.tar.gz"}
            ]}
            """)!.AsObject();

        Assert.Equal("Invalid digest format: md5:abc", Assert.Throws<InvalidOperationException>(() => CodexCliBinary.SelectAsset(release, "0.1.0", "linux-arm64")).Message);
        Assert.Equal("Invalid digest format: ", Assert.Throws<InvalidOperationException>(() => CodexCliBinary.SelectAsset(release, "0.1.0", "linux-x64")).Message);
        Assert.Equal(
            "No asset found for platform linux-x64 in version 0.1.0",
            Assert.Throws<InvalidOperationException>(() => CodexCliBinary.SelectAsset(new JsonObject { ["assets"] = new JsonArray() }, "0.1.0", "linux-x64")).Message);
    }

    [Theory]
    [InlineData("linux-x64", "x86_64-unknown-linux-musl")]
    [InlineData("linux-x64-musl", "x86_64-unknown-linux-musl")]
    [InlineData("linux-arm64", "aarch64-unknown-linux-musl")]
    [InlineData("linux-arm64-musl", "aarch64-unknown-linux-musl")]
    public void codex_arch_is_always_the_musl_triple(string platform, string arch)
    {
        Assert.Equal(arch, CodexCliBinary.CodexArch(platform));
        Assert.Equal([($"codex-package-{arch}.tar.gz", true), ($"codex-{arch}.tar.gz", false)], CodexCliBinary.AssetCandidates(platform));
    }

    [Fact]
    public void unsupported_platforms_and_invalid_versions_are_rejected()
    {
        Assert.Equal("Unsupported platform: darwin-arm64", Assert.Throws<PlatformNotSupportedException>(() => CodexCliBinary.CodexArch("darwin-arm64")).Message);
        Assert.True(CodexCliBinary.IsValidVersion("0.154.0"));
        Assert.True(CodexCliBinary.IsValidVersion("0.155.0-alpha.3"));
        Assert.False(CodexCliBinary.IsValidVersion("0.154"));
        Assert.False(CodexCliBinary.IsValidVersion("v0.154.0"));
        Assert.False(CodexCliBinary.IsValidVersion("0.154.0-"));
        Assert.False(CodexCliBinary.IsValidVersion("0.154.0-../../x"));
    }

    [Fact]
    public async Task invalid_pinned_version_throws_before_touching_the_sandbox_or_network()
    {
        var sandbox = new CliSandbox();

        await Assert.ThrowsAsync<ArgumentException>(() => NewBinary().EnsureInstalledAsync(sandbox, "1.2"));

        Assert.Empty(sandbox.Calls);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task pinned_verified_cache_hit_installs_without_any_http_call()
    {
        SeedCache(Version, package: true);
        var sandbox = new CliSandbox();

        var path = await NewBinary().EnsureInstalledAsync(sandbox, Version, user: "agent");

        Assert.Equal($"{InstallDir()}/bin/codex", path);
        Assert.Empty(_http.Requests);
        AssertInstalled(sandbox, path, PackageArchive);
    }

    [Fact]
    public async Task package_cache_is_preferred_over_a_single_binary_cache()
    {
        SeedCache(Version, package: false);
        SeedCache(Version, package: true);

        Assert.Equal($"{InstallDir()}/bin/codex", await NewBinary().EnsureInstalledAsync(new CliSandbox(), Version));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task single_binary_archive_installs_its_member_as_the_entrypoint()
    {
        SeedCache(Version, package: false);
        var sandbox = new CliSandbox();

        var path = await NewBinary().EnsureInstalledAsync(sandbox, Version);

        Assert.Equal($"{InstallDir()}/codex-{Arch}", path);
        AssertInstalled(sandbox, path, SingleArchive);
    }

    [Fact]
    public async Task an_installed_entrypoint_is_reused_without_extracting()
    {
        SeedCache(Version, package: true);
        var sandbox = new CliSandbox();
        sandbox.MarkInstalled($"{InstallDir()}/bin/codex");

        var path = await NewBinary().EnsureInstalledAsync(sandbox, Version);

        Assert.Equal($"{InstallDir()}/bin/codex", path);
        var argv = Assert.Single(sandbox.Calls, call => CliSandbox.ShellScript(call) is null);
        Assert.Equal(["test", "-x", path], argv.Cmd);
        Assert.Empty(sandbox.Files);
    }

    [Fact]
    public async Task stable_with_a_verified_cache_for_the_resolved_version_calls_only_releases_latest()
    {
        ServeLatest();
        SeedCache(Version, package: true);

        var path = await NewBinary().EnsureInstalledAsync(new CliSandbox(), "stable");

        Assert.Equal($"{InstallDir()}/bin/codex", path);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(LatestUrl, request.RequestUri!.AbsoluteUri);
        Assert.Equal("inspect-azureai", request.Headers.UserAgent.ToString());
        Assert.Empty(_http.Unexpected);
    }

    [Fact]
    public async Task two_stable_installs_on_a_cold_cache_make_one_latest_and_one_tags_call()
    {
        ServeLatest();
        ServeRelease();
        var binary = NewBinary();

        var first = await binary.EnsureInstalledAsync(new CliSandbox(), "stable");
        var second = await binary.EnsureInstalledAsync(new CliSandbox(), "latest");
        var third = await binary.EnsureInstalledAsync(new CliSandbox(), "stable");

        Assert.Equal($"{InstallDir()}/bin/codex", first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Equal(1, _http.CountOf(LatestUrl));
        Assert.Equal(1, _http.CountOf(TagsUrl()));
        Assert.Equal(1, _http.CountOf(PackageUrl()));
        Assert.Equal(0, _http.CountOf(SingleUrl()));
        Assert.Empty(_http.Unexpected);
        Assert.Equal(PackageArchive, File.ReadAllBytes(CachePath(Version, package: true)));
        Assert.Equal(CodexCliBinary.Sha256Hex(PackageArchive) + "\n", File.ReadAllText(CachePath(Version, package: true) + ".sha256"));
    }

    [Fact]
    public async Task release_lookups_are_memoized_process_wide_per_api_base()
    {
        ServeRelease();
        await NewBinary().EnsureInstalledAsync(new CliSandbox(), Version);
        Directory.Delete(_cacheDir, recursive: true);

        await NewBinary().EnsureInstalledAsync(new CliSandbox(), Version);

        Assert.Equal(1, _http.CountOf(TagsUrl()));
        Assert.Equal(2, _http.CountOf(PackageUrl()));
    }

    [Fact]
    public async Task a_faulted_tags_lookup_is_not_memoized()
    {
        _http.Register(TagsUrl(), _ => StrictHttpHandler.Status(HttpStatusCode.NotFound));
        var binary = NewBinary();

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => binary.EnsureInstalledAsync(new CliSandbox(), Version));
        ServeRelease();
        var path = await binary.EnsureInstalledAsync(new CliSandbox(), Version);

        Assert.Equal(HttpStatusCode.NotFound, failure.StatusCode);
        Assert.Equal($"{InstallDir()}/bin/codex", path);
        Assert.Equal(2, _http.CountOf(TagsUrl()));
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task a_faulted_latest_lookup_is_retried_then_not_memoized()
    {
        _http.Register(LatestUrl, _ => StrictHttpHandler.Status(HttpStatusCode.ServiceUnavailable));
        var binary = NewBinary();

        await Assert.ThrowsAsync<HttpRequestException>(() => binary.ResolveVersionAsync("stable"));
        ServeLatest("0.155.0");
        var version = await binary.ResolveVersionAsync("latest");

        Assert.Equal("0.155.0", version);
        Assert.Equal(5, _http.CountOf(LatestUrl));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _delays);
        Assert.Equal("0.155.0", await binary.ResolveVersionAsync("stable"));
        Assert.Equal(5, _http.CountOf(LatestUrl));
    }

    [Fact]
    public async Task an_unexpected_latest_tag_is_rejected()
    {
        _http.Register(LatestUrl, _ => StrictHttpHandler.Json("""{"tag_name":"v1.0.0"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary().ResolveVersionAsync("stable"));

        Assert.Equal("Unexpected tag format: v1.0.0", ex.Message);
    }

    [Fact]
    public async Task a_download_checksum_mismatch_throws_and_caches_nothing()
    {
        ServeRelease(packageDigest: "sha256:" + new string('a', 64));

        var ex = await Assert.ThrowsAsync<ChecksumMismatchException>(() => NewBinary().EnsureInstalledAsync(new CliSandbox(), Version));

        Assert.Equal("Checksum verification failed", ex.Message);
        Assert.False(File.Exists(CachePath(Version, package: true)));
        Assert.False(File.Exists(CachePath(Version, package: true) + ".sha256"));
    }

    [Fact]
    public async Task a_cached_archive_that_fails_its_digest_is_discarded_and_downloaded_again()
    {
        SeedCache(Version, package: true, digest: new string('b', 64));
        ServeRelease();

        var path = await NewBinary().EnsureInstalledAsync(new CliSandbox(), Version);

        Assert.Equal($"{InstallDir()}/bin/codex", path);
        Assert.Equal(1, _http.CountOf(PackageUrl()));
        Assert.Equal(CodexCliBinary.Sha256Hex(PackageArchive) + "\n", File.ReadAllText(CachePath(Version, package: true) + ".sha256"));
        Assert.Contains(ProviderLogger.Warnings, warning => warning.Contains("does not match its digest file", StringComparison.Ordinal));
    }

    [Fact]
    public async Task a_downloaded_single_binary_archive_is_cached_verbatim()
    {
        ServeRelease("0.130.0", package: false);
        var sandbox = new CliSandbox();

        var path = await NewBinary().EnsureInstalledAsync(sandbox, "0.130.0");

        Assert.Equal($"{InstallDir("0.130.0")}/codex-{Arch}", path);
        AssertInstalled(sandbox, path, SingleArchive, "0.130.0");
        Assert.Equal(SingleArchive, File.ReadAllBytes(CachePath("0.130.0", package: false)));
        Assert.Equal(0, _http.CountOf(PackageUrl("0.130.0")));
    }

    [Fact]
    public async Task prune_keeps_three_archives_and_skips_models_catalogs()
    {
        var old = DateTime.UtcNow.AddDays(-10);
        SeedCache("0.100.0", package: true, accessed: old.AddDays(-3));
        SeedCache("0.101.0", package: false, accessed: old.AddDays(-2));
        SeedCache("0.102.0", package: true, accessed: old.AddDays(-1));
        var models = Path.Combine(_cacheDir, "codex-0.100.0-models.json");
        File.WriteAllText(models, """{"models":[]}""");
        File.SetLastAccessTimeUtc(models, old.AddDays(-30));
        ServeRelease();

        await NewBinary().EnsureInstalledAsync(new CliSandbox(), Version);

        Assert.Equal(
            [
                "codex-0.100.0-models.json",
                "codex-0.101.0-linux-arm64.tar.gz",
                "codex-0.101.0-linux-arm64.tar.gz.sha256",
                "codex-package-0.102.0-linux-arm64.tar.gz",
                "codex-package-0.102.0-linux-arm64.tar.gz.sha256",
                "codex-package-0.154.0-linux-arm64.tar.gz",
                "codex-package-0.154.0-linux-arm64.tar.gz.sha256",
            ],
            Directory.GetFiles(_cacheDir).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task auto_with_a_which_hit_returns_it_without_network()
    {
        var sandbox = new CliSandbox();
        sandbox.WhichPaths["codex"] = "/usr/local/bin/codex";

        var path = await NewBinary().EnsureInstalledAsync(sandbox, "auto", user: "agent");

        Assert.Equal("/usr/local/bin/codex", path);
        var call = Assert.Single(sandbox.Calls);
        Assert.Equal(["bash", "-c", "which codex"], call.Cmd);
        Assert.Equal("agent", call.User);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task auto_with_the_default_failing_which_continues_as_stable()
    {
        ServeLatest();
        ServeRelease();
        var sandbox = new CliSandbox();

        var path = await NewBinary().EnsureInstalledAsync(sandbox, "auto");

        Assert.Equal($"{InstallDir()}/bin/codex", path);
        Assert.Equal("which codex", CliSandbox.ShellScript(sandbox.Calls[0]));
        Assert.Equal(1, _http.CountOf(LatestUrl));
        Assert.Equal(1, _http.CountOf(TagsUrl()));
        Assert.Empty(_http.Unexpected);
    }

    [Fact]
    public async Task sandbox_version_with_a_missing_binary_throws()
    {
        var sandbox = new CliSandbox();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NewBinary().EnsureInstalledAsync(sandbox, "sandbox"));

        Assert.Equal("unable to locate codex cli in sandbox", ex.Message);
        Assert.Single(sandbox.Calls);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task installed_version_is_parsed_from_version_output()
    {
        var sandbox = new CliSandbox
        {
            OnExec = (call, _) => Task.FromResult<ExecResult?>(call.Cmd.Count == 2 && call.Cmd[0] == "/usr/local/bin/codex" && call.Cmd[1] == "--version"
                ? CliSandbox.Ok("codex-cli 0.154.0\n")
                : null),
        };

        Assert.Equal("0.154.0", await CodexCliBinary.InstalledVersionAsync(sandbox, "/usr/local/bin/codex", "agent"));
        Assert.Equal("agent", Assert.Single(sandbox.Calls).User);
        Assert.Null(await CodexCliBinary.InstalledVersionAsync(new CliSandbox(), "/usr/local/bin/codex", null));
    }

    [Fact]
    public async Task catalog_without_a_version_is_the_bundled_snapshot()
    {
        var catalog = await NewBinary().CatalogAsync(null);

        Assert.True(JsonNode.DeepEquals(CodexCliModelCatalog.Bundled, catalog));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task catalog_is_fetched_once_and_cached()
    {
        const string text = """{"models":[{"slug":"gpt-5.5","priority":0}]}""";
        _http.Register(CatalogUrl(), _ => StrictHttpHandler.Json(text));

        var fetched = await NewBinary().CatalogAsync(Version);
        var cached = await NewBinary().CatalogAsync(Version);

        Assert.Equal(text, fetched.ToJsonString());
        Assert.True(JsonNode.DeepEquals(fetched, cached));
        Assert.Equal(text, File.ReadAllText(Path.Combine(_cacheDir, $"codex-{Version}-models.json")));
        Assert.Equal(1, _http.CountOf(CatalogUrl()));
    }

    [Fact]
    public async Task a_cached_catalog_is_used_without_network()
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllText(Path.Combine(_cacheDir, $"codex-{Version}-models.json"), CodexCliModelCatalog.Bundled.ToJsonString());

        var catalog = await NewBinary().CatalogAsync(Version);

        Assert.True(JsonNode.DeepEquals(CodexCliModelCatalog.Bundled, catalog));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task catalog_fetch_failure_falls_back_to_the_bundled_snapshot()
    {
        var catalog = await NewBinary().CatalogAsync("9.9.9");

        Assert.True(JsonNode.DeepEquals(CodexCliModelCatalog.Bundled, catalog));
        Assert.Equal(CatalogUrl("9.9.9"), Assert.Single(_http.Unexpected).RequestUri!.AbsoluteUri);
        Assert.False(File.Exists(Path.Combine(_cacheDir, "codex-9.9.9-models.json")));
        Assert.Contains(ProviderLogger.Warnings, warning => warning.StartsWith("Unable to fetch codex model catalog for 9.9.9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task a_catalog_without_a_models_array_falls_back_to_the_bundled_snapshot()
    {
        _http.Register(CatalogUrl(), _ => StrictHttpHandler.Json("""{"slugs":[]}"""));

        var catalog = await NewBinary().CatalogAsync(Version);

        Assert.True(JsonNode.DeepEquals(CodexCliModelCatalog.Bundled, catalog));
        Assert.Equal(1, _http.CountOf(CatalogUrl()));
        Assert.False(File.Exists(Path.Combine(_cacheDir, $"codex-{Version}-models.json")));
    }
}
