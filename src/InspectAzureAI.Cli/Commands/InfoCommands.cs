using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log.EvalFormat;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Commands;

/// <summary>Port of <c>_cli/info.py</c>: <c>info version</c>, <c>info log-schema</c>, and the hidden <c>info log-file</c> / <c>info log-file-headers</c>.</summary>
internal static class InfoCommands
{
    public static Command Build(CliIo io)
    {
        var command = new Command("info", "Read configuration and log info.");
        command.Subcommands.Add(BuildVersion(io));
        command.Subcommands.Add(BuildLogFile(io));
        var headers = new LogCommands().BuildHeaders(io, "log-file-headers", hidden: true);
        command.Subcommands.Add(headers);
        var schema = LogCommands.BuildSchema(io, "log-schema");
        schema.Hidden = true;
        command.Subcommands.Add(schema);
        return command;
    }

    /// <summary>The version reported by <c>info version</c>: the assembly's informational version without its build metadata.</summary>
    public static string Version()
    {
        var assembly = typeof(InfoCommands).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrEmpty(informational) ? assembly.GetName().Version?.ToString() ?? "0.0.0" : informational;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>The install location reported by <c>info version</c> (Python's package path).</summary>
    public static string InstallPath() => AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');

    private static Command BuildVersion(CliIo io)
    {
        var json = Opt.Flag("--json", "Output version and path info as JSON");
        var command = new Command("version", "Output version and path info.");
        command.Options.Add(json);
        command.SetAction(result =>
        {
            if (result.GetValue(json))
            {
                var node = new JsonObject { ["version"] = Version(), ["path"] = InstallPath() };
                io.Out.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                io.Out.WriteLine($"version: {Version()}");
                io.Out.WriteLine($"path: {InstallPath()}");
            }

            return 0;
        });
        return command;
    }

    private static Command BuildLogFile(CliIo io)
    {
        var path = new Argument<string>("path") { Description = "Log file to print." }.RejectOptionLike();
        var headerOnly = Opt.FlagOrValue("--header-only", "Read and print only the header of the log file (i.e. no samples); a number reads the header only when the file is larger than that many MB.");
        var command = new Command("log-file", "Print log file contents as JSON.") { Hidden = true };
        command.Arguments.Add(path);
        command.Options.Add(headerOnly);
        command.SetAction(result =>
        {
            var file = result.GetValue(path)!;
            if (!File.Exists(file))
            {
                throw new PrerequisiteError($"Log file '{file}' does not exist.");
            }

            var flag = Opt.FlagOrValueOf(result, headerOnly);
            var onlyHeader = false;
            if (flag.Given)
            {
                if (flag.IsBare)
                {
                    onlyHeader = true;
                }
                else if (int.TryParse(flag.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var megabytes))
                {
                    onlyHeader = megabytes == 0 || new FileInfo(file).Length > megabytes * 1024L * 1024L;
                }
                else
                {
                    throw new UsageError($"Invalid value for '--header-only': '{flag.Value}' is not an integer.");
                }
            }

            io.Out.WriteLine(EvalLogFiles.EvalLogJson(EvalLogFiles.ReadEvalLog(file, headerOnly: onlyHeader)));
            return 0;
        });
        return command;
    }
}
