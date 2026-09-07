namespace InspectAzureAI.Eval.Sandbox.Docker;

/// <summary>
/// Port of the CLI wrappers in <c>util/_sandbox/docker/compose.py</c> (<c>compose_up</c>, <c>compose_exec</c>,
/// <c>compose_build</c>, <c>compose_cp</c>, <c>compose_down</c>) against the bare <c>docker</c> CLI: each
/// method builds one argv (never a shell string) and runs it through the injected <see cref="IProcessRunner"/>.
/// Management commands that fail surface as <see cref="SandboxUnavailableException"/> carrying the CLI's stderr;
/// <see cref="ExecAsync"/> returns the raw result because its exit code belongs to the caller's command.
/// </summary>
internal sealed class DockerCli(IProcessRunner? runner = null, string executable = "docker")
{
    /// <summary>The <c>--add-host</c> mapping that makes the host reachable as host.docker.internal on Linux too (Docker Desktop provides it anyway).</summary>
    public const string HostGatewayMapping = "host.docker.internal:host-gateway";

    /// <summary>Host-side guard for quick management commands (a hung daemon must not stall a sample forever).</summary>
    public static readonly TimeSpan ManagementTimeout = TimeSpan.FromMinutes(2);

    private const long ManagementOutputLimit = 1024 * 1024;

    private readonly IProcessRunner _runner = runner ?? new ProcessRunner();

    public string Executable { get; } = executable;

    /// <summary><c>docker run -d --init --name NAME --add-host ... IMAGE CMD...</c>; returns the container id.</summary>
    public async Task<string> RunDetachedAsync(string image, string name, IReadOnlyList<string> command, CancellationToken cancellationToken = default)
    {
        // --init reaps the orphans of exec'd processes (the auto-compose file Python generates sets init: true).
        List<string> args = ["run", "-d", "--init", "--name", name, "--add-host", HostGatewayMapping, image, .. command];
        var result = await RunManagementAsync(args, null, cancellationToken).ConfigureAwait(false);
        return result.StdoutText.Trim();
    }

    /// <summary>
    /// <c>docker exec [-u USER] [-w CWD] [-e K]* [-i] NAME CMD...</c> with <paramref name="input"/> on stdin. Each
    /// variable is named on the command line but valued through the CLI's own environment (a bare <c>-e NAME</c>
    /// forwards it), so a bridge token never appears in the host process list.
    /// </summary>
    public Task<ProcessResult> ExecAsync(
        string container,
        IReadOnlyList<string> cmd,
        ReadOnlyMemory<byte>? input = null,
        string? cwd = null,
        IReadOnlyDictionary<string, string>? env = null,
        string? user = null,
        TimeSpan? hostTimeout = null,
        long? outputLimit = null,
        bool abortOnOutputLimit = false,
        CancellationToken cancellationToken = default)
    {
        List<string> args = ["exec"];
        if (!string.IsNullOrEmpty(user))
        {
            args.Add("-u");
            args.Add(user);
        }

        if (!string.IsNullOrEmpty(cwd))
        {
            args.Add("-w");
            args.Add(cwd);
        }

        if (env is not null)
        {
            foreach (var key in env.Keys)
            {
                args.Add("-e");
                args.Add(key);
            }
        }

        if (input is not null)
        {
            args.Add("-i");
        }

        args.Add(container);
        args.AddRange(cmd);
        var request = new ProcessRequest(Executable, args)
        {
            Input = input,
            Environment = env,
            Timeout = hostTimeout,
            OutputLimit = outputLimit ?? SandboxLimits.MaxExecOutputSize,
            AbortOnOutputLimit = abortOnOutputLimit,
        };
        return _runner.RunAsync(request, cancellationToken);
    }

    /// <summary><c>docker build -t TAG -f DOCKERFILE CONTEXT</c> (no host timeout: builds legitimately take minutes).</summary>
    public Task BuildAsync(string tag, string contextDirectory, string dockerfile, CancellationToken cancellationToken = default) =>
        RunManagementAsync(["build", "-t", tag, "-f", dockerfile, contextDirectory], timeout: null, cancellationToken, outputLimit: 8 * ManagementOutputLimit);

    /// <summary><c>docker image inspect --format {{.Id}} IMAGE</c>: true when the image is present locally.</summary>
    public async Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(Request(["image", "inspect", "--format", "{{.Id}}", image], ManagementTimeout), cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    /// <summary><c>docker pull IMAGE</c> (no host timeout).</summary>
    public Task PullAsync(string image, CancellationToken cancellationToken = default) =>
        RunManagementAsync(["pull", image], timeout: null, cancellationToken);

    /// <summary><c>docker inspect --format FORMAT NAME</c>; returns stdout trimmed.</summary>
    public async Task<string> InspectAsync(string container, string format, CancellationToken cancellationToken = default)
    {
        var result = await RunManagementAsync(["inspect", "--format", format, container], ManagementTimeout, cancellationToken).ConfigureAwait(false);
        return result.StdoutText.Trim();
    }

    /// <summary><c>docker rm -f NAME</c>.</summary>
    public Task RemoveContainerAsync(string container, CancellationToken cancellationToken = default) =>
        RunManagementAsync(["rm", "-f", container], ManagementTimeout, cancellationToken);

    /// <summary><c>docker cp SRC DST</c> (either side may be <c>NAME:PATH</c>).</summary>
    public Task CopyAsync(string source, string destination, CancellationToken cancellationToken = default) =>
        RunManagementAsync(["cp", source, destination], timeout: null, cancellationToken);

    private ProcessRequest Request(IReadOnlyList<string> args, TimeSpan? timeout, long outputLimit = ManagementOutputLimit) =>
        new(Executable, args) { Timeout = timeout, OutputLimit = outputLimit };

    private async Task<ProcessResult> RunManagementAsync(IReadOnlyList<string> args, TimeSpan? timeout, CancellationToken cancellationToken, long outputLimit = ManagementOutputLimit)
    {
        var result = await _runner.RunAsync(Request(args, timeout, outputLimit), cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new SandboxUnavailableException($"docker {args[0]} did not complete within {timeout?.TotalSeconds ?? 0} seconds: {Detail(result)}");
        }

        if (!result.Success)
        {
            throw new SandboxUnavailableException($"docker {args[0]} failed with exit code {result.ExitCode}: {Detail(result)}");
        }

        return result;
    }

    private static string Detail(ProcessResult result)
    {
        const int maxChars = 8 * 1024;
        var text = result.StderrText.Trim();
        if (text.Length == 0)
        {
            text = result.StdoutText.Trim();
        }

        // BuildKit streams its whole progress log to stderr; the failure sits at the end.
        return text.Length > maxChars ? text[^maxChars..] : text;
    }
}
