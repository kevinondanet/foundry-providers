namespace InspectAzureAI.Cli;

/// <summary>The streams a command writes to (Python's <c>print</c> / <c>click.echo</c> targets); tests capture them.</summary>
public sealed class CliIo
{
    public CliIo(TextWriter? output = null, TextWriter? error = null)
    {
        Out = output ?? Console.Out;
        Error = error ?? Console.Error;
    }

    /// <summary>Standard output: results, listings and JSON.</summary>
    public TextWriter Out { get; }

    /// <summary>Standard error: usage errors, failures and diagnostics.</summary>
    public TextWriter Error { get; }
}
