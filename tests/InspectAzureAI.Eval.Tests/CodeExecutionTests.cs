using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Approval;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Eval.Tools.Builtin;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>
/// The <c>code_execution</c> tool (<c>tool/_tools/_code_execution.py</c>): schema, options, parallel flag and
/// viewer checked against the <c>ToolDef(code_execution())</c> dump of inspect_ai, provider normalization against
/// <c>_normalize_config</c>, and the <c>python()</c> fallback against <see cref="FakeSandboxEnvironment"/>.
/// </summary>
public class CodeExecutionTests
{
    // ToolDef(code_execution()) as dumped by inspect_ai
    private const string PythonDescription =
        "Use the python function to execute Python code.\n\n"
        + "The Python tool executes single-run Python scripts. Important notes:\n"
        + "1. Each execution is independent - no state is preserved between runs\n"
        + "2. You must explicitly use print() statements to see any output\n"
        + "3. Simply writing expressions (like in notebooks) will not display results\n"
        + "4. The script cannot accept interactive input during execution\n"
        + "5. Return statements alone won't produce visible output\n"
        + "6. All variables and imports are cleared between executions\n"
        + "7. Standard output (via print()) is the only way to see results";

    private const string PythonParameters =
        """{"type":"object","properties":{"code":{"type":"string","description":"The python code to execute."}},"required":["code"],"additionalProperties":false}""";

    private const string PythonDefaultOptions =
        """{"__internal_tool_type__":"code_execution","providers":{"openai":{},"anthropic":{},"google":{},"grok":{},"mistral":{},"python":{}}}""";

    private static string Canonical(string json) => JsonNode.Parse(json)!.ToJsonString();

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    private static ToolCall Call(string json, string id = "c1") => new(id, "code_execution", Args(json));

    private static async Task<ChatMessageTool> Execute(ToolDef tool, string json)
    {
        var result = await ToolExecutor.ExecuteToolsAsync([new ChatMessageUser("go"), new ChatMessageAssistant("", toolCalls: [Call(json)])], [tool]);
        return Assert.IsType<ChatMessageTool>(Assert.Single(result.Messages));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Tool definition versus the Python dump
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void tool_info_matches_the_python_dump()
    {
        var tool = BuiltinTools.CodeExecution();
        var info = tool.ToInfo().ToJson();

        Assert.Equal("code_execution", info["name"]!.GetValue<string>());
        Assert.Equal(PythonDescription, info["description"]!.GetValue<string>());
        Assert.Equal(Canonical(PythonParameters), info["parameters"]!.ToJsonString());
        Assert.Equal(Canonical(PythonDefaultOptions), info["options"]!.ToJsonString());
        Assert.False(tool.Parallel);
        Assert.NotNull(tool.Viewer);
    }

    [Theory]
    [InlineData("default", PythonDefaultOptions)]
    [InlineData("disable_openai_anthropic", """{"__internal_tool_type__":"code_execution","providers":{"google":{},"grok":{},"mistral":{},"python":{}}}""")]
    [InlineData("options", """{"__internal_tool_type__":"code_execution","providers":{"openai":{"container":{"type":"auto"}},"anthropic":{},"google":{},"grok":{},"mistral":{},"python":{"timeout":30,"sandbox":"other"}}}""")]
    [InlineData("no_python", """{"__internal_tool_type__":"code_execution","providers":{"openai":{},"anthropic":{},"google":{},"grok":{},"mistral":{}}}""")]
    [InlineData("true_is_a_no_op", PythonDefaultOptions)]
    public void providers_normalize_like_python(string key, string expectedOptions)
    {
        CodeExecutionProviders? providers = key switch
        {
            "default" => null,
            "disable_openai_anthropic" => new CodeExecutionProviders { OpenAI = false, Anthropic = false },
            "options" => new CodeExecutionProviders
            {
                Python = new JsonObject { ["timeout"] = 30, ["sandbox"] = "other" },
                OpenAI = new JsonObject { ["container"] = new JsonObject { ["type"] = "auto" } },
            },
            "no_python" => new CodeExecutionProviders { Python = false },
            "true_is_a_no_op" => new CodeExecutionProviders { Anthropic = true, Python = true, OpenAI = true },
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        var options = BuiltinTools.CodeExecution(providers).Options!;

        Assert.Equal(Canonical(expectedOptions), options.ToJsonString());
    }

    [Fact]
    public void python_options_helper_builds_the_python_dict()
    {
        Assert.Equal("""{"timeout":30,"sandbox":"other"}""", CodeExecutionProviderOption.PythonOptions(TimeSpan.FromSeconds(30), "other").Options!.ToJsonString());
        Assert.Equal("""{"timeout":2.5}""", CodeExecutionProviderOption.PythonOptions(TimeSpan.FromSeconds(2.5)).Options!.ToJsonString());
        Assert.Equal("{}", CodeExecutionProviderOption.PythonOptions().Options!.ToJsonString());
    }

    [Fact]
    public void invalid_python_options_are_rejected_at_construction()
    {
        var badTimeout = new CodeExecutionProviders { Python = new JsonObject { ["timeout"] = "soon" } };
        var badSandbox = new CodeExecutionProviders { Python = new JsonObject { ["sandbox"] = 3 } };

        Assert.Contains("'timeout'", Assert.Throws<ArgumentException>(() => BuiltinTools.CodeExecution(badTimeout)).Message);
        Assert.Contains("'sandbox'", Assert.Throws<ArgumentException>(() => BuiltinTools.CodeExecution(badSandbox)).Message);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The python() fallback in the sandbox
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task fallback_pipes_the_code_to_python3_in_the_sandbox()
    {
        var sandbox = new FakeSandboxEnvironment { OnExec = _ => FakeSandboxEnvironment.Ok("hi\n") };
        using var scope = new SampleContextScope(sandbox: sandbox);

        var message = await Execute(BuiltinTools.CodeExecution(), "{\"code\": \"print('hi')\"}");

        Assert.Null(message.Error);
        Assert.Equal("hi\n", message.Text);
        var call = Assert.Single(sandbox.Calls);
        Assert.Equal(["bash", "--login", "-c", "python3 -"], call.Cmd);
        Assert.Equal("print('hi')", call.Input);
        Assert.Null(call.Timeout);
        Assert.Null(call.User);
    }

    [Fact]
    public async Task fallback_honours_the_python_timeout_and_sandbox_options()
    {
        var main = new FakeSandboxEnvironment { OnExec = _ => FakeSandboxEnvironment.Ok("main\n") };
        var other = new FakeSandboxEnvironment { OnExec = _ => FakeSandboxEnvironment.Ok("other\n") };
        var context = new SampleContext
        {
            ActiveModel = new Model(new ScriptedModelApi()),
            Limits = new Limits(),
            Sandboxes = SandboxEnvironments.Create(
            [
                new KeyValuePair<string, ISandboxEnvironment>("default", main),
                new KeyValuePair<string, ISandboxEnvironment>("other", other),
            ]),
        };
        using var scope = SampleContext.Begin(context);
        var tool = BuiltinTools.CodeExecution(new CodeExecutionProviders
        {
            Python = CodeExecutionProviderOption.PythonOptions(TimeSpan.FromSeconds(30), "other"),
        });

        var result = await tool.Execute(Args("{\"code\": \"print(1)\"}"), CancellationToken.None);

        Assert.Equal("other\n", result.AsText());
        Assert.Empty(main.Calls);
        var call = Assert.Single(other.Calls);
        Assert.Equal(TimeSpan.FromSeconds(30), call.Timeout);
        Assert.Equal("print(1)", call.Input);
    }

    [Fact]
    public async Task fallback_puts_stderr_before_stdout_like_python()
    {
        var sandbox = new FakeSandboxEnvironment { OnExec = _ => FakeSandboxEnvironment.Fail(1, "Traceback: boom", "partial") };
        using var scope = new SampleContextScope(sandbox: sandbox);

        var result = await BuiltinTools.CodeExecution().Execute(Args("{\"code\": \"raise Exception('boom')\"}"), CancellationToken.None);

        Assert.Equal("Traceback: boom\npartial", result.AsText());
    }

    [Fact]
    public async Task disabled_fallback_is_a_fatal_error_not_a_tool_error()
    {
        var sandbox = new FakeSandboxEnvironment();
        using var scope = new SampleContextScope(sandbox: sandbox);
        var tool = BuiltinTools.CodeExecution(new CodeExecutionProviders { Python = false });

        var direct = await Assert.ThrowsAsync<InvalidOperationException>(() => tool.Execute(Args("{\"code\": \"print(1)\"}"), CancellationToken.None));
        Assert.Equal("Fallback for `code_execution()` tool requires that `python` be enabled.", direct.Message);

        // through the executor the RuntimeError propagates (it is not converted to a tool error message)
        var viaExecutor = await Assert.ThrowsAnyAsync<Exception>(() => Execute(tool, "{\"code\": \"print(1)\"}"));
        var inner = viaExecutor is AggregateException aggregate ? aggregate.InnerExceptions[0] : viaExecutor;
        Assert.IsType<InvalidOperationException>(inner);
        Assert.Equal(BuiltinTools.CodeExecutionFallbackDisabled, inner.Message);
        Assert.Empty(sandbox.Calls);
    }

    [Fact]
    public async Task missing_code_is_a_parsing_error()
    {
        var sandbox = new FakeSandboxEnvironment();
        using var scope = new SampleContextScope(sandbox: sandbox);

        var tool = BuiltinTools.CodeExecution();

        // the tool validates its own arguments with the jsonschema wording (validate_tool_input) ...
        var direct = await Assert.ThrowsAsync<ToolParsingError>(() => tool.Execute(Args("{}"), CancellationToken.None));
        Assert.Contains("'code' is a required property", direct.Message);

        // ... and through the executor a missing required argument is a parsing error reported to the model
        // (the executor's own required-parameter check runs first, with Python's tool_params message)
        var message = await Execute(tool, "{}");
        Assert.Equal("parsing", message.Error!.Type);
        Assert.Equal("Required parameter code not provided to tool call.", message.Error.Message);
        Assert.Empty(sandbox.Calls);
    }

    // ---------------------------------------------------------------------------------------------------------
    // The code viewer (code_viewer of _execute.py)
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void viewer_renders_the_code_as_a_python_block_titled_code_execution()
    {
        var viewer = BuiltinTools.CodeExecution().Viewer!;

        var view = viewer(Call("{\"code\": \"  print(1)\\n\"}"));

        Assert.Null(view.Context);
        Assert.Equal("code_execution", view.Call!.Title);
        Assert.Equal("markdown", view.Call.Format);
        Assert.Equal("```python\nprint(1)\n```\n", view.Call.Content);
    }

    [Fact]
    public void viewer_falls_back_to_the_function_name_for_a_falsy_argument()
    {
        var viewer = BuiltinTools.CodeExecution().Viewer!;

        Assert.Equal("```python\ncode_execution\n```\n", viewer(Call("{}")).Call!.Content);
        Assert.Equal("```python\ncode_execution\n```\n", viewer(Call("{\"code\": \"\"}")).Call!.Content);
        Assert.Equal("```python\ncode_execution\n```\n", viewer(Call("{\"code\": null}")).Call!.Content);
    }

    [Fact]
    public void code_viewer_titles_default_to_the_language_and_stringify_non_strings()
    {
        var bash = ToolCallViews.Code("bash", "cmd");

        var view = bash(new ToolCall("c1", "bash", Args("{\"cmd\": \"ls -la \"}")));
        Assert.Equal("bash", view.Call!.Title);
        Assert.Equal("```bash\nls -la\n```\n", view.Call.Content);

        var numeric = bash(new ToolCall("c1", "bash", Args("{\"cmd\": 42}")));
        Assert.Equal("```bash\n42\n```\n", numeric.Call!.Content);
    }
}
