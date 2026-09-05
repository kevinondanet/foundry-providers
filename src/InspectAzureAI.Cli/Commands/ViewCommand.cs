using System.CommandLine;
using System.CommandLine.Parsing;

namespace InspectAzureAI.Cli.Commands;

/// <summary>
/// The <c>view</c> command. The log viewer (a web app served by <c>_view/server.py</c>) is not part of this port, so
/// <c>inspectai view ...</c> hands its arguments verbatim to Python's <c>inspect view ...</c> when <c>inspect</c> is on
/// <c>PATH</c> (the viewer reads the logs this port writes) and otherwise prints how to install it and exits 2.
/// </summary>
internal static class ViewCommand
{
    public const string PythonExecutable = "inspect";

    public static Command Build(CliIo io, CliServices services)
    {
        var command = new Command("view", "Inspect log viewer (delegates to Python's `inspect view`; arguments such as --log-dir, --port, --host, start, bundle and embed are passed through unchanged).\n\nLearn more about using the log viewer at https://inspect.aisi.org.uk/log-viewer.html.")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        command.SetAction((result, cancellationToken) => RunAsync(result, io, services, cancellationToken));
        return command;
    }

    internal static async Task<int> RunAsync(ParseResult result, CliIo io, CliServices services, CancellationToken cancellationToken)
    {
        var arguments = PassthroughArguments(result);
        var inspect = services.FindOnPath(PythonExecutable);
        if (inspect is null)
        {
            io.Error.WriteLine(MissingViewerMessage(arguments));
            return 2;
        }

        return await services.RunProcessAsync(inspect, ["view", .. arguments], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tokens after <c>view</c>, as typed.</summary>
    internal static IReadOnlyList<string> PassthroughArguments(ParseResult result)
    {
        var tokens = result.Tokens.Select(token => token.Value).ToList();
        var index = tokens.IndexOf("view");
        return index < 0 ? [] : tokens.Skip(index + 1).ToList();
    }

    internal static string MissingViewerMessage(IReadOnlyList<string> arguments)
    {
        var suffix = arguments.Count == 0 ? "" : " " + string.Join(" ", arguments);
        return $"""
            The Inspect log viewer is not part of inspectai; it runs Python's `inspect view`, which was not found on PATH.
            Install it and run the viewer over the same log directory:

              pip install inspect-ai
              inspect view{suffix}

            (inspectai view{suffix} works once `inspect` is on PATH.)
            """;
    }
}
