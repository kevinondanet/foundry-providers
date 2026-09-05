using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Cli.Args;
using InspectAzureAI.Cli.Registry;

namespace InspectAzureAI.Cli.Commands;

/// <summary>Port of <c>_cli/list.py</c>: <c>list tasks</c> over the discovered <c>[Task]</c> methods and the hidden <c>list logs</c> alias of <c>log list</c>.</summary>
internal sealed class ListCommand
{
    public CommonOptions Common { get; } = new();

    public Argument<string[]> Paths { get; } = new Argument<string[]>("paths") { Arity = ArgumentArity.ZeroOrMore, Description = "Assembly (.dll) paths to load tasks from (the loaded assemblies are always scanned)." }.RejectOptionLike();

    public Option<string[]> F { get; } = Opt.Multi("-F", "One or more boolean task filters (e.g. -F light=true or -F draft~=false)");

    public Option<bool> Absolute { get; } = Opt.Flag("--absolute", "List absolute paths to task assemblies (defaults to relative to the cwd).");

    public Option<bool> Json { get; } = Opt.Flag("--json", "Output listing as JSON");

    public Command Build(CliIo io, LogCommands logs)
    {
        var command = new Command("list", "List tasks on the filesystem.");
        var tasks = new Command("tasks", "List tasks in given assemblies.");
        tasks.Arguments.Add(Paths);
        tasks.Options.Add(F);
        tasks.Options.Add(Absolute);
        tasks.Options.Add(Json);
        Common.AddTo(tasks);
        tasks.SetAction(result => RunTasks(result, io));
        command.Subcommands.Add(tasks);
        command.Subcommands.Add(logs.BuildList(io, "logs", hidden: true));
        return command;
    }

    internal int RunTasks(ParseResult result, CliIo io)
    {
        Common.Process(result);
        var absolute = result.GetValue(Absolute);
        var registry = TaskRegistry.Discover(result.GetValue(Paths), absolute);
        var tasks = registry.List(TaskRegistry.AttribFilter(result.GetValue(F)));
        if (result.GetValue(Json))
        {
            var array = new JsonArray(tasks.Select(task => (JsonNode)new JsonObject
            {
                ["file"] = task.File,
                ["name"] = task.Name,
                ["attribs"] = CliArgs.ValueToJson(task.Attribs),
            }).ToArray());
            io.Out.WriteLine(array.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            io.Out.WriteLine(string.Join("\n", tasks.Select(task => task.ToString())));
        }

        return 0;
    }
}
