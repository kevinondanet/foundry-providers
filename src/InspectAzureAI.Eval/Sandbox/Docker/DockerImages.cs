using System.Security.Cryptography;
using System.Text;

namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of the image side of <c>util/_sandbox/docker/docker.py</c> <c>task_init</c> (build the Dockerfile
/// once, pull what is missing) for the config forms <see cref="SandboxSpec.Config"/> allows: null for the
/// default image, a directory or Dockerfile path to build (tagged by content hash so an unchanged context
/// never rebuilds), or an image reference.
/// </summary>
internal static class DockerImages
{
    public const string DefaultImage = "python:3.12-slim-bookworm";

    public const string BuildRepository = "inspect-swe-sandbox";

    // One build/pull in flight per process: concurrent samples of the same task would otherwise race to
    // build the same tag when the runner's task_init was skipped.
    private static readonly SemaphoreSlim EnsureGate = new(1, 1);

    /// <summary>What <paramref name="config"/> denotes: the image to run and, for builds, the context and Dockerfile.</summary>
    public static ImageSource Resolve(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return new ImageSource(DefaultImage);
        }

        if (Directory.Exists(config))
        {
            var context = Path.GetFullPath(config);
            var dockerfile = Path.Combine(context, "Dockerfile");
            if (!File.Exists(dockerfile))
            {
                throw new FileNotFoundException($"Sandbox directory '{config}' contains no Dockerfile.", dockerfile);
            }

            return BuildSource(context, dockerfile);
        }

        if (config.EndsWith("Dockerfile", StringComparison.Ordinal))
        {
            var dockerfile = Path.GetFullPath(config);
            if (!File.Exists(dockerfile))
            {
                throw new FileNotFoundException($"Sandbox Dockerfile '{config}' was not found.", dockerfile);
            }

            return BuildSource(Path.GetDirectoryName(dockerfile)!, dockerfile);
        }

        return new ImageSource(config);
    }

    /// <summary>Resolves <paramref name="config"/> and makes the image available locally (build or pull), returning its reference.</summary>
    public static async Task<string> EnsureAsync(DockerCli cli, string? config, CancellationToken cancellationToken = default)
    {
        var source = Resolve(config);
        await EnsureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await cli.ImageExistsAsync(source.Image, cancellationToken).ConfigureAwait(false))
            {
                return source.Image;
            }

            if (source.IsBuild)
            {
                await cli.BuildAsync(source.Image, source.ContextDirectory!, source.Dockerfile!, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await cli.PullAsync(source.Image, cancellationToken).ConfigureAwait(false);
            }

            return source.Image;
        }
        finally
        {
            EnsureGate.Release();
        }
    }

    /// <summary>
    /// SHA-256 over every file of the build context (sorted relative posix path, then contents), first 12 hex
    /// digits. Files are streamed rather than loaded whole, <c>.git</c> is skipped (never part of an image), and
    /// the result is memoized against a cheap path/length/mtime fingerprint so the per-sample calls of an eval do
    /// not re-read the context. A <c>.dockerignore</c> is not honoured: an ignored file still changes the tag.
    /// </summary>
    public static string ContentHash(string contextDirectory)
    {
        var root = Path.GetFullPath(contextDirectory);
        var files = EnumerateContext(root);
        var fingerprint = Fingerprint(files);
        lock (HashCache)
        {
            if (HashCache.TryGetValue(root, out var cached) && cached.Fingerprint == fingerprint)
            {
                return cached.Tag;
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        var chunk = new byte[64 * 1024];
        foreach (var (relative, full) in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            using var stream = File.OpenRead(full);
            BitConverter.TryWriteBytes(length, stream.Length);
            hash.AppendData(length);
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                hash.AppendData(chunk, 0, read);
            }
        }

        var tag = Convert.ToHexStringLower(hash.GetHashAndReset())[..12];
        lock (HashCache)
        {
            HashCache[root] = (fingerprint, tag);
        }

        return tag;
    }

    private static readonly Dictionary<string, (string Fingerprint, string Tag)> HashCache = new(StringComparer.Ordinal);

    private static List<(string Relative, string Full)> EnumerateContext(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (Relative: Path.GetRelativePath(root, file).Replace('\\', '/'), Full: file))
            .Where(entry => entry.Relative != ".git" && !entry.Relative.StartsWith(".git/", StringComparison.Ordinal))
            .OrderBy(entry => entry.Relative, StringComparer.Ordinal)
            .ToList();

    private static string Fingerprint(List<(string Relative, string Full)> files)
    {
        var sb = new StringBuilder();
        foreach (var (relative, full) in files)
        {
            var info = new FileInfo(full);
            sb.Append(relative).Append('\0').Append(info.Length).Append('\0').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }

        return sb.ToString();
    }

    private static ImageSource BuildSource(string context, string dockerfile) =>
        new($"{BuildRepository}:{ContentHash(context)}", context, dockerfile);

    /// <summary>An image reference plus, when it has to be built, its context directory and Dockerfile.</summary>
    public sealed record ImageSource(string Image, string? ContextDirectory = null, string? Dockerfile = null)
    {
        public bool IsBuild => ContextDirectory is not null;
    }
}
