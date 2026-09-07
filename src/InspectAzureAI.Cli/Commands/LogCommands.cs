using System.CommandLine;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Eval.Log.Tools;
using InspectAzureAI.Provider.Core;
using LogTools = InspectAzureAI.Eval.Log.Tools.LogCommands;

namespace InspectAzureAI.Cli.Commands;

/// <summary>
/// Port of <c>_cli/log.py</c>: <c>log list</c>, <c>log dump</c>, <c>log headers</c>, <c>log convert</c> and
/// <c>log schema</c>. The commands parse their options here and delegate to the library port of the same commands
/// (<see cref="InspectAzureAI.Eval.Log.Tools.LogCommands"/> and <see cref="LogConversion"/>), which read
/// <c>.eval</c> and <c>.json</c> logs alike.
/// </summary>
internal sealed class LogCommands
{
    public static readonly string[] Statuses = ["started", "success", "cancelled", "error"];

    public Command Build(CliIo io)
    {
        var command = new Command("log", "Query, read, and convert logs.\n\nInspect supports two log formats: 'eval' which is a compact, high performance binary format and 'json' which represents logs as JSON. The default format is 'eval'. You can change this by setting the INSPECT_LOG_FORMAT environment variable or using the --log-format command line option.");
        command.Subcommands.Add(BuildList(io, "list", hidden: false));
        command.Subcommands.Add(BuildDump(io));
        command.Subcommands.Add(BuildConvert(io));
        command.Subcommands.Add(BuildHeaders(io, "headers", hidden: true));
        command.Subcommands.Add(BuildSchema(io, "schema"));
        return command;
    }

    public Command BuildList(CliIo io, string name, bool hidden)
    {
        var common = new CommonOptions();
        var status = Opt.Choice("--status", "List only log files with the indicated status.", Statuses, caseInsensitive: true);
        var absolute = Opt.Flag("--absolute", "List absolute paths to log files (defaults to relative to the cwd).");
        var json = Opt.Flag("--json", "Output listing as JSON");
        var noRecursive = Opt.Flag("--no-recursive", "List log files recursively (defaults to True).");
        var command = new Command(name, "List all logs in the log directory.") { Hidden = hidden };
        foreach (var option in new Option[] { status, absolute, json, noRecursive })
        {
            command.Options.Add(option);
        }

        common.AddTo(command);
        command.SetAction(result =>
        {
            var values = common.Process(result);
            var wantedStatus = result.GetValue(status) is { } wanted ? Enum.Parse<EvalStatus>(wanted, ignoreCase: true) : (EvalStatus?)null;
            var absolutePaths = result.GetValue(absolute);
            var recursive = !result.GetValue(noRecursive);
            if (result.GetValue(json))
            {
                io.Out.WriteLine(LogTools.ListLogsJson(values.LogDir, wantedStatus, absolutePaths, recursive));
            }
            else
            {
                var listing = LogTools.ListLogsText(values.LogDir, wantedStatus, absolutePaths, recursive);
                if (listing.Length > 0)
                {
                    io.Out.WriteLine(listing);
                }
            }

            return 0;
        });
        return command;
    }

    public Command BuildDump(CliIo io)
    {
        var path = new Argument<string>("path") { Description = "Log file to print." }.RejectOptionLike();
        var headerOnly = Opt.Flag("--header-only", "Read and print only the header of the log file (i.e. no samples).");
        var resolveAttachments = Opt.FlagOrValue("--resolve-attachments", "Resolve attachments (duplicated content blocks) to their full content. [full|core]");
        var command = new Command("dump", "Print log file contents as JSON.");
        command.Arguments.Add(path);
        command.Options.Add(headerOnly);
        command.Options.Add(resolveAttachments);
        command.SetAction(result =>
        {
            var file = result.GetValue(path)!;
            RequireFile(file);
            io.Out.WriteLine(LogTools.Dump(file, result.GetValue(headerOnly), ResolveAttachmentsOf(Opt.FlagOrValueOf(result, resolveAttachments))));
            return 0;
        });
        return command;
    }

    public Command BuildHeaders(CliIo io, string name, bool hidden)
    {
        var files = new Argument<string[]>("files") { Arity = ArgumentArity.ZeroOrMore, Description = "Log files." }.RejectOptionLike();
        var command = new Command(name, "Print log file headers as JSON.") { Hidden = hidden };
        command.Arguments.Add(files);
        command.SetAction(result =>
        {
            var paths = result.GetValue(files) ?? [];
            foreach (var file in paths)
            {
                RequireFile(file);
            }

            io.Out.WriteLine(LogTools.HeadersJson(paths));
            return 0;
        });
        return command;
    }

    public Command BuildConvert(CliIo io)
    {
        var path = new Argument<string>("path") { Description = "Log file or directory of log files to convert." }.RejectOptionLike();
        var to = Opt.Choice("--to", "Target format to convert to.", ["eval", "json"], caseInsensitive: true);
        to.Required = true;
        var outputDir = Opt.String("--output-dir", "Directory to write converted log files to.");
        outputDir.Required = true;
        var overwrite = Opt.Flag("--overwrite", "Overwrite files in the output directory.");
        var resolveAttachments = Opt.FlagOrValue("--resolve-attachments", "Resolve attachments (duplicated content blocks) to their full content. [full|core]");
        var stream = Opt.FlagOrValue("--stream", "Stream the samples through the conversion process (not supported by this port).");
        var command = new Command("convert", "Convert between log file formats.");
        command.Arguments.Add(path);
        foreach (var option in new Option[] { to, outputDir, overwrite, resolveAttachments, stream })
        {
            command.Options.Add(option);
        }

        command.SetAction((result, cancellationToken) => ConvertAsync(
            result.GetValue(path)!,
            result.GetValue(to)!.ToLowerInvariant(),
            result.GetValue(outputDir)!,
            result.GetValue(overwrite),
            ResolveAttachmentsOf(Opt.FlagOrValueOf(result, resolveAttachments)),
            Opt.FlagOrValueOf(result, stream),
            io,
            cancellationToken));
        return command;
    }

    public static Command BuildSchema(CliIo io, string name)
    {
        var command = new Command(name, "Print JSON schema for log files.");
        command.SetAction(_ =>
        {
            io.Out.WriteLine(LogSchema());
            return 0;
        });
        return command;
    }

    /// <summary>The OpenAPI document Python ships as <c>_view/inspect-openapi.json</c>, embedded in this assembly.</summary>
    public static string LogSchema()
    {
        using var stream = typeof(LogCommands).Assembly.GetManifestResourceStream("inspect-openapi.json")
            ?? throw new InvalidOperationException("The embedded log schema resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd();
    }

    /// <summary>
    /// The <c>convert</c> action over <see cref="LogConversion.ConvertEvalLogsAsync"/>: refuses <c>--stream</c>,
    /// announces a directory conversion as Python does, and reports an existing output file as a prerequisite failure
    /// (exit 2) rather than the library's bare <see cref="IOException"/> (Python: an uncaught <c>FileExistsError</c>).
    /// </summary>
    internal static async Task<int> ConvertAsync(string path, string to, string outputDir, bool overwrite, ResolveAttachments resolveAttachments, FlagValue stream, CliIo io, CancellationToken cancellationToken)
    {
        if (stream.Given && Args.CliArgs.IntOrBoolValue(stream.Value, 1, 0, isOneTrue: false) != 0)
        {
            throw new PrerequisiteError("--stream is not supported by this port: each log is read into memory and written whole.");
        }

        if (Directory.Exists(path))
        {
            io.Out.WriteLine("Converting log files...");
        }

        try
        {
            await LogConversion.ConvertEvalLogsAsync(path, LogFormats.Parse(to), outputDir, overwrite, resolveAttachments, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.GetType() == typeof(IOException))
        {
            throw new PrerequisiteError(ex.Message);
        }

        return 0;
    }

    private static ResolveAttachments ResolveAttachmentsOf(FlagValue value)
    {
        if (!value.Given)
        {
            return ResolveAttachments.None;
        }

        return value.Value switch
        {
            null => ResolveAttachments.Core,
            "core" => ResolveAttachments.Core,
            "full" => ResolveAttachments.Full,
            var other => throw new UsageError($"Expected 'full', or 'core'. Got: {other}"),
        };
    }

    private static void RequireFile(string file)
    {
        if (!File.Exists(file))
        {
            throw new PrerequisiteError($"Log file '{file}' does not exist.");
        }
    }
}
