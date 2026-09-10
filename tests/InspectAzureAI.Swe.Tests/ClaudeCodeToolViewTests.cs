using System.Text.Json.Nodes;
using InspectAzureAI.Swe.ClaudeCode;

namespace InspectAzureAI.Swe.Tests;

/// <summary>Byte-exact port checks against inspect_swe <c>_claude_code/_events/toolview.py</c>.</summary>
public class ClaudeCodeToolViewTests
{
    private static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void write_view_fences_content_with_the_extension_language()
    {
        var view = ClaudeCodeToolView.For("Write", Args("""{"file_path": "/workspace/app.py", "content": "print('hi')\n"}"""))!;

        Assert.Equal("Write", view.Title);
        Assert.Equal("markdown", view.Format);
        Assert.Equal("`file_path: /workspace/app.py`\n\n``````python\n{{content}}``````\n", view.Content);
    }

    [Fact]
    public void write_view_adds_a_newline_when_content_lacks_one_and_lower_cases_the_extension()
    {
        var upper = ClaudeCodeToolView.For("Write", Args("""{"file_path": "src/MAIN.RS", "content": "fn main() {}"}"""))!;
        var r = ClaudeCodeToolView.For("Write", Args("""{"file_path": "analysis.R", "content": "x"}"""))!;

        Assert.Equal("`file_path: src/MAIN.RS`\n\n``````rust\n{{content}}\n``````\n", upper.Content);
        Assert.Equal("`file_path: analysis.R`\n\n``````r\n{{content}}\n``````\n", r.Content);
    }

    [Fact]
    public void write_view_without_a_known_extension_has_no_language()
    {
        var unknown = ClaudeCodeToolView.For("Write", Args("""{"file_path": "notes.txt", "content": "a\n"}"""))!;
        var dotfile = ClaudeCodeToolView.For("Write", Args("""{"file_path": "/home/u/.bashrc", "content": "a\n"}"""))!;
        var dottedDir = ClaudeCodeToolView.For("Write", Args("""{"file_path": "pkg.d/Makefile", "content": "a\n"}"""))!;

        Assert.Equal("`file_path: notes.txt`\n\n``````\n{{content}}``````\n", unknown.Content);
        Assert.Equal("`file_path: /home/u/.bashrc`\n\n``````\n{{content}}``````\n", dotfile.Content);
        Assert.Equal("`file_path: pkg.d/Makefile`\n\n``````\n{{content}}``````\n", dottedDir.Content);
    }

    [Fact]
    public void write_view_python_str_quirks_for_missing_and_null_arguments()
    {
        // file_path missing or null reads as "" (str(x or "")); content missing reads as "", null as "None" (no trailing newline either way)
        var empty = ClaudeCodeToolView.For("Write", new JsonObject())!;
        var nulls = ClaudeCodeToolView.For("Write", Args("""{"file_path": null, "content": null}"""))!;

        Assert.Equal("`file_path: `\n\n``````\n{{content}}\n``````\n", empty.Content);
        Assert.Equal(empty.Content, nulls.Content);
    }

    [Fact]
    public void exit_plan_mode_view_fences_the_plan()
    {
        var view = ClaudeCodeToolView.For("ExitPlanMode", Args("""{"plan": "1. do it"}"""))!;

        Assert.Equal("ExitPlanMode", view.Title);
        Assert.Equal("markdown", view.Format);
        Assert.Equal("``````markdown\n{{plan}}\n``````", view.Content);
    }

    [Theory]
    [InlineData("Task")]
    [InlineData("Agent")]
    public void subagent_views_title_with_the_subagent_type(string function)
    {
        var view = ClaudeCodeToolView.For(function, Args("""{"subagent_type": "Explore", "description": "look", "prompt": "p"}"""))!;

        Assert.Equal($"{function}: Explore", view.Title);
        Assert.Equal("markdown", view.Format);
        Assert.Equal("### {{description}}\n\n{{prompt}}", view.Content);
    }

    [Fact]
    public void subagent_view_title_is_empty_without_a_subagent_type_and_none_for_null()
    {
        var missing = ClaudeCodeToolView.For("Task", Args("""{"prompt": "p"}"""))!;
        var empty = ClaudeCodeToolView.For("Agent", Args("""{"subagent_type": ""}"""))!;
        var none = ClaudeCodeToolView.For("Task", Args("""{"subagent_type": null}"""))!;

        Assert.Equal("", missing.Title);
        Assert.Equal("", empty.Title);
        Assert.Equal("Task: None", none.Title);
    }

    [Fact]
    public void other_tools_have_no_view_and_the_language_table_matches_python()
    {
        Assert.Null(ClaudeCodeToolView.For("Bash", Args("""{"command": "ls"}""")));
        Assert.Null(ClaudeCodeToolView.For("write", new JsonObject()));
        Assert.Equal(
            [".py", ".ts", ".tsx", ".js", ".jsx", ".json", ".yaml", ".yml", ".toml", ".sh", ".bash", ".zsh", ".css", ".html", ".sql", ".rs", ".go", ".r", ".R", ".md", ".qmd"],
            ClaudeCodeToolView.CodeFenceLanguages.Keys);
        Assert.Equal("markdown", ClaudeCodeToolView.CodeFenceLanguages[".qmd"]);
    }

    [Theory]
    [InlineData("a/b.py", ".py")]
    [InlineData("a.b.tar.gz", ".gz")]
    [InlineData(".bashrc", "")]
    [InlineData("..py", "")]
    [InlineData("dir/...x", "")]
    [InlineData("dir.v2/file", "")]
    [InlineData("file.", ".")]
    [InlineData("", "")]
    public void split_extension_follows_posixpath(string path, string expected)
    {
        Assert.Equal(expected, ClaudeCodeToolView.SplitExtension(path));
    }
}
