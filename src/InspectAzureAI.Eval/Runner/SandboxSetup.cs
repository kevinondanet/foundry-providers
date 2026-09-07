using System.Globalization;
using System.Text;
using InspectAzureAI.Eval.Dataset;
using InspectAzureAI.Eval.Sandbox;

namespace InspectAzureAI.Eval.Runner;

/// <summary>
/// Port of <c>_eval/task/sandbox.py</c> (<c>resolve_sandbox</c>, <c>resolve_sample_files</c>,
/// <c>read_sandboxenv_file</c>) and <c>util/_sandbox/context.py</c> (<c>init_sandbox_environments_sample</c>,
/// <c>copy_sandbox_environment_files</c>, <c>setup_sandbox_environment</c>): brings a sample's sandbox up
/// with its files copied in and its setup script run.
/// </summary>
internal static class SandboxSetup
{
    /// <summary>Python <c>SANDBOX_SETUP_TIMEOUT</c>, overridable with <c>INSPECT_SANDBOX_SETUP_TIMEOUT</c>.</summary>
    private const int DefaultSetupTimeoutSeconds = 300;

    private static readonly TimeSpan HousekeepingTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Port of <c>resolve_sandbox</c>: the task's sandbox type wins; the sample's config wins over the task's
    /// only when the types match; without a task sandbox the sample's own spec applies.
    /// </summary>
    public static SandboxSpec? ResolveSpec(SandboxSpec? taskSandbox, Sample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (taskSandbox is null)
        {
            return sample.Sandbox;
        }

        var config = sample.Sandbox is { Config: not null } own && string.Equals(own.Type, taskSandbox.Type, StringComparison.OrdinalIgnoreCase)
            ? own.Config
            : taskSandbox.Config;
        return new SandboxSpec(taskSandbox.Type, config);
    }

    /// <summary>Port of <c>init_sandbox_environments_sample</c>: environments, then files, then setup; the environments are cleaned up when either step fails.</summary>
    public static async Task<SandboxEnvironments> InitAsync(string taskName, SandboxSpec spec, Sample sample, CancellationToken cancellationToken)
    {
        var provider = SandboxRegistry.Get(spec.Type);
        var files = ReadFiles(sample.Files);
        var setup = sample.Setup is { } script ? ReadSetup(script) : null;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in sample.Metadata ?? new Dictionary<string, object?>())
        {
            metadata[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }

        metadata["__sample_id__"] = Convert.ToString(sample.Id, CultureInfo.InvariantCulture) ?? "";

        var environments = await provider.SampleInitAsync(taskName, spec.Config, metadata, cancellationToken).ConfigureAwait(false);
        if (environments.Environments.Count == 0)
        {
            throw new InvalidOperationException($"No environments returned from the '{spec.Type}' sandbox provider.");
        }

        try
        {
            await CopyFilesAsync(files, environments, cancellationToken).ConfigureAwait(false);
            if (setup is not null)
            {
                await RunSetupAsync(setup, environments.Default, cancellationToken).ConfigureAwait(false);
            }

            return environments;
        }
        catch
        {
            if (environments.Cleanup is { } cleanup)
            {
                await cleanup(true).ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>
    /// Port of <c>read_sandboxenv_file</c>: a data URI is decoded, an existing file is read, anything else
    /// (including the empty string, which fsspec would resolve to the cwd) is the literal contents.
    /// </summary>
    public static byte[] ReadSandboxFile(string contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        if (IsDataUri(contents))
        {
            return DecodeDataUri(contents);
        }

        if (contents.Length > 0 && File.Exists(contents))
        {
            return File.ReadAllBytes(contents);
        }

        return Encoding.UTF8.GetBytes(contents);
    }

    /// <summary>Port of <c>resolve_sample_files</c> + <c>read_sandboxenv_file</c>: a directory value expands to its files (recursively) under the key.</summary>
    public static Dictionary<string, byte[]> ReadFiles(IReadOnlyDictionary<string, string>? files)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (files is null)
        {
            return result;
        }

        foreach (var (key, value) in files)
        {
            if (value.Length > 0 && !IsDataUri(value) && Directory.Exists(value))
            {
                foreach (var file in Directory.EnumerateFiles(value, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(value, file).Replace('\\', '/');
                    result[$"{key.TrimEnd('/')}/{relative}"] = File.ReadAllBytes(file);
                }
            }
            else
            {
                result[key] = ReadSandboxFile(value);
            }
        }

        return result;
    }

    /// <summary>Port of the setup read in <c>sandboxenv_context</c>: a bash shebang is prepended when the script has none.</summary>
    public static byte[] ReadSetup(string setup)
    {
        var text = Encoding.UTF8.GetString(ReadSandboxFile(setup));
        if (!text.TrimStart().StartsWith("#!", StringComparison.Ordinal))
        {
            text = $"#!/usr/bin/env bash\n\n{text}";
        }

        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Port of <c>copy_sandbox_environment_files</c>: an <c>envname:path</c> key targets that environment, others the default.</summary>
    private static async Task CopyFilesAsync(IReadOnlyDictionary<string, byte[]> files, SandboxEnvironments environments, CancellationToken cancellationToken)
    {
        foreach (var (file, contents) in files)
        {
            var target = environments.Default;
            var path = file;
            var separator = file.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0)
            {
                var envName = file[..separator];
                path = file[(separator + 1)..];
                target = environments.Environments.TryGetValue(envName, out var named)
                    ? named
                    : throw new InvalidOperationException(
                        $"Environment referenced in sample file not found: '{envName}:{path}'. "
                        + "Note that ':' can be optionally used to specify an explicit environment name for sample files (e.g. 'envname:file') so cannot be used as a character within filenames.");
            }

            await target.WriteFileAsync(path, contents, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Port of <c>setup_sandbox_environment</c>: the script is written to <c>/tmp</c>, made executable, run once (never retried) and removed.</summary>
    private static async Task RunSetupAsync(byte[] setup, ISandboxEnvironment environment, CancellationToken cancellationToken)
    {
        var setupFile = $"/tmp/{Guid.NewGuid():N}";
        await environment.WriteFileAsync(setupFile, setup, cancellationToken).ConfigureAwait(false);
        try
        {
            await environment.ExecAsync(["chmod", "+x", setupFile], timeout: HousekeepingTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = await environment.ExecAsync(["env", setupFile], timeout: SetupTimeout(), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidOperationException($"Failed to execute setup script for sample: {result.Stderr}");
            }

            await environment.ExecAsync(["rm", setupFile], timeout: HousekeepingTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException("Timed out executing setup command in sandbox", ex);
        }
    }

    private static TimeSpan SetupTimeout()
    {
        var configured = Environment.GetEnvironmentVariable("INSPECT_SANDBOX_SETUP_TIMEOUT");
        return TimeSpan.FromSeconds(
            int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : DefaultSetupTimeoutSeconds);
    }

    private static bool IsDataUri(string value) => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Port of <c>data_uri_to_base64</c> + <c>b64decode</c>; a non-base64 data URI carries percent-encoded text.</summary>
    private static byte[] DecodeDataUri(string uri)
    {
        var comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            throw new FormatException($"Invalid data URI (no ',' separating the header from the data): '{Truncate(uri)}'.");
        }

        var header = uri[..comma];
        var data = uri[(comma + 1)..];
        return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(data)
            : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
    }

    private static string Truncate(string value) => value.Length <= 64 ? value : value[..64] + "...";
}
