using System.Text.Json.Nodes;
using InspectAzureAI.Swe.CodexCli;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Byte-exact views of <c>_codex_cli/_events/toolview.py</c>.</summary>
public class CodexCliToolViewsTests
{
    private static readonly IReadOnlyDictionary<string, string> NoNicknames = new Dictionary<string, string>();

    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void exec_command_is_a_bash_block_of_cmd()
    {
        var view = CodexCliToolViews.For("exec_command", Args("""{"cmd": "ls -la"}"""), NoNicknames)!;

        Assert.Equal(("exec_command", "markdown", "``````bash\n{{cmd}}\n``````\n"), (view.Title, view.Format, view.Content));
        Assert.Null(CodexCliToolViews.For("exec_command", Args("""{"command": "ls"}"""), NoNicknames));
    }

    [Fact]
    public void spawn_agent_is_titled_by_agent_type_when_present()
    {
        var typed = CodexCliToolViews.For("spawn_agent", Args("""{"agent_type": "explorer", "message": "look around"}"""), NoNicknames)!;
        var untyped = CodexCliToolViews.For("spawn_agent", Args("""{"agent_type": "", "message": "look around"}"""), NoNicknames)!;

        Assert.Equal(("spawn_agent: explorer", "{{message}}"), (typed.Title, typed.Content));
        Assert.Equal("spawn_agent", untyped.Title);
        Assert.Null(CodexCliToolViews.For("spawn_agent", Args("""{"task_name": "x"}"""), NoNicknames));
    }

    [Fact]
    public void apply_patch_is_a_diff_of_input_then_patch()
    {
        Assert.Equal("``````diff\n{{input}}\n``````\n", CodexCliToolViews.For("apply_patch", Args("""{"input": "*** Begin Patch", "patch": "x"}"""), NoNicknames)!.Content);
        Assert.Equal("``````diff\n{{patch}}\n``````\n", CodexCliToolViews.For("apply_patch", Args("""{"patch": "x"}"""), NoNicknames)!.Content);
        Assert.Equal("apply_patch", CodexCliToolViews.For("apply_patch", Args("""{"patch": "x"}"""), NoNicknames)!.Title);
        Assert.Null(CodexCliToolViews.For("apply_patch", Args("{}"), NoNicknames));
    }

    [Fact]
    public void web_search_and_send_input_render_their_payload()
    {
        Assert.Equal(("web_search", "{{query}}"), (CodexCliToolViews.For("web_search", Args("""{"query": "dotnet"}"""), NoNicknames)!.Title, CodexCliToolViews.For("web_search", Args("""{"query": "dotnet"}"""), NoNicknames)!.Content));
        Assert.Equal(("send_input", "{{message}}"), (CodexCliToolViews.For("send_input", Args("""{"target": "t", "message": "hi"}"""), NoNicknames)!.Title, CodexCliToolViews.For("send_input", Args("""{"message": "hi"}"""), NoNicknames)!.Content));
        Assert.Null(CodexCliToolViews.For("send_input", Args("""{"target": "t", "message": ""}"""), NoNicknames));
        Assert.Null(CodexCliToolViews.For("send_input", Args("""{"target": "t"}"""), NoNicknames));
    }

    [Fact]
    public void wait_agent_lists_targets_with_nicknames_and_the_timeout_in_seconds()
    {
        var nicknames = new Dictionary<string, string> { ["thread_1"] = "Scout", ["thread_2"] = "" };

        var view = CodexCliToolViews.For("wait_agent", Args("""{"targets": ["thread_1", "thread_2", 5], "timeout_ms": 30000}"""), nicknames)!;

        Assert.Equal("wait_agent", view.Title);
        Assert.Equal("- Scout — `thread_1`\n- `thread_2`\n- `5`\n\n_timeout: 30s_", view.Content);
    }

    [Theory]
    [InlineData("1500.7", "\n\n_timeout: 1s_")]
    [InlineData("-1500", "\n\n_timeout: -2s_")]
    [InlineData("true", "\n\n_timeout: 0s_")]
    [InlineData("\"30000\"", "")]
    [InlineData("null", "")]
    public void wait_agent_timeout_follows_python_int_floor_division(string timeout, string suffix)
    {
        var view = CodexCliToolViews.For("wait_agent", Args($$"""{"targets": ["t"], "timeout_ms": {{timeout}}}"""), NoNicknames)!;

        Assert.Equal("- `t`" + suffix, view.Content);
    }

    [Fact]
    public void numbers_built_in_code_read_like_parsed_ones()
    {
        var targets = new JsonArray("t");

        Assert.Equal("- `t`\n\n_timeout: 2s_", CodexCliToolViews.For("wait_agent", new JsonObject { ["targets"] = targets.DeepClone(), ["timeout_ms"] = 2500 }, NoNicknames)!.Content);
        Assert.Equal("- `t`\n\n_timeout: 1s_", CodexCliToolViews.For("wait_agent", new JsonObject { ["targets"] = targets.DeepClone(), ["timeout_ms"] = 1999.9 }, NoNicknames)!.Content);
        Assert.Equal("spawn_agent: 5", CodexCliToolViews.For("spawn_agent", new JsonObject { ["agent_type"] = 5, ["message"] = "m" }, NoNicknames)!.Title);
        Assert.Equal("spawn_agent", CodexCliToolViews.For("spawn_agent", new JsonObject { ["agent_type"] = 0, ["message"] = "m" }, NoNicknames)!.Title);
    }

    [Fact]
    public void wait_agent_without_targets_and_other_tools_have_no_view()
    {
        Assert.Null(CodexCliToolViews.For("wait_agent", Args("""{"targets": []}"""), NoNicknames));
        Assert.Null(CodexCliToolViews.For("wait_agent", Args("""{"targets": "thread_1"}"""), NoNicknames));
        Assert.Null(CodexCliToolViews.For("close_agent", Args("""{"target": "thread_1"}"""), NoNicknames));
        Assert.Null(CodexCliToolViews.For("shell", Args("""{"cmd": "ls"}"""), NoNicknames));
    }
}
