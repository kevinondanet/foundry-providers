using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Log;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

/// <summary>
/// The built-in tools (<c>tool/_tools/</c>): schemas checked against the Python <c>ToolDef(...)</c> dump in
/// <c>fixtures/tools/tool_info.json</c>, argument validation against jsonschema's messages, the sandbox tools
/// against <see cref="FakeSandboxEnvironment"/>, and the web search providers against a fake HTTP transport.
/// </summary>
public class BuiltinToolsTests
{
    // fixtures are not copied to the output directory, so walk up from the test assembly to the project's fixtures folder
    private static readonly Lazy<JsonObject> PythonToolInfo = new(() =>
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory, "fixtures", "tools", "tool_info.json");
            if (File.Exists(candidate))
            {
                return JsonNode.Parse(File.ReadAllText(candidate))!.AsObject();
            }

            directory = Path.GetDirectoryName(directory);
        }

        throw new FileNotFoundException("No fixtures/tools/tool_info.json above " + AppContext.BaseDirectory);
    });

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static ToolCall Call(string function, string json, string id = "c1") => new(id, function, Args(json));

    private static async Task<ChatMessageTool> Execute(ToolDef tool, string json)
    {
        var result = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("go"), new ChatMessageAssistant("", toolCalls: [Call(tool.Name, json)])], [tool]);
        return Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
    }

    private static async Task<ToolParsingError> ParsingError(ToolDef tool, string json) =>
        await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(Args(json), CancellationToken.None));

    // ---------------------------------------------------------------------------------------------------------
    // Schemas and descriptions versus the Python ToolDef dump
    // ---------------------------------------------------------------------------------------------------------

    private static readonly string[] ToolKeys =
    [
        "think", "think_custom", "web_search_tavily", "web_search_default", "web_search_mixed",
        "read_file", "list_files", "grep", "todo_write", "update_plan", "update_plan_custom",
    ];

    public static TheoryData<string> PythonToolKeys => [.. ToolKeys];

    private static ToolDef PythonTool(string key) => key switch
    {
        "think" => BuiltinTools.Think(),
        "think_custom" => BuiltinTools.Think("Custom think", "Custom thought"),
        "web_search_tavily" => BuiltinTools.WebSearch("tavily"),
        "web_search_default" => BuiltinTools.WebSearch(),
        "web_search_mixed" => BuiltinTools.WebSearch("openai", new WebSearchProviders { ["tavily"] = new JsonObject { ["max_results"] = 5 } }),
        "read_file" => BuiltinTools.ReadFile(),
        "list_files" => BuiltinTools.ListFiles(),
        "grep" => BuiltinTools.Grep(),
        "todo_write" => BuiltinTools.TodoWrite(),
        "update_plan" => BuiltinTools.UpdatePlan(),
        "update_plan_custom" => BuiltinTools.UpdatePlan("Custom plan"),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Theory]
    [MemberData(nameof(PythonToolKeys))]
    public void tool_info_matches_the_python_dump(string key)
    {
        var expected = PythonToolInfo.Value[key]!.AsObject();
        var tool = PythonTool(key);
        var actual = tool.ToInfo().ToJson();

        Assert.Equal(expected["name"]!.GetValue<string>(), actual["name"]!.GetValue<string>());
        Assert.Equal(expected["description"]!.GetValue<string>(), actual["description"]!.GetValue<string>());
        Assert.Equal(expected["parameters"]!.ToJsonString(), actual["parameters"]!.ToJsonString());   // same field order as model_dump
        Assert.Equal(expected["options"]?.ToJsonString(), actual["options"]?.ToJsonString());
        Assert.Equal(expected["parallel"]!.GetValue<bool>(), tool.Parallel);
    }

    [Fact]
    public void fixture_covers_every_built_in_tool()
    {
        Assert.Equal(ToolKeys.Order(), PythonToolInfo.Value.Select(p => p.Key).Order());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Argument validation (validate_tool_input parity)
    // ---------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, string> ValidationMessages => new()
    {
        { "todo_write", "{\"todos\": [{\"content\": \"a\", \"status\": \"done\"}]}", "Found 1 validation errors parsing tool input arguments:\n- 'done' is not one of ['pending', 'in_progress', 'completed']" },
        { "todo_write", "{\"todos\": [{\"content\": \"a\", \"status\": \"pending\", \"extra\": 1}], \"bogus\": 2}", "Found 2 validation errors parsing tool input arguments:\n- Additional properties are not allowed ('extra' was unexpected)\n- Additional properties are not allowed ('bogus' was unexpected)" },
        { "todo_write", "{}", "Found 1 validation errors parsing tool input arguments:\n- 'todos' is a required property" },
        { "todo_write", "{\"todos\": [{\"content\": 5}]}", "Found 2 validation errors parsing tool input arguments:\n- 5 is not of type 'string'\n- 'status' is a required property" },
        { "read_file", "{\"file_path\": \"x\", \"offset\": \"2\"}", "Found 1 validation errors parsing tool input arguments:\n- '2' is not of type 'integer'" },
        { "read_file", "{\"file_path\": \"x\", \"limit\": \"5\"}", "Found 1 validation errors parsing tool input arguments:\n- '5' is not valid under any of the given schemas" },
        { "think", "{}", "Found 1 validation errors parsing tool input arguments:\n- 'thought' is a required property" },
        { "grep", "{\"pattern\": \"a\", \"output_mode\": \"weird\", \"fixed_strings\": 1}", "Found 2 validation errors parsing tool input arguments:\n- 1 is not of type 'boolean'\n- 'weird' is not one of ['content', 'files_with_matches', 'count']" },
    };

    [Theory]
    [MemberData(nameof(ValidationMessages))]
    public async Task invalid_arguments_raise_the_jsonschema_message(string tool, string json, string expected)
    {
        var error = await ParsingError(PythonTool(tool), json);

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task a_parsing_error_is_reported_to_the_model_not_the_sample()
    {
        var message = await Execute(BuiltinTools.TodoWrite(), "{\"todos\": [{\"content\": \"a\", \"status\": \"done\"}]}");

        Assert.Equal("parsing", message.Error!.Type);
        Assert.Equal("Found 1 validation errors parsing tool input arguments:\n- 'done' is not one of ['pending', 'in_progress', 'completed']", message.Error.Message);
    }

    // ---------------------------------------------------------------------------------------------------------
    // think, todo_write, update_plan
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task think_returns_an_empty_string_and_runs_in_parallel()
    {
        var tool = BuiltinTools.Think();

        var result = await tool.Execute(Args("{\"thought\": \"hmm\"}"), CancellationToken.None);

        Assert.Equal("", result.AsText());
        Assert.True(tool.Parallel);
        Assert.Null(tool.Options);
    }

    [Fact]
    public void think_overrides_fall_back_to_the_defaults_when_empty()
    {
        var tool = BuiltinTools.Think("", "");

        Assert.Equal(BuiltinTools.ThinkDescription, tool.Description);
        Assert.Equal(BuiltinTools.ThinkThoughtDescription, tool.Parameters.Properties["thought"].Description);
    }

    [Fact]
    public async Task todo_write_and_update_plan_answer_their_fixed_strings()
    {
        var todo = await BuiltinTools.TodoWrite().Execute(Args("{\"todos\": [{\"content\": \"Step 1\", \"status\": \"completed\"}, {\"content\": \"Step 2\", \"status\": \"in_progress\"}, {\"content\": \"Step 3\", \"status\": \"pending\"}], \"explanation\": \"Making progress\"}"), CancellationToken.None);
        var plan = await BuiltinTools.UpdatePlan().Execute(Args("{\"plan\": [{\"step\": \"Step 1\", \"status\": \"completed\"}, {\"step\": \"Step 2\", \"status\": \"in_progress\"}, {\"step\": \"Step 3\", \"status\": \"pending\"}], \"explanation\": \"Making progress\"}"), CancellationToken.None);
        var nullExplanation = await BuiltinTools.UpdatePlan().Execute(Args("{\"plan\": [], \"explanation\": null}"), CancellationToken.None);

        Assert.Equal("Todo list updated", todo.AsText());
        Assert.Equal("Plan updated", plan.AsText());
        Assert.Equal("Plan updated", nullExplanation.AsText());
    }

    [Fact]
    public async Task update_plan_status_is_free_form_but_todo_write_status_is_an_enum()
    {
        var plan = await BuiltinTools.UpdatePlan().Execute(Args("{\"plan\": [{\"step\": \"s\", \"status\": \"canceled\"}]}"), CancellationToken.None);
        var todo = await ParsingError(BuiltinTools.TodoWrite(), "{\"todos\": [{\"content\": \"s\", \"status\": \"canceled\"}]}");

        Assert.Equal("Plan updated", plan.AsText());
        Assert.Contains("'canceled' is not one of", todo.Message);
    }

    // ---------------------------------------------------------------------------------------------------------
    // read_file, list_files, grep against the fake sandbox
    // ---------------------------------------------------------------------------------------------------------

    private static (SampleContextScope Scope, FakeSandboxEnvironment Sandbox) Sandboxed(Func<IReadOnlyList<string>, ExecResult?>? onExec = null)
    {
        var sandbox = new FakeSandboxEnvironment { OnExec = onExec };
        return (new SampleContextScope(sandbox: sandbox), sandbox);
    }

    [Fact]
    public async Task read_file_runs_awk_with_a_pure_argv_and_strips_the_trailing_newline()
    {
        var (scope, sandbox) = Sandboxed(_ => FakeSandboxEnvironment.Ok("1\tline1\n2\tline2\n"));
        using (scope)
        {
            var result = await BuiltinTools.ReadFile(timeout: TimeSpan.FromSeconds(30), user: "nobody").Execute(Args("{\"file_path\": \"/tmp/test.txt\"}"), CancellationToken.None);

            Assert.Equal("1\tline1\n2\tline2", result.AsText());
            var call = Assert.Single(sandbox.Calls);
            Assert.Equal(["awk", "NR >= 1 { printf \"%d\\t%s\\n\", NR, $0 }", "/tmp/test.txt"], call.Cmd);
            Assert.Equal("nobody", call.User);
            Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
        }
    }

    [Theory]
    [InlineData("{\"file_path\": \"f\", \"offset\": 2}", "NR >= 3 { printf \"%d\\t%s\\n\", NR, $0 }")]
    [InlineData("{\"file_path\": \"f\", \"limit\": 2}", "NR >= 1 && NR <= 2 { printf \"%d\\t%s\\n\", NR, $0 } NR > 2 { exit }")]
    [InlineData("{\"file_path\": \"f\", \"offset\": 1, \"limit\": 2}", "NR >= 2 && NR <= 3 { printf \"%d\\t%s\\n\", NR, $0 } NR > 3 { exit }")]
    [InlineData("{\"file_path\": \"f\", \"offset\": -5, \"limit\": null}", "NR >= 1 { printf \"%d\\t%s\\n\", NR, $0 }")]
    [InlineData("{\"file_path\": \"f\", \"offset\": 2.0}", "NR >= 3 { printf \"%d\\t%s\\n\", NR, $0 }")]
    public async Task read_file_paginates_with_offset_and_limit(string json, string program)
    {
        var (scope, sandbox) = Sandboxed();
        using (scope)
        {
            await BuiltinTools.ReadFile().Execute(Args(json), CancellationToken.None);

            Assert.Equal(program, sandbox.Calls.Single().Cmd[1]);
        }
    }

    [Fact]
    public async Task read_file_prefixes_a_dash_path_so_awk_does_not_parse_an_option()
    {
        var (scope, sandbox) = Sandboxed();
        using (scope)
        {
            await BuiltinTools.ReadFile().Execute(Args("{\"file_path\": \"-f\"}"), CancellationToken.None);

            Assert.Equal("./-f", sandbox.Calls.Single().Cmd[2]);
        }
    }

    [Theory]
    [InlineData("awk: cannot open /tmp/nope.txt (No such file or directory)\n", "File not found: /tmp/nope.txt")]
    [InlineData("awk: fatal: cannot open file `/tmp/nope.txt' for reading: Permission denied", "Permission denied: /tmp/nope.txt")]
    [InlineData("awk: read error (Is a directory)", "Path is a directory, not a file: /tmp/nope.txt")]
    [InlineData("  awk: syntax error  ", "awk: syntax error")]
    [InlineData("", "Error reading: /tmp/nope.txt")]
    public async Task read_file_maps_failures_to_tool_errors(string stderr, string expected)
    {
        var (scope, _) = Sandboxed(_ => FakeSandboxEnvironment.Fail(2, stderr));
        using (scope)
        {
            var error = await Assert.ThrowsAsync<ToolError>(() => BuiltinTools.ReadFile().Execute(Args("{\"file_path\": \"/tmp/nope.txt\"}"), CancellationToken.None));

            Assert.Equal(expected, error.Message);
        }
    }

    [Fact]
    public async Task read_file_not_found_reaches_the_model_as_a_tool_error()
    {
        var (scope, _) = Sandboxed(_ => FakeSandboxEnvironment.Fail(2, "awk: cannot open /tmp/nonexistent.txt (No such file or directory)"));
        using (scope)
        {
            var message = await Execute(BuiltinTools.ReadFile(), "{\"file_path\": \"/tmp/nonexistent.txt\"}");

            Assert.NotNull(message.Error);
            Assert.Contains("not found", message.Error.Message.ToLowerInvariant());
        }
    }

    [Fact]
    public async Task sandbox_tools_without_a_sandbox_are_fatal()
    {
        using var scope = new SampleContextScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => BuiltinTools.ListFiles().Execute(Args("{}"), CancellationToken.None));
    }

    [Fact]
    public async Task list_files_runs_find_and_sorts_the_output()
    {
        var (scope, sandbox) = Sandboxed(_ => FakeSandboxEnvironment.Ok("/tmp/testdir/sub\n/tmp/testdir/b.txt\n/tmp/testdir/a.txt\n/tmp/testdir/sub/c.txt\n"));
        using (scope)
        {
            var result = await BuiltinTools.ListFiles().Execute(Args("{\"path\": \"/tmp/testdir\"}"), CancellationToken.None);

            Assert.Equal("/tmp/testdir/a.txt\n/tmp/testdir/b.txt\n/tmp/testdir/sub\n/tmp/testdir/sub/c.txt", result.AsText());
            Assert.Equal(["find", "--", "/tmp/testdir", "-mindepth", "1", "-print"], sandbox.Calls.Single().Cmd);
        }
    }

    [Fact]
    public async Task list_files_defaults_to_the_working_directory_and_honours_depth()
    {
        var (scope, sandbox) = Sandboxed(_ => FakeSandboxEnvironment.Ok(""));
        using (scope)
        {
            var result = await BuiltinTools.ListFiles().Execute(Args("{\"depth\": 1}"), CancellationToken.None);

            Assert.Equal("No files found in: .", result.AsText());
            Assert.Equal(["find", "--", ".", "-mindepth", "1", "-maxdepth", "1", "-print"], sandbox.Calls.Single().Cmd);
        }
    }

    [Fact]
    public async Task list_files_dash_path_is_an_argument_not_a_predicate()
    {
        var (scope, sandbox) = Sandboxed(_ => FakeSandboxEnvironment.Fail(1, "find: ‘-delete’: No such file or directory"));
        using (scope)
        {
            var error = await Assert.ThrowsAsync<ToolError>(() => BuiltinTools.ListFiles().Execute(Args("{\"path\": \"-delete\"}"), CancellationToken.None));

            Assert.Equal("find: ‘-delete’: No such file or directory", error.Message);
            Assert.Equal("--", sandbox.Calls.Single().Cmd[1]);
        }
    }

    [Fact]
    public async Task list_files_failure_without_stderr_names_the_path()
    {
        var (scope, _) = Sandboxed(_ => FakeSandboxEnvironment.Fail(1));
        using (scope)
        {
            var error = await Assert.ThrowsAsync<ToolError>(() => BuiltinTools.ListFiles().Execute(Args("{\"path\": \"/x\"}"), CancellationToken.None));

            Assert.Equal("Error listing: /x", error.Message);
        }
    }

    [Theory]
    [InlineData("{\"pattern\": \"hello\"}", "grep -rn -- hello .")]
    [InlineData("{\"pattern\": \"world\", \"path\": \"/tmp/testdir\", \"glob\": \"*.py\"}", "grep -rn --include *.py -- world /tmp/testdir")]
    [InlineData("{\"pattern\": \"print('x')\", \"fixed_strings\": true}", "grep -rn -F -- print('x') .")]
    [InlineData("{\"pattern\": \"hello|goodbye\", \"extended_regexp\": true}", "grep -rn -E -- hello|goodbye .")]
    [InlineData("{\"pattern\": \"a\", \"fixed_strings\": true, \"extended_regexp\": true}", "grep -rn -F -E -- a .")]
    [InlineData("{\"pattern\": \"world\", \"output_mode\": \"files_with_matches\"}", "grep -rn -l -- world .")]
    [InlineData("{\"pattern\": \"world\", \"output_mode\": \"count\"}", "grep -rn -c -- world .")]
    [InlineData("{\"pattern\": \"-v\", \"glob\": null}", "grep -rn -- -v .")]
    public async Task grep_builds_the_argv_from_its_options(string json, string argv)
    {
        var (scope, sandbox) = Sandboxed(_ => FakeSandboxEnvironment.Ok("./f:1:x"));
        using (scope)
        {
            await BuiltinTools.Grep().Execute(Args(json), CancellationToken.None);

            Assert.Equal(argv, string.Join(" ", sandbox.Calls.Single().Cmd));
        }
    }

    [Fact]
    public async Task grep_returns_trimmed_content_and_treats_exit_1_as_no_matches()
    {
        var (matches, _) = Sandboxed(_ => FakeSandboxEnvironment.Ok("/tmp/testdir/hello.py:2:    print('hello world')\n"));
        using (matches)
        {
            var result = await BuiltinTools.Grep().Execute(Args("{\"pattern\": \"hello\", \"path\": \"/tmp/testdir\"}"), CancellationToken.None);
            Assert.Equal("/tmp/testdir/hello.py:2:    print('hello world')", result.AsText());
        }

        var (none, _) = Sandboxed(_ => FakeSandboxEnvironment.Fail(1));
        using (none)
        {
            var result = await BuiltinTools.Grep().Execute(Args("{\"pattern\": \"zzz\"}"), CancellationToken.None);
            Assert.Equal("No matches found.", result.AsText());
        }
    }

    [Fact]
    public async Task grep_count_mode_drops_files_without_matches()
    {
        var (some, _) = Sandboxed(_ => FakeSandboxEnvironment.Ok("./a.py:2\n./b.txt:0\n./c.csv:1\n"));
        using (some)
        {
            var result = await BuiltinTools.Grep().Execute(Args("{\"pattern\": \"x\", \"output_mode\": \"count\"}"), CancellationToken.None);
            Assert.Equal("./a.py:2\n./c.csv:1", result.AsText());
        }

        var (zeros, _) = Sandboxed(_ => FakeSandboxEnvironment.Ok("./a.py:0\n./b.txt:0\n"));
        using (zeros)
        {
            var result = await BuiltinTools.Grep().Execute(Args("{\"pattern\": \"x\", \"output_mode\": \"count\"}"), CancellationToken.None);
            Assert.Equal("No matches found.", result.AsText());
        }
    }

    [Fact]
    public async Task grep_failures_other_than_no_match_are_tool_errors()
    {
        var (conflict, _) = Sandboxed(_ => FakeSandboxEnvironment.Fail(2, "grep: conflicting matchers specified\n"));
        using (conflict)
        {
            var error = await Assert.ThrowsAsync<ToolError>(() => BuiltinTools.Grep().Execute(Args("{\"pattern\": \"hello\", \"fixed_strings\": true, \"extended_regexp\": true}"), CancellationToken.None));
            Assert.Equal("grep: conflicting matchers specified", error.Message);
        }

        var (silent, _) = Sandboxed(_ => FakeSandboxEnvironment.Fail(2));
        using (silent)
        {
            var error = await Assert.ThrowsAsync<ToolError>(() => BuiltinTools.Grep().Execute(Args("{\"pattern\": \"hello\"}"), CancellationToken.None));
            Assert.Equal("grep failed", error.Message);
        }
    }

    [Fact]
    public async Task sandbox_tools_honour_cancellation()
    {
        var (scope, _) = Sandboxed();
        using (scope)
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuiltinTools.Grep().Execute(Args("{\"pattern\": \"x\"}"), cts.Token));
        }
    }

    [Fact]
    public async Task in_memory_json_numbers_validate_and_convert_like_parsed_ones()
    {
        var (scope, sandbox) = Sandboxed();
        using (scope)
        {
            await BuiltinTools.ReadFile().Execute(new JsonObject { ["file_path"] = "f", ["offset"] = 2, ["limit"] = 3L }, CancellationToken.None);
            var error = await Assert.ThrowsAsync<ToolParsingError>(() => BuiltinTools.ReadFile().Execute(new JsonObject { ["file_path"] = "f", ["offset"] = 2.5 }, CancellationToken.None));

            Assert.Equal("NR >= 3 && NR <= 5 { printf \"%d\\t%s\\n\", NR, $0 } NR > 5 { exit }", sandbox.Calls.Single().Cmd[1]);
            Assert.Equal("Found 1 validation errors parsing tool input arguments:\n- 2.5 is not of type 'integer'", error.Message);
        }
    }

    [Fact]
    public void python_splitlines_recognises_every_line_boundary()
    {
        Assert.Equal(["a", "b", "c", "d"], BuiltinTools.PythonSplitLines("a\nb\r\nc\rd\n"));
        Assert.Equal(["x", "y"], BuiltinTools.PythonSplitLines("x\u2028y"));
        Assert.Empty(BuiltinTools.PythonSplitLines(""));
    }

    // ---------------------------------------------------------------------------------------------------------
    // web_search provider configuration (_normalize_config, _has_external_provider, _create_external_provider)
    // ---------------------------------------------------------------------------------------------------------

    private static WebSearchProviders Dict(params (string Key, object? Value)[] entries)
    {
        var providers = new WebSearchProviders();
        foreach (var (key, value) in entries)
        {
            providers[key] = value;
        }

        return providers;
    }

    private static string Normalized(params WebSearchProviderSpec[] providers)
    {
        var obj = new JsonObject();
        foreach (var (name, options) in WebSearchProviderConfig.Normalize(providers))
        {
            obj[name] = options.DeepClone();
        }

        return obj.ToJsonString();
    }

    private const string AllInternal = "{\"openai\":{},\"anthropic\":{},\"grok\":{},\"gemini\":{},\"mistral\":{},\"perplexity\":{}}";

    [Fact]
    public void normalize_with_external_providers_returns_only_what_was_specified()
    {
        Assert.Equal("{\"google\":{}}", Normalized("google"));
        Assert.Equal("{\"tavily\":{}}", Normalized("tavily"));
        Assert.Equal("{\"exa\":{}}", Normalized("exa"));
        Assert.Equal("{\"google\":{},\"tavily\":{}}", Normalized("google", "tavily"));
        Assert.Equal("{\"tavily\":{}}", Normalized(Dict(("tavily", true))));
        Assert.Equal("{\"tavily\":{\"max_results\":5}}", Normalized(Dict(("tavily", new JsonObject { ["max_results"] = 5 }))));
        Assert.Equal("{\"tavily\":{}}", Normalized(Dict(("tavily", null))));
        Assert.Equal("{\"tavily\":{\"max_results\":5},\"google\":{}}", Normalized(Dict(("tavily", new JsonObject { ["max_results"] = 5 })), Dict(("google", new JsonObject()))));
        Assert.Equal("{\"google\":{},\"tavily\":{\"max_results\":5}}", Normalized("google", Dict(("tavily", new JsonObject { ["max_results"] = 5 }))));
        Assert.Equal("{\"google\":{},\"tavily\":{},\"openai\":{\"model\":\"gpt-4o\"}}", Normalized("google", Dict(("tavily", null)), Dict(("openai", new JsonObject { ["model"] = "gpt-4o" }))));
        Assert.Equal("{\"openai\":{},\"tavily\":{}}", Normalized("openai", "tavily"));
    }

    [Fact]
    public void normalize_with_internal_only_enables_every_internal_provider()
    {
        Assert.Equal(AllInternal, Normalized());
        Assert.Equal(AllInternal, Normalized("openai"));
        Assert.Equal(AllInternal, Normalized("anthropic"));
        Assert.Equal(AllInternal, Normalized("openai", "anthropic"));
        Assert.Equal(AllInternal.Replace("\"openai\":{}", "\"openai\":{\"model\":\"gpt-4o\"}", StringComparison.Ordinal), Normalized(Dict(("openai", new JsonObject { ["model"] = "gpt-4o" }))));
        Assert.Equal(AllInternal.Replace("\"openai\":{}", "\"openai\":{\"model\":\"gpt-4o\"}", StringComparison.Ordinal), Normalized(Dict(("openai", new JsonObject { ["model"] = "gpt-4o" })), "anthropic"));
    }

    [Fact]
    public void normalize_disables_providers_with_false()
    {
        Assert.Equal(AllInternal.Replace("\"openai\":{},", "", StringComparison.Ordinal), Normalized(Dict(("openai", false))));
        Assert.Equal(AllInternal.Replace("\"openai\":{},\"anthropic\":{},", "", StringComparison.Ordinal), Normalized(Dict(("openai", false)), Dict(("anthropic", false))));
        Assert.Equal("{\"tavily\":{}}", Normalized("tavily", Dict(("openai", false))));
        Assert.Equal("{\"tavily\":{},\"openai\":{}}", Normalized("tavily", "openai", Dict(("anthropic", false))));
    }

    [Fact]
    public void normalize_rejects_unknown_providers_and_bad_values()
    {
        var unknown = Assert.Throws<ArgumentException>(() => WebSearchProviderConfig.Normalize(["invalid_provider"]));
        var unknownKey = Assert.Throws<ArgumentException>(() => WebSearchProviderConfig.Normalize([Dict(("nope", true))]));
        var badValue = Assert.Throws<ArgumentException>(() => WebSearchProviderConfig.Normalize([Dict(("tavily", "yes"))]));

        Assert.StartsWith("Invalid provider: 'invalid_provider'", unknown.Message);
        Assert.StartsWith("Invalid provider: 'nope'", unknownKey.Message);
        Assert.StartsWith("Invalid value for provider 'tavily': yes. Expected a dict, bool, or None", badValue.Message);
    }

    [Fact]
    public void has_external_provider_and_explicit_providers()
    {
        Assert.False(WebSearchProviderConfig.HasExternalProvider([]));
        Assert.False(WebSearchProviderConfig.HasExternalProvider(["openai"]));
        Assert.True(WebSearchProviderConfig.HasExternalProvider(["tavily"]));
        Assert.True(WebSearchProviderConfig.HasExternalProvider([Dict(("exa", null))]));
        Assert.False(WebSearchProviderConfig.HasExternalProvider([Dict(("google", false))]));
        Assert.True(WebSearchProviderConfig.HasExternalProvider(["openai", Dict(("google", new JsonObject()))]));

        Assert.Empty(WebSearchProviderConfig.ExplicitProviders([]));
        Assert.Equal(["perplexity", "tavily"], WebSearchProviderConfig.ExplicitProviders(["tavily", Dict(("perplexity", true))]).Order());
        Assert.Empty(WebSearchProviderConfig.ExplicitProviders(["perplexity", Dict(("perplexity", false))]));
    }

    [Fact]
    public void create_external_provider_prefers_tavily_then_exa_and_reports_the_rest()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "t-key").Set(ExaSearchProvider.EnvKey, "e-key").Set(PerplexitySearchProvider.EnvKey, "p-key");
        var both = WebSearchProviderConfig.Normalize([Dict(("exa", null)), Dict(("tavily", new JsonObject { ["max_results"] = 5 }))]);

        Assert.NotNull(WebSearchProviderConfig.CreateExternalProvider(both));
        Assert.NotNull(WebSearchProviderConfig.CreateExternalProvider(WebSearchProviderConfig.Normalize(["exa"])));
        Assert.NotNull(WebSearchProviderConfig.CreateExternalProvider(WebSearchProviderConfig.Normalize(["perplexity"]), explicitProviders: new HashSet<string> { "perplexity" }));
        Assert.Throws<NotSupportedException>(() => WebSearchProviderConfig.CreateExternalProvider(WebSearchProviderConfig.Normalize(["google"])));
        var none = Assert.Throws<InvalidOperationException>(() => WebSearchProviderConfig.CreateExternalProvider(WebSearchProviderConfig.Normalize([])));
        Assert.StartsWith("No valid provider found.", none.Message);
        Assert.Throws<InvalidOperationException>(() => WebSearchProviderConfig.CreateExternalProvider(WebSearchProviderConfig.Normalize(["perplexity"])));   // perplexity only when explicit
        Assert.Throws<ArgumentException>(() => WebSearchProviderConfig.CreateExternalProvider(WebSearchProviderConfig.Normalize([Dict(("tavily", new JsonObject { ["topic"] = "nonsense" }))])));
        Assert.Throws<ArgumentException>(() => WebSearchProviderConfig.CreateExternalProvider(WebSearchProviderConfig.Normalize([Dict(("exa", new JsonObject { ["model"] = "bogus" }))])));
    }

    [Fact]
    public async Task a_missing_api_key_surfaces_on_the_first_call_not_at_construction()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, null);
        var tool = BuiltinTools.WebSearch("tavily");

        var error = await Assert.ThrowsAsync<PrerequisiteError>(() => tool.Execute(Args("{\"query\": \"q\"}"), CancellationToken.None));

        Assert.StartsWith("TAVILY_API_KEY not set in the environment. Please ensure this variable is defined to use Tavily with the web_search tool.", error.Message);
        Assert.Contains("https://inspect.aisi.org.uk/tools.html#tavily-provider", error.Message);
    }

    // ---------------------------------------------------------------------------------------------------------
    // HTTP providers over a fake transport
    // ---------------------------------------------------------------------------------------------------------

    private const string TavilyResponse = """
        {"query": "test query", "answer": "test answer", "follow_up_questions": null, "images": [],
         "results": [
           {"title": "First Result", "url": "https://example.com/1", "content": "This is the first search result content.", "score": 0.80698997, "raw_content": null},
           {"title": "Second Result", "url": "https://example.com/2", "content": "This is the second search result content.", "score": 0.79901963, "raw_content": null}
         ],
         "response_time": 2.42}
        """;

    private static ContentText SingleText(ToolResult result) => Assert.IsType<ContentText>(Assert.Single(result.Contents!));

    [Fact]
    public async Task tavily_posts_the_query_with_a_bearer_token_and_renders_answer_and_citations()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "dummy-key");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, TavilyResponse));
        var tool = BuiltinTools.WebSearch([Dict(("tavily", new JsonObject { ["max_results"] = 5, ["max_connections"] = 3 }))], handler);

        var result = await tool.Execute(Args("{\"query\": \"test query\"}"), CancellationToken.None);

        var request = Assert.Single(handler.Calls);
        Assert.Equal("POST", request.Method);
        Assert.Equal(TavilySearchProvider.Endpoint, request.Uri);
        Assert.Equal("Bearer dummy-key", request.Headers["Authorization"]);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("{\"query\":\"test query\",\"max_results\":5,\"include_answer\":true}", request.Body);

        var text = SingleText(result);
        Assert.Equal("test answer", text.Text);
        Assert.Equal(
            [new UrlCitation("https://example.com/1") { CitedText = "This is the first search result content.", Title = "First Result" }, new UrlCitation("https://example.com/2") { CitedText = "This is the second search result content.", Title = "Second Result" }],
            text.Citations!.Cast<Citation>());
    }

    [Fact]
    public async Task tavily_without_an_answer_says_so_and_without_anything_yields_the_no_results_message()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "dummy-key");
        var responses = new Queue<string>([
            """{"query": "q", "answer": null, "images": [], "results": [{"title": "T", "url": "https://e.com", "content": "c", "score": 1.0}], "response_time": 0.1}""",
            """{"query": "q", "answer": "", "images": [], "results": [], "response_time": 0.1}""",
        ]);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        var tool = BuiltinTools.WebSearch(["tavily"], handler);

        var noAnswer = await tool.Execute(Args("{\"query\": \"q\"}"), CancellationToken.None);
        var nothing = await tool.Execute(Args("{\"query\": \"q\"}"), CancellationToken.None);

        Assert.Equal("No answer found.", SingleText(noAnswer).Text);
        Assert.Equal(BuiltinTools.WebSearchNoResults, nothing.Text);
    }

    [Theory]
    [InlineData("""{"detail": {"error": "Query is too long. Max query length is 400 characters."}}""")]
    [InlineData("""{"detail": "Query is too long. Max query length is 400 characters."}""")]
    public async Task tavily_query_too_long_is_a_tool_error(string body)
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "dummy-key");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, body));
        var tool = BuiltinTools.WebSearch(["tavily"], handler);

        var error = await Assert.ThrowsAsync<ToolError>(() => tool.Execute(Args("{\"query\": \"" + new string('a', 401) + "\"}"), CancellationToken.None));

        Assert.Equal("Query is too long. Max query length is 400 characters.", error.Message);
        Assert.Single(handler.Calls);   // a 400 is not retried
    }

    [Fact]
    public async Task tavily_other_400s_propagate_as_http_status_errors()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "dummy-key");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"unexpected": "format"}"""));
        var tool = BuiltinTools.WebSearch(["tavily"], handler);

        var error = await Assert.ThrowsAsync<HttpStatusException>(() => tool.Execute(Args("{\"query\": \"q\"}"), CancellationToken.None));

        Assert.Equal(400, error.Status);
        Assert.Equal("""{"unexpected": "format"}""", error.Body);
        Assert.StartsWith("Client error '400 Bad Request' for url 'https://api.tavily.com/search'", error.Message);
    }

    [Fact]
    public async Task tavily_rejects_a_malformed_response_body()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "dummy-key");
        var responses = new Queue<string>(["""{"query": "q", "images": [], "results": [{"title": "T", "url": "https://e.com"}], "response_time": 0.1}""", "not json"]);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        var provider = new TavilySearchProvider(null, handler);

        var missing = await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("q"));
        var garbage = await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("q"));

        Assert.Equal("TavilySearchResult: content: Field required", missing.Message);
        Assert.StartsWith("Tavily returned a response that is not valid JSON", garbage.Message);
    }

    [Fact]
    public async Task http_providers_retry_transient_failures_with_exponential_jitter()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "dummy-key");
        var statuses = new Queue<HttpStatusCode>([HttpStatusCode.InternalServerError, HttpStatusCode.TooManyRequests, HttpStatusCode.OK]);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(statuses.Dequeue(), TavilyResponse));
        var waits = new List<TimeSpan>();
        var provider = new TavilySearchProvider(null, handler) { Delay = (wait, _) => { waits.Add(wait); return Task.CompletedTask; }, Jitter = () => 0.5 };

        var text = await provider.SearchAsync("q");

        Assert.Equal("test answer", text!.Text);
        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal([TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5)], waits);
        Assert.Equal(TimeSpan.FromSeconds(10), provider.RetryWait(6));   // capped at 10s
    }

    [Fact]
    public async Task http_providers_give_up_after_five_attempts_and_never_retry_client_errors()
    {
        using var env = new EnvVarScope().Set(ExaSearchProvider.EnvKey, "dummy-key");
        var failing = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.ServiceUnavailable, "down"));
        var provider = new ExaSearchProvider(null, failing) { Delay = (_, _) => Task.CompletedTask };
        var error = await Assert.ThrowsAsync<HttpStatusException>(() => provider.SearchAsync("q"));
        Assert.Equal(503, error.Status);
        Assert.Equal(BaseHttpProvider.MaxAttempts, failing.Calls.Count);

        var notFound = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.NotFound, "nope"));
        var once = new ExaSearchProvider(null, notFound) { Delay = (_, _) => Task.CompletedTask };
        await Assert.ThrowsAsync<HttpStatusException>(() => once.SearchAsync("q"));
        Assert.Single(notFound.Calls);
    }

    [Fact]
    public async Task http_providers_stop_on_cancellation()
    {
        using var env = new EnvVarScope().Set(TavilySearchProvider.EnvKey, "dummy-key");
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("boom"));
        using var cts = new CancellationTokenSource();
        var provider = new TavilySearchProvider(null, handler) { Delay = async (_, token) => { await cts.CancelAsync(); token.ThrowIfCancellationRequested(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync("q", cts.Token));

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task exa_posts_with_its_api_key_header_and_requests_citation_text_by_default()
    {
        using var env = new EnvVarScope().Set(ExaSearchProvider.EnvKey, "dummy-key");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"answer": "test answer", "citations": [{"url": "https://example.com/1", "title": "First Result", "id": "https://example.com/1", "text": "Page contents."}]}"""));
        var tool = BuiltinTools.WebSearch(["exa"], handler);

        var result = await tool.Execute(Args("{\"query\": \"test query\"}"), CancellationToken.None);

        var request = Assert.Single(handler.Calls);
        Assert.Equal(ExaSearchProvider.Endpoint, request.Uri);
        Assert.Equal("dummy-key", request.Headers["x-api-key"]);
        Assert.False(request.Headers.ContainsKey("Authorization"));
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("{\"query\":\"test query\",\"text\":true}", request.Body);
        var text = SingleText(result);
        Assert.Equal("test answer", text.Text);
        var citation = Assert.IsType<UrlCitation>(Assert.Single(text.Citations!));
        Assert.Equal("Page contents.", citation.CitedText);
        Assert.Equal("First Result", citation.Title);
    }

    [Fact]
    public async Task exa_options_can_opt_out_of_text_and_max_connections_is_not_forwarded()
    {
        using var env = new EnvVarScope().Set(ExaSearchProvider.EnvKey, "dummy-key");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"answer": "test answer", "citations": [{"url": "https://example.com/1", "title": "First Result", "id": "https://example.com/1"}]}"""));
        var optOut = BuiltinTools.WebSearch([Dict(("exa", new JsonObject { ["text"] = false }))], handler);
        var withModel = BuiltinTools.WebSearch([Dict(("exa", new JsonObject { ["model"] = "exa-pro", ["max_connections"] = 5 }))], handler);

        var result = await optOut.Execute(Args("{\"query\": \"test query\"}"), CancellationToken.None);
        await withModel.Execute(Args("{\"query\": \"test query\"}"), CancellationToken.None);

        Assert.Equal("{\"query\":\"test query\",\"text\":false}", handler.Calls[0].Body);
        Assert.Equal("{\"query\":\"test query\",\"model\":\"exa-pro\",\"text\":true}", handler.Calls[1].Body);
        Assert.Null(Assert.IsType<UrlCitation>(Assert.Single(SingleText(result).Citations!)).CitedText);
    }

    [Fact]
    public async Task exa_accepts_minimal_citations_and_reports_empty_answers_as_no_results()
    {
        using var env = new EnvVarScope().Set(ExaSearchProvider.EnvKey, "dummy-key");
        var responses = new Queue<string>(["""{"answer": "a", "citations": [{"url": "https://example.com/1", "title": "First Result"}]}""", """{"answer": ""}""", """{"citations": []}"""]);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, responses.Dequeue()));
        var provider = new ExaSearchProvider(null, handler);

        var minimal = await provider.SearchAsync("q");
        var empty = await provider.SearchAsync("q");
        var invalid = await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("q"));

        Assert.Equal("https://example.com/1", Assert.IsType<UrlCitation>(minimal!.Citations![0]).Url);
        Assert.Null(empty);
        Assert.Equal("ExaSearchResponse: answer: Field required", invalid.Message);
    }

    [Fact]
    public async Task perplexity_fallback_posts_a_chat_completion_and_cites_the_search_results()
    {
        using var env = new EnvVarScope().Set(PerplexitySearchProvider.EnvKey, "pplx-key");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """
            {"id": "x", "model": "sonar", "choices": [{"index": 0, "message": {"role": "assistant", "content": "Paris is the capital."}, "finish_reason": "stop"}],
             "search_results": [{"title": "Paris", "url": "https://en.wikipedia.org/wiki/Paris", "date": "2024-01-01"}, {"title": "no url"}],
             "usage": {"prompt_tokens": 5, "completion_tokens": 7, "total_tokens": 12}}
            """));
        var tool = BuiltinTools.WebSearch([Dict(("perplexity", new JsonObject { ["search_domain_filter"] = new JsonArray("wikipedia.org"), ["max_connections"] = 2 }))], handler);

        var result = await tool.Execute(Args("{\"query\": \"capital of France\"}"), CancellationToken.None);

        var request = Assert.Single(handler.Calls);
        Assert.Equal(PerplexitySearchProvider.Endpoint, request.Uri);
        Assert.Equal("Bearer pplx-key", request.Headers["Authorization"]);
        Assert.Equal("{\"model\":\"sonar\",\"messages\":[{\"role\":\"user\",\"content\":\"capital of France\"}],\"search_domain_filter\":[\"wikipedia.org\"]}", request.Body);
        var text = SingleText(result);
        Assert.Equal("Paris is the capital.", text.Text);
        var citation = Assert.IsType<UrlCitation>(Assert.Single(text.Citations!));
        Assert.Equal("https://en.wikipedia.org/wiki/Paris", citation.Url);
        Assert.Equal("Paris", citation.Title);
        Assert.Null(citation.CitedText);
        Assert.Equal(new JsonObject { [WebSearchProviderConfig.InternalToolType] = "web_search", ["openai"] = new JsonObject(), ["anthropic"] = new JsonObject(), ["grok"] = new JsonObject(), ["gemini"] = new JsonObject(), ["mistral"] = new JsonObject(), ["perplexity"] = new JsonObject { ["search_domain_filter"] = new JsonArray("wikipedia.org"), ["max_connections"] = 2 } }.ToJsonString(), tool.Options!.ToJsonString());
    }

    [Fact]
    public async Task perplexity_fallback_validates_its_own_options_and_handles_empty_answers()
    {
        using var env = new EnvVarScope().Set(PerplexitySearchProvider.EnvKey, "pplx-key");
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(HttpStatusCode.OK, """{"choices": [{"message": {"role": "assistant", "content": ""}}]}"""));
        var provider = new PerplexitySearchProvider(new JsonObject { ["model"] = "sonar-pro" }, handler);

        var empty = await provider.SearchAsync("q");

        Assert.Null(empty);
        Assert.Equal("sonar-pro", JsonNode.Parse(handler.Calls.Single().Body!)!["model"]!.GetValue<string>());
        Assert.Throws<ArgumentException>(() => PerplexitySearchProvider.ValidateOptions(new JsonObject { ["model"] = 5 }));
        Assert.Throws<ArgumentException>(() => PerplexitySearchProvider.ValidateOptions(new JsonObject { ["max_connections"] = "many" }));
    }

    [Fact]
    public async Task named_concurrency_limits_callers_per_key()
    {
        var key = "test_" + Guid.NewGuid().ToString("N");
        using var first = await NamedConcurrency.EnterAsync(key, 1);
        var second = NamedConcurrency.EnterAsync(key, 5);   // the first caller's limit sticks

        Assert.Equal(1, NamedConcurrency.Limit(key));
        Assert.Equal(1, NamedConcurrency.InUse(key));
        Assert.False(second.IsCompleted);
        first.Dispose();
        using var lease = await second;
        Assert.Equal(1, NamedConcurrency.InUse(key));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NamedConcurrency.EnterAsync(key + "x", 0));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Log shape of the new content
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void server_tool_use_and_citations_round_trip_through_the_log_json()
    {
        var toolUse = new ContentToolUse("web_search", "srvtoolu_1", "web_search", "{\"query\": \"q\"}", "[{\"type\": \"web_search_result\"}]") { Error = "max_uses_exceeded" };
        var text = new ContentText("Paris")
        {
            Citations = [new UrlCitation("https://e.com") { CitedText = "Paris is", Title = "T", Internal = new JsonObject { ["encrypted_index"] = "abc" } }],
        };

        var compact = new JsonSerializerOptions(EvalLogWriter.Options) { WriteIndented = false };
        var toolUseJson = JsonSerializer.Serialize<Content>(toolUse, compact);
        var textJson = JsonSerializer.Serialize<Content>(text, compact);

        Assert.Equal("{\"type\":\"tool_use\",\"tool_type\":\"web_search\",\"id\":\"srvtoolu_1\",\"name\":\"web_search\",\"arguments\":\"{\\\"query\\\": \\\"q\\\"}\",\"result\":\"[{\\\"type\\\": \\\"web_search_result\\\"}]\",\"error\":\"max_uses_exceeded\"}", toolUseJson);
        Assert.Equal("{\"type\":\"text\",\"text\":\"Paris\",\"citations\":[{\"type\":\"url\",\"cited_text\":\"Paris is\",\"title\":\"T\",\"internal\":{\"encrypted_index\":\"abc\"},\"url\":\"https://e.com\"}]}", textJson);
        Assert.Equal(toolUse, JsonSerializer.Deserialize<Content>(toolUseJson, EvalLogWriter.Options));
        var readText = Assert.IsType<ContentText>(JsonSerializer.Deserialize<Content>(textJson, EvalLogWriter.Options));
        var citation = Assert.IsType<UrlCitation>(Assert.Single(readText.Citations!));
        Assert.Equal("abc", citation.Internal!["encrypted_index"]!.GetValue<string>());
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Content>("{\"type\":\"tool_use\",\"tool_type\":\"web_search\"}", EvalLogWriter.Options));
    }
}

/// <summary>A snapshot of one request seen by <see cref="FakeHttpHandler"/> (taken at send time; the provider disposes the request afterwards).</summary>
internal sealed record FakeHttpCall(string Method, string Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, string? Body);

/// <summary>An <see cref="HttpMessageHandler"/> answering every request from a responder and recording what was sent.</summary>
internal sealed class FakeHttpHandler(Func<FakeHttpCall, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<FakeHttpCall> Calls { get; } = [];

    public static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var call = new FakeHttpCall(request.Method.Method, request.RequestUri!.ToString(), headers, request.Content?.Headers.ContentType?.MediaType, body);
        Calls.Add(call);
        return responder(call);
    }
}
