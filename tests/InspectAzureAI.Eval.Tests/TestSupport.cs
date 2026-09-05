using System.Diagnostics;
using Azure.Core;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Sandbox.Local;
using InspectAzureAI.Eval.Testing;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>A fact that runs only when a Docker daemon answers <c>docker version</c> and INSPECT_SWE_SKIP_DOCKER is unset.</summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        if (!DockerProbe.Available)
        {
            Skip = DockerProbe.SkipReason;
        }
    }
}

/// <summary>A fact that runs only when INSPECT_SWE_NETWORK_TESTS=1.</summary>
public sealed class NetworkFactAttribute : FactAttribute
{
    public NetworkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("INSPECT_SWE_NETWORK_TESTS") != "1")
        {
            Skip = "Set INSPECT_SWE_NETWORK_TESTS=1 to run tests that need the network.";
        }
    }
}

internal static class DockerProbe
{
    private static readonly Lazy<(bool Available, string Reason)> Probe = new(Run);

    public static bool Available => Probe.Value.Available;

    public static string SkipReason => Probe.Value.Reason;

    private static (bool, string) Run()
    {
        if (Environment.GetEnvironmentVariable("INSPECT_SWE_SKIP_DOCKER") is { Length: > 0 })
        {
            return (false, "INSPECT_SWE_SKIP_DOCKER is set.");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("docker", "version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return (false, "docker could not be started.");
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill(entireProcessTree: true);
                return (false, "docker version timed out.");
            }

            return process.ExitCode == 0 ? (true, "") : (false, "docker version failed (daemon not running?).");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, $"docker is not available: {ex.Message}");
        }
    }
}

/// <summary>Installs a <see cref="SampleContext"/> around a scripted model (and optionally a local sandbox) for the duration of a test.</summary>
internal sealed class SampleContextScope : IDisposable
{
    private readonly IDisposable _scope;

    public SampleContextScope(ScriptedModelApi? api = null, bool withLocalSandbox = false, Limits? limits = null, ISandboxEnvironment? sandbox = null)
    {
        Api = api ?? new ScriptedModelApi();
        Model = new Model(Api);
        SandboxEnvironments? sandboxes = null;
        if (withLocalSandbox)
        {
            Local = new LocalSandboxEnvironment();
            sandboxes = SandboxEnvironments.Single(Local);
        }
        else if (sandbox is not null)
        {
            sandboxes = SandboxEnvironments.Single(sandbox);
        }

        Context = new SampleContext { ActiveModel = Model, Limits = limits ?? new Limits(), Sandboxes = sandboxes };
        _scope = SampleContext.Begin(Context);
    }

    public ScriptedModelApi Api { get; }

    public Model Model { get; }

    public SampleContext Context { get; }

    public LocalSandboxEnvironment? Local { get; }

    public Transcript Transcript => Context.Transcript;

    public void Dispose()
    {
        _scope.Dispose();
        Local?.Dispose();
    }
}

/// <summary>A token credential returning a fixed token (stands in for DefaultAzureCredential).</summary>
internal sealed class FakeTokenCredential(string token) : TokenCredential
{
    public List<string[]> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Scopes.Add(requestContext.Scopes);
        return new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(GetToken(requestContext, cancellationToken));
}

/// <summary>Sets environment variables for the duration of a test and restores them afterwards.</summary>
internal sealed class EnvVarScope : IDisposable
{
    private readonly Dictionary<string, string?> _saved = new();

    public EnvVarScope Set(string name, string? value)
    {
        _saved.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
