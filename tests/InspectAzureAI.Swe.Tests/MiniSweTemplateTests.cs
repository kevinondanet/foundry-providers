using InspectAzureAI.Swe.MiniSwe;

namespace InspectAzureAI.Swe.Tests;

/// <summary>The mini.yaml texts of <see cref="MiniSweTemplates"/> and their rendering, checked against what Jinja2 produces for the same inputs.</summary>
public class MiniSweTemplateTests
{
    /// <summary>Jinja2's rendering of the upstream instance_template with task "Fix the bug" on a Linux uname.</summary>
    private const string RenderedInstanceOnLinux = """
        Please solve this issue: Fix the bug

        You can execute bash commands and edit files to implement the necessary changes.

        ## Recommended Workflow

        This workflow should be done step-by-step so that you can iterate on your changes and any possible problems.

        1. Analyze the codebase by finding and reading relevant files
        2. Create a script to reproduce the issue
        3. Edit the source code to resolve the issue
        4. Verify your fix works by running your script again
        5. Test edge cases to ensure your fix is robust
        6. Submit your changes and finish your work by issuing the following command: `echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT`.
           Do not combine it with any other command. <important>After this command, you cannot continue working on this task.</important>

        ## Command Execution Rules

        You are operating in an environment where

        1. You issue at least one command
        2. The system executes the command(s) in a subshell
        3. You see the result(s)
        4. You write your next command(s)

        Each response should include:

        1. **Reasoning text** where you explain your analysis and plan
        2. At least one tool call with your command

        **CRITICAL REQUIREMENTS:**

        - Your response SHOULD include reasoning text explaining what you're doing
        - Your response MUST include AT LEAST ONE bash tool call
        - Directory or environment variable changes are not persistent. Every action is executed in a new subshell.
        - However, you can prefix any action with `MY_ENV_VAR=MY_VALUE cd /path/to/working/dir && ...` or write/load environment variables from files
        - Submit your changes and finish your work by issuing the following command: `echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT`.
          Do not combine it with any other command. <important>After this command, you cannot continue working on this task.</important>

        Example of a CORRECT response:
        <example_response>
        I need to understand the structure of the repository first. Let me check what files are in the current directory to get a better understanding of the codebase.

        [Makes bash tool call with {"command": "ls -la"} as arguments]
        </example_response>

        <system_information>
        Linux 6.1.0 #1 SMP x86_64
        </system_information>

        ## Useful command examples

        ### Create a new file:

        ```bash
        cat <<'EOF' > newfile.py
        import numpy as np
        hello = "world"
        print(hello)
        EOF
        ```

        ### Edit files with sed:```bash
        # Replace all occurrences
        sed -i 's/old_string/new_string/g' filename.py

        # Replace only first occurrence
        sed -i 's/old_string/new_string/' filename.py

        # Replace first occurrence on line 1
        sed -i '1s/old_string/new_string/' filename.py

        # Replace all occurrences in lines 1-10
        sed -i '1,10s/old_string/new_string/g' filename.py
        ```

        ### View file content:

        ```bash
        # View specific lines with numbers
        nl -ba filename.py | sed -n '10,20p'
        ```

        ### Any other command you want to run

        ```bash
        anything
        ```
        """;

    private const string GuidancePrefix = "Tool call error:\n\n<error>\n";

    private const string GuidanceSuffix =
        "\n</error>\n\nHere is general guidance on how to submit correct toolcalls:\n\n"
        + "Every response needs to use the 'bash' tool at least once to execute commands.\n\n"
        + "Call the bash tool with your command as the argument:\n- Tool: bash\n- Arguments: {\"command\": \"your_command_here\"}\n\n"
        + "If you want to end the task, please issue the following command: `echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT`\nwithout any other command.";

    private static Dictionary<string, string> Vars(params (string Name, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);

    [Fact]
    public void system_template_is_the_yaml_text_and_renders_without_the_trailing_newline()
    {
        Assert.Equal("You are a helpful assistant that can interact with a computer.\n", MiniSweTemplates.System);
        Assert.Equal("You are a helpful assistant that can interact with a computer.", TemplateRenderer.Render(MiniSweTemplates.System, Vars()));
    }

    [Fact]
    public void instance_template_renders_exactly_like_jinja_on_linux()
    {
        var rendered = TemplateRenderer.Render(
            MiniSweTemplates.Instance,
            Vars(("task", "Fix the bug"), ("system", "Linux"), ("release", "6.1.0"), ("version", "#1 SMP"), ("machine", "x86_64")));

        Assert.Equal(RenderedInstanceOnLinux, rendered);
    }

    [Fact]
    public void instance_template_keeps_the_placeholders_and_drops_the_darwin_block()
    {
        Assert.StartsWith("Please solve this issue: {{task}}\n", MiniSweTemplates.Instance);
        Assert.Contains("<system_information>\n{{system}} {{release}} {{version}} {{machine}}\n</system_information>", MiniSweTemplates.Instance);
        Assert.DoesNotContain("Darwin", MiniSweTemplates.Instance);
        Assert.DoesNotContain("{%", MiniSweTemplates.Instance);
        Assert.Contains("### Edit files with sed:```bash\n# Replace all occurrences\n", MiniSweTemplates.Instance);
        Assert.EndsWith("```bash\nanything\n```\n", MiniSweTemplates.Instance);
        Assert.Throws<KeyNotFoundException>(() => TemplateRenderer.Render(MiniSweTemplates.Instance, Vars(("task", "t"))));
    }

    [Fact]
    public void branch_constants_are_verbatim_fragments_of_the_jinja_sources()
    {
        Assert.Contains(MiniSweTemplates.FormatErrorTruncated, MiniSweTemplates.FormatError);
        Assert.Contains(MiniSweTemplates.FormatErrorGuidance, MiniSweTemplates.FormatError);
        Assert.StartsWith("{% if finish_reason is defined and (finish_reason == \"length\" or (finish_reason == \"tool_calls\" and not has_tool_calls)) -%}\n", MiniSweTemplates.FormatError);
        Assert.StartsWith("{%- if output.output | length < 10000 -%}\n{\n  \"returncode\": {{ output.returncode }},\n  \"output\": {{ output.output | tojson }}\n", MiniSweTemplates.Observation);
        Assert.EndsWith("  \"warning\": \"Output too long.\"\n  {%- if output.exception_info %}, \"exception_info\": {{ output.exception_info | tojson }}{% endif %}\n}\n{%- endif -%}\n", MiniSweTemplates.Observation);
    }

    [Theory]
    [InlineData("stop", false, false)]
    [InlineData("length", false, true)]
    [InlineData("tool_calls", false, true)]
    [InlineData("tool_calls", true, false)]
    [InlineData("length", true, true)]
    [InlineData(null, false, false)]
    public void format_error_picks_the_token_limit_branch_like_jinja(string? finishReason, bool hasToolCalls, bool truncated)
    {
        var rendered = MiniSweTemplates.RenderFormatError("Unknown tool 'ls'.", hasToolCalls, finishReason);

        if (truncated)
        {
            Assert.Equal(
                $"Your previous response reached the output token limit (finish_reason={finishReason}) before you produced a tool call, so it was cut off. "
                + "Respond more concisely and finish with exactly one bash tool call. If you need to think more, do so briefly.",
                rendered);
        }
        else
        {
            Assert.Equal(GuidancePrefix + "Unknown tool 'ls'." + GuidanceSuffix, rendered);
        }
    }

    [Fact]
    public void short_observation_matches_jinja()
    {
        var rendered = MiniSweTemplates.RenderObservation(new CommandObservation("hello <world> & 'x'\n", 0, ""));

        Assert.Equal("{\n  \"returncode\": 0,\n  \"output\": \"hello \\u003cworld\\u003e \\u0026 \\u0027x\\u0027\\n\"\n}", rendered);
    }

    [Fact]
    public void observation_with_exception_info_matches_jinja()
    {
        var rendered = MiniSweTemplates.RenderObservation(
            new CommandObservation("", -1, "An error occurred while executing the command: Command 'sleep 5' timed out after 1 seconds"));

        Assert.Equal(
            "{\n  \"returncode\": -1,\n  \"output\": \"\", \"exception_info\": \"An error occurred while executing the command: Command \\u0027sleep 5\\u0027 timed out after 1 seconds\"\n}",
            rendered);
    }

    [Fact]
    public void observation_escapes_like_python_json_dumps()
    {
        var rendered = MiniSweTemplates.RenderObservation(new CommandObservation("h\u00e9llo\t\"q\"\\ \u0001 \u00e9 \u65e5\u672c\r\n", 0, ""));

        Assert.Equal("{\n  \"returncode\": 0,\n  \"output\": \"h\\u00e9llo\\t\\\"q\\\"\\\\ \\u0001 \\u00e9 \\u65e5\\u672c\\r\\n\"\n}", rendered);
    }

    [Fact]
    public void long_observation_uses_head_tail_and_elided_count()
    {
        var output = new string('a', 6000) + new string('b', 6000);

        var rendered = MiniSweTemplates.RenderObservation(new CommandObservation(output, 0, ""));

        Assert.Equal(
            "{\n  \"returncode\": 0,\n  \"output_head\": \"" + new string('a', 5000) + "\",\n  \"output_tail\": \"" + new string('b', 5000)
            + "\",\n  \"elided_chars\": 2000,\n  \"warning\": \"Output too long.\"\n}",
            rendered);
    }

    [Fact]
    public void ten_thousand_chars_is_long_while_9999_is_short()
    {
        var atLimit = MiniSweTemplates.RenderObservation(new CommandObservation(new string('x', 10000), -1, "boom"));
        var below = MiniSweTemplates.RenderObservation(new CommandObservation(new string('y', 9999), 2, ""));

        Assert.EndsWith("\",\n  \"elided_chars\": 0,\n  \"warning\": \"Output too long.\", \"exception_info\": \"boom\"\n}", atLimit);
        Assert.StartsWith("{\n  \"returncode\": -1,\n  \"output_head\": \"" + new string('x', 5000) + "\",\n  \"output_tail\": \"", atLimit);
        Assert.Equal("{\n  \"returncode\": 2,\n  \"output\": \"" + new string('y', 9999) + "\"\n}", below);
    }

    [Fact]
    public void observation_lengths_count_code_points_like_python()
    {
        var output = string.Concat(Enumerable.Repeat("\U0001F600", 10000));

        var rendered = MiniSweTemplates.RenderObservation(new CommandObservation(output, 0, ""));

        Assert.Contains("\"elided_chars\": 0,", rendered);
        Assert.Contains("\"output_head\": \"" + string.Concat(Enumerable.Repeat("\\ud83d\\ude00", 5000)) + "\",", rendered);
        Assert.Contains("\"output_tail\": \"" + string.Concat(Enumerable.Repeat("\\ud83d\\ude00", 5000)) + "\",", rendered);
    }

    [Fact]
    public void renderer_substitutes_placeholders_strictly_and_never_re_renders_values()
    {
        Assert.Equal("a-B-c", TemplateRenderer.Render("a-{{ x }}-c", Vars(("x", "B"))));
        Assert.Equal("{{y}}", TemplateRenderer.Render("{{x}}", Vars(("x", "{{y}}"), ("y", "no"))));
        Assert.Equal("keep\n", TemplateRenderer.Render("keep\n\n", Vars()));
        Assert.Equal("a\nb", TemplateRenderer.Render("a\r\nb\r\n", Vars()));

        var ex = Assert.Throws<KeyNotFoundException>(() => TemplateRenderer.Render("{{ missing }}", Vars()));

        Assert.Equal("'missing' is undefined", ex.Message);
    }

    [Fact]
    public void default_environment_is_the_yaml_environment_section()
    {
        Assert.Equal(
            new Dictionary<string, string> { ["PAGER"] = "cat", ["MANPAGER"] = "cat", ["LESS"] = "-R", ["PIP_PROGRESS_BAR"] = "off", ["TQDM_DISABLE"] = "1" },
            MiniSweTemplates.DefaultEnvironment);
    }
}
