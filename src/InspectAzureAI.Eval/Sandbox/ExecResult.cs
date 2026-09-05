namespace InspectAzureAI.Eval.Sandbox;

/// <summary>Port of <c>util/_subprocess.py</c> <c>ExecResult</c>: the outcome of a sandbox command.</summary>
public sealed record ExecResult(bool Success, int ReturnCode, string Stdout, string Stderr);
