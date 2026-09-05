using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Commands;

/// <summary>
/// Port of <c>_cli/log.py</c>: <c>log list</c>, <c>log dump</c>, <c>log headers</c>, <c>log convert</c> and
/// <c>log schema</c> over the eval-format port (<see cref="EvalLogFiles"/>), reading <c>.eval</c> and <c>.json</c> logs alike.
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
            var wanted = result.GetValue(status)?.ToLowerInvariant();
            var logs = EvalLogFiles.ListEvalLogs(
                values.LogDir,
                filter: wanted is null ? null : log => log.Status.ToString().Equals(wanted, StringComparison.OrdinalIgnoreCase),
                recursive: !result.GetValue(noRecursive));
            var absolutePaths = result.GetValue(absolute);
            var named = logs.Select(log => log with { Name = absolutePaths ? Path.GetFullPath(log.Name) : Path.GetRelativePath(Directory.GetCurrentDirectory(), log.Name) }).ToList();
            if (result.GetValue(json))
            {
                io.Out.WriteLine(new JsonArray(named.Select(LogInfoJson).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                foreach (var log in named)
                {
                    io.Out.WriteLine(log.Name);
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
            var log = EvalLogFiles.ReadEvalLog(file, headerOnly: result.GetValue(headerOnly), resolveAttachments: ResolveAttachmentsOf(Opt.FlagOrValueOf(result, resolveAttachments)));
            io.Out.WriteLine(EvalLogFiles.EvalLogJson(log));
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

            io.Out.WriteLine(HeadersJson(paths));
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

        command.SetAction(result =>
        {
            var streaming = Opt.FlagOrValueOf(result, stream);
            if (streaming.Given && Args.CliArgs.IntOrBoolValue(streaming.Value, 1, 0, isOneTrue: false) != 0)
            {
                throw new PrerequisiteError("--stream is not supported by this port: each log is read into memory and written whole.");
            }

            Convert(result.GetValue(path)!, result.GetValue(to)!.ToLowerInvariant(), result.GetValue(outputDir)!, result.GetValue(overwrite), ResolveAttachmentsOf(Opt.FlagOrValueOf(result, resolveAttachments)), io);
            return 0;
        });
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

    /// <summary>Port of <c>headers()</c>: the headers as a JSON list in the log's own JSON format.</summary>
    public static string HeadersJson(IEnumerable<string> files)
    {
        var headers = EvalLogFiles.ReadEvalLogHeaders(files);
        var array = new JsonArray(headers.Select(header => JsonNode.Parse(EvalLogFiles.EvalLogJson(header))).ToArray());
        return array.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Port of <c>log/_convert.py</c> <c>convert_eval_logs</c> for local files: a file, or every log under a directory (mirroring its layout), rewritten in the target format.</summary>
    public static void Convert(string path, string to, string outputDir, bool overwrite, ResolveAttachments resolveAttachments, CliIo io)
    {
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
        {
            throw new PrerequisiteError($"Error: path '{path}' does not exist.");
        }

        outputDir = outputDir.TrimEnd('/', '\\');
        Directory.CreateDirectory(outputDir);
        var format = LogFormats.Parse(to);
        if (!isDirectory)
        {
            ConvertFile(path, outputDir, Path.GetFileNameWithoutExtension(path), format, overwrite, resolveAttachments);
            return;
        }

        var logs = EvalLogFiles.ListEvalLogs(path, recursive: true);
        io.Out.WriteLine("Converting log files...");
        foreach (var log in logs)
        {
            var relative = Path.GetRelativePath(path, log.Name).Replace('\\', '/');
            var relativeStem = Path.ChangeExtension(relative, null);
            var targetDir = Path.Combine(outputDir, Path.GetDirectoryName(relativeStem) ?? "");
            Directory.CreateDirectory(targetDir);
            ConvertFile(log.Name, outputDir, relativeStem, format, overwrite, resolveAttachments);
        }
    }

    private static void ConvertFile(string inputFile, string outputDir, string outputStem, LogFormat format, bool overwrite, ResolveAttachments resolveAttachments)
    {
        var outputFile = Path.Combine(outputDir, outputStem + format.Extension());
        if (File.Exists(outputFile) && !overwrite)
        {
            throw new PrerequisiteError($"Output file {outputFile} already exists (use --overwrite to overwrite existing files)");
        }

        var log = EvalLogFiles.ReadEvalLog(inputFile, resolveAttachments: resolveAttachments);
        EvalLogFiles.WriteEvalLog(log, outputFile, format);
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

    private static JsonNode LogInfoJson(EvalLogInfo info) => new JsonObject
    {
        ["name"] = info.Name,
        ["type"] = info.Type,
        ["size"] = info.Size,
        ["mtime"] = info.Mtime is { } mtime ? JsonValue.Create(mtime) : null,
        ["task"] = info.Task,
        ["task_id"] = info.TaskId,
        ["suffix"] = info.Suffix,
    };

    private static void RequireFile(string file)
    {
        if (!File.Exists(file))
        {
            throw new PrerequisiteError($"Log file '{file}' does not exist.");
        }
    }
}
