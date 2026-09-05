namespace InspectAzureAI.Cli;

/// <summary>Port of <c>click.UsageError</c> / <c>click.BadParameter</c>: a command line the CLI cannot act on (exit code 2).</summary>
public sealed class UsageError(string message) : Exception(message);
