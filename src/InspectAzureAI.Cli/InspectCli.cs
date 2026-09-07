using System.CommandLine;
using System.CommandLine.Parsing;
using Azure;
using Azure.Identity;
using InspectAzureAI.Cli.Commands;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Provider.Core;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Cli;

/// <summary>
/// Port of <c>_cli/main.py</c>: the <c>inspectai</c> root command with <c>eval</c>, <c>eval-set</c>, <c>eval-retry</c>,
/// <c>score</c>, <c>list</c>, <c>log</c>, <c>cache</c>, <c>info</c> and <c>view</c>, and the exit-code contract of
/// docs/ARCHITECTURE.md section 8: 0 success; 1 a run whose log is not a success; 2 a usage or prerequisite
/// error; 3 a sign-in, Azure, sandbox or cancellation failure (any other failure is reported as 3 too).
/// </summary>
public static class InspectCli
{
    public const string Name = "inspectai";

    private const string VersionOptionName = "--version";

    /// <summary>Builds the command tree; the commands write to <paramref name="io"/> and use <paramref name="services"/> for processes.</summary>
    public static RootCommand Build(CliIo io, CliServices? services = null)
    {
        ArgumentNullException.ThrowIfNull(io);
        services ??= CliServices.Default;
        var root = new RootCommand("Inspect AI evaluations for Azure AI Foundry (a .NET port of the `inspect` CLI).");
        // Python prints __version__ bare; replace the built-in option (which appends the commit hash) with one this class reads.
        foreach (var builtin in root.Options.Where(option => option is VersionOption).ToList())
        {
            root.Options.Remove(builtin);
        }

        root.Options.Add(new Option<bool>(VersionOptionName) { Description = "Print the Inspect version." });
        var logs = new LogCommands();
        root.Subcommands.Add(CacheCommands.Build(io));
        root.Subcommands.Add(EvalCommands.BuildEval(io));
        root.Subcommands.Add(EvalCommands.BuildEvalSet(io));
        root.Subcommands.Add(EvalCommands.BuildEvalRetry(io));
        root.Subcommands.Add(InfoCommands.Build(io));
        root.Subcommands.Add(new ListCommand().Build(io, logs));
        root.Subcommands.Add(logs.Build(io));
        root.Subcommands.Add(new ScoreCommand().Build(io));
        root.Subcommands.Add(ViewCommand.Build(io, services));
        return root;
    }

    /// <summary>Parses and runs <paramref name="args"/>, returning the process exit code.</summary>
    public static async Task<int> RunAsync(string[] args, CliIo? io = null, CliServices? services = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        io ??= new CliIo();
        var root = Build(io, services);
        var configuration = new InvocationConfiguration { Output = io.Out, Error = io.Error, EnableDefaultExceptionHandler = false };
        if (args.Length == 0)
        {
            // Python: `inspect` alone prints the help and exits 0
            return await root.Parse(["--help"]).InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        var result = root.Parse(args);
        if (result.CommandResult.Command is RootCommand && result.GetValue<bool>(VersionOptionName))
        {
            io.Out.WriteLine(InfoCommands.Version());
            return 0;
        }

        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
            {
                io.Error.WriteLine(error.Message);
            }

            io.Error.WriteLine($"Try '{CommandPath(result)} --help' for help.");
            return 2;
        }

        if (result.Action is null)
        {
            return await root.Parse(["--help"]).InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await result.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (UsageError ex)
        {
            io.Error.WriteLine($"Error: {ex.Message}");
            io.Error.WriteLine($"Try '{CommandPath(result)} --help' for help.");
            return 2;
        }
        catch (PrerequisiteError ex)
        {
            io.Error.WriteLine(ProviderUtil.StripRichMarkup(ex.Message));
            return 2;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            io.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (IsSignInFailure(ex))
        {
            io.Error.WriteLine($"Entra ID sign-in failed: {SignInFailureMessage(ex)}\n\nRun `az login` (or set AZURE_TENANT_ID / AZURE_CLIENT_ID) and retry.");
            return 3;
        }
        catch (RequestFailedException ex)
        {
            io.Error.WriteLine($"Azure request failed (HTTP {ex.Status}): {ex.Message}");
            return 3;
        }
        catch (ServiceResponseException ex)
        {
            io.Error.WriteLine($"Azure response could not be read (retryable): {ex.Message}");
            return 3;
        }
        catch (SandboxUnavailableException ex)
        {
            io.Error.WriteLine($"Sandbox unavailable: {ex.Message}");
            return 3;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            io.Error.WriteLine("Cancelled.");
            return 3;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            io.Error.WriteLine($"Error: {ex.Message}");
            return 3;
        }
    }

    internal static bool IsSignInFailure(Exception ex) =>
        ex is CredentialUnavailableException or AuthenticationFailedException
        || (ex is AggregateException aggregate && aggregate.InnerExceptions.Any(IsSignInFailure));

    internal static string SignInFailureMessage(Exception ex) =>
        ex is AggregateException { InnerExceptions.Count: > 0 } aggregate ? SignInFailureMessage(aggregate.InnerExceptions[^1]) : ex.Message;

    /// <summary>The command path of a parse result (<c>inspectai log dump</c>) for the "Try --help" hint.</summary>
    internal static string CommandPath(ParseResult result)
    {
        var parts = new List<string>();
        for (var command = result.CommandResult; command is not null; command = command.Parent as CommandResult)
        {
            parts.Insert(0, command.Command is RootCommand ? Name : command.Command.Name);
        }

        return string.Join(" ", parts);
    }
}
