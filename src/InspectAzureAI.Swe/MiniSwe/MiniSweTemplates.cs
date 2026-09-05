using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using InspectAzureAI.Provider.Util;

namespace InspectAzureAI.Swe.MiniSwe;

/// <summary>
/// The texts of mini-swe-agent's <c>config/mini.yaml</c>, verbatim: the agent's system and instance templates
/// (rendered with <see cref="TemplateRenderer"/>) and the model's observation and format-error templates, kept
/// as their Jinja sources while <see cref="RenderObservation"/> and <see cref="RenderFormatError"/> reproduce
/// their conditionals in C#.
/// </summary>
public static class MiniSweTemplates
{
    /// <summary>The first output line that ends the task (<c>LocalEnvironment._check_finished</c>).</summary>
    public const string SubmitMarker = "COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT";

    /// <summary>The exit reminder inspect_swe's <c>ResumableAgent</c> appends to a resumed task.</summary>
    public const string ResumeReminder = "When you are done, submit by running: `echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT`";

    /// <summary>The <c>error</c> of <c>parse_toolcall_actions</c> when a response carries no tool call.</summary>
    public const string NoToolCallsError = "No tool calls found in the response. Every response MUST include at least one tool call.";

    /// <summary>Outputs of this many characters or more are shown as head, tail and an elided count.</summary>
    public const int ObservationMaxChars = 10000;

    public const int ObservationHeadChars = 5000;

    public const int ObservationTailChars = 5000;

    /// <summary>Port of the <c>environment.env</c> section: pager and progress-bar suppression for every command.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PAGER"] = "cat",
        ["MANPAGER"] = "cat",
        ["LESS"] = "-R",
        ["PIP_PROGRESS_BAR"] = "off",
        ["TQDM_DISABLE"] = "1",
    };

    /// <summary>Port of <c>agent.system_template</c>.</summary>
    public const string System = """
        You are a helpful assistant that can interact with a computer.

        """;

    /// <summary>
    /// Port of <c>agent.instance_template</c> as Jinja renders it on Linux: the <c>{% if system == "Darwin" %}</c>
    /// block is gone and, because its tags strip the surrounding whitespace (<c>{%-</c> / <c>-%}</c>), the
    /// "Edit files with sed" heading runs straight into the code fence exactly as upstream shows the model.
    /// </summary>
    public const string Instance = """
        Please solve this issue: {{task}}

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
        {{system}} {{release}} {{version}} {{machine}}
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

    /// <summary>Port of <c>model.observation_template</c> (the Jinja source; see <see cref="RenderObservation"/>).</summary>
    public const string Observation = """
        {%- if output.output | length < 10000 -%}
        {
          "returncode": {{ output.returncode }},
          "output": {{ output.output | tojson }}
          {%- if output.exception_info %}, "exception_info": {{ output.exception_info | tojson }}{% endif %}
        }
        {%- else -%}
        {
          "returncode": {{ output.returncode }},
          "output_head": {{ output.output[:5000] | tojson }},
          "output_tail": {{ output.output[-5000:] | tojson }},
          "elided_chars": {{ output.output | length - 10000 }},
          "warning": "Output too long."
          {%- if output.exception_info %}, "exception_info": {{ output.exception_info | tojson }}{% endif %}
        }
        {%- endif -%}

        """;

    /// <summary>Port of <c>model.format_error_template</c> (the Jinja source; see <see cref="RenderFormatError"/>).</summary>
    public const string FormatError = """
        {% if finish_reason is defined and (finish_reason == "length" or (finish_reason == "tool_calls" and not has_tool_calls)) -%}
        Your previous response reached the output token limit (finish_reason={{ finish_reason }}) before you produced a tool call, so it was cut off. Respond more concisely and finish with exactly one bash tool call. If you need to think more, do so briefly.
        {%- else -%}
        Tool call error:

        <error>
        {{error}}
        </error>

        Here is general guidance on how to submit correct toolcalls:

        Every response needs to use the 'bash' tool at least once to execute commands.

        Call the bash tool with your command as the argument:
        - Tool: bash
        - Arguments: {"command": "your_command_here"}

        If you want to end the task, please issue the following command: `echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT`
        without any other command.
        {%- endif %}

        """;

    /// <summary>The token-limit branch of <see cref="FormatError"/>.</summary>
    public const string FormatErrorTruncated = """
        Your previous response reached the output token limit (finish_reason={{ finish_reason }}) before you produced a tool call, so it was cut off. Respond more concisely and finish with exactly one bash tool call. If you need to think more, do so briefly.
        """;

    /// <summary>The guidance branch of <see cref="FormatError"/>.</summary>
    public const string FormatErrorGuidance = """
        Tool call error:

        <error>
        {{error}}
        </error>

        Here is general guidance on how to submit correct toolcalls:

        Every response needs to use the 'bash' tool at least once to execute commands.

        Call the bash tool with your command as the argument:
        - Tool: bash
        - Arguments: {"command": "your_command_here"}

        If you want to end the task, please issue the following command: `echo COMPLETE_TASK_AND_SUBMIT_FINAL_OUTPUT`
        without any other command.
        """;

    /// <summary>
    /// Renders <see cref="FormatError"/>: the token-limit text when the response was cut off (<c>length</c>, or
    /// <c>tool_calls</c> without any tool call), otherwise the guidance around <paramref name="error"/>.
    /// </summary>
    public static string RenderFormatError(string error, bool hasToolCalls, string? finishReason)
    {
        ArgumentNullException.ThrowIfNull(error);
        var truncated = finishReason is not null && (finishReason == "length" || (finishReason == "tool_calls" && !hasToolCalls));
        return truncated
            ? TemplateRenderer.Render(FormatErrorTruncated, new Dictionary<string, string>(StringComparer.Ordinal) { ["finish_reason"] = finishReason! })
            : TemplateRenderer.Render(FormatErrorGuidance, new Dictionary<string, string>(StringComparer.Ordinal) { ["error"] = error });
    }

    /// <summary>
    /// Renders <see cref="Observation"/>: the JSON object the model sees, byte-for-byte what Jinja produces
    /// (its <c>tojson</c> filter escapes non-ASCII and HTML-sensitive characters; lengths count code points).
    /// </summary>
    public static string RenderObservation(CommandObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var output = observation.Output;
        var length = output.EnumerateRunes().Count();
        var exception = observation.ExceptionInfo.Length > 0 ? $", \"exception_info\": {ToJson(observation.ExceptionInfo)}" : "";
        var sb = new StringBuilder();
        sb.Append("{\n  \"returncode\": ").Append(observation.ReturnCode.ToString(CultureInfo.InvariantCulture));
        if (length < ObservationMaxChars)
        {
            sb.Append(",\n  \"output\": ").Append(ToJson(output));
        }
        else
        {
            sb.Append(",\n  \"output_head\": ").Append(ToJson(output[..CharIndexOfRune(output, ObservationHeadChars)]));
            sb.Append(",\n  \"output_tail\": ").Append(ToJson(output[CharIndexOfRune(output, length - ObservationTailChars)..]));
            sb.Append(",\n  \"elided_chars\": ").Append((length - ObservationMaxChars).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\n  \"warning\": \"Output too long.\"");
        }

        return sb.Append(exception).Append("\n}").ToString();
    }

    /// <summary>Port of Jinja's <c>tojson</c> filter: <c>json.dumps</c> (ASCII only) plus the HTML-safe escapes.</summary>
    internal static string ToJson(string value) =>
        PythonJson.Dumps(JsonValue.Create(value))
            .Replace("<", "\\u003c", StringComparison.Ordinal)
            .Replace(">", "\\u003e", StringComparison.Ordinal)
            .Replace("&", "\\u0026", StringComparison.Ordinal)
            .Replace("'", "\\u0027", StringComparison.Ordinal);

    private static int CharIndexOfRune(string text, int runeIndex)
    {
        var chars = 0;
        var runes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (runes == runeIndex)
            {
                break;
            }

            chars += rune.Utf16SequenceLength;
            runes++;
        }

        return chars;
    }
}
