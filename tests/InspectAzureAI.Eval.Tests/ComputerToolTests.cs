using System.Text.Json.Nodes;
using InspectAzureAI.Eval.Context;
using InspectAzureAI.Eval.Sandbox;
using InspectAzureAI.Eval.Testing;
using InspectAzureAI.Eval.Tools;
using InspectAzureAI.Provider.Core;

namespace InspectAzureAI.Eval.Tests;

using Model = InspectAzureAI.Eval.Model.Model;

/// <summary>The computer tool (<c>tool/_tools/_computer/</c>) and the <c>model_input</c> hook (<c>resolve_tool_model_input</c>).</summary>
public class ComputerToolTests
{
    private static readonly string[] Prefix = ["python3", Computer.ToolPath];

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>A sample with one fake sandbox that has the tool service; every service call answers <paramref name="stdout"/>.</summary>
    private static (SampleContextScope Scope, FakeSandboxEnvironment Sandbox) Sandboxed(string stdout = "{}", ScriptedModelApi? api = null)
    {
        var sandbox = new FakeSandboxEnvironment
        {
            OnExec = cmd => cmd[0] == "python3" ? FakeSandboxEnvironment.Ok(stdout) : FakeSandboxEnvironment.Ok(),
        };
        return (new SampleContextScope(api, sandbox: sandbox), sandbox);
    }

    private static List<IReadOnlyList<string>> ServiceCalls(FakeSandboxEnvironment sandbox) =>
        sandbox.Calls.Where(c => c.Cmd[0] == "python3").Select(c => c.Cmd).ToList();

    // ---------------------------------------------------------------------------------------------------------
    // schema
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_schema_and_description_are_pythons()
    {
        var expected = JsonNode.Parse(await File.ReadAllTextAsync(SandboxToolsFixtures.FixturePath("computer_params.json")));
        var tool = Computer.Create();

        Assert.Equal("computer", tool.Name);
        Assert.Equal(await File.ReadAllTextAsync(SandboxToolsFixtures.FixturePath("computer_description.txt")), tool.Description);
        Assert.True(JsonNode.DeepEquals(expected, tool.Parameters.ToJson()), tool.Parameters.ToJson().ToJsonString());
    }

    [Fact]
    public void is_computer_tool_info_needs_the_name_and_the_exact_parameter_set()
    {
        Assert.True(Computer.IsComputerToolInfo(Computer.Create().ToInfo()));
        Assert.False(Computer.IsComputerToolInfo(new ToolInfo("computer", "Something else called computer.")));
        Assert.False(Computer.IsComputerToolInfo(new ToolInfo("desktop", "Renamed.") { Parameters = Computer.Parameters }));
        var extra = new Dictionary<string, ToolParam>(Computer.Parameters.Properties) { ["extra"] = ToolParam.Of("string") };
        Assert.False(Computer.IsComputerToolInfo(new ToolInfo("computer", "Extended.") { Parameters = new ToolParams { Properties = extra } }));
    }

    // ---------------------------------------------------------------------------------------------------------
    // argv per action
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"action":"screenshot"}""", "screenshot")]
    [InlineData("""{"action":"cursor_position"}""", "cursor_position")]
    [InlineData("""{"action":"left_mouse_down"}""", "left_mouse_down")]
    [InlineData("""{"action":"left_mouse_up"}""", "left_mouse_up")]
    [InlineData("""{"action":"open_web_browser"}""", "open_web_browser")]
    [InlineData("""{"action":"mouse_move","coordinate":[100,200]}""", "mouse_move|--coordinate|100|200")]
    [InlineData("""{"action":"left_click","coordinate":[1,2]}""", "left_click|--coordinate|1|2")]
    [InlineData("""{"action":"right_click","coordinate":[1,2]}""", "right_click|--coordinate|1|2")]
    [InlineData("""{"action":"middle_click","coordinate":[1,2]}""", "middle_click|--coordinate|1|2")]
    [InlineData("""{"action":"back_click","coordinate":[1,2]}""", "back_click|--coordinate|1|2")]
    [InlineData("""{"action":"forward_click","coordinate":[1,2]}""", "forward_click|--coordinate|1|2")]
    [InlineData("""{"action":"double_click","coordinate":[1,2]}""", "double_click|--coordinate|1|2")]
    [InlineData("""{"action":"triple_click","coordinate":[1,2]}""", "triple_click|--coordinate|1|2")]
    [InlineData("""{"action":"left_click_drag","start_coordinate":[1,2],"coordinate":[3,4]}""", "left_click_drag|--start_coordinate|1|2|--coordinate|3|4")]
    [InlineData("""{"action":"scroll","scroll_amount":3,"scroll_direction":"down"}""", "scroll|--scroll_amount|3|--scroll_direction|down")]
    [InlineData("""{"action":"scroll","scroll_amount":3,"scroll_direction":"up","coordinate":[5,6]}""", "scroll|--scroll_amount|3|--scroll_direction|up|--coordinate|5|6")]
    [InlineData("""{"action":"key","text":"ctrl+ENTER"}""", "key|--text|ctrl+Return")]
    [InlineData("""{"action":"hold_key","text":"shift","duration":2}""", "hold_key|--text|shift|--duration|2")]
    [InlineData("""{"action":"wait","duration":5}""", "wait|--duration|5")]
    [InlineData("""{"action":"zoom","region":[1,2,3,4]}""", "zoom|--region|1|2|3|4")]
    [InlineData("""{"action":"navigate","text":"https://example.com"}""", "navigate|--text|https://example.com")]
    [InlineData("""{"action":"type","text":"hello world"}""", "type|--text=hello world")]
    [InlineData("""{"action":"type","text":"hi","press_enter":false}""", "type|--text=hi")]
    public async Task each_action_runs_the_tool_service_with_pythons_argv(string json, string expectedTail)
    {
        var (scope, sandbox) = Sandboxed();
        using (scope)
        {
            await Computer.Create().Execute(Args(json), CancellationToken.None);

            var call = Assert.Single(ServiceCalls(sandbox));
            Assert.Equal([.. Prefix, .. expectedTail.Split('|')], call);
        }
    }

    [Fact]
    public async Task type_clicks_first_and_presses_return_when_asked()
    {
        var (scope, sandbox) = Sandboxed();
        using (scope)
        {
            await Computer.Create().Execute(Args("""{"action":"type","text":"hi","coordinate":[7,8],"press_enter":true}"""), CancellationToken.None);

            var calls = ServiceCalls(sandbox);
            Assert.Equal(3, calls.Count);
            Assert.Equal([.. Prefix, "left_click", "--coordinate", "7", "8"], calls[0]);
            Assert.Equal([.. Prefix, "type", "--text=hi"], calls[1]);
            Assert.Equal([.. Prefix, "key", "--text", "Return"], calls[2]);
        }
    }

    [Fact]
    public async Task an_actions_list_runs_sequentially_and_returns_the_last_result()
    {
        var count = 0;
        var sandbox = new FakeSandboxEnvironment
        {
            OnExec = cmd => cmd[0] == "python3" ? FakeSandboxEnvironment.Ok($$"""{"output":"result {{++count}}"}""") : FakeSandboxEnvironment.Ok(),
        };
        using var scope = new SampleContextScope(sandbox: sandbox);

        var result = await Computer.Create().Execute(
            Args("""{"actions":[{"action":"screenshot"},{"action":"key","text":"a"},{"action":"wait","duration":1}]}"""),
            CancellationToken.None);

        Assert.Equal("result 3", result.AsText());
        Assert.Equal(["screenshot", "key", "wait"], ServiceCalls(sandbox).Select(c => c[2]));
    }

    [Theory]
    [InlineData("""{"action":"left_click"}""", "coordinate must be provided")]
    [InlineData("""{"action":"left_click_drag","coordinate":[1,2]}""", "start_coordinate must be provided")]
    [InlineData("""{"action":"key"}""", "text must be provided")]
    [InlineData("""{"action":"wait"}""", "duration must be provided")]
    [InlineData("""{"action":"scroll","scroll_amount":1}""", "scroll_direction must be provided")]
    [InlineData("""{"action":"zoom","region":[1,2]}""", "region must have 4 elements")]
    [InlineData("""{"action":"mouse_move","coordinate":[1]}""", "coordinate must have 2 elements")]
    [InlineData("""{"action":"bogus"}""", "Invalid action: bogus")]
    [InlineData("""{"actions":[{}]}""", "Invalid action: ")]
    [InlineData("""{}""", "Either 'action' or 'actions' must be provided")]
    [InlineData("""{"actions":[]}""", "Either 'action' or 'actions' must be provided")]
    public async Task missing_parameters_and_unknown_actions_are_parsing_errors(string json, string message)
    {
        var (scope, sandbox) = Sandboxed();
        using (scope)
        {
            var ex = await Assert.ThrowsAsync<ToolParsingError>(() => Computer.Create().Execute(Args(json), CancellationToken.None));

            Assert.Equal(message, ex.Message);
            Assert.Empty(ServiceCalls(sandbox));
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // result parsing
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task output_and_screenshot_become_text_and_image_content()
    {
        var (scope, _) = Sandboxed("""{"output":"at 1,2","error":null,"base64_image":"AAAA"}""");
        using (scope)
        {
            var result = await Computer.Create().Execute(Args("""{"action":"cursor_position"}"""), CancellationToken.None);

            Assert.Null(result.Text);
            Assert.Collection(
                result.Contents!,
                c => Assert.Equal("at 1,2", Assert.IsType<ContentText>(c).Text),
                c => Assert.Equal("data:image/png;base64,AAAA", Assert.IsType<ContentImage>(c).Image));
        }
    }

    [Fact]
    public async Task a_screenshot_alone_is_a_single_image_item()
    {
        var (scope, _) = Sandboxed("""{"base64_image":"AAAA"}""");
        using (scope)
        {
            var result = await Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);

            var image = Assert.IsType<ContentImage>(Assert.Single(result.Contents!));
            Assert.Equal("data:image/png;base64,AAAA", image.Image);
        }
    }

    [Theory]
    [InlineData("""{"output":"(10, 20)"}""", "(10, 20)")]
    [InlineData("""{"output":""}""", "OK")]
    [InlineData("""{}""", "OK")]
    [InlineData("""{"output":null,"error":"","base64_image":null}""", "OK")]
    public async Task text_only_results_are_plain_text_and_empty_results_are_ok(string stdout, string expected)
    {
        var (scope, _) = Sandboxed(stdout);
        using (scope)
        {
            var result = await Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);

            Assert.Null(result.Contents);
            Assert.Equal(expected, result.Text);
        }
    }

    [Fact]
    public async Task a_service_error_is_a_tool_error()
    {
        var (scope, _) = Sandboxed("""{"error":"xdotool: no display"}""");
        using (scope)
        {
            var ex = await Assert.ThrowsAsync<ToolError>(() => Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None));

            Assert.Equal("xdotool: no display", ex.Message);
        }
    }

    [Fact]
    public async Task a_failed_command_fails_the_sample_with_pythons_message()
    {
        var sandbox = new FakeSandboxEnvironment
        {
            OnExec = cmd => cmd[0] == "python3" ? FakeSandboxEnvironment.Fail(1, "boom") : FakeSandboxEnvironment.Ok(),
        };
        using var scope = new SampleContextScope(sandbox: sandbox);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None));

        Assert.Equal("Failure executing command: $['python3', '/opt/inspect/tool/computer_tool.py', 'screenshot'] boom", ex.Message);
    }

    [Fact]
    public async Task the_timeout_reaches_exec()
    {
        var (scope, sandbox) = Sandboxed();
        using (scope)
        {
            await Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);
            await Computer.Create(timeout: TimeSpan.FromSeconds(10)).Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);
            await Computer.Create(timeout: Timeout.InfiniteTimeSpan).Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);

            var timeouts = sandbox.Calls.Where(c => c.Cmd[0] == "python3").Select(c => c.Timeout).ToList();
            Assert.Equal([TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(10), null], timeouts);
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // sandbox discovery
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task the_sandbox_with_the_tool_service_is_found_and_remembered()
    {
        var plain = new FakeSandboxEnvironment { OnExec = _ => FakeSandboxEnvironment.Fail(1) };
        var desktop = new FakeSandboxEnvironment
        {
            OnExec = cmd => cmd[0] == "python3" ? FakeSandboxEnvironment.Ok("""{"output":"ok"}""") : FakeSandboxEnvironment.Ok(),
        };
        var context = new SampleContext
        {
            ActiveModel = new Model(new ScriptedModelApi()),
            Sandboxes = SandboxEnvironments.Create(
            [
                new KeyValuePair<string, ISandboxEnvironment>("default", plain),
                new KeyValuePair<string, ISandboxEnvironment>("desktop", desktop),
            ]),
        };
        using var scope = SampleContext.Begin(context);

        var tool = Computer.Create();
        await tool.Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);
        await tool.Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);

        var probe = Assert.Single(plain.Calls);
        Assert.Equal(["test", "-r", Computer.ToolPath], probe.Cmd);
        Assert.Equal(["test", "-r", Computer.ToolPath], Assert.Single(desktop.Calls, c => c.Cmd[0] == "test").Cmd);
        Assert.Equal(2, ServiceCalls(desktop).Count);
    }

    [Fact]
    public async Task a_readable_file_counts_when_test_is_unavailable()
    {
        var sandbox = new FakeSandboxEnvironment
        {
            OnExec = cmd => cmd[0] == "python3" ? FakeSandboxEnvironment.Ok("{}") : FakeSandboxEnvironment.Fail(127, "test: not found"),
        };
        sandbox.Files[Computer.ToolPath] = "#!/usr/bin/env python3"u8.ToArray();
        using var scope = new SampleContextScope(sandbox: sandbox);

        var result = await Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None);

        Assert.Equal("OK", result.Text);
        Assert.Single(ServiceCalls(sandbox));
    }

    [Fact]
    public async Task no_sandbox_with_the_tool_service_is_a_prerequisite_error()
    {
        var sandbox = new FakeSandboxEnvironment { OnExec = _ => FakeSandboxEnvironment.Fail(1) };
        using var scope = new SampleContextScope(sandbox: sandbox);

        var ex = await Assert.ThrowsAsync<PrerequisiteError>(() => Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None));

        Assert.StartsWith("The computer tool service was not found in any of the sandboxes for this sample.", ex.Message);
        Assert.EndsWith("services:\n  default:\n    image: \"aisiuk/inspect-computer-tool\"\n    init: true", ex.Message);
        Assert.Empty(ServiceCalls(sandbox));
    }

    [Fact]
    public async Task no_sandbox_at_all_is_an_invalid_operation()
    {
        using var scope = new SampleContextScope();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Computer.Create().Execute(Args("""{"action":"screenshot"}"""), CancellationToken.None));
    }

    [Fact]
    public async Task sandbox_with_on_path_uses_which_and_a_named_sandbox_only()
    {
        var first = new FakeSandboxEnvironment { OnExec = _ => FakeSandboxEnvironment.Fail(1) };
        var second = new FakeSandboxEnvironment { OnExec = cmd => cmd[0] == "which" ? FakeSandboxEnvironment.Ok("/usr/bin/xdotool\n") : FakeSandboxEnvironment.Fail(1) };
        var context = new SampleContext
        {
            ActiveModel = new Model(new ScriptedModelApi()),
            Sandboxes = SandboxEnvironments.Create(
            [
                new KeyValuePair<string, ISandboxEnvironment>("first", first),
                new KeyValuePair<string, ISandboxEnvironment>("second", second),
            ]),
        };
        using var scope = SampleContext.Begin(context);

        Assert.Same(second, await SandboxWith.FindAsync("xdotool", onPath: true));
        Assert.Equal(["which", "xdotool"], Assert.Single(second.Calls).Cmd);
        Assert.Null(await SandboxWith.FindAsync("xdotool", onPath: true, name: "first"));
        Assert.Null(await SandboxWith.FindAsync("xdotool", onPath: true, name: "missing"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // key normalisation
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("enter", "Return")]
    [InlineData("ctrl+ENTER", "ctrl+Return")]
    [InlineData("Ctrl+A", "Ctrl+a")]
    [InlineData("A", "A")]
    [InlineData("cmd+c", "super+c")]
    [InlineData("control+shift+esc", "ctrl+shift+Escape")]
    [InlineData("PageUp", "Prior")]
    [InlineData("ctrl+s  alt+tab", "ctrl+s alt+Tab")]
    [InlineData(",", "comma")]
    [InlineData("shift+,", "shift+comma")]
    [InlineData("kp_0", "KP_0")]
    [InlineData("KP_ENTER", "KP_Enter")]
    [InlineData("f5", "F5")]
    [InlineData("PRINTSCREEN", "Print")]
    [InlineData("arrowdown", "Down")]
    [InlineData("Unknown_Key", "Unknown_Key")]
    public void key_text_is_normalised_for_xdotool(string text, string expected)
    {
        Assert.Equal(expected, Computer.NormalizeKeyText(text));
    }

    // ---------------------------------------------------------------------------------------------------------
    // model_input hook
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task old_screenshots_are_replaced_before_the_model_sees_them()
    {
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("computer", new { action = "screenshot" }),
            ScriptedTurn.ToolCall("computer", new { action = "left_click", coordinate = new[] { 1, 2 } }),
            ScriptedTurn.ToolCall("computer", new { action = "screenshot" }),
            ScriptedTurn.Text("done"));
        var (scope, _) = Sandboxed("""{"base64_image":"AAAA"}""", api);
        using (scope)
        {
            var (messages, output) = await scope.Model.GenerateLoopAsync([new ChatMessageUser("look")], new ToolDef[] { Computer.Create() });

            Assert.Equal("done", output.Completion);
            Assert.Equal(4, api.Requests.Count);

            // the conversation itself keeps every screenshot
            var results = messages.OfType<ChatMessageTool>().ToList();
            Assert.Equal(3, results.Count);
            Assert.All(results, m => Assert.IsType<ContentImage>(Assert.Single(m.Content.Items!)));

            // one result: inside max_screenshots
            Assert.IsType<ContentImage>(Assert.Single(Assert.Single(api.Requests[1].Input.OfType<ChatMessageTool>()).Content.Items!));

            // three results: only the last keeps its image, and the redacted ones carry new ids
            var seen = api.Requests[3].Input.OfType<ChatMessageTool>().ToList();
            Assert.Equal(3, seen.Count);
            Assert.Equal(Computer.ScreenshotRemovedText, Assert.IsType<ContentText>(Assert.Single(seen[0].Content.Items!)).Text);
            Assert.Equal(Computer.ScreenshotRemovedText, Assert.IsType<ContentText>(Assert.Single(seen[1].Content.Items!)).Text);
            Assert.IsType<ContentImage>(Assert.Single(seen[2].Content.Items!));
            Assert.NotEqual(results[0].Id, seen[0].Id);
            Assert.Equal(results[2].Id, seen[2].Id);
            Assert.Equal(results[0].ToolCallId, seen[0].ToolCallId);
        }
    }

    [Fact]
    public async Task max_screenshots_bounds_how_many_images_survive()
    {
        var api = new ScriptedModelApi(
            ScriptedTurn.ToolCall("computer", new { action = "screenshot" }),
            ScriptedTurn.ToolCall("computer", new { action = "screenshot" }),
            ScriptedTurn.ToolCall("computer", new { action = "screenshot" }),
            ScriptedTurn.Text("done"));
        var (scope, _) = Sandboxed("""{"output":"shot","base64_image":"AAAA"}""", api);
        using (scope)
        {
            await scope.Model.GenerateLoopAsync([new ChatMessageUser("look")], new ToolDef[] { Computer.Create(maxScreenshots: 2) });

            var seen = api.Requests[3].Input.OfType<ChatMessageTool>().Select(m => m.Content.Items!).ToList();
            Assert.Equal(["shot", Computer.ScreenshotRemovedText], seen[0].Select(c => Assert.IsType<ContentText>(c).Text));
            Assert.IsType<ContentImage>(seen[1][1]);
            Assert.IsType<ContentImage>(seen[2][1]);
        }
    }

    [Fact]
    public void max_screenshots_null_installs_no_hook()
    {
        Assert.Null(Computer.Create(maxScreenshots: null).ModelInput);
        Assert.NotNull(Computer.Create().ModelInput);
    }

    [Fact]
    public void the_hook_leaves_scalars_recent_results_and_hinted_models_alone()
    {
        var hook = Computer.ModelInput(1);
        var image = MessageContent.FromItems([new ContentText("shot"), new ContentImage("data:image/png;base64,AAAA")]);
        var text = MessageContent.FromString("OK");

        Assert.Same(text, hook(0, 3, text, ToolCallModelInputHints.None));
        Assert.Same(image, hook(2, 3, image, ToolCallModelInputHints.None));
        Assert.Same(image, hook(0, 3, image, new ToolCallModelInputHints(DisableComputerScreenshotTruncation: true)));

        var redacted = hook(0, 3, image, ToolCallModelInputHints.None);
        Assert.Equal(["shot", Computer.ScreenshotRemovedText], redacted.Items!.Select(c => Assert.IsType<ContentText>(c).Text));
    }

    [Fact]
    public void resolve_applies_each_tools_hook_to_its_own_results_only()
    {
        var upper = new ToolDef("upper", "Upper-cases.", new ToolParams(), (_, _) => Task.FromResult<ToolResult>("x"))
        {
            ModelInput = (index, total, content, _) => MessageContent.FromString($"{content.Text!.ToUpperInvariant()} {index}/{total}"),
        };
        var plain = new ToolDef("plain", "No hook.", new ToolParams(), (_, _) => Task.FromResult<ToolResult>("x"));
        ChatMessage[] messages =
        [
            new ChatMessageUser("go"),
            new ChatMessageTool("a", toolCallId: "1", function: "upper"),
            new ChatMessageTool("b", toolCallId: "2", function: "plain"),
            new ChatMessageTool("c", toolCallId: "3", function: "upper"),
        ];

        var resolved = ToolModelInput.Resolve([upper, plain], messages);

        Assert.Equal(["go", "A 0/3", "b", "C 1/3"], resolved.Select(m => m.Text));
        Assert.Same(messages[0], resolved[0]);
        Assert.Same(messages[2], resolved[2]);
        Assert.NotEqual(messages[1].Id, resolved[1].Id);
        Assert.Equal("1", Assert.IsType<ChatMessageTool>(resolved[1]).ToolCallId);
        Assert.Equal("a", messages[1].Text);
        Assert.Same(messages, ToolModelInput.Resolve([plain], messages));
    }

    [Fact]
    public async Task an_api_that_declines_truncation_keeps_every_screenshot()
    {
        var scripted = new ScriptedModelApi(
            ScriptedTurn.ToolCall("computer", new { action = "screenshot" }),
            ScriptedTurn.ToolCall("computer", new { action = "screenshot" }),
            ScriptedTurn.Text("done"));
        var api = new NoTruncationApi(scripted);
        Assert.True(ToolModelInput.HintsFor(api).DisableComputerScreenshotTruncation);
        Assert.False(ToolModelInput.HintsFor(scripted).DisableComputerScreenshotTruncation);

        var (scope, _) = Sandboxed("""{"base64_image":"AAAA"}""");
        using (scope)
        {
            await new Model(api).GenerateLoopAsync([new ChatMessageUser("look")], new ToolDef[] { Computer.Create() });

            var seen = scripted.Requests[2].Input.OfType<ChatMessageTool>().ToList();
            Assert.Equal(2, seen.Count);
            Assert.All(seen, m => Assert.IsType<ContentImage>(Assert.Single(m.Content.Items!)));
        }
    }

    /// <summary>A model api with Python's OpenAI-style <c>disable_computer_screenshot_truncation() -> True</c>.</summary>
    private sealed class NoTruncationApi(ScriptedModelApi inner) : IModelApi, IToolModelInputHintsApi
    {
        public string ModelName => inner.ModelName;

        public int? MaxTokens() => inner.MaxTokens();

        public bool DisableComputerScreenshotTruncation() => true;

        public Task<GenerateResult> GenerateAsync(IReadOnlyList<ChatMessage> input, IReadOnlyList<ToolInfo> tools, ToolChoice toolChoice, GenerateConfig config, StreamHandler? onStream, CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(input, tools, toolChoice, config, onStream, cancellationToken);
    }
}
