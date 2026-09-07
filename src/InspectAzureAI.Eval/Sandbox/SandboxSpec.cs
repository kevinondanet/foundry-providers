namespace InspectAzureAI.Eval.Sandbox;

/// <summary>
/// Port of <c>util/_sandbox/environment.py</c> <c>SandboxEnvironmentSpec</c>: <see cref="Type"/> is a
/// <see cref="SandboxRegistry"/> key ("docker", "local"); <see cref="Config"/> is provider specific
/// (docker: an image name, a Dockerfile path, or a directory containing a Dockerfile; null = provider default).
/// </summary>
public sealed record SandboxSpec(string Type, string? Config = null);
