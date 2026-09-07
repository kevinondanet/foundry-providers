using System.Text.Json.Nodes;
using InspectAzureAI.Cli.Registry;
using InspectAzureAI.Eval.Scorers;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Cli.Tests;

/// <summary>Task discovery over this test assembly, task-spec resolution, <c>-T</c> binding, <c>-F</c> filters and the built-in solver/scorer/metric/reducer catalog.</summary>
public class TaskRegistryTests
{
    private static readonly string OwnAssembly = typeof(TestTasks).Assembly.Location;

    [Fact]
    public void discovers_task_methods_in_loaded_assemblies()
    {
        var registry = TaskRegistry.Discover();
        var names = registry.Tasks.Select(task => task.Name).ToList();
        Assert.Contains("hello", names);
        Assert.Contains("parametrized", names);
        Assert.Contains("draft_task", names);
        Assert.Contains("needs_arg", names);
        Assert.Equal(2, names.Count(name => name == "dup"));

        var hello = registry.Resolve("hello");
        Assert.EndsWith("InspectAzureAI.Cli.Tests.dll", hello.File);
        Assert.False(Path.IsPathRooted(hello.File));
        Assert.Equal($"{hello.File}@hello", hello.ToString());
        Assert.Empty(hello.Attribs);

        var parametrized = registry.Resolve("parametrized");
        Assert.Equal(true, parametrized.Attribs["light"]);
        Assert.Equal(false, parametrized.Attribs["draft"]);

        Assert.True(Path.IsPathRooted(TaskRegistry.Discover(absolute: true).Resolve("hello").File));
        Assert.Equal(registry.Tasks.Select(task => task.ToString()).Order(StringComparer.Ordinal), registry.Tasks.Select(task => task.ToString()));
    }

    [Fact]
    public void resolves_names_assembly_qualified_names_and_reports_ambiguity()
    {
        var registry = TaskRegistry.Discover();
        Assert.Equal("hello", registry.Resolve("hello").Name);
        Assert.Equal("hello", registry.Resolve("InspectAzureAI.Cli.Tests@hello").Name);
        Assert.Equal("hello", registry.Resolve($"{OwnAssembly}@hello").Name);
        Assert.Equal("hello", registry.Resolve($"{registry.Resolve("hello").File}@hello").Name);

        var ambiguous = Assert.Throws<PrerequisiteError>(() => registry.Resolve("dup"));
        Assert.Contains("ambiguous", ambiguous.Message);
        var missing = Assert.Throws<PrerequisiteError>(() => registry.Resolve("nope"));
        Assert.Contains("Task 'nope' not found", missing.Message);
        Assert.Throws<PrerequisiteError>(() => registry.Resolve("other.dll@hello"));
    }

    [Fact]
    public void creates_tasks_binding_arguments_by_name_and_type()
    {
        var registry = TaskRegistry.Discover();
        var info = registry.Resolve("parametrized");

        var task = TaskRegistry.Create(info, new Dictionary<string, object?>
        {
            ["count"] = 3L,
            ["target"] = "yes",
            ["shuffle"] = true,
            ["threshold"] = 0.75,
            ["tags"] = new List<object?> { "a", "b" },
        });
        Assert.Equal(3, task.Dataset.Count());
        Assert.Equal(3, task.Metadata!["count"]);
        Assert.Equal("yes", task.Metadata["target"]);
        Assert.Equal(true, task.Metadata["shuffle"]);
        Assert.Equal(0.75, task.Metadata["threshold"]);
        Assert.Equal(new[] { "a", "b" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(task.Metadata["tags"]));
        Assert.Equal(3L, task.TaskArgs!["count"]);

        var defaults = TaskRegistry.Create(info);
        Assert.Equal(2, defaults.Metadata!["count"]);
        Assert.Null(defaults.Metadata["tags"]);
        Assert.Null(defaults.TaskArgs);

        var converted = TaskRegistry.Create(info, new Dictionary<string, object?> { ["Count"] = "4", ["tags"] = "solo", ["threshold"] = 1L });
        Assert.Equal(4, converted.Metadata!["count"]);
        Assert.Equal(new[] { "solo" }, Assert.IsAssignableFrom<IReadOnlyList<string>>(converted.Metadata["tags"]));
        Assert.Equal(1.0, converted.Metadata["threshold"]);

        var fromJson = TaskRegistry.Create(info, new Dictionary<string, object?> { ["count"] = JsonValue.Create(5), ["tags"] = JsonNode.Parse("[\"x\"]") });
        Assert.Equal(5, fromJson.Metadata!["count"]);

        var invalid = Assert.Throws<PrerequisiteError>(() => TaskRegistry.Create(info, new Dictionary<string, object?> { ["count"] = "many" }));
        Assert.Contains("'count'", invalid.Message);
        var unknown = Assert.Throws<PrerequisiteError>(() => TaskRegistry.Create(info, new Dictionary<string, object?> { ["bogus"] = 1L }));
        Assert.Contains("bogus", unknown.Message);
        Assert.Contains("count, target, shuffle, threshold, tags", unknown.Message);
        var required = Assert.Throws<PrerequisiteError>(() => TaskRegistry.Create(registry.Resolve("needs_arg")));
        Assert.Contains("Missing required argument 'required'", required.Message);
        Assert.Equal("named", TaskRegistry.Create(registry.Resolve("needs_arg"), new Dictionary<string, object?> { ["required"] = "named" }).Name);
    }

    [Fact]
    public void attrib_filters_follow_list_tasks()
    {
        var registry = TaskRegistry.Discover();
        Assert.Equal(["parametrized"], registry.List(TaskRegistry.AttribFilter(["light=true"])).Select(task => task.Name));
        Assert.Equal(["draft_task"], registry.List(TaskRegistry.AttribFilter(["draft=true"])).Select(task => task.Name));
        Assert.DoesNotContain("draft_task", registry.List(TaskRegistry.AttribFilter(["draft~=true"])).Select(task => task.Name));
        Assert.Contains("hello", registry.List(TaskRegistry.AttribFilter(["draft~=true"])).Select(task => task.Name));
        Assert.Equal(["draft_task"], registry.List(TaskRegistry.AttribFilter(["size=large", "draft=true"])).Select(task => task.Name));
        Assert.Empty(registry.List(TaskRegistry.AttribFilter(["missing=1"])));
        Assert.Equal(registry.Tasks.Count, registry.List(TaskRegistry.AttribFilter(null)).Count);
    }

    [Fact]
    public void loads_assemblies_from_paths()
    {
        var registry = TaskRegistry.Discover([OwnAssembly]);
        Assert.Single(registry.Tasks, task => task.Name == "hello");
        Assert.Throws<PrerequisiteError>(() => TaskRegistry.Discover(["no-such.dll"]));

        var notAssembly = Path.GetTempFileName();
        try
        {
            File.WriteAllText(notAssembly, "not a dll");
            Assert.Throws<PrerequisiteError>(() => TaskRegistry.Discover([notAssembly]));
        }
        finally
        {
            File.Delete(notAssembly);
        }
    }

    [Fact]
    public void catalog_creates_builtins_by_python_name()
    {
        Assert.Equal("includes", Catalog.CreateScorer("includes").Name);
        Assert.Equal("exact_match", Catalog.CreateScorer("exact", new Dictionary<string, object?> { ["ignore_case"] = false }).Name);
        Assert.Equal("match", Catalog.CreateScorer("match", new Dictionary<string, object?> { ["location"] = "begin", ["numeric"] = true }).Name);
        Assert.Equal("pattern", Catalog.CreateScorer("pattern", new Dictionary<string, object?> { ["pattern"] = "\\d+" }).Name);
        Assert.Equal("model_graded_qa", Catalog.CreateScorer("model_graded_qa", new Dictionary<string, object?> { ["model"] = "mockllm/model" }).Name);
        var unknown = Assert.Throws<PrerequisiteError>(() => Catalog.CreateScorer("nope"));
        Assert.Contains("includes", unknown.Message);
        Assert.Throws<PrerequisiteError>(() => Catalog.CreateScorer("includes", new Dictionary<string, object?> { ["bogus"] = 1L }));
        Assert.Contains("model_graded_qa", Catalog.BuiltinNames("scorer"));

        Assert.NotNull(Catalog.CreateSolver("generate"));
        Assert.NotNull(Catalog.CreateSolver("system_message", new Dictionary<string, object?> { ["template"] = "hi" }));
        Assert.NotNull(Catalog.CreateSolver("basic_agent"));
        Assert.Throws<PrerequisiteError>(() => Catalog.CreateSolver("chain"));

        Assert.Equal("accuracy", Catalog.CreateMetric("accuracy").Name);
        Assert.Equal("stderr", Catalog.CreateMetric("stderr").Name);
        Assert.Throws<PrerequisiteError>(() => Catalog.CreateMetric("nope"));

        Assert.Equal("mean", Reducers.NameOf(Catalog.CreateReducer("mean")));
        Assert.Equal("median", Reducers.NameOf(Catalog.CreateReducer("median")));
        Assert.Equal("at_least_2", Reducers.NameOf(Catalog.CreateReducer("at_least_2")));
        Assert.Equal("pass_at_3", Reducers.NameOf(Catalog.CreateReducer("pass_at_3")));
        Assert.Equal(["mean", "max"], Catalog.CreateReducers(["mean", "max"])!.Select(Reducers.NameOf));
        Assert.Null(Catalog.CreateReducers(null));
        Assert.Throws<PrerequisiteError>(() => Catalog.CreateReducer("nope"));
        Assert.Throws<PrerequisiteError>(() => Catalog.CreateReducer("top_5"));
    }

    [Fact]
    public async Task list_tasks_prints_file_at_name_lines_or_json()
    {
        var output = new StringWriter();
        var code = await InspectCli.RunAsync(["list", "tasks"], new CliIo(output, new StringWriter()));
        Assert.Equal(0, code);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains(lines, line => line.EndsWith("InspectAzureAI.Cli.Tests.dll@hello", StringComparison.Ordinal));
        Assert.Equal(lines.Order(StringComparer.Ordinal), lines);

        output = new StringWriter();
        code = await InspectCli.RunAsync(["list", "tasks", "--json", "-F", "light=true"], new CliIo(output, new StringWriter()));
        Assert.Equal(0, code);
        var array = JsonNode.Parse(output.ToString())!.AsArray();
        var entry = Assert.Single(array)!.AsObject();
        Assert.Equal("parametrized", (string?)entry["name"]);
        Assert.EndsWith("InspectAzureAI.Cli.Tests.dll", (string?)entry["file"]);
        Assert.True((bool?)entry["attribs"]!["light"]);

        output = new StringWriter();
        code = await InspectCli.RunAsync(["list", "tasks", OwnAssembly, "--absolute"], new CliIo(output, new StringWriter()));
        Assert.Equal(0, code);
        Assert.Contains($"{OwnAssembly}@hello", output.ToString());
    }
}
